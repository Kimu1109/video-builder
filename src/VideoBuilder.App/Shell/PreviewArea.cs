using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Shell;

/// <summary>プレビューと、その下の再生バー (先頭へ・再生・時刻・シークバー)。どちらの編集画面でも共通。</summary>
public sealed class PreviewArea : DockPanel
{
    private readonly EditorSession _session = Svc.Session;
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _frameText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), MinWidth = 64, TextAlignment = TextAlignment.Right };
    private readonly MiniSlider _seek = new() { Margin = new Thickness(4, 0) };
    private readonly IconButton _play;

    public PreviewView Preview { get; } = new();

    public PreviewArea()
    {
        _play = new IconButton("Play.Fill", "再生 / 停止 (Space)", () => Svc.Preview.TogglePlay()) { IconSize = 20 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0) };
        buttons.Children.AddRange(new Control[]
        {
            new IconButton("FirstPage", "先頭へ (Home)", () => EditorCommands.Seek(0)),
            new IconButton("ChevronLeft", "1 フレーム戻る (←)", () => EditorCommands.Step(-1)),
            _play,
            new IconButton("ChevronRight", "1 フレーム進む (→)", () => EditorCommands.Step(1)),
            new IconButton("LastPage", "末尾へ (End)", () => EditorCommands.Seek(_session.SceneLength - 1)),
        });
        _time.Classes.Add("mono"); _frameText.Classes.Add("mono"); _frameText.Classes.Add("secondary");
        var bar = new DockPanel { Height = 28 };
        DockPanel.SetDock(buttons, Dock.Left); DockPanel.SetDock(_time, Dock.Left); DockPanel.SetDock(_frameText, Dock.Right);
        bar.Children.Add(buttons); bar.Children.Add(_time); bar.Children.Add(_frameText); bar.Children.Add(_seek);
        var border = new Border { Child = bar, BorderThickness = new Thickness(0, 1, 0, 0) };
        border.Classes.Add("panel");
        SetDock(border, Dock.Bottom);
        Children.Add(border);
        Children.Add(Preview);

        _session.FrameChanged += UpdateTime;
        _session.ProjectChanged += UpdateTime;
        _session.SceneChanged += UpdateTime;
        Svc.Preview.PlayStateChanged += () => _play.Icon = Icons.Get(Svc.Preview.IsPlaying ? "Pause.Fill" : "Play.Fill");
        _seek.Changed += v => { Svc.Preview.Stop(); EditorCommands.Seek((int)Math.Round(v)); };
        UpdateTime();
    }

    private void UpdateTime()
    {
        var p = _session.Project;
        int length = _session.SceneLength;
        _time.Text = $"{p.FormatTime(_session.CurrentFrame)} / {p.FormatTime(_session.Scene.Length)}";
        _frameText.Text = $"F {_session.CurrentFrame}";
        _seek.Maximum = Math.Max(1, length - 1);
        _seek.Value = Math.Min(_session.CurrentFrame, _seek.Maximum);
    }
}
