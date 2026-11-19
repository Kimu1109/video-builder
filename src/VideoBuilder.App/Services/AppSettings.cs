using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoBuilder.App.Services;

public enum ThemeMode { System, Light, Dark }
public enum AccentMode { System, AppDefault, Custom }
public enum WindowMode { Auto, Separate, Docked }
/// <summary>
/// 編集画面の種類。Advanced: レイヤーとオブジェクトで細かく組み立てる (AviUtl 風)。
/// Simple: クリップを 1 列に並べるだけ (ムービーメーカー風)。
/// </summary>
public enum EditorMode { Advanced, Simple }
/// <summary>Auto: 本物の GPU があれば GPU、なければ CPU。</summary>
public enum RenderBackend { Auto, Gpu, Cpu }

/// <summary>環境設定。XDG_CONFIG_HOME/video-builder/settings.json に保存する。</summary>
public sealed class AppSettings
{
    public Language Language { get; set; } = Language.Auto;
    public EditorMode EditorMode { get; set; } = EditorMode.Advanced;
    /// <summary>パフォーマンスチェックモード (プレビューの描画時間・FPS・出力の内訳などを表示する)。</summary>
    public bool PerformanceMode { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public AccentMode Accent { get; set; } = AccentMode.System;
    public string CustomAccent { get; set; } = "#2F80ED";
    public WindowMode WindowMode { get; set; } = WindowMode.Auto;
    public RenderBackend RenderBackend { get; set; } = RenderBackend.Auto;
    /// <summary>動画のデコードに GPU を使うか (Auto: 使えれば使う)。</summary>
    public VideoBuilder.Media.HardwareDecodeMode HardwareDecode { get; set; } = VideoBuilder.Media.HardwareDecodeMode.Auto;
    /// <summary>プレビューでプロキシを使う。</summary>
    public bool UseProxies { get; set; } = true;
    /// <summary>重い動画 (4K・H.265 など) を読み込んだとき、自動でプロキシを作る。</summary>
    public bool AutoProxy { get; set; } = true;
    /// <summary>プロキシの縮小率の分母 (2 または 4)。</summary>
    public int ProxyDivisor { get; set; } = 2;
    public int LayerHeight { get; set; } = 24;
    public bool Snap { get; set; } = true;
    public bool AutoSave { get; set; } = true;
    public int AutoSaveMinutes { get; set; } = 5;
    public int UndoLimit { get; set; } = 200;
    /// <summary>
    /// プレビュー解像度の分母 (1 = フル, 2 = 1/2, 4 = 1/4)。0 は自動 (画面に表示される大きさで描く)。
    /// </summary>
    public int PreviewDivisor { get; set; } = 0;
    public double DefaultObjectSeconds { get; set; } = 3;
    public List<string> RecentFiles { get; set; } = new();
    /// <summary>変更したショートカット (操作 ID → キー)。ここにない操作は既定のキー。</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    public bool ShowWaveform { get; set; } = true;
    public bool ShowThumbnails { get; set; } = true;
    /// <summary>共有テンプレートフォルダ (読むだけ)。空なら使わない。</summary>
    public string SharedTemplateDir { get; set; } = "";
    /// <summary>テンプレートを入れるとき、作った解像度とプロジェクトの解像度が違えば、位置と大きさを合わせる。</summary>
    public bool FitTemplateResolution { get; set; } = true;
    /// <summary>「次のクリップとつなぐ (クロスフェード)」で重ねる長さ (秒)。</summary>
    public double CrossfadeSeconds { get; set; } = 0.5;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new AppSettings();
        }
        catch { /* 壊れた設定ファイルは無視して既定値で起動する */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch { /* 保存できなくても編集は続けられる */ }
    }

    public void AddRecent(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        Save();
    }
}

/// <summary>設定・データ・キャッシュの置き場所 (Linux は XDG Base Directory)。</summary>
public static class AppPaths
{
    private const string App = "video-builder";

    private static string Xdg(string env, Environment.SpecialFolder fallback, params string[] homeRelative)
    {
        var v = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrEmpty(v)) return Path.Combine(v, App);
        if (OperatingSystem.IsLinux() && homeRelative.Length > 0)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(new[] { home }.Concat(homeRelative).Append(App).ToArray());
        }
        return Path.Combine(Environment.GetFolderPath(fallback), App);
    }

    public static string ConfigDir => Xdg("XDG_CONFIG_HOME", Environment.SpecialFolder.ApplicationData, ".config");
    public static string DataDir => Xdg("XDG_DATA_HOME", Environment.SpecialFolder.LocalApplicationData, ".local", "share");
    public static string CacheDir => Xdg("XDG_CACHE_HOME", Environment.SpecialFolder.LocalApplicationData, ".cache");
    public static string PluginDir => Path.Combine(DataDir, "plugins");
    public static string AliasDir => Path.Combine(DataDir, "aliases");
    public static string TemplateDir => Path.Combine(DataDir, "templates");
    public static string StyleDir => Path.Combine(DataDir, "styles");
    /// <summary>テンプレートから取り出した素材。プロジェクトから参照されるので、キャッシュではなくデータの場所に置く。</summary>
    public static string TemplateAssetDir => Path.Combine(DataDir, "template-assets");
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string AutoSaveDir => Path.Combine(DataDir, "autosave");
}
