using System.Runtime.InteropServices;

namespace VideoBuilder.Media;

/// <summary>
/// Windows のタイマーの精度と、電力の制限 (Power Throttling・EcoQoS) の切り替え。ほかの OS では何もしない。設計書 22.65。
/// <para>
/// タイマー: Windows の標準のタイマーの精度は約 15.6ms で、Thread.Sleep(10) や DispatcherTimer (8ms) も 15.6ms ごとになる。
/// 再生中だけ 1ms にする (timeBeginPeriod)。音声のバッファを積む間隔と、再生の時計 (画面の更新がないとき) が細かくなる。
/// </para>
/// <para>
/// 電力の制限: Windows は、前面にないアプリのスレッドを省電力 (EcoQoS。低いクロック・Intel の E コア) で動かすことがある。
/// 書き出しの間に別のウィンドウを使うと、書き出しが遅くなる。書き出しの間だけ、このプロセスの省電力を切る (SetProcessInformation)。
/// </para>
/// どちらも、使い始めた数を数えて、最初に入れ、最後に戻す (どのスレッドから使ってもよい)。
/// </summary>
public static class WindowsScheduling
{
    /// <summary>使っている数を数え、最初の 1 つで enter、最後の 1 つが終わったら leave を呼ぶ。</summary>
    internal sealed class Counter(Action enter, Action leave)
    {
        private readonly object _gate = new();
        private int _users;
        public bool Active { get { lock (_gate) return _users > 0; } }

        public IDisposable Use()
        {
            lock (_gate) { if (_users++ == 0) Run(enter); }
            return new Lease(this);
        }

        private void Return()
        {
            lock (_gate) { if (--_users == 0) Run(leave); }
        }

        private sealed class Lease(Counter owner) : IDisposable
        {
            private int _disposed;
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Return(); }
        }
    }

    private static readonly Counter Timers = new(() => Timer(true), () => Timer(false));
    private static readonly Counter Throttles = new(() => Throttling(false), () => Throttling(true));

    /// <summary>Dispose するまで、タイマーの精度を 1ms にする (再生中に使う)。</summary>
    public static IDisposable FineTimer() => Timers.Use();

    /// <summary>Dispose するまで、このプロセスの電力の制限 (省電力での実行) を切る (書き出しに使う)。</summary>
    public static IDisposable NoPowerThrottling() => Throttles.Use();

    private static void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* 古い Windows: 何もしない */ }
    }

    private static void Timer(bool fine)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (fine) timeBeginPeriod(1); else timeEndPeriod(1);
    }

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1, PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    private static unsafe void Throttling(bool allow)
    {
        if (!OperatingSystem.IsWindows()) return;
        // ControlMask に EXECUTION_SPEED を入れて StateMask を 0 にすると「省電力にしない」、両方 0 で Windows に任せる (元どおり)
        var state = new PowerThrottlingState
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = allow ? 0 : PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = 0,
        };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state, (uint)sizeof(PowerThrottlingState));
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern unsafe int SetProcessInformation(IntPtr process, int infoClass, void* info, uint size);
}
