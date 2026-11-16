using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace VideoBuilder.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // 同梱の Noto Sans JP: 画面の日本語表示に使う (環境ごとのフォントの差をなくす)
            .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(
                new Uri("fonts:NotoSansJP", UriKind.Absolute),
                new Uri("avares://video-builder/Assets/Fonts", UriKind.Absolute))))
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "fonts:Inter#Inter",
                FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily("fonts:NotoSansJP#Noto Sans JP") } },
            })
            .LogToTrace();
}
