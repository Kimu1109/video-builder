using System.Runtime.InteropServices;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>
/// デスクトップの OpenGL のコンテキストを EGL で作り、<see cref="GpuContext.FromExternal"/> で包む (テスト用)。
/// Linux で画面の描画 (Avalonia の GLX) と共有するコンテキストはデスクトップの OpenGL なので、その代わりに使う (設計書 22.28)。
/// Mesa なら画面なし (surfaceless) で作れる。作れない環境 (Windows の ANGLE など) では null。
/// </summary>
internal static unsafe class DesktopGlContext
{
    private const int EGL_NONE = 0x3038, EGL_SURFACE_TYPE = 0x3033, EGL_PBUFFER_BIT = 1, EGL_RENDERABLE_TYPE = 0x3040, EGL_OPENGL_BIT = 8;
    private const int EGL_RED_SIZE = 0x3024, EGL_GREEN_SIZE = 0x3023, EGL_BLUE_SIZE = 0x3022, EGL_ALPHA_SIZE = 0x3021;
    private const int EGL_OPENGL_API = 0x30A2, EGL_OPENGL_ES_API = 0x30A0, EGL_PLATFORM_SURFACELESS_MESA = 0x31DD;
    private const int EGL_CONTEXT_MAJOR_VERSION = 0x3098, EGL_CONTEXT_MINOR_VERSION = 0x30FB, EGL_CONTEXT_OPENGL_PROFILE_MASK = 0x30FD;
    private const int EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT = 1, EGL_CONTEXT_OPENGL_COMPATIBILITY_PROFILE_BIT = 2;
    private const int GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01;

    /// <summary>
    /// core が true ならコア プロファイルの OpenGL 3.3、false なら互換プロファイル (版は指定しない。GLX で作るものに近い)。
    /// </summary>
    public static GpuContext? Create(bool core, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsLinux()) { error = "Linux だけ"; return null; }
        if (!EglApi.Available) { error = EglApi.LoadError; return null; }
        if (EglApi.GetPlatformDisplay == null) { error = "eglGetPlatformDisplay がありません"; return null; }
        var display = EglApi.GetPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, IntPtr.Zero, null);
        int major, minor;
        if (display == IntPtr.Zero || EglApi.Initialize(display, &major, &minor) == 0) { error = "画面なしの EGL を開けません"; return null; }
        // GL の API を選ぶのはスレッドごと。描画のスレッドの OpenGL ES の設定を変えないよう、作り終えたら戻す
        if (EglApi.BindApi(EGL_OPENGL_API) == 0) { error = "デスクトップの OpenGL を選べません"; return null; }
        try
        {
            int* attribs = stackalloc[] { EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
                EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_NONE };
            IntPtr config;
            int count;
            if (EglApi.ChooseConfig(display, attribs, &config, 1, &count) == 0 || count == 0) { error = "デスクトップの OpenGL の設定がありません"; return null; }
            var contextAttribs = core
                ? new[] { EGL_CONTEXT_MAJOR_VERSION, 3, EGL_CONTEXT_MINOR_VERSION, 3, EGL_CONTEXT_OPENGL_PROFILE_MASK, EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT, EGL_NONE }
                : new[] { EGL_CONTEXT_OPENGL_PROFILE_MASK, EGL_CONTEXT_OPENGL_COMPATIBILITY_PROFILE_BIT, EGL_NONE };
            IntPtr context;
            fixed (int* a = contextAttribs) context = EglApi.CreateContext(display, config, IntPtr.Zero, a);
            if (context == IntPtr.Zero) { error = $"デスクトップの OpenGL のコンテキストを作れません (core={core})"; return null; }
            void MakeCurrent()
            {
                EglApi.BindApi(EGL_OPENGL_API);
                EglApi.MakeCurrent(display, IntPtr.Zero, IntPtr.Zero, context);
            }
            MakeCurrent();
            var getString = (delegate* unmanaged<int, IntPtr>)EglApi.Proc("glGetString");
            string renderer = getString == null ? "" : Marshal.PtrToStringAnsi(getString(GL_RENDERER)) ?? "";
            string vendor = getString == null ? "" : Marshal.PtrToStringAnsi(getString(GL_VENDOR)) ?? "";
            var gpu = GpuContext.FromExternal(EglApi.Proc, gles: false, renderer, vendor, MakeCurrent, () =>
            {
                EglApi.MakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                EglApi.DestroyContext(display, context);
                EglApi.BindApi(EGL_OPENGL_ES_API);
            }, out error);
            if (gpu == null) EglApi.DestroyContext(display, context);
            return gpu;
        }
        finally { EglApi.BindApi(EGL_OPENGL_ES_API); }
    }
}
