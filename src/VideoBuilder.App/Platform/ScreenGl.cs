using Avalonia;
using VideoBuilder.App.Services;

namespace VideoBuilder.App.Platform;

/// <summary>
/// Linux (X11): 画面の描画 (Avalonia のコンポジタ) を EGL と GLX のどちらで行うか (設計書 22.62)。
/// <para>
/// 画面と共有するプレビューのコンテキストは、画面の描画と同じ仕組みのものになる。GLX には DMA-BUF を取り込む方法がないので、
/// VAAPI でデコードしたフレームを CPU へ写さずに出すには、画面の描画が EGL である必要がある。
/// 既定は EGL (使えなければ GLX → ソフトウェア)。環境設定の「画面の描画 (Linux)」か、
/// 環境変数 VIDEO_BUILDER_SCREEN_GL=glx / egl で選べる (環境変数が優先)。画面の描画は起動のときにしか決められない。
/// </para>
/// </summary>
internal static class ScreenGl
{
    /// <summary>Avalonia の X11PlatformOptions.RenderingMode (先頭ほど優先)。</summary>
    public static X11RenderingMode[] RenderingModes() => Choose() == ScreenGlMode.Glx
        ? new[] { X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software }
        : new[] { X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software };

    /// <summary>使う仕組み (環境変数 → 設定)。</summary>
    internal static ScreenGlMode Choose()
    {
        var env = Environment.GetEnvironmentVariable("VIDEO_BUILDER_SCREEN_GL");
        if (string.Equals(env, "glx", StringComparison.OrdinalIgnoreCase)) return ScreenGlMode.Glx;
        if (string.Equals(env, "egl", StringComparison.OrdinalIgnoreCase)) return ScreenGlMode.Egl;
        if (!OperatingSystem.IsLinux()) return ScreenGlMode.Egl;
        // この時点ではまだアプリの設定を読んでいない (画面を作る前に呼ばれる) ので、ここで読む
        try { return AppSettings.Load().ScreenGl; } catch { return ScreenGlMode.Egl; }
    }
}
