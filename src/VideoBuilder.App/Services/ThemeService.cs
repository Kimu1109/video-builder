using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace VideoBuilder.App.Services;

/// <summary>
/// テーマ (ライト / ダーク) とアクセントカラーを、設定と OS の状態から決めて反映する (設計書 7.3)。
/// </summary>
public sealed class ThemeService
{
    private readonly Application _app;
    private readonly AppSettings _settings;
    private readonly IPlatformSettings? _platform;

    public event Action? Changed;

    public ThemeService(Application app, AppSettings settings)
    {
        _app = app; _settings = settings;
        _platform = app.PlatformSettings;
        if (_platform != null) _platform.ColorValuesChanged += (_, _) => Apply();   // OS 側の変更に追従
    }

    public bool IsDark => _app.ActualThemeVariant == ThemeVariant.Dark;

    public void Apply()
    {
        _app.RequestedThemeVariant = _settings.Theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ResolveSystemVariant(),
        };
        bool dark = IsDark;

        Color baseAccent;
        if (_settings.Accent == AccentMode.Custom && Color.TryParse(_settings.CustomAccent, out var custom)) baseAccent = custom;
        else if (_settings.Accent == AccentMode.System && TryGetSystemAccent(out var sys)) baseAccent = sys;
        else baseAccent = Color.Parse(dark ? "#2F80ED" : "#1F6FD9");

        var panel = Color.Parse(dark ? "#252526" : "#FFFFFF");
        var p = AccentPalette.Create(baseAccent, panel, dark);
        var r = _app.Resources;
        r["Accent"] = new SolidColorBrush(p.Accent);
        r["Accent.Hover"] = new SolidColorBrush(p.Hover);
        r["Fg.OnAccent"] = new SolidColorBrush(p.OnAccent);
        // Fluent テーマ側のコントロール (チェックボックス・選択行など) も同じ色にする
        r["SystemAccentColor"] = p.Accent;
        r["SystemAccentColorDark1"] = AccentPalette.ShiftLightness(p.Accent, -0.05);
        r["SystemAccentColorDark2"] = AccentPalette.ShiftLightness(p.Accent, -0.10);
        r["SystemAccentColorDark3"] = AccentPalette.ShiftLightness(p.Accent, -0.15);
        r["SystemAccentColorLight1"] = AccentPalette.ShiftLightness(p.Accent, 0.05);
        r["SystemAccentColorLight2"] = AccentPalette.ShiftLightness(p.Accent, 0.10);
        r["SystemAccentColorLight3"] = AccentPalette.ShiftLightness(p.Accent, 0.15);
        Changed?.Invoke();
    }

    /// <summary>OS のテーマ。取得できなければ GTK_THEME を見て、それでも不明ならダーク。</summary>
    private ThemeVariant ResolveSystemVariant()
    {
        try
        {
            var values = _platform?.GetColorValues();
            if (values != null)
            {
                bool portalMissing = OperatingSystem.IsLinux() && values.ThemeVariant == PlatformThemeVariant.Light && !HasAppearancePortal();
                if (!portalMissing)
                    return values.ThemeVariant == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
            }
        }
        catch { /* 下のフォールバックへ */ }
        var gtk = Environment.GetEnvironmentVariable("GTK_THEME");
        if (!string.IsNullOrEmpty(gtk))
            return gtk.Contains("dark", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Dark : ThemeVariant.Light;
        return ThemeVariant.Dark;
    }

    /// <summary>
    /// xdg-desktop-portal が動いているかの目安。動いていない環境 (sway / i3 など) では
    /// Avalonia が常に「ライト」を返すので、その値を信用しない。
    /// </summary>
    private static bool HasAppearancePortal()
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        if (desktop.Length == 0) return false;
        string[] known = { "GNOME", "KDE", "Cinnamon", "XFCE", "MATE", "Unity", "Pantheon", "COSMIC", "Budgie", "LXQt", "Deepin" };
        return known.Any(k => desktop.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetSystemAccent(out Color color)
    {
        color = default;
        try
        {
            var values = _platform?.GetColorValues();
            if (values == null) return false;
            color = values.AccentColor1;
            // 取得できない環境では既定値 (透明や黒) が返る
            return color.A == 255 && (color.R | color.G | color.B) != 0;
        }
        catch { return false; }
    }
}

public readonly record struct AccentPalette(Color Accent, Color Hover, Color OnAccent)
{
    /// <summary>背景とのコントラスト比が 3:1 以上になるまで明度を調整する。</summary>
    public static AccentPalette Create(Color accent, Color background, bool dark)
    {
        var (l, a, b) = ToOklab(accent);
        var c = accent;
        for (int i = 0; i < 40 && Contrast(c, background) < 3.0; i++)
        {
            l = Math.Clamp(l + (dark ? 0.02 : -0.02), 0, 1);
            c = FromOklab(l, a, b);
        }
        var hover = FromOklab(Math.Clamp(l + (dark ? 0.06 : -0.06), 0, 1), a, b);
        var on = Contrast(c, Colors.White) >= Contrast(c, Colors.Black) ? Colors.White : Colors.Black;
        return new AccentPalette(c, hover, on);
    }

    public static Color ShiftLightness(Color color, double delta)
    {
        var (l, a, b) = ToOklab(color);
        return FromOklab(Math.Clamp(l + delta, 0, 1), a, b);
    }

    public static double Contrast(Color x, Color y)
    {
        double lx = Luminance(x), ly = Luminance(y);
        return (Math.Max(lx, ly) + 0.05) / (Math.Min(lx, ly) + 0.05);
    }

    private static double Luminance(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    private static double Lin(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    private static byte Gam(double v)
    {
        v = Math.Clamp(v, 0, 1);
        double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(s, 0, 1) * 255);
    }

    private static (double L, double A, double B) ToOklab(Color c)
    {
        double r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static Color FromOklab(double L, double A, double B)
    {
        double l = L + 0.3963377774 * A + 0.2158037573 * B;
        double m = L - 0.1055613458 * A - 0.0638541728 * B;
        double s = L - 0.0894841775 * A - 1.2914855480 * B;
        l = l * l * l; m = m * m * m; s = s * s * s;
        return Color.FromRgb(
            Gam(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
            Gam(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
            Gam(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
    }
}
