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
    // ---------------- 画面と共有するテクスチャ ----------------

    private volatile SharedPreviewTarget? _sharedRequested;   // UI スレッドが渡したもの
    private SharedPreviewTarget? _shared;                      // 描画のスレッドが使っているもの
    private IDisposable? _sharedCurrent;                        // 共有の GL コンテキストを、描画のスレッドで有効にしている間

    /// <summary>
    /// 画面と共有する GL のテクスチャ (PreviewView が作る)。渡すと、使えるときは GPU で描いた絵を読み戻さずに画面に出す。
    /// null にすると、これまでどおり読み戻して PreviewView が描く。
    /// </summary>
    public SharedPreviewTarget? SharedTarget
    {
        get => _sharedRequested;
        set
        {
            if (ReferenceEquals(_sharedRequested, value)) return;
            _sharedRequested = value;
            ReloadBackend();
        }
    }

    /// <summary>
    /// 画面と共有するテクスチャを使ってよいか。画面を画像として保存するとき (--screenshot) は false にする
    /// (共有のテクスチャの絵は、画面の描画が直接合成するので、コントロールを画像に描く方法では写らない)。
    /// </summary>
    public bool AllowSharedTarget
    {
        get => _allowShared;
        set { if (_allowShared == value) return; _allowShared = value; ReloadBackend(); }
    }
    private volatile bool _allowShared = true;

    /// <summary>今、描いた絵を共有のテクスチャで画面に出しているか (PreviewView は自分で描かない)。</summary>
    public bool PresentsToShared => _presentsToShared;
    private volatile bool _presentsToShared;

    /// <summary>共有のテクスチャを使ってよいか。画面の GPU が、描画に選んだ GPU と同じときだけ (違う GPU の間では共有できない)。</summary>
    private static bool SharedAllowed(SharedPreviewTarget shared, RenderBackend mode, out string reason)
    {
        reason = "";
        var env = Environment.GetEnvironmentVariable("VIDEO_BUILDER_SHARED_PREVIEW");
        if (env == "0") { reason = "使わない設定"; return false; }
        // GPU 描画は Linux でだけ確かめている (Windows の ANGLE などでは、まだ使わない)
        if (!OperatingSystem.IsLinux()) { reason = "この OS ではまだ使わない"; return false; }
        bool force = env == "1";
        bool software = new[] { "llvmpipe", "softpipe", "swiftshader", "software" }.Any(k => shared.Renderer.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (software && mode == RenderBackend.Auto && !force) { reason = "画面の描画がソフトウェアの GPU"; return false; }
        if (!force && SafeSelectedAdapter() is { IsSoftware: false } adapter && !SameGpu(adapter.Name, shared.Renderer))
        {
            reason = $"画面の GPU ({shared.Renderer}) と描画の GPU ({adapter.Name}) が違う";
            return false;
        }
        return true;
    }

    private static GpuAdapter? SafeSelectedAdapter()
    {
        try { return GpuContext.SelectedAdapter(); } catch { return null; }
    }

    /// <summary>GL の GPU の名前 (GL_RENDERER) が同じ GPU を指しているか。後ろの "/PCIe/SSE2" などは比べない。</summary>
    internal static bool SameGpu(string a, string b)
    {
        static string Core(string s) => (s.Split('/')[0]).Trim();
        string x = Core(a), y = Core(b);
        return x.Length > 0 && y.Length > 0 && (x.Contains(y, StringComparison.OrdinalIgnoreCase) || y.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>共有のテクスチャに描く SKSurface を作る (描画のスレッドで)。</summary>
    private static SKSurface? CreateTextureSurface(GRContext gr, SharedPreviewTarget.Texture texture)
    {
        int format = texture.Shared.InternalFormat;
        const int GL_RGBA = 0x1908, GL_RGBA8 = 0x8058, GL_TEXTURE_2D = 0x0DE1;
        if (format == GL_RGBA || format == 0) format = GL_RGBA8;   // Skia には大きさ付きの形式で渡す
        var info = new GRGlTextureInfo(GL_TEXTURE_2D, (uint)texture.Shared.TextureId, (uint)format);
        using var backend = new GRBackendTexture(texture.Size.Width, texture.Size.Height, false, info);
        return SKSurface.Create(gr, backend, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
    }

    /// <summary>今使っている共有のテクスチャと GL コンテキストを片付ける (描画のスレッドで、エンジンを捨てたあと)。</summary>
    private void ReleaseShared(GpuContext? oldGpu, bool disposeContext)
    {
        if (_shared != null && oldGpu is { IsExternal: true })
        {
            oldGpu.MakeCurrent();
            _shared.DisposeResources(t => (t.Tag as SKSurface)?.Dispose());
        }
        oldGpu?.Dispose();   // 共有のものなら、ここで描画のスレッドでの有効化も解く
        if (disposeContext && _shared != null) _shared.DisposeContext();
    }

    private void CreateEngine()
    {
        _engine?.Gpu?.MakeCurrent();
        var oldGpu = _engine?.Gpu;
        _engine?.Dispose();
        var requested = _sharedRequested;
        ReleaseShared(oldGpu, disposeContext: !ReferenceEquals(_shared, requested));
        _shared = requested;
        _presentsToShared = false;

        GpuContext? gpu = null;
        string description = "";
        string notShared = "";
        var mode = _settings.RenderBackend;
        if (mode == RenderBackend.Cpu) description = "CPU";
        else if (!_allowShared) notShared = "画面の保存中";
        else if (_shared is { } shared && SharedAllowed(shared, mode, out notShared))
        {
            // 画面と共有する GL コンテキストで描く (描いた絵を読み戻さずに、そのまま画面に出す)
            gpu = GpuContext.FromExternal(name => shared.Gl.GlInterface.GetProcAddress(name),
                shared.Gl.Version.Type == Avalonia.OpenGL.GlProfileType.OpenGLES, shared.Renderer, shared.Vendor,
                () => _sharedCurrent ??= shared.Gl.MakeCurrent(), () => { _sharedCurrent?.Dispose(); _sharedCurrent = null; }, out _);
            if (gpu != null)
            {
                _presentsToShared = true;
                description = $"GPU ({gpu.Renderer}、画面と共有)";
            }
        }
        if (mode != RenderBackend.Cpu && gpu == null)
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
        // 画面との共有ができる環境なのに使わないときは、理由を添える (環境設定・パフォーマンスチェックに出る)
        if (gpu != null && !_presentsToShared && _shared != null && notShared.Length > 0) description += $" ※画面との共有なし: {notShared}";
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
        gpu?.MakeCurrent();
        _engine?.Dispose();
        ReleaseShared(gpu, disposeContext: true);
        _shared = null;
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
        // 再生中は次のフレームを裏でデコードしておく (止まっているときは、次にどこを描くか分からないので読まない)
        engine.Lookahead = IsPlaying;
        if (_presentsToShared && _shared != null && engine.Gpu is { IsExternal: true } g)
        {
            RenderShared(engine, g, _shared, p, scene, frame, w, h, sw);
            return;
        }
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

    /// <summary>共有のテクスチャに描いて、画面に出す。テクスチャが空かなければ、このフレームは飛ばす。</summary>
    private void RenderShared(RenderEngine engine, GpuContext g, SharedPreviewTarget shared, Project p, Scene scene, int frame, int w, int h, Stopwatch sw)
    {
        g.MakeCurrent();
        var texture = shared.Acquire(new Avalonia.PixelSize(w, h), t => (t.Tag as SKSurface)?.Dispose());
        if (texture == null) return;   // 画面に出すのが追いつかない
        var surface = texture.Tag as SKSurface;
        if (surface == null) texture.Tag = surface = CreateTextureSurface(g.Context, texture);
        if (surface == null)
        {
            // 共有のテクスチャに描けない: これまでどおり読み戻す方法に戻す
            shared.Release(texture);
            _presentsToShared = false;
            _sharedRequested = null;
            _backendDirty = true;
            Request();
            return;
        }
        engine.RenderToGpuSurface(p, scene, frame, surface);
        // 画面の描画 (別のコンテキスト) が読む前に、GPU での描画を終わらせる
        g.Context.Flush(true, true);
        shared.Present(texture);
        LastRenderMs = sw.Elapsed.TotalMilliseconds;
        LastRenderSize = (w, h);
        Rendered?.Invoke(LastRenderMs, w, h);
        var quads = engine.LastQuads.ToList();
        bool post;
        lock (_frameLock)
        {
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

    /// <summary>描いたフレームがあるか。</summary>
    public bool HasFrame { get { lock (_frameLock) return _front != null; } }

    /// <summary>
    /// 最新のフレームを canvas の dest に描く (画面の描画のスレッドから呼ばれる)。フレームを複製せずにそのまま描く。
    /// GPU の画面なら、描き終える前にフレームが書き換わらないよう、ここで GPU へ送り終えてから戻る。
    /// </summary>
    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKFilterQuality quality, GRContext? gpu)
    {
        lock (_frameLock)
        {
            if (_front == null) return false;
            using var pixmap = _front.PeekPixels();
            using var image = pixmap == null ? null : SKImage.FromPixels(pixmap);
            if (image == null) return false;
            using var paint = new SKPaint { FilterQuality = quality };
            canvas.DrawImage(image, dest, paint);
            gpu?.Flush();
            return true;
        }
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
