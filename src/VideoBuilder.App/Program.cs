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
            // Windows: 画面の描画も、描画に選んだ GPU で行う (プレビューを読み戻さずに出すため。Platform/ScreenGpu.cs)
            .With(new Win32PlatformOptions { GraphicsAdapterSelectionCallback = Platform.ScreenGpu.Choose })
            // Linux (X11): 画面の描画を EGL で行う (画面と共有するプレビューでも、VAAPI のフレームを DMA-BUF で取り込めるようにする。
            // 設計書 22.62)。使えなければ GLX、それもだめならソフトウェア
            .With(new X11PlatformOptions { RenderingMode = Platform.ScreenGl.RenderingModes() })
            .LogToTrace();
}
