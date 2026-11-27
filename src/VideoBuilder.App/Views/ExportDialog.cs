using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Views;

public sealed class ExportDialog : DialogBase
{
    private sealed record Format(string Name, string Extension, string VideoCodec, string AudioCodec, bool Sequence = false);

    private static readonly Format[] AllFormats =
    {
        new("MP4 (H.264 + AAC)", "mp4", "libx264", "aac"),
        new("MP4 (H.265 + AAC)", "mp4", "libx265", "aac"),
        new("WebM (VP9 + Opus)", "webm", "libvpx-vp9", "libopus"),
        new("WebM (AV1 + Opus)", "webm", "libsvtav1", "libopus"),
        new("MOV (ProRes + PCM)", "mov", "prores_ks", "pcm_s16le"),
        new("連番 PNG", "png", "", "", true),
        new("WAV (音声のみ)", "wav", "", "pcm_s16le"),
        new("FLAC (音声のみ)", "flac", "", "flac"),
    };

    private readonly List<Format> _formats;
    private readonly ComboBox _format;
    private readonly TextBox _path = new() { Watermark = "出力先のファイル" };
    private readonly NumberBox _crf = new() { Minimum = 0, Maximum = 51, Decimals = 0, Value = 20, Width = 64, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _preset = new() { ItemsSource = new[] { "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow" }, SelectedIndex = 3, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 8 };
    private readonly TextBlock _status = new() { Text = " ", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _start, _close;
    private readonly StackPanel _options = new() { Spacing = 8 };
    private readonly CheckBox _rangeOnly = new();
    private CancellationTokenSource? _cancel;

    public ExportDialog() : base("動画の出力", 480)
    {
        var session = Svc.Session;
        // この環境の FFmpeg で使えない形式は出さない
        _formats = AllFormats.Where(f => f.Sequence ||
            (f.VideoCodec == "" || MediaWriter.IsEncoderAvailable(f.VideoCodec)) && (f.AudioCodec == "" || MediaWriter.IsEncoderAvailable(f.AudioCodec))).ToList();
        _format = new ComboBox { ItemsSource = _formats.Select(f => f.Name).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        // おすすめの構成のエンコーダ (ソフトウェアのもの) なら、はじめからそれを選ぶ。ハードウェアのものは見つかってから (AddHardwareFormatsAsync)
        if (PreferredIndex() is var pre and >= 0) _format.SelectedIndex = _initialFormat = pre;
        _format.SelectionChanged += (_, _) => { if (_format.SelectedIndex >= 0) { FixExtension(); UpdateOptions(); } };
        // このマシンで使えるハードウェアエンコーダを裏で調べて、見つかったら選択肢に足す
        _ = AddHardwareFormatsAsync();

        var browse = new IconButton("FolderOpen", "出力先を選ぶ", flat: false) { Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += async (_, _) => await Browse();
        var pathRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse); pathRow.Children.Add(_path);

        var p = session.Project;
        var info = new TextBlock { Text = $"{p.Settings.Width} x {p.Settings.Height}, {p.Settings.Fps.Value:0.###} fps, {session.SceneLength} フレーム ({p.FormatTime(session.SceneLength)})" };
        info.Classes.Add("secondary");

        _options.Children.Add(Row("形式", _format, 100));
        _options.Children.Add(Row("出力先", pathRow, 100));
        _options.Children.Add(Row("品質 (CRF)", _crf, 100));
        _options.Children.Add(Row("速度", _preset, 100));
        var (rs, re) = session.EffectiveRange;
        bool hasRange = session.Scene.RangeStart != null || session.Scene.RangeEnd != null;
        _rangeOnly.Content = hasRange
            ? $"出力範囲だけを出力する ({p.FormatTime(rs)} - {p.FormatTime(re + 1)}, {re - rs + 1} フレーム)"
            : "出力範囲だけを出力する (範囲が設定されていません。[ と ] で設定できます)";
        _rangeOnly.IsEnabled = hasRange; _rangeOnly.IsChecked = hasRange;
        _options.Children.Add(_rangeOnly);
        Body.Children.Add(info);
        Body.Children.Add(_options);
        Body.Children.Add(_progress);
        _status.Classes.Add("secondary");
        Body.Children.Add(_status);
        ToolTip.SetTip(_crf, "小さいほど高画質でファイルが大きくなります (目安: 18〜23)");

        _close = AddButton("閉じる", () => { if (_cancel != null) _cancel.Cancel(); else Close(); });
        _start = AddButton("出力", Start, accent: true, icon: "Output");

        var baseName = session.FilePath != null ? Path.ChangeExtension(session.FilePath, null) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "output");
        _path.Text = baseName + ".mp4";
        FixExtension();
        UpdateOptions();
        if (session.Scene.Length == 0) { _start.IsEnabled = false; _status.Text = "タイムラインにオブジェクトがありません"; }
        Closing += (_, e) => { if (_cancel != null) { e.Cancel = true; _cancel.Cancel(); } };
    }

    private Format Current => _formats[Math.Clamp(_format.SelectedIndex, 0, _formats.Count - 1)];

    /// <summary>今選んでいる形式の名前 (テスト用)。</summary>
    internal string SelectedFormatName => Current.Name;

    private async Task AddHardwareFormatsAsync()
    {
        List<HardwareEncoder> found;
        try { found = await Task.Run(HardwareEncoders.FindAvailable); }
        catch { return; }
        if (found.Count == 0 || !MediaWriter.IsEncoderAvailable("aac")) return;
        // まだ形式を選び直していなければ、おすすめの構成のエンコーダ (設計書 22.63) を選ぶ
        bool untouched = _format.SelectedIndex == _initialFormat;
        var selected = Current;
        // ソフトウェアの映像形式のすぐ後ろ (連番 PNG・音声のみの前) に並べる
        int at = _formats.FindIndex(f => f.Sequence || f.VideoCodec == "");
        if (at < 0) at = _formats.Count;
        _formats.InsertRange(at, found.Select(h => new Format($"MP4 ({h.Codec} + AAC) - ハードウェア ({h.Api})", "mp4", h.Encoder, "aac")));
        _format.ItemsSource = _formats.Select(f => f.Name).ToList();
        int preferred = untouched ? PreferredIndex() : -1;
        _format.SelectedIndex = preferred >= 0 ? preferred : _formats.IndexOf(selected);
        if (preferred >= 0) _initialFormat = preferred;
    }

    // 開いたときに選んでいた形式 (利用者が選び直したかを見分ける)
    private int _initialFormat;

    /// <summary>環境設定の「書き出しで最初に選ぶエンコーダ」の形式の番号 (なければ -1)。</summary>
    private int PreferredIndex()
    {
        var want = Svc.Settings.PreferredVideoEncoder;
        return want.Length == 0 ? -1 : _formats.FindIndex(f => !f.Sequence && f.VideoCodec == want);
    }

    private void UpdateOptions()
    {
        string codec = Current.VideoCodec, family = HardwareEncoders.FamilyOf(codec);
        _crf.IsEnabled = family.Length > 0 || codec is "libx264" or "libx265" or "libvpx-vp9" or "libsvtav1";
        _preset.IsEnabled = codec is "libx264" or "libx265" || family is "nvenc" or "qsv" or "amf" or "mf";
    }

    private void FixExtension()
    {
        if (!string.IsNullOrWhiteSpace(_path.Text)) _path.Text = Path.ChangeExtension(_path.Text, Current.Extension);
    }

    private async Task Browse()
    {
        var f = Current;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "出力先", DefaultExtension = f.Extension,
            SuggestedFileName = Path.GetFileName(_path.Text ?? "output." + f.Extension),
            FileTypeChoices = new[] { new FilePickerFileType(f.Name) { Patterns = new[] { "*." + f.Extension } } },
        });
        var path = file?.TryGetLocalPath();
        if (path != null) _path.Text = path;
    }

    private async void Start()
    {
        var session = Svc.Session;
        var path = (_path.Text ?? "").Trim();
        if (path.Length == 0) { _status.Text = "出力先を指定してください"; return; }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir == null || !Directory.Exists(dir)) { _status.Text = "出力先のフォルダがありません"; return; }
        var f = Current;
        Svc.Preview.Stop();

        var stats = Svc.Performance.BeginExport();
        var request = new ExportRequest
        {
            Stats = stats,
            Project = session.Project.Clone(), SceneId = session.Scene.Id,
            StartFrame = _rangeOnly.IsChecked == true ? session.EffectiveRange.Start : 0,
            EndFrame = _rangeOnly.IsChecked == true ? session.EffectiveRange.End : session.Scene.Length - 1,
            ImageSequence = f.Sequence,
            UseGpu = Svc.Preview.IsGpuActive,   // プレビューと同じ描画方式で出力する
            Settings = new ExportSettings { Path = path, VideoCodec = f.VideoCodec, AudioCodec = f.AudioCodec, Crf = (int)_crf.Value, Preset = (string)_preset.SelectedItem! },
        };
        _status.SetValue(L.VerbatimProperty, false);
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _options.IsEnabled = false; _start.IsEnabled = false; _close.Content = "キャンセル";
        var started = DateTime.UtcNow;
        var last = DateTime.MinValue;
        var progress = new Progress<(int Done, int Total)>(v =>
        {
            // 進捗表示は 1 秒に 10 回まで
            if ((DateTime.UtcNow - last).TotalMilliseconds < 100 && v.Done != v.Total) return;
            last = DateTime.UtcNow;
            _progress.Value = (double)v.Done / v.Total;
            var elapsed = DateTime.UtcNow - started;
            var remain = v.Done > 0 ? TimeSpan.FromSeconds(elapsed.TotalSeconds / v.Done * (v.Total - v.Done)) : TimeSpan.Zero;
            _status.Text = $"{v.Done} / {v.Total} フレーム   経過 {elapsed:mm\\:ss}   残り 約 {remain:mm\\:ss}";
        });
        string result;
        string? error = null;
        try
        {
            await Task.Run(() => Exporter.Run(request, progress, token), token);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);   // 残っている進捗通知を先に流す
            _progress.Value = 1;
            result = $"出力が完了しました ({DateTime.UtcNow - started:mm\\:ss})";
        }
        catch (OperationCanceledException) { result = "キャンセルしました"; _progress.Value = 0; error = L.T("キャンセル"); }
        catch (Exception ex) { result = "出力に失敗しました: " + ex.Message; error = ex.Message; }
        Svc.Performance.EndExport(stats, f.Name, path, session.Project.FrameToSeconds(request.EndFrame - request.StartFrame + 1), error);
        // パフォーマンスチェックモードなら、時間の内訳も出す
        bool verbatim = Svc.Performance.Enabled && error == null;
        if (verbatim)
            result = L.Display(result) + "\n" + string.Join("  ", PerformanceMonitor.Describe(stats, session.Project.FrameToSeconds(request.EndFrame - request.StartFrame + 1))
                .Skip(2).Select(x => $"{L.T(x.Label)} {x.Value}"));
        _cancel.Dispose(); _cancel = null;
        // 内訳を付けたときは、もう訳してあるので、そのまま出す
        _status.SetValue(L.VerbatimProperty, verbatim);
        _status.Text = result;
        _options.IsEnabled = true; _start.IsEnabled = true; _close.Content = "閉じる";
    }
}
