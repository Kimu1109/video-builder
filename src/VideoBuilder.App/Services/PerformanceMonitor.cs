using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Services;

/// <summary>
/// パフォーマンスチェックモードで表示する数値を集める。プレビューの描画時間・表示 FPS・再生の遅れ・音声のミックス時間・
/// 出力 (エンコード) の内訳・ベンチマークの結果・メモリ。詳細編集・シンプル編集のどちらでも同じものを使う。
/// 集めるのはモードがオンのときだけ (オフのときは、描画のたびの負担を増やさない)。
/// </summary>
public sealed class PerformanceMonitor
{
    private readonly PreviewService _preview;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly Queue<long> _shown = new();          // プレビューに絵を出した時刻 (直近 1 秒)
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _lastPlayedFrame = -1;
    private long _playFrames, _playSkipped;

    /// <summary>プレビューの 1 フレームの描画時間 (ms)。</summary>
    public RollingStats RenderMs { get; } = new(240);
    /// <summary>再生中の、音声 1 回ぶんのミックス時間 (ms)。</summary>
    public RollingStats AudioMs { get; } = new(120);
    /// <summary>音声 1 回ぶんの長さ (ms)。ミックス時間がこれに近づくと音が途切れる。</summary>
    public double AudioBlockMs { get; private set; }
    public (int Width, int Height) RenderSize { get; private set; }

    /// <summary>プレビューに 1 秒あたり何枚の絵を出したか。</summary>
    public double DisplayFps { get; private set; }
    /// <summary>再生中に、描画が間に合わず飛ばしたフレームの割合 (0〜1)。</summary>
    public double PlaybackSkipRatio => _playFrames + _playSkipped == 0 ? 0 : (double)_playSkipped / (_playFrames + _playSkipped);
    public long PlaybackFrames => _playFrames;
    public long PlaybackSkipped => _playSkipped;

    /// <summary>出力の記録 (新しいものが先頭)。</summary>
    public List<ExportRecord> Exports { get; } = new();
    /// <summary>出力中なら、その内訳 (途中経過)。</summary>
    public ExportStats? RunningExport { get; private set; }
    public RenderBenchmark.Result? LastBenchmark { get; set; }

    /// <summary>表示を更新する合図 (オンの間、1 秒に 4 回。UI スレッド)。</summary>
    public event Action? Updated;
    public event Action? EnabledChanged;

    public sealed record ExportRecord(DateTime Time, string Format, string File, ExportStats Stats, double VideoSeconds, string? Error);

    public PerformanceMonitor(PreviewService preview, AppSettings settings)
    {
        _preview = preview; _settings = settings;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        preview.Rendered += (ms, w, h) => { if (Enabled) { RenderMs.Add(ms); RenderSize = (w, h); } };
        preview.AudioMixed += (ms, blockMs) => { if (Enabled) { AudioMs.Add(ms); AudioBlockMs = blockMs; } };
        preview.FrameReady += OnFrameShown;
        preview.PlayStateChanged += () => _lastPlayedFrame = -1;
        if (Enabled) _timer.Start();
    }

    /// <summary>パフォーマンスチェックモード。設定に覚える。</summary>
    public bool Enabled
    {
        get => _settings.PerformanceMode;
        set
        {
            if (_settings.PerformanceMode == value) return;
            _settings.PerformanceMode = value;
            _settings.Save();
            if (value) { Reset(); _timer.Start(); } else _timer.Stop();
            EnabledChanged?.Invoke();
            Updated?.Invoke();
        }
    }

    /// <summary>プレビュー・再生の数値を数え直す (出力の記録は残す)。</summary>
    public void Reset()
    {
        RenderMs.Clear(); AudioMs.Clear();
        lock (_shown) _shown.Clear();
        _playFrames = _playSkipped = 0;
        _lastPlayedFrame = -1;
        DisplayFps = 0;
        Updated?.Invoke();
    }

    private void OnFrameShown()
    {
        if (!Enabled) return;
        long now = _clock.ElapsedMilliseconds;
        lock (_shown)
        {
            _shown.Enqueue(now);
            while (_shown.Count > 0 && now - _shown.Peek() > 1000) _shown.Dequeue();
        }
        // 再生中: 前に出したフレームから飛んだぶんを、間に合わなかったフレームとして数える
        if (_preview.IsPlaying)
        {
            int frame = Svc.Session.CurrentFrame;
            if (_lastPlayedFrame >= 0 && frame > _lastPlayedFrame)
            {
                _playFrames++;
                _playSkipped += frame - _lastPlayedFrame - 1;
            }
            _lastPlayedFrame = frame;
        }
    }

    private void Tick()
    {
        long now = _clock.ElapsedMilliseconds;
        lock (_shown)
        {
            while (_shown.Count > 0 && now - _shown.Peek() > 1000) _shown.Dequeue();
            DisplayFps = _shown.Count;
        }
        Updated?.Invoke();
    }

    // ---------------- 出力 ----------------

    /// <summary>出力を始める。返した内訳を ExportRequest.Stats に渡す。</summary>
    public ExportStats BeginExport()
    {
        RunningExport = new ExportStats();
        return RunningExport;
    }

    /// <summary>出力が終わった (失敗・中止でも呼ぶ)。</summary>
    public void EndExport(ExportStats stats, string format, string file, double videoSeconds, string? error)
    {
        if (RunningExport == stats) RunningExport = null;
        Exports.Insert(0, new ExportRecord(DateTime.Now, format, file, stats, videoSeconds, error));
        if (Exports.Count > 10) Exports.RemoveAt(Exports.Count - 1);
        Updated?.Invoke();
    }

    // ---------------- 表示用の文字 ----------------

    public static string Ms(double ms) => ms >= 100 ? $"{ms:0} ms" : $"{ms:0.0} ms";
    public static string Time(TimeSpan t) => t.TotalHours >= 1 ? $"{t:h\\:mm\\:ss}" : t.TotalSeconds >= 60 ? $"{t:m\\:ss}" : $"{t.TotalSeconds:0.0} s";

    /// <summary>出力の内訳を 1 行ずつ (ラベル, 値)。</summary>
    public static List<(string Label, string Value)> Describe(ExportStats s, double videoSeconds)
    {
        double total = Math.Max(1e-6, s.Elapsed.TotalSeconds);
        string Part(TimeSpan t) => $"{Time(t)}  ({t.TotalSeconds / total * 100:0}%)";
        var rows = new List<(string, string)>
        {
            ("合計時間", Time(s.Elapsed)),
            ("フレーム", $"{s.Frames} / {s.TotalFrames}"),
            ("速さ", $"{s.FramesPerSecond:0.0} fps"),
        };
        if (videoSeconds > 0 && s.Finished) rows.Add(("実時間との比", $"{videoSeconds / total:0.00} x"));
        rows.Add(("描画", Part(s.Render)));
        rows.Add(("変換 (YUV)", Part(s.Convert)));
        rows.Add(("音声のミックス", Part(s.Audio)));
        rows.Add(("エンコード", Part(s.Encode)));
        rows.Add(("エンコード待ち", Part(s.WaitForEncoder)));
        return rows;
    }

    /// <summary>今の数値を、そのまま貼り付けられる文字にする (不具合の報告などに使う)。</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        var p = Svc.Session.Project.Settings;
        sb.AppendLine($"video-builder {typeof(PerformanceMonitor).Assembly.GetName().Version?.ToString(3)}  {Environment.OSVersion}  .NET {Environment.Version}  CPU {Environment.ProcessorCount}");
        sb.AppendLine($"{L.T("描画方式")}: {ShortBackend(L.Display(_preview.BackendDescription))}");
        sb.AppendLine($"{L.T("プロジェクト")}: {p.Width}x{p.Height} {p.Fps.Value:0.###}fps");
        var r = RenderMs.Summarize();
        sb.AppendLine($"{L.T("プレビュー")}: {RenderSize.Width}x{RenderSize.Height}  {L.T("描画")} avg {Ms(r.Average)} / p95 {Ms(r.P95)} / max {Ms(r.Max)} ({r.Count})  {L.T("表示")} {DisplayFps:0} fps");
        if (_playFrames > 0) sb.AppendLine($"{L.T("再生")}: {_playFrames} / {L.T("飛ばした")} {_playSkipped} ({PlaybackSkipRatio * 100:0.0}%)");
        var a = AudioMs.Summarize();
        if (a.Count > 0) sb.AppendLine($"{L.T("音声のミックス")}: avg {Ms(a.Average)} / max {Ms(a.Max)} ({L.T("1 回の長さ")} {Ms(AudioBlockMs)})");
        if (LastBenchmark is { } b)
            sb.AppendLine($"{L.T("ベンチマーク")}: {b.Width}x{b.Height} {(b.Gpu ? "GPU" : "CPU")} {b.Frames} {L.T("フレーム")}  {b.FramesPerSecond:0.0} fps  avg {Ms(b.Milliseconds.Average)} / p95 {Ms(b.Milliseconds.P95)} / max {Ms(b.Milliseconds.Max)}");
        foreach (var e in Exports.Take(3))
        {
            sb.AppendLine($"{L.T("出力")} {e.Time:HH:mm:ss} {e.Format}{(e.Error != null ? " (" + e.Error + ")" : "")}");
            foreach (var (label, value) in Describe(e.Stats, e.VideoSeconds)) sb.AppendLine($"  {L.T(label)}: {value}");
        }
        sb.AppendLine($"{L.T("メモリ")}: {MemoryText()}");
        return sb.ToString();
    }

    /// <summary>描画方式の説明を 1 行に縮める (GPU を使えない理由が長いことがある)。</summary>
    public static string ShortBackend(string description)
    {
        var line = description.Split('\n')[0].Trim();
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    public static string MemoryText()
    {
        using var proc = Process.GetCurrentProcess();
        return $"{proc.WorkingSet64 / 1048576.0:0} MB ({L.T("管理メモリ")} {GC.GetTotalMemory(false) / 1048576.0:0} MB)";
    }
}
