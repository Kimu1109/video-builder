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
    public static WaveformCache Waveforms { get; } = new();
    public static ProxyService Proxies { get; } = new();
    public static ThumbnailCache Thumbnails { get; } = new();
    public static AliasStore Aliases { get; private set; } = new(AppPaths.AliasDir);

    public static void Initialize(Application app)
    {
        Settings = AppSettings.Load();
        Aliases = new AliasStore(AppPaths.AliasDir);
        VideoBuilder.Media.HardwareDecoding.Mode = Settings.HardwareDecode;
        VideoBuilder.Media.ProxyStore.Directory = Path.Combine(AppPaths.CacheDir, "proxy");
        VideoBuilder.Media.ProxyStore.Enabled = Settings.UseProxies;
        VideoBuilder.Media.ProxyStore.Divisor = Settings.ProxyDivisor is 4 ? 4 : 2;
        L.Initialize(LanguageOverride ?? Settings.Language);
        RegisterBundledFonts();
        if (!_pluginsLoaded) { _pluginsLoaded = true; VideoBuilder.Rendering.PluginLoader.LoadAll(AppPaths.PluginDir); }
        Session.History.Limit = Settings.UndoLimit;
        Theme = new ThemeService(app, Settings);
        Theme.Apply();
        Preview = new PreviewService(Session, Settings);
        // プロキシができたら、プレビューを描き直す (開いている動画は次の描画でプロキシに切り替わる)
        VideoBuilder.Media.ProxyStore.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => Preview.Request());
    }

    private static bool _fontsRegistered, _pluginsLoaded;
    /// <summary>起動オプション (--lang) による指定。設定ファイルより優先する。</summary>
    public static Language? LanguageOverride { get; set; }

    /// <summary>同梱の Noto Sans JP を、テキストオブジェクトの描画でも使えるようにする。</summary>
    private static void RegisterBundledFonts()
    {
        if (_fontsRegistered) return;
        _fontsRegistered = true;
        try
        {
            SkiaSharp.SKTypeface? Load(string name)
            {
                using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://video-builder/Assets/Fonts/{name}"));
                using var data = SkiaSharp.SKData.Create(stream);
                return SkiaSharp.SKTypeface.FromData(data);
            }
            var regular = Load("NotoSansJP-Regular.ttf");
            if (regular != null) VideoBuilder.Rendering.Generators.RegisterBundledFont(regular, Load("NotoSansJP-Bold.ttf"));
        }
        catch { /* 読み込めなければシステムのフォントを使う */ }
    }

    /// <summary>テーマに応じたブラシを取り出す (コントロールの Render から使う)。</summary>
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b) return b;
        return Brushes.Magenta;
    }
}
