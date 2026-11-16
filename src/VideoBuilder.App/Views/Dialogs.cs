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
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8) };

    public DialogBase(string title, double width)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var footer = new Border { Child = Buttons, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.Classes.Add("panel");
        var dock = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(Body);
        Content = dock;
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
    public PreferencesDialog() : base("環境設定", 440)
    {
        var s = Svc.Settings;
        ComboBox Combo(string[] items, int index, Action<int> changed)
        {
            var c = new ComboBox { ItemsSource = items, SelectedIndex = index, HorizontalAlignment = HorizontalAlignment.Stretch };
            c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { changed(c.SelectedIndex); s.Save(); } };
            return c;
        }

        Body.Children.Add(Heading("外観"));
        Body.Children.Add(Row("テーマ", Combo(new[] { "システムに従う", "ライト", "ダーク" }, (int)s.Theme, i => { s.Theme = (ThemeMode)i; Svc.Theme.Apply(); })));

        var custom = new ColorPicker { Color = Color.TryParse(s.CustomAccent, out var cc) ? cc : Colors.DodgerBlue, IsAlphaEnabled = false, IsAlphaVisible = false, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = s.Accent == AccentMode.Custom };
        custom.ColorChanged += (_, e) =>
        {
            s.CustomAccent = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
            s.Save();
            if (s.Accent == AccentMode.Custom) Svc.Theme.Apply();
        };
        Body.Children.Add(Row("アクセントカラー", Combo(new[] { "システムに従う", "アプリ既定 (青)", "カスタム" }, (int)s.Accent, i =>
        {
            s.Accent = (AccentMode)i; custom.IsEnabled = s.Accent == AccentMode.Custom; Svc.Theme.Apply();
        })));
        Body.Children.Add(Row("カスタム色", custom));
        Body.Children.Add(Row("ウィンドウ", Combo(new[] { "自動 (X11 は分離 / Wayland はドッキング)", "分離 (AviUtl と同じ)", "ドッキング (1 つにまとめる)" }, (int)s.WindowMode, i =>
        {
            s.WindowMode = (WindowMode)i; EditorCommands.Main?.ApplyWindowMode();
        })));
        Body.Children.Add(Row("レイヤーの高さ", Combo(new[] { "小 (20)", "中 (24)", "大 (32)" }, s.LayerHeight <= 20 ? 0 : s.LayerHeight <= 24 ? 1 : 2, i =>
        {
            s.LayerHeight = new[] { 20, 24, 32 }[i]; Svc.Session.NotifyChanged();
        })));

        Body.Children.Add(Heading("編集"));
        Body.Children.Add(Row("プレビュー品質", Combo(new[] { "フル", "1/2", "1/4" }, s.PreviewDivisor >= 4 ? 2 : s.PreviewDivisor >= 2 ? 1 : 0, i =>
        {
            s.PreviewDivisor = new[] { 1, 2, 4 }[i]; Svc.Preview.Request();
        })));
        var seconds = new NumberBox { Minimum = 0.1, Maximum = 600, Decimals = 1, Value = s.DefaultObjectSeconds, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        seconds.Committed += v => { s.DefaultObjectSeconds = v; s.Save(); };
        Body.Children.Add(Row("新規オブジェクトの長さ (秒)", seconds, 180));
        var auto = new CheckBox { Content = $"自動保存する ({s.AutoSaveMinutes} 分ごと)", IsChecked = s.AutoSave };
        auto.IsCheckedChanged += (_, _) => { s.AutoSave = auto.IsChecked == true; s.Save(); };
        Body.Children.Add(auto);

        Body.Children.Add(Heading("FFmpeg"));
        var ff = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = FFmpegLoader.EnsureLoaded() ? $"FFmpeg {FFmpegLoader.Version} を使用中" : "FFmpeg が見つかりません。動画・音声の読み込みと出力は使えません。\n" + FFmpegLoader.Error,
        };
        ff.Classes.Add("secondary");
        Body.Children.Add(ff);

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
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "AviUtl ライクな操作感の動画編集アプリ。\n\n" +
                   "使用ライブラリ: Avalonia UI (MIT), SkiaSharp (MIT), FFmpeg (LGPL / GPL), OpenAL Soft (LGPL), " +
                   "Silk.NET (MIT), CommunityToolkit.Mvvm (MIT)\nアイコン: Material Symbols Sharp (Apache License 2.0)",
        };
        text.Classes.Add("secondary");
        Body.Children.Add(text);
        AddButton("閉じる", Close, accent: true);
    }
}
