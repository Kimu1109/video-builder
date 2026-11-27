using System.Runtime.InteropServices;
using System.Text;

namespace VideoBuilder.App.Platform;

/// <summary>
/// X11 で、ほかのアプリ (ファイルマネージャなど) からのファイルのドロップを受け取る (XDND プロトコル)。
///
/// 使っている Avalonia 11 の X11 版はドロップの受け取りに対応していないので、ここで自前で行う。
/// Avalonia とは別に X サーバーへつなぎ、見えない「代理ウィンドウ」を作る。アプリのウィンドウに
/// XdndProxy (ドロップのやり取りはこの代理と行ってほしい、という目印) を付けておくと、
/// ドラッグ元のアプリは代理ウィンドウとやり取りするので、Avalonia を通さずにメッセージを受け取れる。
/// Wayland でも、アプリが XWayland 上で動いていれば同じように動く。
/// </summary>
public sealed unsafe class XdndDropTarget : IDisposable
{
    /// <summary>ドロップ位置が動いた (window: アプリのウィンドウ, x/y: そのウィンドウ内の位置 (物理ピクセル))。</summary>
    public event Action<IntPtr, int, int>? Moved;
    /// <summary>ドラッグがウィンドウの外へ出た、または中止された。</summary>
    public event Action<IntPtr>? Left;
    /// <summary>ファイルが落とされた。</summary>
    public event Action<IntPtr, int, int, IReadOnlyList<string>>? Dropped;

    private const int Version = 5;
    private readonly IntPtr _display;
    private readonly IntPtr _proxy;
    private readonly IntPtr _root;
    private readonly Atoms _a;
    private readonly Thread _thread;
    private volatile bool _quit;
    /// <summary>
    /// 終了の合図を送る eventfd。イベントを待つときに X の接続と一緒に待つので、何もないときは眠ったまま (起きない)。
    /// 作れなければ -1 (そのときは、終了を確かめるために 100ms ごとに起きる)。
    /// </summary>
    private readonly int _wake = -1;
    private readonly HashSet<IntPtr> _windows = new();

    // ドラッグ中の状態 (イベントのスレッドだけが触る)
    private IntPtr _source, _target;
    private bool _acceptable;
    private int _rootX, _rootY;
    private IntPtr _dropTime;

    // ---- X のエラーの扱い ----
    // Xlib は、エラーが起きると既定ではプロセスごと終了してしまう。ドラッグ元のウィンドウが途中で閉じられた場合など、
    // こちらの接続でのエラーは避けられないので、自前の処理に差し替える。ほかの接続 (Avalonia) のエラーは、元の処理に回す。
    private static readonly object ErrorGate = new();
    private static readonly HashSet<IntPtr> OurDisplays = new();
    private static IntPtr _previousHandler;
    private static X.ErrorHandler? _handler;

    private static void InstallErrorHandler(IntPtr display)
    {
        lock (ErrorGate)
        {
            OurDisplays.Add(display);
            if (_handler != null) return;
            _handler = OnXError;
            _previousHandler = X.XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(_handler));
        }
    }

    private static int OnXError(IntPtr display, IntPtr error)
    {
        lock (ErrorGate)
        {
            if (OurDisplays.Contains(display)) return 0;     // こちらの接続のエラーは無視する
        }
        if (_previousHandler != IntPtr.Zero)
            return ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)_previousHandler)(display, error);
        return 0;
    }

    private XdndDropTarget(IntPtr display)
    {
        _display = display;
        InstallErrorHandler(display);
        _root = X.XDefaultRootWindow(display);
        _a = new Atoms(display);
        // 代理ウィンドウ: 画面には出さない
        _proxy = X.XCreateSimpleWindow(display, _root, -10, -10, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
        SetAtomProperty(_proxy, _a.XdndAware, _a.ATOM, (IntPtr)Version);
        SetAtomProperty(_proxy, _a.XdndProxy, _a.WINDOW, _proxy);   // 代理ウィンドウ自身にも付ける (規約)
        X.XFlush(display);
        try { _wake = Libc.eventfd(0, Libc.EFD_CLOEXEC | Libc.EFD_NONBLOCK); }
        catch (EntryPointNotFoundException) { _wake = -1; }
        _thread = new Thread(Loop) { IsBackground = true, Name = "XDND" };
        _thread.Start();
    }

    /// <summary>X11 につなげて作る。X11 でない (Wayland だけ・Windows・macOS) などで使えなければ null。</summary>
    public static XdndDropTarget? TryCreate()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        try
        {
            var display = X.XOpenDisplay(IntPtr.Zero);
            return display == IntPtr.Zero ? null : new XdndDropTarget(display);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>アプリのウィンドウ (X11 のウィンドウ ID) で、ドロップを受け付けるようにする。</summary>
    public void Attach(IntPtr window)
    {
        lock (_windows)
        {
            if (!_windows.Add(window)) return;
        }
        SetAtomProperty(window, _a.XdndAware, _a.ATOM, (IntPtr)Version);
        SetAtomProperty(window, _a.XdndProxy, _a.WINDOW, _proxy);
        X.XFlush(_display);
    }

    private void SetAtomProperty(IntPtr window, IntPtr property, IntPtr type, IntPtr value)
    {
        // Xlib は 32bit の値を C の long (64bit 環境では 8 バイト) の並びとして受け取る
        long v = value.ToInt64();
        X.XChangeProperty(_display, window, property, type, 32, 0 /* PropModeReplace */, (IntPtr)(&v), 1);
    }

    // ---------------- イベントの処理 ----------------

    private void Loop()
    {
        int fd = X.XConnectionNumber(_display);
        var ev = new XEvent();
        while (!_quit)
        {
            if (X.XPending(_display) == 0)
            {
                // X のイベントか終了の合図 (eventfd) が来るまで眠る。eventfd がなければ、終了を確かめられるよう最大 100ms ずつ
                var pfd = stackalloc PollFd[2];
                pfd[0] = new PollFd { fd = fd, events = Libc.POLLIN };
                pfd[1] = new PollFd { fd = _wake, events = Libc.POLLIN };
                Libc.poll(pfd, _wake >= 0 ? 2UL : 1UL, _wake >= 0 ? -1 : 100);
                continue;
            }
            X.XNextEvent(_display, ref ev);
            try { Handle(ref ev); }
            catch { /* 1 回のドロップの失敗で、受け取りを止めない */ }
        }
    }

    private void Handle(ref XEvent ev)
    {
        if (ev.type == 33 /* ClientMessage */)
        {
            var type = ev.ClientMessage.message_type;
            if (type == _a.XdndEnter) OnEnter(ref ev.ClientMessage);
            else if (type == _a.XdndPosition) OnPosition(ref ev.ClientMessage);
            else if (type == _a.XdndLeave) OnLeave();
            else if (type == _a.XdndDrop) OnDrop(ref ev.ClientMessage);
        }
        else if (ev.type == 31 /* SelectionNotify */ && ev.Selection.requestor == _proxy && ev.Selection.selection == _a.XdndSelection)
            OnSelection(ref ev.Selection);
    }

    private void OnEnter(ref XClientMessageEvent m)
    {
        _source = m.l0;
        _target = m.window;     // 代理を通すときも、window にはアプリのウィンドウが入っている
        long flags = m.l1.ToInt64();
        var types = new List<IntPtr>();
        if ((flags & 1) != 0) types.AddRange(ReadAtomList(_source, _a.XdndTypeList));
        else foreach (var t in new[] { m.l2, m.l3, m.l4 }) if (t != IntPtr.Zero) types.Add(t);
        _acceptable = types.Contains(_a.UriList);
    }

    private void OnPosition(ref XClientMessageEvent m)
    {
        long xy = m.l2.ToInt64();
        _rootX = (int)((xy >> 16) & 0xFFFF); _rootY = (int)(xy & 0xFFFF);
        if (m.window != IntPtr.Zero) _target = m.window;
        SendStatus(_acceptable);
        if (_acceptable && ToWindow(out int x, out int y)) Moved?.Invoke(_target, x, y);
    }

    private void OnLeave()
    {
        var t = _target;
        _source = _target = IntPtr.Zero;
        _acceptable = false;
        Left?.Invoke(t);
    }

    private void OnDrop(ref XClientMessageEvent m)
    {
        if (!_acceptable) { SendFinished(false); OnLeave(); return; }
        _dropTime = m.l2;
        // 落とされたファイルの一覧を、ドラッグ元に text/uri-list で書いてもらう
        X.XConvertSelection(_display, _a.XdndSelection, _a.UriList, _a.DropData, _proxy, _dropTime);
        X.XFlush(_display);
    }

    private void OnSelection(ref XSelectionEvent s)
    {
        var files = new List<string>();
        if (s.property != IntPtr.Zero)
        {
            var text = ReadStringProperty(_proxy, _a.DropData);
            X.XDeleteProperty(_display, _proxy, _a.DropData);
            files.AddRange(ParseUriList(text));
        }
        bool ok = files.Count > 0;
        SendFinished(ok);
        var target = _target;
        bool located = ToWindow(out int x, out int y);
        _source = _target = IntPtr.Zero;
        _acceptable = false;
        if (ok && located) Dropped?.Invoke(target, x, y, files);
        else Left?.Invoke(target);
    }

    /// <summary>画面全体での位置を、アプリのウィンドウ内の位置に直す。</summary>
    private bool ToWindow(out int x, out int y)
    {
        x = y = 0;
        if (_target == IntPtr.Zero) return false;
        return X.XTranslateCoordinates(_display, _root, _target, _rootX, _rootY, out x, out y, out _) != 0;
    }

    private void SendStatus(bool accept)
    {
        var e = new XEvent();
        e.ClientMessage.type = 33;
        e.ClientMessage.window = _source;
        e.ClientMessage.message_type = _a.XdndStatus;
        e.ClientMessage.format = 32;
        e.ClientMessage.l0 = _target;
        // bit0: 受け付ける, bit1: 位置が変わるたびに知らせてほしい (ドロップ先の表示を動かすため)
        e.ClientMessage.l1 = (IntPtr)(accept ? 3 : 2);
        e.ClientMessage.l4 = accept ? _a.XdndActionCopy : IntPtr.Zero;
        X.XSendEvent(_display, _source, 0, IntPtr.Zero, ref e);
        X.XFlush(_display);
    }

    private void SendFinished(bool success)
    {
        if (_source == IntPtr.Zero) return;
        var e = new XEvent();
        e.ClientMessage.type = 33;
        e.ClientMessage.window = _source;
        e.ClientMessage.message_type = _a.XdndFinished;
        e.ClientMessage.format = 32;
        e.ClientMessage.l0 = _target;
        e.ClientMessage.l1 = (IntPtr)(success ? 1 : 0);
        e.ClientMessage.l2 = success ? _a.XdndActionCopy : IntPtr.Zero;
        X.XSendEvent(_display, _source, 0, IntPtr.Zero, ref e);
        X.XFlush(_display);
    }

    private List<IntPtr> ReadAtomList(IntPtr window, IntPtr property)
    {
        var list = new List<IntPtr>();
        if (X.XGetWindowProperty(_display, window, property, IntPtr.Zero, (IntPtr)1024, 0, (IntPtr)0 /* AnyPropertyType */,
                out _, out int format, out var count, out _, out var data) != 0 || data == IntPtr.Zero) return list;
        try
        {
            if (format == 32)
                for (long i = 0; i < count.ToInt64(); i++) list.Add(((IntPtr*)data)[i]);
        }
        finally { X.XFree(data); }
        return list;
    }

    private string ReadStringProperty(IntPtr window, IntPtr property)
    {
        if (X.XGetWindowProperty(_display, window, property, IntPtr.Zero, (IntPtr)(1 << 20), 0, (IntPtr)0,
                out _, out int format, out var count, out _, out var data) != 0 || data == IntPtr.Zero) return "";
        try { return format == 8 ? Encoding.UTF8.GetString((byte*)data, (int)count.ToInt64()) : ""; }
        finally { X.XFree(data); }
    }

    /// <summary>text/uri-list (1 行に 1 つの URI、# で始まる行は注釈) から、ローカルのファイルのパスを取り出す。</summary>
    public static List<string> ParseUriList(string text)
    {
        var files = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\0');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile) files.Add(uri.LocalPath);
            else if (line.StartsWith('/')) files.Add(line);   // URI ではなくパスをそのまま入れてくるアプリ向け
        }
        return files;
    }

    public void Dispose()
    {
        if (_quit) return;
        _quit = true;
        if (_wake >= 0)
        {
            ulong one = 1;
            Libc.write(_wake, &one, sizeof(ulong));   // 眠っているイベントのスレッドを起こす
        }
        _thread.Join(1000);
        if (_wake >= 0) Libc.close(_wake);
        // アプリのウィンドウに付けた目印 (XdndProxy) は消さない。ウィンドウがもう閉じていると X のエラーになるため。
        // 代理ウィンドウがなくなれば、ドラッグ元は目印を無視する (規約で、代理側の目印を確かめることになっている)
        X.XDestroyWindow(_display, _proxy);
        X.XSync(_display, 0);
        lock (ErrorGate) OurDisplays.Remove(_display);
        X.XCloseDisplay(_display);
    }

    // ---------------- Xlib ----------------

    internal sealed class Atoms
    {
        public readonly IntPtr XdndAware, XdndProxy, XdndEnter, XdndPosition, XdndStatus, XdndLeave, XdndDrop, XdndFinished,
            XdndSelection, XdndTypeList, XdndActionCopy, UriList, DropData, ATOM, WINDOW;

        public Atoms(IntPtr d)
        {
            IntPtr A(string n) => X.XInternAtom(d, n, 0);
            XdndAware = A("XdndAware"); XdndProxy = A("XdndProxy"); XdndEnter = A("XdndEnter"); XdndPosition = A("XdndPosition");
            XdndStatus = A("XdndStatus"); XdndLeave = A("XdndLeave"); XdndDrop = A("XdndDrop"); XdndFinished = A("XdndFinished");
            XdndSelection = A("XdndSelection"); XdndTypeList = A("XdndTypeList"); XdndActionCopy = A("XdndActionCopy");
            UriList = A("text/uri-list"); DropData = A("VIDEO_BUILDER_DROP"); ATOM = A("ATOM"); WINDOW = A("WINDOW");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XClientMessageEvent
    {
        public int type;
        public IntPtr serial;
        public int send_event;
        public IntPtr display;
        public IntPtr window;
        public IntPtr message_type;
        public int format;
        public IntPtr l0, l1, l2, l3, l4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XSelectionEvent
    {
        public int type;
        public IntPtr serial;
        public int send_event;
        public IntPtr display;
        public IntPtr requestor, selection, target, property, time;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XSelectionRequestEvent
    {
        public int type;
        public IntPtr serial;
        public int send_event;
        public IntPtr display;
        public IntPtr owner, requestor, selection, target, property, time;
    }

    /// <summary>XEvent (中身の種類ごとに読み方が変わる共用体。大きさは long 24 個ぶん)。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    internal struct XEvent
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(0)] public XClientMessageEvent ClientMessage;
        [FieldOffset(0)] public XSelectionEvent Selection;
        [FieldOffset(0)] public XSelectionRequestEvent SelectionRequest;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd { public int fd; public short events; public short revents; }

    internal static class Libc
    {
        public const short POLLIN = 1;
        public const int EFD_NONBLOCK = 0x800, EFD_CLOEXEC = 0x80000;
        [DllImport("libc", SetLastError = true)] public static extern int poll(PollFd* fds, ulong nfds, int timeout);
        [DllImport("libc", SetLastError = true)] public static extern int eventfd(uint initval, int flags);
        [DllImport("libc", SetLastError = true)] public static extern nint write(int fd, void* buf, nuint count);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
    }

    internal static class X
    {
        private const string Lib = "libX11.so.6";
        [DllImport(Lib)] public static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(Lib)] public static extern int XCloseDisplay(IntPtr display);
        [DllImport(Lib)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(Lib)] public static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint w, uint h, uint border, IntPtr borderColor, IntPtr background);
        [DllImport(Lib)] public static extern int XDestroyWindow(IntPtr display, IntPtr window);
        [DllImport(Lib)] public static extern int XMapWindow(IntPtr display, IntPtr window);
        [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern IntPtr XInternAtom(IntPtr display, string name, int onlyIfExists);
        [DllImport(Lib)] public static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, IntPtr data, int count);
        [DllImport(Lib)] public static extern int XDeleteProperty(IntPtr display, IntPtr window, IntPtr property);
        [DllImport(Lib)] public static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length, int delete, IntPtr reqType,
            out IntPtr actualType, out int actualFormat, out IntPtr count, out IntPtr bytesAfter, out IntPtr data);
        [DllImport(Lib)] public static extern int XFree(IntPtr data);
        [DllImport(Lib)] public static extern int XFlush(IntPtr display);
        [DllImport(Lib)] public static extern int XSync(IntPtr display, int discard);
        [DllImport(Lib)] public static extern int XPending(IntPtr display);
        [DllImport(Lib)] public static extern int XNextEvent(IntPtr display, ref XEvent ev);
        [DllImport(Lib)] public static extern int XSendEvent(IntPtr display, IntPtr window, int propagate, IntPtr mask, ref XEvent ev);
        [DllImport(Lib)] public static extern int XConnectionNumber(IntPtr display);
        [DllImport(Lib)] public static extern int XConvertSelection(IntPtr display, IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, IntPtr time);
        [DllImport(Lib)] public static extern int XSetSelectionOwner(IntPtr display, IntPtr selection, IntPtr owner, IntPtr time);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ErrorHandler(IntPtr display, IntPtr errorEvent);
        [DllImport(Lib)] public static extern IntPtr XSetErrorHandler(IntPtr handler);
        [DllImport(Lib)] public static extern int XTranslateCoordinates(IntPtr display, IntPtr src, IntPtr dest, int x, int y, out int destX, out int destY, out IntPtr child);
    }
}
