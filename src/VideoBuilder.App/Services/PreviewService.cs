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
    private readonly RenderEngine _engine = new();
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

    public PreviewService(EditorSession session, AppSettings settings)
    {
        _session = session; _settings = settings;
        session.ProjectChanged += () => { _snapshot = null; Request(); };
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
        if (s == null) _snapshot = s = new Snapshot(_session.Project.Clone(), _session.Scene.Id);
        return s;
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
                try { RenderOne(req, frame); }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
        }
    }

    private void RenderOne(Snapshot req, int frame)
    {
        var p = req.Project;
        var scene = p.FindScene(req.SceneId) ?? p.Root;
        int div = Math.Clamp(_settings.PreviewDivisor, 1, 8);
        int w = Math.Max(2, p.Settings.Width / div), h = Math.Max(2, p.Settings.Height / div);
        if (_back == null || _back.Width != w || _back.Height != h)
        {
            _back?.Dispose();
            _back = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        }
        var sw = Stopwatch.StartNew();
        using (var surface = SKSurface.Create(_back.Info, _back.GetPixels(), _back.RowBytes))
            _engine.Render(p, scene, frame, surface);
        LastRenderMs = sw.Elapsed.TotalMilliseconds;
        var quads = _engine.LastQuads.ToList();
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
                _mixer.Mix(s.Project, s.Project.FindScene(s.SceneId) ?? s.Project.Root, pos, buffer);
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
        _thread.Join(1000);
        _audio?.Dispose();
        _mixer.Dispose();
        _engine.Dispose();
        _front?.Dispose(); _back?.Dispose();
    }
}
