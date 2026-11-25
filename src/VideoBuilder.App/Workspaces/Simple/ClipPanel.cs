using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Rendering;

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
    /// <summary>タイトルの動き (<see cref="ClipMotion.None"/> と <see cref="ClipMotion.ScrollUp"/>)。</summary>
    public static readonly string[] TitleMotions = { "なし", "下から上へ流す (エンドロール)" };
    /// <summary>文字の大きさ (%)。</summary>
    public static readonly double[] TextScales = { 75, 100, 130, 160 };
    public static readonly string[] TextScaleNames = { "小さく", "標準", "大きく", "とても大きく" };
    private static IReadOnlyList<string>? _fonts;
    private static IReadOnlyList<string> Fonts => _fonts ??= new[] { "(既定のフォント)" }.Concat(Generators.FontFamilies).ToList();
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

    /// <summary>欄に出すもの: クリップ、音楽、いくつも選んでいればクリップの一覧。</summary>
    private object? Selected => _editor.HasMultipleSelection
        ? _editor.SelectedClipIds.Select(id => _editor.Board!.FindClip(id)!).ToList()
        : (object?)_editor.SelectedClip ?? _editor.SelectedMusic;

    /// <summary>選んでいるものが変わったか、その中身がほかの操作 (取り消しなど) で変わったときだけ作り直す。</summary>
    public void Refresh()
    {
        var item = Selected;
        string? id = _editor.SelectedId;
        string state = StateOf(item);
        if (id == _shownId && state == _shownState) return;
        _shownId = id; _shownState = state;
        _body.Children.Clear();
        CaptionBox = null;
        switch (item)
        {
            case List<StoryClip> list: BuildMulti(list); break;
            case StoryClip c: BuildClip(c); break;
            case MusicClip m: BuildMusic(m); break;
            default: BuildNothing(); break;
        }
    }

    /// <summary>欄の中身を決めるもの。これが変わったときだけ作り直す。</summary>
    private string StateOf(object? item)
    {
        var board = _editor.Board;
        string common = $"{board?.Clips.Count}:{board?.FitMusicToVideo}:{board?.EndFadeSeconds}:{board?.Background}";
        return item == null ? $"none:{common}:{_editor.Layout.Length}" : common + ":" + JsonSerializer.Serialize(item, item.GetType());
    }

    private void Apply(string description, Action<StoryClip> change, string? mergeKey = null)
    {
        if (_editor.SelectedClip is not { } c) return;
        _applying = true;
        try { _editor.UpdateClip(c.Id, description, change, mergeKey); }
        finally { _applying = false; }
        _shownState = StateOf(Selected);
    }

    /// <summary>選んでいるすべてのクリップの設定を変える。</summary>
    private void ApplySelected(string description, Action<StoryClip> change)
    {
        _applying = true;
        try { _editor.UpdateSelectedClips(description, change); }
        finally { _applying = false; }
        _shownState = StateOf(Selected);
    }

    /// <summary>ストーリーボード全体の設定を変える。</summary>
    private void ApplyBoard(string description, Action<Storyboard> change, string? mergeKey = null)
    {
        _applying = true;
        try { _editor.Change(description, change, mergeKey); }
        finally { _applying = false; }
        _shownState = StateOf(Selected);
    }

    private void ApplyMusic(string description, Action<MusicClip> change, string? mergeKey = null)
    {
        if (_editor.SelectedMusic is not { } m) return;
        _applying = true;
        try { _editor.UpdateMusic(m.Id, description, change, mergeKey); }
        finally { _applying = false; }
        _shownState = StateOf(Selected);
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

    private static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    private static ComboBox FontCombo(string font, Action<string> changed)
    {
        int index = font.Length == 0 ? 0 : Fonts.ToList().IndexOf(font);
        if (index < 0) index = 0;
        var c = Combo(Fonts.ToArray(), index, i => changed(i == 0 ? "" : Fonts[i]));
        c.MaxDropDownHeight = 360;
        return c;
    }

    private static ComboBox ScaleCombo(double scale, Action<double> changed)
    {
        int index = Array.IndexOf(TextScales, scale);
        var names = TextScaleNames.ToList();
        var values = TextScales.ToList();
        if (index < 0) { values.Add(scale); names.Add($"{scale:0}%"); index = values.Count - 1; }
        return Combo(names.ToArray(), index, i => changed(values[i]));
    }

    private TextBox TextInput(string value, string watermark, bool multiline, Action<string> changed)
    {
        var box = new TextBox { Text = value, Watermark = watermark, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 56 : 0 };
        box.TextChanged += (_, _) => { if (box.Text != value) { value = box.Text ?? ""; changed(value); } };
        return box;
    }

    private void TrimAtPlayhead(bool start)
    {
        if (!_editor.TrimSelectedAt(_session.CurrentFrame, start))
            _ = EditorCommands.ShowErrorAsync("現在位置を、選んでいるクリップの中に合わせてください。");
    }

    /// <summary>「すべてに同じ…を付ける」ボタン。付けられるほかのクリップがなければ出さない。</summary>
    private void ApplyAllButton(StoryClip c, string text, string description, Action<StoryClip, StoryClip> copy, Func<StoryClip, bool>? target = null)
    {
        if (_editor.Board is not { } board || !board.Clips.Any(x => x.Id != c.Id && (target?.Invoke(x) ?? true))) return;
        _body.Children.Add(Small(text, () =>
        {
            _applying = true;
            try { _editor.ApplyToAll(c.Id, description, copy, target); }
            finally { _applying = false; }
            _shownState = StateOf(Selected);
        }));
    }

    /// <summary>「映像の終わりで止める」。ストーリーボード全体の設定 (すべての音楽に効く)。</summary>
    private void FitMusicBox()
    {
        var fit = new CheckBox { Content = "映像の終わりで音楽を止める", IsChecked = _editor.Board?.FitMusicToVideo ?? true };
        fit.IsCheckedChanged += (_, _) =>
        {
            _applying = true;
            try { _editor.Change("音楽の長さの設定", b => b.FitMusicToVideo = fit.IsChecked == true); }
            finally { _applying = false; }
            _shownState = StateOf(Selected);
        };
        _body.Children.Add(fit);
        Note("すべての音楽に共通の設定です。");
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
        if (clips > 0)
        {
            Heading("作品全体");
            var endFade = board!.EndFadeSeconds;
            var fade = Check("最後に黒い画面へフェードアウト", endFade > 0, on => ApplyBoard("終わりのフェードアウト", b => b.EndFadeSeconds = on ? (b.EndFadeSeconds > 0 ? b.EndFadeSeconds : 1.5) : 0));
            _body.Children.Add(fade);
            if (endFade > 0) Row("長さ (秒)", Seconds(endFade, 0.1, 10, v => ApplyBoard("終わりのフェードアウト", b => b.EndFadeSeconds = v)));
            bool colored = !string.IsNullOrWhiteSpace(board.Background);
            Row("余白の色", new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    ColorBox(colored ? board.Background : "#000000", v => ApplyBoard("余白の色", b => b.Background = v == "#000000" ? "" : v, "bgcolor")),
                    Small("黒に戻す", () => ApplyBoard("余白の色", b => b.Background = "")),
                },
            });
            Note("写真・動画を画面に収めたときにできる、上下・左右の帯の色です。");
        }
        if (music > 0)
        {
            Heading("音楽");
            FitMusicBox();
        }
    }

    // ---------------- いくつも選んでいるとき ----------------

    private void BuildMulti(List<StoryClip> clips)
    {
        Heading(L.Format("{0} 個のクリップを選んでいます", clips.Count.ToString()));
        Note("ここで変えた設定は、選んでいるすべてのクリップに付きます。Ctrl を押しながらクリックで選択に加える / 外す、Shift で範囲を選べます。");
        // 全部が同じ値なら、それを選んでおく (違えば空欄)
        int Common(Func<StoryClip, int> value) => clips.Select(value).Distinct().Count() == 1 ? value(clips[0]) : -1;

        Heading("切り替え効果");
        Row("種類", Combo(Transitions, Common(c => Math.Clamp(c.Transition, 0, EffectRegistry.TransitionTypes.Length)), i => ApplySelected("切り替え効果の変更", x => x.Transition = i)));
        Row("長さ (秒)", Seconds(clips[0].TransitionSeconds, 0.1, 10, v => ApplySelected("切り替え効果の長さ", x => x.TransitionSeconds = v)));

        Heading("効果");
        var media = clips.Where(c => c.UsesFile).ToList();
        if (media.Count > 0)
        {
            Row("動き", Combo(Motions, media.Select(c => (int)c.Motion).Distinct().Count() == 1 && media[0].Motion != ClipMotion.ScrollUp ? (int)media[0].Motion : -1,
                i => ApplySelected("動きの変更", x => { if (x.UsesFile) x.Motion = (ClipMotion)i; })));
        }
        Row("見た目", Combo(Looks, Common(c => (int)c.Look), i => ApplySelected("見た目の変更", x => x.Look = (ClipLook)i)));
        if (media.Count > 0)
            _body.Children.Add(Check("画面いっぱいに広げる (はみ出た所は切れる)", media.All(c => c.Fill), on => ApplySelected("画面いっぱいに広げる", x => x.Fill = on)));

        var timed = clips.Where(c => c.Kind != StoryClipKind.Video).ToList();
        if (timed.Count > 0)
        {
            Heading("長さ");
            Row("表示時間 (秒)", Seconds(timed[0].Duration, StoryClip.MinSeconds, 3600, v => ApplySelected("表示時間の変更", x => { if (x.Kind != StoryClipKind.Video) x.Duration = v; })));
            Note("写真・タイトルの表示時間です (動画は変わりません)。");
        }

        Heading("操作");
        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                Small("前へ移動", () => _editor.MoveSelected(-1)),
                Small("後ろへ移動", () => _editor.MoveSelected(1)),
                Small("削除", _editor.RemoveSelected),
            },
        });
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
                Children = { Small("開始位置を現在位置に", () => TrimAtPlayhead(true)), Small("終了位置を現在位置に", () => TrimAtPlayhead(false)) },
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
            bool photo = c.Kind == StoryClipKind.Photo;
            ApplyAllButton(c, photo ? "すべての写真を同じ表示時間にする" : "すべてのタイトルを同じ表示時間にする", "表示時間をすべてに付ける",
                (src, x) => x.Duration = src.Duration, x => x.Kind == c.Kind);
        }

        // ---- タイトル / キャプション ----
        if (c.Kind == StoryClipKind.Title)
        {
            Heading("タイトル");
            _body.Children.Add(TextInput(c.Text, "タイトルの文字", true, v => Apply("タイトルの文字", x => x.Text = v, "title:" + c.Id)));
            Row("文字色", ColorBox(c.TextColor, v => Apply("文字色の変更", x => x.TextColor = v, "tcolor:" + c.Id)));
            Row("背景色", ColorBox(c.Background, v => Apply("背景色の変更", x => x.Background = v, "bg:" + c.Id)));
            Row("大きさ", ScaleCombo(c.TextScale, v => Apply("文字の大きさ", x => x.TextScale = v)));
            Row("フォント", FontCombo(c.Font, v => Apply("フォントの変更", x => x.Font = v)));
            Row("動き", Combo(TitleMotions, c.Motion == ClipMotion.ScrollUp ? 1 : 0, i => Apply("動きの変更", x => x.Motion = i == 1 ? ClipMotion.ScrollUp : ClipMotion.None)));
        }
        else
        {
            Heading("キャプション");
            CaptionBox = TextInput(c.Caption, "映像に重ねる文字 (空なら出さない)", true, v => Apply("キャプションの変更", x => x.Caption = v, "caption:" + c.Id));
            _body.Children.Add(CaptionBox);
            Row("位置", Combo(Placements, (int)c.CaptionPlacement, i => Apply("キャプションの位置", x => x.CaptionPlacement = (CaptionPlacement)i)));
            Row("文字色", ColorBox(c.TextColor, v => Apply("文字色の変更", x => x.TextColor = v, "tcolor:" + c.Id)));
            Row("大きさ", ScaleCombo(c.TextScale, v => Apply("文字の大きさ", x => x.TextScale = v)));
            Row("フォント", FontCombo(c.Font, v => Apply("フォントの変更", x => x.Font = v)));
            _body.Children.Add(Check("後ろに帯を敷く (読みやすくする)", c.CaptionBackground, on => Apply("キャプションの帯", x => x.CaptionBackground = on)));
            Row("出し始め (秒)", Seconds(c.CaptionStart, 0, 3600, v => Apply("キャプションの時間", x => x.CaptionStart = v)));
            Row("出す長さ (秒)", Seconds(c.CaptionSeconds, 0, 3600, v => Apply("キャプションの時間", x => x.CaptionSeconds = v)));
            Note("出し始めはクリップの先頭からの秒です。出す長さが 0 なら、クリップの終わりまで出します。");
        }

        // ---- 切り替え効果 ----
        Heading("切り替え効果");
        Row("種類", Combo(Transitions, Math.Clamp(c.Transition, 0, EffectRegistry.TransitionTypes.Length), i => Apply("切り替え効果の変更", x => x.Transition = i)));
        Row("長さ (秒)", Seconds(c.TransitionSeconds, 0.1, 10, v => Apply("切り替え効果の長さ", x => x.TransitionSeconds = v)));
        Note(_editor.Board?.Clips.IndexOf(c) == 0 ? "先頭のクリップでは、黒い画面から切り替わります。" : "前のクリップから、このクリップへ切り替わるときの効果です。");
        ApplyAllButton(c, "すべてのクリップに同じ効果を付ける", "切り替え効果をすべてに付ける",
            (src, x) => { x.Transition = src.Transition; x.TransitionSeconds = src.TransitionSeconds; });

        // ---- 効果 ----
        Heading("効果");
        if (c.Kind != StoryClipKind.Title)
            Row("動き", Combo(Motions, (int)c.Motion, i => Apply("動きの変更", x => x.Motion = (ClipMotion)i)));
        if (c.Kind != StoryClipKind.Title)
            ApplyAllButton(c, "すべての動画・写真に同じ動きを付ける", "動きをすべてに付ける", (src, x) => x.Motion = src.Motion, x => x.UsesFile);
        Row("見た目", Combo(Looks, (int)c.Look, i => Apply("見た目の変更", x => x.Look = (ClipLook)i)));
        ApplyAllButton(c, "すべてのクリップに同じ見た目を付ける", "見た目をすべてに付ける", (src, x) => x.Look = src.Look);
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
        if (c.Kind != StoryClipKind.Title)
            _body.Children.Add(Check("画面いっぱいに広げる (はみ出た所は切れる)", c.Fill, on => Apply("画面いっぱいに広げる", x => x.Fill = on)));

        // ---- 音声 ----
        if (c.Kind == StoryClipKind.Video)
        {
            Heading("音声");
            if (!c.HasAudio) Note("この動画には音声がありません。");
            else
            {
                Row("音量", Volume(c.Volume, (v, done) => Apply("音量の変更", x => x.Volume = v, "volume:" + c.Id)));
                if (c.EffectiveSpeed != 1)
                    _body.Children.Add(Check("音の高さを変えない", c.KeepPitch, on => Apply("音の高さを変えない", x => x.KeepPitch = on)));
                if (c.EffectiveSpeed != 1 && !c.KeepPitch) Note("速度を変えると、音の高さも変わります。");
                if (c.KeepPitch && c.EffectiveSpeed is > 4 or < 0.25) Note("この速度では、音の高さを元どおりにはできません (近づけるだけ)。");
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

        _body.Children.Add(Check("繰り返して、映像の終わりまで鳴らす", m.Loop, on => ApplyMusic("音楽の繰り返し", x => x.Loop = on)));
        _body.Children.Add(Check("動画の音声が鳴っている間は小さくする", m.Duck, on => ApplyMusic("音楽を自動で小さくする", x => x.Duck = on)));

        Heading("すべての音楽");
        FitMusicBox();
    }
}
