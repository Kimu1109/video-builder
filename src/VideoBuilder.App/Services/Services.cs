using Avalonia;
using Avalonia.Media;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Services;

/// <summary>アプリ全体で共有するもの。App の起動時に用意する。</summary>
public static class Svc
{
    public static EditorSession Session { get; } = new();
    /// <summary>シンプル編集の操作 (Session のプロジェクトのストーリーボードを編集する)。</summary>
    public static StoryboardEditor Storyboard { get; } = new(Session);
    public static AppSettings Settings { get; private set; } = new();
    public static ThemeService Theme { get; private set; } = null!;
    public static PreviewService Preview { get; private set; } = null!;
    /// <summary>パフォーマンスチェックモードで表示する数値。</summary>
    public static PerformanceMonitor Performance { get; private set; } = null!;
    public static WaveformCache Waveforms { get; } = new();
    public static ProxyService Proxies { get; } = new();
    public static ThumbnailCache Thumbnails { get; } = new();
    public static AliasStore Aliases { get; private set; } = new(AppPaths.AliasDir);
    public static TemplateStore Templates { get; private set; } = new(AppPaths.TemplateDir, AppPaths.TemplateAssetDir, AppPaths.AliasDir);
    public static StyleStore Styles { get; private set; } = new(AppPaths.StyleDir);

    public static void Initialize(Application app)
    {
        Settings = AppSettings.Load();
        Aliases = new AliasStore(AppPaths.AliasDir);
        Templates = new TemplateStore(AppPaths.TemplateDir, AppPaths.TemplateAssetDir, AppPaths.AliasDir)
        {
            SharedDirectory = string.IsNullOrWhiteSpace(Settings.SharedTemplateDir) ? null : Settings.SharedTemplateDir,
        };
        Styles = new StyleStore(AppPaths.StyleDir);
        VideoBuilder.Media.HardwareDecoding.Mode = Settings.HardwareDecode;
        ApplyGpuAdapter();
        // 動画のデコードも、描画に使う GPU に合わせる (CPU 内蔵の GPU を選べば VAAPI / D3D11VA を、NVIDIA なら NVDEC を先に試す)
        VideoBuilder.Media.HardwareDecoding.DeviceHint = () =>
        {
            var a = VideoBuilder.Rendering.GpuContext.SelectedAdapter();
            if (a == null || a.IsSoftware) return null;
            // Windows: D3D11VA は DXGI の列挙の番号で GPU を選ぶ (NVIDIA でも、NVDEC が使えないときの D3D11VA を同じ GPU にする)
            if (OperatingSystem.IsWindows()) return (a.IsNvidia ? "cuda" : "d3d11va", a.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return a.IsNvidia ? ("cuda", "") : ("vaapi", a.RenderNode);
        };
        VideoBuilder.Media.ProxyStore.Directory = Path.Combine(AppPaths.CacheDir, "proxy");
        VideoBuilder.Media.ProxyStore.Enabled = Settings.UseProxies;
        VideoBuilder.Media.ProxyStore.Divisor = Settings.ProxyDivisor is 4 ? 4 : 2;
        L.Initialize(LanguageOverride ?? Settings.Language);
        RegisterBundledFonts();
        if (!_pluginsLoaded)
        {
            _pluginsLoaded = true;
            // 利用者が入れたものを先に読み、同梱のもの (VOICEVOX 連携など) は同じ ID がなければ読む
            VideoBuilder.Rendering.PluginLoader.LoadAll(AppPaths.PluginDir);
            VideoBuilder.Rendering.PluginLoader.LoadAll(AppPaths.BundledPluginDir, skipLoaded: true);
        }
        Session.History.Limit = Settings.UndoLimit;
        Theme = new ThemeService(app, Settings);
        Theme.Apply();
        Preview = new PreviewService(Session, Settings);
        Performance = new PerformanceMonitor(Preview, Settings);
        // プロキシができたら、プレビューを描き直す (開いている動画は次の描画でプロキシに切り替わる)
        VideoBuilder.Media.ProxyStore.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => Preview.Request());
        // プロキシは今見ている位置から先に作る
        void Focus() => Proxies.UpdateFocus(Session.Project, Session.Scene, Session.CurrentFrame);
        Session.FrameChanged += Focus;
        Session.SceneChanged += Focus;
        Proxies.FocusChanged += Focus;
    }

    /// <summary>設定の「使う GPU」を描画に伝える (環境変数 VIDEO_BUILDER_GPU があれば、そちらを優先)。</summary>
    public static void ApplyGpuAdapter()
    {
        var env = Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU");
        VideoBuilder.Rendering.GpuContext.PreferredAdapter = !string.IsNullOrEmpty(env) ? env : Settings.GpuAdapter.Length > 0 ? Settings.GpuAdapter : null;
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
