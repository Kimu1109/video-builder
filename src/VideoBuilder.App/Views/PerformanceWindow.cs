using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Views;

/// <summary>描画時間の推移のグラフ。点線は 1 フレームに使える時間 (これを超えると再生が遅れる)。</summary>
public sealed class Sparkline : Control
{
    public double[] Values { get; set; } = Array.Empty<double>();
    /// <summary>目安の線 (1 フレームに使える時間)。</summary>
    public double Budget { get; set; }

    public Sparkline() { Height = 64; }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.FillRectangle(Svc.Brush("Bg.Input"), r);
        var v = Values;
        double max = Math.Max(Budget * 1.5, v.Length == 0 ? 1 : v.Max()) * 1.1;
        if (max <= 0) return;
        double Y(double value) => r.Height - value / max * (r.Height - 4) - 2;
        if (Budget > 0)
        {
            var dash = new Pen(Svc.Brush("Warning"), 1, new DashStyle(new double[] { 3, 3 }, 0));
            ctx.DrawLine(dash, new Point(0, Y(Budget)), new Point(r.Width, Y(Budget)));
        }
        if (v.Length < 2) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < v.Length; i++)
            {
                var p = new Point(r.Width * i / (v.Length - 1), Y(v[i]));
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(Svc.Brush("Accent"), 1.5), geo);
    }
}

/// <summary>
/// パフォーマンスチェックの詳細: プレビュー・再生・音声・ベンチマーク・出力・メモリ。どちらの編集画面でも使う。
/// 閉じても隠すだけ (モードはオンのまま。プレビューの左上の表示をクリックすると、また開く)。
/// </summary>
public sealed class PerformanceWindow : Window
{
    private readonly PerformanceMonitor _monitor = Svc.Performance;
    private readonly StackPanel _body = new() { Margin = new Thickness(14), Spacing = 4 };
    private readonly Sparkline _graph = new();
    private readonly Dictionary<string, TextBlock> _values = new();
    private readonly StackPanel _exports = new() { Spacing = 2 };
    private readonly TextBlock _benchStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _benchButton;
    private readonly NumberBox _benchFrames = new() { Minimum = 10, Maximum = 10000, Decimals = 0, Value = 120, Width = 72 };
    private CancellationTokenSource? _benchCancel;
    public bool AllowClose { get; set; }

    public PerformanceWindow()
    {
        Title = "パフォーマンス - video-builder";
        Width = 440; Height = 720; MinWidth = 360;
        ShowInTaskbar = false;
        KeyDown += (_, e) => { if (EditorCommands.HandleKey(e, this)) e.Handled = true; };

        Heading("プレビュー");
        Value("描画方式");
        Value("描画の大きさ");
        Value("1 フレームの描画");
        Value("描画 (平均 / 95% / 最大)");
        Value("表示");
        _body.Children.Add(_graph);
        Note("線: 直近の描画時間。点線: 1 フレームに使える時間 (これより上だと、再生でフレームが飛ぶ)。");

        Heading("再生");
        Value("フレームを飛ばした割合");
        Value("音声のミックス (平均 / 最大)");

        Heading("ベンチマーク");
        Note("今のシーンを、現在位置からプレビューと同じ大きさ・描画方式で続けて描き、描画の速さを測ります。");
        _benchButton = new Button { Content = "測る" };
        _benchButton.Click += (_, _) => _ = RunBenchmarkAsync();
        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = "フレーム数", VerticalAlignment = VerticalAlignment.Center }, _benchFrames, _benchButton },
        });
        _benchStatus.Classes.Add("mono");
        _body.Children.Add(_benchStatus);

        Heading("出力 (エンコード)");
        _body.Children.Add(_exports);

        Heading("メモリ");
        Value("使用中");

        var reset = new Button { Content = "数値をリセット" };
        reset.Click += (_, _) => _monitor.Reset();
        var copy = new Button { Content = "結果をコピー" };
        copy.Click += async (_, _) => { if (Clipboard != null) await Clipboard.SetTextAsync(_monitor.Report()); };
        var off = new Button { Content = "パフォーマンスチェックを終了" };
        off.Click += (_, _) => _monitor.Enabled = false;
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in new[] { reset, copy, off }) { b.Margin = new Thickness(0, 0, 8, 8); buttons.Children.Add(b); }
        _body.Children.Add(buttons);

        Content = new ScrollViewer { Content = _body };
        _monitor.Updated += Refresh;
        Svc.Preview.BackendChanged += Refresh;
        Refresh();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 閉じるボタンでは隠すだけ (メインウィンドウと一緒のときは閉じる。ToolWindow と同じ)
        if (!AllowClose && e.CloseReason == WindowCloseReason.WindowClosing) { e.Cancel = true; Hide(); }
        else { _benchCancel?.Cancel(); _monitor.Updated -= Refresh; Svc.Preview.BackendChanged -= Refresh; }
        base.OnClosing(e);
    }

    // ---------------- 部品 ----------------

    private void Heading(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 2) };
        t.Classes.Add("heading");
        _body.Children.Add(t);
    }

    private void Note(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.Classes.Add("secondary");
        _body.Children.Add(t);
    }

    private void Value(string label) => _body.Children.Add(Line(label, _values[label] = new TextBlock()));

    private static Control Line(string label, TextBlock value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), MinHeight = 20 };
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        l.Classes.Add("secondary");
        grid.Children.Add(l);
        value.Classes.Add("mono");
        value.VerticalAlignment = VerticalAlignment.Center;
        value.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private void Set(string label, string text, IBrush? brush = null)
    {
        var tb = _values[label];
        tb.SetValue(L.VerbatimProperty, true);
        tb.Text = text;
        if (brush != null) tb.Foreground = brush; else tb.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>1 フレームに使える時間に対して、余裕があれば緑、ぎりぎりなら黄、超えていれば赤。</summary>
    public static IBrush? Grade(double ms, double budget) =>
        budget <= 0 || ms <= 0 ? null : ms <= budget * 0.7 ? Brushes.MediumSeaGreen : ms <= budget ? Brushes.Goldenrod : Brushes.IndianRed;

    // ---------------- 更新 ----------------

    private void Refresh()
    {
        if (!IsVisible) return;
        var p = Svc.Session.Project.Settings;
        double budget = 1000 / Math.Max(1, p.Fps.Value);
        var r = _monitor.RenderMs.Summarize();
        var backend = L.Display(Svc.Preview.BackendDescription);
        Set("描画方式", PerformanceMonitor.ShortBackend(backend));
        ToolTip.SetTip(_values["描画方式"], backend);
        var (w, h) = _monitor.RenderSize;
        Set("描画の大きさ", w > 0 ? $"{w} x {h}  ({L.T("プロジェクト")} {p.Width} x {p.Height})" : "-");
        Set("1 フレームの描画", r.Count > 0 ? $"{PerformanceMonitor.Ms(r.Last)}  ({L.T("目安")} {PerformanceMonitor.Ms(budget)})" : "-", Grade(r.Last, budget));
        Set("描画 (平均 / 95% / 最大)", r.Count > 0 ? $"{PerformanceMonitor.Ms(r.Average)} / {PerformanceMonitor.Ms(r.P95)} / {PerformanceMonitor.Ms(r.Max)}" : "-", Grade(r.P95, budget));
        Set("表示", $"{_monitor.DisplayFps:0} fps" + (Svc.Preview.IsPlaying ? $"  ({L.T("目標")} {p.Fps.Value:0.##} fps)" : $"  ({L.T("停止中")})"));
        _graph.Values = _monitor.RenderMs.Values;
        _graph.Budget = budget;
        _graph.InvalidateVisual();

        Set("フレームを飛ばした割合", _monitor.PlaybackFrames > 0
            ? $"{_monitor.PlaybackSkipRatio * 100:0.0}%  ({_monitor.PlaybackSkipped} / {_monitor.PlaybackFrames + _monitor.PlaybackSkipped})"
            : L.T("(再生すると測ります)"), _monitor.PlaybackFrames > 0 ? (_monitor.PlaybackSkipRatio < 0.02 ? Brushes.MediumSeaGreen : _monitor.PlaybackSkipRatio < 0.1 ? Brushes.Goldenrod : Brushes.IndianRed) : null);
        var a = _monitor.AudioMs.Summarize();
        Set("音声のミックス (平均 / 最大)", a.Count > 0
            ? $"{PerformanceMonitor.Ms(a.Average)} / {PerformanceMonitor.Ms(a.Max)}  ({L.T("1 回の長さ")} {PerformanceMonitor.Ms(_monitor.AudioBlockMs)})"
            : L.T("(再生すると測ります)"), a.Count > 0 ? Grade(a.Max, _monitor.AudioBlockMs) : null);

        if (_monitor.LastBenchmark is { } b && _benchCancel == null) ShowBenchmark(b);

        _exports.Children.Clear();
        if (_monitor.RunningExport is { } running) AddExport(L.T("出力中"), running, Svc.Session.Project.FrameToSeconds(running.TotalFrames));
        foreach (var e in _monitor.Exports.Take(3))
            AddExport($"{e.Time:HH:mm:ss}  {e.Format}" + (e.Error != null ? $"  ({e.Error})" : ""), e.Stats, e.VideoSeconds);
        if (_exports.Children.Count == 0)
        {
            var none = new TextBlock { Text = "(まだ出力していません。「動画の出力」で出力すると、ここに内訳が出ます)", TextWrapping = TextWrapping.Wrap };
            none.Classes.Add("secondary");
            _exports.Children.Add(none);
        }

        Set("使用中", PerformanceMonitor.MemoryText());
    }

    private void AddExport(string title, ExportStats stats, double videoSeconds)
    {
        var head = L.Verbatim(title);
        head.FontWeight = FontWeight.SemiBold;
        head.Margin = new Thickness(0, 4, 0, 0);
        _exports.Children.Add(head);
        foreach (var (label, value) in PerformanceMonitor.Describe(stats, videoSeconds))
        {
            var v = L.Verbatim(value);
            _exports.Children.Add(Line(label, v));
        }
    }

    // ---------------- ベンチマーク ----------------

    private async Task RunBenchmarkAsync()
    {
        if (_benchCancel != null) { _benchCancel.Cancel(); return; }
        var session = Svc.Session;
        if (session.Scene.Length == 0) { _benchStatus.Text = L.T("シーンが空です"); return; }
        Svc.Preview.Stop();
        var project = session.SnapshotForRead();
        var scene = project.FindScene(session.Scene.Id) ?? project.Root;
        var (w, h) = Svc.Preview.TargetSize(project.Settings.Width, project.Settings.Height);
        int count = (int)_benchFrames.Value, start = session.CurrentFrame;
        bool gpu = Svc.Preview.IsGpuActive;
        _benchCancel = new CancellationTokenSource();
        var token = _benchCancel.Token;
        _benchButton.Content = "中止";
        var progress = new Progress<int>(n => _benchStatus.Text = L.Format("測っています... {0} / {1}", n.ToString(), count.ToString()));
        try
        {
            var result = await Task.Run(() => RenderBenchmark.Run(project, scene, start, count, w, h, gpu, draft: true, progress, token), token);
            _monitor.LastBenchmark = result;
            _benchCancel.Dispose(); _benchCancel = null;
            ShowBenchmark(result);
        }
        catch (OperationCanceledException) { _benchStatus.Text = L.T("中止しました"); }
        catch (Exception ex) { _benchStatus.Text = L.T("測れませんでした: ") + ex.Message; }
        finally
        {
            _benchCancel?.Dispose(); _benchCancel = null;
            _benchButton.Content = "測る";
        }
    }

    private void ShowBenchmark(RenderBenchmark.Result b)
    {
        var budget = 1000 / Math.Max(1, Svc.Session.Project.Settings.Fps.Value);
        _benchStatus.SetValue(L.VerbatimProperty, true);
        _benchStatus.Text = $"{b.FramesPerSecond:0.0} fps  ({b.Width} x {b.Height}, {(b.Gpu ? "GPU" : "CPU")}, {b.Frames} {L.T("フレーム")})\n" +
                            $"avg {PerformanceMonitor.Ms(b.Milliseconds.Average)} / p95 {PerformanceMonitor.Ms(b.Milliseconds.P95)} / max {PerformanceMonitor.Ms(b.Milliseconds.Max)}";
        _benchStatus.Foreground = Grade(b.Milliseconds.P95, budget) ?? _benchStatus.Foreground;
    }

    public void Open(Window owner)
    {
        if (!IsVisible)
        {
            Position = new PixelPoint(owner.Position.X + (int)(owner.Width * owner.RenderScaling) - (int)(Width * owner.RenderScaling) - 24, owner.Position.Y + 80);
            base.Show(owner);
        }
        Activate();
        Refresh();
    }
}
