using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Services;
using VideoBuilder.App.Views;

namespace VideoBuilder.App.Shell;

/// <summary>
/// パフォーマンスチェックモードのとき、プレビューの左上に出す小さな表示 (描画時間・表示 FPS・描画の大きさ)。
/// クリックで詳細 (<see cref="PerformanceWindow"/>) を開く。プレビューは共通なので、どちらの編集画面でも出る。
/// </summary>
public sealed class PerformanceOverlay : Border
{
    private readonly PerformanceMonitor _monitor = Svc.Performance;
    private readonly TextBlock _line1 = new(), _line2 = new(), _line3 = new();

    public PerformanceOverlay()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(12);
        Padding = new Thickness(8, 5);
        CornerRadius = new CornerRadius(3);
        Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0));
        Cursor = new Cursor(StandardCursorType.Hand);
        foreach (var t in new[] { _line1, _line2, _line3 })
        {
            t.SetValue(L.VerbatimProperty, true);
            t.Classes.Add("mono");
            t.FontSize = 11;
            t.Foreground = Brushes.White;
        }
        Child = new StackPanel { Spacing = 1, Children = { _line1, _line2, _line3 } };
        ToolTip.SetTip(this, "クリックで詳細を表示");
        PointerPressed += (_, e) => { EditorCommands.ShowPerformanceWindow(); e.Handled = true; };
        _monitor.Updated += Refresh;
        _monitor.EnabledChanged += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        IsVisible = _monitor.Enabled;
        if (!IsVisible) return;
        var p = Svc.Session.Project.Settings;
        double budget = 1000 / Math.Max(1, p.Fps.Value);
        var r = _monitor.RenderMs.Summarize();
        _line1.Text = r.Count > 0 ? $"{L.T("描画")} {PerformanceMonitor.Ms(r.Last)}  (p95 {PerformanceMonitor.Ms(r.P95)})" : $"{L.T("描画")} -";
        _line1.Foreground = PerformanceWindow.Grade(r.P95, budget) ?? Brushes.White;
        string play = Svc.Preview.IsPlaying
            ? $"{_monitor.DisplayFps:0} / {p.Fps.Value:0.##} fps" + (_monitor.PlaybackFrames > 0 ? $"  {L.T("飛ばした")} {_monitor.PlaybackSkipRatio * 100:0}%" : "")
            : $"{_monitor.DisplayFps:0} fps";
        _line2.Text = $"{L.T("表示")} {play}";
        var (w, h) = _monitor.RenderSize;
        _line3.Text = $"{(w > 0 ? $"{w}x{h}" : "-")}  {(Svc.Preview.IsGpuActive ? "GPU" : "CPU")}";
    }
}
