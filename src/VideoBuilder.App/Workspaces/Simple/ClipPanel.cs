using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Workspaces.Simple;

/// <summary>
/// シンプル編集の右側: 選んでいるクリップ (または音楽) の設定。長さ・切り替え効果・動き・見た目・キャプション・音量など、
/// よく使うものだけを並べる。
/// </summary>
public sealed class ClipPanel : UserControl
{
    private readonly StoryboardEditor _editor = Svc.Storyboard;
    private readonly EditorSession _session = Svc.Session;
    private readonly StackPanel _body = new() { Margin = new Thickness(12), Spacing = 6 };
    private string? _shownId, _shownState;
    // この欄から変えている間は、作り直さない (入力中の欄が消えないように)
    private bool _applying;

    /// <summary>キャプションの入力欄 (ツールバーの「キャプション」から移る)。</summary>
    public TextBox? CaptionBox { get; private set; }

    public static readonly string[] Motions = { "なし", "ズームイン", "ズームアウト", "左へパン", "右へパン" };
    public static readonly string[] Looks = { "なし", "白黒", "セピア", "ぼかし", "明るく", "暗く", "鮮やか" };
    public static readonly string[] Placements = { "下", "中央", "上" };
    /// <summary>速度の表示 (1 倍は「標準」)。</summary>
    public static string SpeedLabel(double speed) => speed == 1 ? L.T("標準 (1 倍)") : $"{speed:0.##}x";

    public static string[] Transitions => new[] { "なし" }.Concat(EffectRegistry.TransitionTypes).ToArray();

    public ClipPanel()
    {
        var border = new Border { Child = new ScrollViewer { Content = _body }, BorderThickness = new Thickness(1, 0, 0, 0) };
        border.Classes.Add("panel");
        Content = border;
        _editor.SelectionChanged += Refresh;
        _session.ProjectChanged += () => { if (!_applying) Refresh(); };
        Refresh();
    }

    private object? Selected => (object?)_editor.SelectedClip ?? _editor.SelectedMusic;

    /// <summary>選んでいるものが変わったか、その中身がほかの操作 (取り消しなど) で変わったときだけ作り直す。</summary>
    public void Refresh()
    {
        var item = Selected;
        string? id = _editor.SelectedId;
        string state = item == null ? $"none:{_editor.Board?.Clips.Count}:{_editor.Layout.Length}" : JsonSerializer.Serialize(item, item.GetType());
        if (id == _shownId && state == _shownState) return;
        _shownId = id; _shownState = state;
        _body.Children.Clear();
        CaptionBox = null;
        switch (item)
        {
            case StoryClip c: BuildClip(c); break;
            case MusicClip m: BuildMusic(m); break;
            default: BuildNothing(); break;
        }
    }

    private void Apply(string description, Action<StoryClip> change, string? mergeKey = null)
    {
        if (_editor.SelectedClip is not { } c) return;
        _applying = true;
        try { _editor.UpdateClip(c.Id, description, change, mergeKey); }
        finally { _applying = false; }
        _shownState = JsonSerializer.Serialize(_editor.SelectedClip);
    }

    private void ApplyMusic(string description, Action<MusicClip> change, string? mergeKey = null)
    {
        if (_editor.SelectedMusic is not { } m) return;
        _applying = true;
        try { _editor.UpdateMusic(m.Id, description, change, mergeKey); }
        finally { _applying = false; }
        _shownState = JsonSerializer.Serialize(_editor.SelectedMusic);
    }

    // ---------------- 部品 ----------------

    private void Heading(string text, string? icon = null)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 0) };
        t.Classes.Add("heading");
        _body.Children.Add(t);
    }

    private void Note(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.Classes.Add("secondary");
        _body.Children.Add(t);
    }

    private void Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), MinHeight = 26 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        _body.Children.Add(grid);
    }

    private static NumberBox Seconds(double value, double min, double max, Action<double> commit)
    {
        var box = new NumberBox { Minimum = min, Maximum = max, Decimals = 2, Value = value, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        box.Committed += commit;
        return box;
    }

    private static ComboBox Combo(string[] items, int index, Action<int> changed)
    {
        var c = new ComboBox { ItemsSource = items, SelectedIndex = index, HorizontalAlignment = HorizontalAlignment.Stretch };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0 && c.SelectedIndex != index) { index = c.SelectedIndex; changed(c.SelectedIndex); } };
        return c;
    }

    private Control Volume(double value, Action<double, bool> changed)
    {
        var label = new TextBlock { Text = $"{value:0}%", Width = 44, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        var slider = new MiniSlider { Minimum = 0, Maximum = 200, Value = value, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        slider.Changed += v => { v = Math.Round(v); label.Text = $"{v:0}%"; changed(v, false); };
        slider.DragCompleted += () => changed(Math.Round(slider.Value), true);
        var dock = new DockPanel();
        DockPanel.SetDock(label, Dock.Right);
        dock.Children.Add(label); dock.Children.Add(slider);
        return dock;
    }

    private static Button Small(string text, Action click)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => click();
        return b;
    }

    private static ColorPicker ColorBox(string value, Action<string> changed)
    {
        var picker = new ColorPicker
        {
            Color = Color.TryParse(value, out var c) ? c : Colors.White, IsAlphaEnabled = false, IsAlphaVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        picker.ColorChanged += (_, e) => changed($"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}");
        return picker;
    }

    private TextBox TextInput(string value, string watermark, bool multiline, Action<string> changed)
    {
        var box = new TextBox { Text = value, Watermark = watermark, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 56 : 0 };
        box.TextChanged += (_, _) => { if (box.Text != value) { value = box.Text ?? ""; changed(value); } };
        return box;
    }

    // ---------------- 何も選んでいないとき ----------------

    private void BuildNothing()
    {
        Heading("ストーリーボード");
        var board = _editor.Board;
        int clips = board?.Clips.Count ?? 0, music = board?.Music.Count ?? 0;
        var layout = _editor.Layout;
        Note(L.Format("クリップ {0} 個・音楽 {1} 個", clips.ToString(), music.ToString()));
        Note(L.Format("長さ {0}", StoryboardStrip.FormatSeconds(_editor.Project.FrameToSeconds(layout.Length))));
        Note("クリップを選ぶと、ここで長さ・切り替え効果・キャプションなどを変えられます。");
        Note("ツールバーの「ビデオと写真」で素材を追加し、出来上がったら「動画の出力」で書き出します。");
    }

    // ---------------- クリップ ----------------

    private void BuildClip(StoryClip c)
    {
        string kind = c.Kind switch { StoryClipKind.Video => "動画", StoryClipKind.Photo => "写真", _ => "タイトル" };
        Heading(kind);
        if (c.UsesFile)
        {
            var name = L.Verbatim(Path.GetFileName(c.File));
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(name, L.Verbatim(c.File));
            _body.Children.Add(name);
        }

        // ---- 長さ ----
        Heading("長さ");
        if (c.Kind == StoryClipKind.Video)
        {
            double max = c.SourceSeconds > 0 ? c.SourceSeconds : 36000;
            Row("開始 (秒)", Seconds(c.TrimStart, 0, max, v => Apply("開始位置の設定", x =>
            {
                if (x.TrimEnd <= 0) x.TrimEnd = x.EffectiveTrimEnd;
                x.TrimStart = Math.Min(v, x.EffectiveTrimEnd - StoryClip.MinSeconds);
            })));
            Row("終了 (秒)", Seconds(c.EffectiveTrimEnd, 0, max, v => Apply("終了位置の設定", x => x.TrimEnd = Math.Max(v, x.TrimStart + StoryClip.MinSeconds))));
            _body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { Small("開始位置を現在位置に", () => _editor.TrimSelectedAt(_session.CurrentFrame, true)), Small("終了位置を現在位置に", () => _editor.TrimSelectedAt(_session.CurrentFrame, false)) },
            });
            if (c.SourceSeconds > 0) Note(L.Format("素材の長さ {0}", StoryboardStrip.FormatSeconds(c.SourceSeconds)));

            // 再生速度: 決まった倍率から選ぶ (取り込んだものなど、ほかの倍率ならそれも並べる)
            var speeds = StoryClip.Speeds.ToList();
            if (!speeds.Contains(c.EffectiveSpeed)) { speeds.Add(c.EffectiveSpeed); speeds.Sort(); }
            Row("速度", Combo(speeds.Select(SpeedLabel).ToArray(), speeds.IndexOf(c.EffectiveSpeed), i =>
            {
                double speed = speeds[i];
                Apply("速度の変更", x => x.Speed = speed);
            }));
            if (c.EffectiveSpeed != 1)
                Note(L.Format("長さ {0} (使う範囲 {1} を {2} 倍速)", StoryboardStrip.FormatSeconds(c.Seconds),
                    StoryboardStrip.FormatSeconds(c.EffectiveTrimEnd - c.TrimStart), c.EffectiveSpeed.ToString("0.##")));
        }
        else
        {
            Row("表示時間 (秒)", Seconds(c.Duration, StoryClip.MinSeconds, 3600, v => Apply("表示時間の変更", x => x.Duration = v)));
        }

        // ---- タイトル / キャプション ----
        if (c.Kind == StoryClipKind.Title)
        {
            Heading("タイトル");
            _body.Children.Add(TextInput(c.Text, "タイトルの文字", true, v => Apply("タイトルの文字", x => x.Text = v, "title:" + c.Id)));
            Row("文字色", ColorBox(c.TextColor, v => Apply("文字色の変更", x => x.TextColor = v, "tcolor:" + c.Id)));
            Row("背景色", ColorBox(c.Background, v => Apply("背景色の変更", x => x.Background = v, "bg:" + c.Id)));
        }
        else
        {
            Heading("キャプション");
            CaptionBox = TextInput(c.Caption, "映像に重ねる文字 (空なら出さない)", true, v => Apply("キャプションの変更", x => x.Caption = v, "caption:" + c.Id));
            _body.Children.Add(CaptionBox);
            Row("位置", Combo(Placements, (int)c.CaptionPlacement, i => Apply("キャプションの位置", x => x.CaptionPlacement = (CaptionPlacement)i)));
            Row("文字色", ColorBox(c.TextColor, v => Apply("文字色の変更", x => x.TextColor = v, "tcolor:" + c.Id)));
        }

        // ---- 切り替え効果 ----
        Heading("切り替え効果");
        Row("種類", Combo(Transitions, Math.Clamp(c.Transition, 0, EffectRegistry.TransitionTypes.Length), i => Apply("切り替え効果の変更", x => x.Transition = i)));
        Row("長さ (秒)", Seconds(c.TransitionSeconds, 0.1, 10, v => Apply("切り替え効果の長さ", x => x.TransitionSeconds = v)));
        Note(_editor.Board?.Clips.IndexOf(c) == 0 ? "先頭のクリップでは、黒い画面から切り替わります。" : "前のクリップから、このクリップへ切り替わるときの効果です。");
        if (_editor.Board is { Clips.Count: > 1 })
            _body.Children.Add(Small("すべてのクリップに同じ効果を付ける", () => Apply("切り替え効果をすべてに付ける", _ =>
            {
                foreach (var other in _editor.Board!.Clips) { other.Transition = c.Transition; other.TransitionSeconds = c.TransitionSeconds; }
            })));

        // ---- 効果 ----
        Heading("効果");
        if (c.Kind != StoryClipKind.Title)
            Row("動き", Combo(Motions, (int)c.Motion, i => Apply("動きの変更", x => x.Motion = (ClipMotion)i)));
        Row("見た目", Combo(Looks, (int)c.Look, i => Apply("見た目の変更", x => x.Look = (ClipLook)i)));
        if (c.Kind != StoryClipKind.Title)
            Row("回転", new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    Small("左に 90°", () => Apply("回転", x => x.Rotation = (x.Rotation + 270) % 360)),
                    Small("右に 90°", () => Apply("回転", x => x.Rotation = (x.Rotation + 90) % 360)),
                },
            });

        // ---- 音声 ----
        if (c.Kind == StoryClipKind.Video)
        {
            Heading("音声");
            if (!c.HasAudio) Note("この動画には音声がありません。");
            else
            {
                Row("音量", Volume(c.Volume, (v, done) => Apply("音量の変更", x => x.Volume = v, "volume:" + c.Id)));
                if (c.EffectiveSpeed != 1) Note("速度を変えると、音の高さも変わります。");
                var mute = new CheckBox { Content = "音を消す", IsChecked = c.Muted };
                mute.IsCheckedChanged += (_, _) => Apply("音を消す", x => x.Muted = mute.IsChecked == true);
                _body.Children.Add(mute);
            }
        }
    }

    // ---------------- 音楽 ----------------

    private void BuildMusic(MusicClip m)
    {
        Heading("音楽");
        var name = L.Verbatim(Path.GetFileName(m.File));
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(name, L.Verbatim(m.File));
        _body.Children.Add(name);

        Row("鳴らし始め (秒)", Seconds(m.StartSeconds, 0, 36000, v => ApplyMusic("音楽の移動", x => x.StartSeconds = v)));
        _body.Children.Add(Small("現在位置から鳴らす", () =>
        {
            double at = Math.Round(_editor.Project.FrameToSeconds(_session.CurrentFrame), 3);
            ApplyMusic("音楽の移動", x => x.StartSeconds = at);
        }));

        Heading("使う範囲");
        double max = m.SourceSeconds > 0 ? m.SourceSeconds : 36000;
        Row("開始 (秒)", Seconds(m.TrimStart, 0, max, v => ApplyMusic("音楽の範囲", x =>
        {
            if (x.TrimEnd <= 0) x.TrimEnd = x.EffectiveTrimEnd;
            x.TrimStart = Math.Min(v, x.EffectiveTrimEnd - StoryClip.MinSeconds);
        })));
        Row("終了 (秒)", Seconds(m.EffectiveTrimEnd, 0, max, v => ApplyMusic("音楽の範囲", x => x.TrimEnd = Math.Max(v, x.TrimStart + StoryClip.MinSeconds))));

        Heading("音量");
        Row("音量", Volume(m.Volume, (v, done) => ApplyMusic("音量の変更", x => x.Volume = v, "mvolume:" + m.Id)));
        Row("フェードイン (秒)", Seconds(m.FadeIn, 0, 30, v => ApplyMusic("フェードの変更", x => x.FadeIn = v)));
        Row("フェードアウト (秒)", Seconds(m.FadeOut, 0, 30, v => ApplyMusic("フェードの変更", x => x.FadeOut = v)));

        var fit = new CheckBox { Content = "映像の終わりで止める", IsChecked = _editor.Board?.FitMusicToVideo ?? true };
        fit.IsCheckedChanged += (_, _) =>
        {
            _applying = true;
            try { _editor.Change("音楽の長さの設定", b => b.FitMusicToVideo = fit.IsChecked == true); }
            finally { _applying = false; }
        };
        _body.Children.Add(fit);
    }
}
