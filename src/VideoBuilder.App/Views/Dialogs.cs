using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Media;

namespace VideoBuilder.App.Views;

/// <summary>ダイアログ共通の土台: 本文 + 下端のボタン列。</summary>
public class DialogBase : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(16), Spacing = 8 };
    /// <summary>下端のボタン列と本文を並べる入れ物。本文を別の作りにしたいダイアログは、Body を差し替える。</summary>
    protected readonly DockPanel Root;
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8) };

    public DialogBase(string title, double width)
    {
        Title = title;
        Icon = VideoBuilder.App.Shell.AppIcon.Get();
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var footer = new Border { Child = Buttons, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.Classes.Add("panel");
        Root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        Root.Children.Add(footer);
        Root.Children.Add(Body);
        Content = Root;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    protected Button AddButton(string text, Action click, bool accent = false, string? icon = null)
    {
        Button b = icon != null ? new IconButton { Icon = Icons.Get(icon), Text = text } : new Button { Content = text };
        b.MinWidth = 88;
        if (accent) { b.Classes.Add("accent"); b.IsDefault = true; }
        b.Click += (_, _) => click();
        Buttons.Children.Add(b);
        return b;
    }

    protected static Control Row(string label, Control control, double labelWidth = 120)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*"), MinHeight = 26 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    protected static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 4, 0, 0) };
        t.Classes.Add("heading");
        return t;
    }
}

public sealed class MessageDialog : DialogBase
{
    public string? Result { get; private set; }

    public MessageDialog(string title, string message, params (string Text, bool Accent)[] buttons) : base(title, 420)
    {
        Body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        foreach (var (text, accent) in buttons)
            AddButton(text, () => { Result = text; Close(); }, accent);
    }

    public static async Task<string?> Show(Window owner, string title, string message, params (string Text, bool Accent)[] buttons)
    {
        if (buttons.Length == 0) buttons = new[] { ("OK", true) };
        var d = new MessageDialog(title, message, buttons);
        await d.ShowDialog(owner);
        return d.Result;
    }
}

public sealed class ProjectSettingsDialog : DialogBase
{
    private static readonly (string Name, int W, int H)[] Presets =
    {
        ("1920 x 1080 (フル HD)", 1920, 1080), ("1280 x 720 (HD)", 1280, 720), ("3840 x 2160 (4K)", 3840, 2160),
        ("1080 x 1920 (縦長)", 1080, 1920), ("1080 x 1080 (正方形)", 1080, 1080),
    };
    private static readonly string[] Rates = { "24", "24000/1001", "25", "30", "30000/1001", "50", "60", "60000/1001" };

    public ProjectSettings? Result { get; private set; }

    public ProjectSettingsDialog(ProjectSettings current, bool isNew) : base(isNew ? "新規プロジェクト" : "プロジェクト設定", 400)
    {
        var width = new NumberBox { Minimum = 16, Maximum = 8192, Decimals = 0, Value = current.Width, Width = 80 };
        var height = new NumberBox { Minimum = 16, Maximum = 8192, Decimals = 0, Value = current.Height, Width = 80 };
        var preset = new ComboBox { ItemsSource = Presets.Select(p => p.Name).ToList(), PlaceholderText = "プリセットから選ぶ", HorizontalAlignment = HorizontalAlignment.Stretch };
        preset.SelectedIndex = Array.FindIndex(Presets, p => p.W == current.Width && p.H == current.Height);
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedIndex < 0) return;
            width.Value = Presets[preset.SelectedIndex].W; height.Value = Presets[preset.SelectedIndex].H;
        };
        var rate = new ComboBox { ItemsSource = Rates.Select(RateLabel).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
        int ri = Array.IndexOf(Rates, current.FrameRate);
        rate.SelectedIndex = ri >= 0 ? ri : 3;
        var sample = new ComboBox { ItemsSource = new[] { "44100 Hz", "48000 Hz" }, SelectedIndex = current.SampleRate == 44100 ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };

        Body.Children.Add(Row("プリセット", preset));
        Body.Children.Add(Row("画像サイズ", new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { width, new TextBlock { Text = "x", VerticalAlignment = VerticalAlignment.Center }, height },
        }));
        Body.Children.Add(Row("フレームレート", rate));
        Body.Children.Add(Row("音声サンプルレート", sample));
        if (!isNew)
        {
            var note = new TextBlock { Text = "フレームレートを変えても、オブジェクトのフレーム位置は変わりません (再生時間が変わります)。", TextWrapping = TextWrapping.Wrap };
            note.Classes.Add("secondary");
            Body.Children.Add(note);
        }

        AddButton("キャンセル", Close);
        AddButton(isNew ? "作成" : "OK", () =>
        {
            Result = new ProjectSettings
            {
                Width = (int)width.Value, Height = (int)height.Value,
                FrameRate = Rates[Math.Max(0, rate.SelectedIndex)], SampleRate = sample.SelectedIndex == 0 ? 44100 : 48000,
            };
            Close();
        }, accent: true);
    }

    private static string RateLabel(string r) => r.Contains('/') ? $"{Rational.Parse(r).Value:0.###} fps ({r})" : r + " fps";
}

public sealed class PreferencesDialog : DialogBase
{
    private Action? _showDecode;
    private void ShowDecodeLater() => _showDecode?.Invoke();

    // 項目が多く縦に長くなるので、左に分類の一覧、右にその分類の項目を出す。最後に開いた分類を覚えておく (起動中だけ)
    private static int _lastPage;
    private readonly ListBox _categories = new() { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent };
    private readonly List<string> _categoryNames = new();
    private readonly List<Control> _pages = new();
    private readonly ContentControl _pageHost = new();

    /// <summary>分類を 1 つ足して、その項目を入れる入れ物を返す。</summary>
    private StackPanel AddPage(string name)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 16), Spacing = 8 };
        var title = new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(title);
        _categoryNames.Add(name);
        _pages.Add(new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return panel;
    }

    private void ShowPage(int index)
    {
        _lastPage = index;
        _pageHost.Content = _pages[index];
        if (_categories.SelectedIndex != index) _categories.SelectedIndex = index;
    }

    /// <summary>本文を「分類の一覧 + 項目」の 2 段組みに差し替える。</summary>
    private void BuildLayout()
    {
        SizeToContent = SizeToContent.Manual;
        Height = 520;
        MinWidth = 600;
        MinHeight = 360;
        CanResize = true;
        Root.Children.Remove(Body);
        _categories.ItemsSource = _categoryNames;
        _categories.ContainerPrepared += (_, e) => { if (e.Container is ListBoxItem item) item.Padding = new Thickness(16, 8); };
        _categories.SelectionChanged += (_, _) => { if (_categories.SelectedIndex >= 0) ShowPage(_categories.SelectedIndex); };
        var side = new Border { Child = _categories, Width = 168, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 8) };
        side.Classes.Add("panel");
        var body = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        body.Children.Add(side);
        body.Children.Add(_pageHost);
        Root.Children.Add(body);
    }

    /// <summary>「使う GPU」の一覧を作る。一覧は描画と同じ方法 (EGL のデバイス列挙) で調べる。</summary>
    private static async Task FillGpuListAsync(ComboBox combo, TextBlock note, Action changed)
    {
        var s = Svc.Settings;
        IReadOnlyList<VideoBuilder.Rendering.GpuAdapter> adapters;
        string? error;
        try
        {
            (adapters, error) = await Task.Run(() => (VideoBuilder.Rendering.GpuContext.Adapters(), VideoBuilder.Rendering.GpuContext.AdaptersError));
        }
        catch (Exception ex) { adapters = Array.Empty<VideoBuilder.Rendering.GpuAdapter>(); error = ex.Message; }

        var auto = VideoBuilder.Rendering.GpuContext.ChooseAdapter(adapters, null);
        var ids = new List<string> { "" };
        // GPU の名前はドライバが返すものなので訳さない (「自動」などアプリの言葉だけ訳す)
        var labels = new List<string> { auto != null && !auto.IsSoftware ? L.Format("自動 (外付けの GPU を優先: {0})", auto.Name) : L.T("自動 (外付けの GPU を優先)") };
        foreach (var a in adapters)
        {
            ids.Add(a.Id);
            labels.Add(a.Label + (a.IsSoftware ? " " + L.T("(ソフトウェア)") : ""));
        }
        if (s.GpuAdapter.Length > 0 && !ids.Contains(s.GpuAdapter))
        {
            // 前に選んだ GPU が今は見つからない (取り外した・ドライバが変わった)。自動で動いていることを示す
            ids.Add(s.GpuAdapter);
            labels.Add(L.Format("{0} (見つかりません。自動で選んでいます)", s.GpuAdapter.Split('|')[0]));
        }
        combo.ItemTemplate = Menus.VerbatimItems();
        combo.ItemsSource = labels;
        combo.SelectedIndex = Math.Max(0, ids.IndexOf(s.GpuAdapter));
        combo.IsEnabled = adapters.Count > 0;
        note.Text = adapters.Count == 0
            ? L.T("GPU を選べません (この環境では、デスクトップの GPU で描きます)。") + (error != null ? "\n" + error : "")
            : L.T("CPU 内蔵の GPU と外付けの GPU があるときに選びます。描画・出力・動画のデコードに使います。")
              // Windows: 画面の描画の GPU は起動のときに決まる (プレビューを読み戻さずに出すには、同じ GPU である必要がある)
              + (OperatingSystem.IsWindows() ? "\n" + L.T("画面の表示にも同じ GPU を使います。変えたときは、アプリを起動し直すと画面の表示も切り替わります。") : "");
        note.SetValue(L.VerbatimProperty, true);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU")))
        {
            combo.IsEnabled = false;
            note.Text += "\n" + L.T("環境変数 VIDEO_BUILDER_GPU で指定されているため、ここでは変えられません。");
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || ids[combo.SelectedIndex] == s.GpuAdapter) return;
            s.GpuAdapter = ids[combo.SelectedIndex];
            s.Save();
            Svc.ApplyGpuAdapter();
            // 描画のエンジンと、動画のデコードのデバイスを作り直す
            VideoBuilder.Media.HardwareDecoding.ResetDevices();
            Svc.Preview.ReloadBackend();
            changed();
        };
    }

    public PreferencesDialog() : base("環境設定", 720)
    {
        var s = Svc.Settings;
        ComboBox Combo(string[] items, int index, Action<int> changed)
        {
            var c = new ComboBox { ItemsSource = items, SelectedIndex = index, HorizontalAlignment = HorizontalAlignment.Stretch };
            c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { changed(c.SelectedIndex); s.Save(); } };
            return c;
        }
        StackPanel page;

        page = AddPage("外観");
        // 言語名はどの表示言語でも読めるよう、それぞれの言語で書く
        page.Children.Add(Row("言語", Combo(new[] { "自動", "日本語", "English" }, (int)s.Language, i => s.Language = (Language)i)));
        var langNote = new TextBlock { Text = "言語の変更は、次回起動時に反映されます。", TextWrapping = TextWrapping.Wrap };
        langNote.Classes.Add("secondary");
        page.Children.Add(langNote);
        page.Children.Add(Row("編集モード", Combo(new[] { "詳細編集 (AviUtl 風)", "シンプル編集 (ムービーメーカー風)" }, (int)s.EditorMode, i =>
        {
            EditorCommands.SetEditorMode((EditorMode)i);
        })));
        var modeNote = new TextBlock { Text = "シンプル編集は、動画・写真を並べて切り替え効果・キャプション・音楽を付けるだけの簡単な編集画面です。", TextWrapping = TextWrapping.Wrap };
        modeNote.Classes.Add("secondary");
        page.Children.Add(modeNote);
        page.Children.Add(Row("テーマ", Combo(new[] { "システムに従う", "ライト", "ダーク" }, (int)s.Theme, i => { s.Theme = (ThemeMode)i; Svc.Theme.Apply(); })));

        var custom = new ColorPicker { Color = Color.TryParse(s.CustomAccent, out var cc) ? cc : Colors.DodgerBlue, IsAlphaEnabled = false, IsAlphaVisible = false, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = s.Accent == AccentMode.Custom };
        custom.ColorChanged += (_, e) =>
        {
            s.CustomAccent = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
            s.Save();
            if (s.Accent == AccentMode.Custom) Svc.Theme.Apply();
        };
        page.Children.Add(Row("アクセントカラー", Combo(new[] { "システムに従う", "アプリ既定 (青)", "カスタム" }, (int)s.Accent, i =>
        {
            s.Accent = (AccentMode)i; custom.IsEnabled = s.Accent == AccentMode.Custom; Svc.Theme.Apply();
        })));
        page.Children.Add(Row("カスタム色", custom));
        page.Children.Add(Row("ウィンドウ", Combo(new[] { "自動 (1 つのウィンドウにまとめる)", "ウィンドウを分ける", "1 つのウィンドウにまとめる" }, (int)s.WindowMode, i =>
        {
            s.WindowMode = (WindowMode)i; EditorCommands.ApplyWindowMode();
        })));
        page.Children.Add(Row("レイヤーの高さ", Combo(new[] { "小 (20)", "中 (24)", "大 (32)" }, s.LayerHeight <= 20 ? 0 : s.LayerHeight <= 24 ? 1 : 2, i =>
        {
            s.LayerHeight = new[] { 20, 24, 32 }[i]; Svc.Session.NotifyChanged();
        })));

        page = AddPage("描画");
        var backend = new TextBlock { TextWrapping = TextWrapping.Wrap };
        backend.Classes.Add("secondary");
        void ShowBackend() => backend.Text = "現在: " + Svc.Preview.BackendDescription;
        Svc.Preview.BackendChanged += ShowBackend;
        Closed += (_, _) => Svc.Preview.BackendChanged -= ShowBackend;
        page.Children.Add(Row("描画方式", Combo(new[] { "自動 (GPU があれば使う)", "GPU", "CPU" }, (int)s.RenderBackend, i =>
        {
            s.RenderBackend = (RenderBackend)i; Svc.Preview.ReloadBackend();
        })));
        ShowBackend();
        page.Children.Add(backend);

        // 使う GPU: CPU 内蔵と外付けの GPU があるとき、どちらで描くか (調べるのに少し時間がかかるので、裏で一覧を作る)
        var gpu = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false, PlaceholderText = L.T("GPU を調べています...") };
        var gpuNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
        gpuNote.Classes.Add("secondary");
        page.Children.Add(Row("使う GPU", gpu));
        page.Children.Add(gpuNote);
        _ = FillGpuListAsync(gpu, gpuNote, ShowDecodeLater);
        if (OperatingSystem.IsLinux())
        {
            // ハイブリッドの GPU の PC: 画面は CPU 内蔵の GPU、描画は外付けの GPU だと、プレビューを毎フレーム読み戻して送り直す (設計書 22.57)
            page.Children.Add(Row("プレビューを描く GPU", Combo(new[] { "使う GPU と同じ", "画面の表示と同じ GPU (読み戻さない)" }, s.PreviewOnScreenGpu ? 1 : 0, i =>
            {
                s.PreviewOnScreenGpu = i == 1; Svc.Preview.ReloadBackend();
            })));
            var previewGpuNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "画面の表示と描画で GPU が違うとき (CPU 内蔵の GPU で画面を出すノート PC など) に効きます。画面の表示と同じ GPU で描くと、プレビューを読み戻して送り直す手間がなくなりますが、外付けの GPU より遅いことがあります。出力と動画のデコードは「使う GPU」で行います。" };
            previewGpuNote.Classes.Add("secondary");
            page.Children.Add(previewGpuNote);
        }
        var decodeInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "確認中..." };
        decodeInfo.Classes.Add("secondary");
        async void ShowDecode()
        {
            // デバイスを開いて確かめるので、少し時間がかかることがある
            var text = await Task.Run(VideoBuilder.Media.HardwareDecoding.Describe);
            decodeInfo.Text = text;
        }
        page.Children.Add(Row("動画のデコード", Combo(new[] { "自動 (GPU があれば使う)", "ソフトウェアのみ" }, s.HardwareDecode == VideoBuilder.Media.HardwareDecodeMode.Auto ? 0 : 1, i =>
        {
            s.HardwareDecode = i == 0 ? VideoBuilder.Media.HardwareDecodeMode.Auto : VideoBuilder.Media.HardwareDecodeMode.Off;
            VideoBuilder.Media.HardwareDecoding.Mode = s.HardwareDecode;
            Svc.Preview.ReloadBackend();     // 開いている動画を閉じて、新しい設定で開き直す
            ShowDecode();
        })));
        page.Children.Add(decodeInfo);
        ShowDecode();
        _showDecode = ShowDecode;

        page = AddPage("プロキシ");
        var useProxy = new CheckBox { Content = "プレビューでプロキシを使う (出力には使わない)", IsChecked = s.UseProxies };
        useProxy.IsCheckedChanged += (_, _) =>
        {
            s.UseProxies = useProxy.IsChecked == true; VideoBuilder.Media.ProxyStore.Enabled = s.UseProxies; s.Save(); Svc.Preview.Request();
        };
        page.Children.Add(useProxy);
        var autoProxy = new CheckBox { Content = "再生が重くなりそうな動画は自動でプロキシを作る (4K・H.265 など。この PC でのデコードの速さも測って決める)", IsChecked = s.AutoProxy };
        autoProxy.IsCheckedChanged += (_, _) => { s.AutoProxy = autoProxy.IsChecked == true; s.Save(); };
        page.Children.Add(autoProxy);
        page.Children.Add(Row("プロキシの大きさ", Combo(new[] { "縦横 1/2", "縦横 1/4" }, s.ProxyDivisor == 4 ? 1 : 0, i =>
        {
            s.ProxyDivisor = i == 1 ? 4 : 2;
            VideoBuilder.Media.ProxyStore.Divisor = s.ProxyDivisor;
            VideoBuilder.Media.ProxyStore.NotifyChanged();
        })));
        var proxySize = new TextBlock { TextWrapping = TextWrapping.Wrap };
        proxySize.Classes.Add("secondary");
        void ShowProxySize() => proxySize.Text = $"使用中の容量: {VideoBuilder.Media.ProxyStore.TotalSize() / 1048576.0:0.0} MB";
        var clearProxies = new Button { Content = "すべてのプロキシを削除", HorizontalAlignment = HorizontalAlignment.Left };
        clearProxies.Click += (_, _) => { Svc.Proxies.CancelAll(); VideoBuilder.Media.ProxyStore.Clear(); ShowProxySize(); };
        ShowProxySize();
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { clearProxies, proxySize } });

        page = AddPage("編集");
        page.Children.Add(Row("プレビュー品質", Combo(new[] { "自動 (表示の大きさ)", "フル", "1/2", "1/4" },
            s.PreviewDivisor <= 0 ? 0 : s.PreviewDivisor >= 4 ? 3 : s.PreviewDivisor >= 2 ? 2 : 1, i =>
        {
            s.PreviewDivisor = new[] { 0, 1, 2, 4 }[i]; Svc.Preview.Request();
        })));
        var adaptive = new CheckBox { Content = "再生が間に合わないときは、再生中だけ解像度を下げる (プレビュー品質が自動のとき)", IsChecked = s.AdaptivePlaybackResolution };
        adaptive.IsCheckedChanged += (_, _) => { s.AdaptivePlaybackResolution = adaptive.IsChecked == true; s.Save(); };
        page.Children.Add(adaptive);
        var seconds = new NumberBox { Minimum = 0.1, Maximum = 600, Decimals = 1, Value = s.DefaultObjectSeconds, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        seconds.Committed += v => { s.DefaultObjectSeconds = v; s.Save(); };
        page.Children.Add(Row("新規オブジェクトの長さ (秒)", seconds, 180));
        var crossfade = new NumberBox { Minimum = 0.05, Maximum = 30, Decimals = 2, Value = s.CrossfadeSeconds, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        crossfade.Committed += v => { s.CrossfadeSeconds = v; s.Save(); };
        page.Children.Add(Row("クロスフェードの長さ (秒)", crossfade, 180));
        var auto = new CheckBox { Content = $"自動保存する ({s.AutoSaveMinutes} 分ごと)", IsChecked = s.AutoSave };
        auto.IsCheckedChanged += (_, _) => { s.AutoSave = auto.IsChecked == true; s.Save(); };
        page.Children.Add(auto);

        var wave = new CheckBox { Content = "音声の波形をタイムラインに表示する", IsChecked = s.ShowWaveform };
        wave.IsCheckedChanged += (_, _) => { s.ShowWaveform = wave.IsChecked == true; s.Save(); Svc.Session.NotifyChanged(); };
        page.Children.Add(wave);
        var thumbs = new CheckBox { Content = "動画・画像のサムネイルをタイムラインに表示する", IsChecked = s.ShowThumbnails };
        thumbs.IsCheckedChanged += (_, _) => { s.ShowThumbnails = thumbs.IsChecked == true; s.Save(); Svc.Session.NotifyChanged(); };
        page.Children.Add(thumbs);
        var keys = new IconButton { Icon = Icons.Get("Tune"), Text = "ショートカットの設定...", HorizontalAlignment = HorizontalAlignment.Left };
        keys.Click += async (_, _) => await new ShortcutDialog().ShowDialog(this);
        page.Children.Add(keys);

        page = AddPage("テンプレート");
        var shared = new TextBox { Text = s.SharedTemplateDir, Watermark = "(使わない)", IsReadOnly = true };
        void SetShared(string dir)
        {
            s.SharedTemplateDir = dir; s.Save();
            Svc.Templates.SharedDirectory = dir.Length == 0 ? null : dir;
            shared.Text = dir;
        }
        var pickShared = new IconButton("FolderOpen", "共有テンプレートフォルダを選ぶ", flat: false) { Margin = new Thickness(4, 0, 0, 0) };
        pickShared.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "共有テンプレートフォルダ" });
            var path = folders.Count > 0 ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folders[0]) : null;
            if (path != null) SetShared(path);
        };
        var clearShared = new IconButton("Close", "共有テンプレートフォルダを使わない", flat: false) { Margin = new Thickness(4, 0, 0, 0) };
        clearShared.Click += (_, _) => SetShared("");
        var sharedRow = new DockPanel();
        DockPanel.SetDock(clearShared, Dock.Right); DockPanel.SetDock(pickShared, Dock.Right);
        sharedRow.Children.Add(clearShared); sharedRow.Children.Add(pickShared); sharedRow.Children.Add(shared);
        page.Children.Add(Row("共有フォルダ", sharedRow));
        var sharedNote = new TextBlock { Text = "チームの共有ドライブなどを指定すると、そこのテンプレートもライブラリに並びます (読むだけ)。", TextWrapping = TextWrapping.Wrap };
        sharedNote.Classes.Add("secondary");
        page.Children.Add(sharedNote);

        page = AddPage("情報");
        page.Children.Add(Heading("プラグイン"));
        var plugins = VideoBuilder.Rendering.PluginLoader.Loaded;
        var pluginText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = (plugins.Count == 0 ? "プラグインはありません。" : string.Join("\n", plugins.Select(pl => pl.Loaded ? $"{pl.Name} {pl.Version}" : $"{pl.Name}: {pl.Error}")))
                   + $"\n置き場所: {AppPaths.PluginDir}",
        };
        pluginText.Classes.Add("secondary");
        page.Children.Add(pluginText);

        page.Children.Add(Heading("FFmpeg"));
        var ff = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = FFmpegLoader.EnsureLoaded() ? $"FFmpeg {FFmpegLoader.Version} を使用中" : "FFmpeg が見つかりません。動画・音声の読み込みと出力は使えません。\n" + FFmpegLoader.Error,
        };
        ff.Classes.Add("secondary");
        page.Children.Add(ff);

        BuildLayout();
        ShowPage(Math.Clamp(_lastPage, 0, _pages.Count - 1));
        AddButton("閉じる", Close, accent: true);
    }
}

public sealed class AboutDialog : DialogBase
{
    public AboutDialog() : base("バージョン情報", 400)
    {
        var name = new TextBlock { Text = "video-builder", FontSize = 18, FontWeight = FontWeight.SemiBold };
        Body.Children.Add(name);
        var version = typeof(AboutDialog).Assembly.GetName().Version;
        Body.Children.Add(new TextBlock { Text = $"バージョン {version?.ToString(3) ?? "0.1.0"}" });
        Body.Children.Add(new TextBlock { Text = "AviUtl ライクな操作感の動画編集アプリ。", TextWrapping = TextWrapping.Wrap });
        Body.Children.Add(new TextBlock { Text = "ライセンス: LGPL-3.0-or-later", Margin = new Thickness(0, 8, 0, 0) });
        // 読み込んだ FFmpeg のライセンス (同梱するものは x264・x265 入りの GPL ビルド。THIRD_PARTY_NOTICES.md)
        if (FFmpegLoader.EnsureLoaded())
            Body.Children.Add(new TextBlock { Text = $"FFmpeg {FFmpegLoader.Version} ({FFmpegLoader.License})", TextWrapping = TextWrapping.Wrap });
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Text = "使用ライブラリ: FFmpeg (GPL-3.0。x264・x265 を含む), Avalonia UI (MIT), SkiaSharp (MIT), Svg.Skia (MIT), " +
                   "FFmpeg.AutoGen (MIT), Silk.NET (MIT), OpenAL Soft (LGPL), MoonSharp (BSD)\n" +
                   "フォント: Noto Sans JP・Inter (OFL)\nアイコン: Material Symbols Sharp (Apache License 2.0)\n\n" +
                   "配布物は GPL のライブラリを含むため、全体を GPL-3.0-or-later の条件で配布しています。詳しくは THIRD_PARTY_NOTICES.md を参照してください。",
        };
        text.Classes.Add("secondary");
        Body.Children.Add(text);
        AddButton("閉じる", Close, accent: true);
    }
}

/// <summary>1 行の文字入力。</summary>
public sealed class PromptDialog : DialogBase
{
    public string? Result { get; private set; }

    public PromptDialog(string title, string label, string initial = "") : base(title, 380)
    {
        var box = new TextBox { Text = initial };
        Body.Children.Add(new TextBlock { Text = label });
        Body.Children.Add(box);
        AddButton("キャンセル", Close);
        AddButton("OK", () => { if (!string.IsNullOrWhiteSpace(box.Text)) { Result = box.Text.Trim(); Close(); } }, accent: true);
        Opened += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    public static async Task<string?> Show(Window owner, string title, string label, string initial = "")
    {
        var d = new PromptDialog(title, label, initial);
        await d.ShowDialog(owner);
        return d.Result;
    }
}

public sealed class GridDialog : DialogBase
{
    public GridDialog() : base("グリッドの設定", 380)
    {
        var session = Svc.Session;
        var g = session.Project.Grid;
        var mode = new ComboBox { ItemsSource = new[] { "表示しない", "時間", "BPM (テンポ)" }, SelectedIndex = (int)g.Mode, HorizontalAlignment = HorizontalAlignment.Stretch };
        var bpm = new NumberBox { Minimum = 1, Maximum = 999, Decimals = 2, Value = g.Bpm, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        var beats = new NumberBox { Minimum = 1, Maximum = 32, Decimals = 0, Value = g.Beats, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        var offset = new NumberBox { Minimum = 0, Maximum = 36000, Decimals = 3, Value = g.OffsetSeconds, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        Body.Children.Add(Row("グリッド", mode));
        Body.Children.Add(Row("テンポ (BPM)", bpm));
        Body.Children.Add(Row("拍子 (1 小節の拍数)", beats, 150));
        Body.Children.Add(Row("1 拍目の位置 (秒)", offset, 150));
        var here = new Button { Content = "現在位置を 1 拍目にする", HorizontalAlignment = HorizontalAlignment.Left };
        here.Click += (_, _) => offset.Value = Math.Round(session.Project.FrameToSeconds(session.CurrentFrame), 3);
        Body.Children.Add(here);
        var note = new TextBlock { Text = "BPM グリッドを表示している間は、オブジェクトの端が拍にスナップします。", TextWrapping = TextWrapping.Wrap };
        note.Classes.Add("secondary");
        Body.Children.Add(note);
        AddButton("キャンセル", Close);
        AddButton("OK", () =>
        {
            session.Edit("グリッドの設定", () => session.Project.Grid = new GridSettings
            {
                Mode = (GridMode)Math.Max(0, mode.SelectedIndex), Bpm = bpm.Value, Beats = (int)beats.Value, OffsetSeconds = offset.Value,
            });
            Close();
        }, accent: true);
    }
}

/// <summary>見つからない素材の一覧と、付け替えの操作。</summary>
public sealed class MissingFilesDialog : DialogBase
{
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    public MissingFilesDialog() : base("見つからない素材", 560)
    {
        Body.Children.Add(new TextBlock { Text = "次の素材ファイルが見つかりません。フォルダを指定すると、同じ名前のファイルを探して付け替えます。", TextWrapping = TextWrapping.Wrap });
        Body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 240 });
        _status.Classes.Add("secondary");
        Body.Children.Add(_status);
        AddButton("閉じる", Close);
        AddButton("フォルダから探す...", async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "素材を探すフォルダ" });
            var folder = folders.FirstOrDefault()?.Path.LocalPath;
            if (folder == null) return;
            int n = 0;
            Svc.Session.Edit("素材の付け替え", () => n = ProjectMedia.RelinkFromFolder(Svc.Session.Project, folder));
            _status.Text = n > 0 ? $"{n} 個のファイルを付け替えました" : "同じ名前のファイルは見つかりませんでした";
            Refresh();
        }, accent: true, icon: "FolderOpen");
        Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();
        var missing = ProjectMedia.FindMissing(Svc.Session.Project);
        foreach (var path in missing)
        {
            var row = new DockPanel();
            var pick = new IconButton("FolderOpen", "このファイルの場所を指定");
            pick.Click += async (_, _) =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = Path.GetFileName(path) });
                var now = files.FirstOrDefault()?.Path.LocalPath;
                if (now == null) return;
                Svc.Session.Edit("素材の付け替え", () => ProjectMedia.Relink(Svc.Session.Project, path, now));
                Refresh();
            };
            DockPanel.SetDock(pick, Dock.Right);
            row.Children.Add(pick);
            var text = new TextBlock { Text = path, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.PathSegmentEllipsis };
            text.Classes.Add("mono");
            ToolTip.SetTip(text, path);
            row.Children.Add(text);
            _list.Children.Add(row);
        }
        if (missing.Count == 0) _list.Children.Add(new TextBlock { Text = "すべての素材が見つかりました。" });
    }
}

public sealed class ShortcutDialog : DialogBase
{
    private readonly StackPanel _list = new() { Spacing = 1 };
    private readonly TextBlock _status = new() { Text = "変更したい操作のボタンを押してから、新しいキーを押してください。", TextWrapping = TextWrapping.Wrap };
    private string? _capturing;

    public ShortcutDialog() : base("ショートカットの設定", 520)
    {
        Body.Children.Add(new ScrollViewer { Content = _list, Height = 420 });
        _status.Classes.Add("secondary");
        Body.Children.Add(_status);
        AddButton("すべて既定に戻す", () => { Shortcuts.Reset(); _capturing = null; Refresh(); });
        AddButton("閉じる", Close, accent: true);
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Refresh();
    }

    private void OnKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (_capturing == null) return;
        e.Handled = true;
        var k = e.Key;
        if (k is Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl or Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift
            or Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt or Avalonia.Input.Key.LWin or Avalonia.Input.Key.RWin) return;
        string id = _capturing;
        _capturing = null;
        if (k == Avalonia.Input.Key.Escape) { _status.Text = "変更をやめました"; Refresh(); return; }
        var stolen = Shortcuts.Assign(id, new Avalonia.Input.KeyGesture(k, e.KeyModifiers));
        _status.Text = stolen != null ? $"「{stolen}」に割り当てられていたキーを移しました" : "キーを変更しました (メニューの表示は次回起動時に更新されます)";
        Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();
        foreach (var c in Shortcuts.All)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,150,Auto,Auto"), MinHeight = 26 };
            grid.Children.Add(new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center });
            string shown = Shortcuts.Display(c.Id);
            var key = new Button { Content = _capturing == c.Id ? "キーを押してください..." : shown.Length == 0 ? "(なし)" : shown, HorizontalAlignment = HorizontalAlignment.Stretch, Focusable = false };
            if (_capturing == c.Id) key.Classes.Add("accent");
            key.Click += (_, _) => { _capturing = c.Id; _status.Text = $"「{c.Name}」の新しいキーを押してください (Esc でやめる)"; Refresh(); };
            Grid.SetColumn(key, 1); grid.Children.Add(key);
            var clear = new IconButton("Close", "割り当てをなくす", () => { Shortcuts.Assign(c.Id, null); Refresh(); });
            Grid.SetColumn(clear, 2); grid.Children.Add(clear);
            var reset = new IconButton("Reset", "既定に戻す", () => { Shortcuts.Reset(c.Id); Refresh(); });
            Grid.SetColumn(reset, 3); grid.Children.Add(reset);
            _list.Children.Add(grid);
        }
    }
}

