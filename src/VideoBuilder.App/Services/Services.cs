using Avalonia;
using Avalonia.Media;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Services;

/// <summary>アプリ全体で共有するもの。App の起動時に用意する。</summary>
public static class Svc
{
    public static EditorSession Session { get; } = new();
    public static AppSettings Settings { get; private set; } = new();
    public static ThemeService Theme { get; private set; } = null!;
    public static PreviewService Preview { get; private set; } = null!;

    public static void Initialize(Application app)
    {
        Settings = AppSettings.Load();
        Session.History.Limit = Settings.UndoLimit;
        Theme = new ThemeService(app, Settings);
        Theme.Apply();
        Preview = new PreviewService(Session, Settings);
    }

    /// <summary>テーマに応じたブラシを取り出す (コントロールの Render から使う)。</summary>
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b) return b;
        return Brushes.Magenta;
    }
}
