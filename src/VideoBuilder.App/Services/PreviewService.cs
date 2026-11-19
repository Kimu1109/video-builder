using System.Diagnostics;
using Avalonia.Threading;
using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Services;

/// <summary>
/// プレビューの描画 (別スレッド) と再生を受け持つ。
/// UI スレッドのモデルは直接触らず、変更のたびに複製 (スナップショット) を渡す。
/// </summary>
public sealed class PreviewService : IDisposable
{
    private readonly EditorSession _session;
    private readonly AppSettings _settings;
    private RenderEngine? _engine;      // 描画スレッドの中で作って捨てる (GPU コンテキストがスレッドに結び付くため)
    private volatile bool _backendDirty = true;
    private readonly AudioMixer _mixer = new();
    private AudioOutput? _audio;

    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _quit;

    private sealed record Snapshot(Project Project, string SceneId);
    private volatile Snapshot? _snapshot;

    private readonly object _requestLock = new();
    private Snapshot? _requested;
    private int _requestedFrame;

    private readonly object _frameLock = new();
    private SKBitmap? _front, _back;
    private List<(string Id, SKPoint[] Quad)> _quads = new();
    private bool _postPending;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private long _playStartSample;
    private int _playEndFrame;

    public bool IsPlaying { get; private set; }
    public event Action? FrameReady;
    public event Action? PlayStateChanged;
    /// <summary>直近 1 フレームの描画にかかった時間 (ミリ秒)。</summary>
    public double LastRenderMs { get; private set; }
    /// <summary>1 フレーム描いた (描画のスレッドから呼ばれる)。(かかった時間 ms, 幅, 高さ)</summary>
    public event Action<double, int, int>? Rendered;
    /// <summary>再生中に音声を 1 回ぶんミックスした (音声のスレッドから呼ばれる)。(かかった時間 ms, 音声の長さ ms)</summary>
    public event Action<double, double>? AudioMixed;
    /// <summary>直近の描画の大きさ。</summary>
    public (int Width, int Height) LastRenderSize { get; private set; }
    /// <summary>
    /// プレビューが画面に表示される大きさ (物理ピクセル)。PreviewView が知らせる。
    /// 自動のときは、これより大きく描いても見えないので、この大きさで描く。
    /// </summary>
    public (int Width, int Height) DisplaySize
    {
        get { lock (_frameLock) return _displaySize; }
        set
        {
            lock (_frameLock)
            {
                if (_displaySize == value) return;
                _displaySize = value;
            }
            Request();
        }
    }
    private (int Width, int Height) _displaySize;

    /// <summary>描く大きさ。設定が自動なら、表示される大きさ (プロジェクトより大きくはしない)。</summary>
    internal (int Width, int Height) TargetSize(int projectWidth, int projectHeight)
    {
        int div = _settings.PreviewDivisor;
        if (div >= 1)
        {
            div = Math.Clamp(div, 1, 8);
            return (Math.Max(2, projectWidth / div), Math.Max(2, projectHeight / div));
        }
        var (dw, dh) = DisplaySize;
        if (dw <= 0 || dh <= 0) return (projectWidth, projectHeight);
        double k = Math.Min(1.0, Math.Min((double)dw / projectWidth, (double)dh / projectHeight));
        // 少しの大きさの違いで作り直さないよう、8 ピクセル単位に切り上げる
        int w = Math.Min(projectWidth, (int)Math.Ceiling(projectWidth * k / 8) * 8);
        int h = Math.Min(projectHeight, (int)Math.Round((double)w * projectHeight / projectWidth));
        return (Math.Max(2, w), Math.Max(2, h));
    }

    /// <summary>今使っている描画方式の説明 (環境設定に表示する)。</summary>
    public string BackendDescription { get; private set; } = "";
    public bool IsGpuActive { get; private set; }
    public event Action? BackendChanged;

    /// <summary>描画方式の設定が変わったので、エンジンを作り直す。</summary>
    public void ReloadBackend()
    {
        _backendDirty = true;
        Request();
    }

    /// <summary>設定と環境から、GPU を使うかどうかを決めてエンジンを作る。描画スレッドから呼ぶ。</summary>
    private void CreateEngine()
    {
        _engine?.Gpu?.MakeCurrent();
        var oldGpu = _engine?.Gpu;
        _engine?.Dispose();
        oldGpu?.Dispose();

        GpuContext? gpu = null;
        string description;
        var mode = _settings.RenderBackend;
        if (mode == RenderBackend.Cpu) description = "CPU";
        else
        {
            gpu = GpuContext.TryCreate(out var error);
            if (gpu == null) description = $"CPU (GPU を使えません: {error})";
            else if (gpu.IsSoftware && mode == RenderBackend.Auto)
            {
                // ソフトウェア実装の OpenGL は CPU 描画より遅いので使わない
                description = $"CPU (GPU が見つかりません: {gpu.Renderer})";
                gpu.Dispose(); gpu = null;
            }
            else description = $"GPU ({gpu.Renderer})";
        }
        _engine = new RenderEngine(gpu) { Draft = true };
        IsGpuActive = gpu != null;
        BackendDescription = description;
        Dispatcher.UIThread.Post(() => BackendChanged?.Invoke());
    }

    public PreviewService(EditorSession session, AppSettings settings)
    {
        _session = session; _settings = settings;
        session.ProjectChanged += () => { _snapshot = null; RequestSoon(); };
        session.FrameChanged += Request;
        session.SceneChanged += () => { _snapshot = null; Request(); };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) => Tick());
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Render" };
        _thread.Start();
        Request();
    }

    private Snapshot Current()
    {
        var s = _snapshot;
        if (s == null) _snapshot = s = new Snapshot(_session.SnapshotForRead(), _session.Scene.Id);
        return s;
    }

    private bool _requestPosted;

    /// <summary>
    /// プロジェクトが変わったときの描き直し。ドラッグ中などに続けて何度も変わっても、
    /// 画面の更新 1 回につき 1 度だけ複製して描く。
    /// </summary>
    private void RequestSoon()
    {
        if (_requestPosted) return;
        _requestPosted = true;
        Dispatcher.UIThread.Post(() => { _requestPosted = false; Request(); }, DispatcherPriority.Render);
    }

    /// <summary>今の状態でプレビューを描き直す。</summary>
    public void Request()
    {
        var s = Current();
        lock (_requestLock) { _requested = s; _requestedFrame = _session.CurrentFrame; }
        _signal.Set();
    }

    private void RenderLoop()
    {
        while (!_quit)
        {
            _signal.WaitOne();
            while (!_quit)
            {
                Snapshot? req; int frame;
                lock (_requestLock) { req = _requested; frame = _requestedFrame; _requested = null; }
                if (req == null) break;
                try
                {
                    if (_backendDirty) { _backendDirty = false; CreateEngine(); }
                    RenderOne(req, frame);
                }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
        }
        // GPU のリソースは、作ったこのスレッドで片付ける
        var gpu = _engine?.Gpu;
        _engine?.Dispose();
        gpu?.Dispose();
        _engine = null;
    }

    private void RenderOne(Snapshot req, int frame)
    {
        var p = req.Project;
        var scene = p.FindScene(req.SceneId) ?? p.Root;
        var (w, h) = TargetSize(p.Settings.Width, p.Settings.Height);
        if (_back == null || _back.Width != w || _back.Height != h)
        {
            _back?.Dispose();
            _back = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        }
        var sw = Stopwatch.StartNew();
        var engine = _engine!;
        engine.RenderToBitmap(p, scene, frame, _back);
        LastRenderMs = sw.Elapsed.TotalMilliseconds;
        LastRenderSize = (w, h);
        Rendered?.Invoke(LastRenderMs, w, h);
        var quads = engine.LastQuads.ToList();
        bool post;
        lock (_frameLock)
        {
            (_front, _back) = (_back, _front);
            _quads = quads;
            post = !_postPending;
            _postPending = true;
        }
        if (post) Dispatcher.UIThread.Post(() =>
        {
            lock (_frameLock) _postPending = false;
            FrameReady?.Invoke();
        }, DispatcherPriority.Render);
    }

    /// <summary>最新のフレームを受け取る。action の中でだけピクセルに触れる。</summary>
    public void WithFrame(Action<SKBitmap> action)
    {
        lock (_frameLock)
        {
            if (_front != null) action(_front);
        }
    }

    public List<(string Id, SKPoint[] Quad)> Quads
    {
        get { lock (_frameLock) return _quads; }
    }

    // ---------------- 再生 ----------------

    public void TogglePlay()
    {
        if (IsPlaying) Stop(); else Play();
    }

    public void Play()
    {
        if (IsPlaying) return;
        var project = _session.Project;
        _playEndFrame = _session.SceneLength;
        if (_session.CurrentFrame >= _playEndFrame - 1) _session.CurrentFrame = 0;
        _playStartSample = project.FrameToSample(_session.CurrentFrame);

        int rate = project.Settings.SampleRate;
        if (_audio == null || _audioRate != rate)
        {
            _audio?.Dispose();
            _audio = new AudioOutput(rate);
            _audioRate = rate;
        }
        if (_audio.IsAvailable)
        {
            _audio.Start(_playStartSample, (pos, buffer) =>
            {
                var s = _snapshot;
                if (s == null) { Array.Clear(buffer); return; }
                long t0 = Stopwatch.GetTimestamp();
                _mixer.Mix(s.Project, s.Project.FindScene(s.SceneId) ?? s.Project.Root, pos, buffer);
                AudioMixed?.Invoke(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, buffer.Length / 2 * 1000.0 / rate);
            });
        }
        Current();
        _clock.Restart();
        IsPlaying = true;
        _timer.Start();
        PlayStateChanged?.Invoke();
    }

    private int _audioRate;

    public void Stop()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        _timer.Stop();
        _audio?.Stop();
        PlayStateChanged?.Invoke();
    }

    private void Tick()
    {
        if (!IsPlaying) return;
        var project = _session.Project;
        int rate = project.Settings.SampleRate;
        // 音声が出せるときは音声の再生位置を時計にする (映像を音に合わせる)
        long sample = _audio is { IsAvailable: true }
            ? _audio.Position
            : _playStartSample + (long)(_clock.Elapsed.TotalSeconds * rate);
        var fps = project.Settings.Fps;
        int frame = (int)(sample * fps.Num / ((long)rate * fps.Den));
        if (frame >= _playEndFrame)
        {
            _session.CurrentFrame = Math.Max(0, _playEndFrame - 1);
            Stop();
            return;
        }
        _session.CurrentFrame = frame;
    }

    public void Dispose()
    {
        Stop();
        _quit = true;
        _signal.Set();
        _thread.Join(2000);
        _audio?.Dispose();
        _mixer.Dispose();
        _front?.Dispose(); _back?.Dispose();
    }
}
