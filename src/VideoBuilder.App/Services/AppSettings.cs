using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoBuilder.App.Services;

public enum ThemeMode { System, Light, Dark }
public enum AccentMode { System, AppDefault, Custom }
public enum WindowMode { Auto, Separate, Docked }

/// <summary>環境設定。XDG_CONFIG_HOME/video-builder/settings.json に保存する。</summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public AccentMode Accent { get; set; } = AccentMode.System;
    public string CustomAccent { get; set; } = "#2F80ED";
    public WindowMode WindowMode { get; set; } = WindowMode.Auto;
    public int LayerHeight { get; set; } = 24;
    public bool Snap { get; set; } = true;
    public bool AutoSave { get; set; } = true;
    public int AutoSaveMinutes { get; set; } = 5;
    public int UndoLimit { get; set; } = 200;
    /// <summary>プレビュー解像度の分母 (1 = フル, 2 = 1/2, 4 = 1/4)。</summary>
    public int PreviewDivisor { get; set; } = 1;
    public double DefaultObjectSeconds { get; set; } = 3;
    public List<string> RecentFiles { get; set; } = new();

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
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string AutoSaveDir => Path.Combine(DataDir, "autosave");
}
