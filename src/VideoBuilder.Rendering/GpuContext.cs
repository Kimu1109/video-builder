using System.Runtime.InteropServices;
using SkiaSharp;

namespace VideoBuilder.Rendering;

/// <summary>
/// 画面に出さない (オフスクリーンの) OpenGL ES コンテキストと、その上の Skia GPU コンテキスト。
/// 作ったスレッドでだけ使う。現在は EGL が使える環境 (Linux) に対応。
/// </summary>
public sealed unsafe class GpuContext : IDisposable
{
    private const string Egl = "libEGL.so.1";

    [DllImport(Egl)] private static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
    [DllImport(Egl)] private static extern IntPtr eglGetPlatformDisplay(int platform, IntPtr nativeDisplay, IntPtr* attribs);
    [DllImport(Egl)] private static extern int eglInitialize(IntPtr display, out int major, out int minor);
    [DllImport(Egl)] private static extern int eglTerminate(IntPtr display);
    [DllImport(Egl)] private static extern int eglBindAPI(int api);
    [DllImport(Egl)] private static extern int eglChooseConfig(IntPtr display, int[] attribs, IntPtr* configs, int size, out int count);
    [DllImport(Egl)] private static extern IntPtr eglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attribs);
    [DllImport(Egl)] private static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attribs);
    [DllImport(Egl)] private static extern int eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
    [DllImport(Egl)] private static extern int eglDestroyContext(IntPtr display, IntPtr context);
    [DllImport(Egl)] private static extern int eglDestroySurface(IntPtr display, IntPtr surface);
    [DllImport(Egl, CharSet = CharSet.Ansi)] private static extern IntPtr eglGetProcAddress(string name);

    private const int EGL_NONE = 0x3038, EGL_SURFACE_TYPE = 0x3033, EGL_PBUFFER_BIT = 1, EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_OPENGL_ES2_BIT = 4, EGL_OPENGL_ES3_BIT = 0x40, EGL_OPENGL_ES_API = 0x30A0;
    private const int EGL_RED_SIZE = 0x3024, EGL_GREEN_SIZE = 0x3023, EGL_BLUE_SIZE = 0x3022, EGL_ALPHA_SIZE = 0x3021;
    private const int EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056, EGL_CONTEXT_CLIENT_VERSION = 0x3098;
    private const int EGL_PLATFORM_SURFACELESS_MESA = 0x31DD;
    private const int GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GlGetString(int name);

    private IntPtr _display, _surface, _context;
    private GRGlInterface? _interface;

    public GRContext Context { get; private set; } = null!;
    public string Renderer { get; private set; } = "";
    public string Vendor { get; private set; } = "";
    /// <summary>CPU でエミュレートしている (llvmpipe など)。この場合は CPU 描画の方が速い。</summary>
    public bool IsSoftware { get; private set; }

    private GpuContext() { }

    /// <summary>GPU コンテキストを作る。使えない環境では null を返し、error に理由を入れる。</summary>
    public static GpuContext? TryCreate(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsLinux()) { error = "この OS では GPU 描画にまだ対応していません"; return null; }
        var errors = new List<string>();
        // 1. 今のデスクトップ (X11 / Wayland) の GPU  2. 画面なしの Mesa
        foreach (var source in new[] { "default", "surfaceless" })
        {
            IntPtr display = IntPtr.Zero;
            try
            {
                display = source == "default" ? eglGetDisplay(IntPtr.Zero) : eglGetPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, IntPtr.Zero, null);
                if (display == IntPtr.Zero) { errors.Add($"{source}: ディスプレイなし"); continue; }
                if (eglInitialize(display, out _, out _) == 0) { errors.Add($"{source}: 初期化に失敗"); continue; }
                var ctx = new GpuContext { _display = display };
                if (ctx.Setup(out var reason)) return ctx;
                errors.Add($"{source}: {reason}");
                ctx.Dispose();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                errors.Add($"{source}: {ex.Message}");
            }
        }
        error = string.Join(" / ", errors);
        return null;
    }

    private bool Setup(out string reason)
    {
        reason = "";
        eglBindAPI(EGL_OPENGL_ES_API);
        foreach (var (bit, version) in new[] { (EGL_OPENGL_ES3_BIT, 3), (EGL_OPENGL_ES2_BIT, 2) })
        {
            int[] configAttribs =
            {
                EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE, bit,
                EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_NONE,
            };
            IntPtr config;
            if (eglChooseConfig(_display, configAttribs, &config, 1, out int count) == 0 || count == 0) { reason = "使える設定がありません"; continue; }
            _context = eglCreateContext(_display, config, IntPtr.Zero, new[] { EGL_CONTEXT_CLIENT_VERSION, version, EGL_NONE });
            if (_context == IntPtr.Zero) { reason = $"OpenGL ES {version} のコンテキストを作れません"; continue; }
            _surface = eglCreatePbufferSurface(_display, config, new[] { EGL_WIDTH, 1, EGL_HEIGHT, 1, EGL_NONE });
            if (eglMakeCurrent(_display, _surface, _surface, _context) == 0)
            {
                reason = "コンテキストを有効にできません";
                eglDestroyContext(_display, _context); _context = IntPtr.Zero;
                continue;
            }
            var getString = eglGetProcAddress("glGetString");
            if (getString != IntPtr.Zero)
            {
                var gl = Marshal.GetDelegateForFunctionPointer<GlGetString>(getString);
                Renderer = Marshal.PtrToStringAnsi(gl(GL_RENDERER)) ?? "";
                Vendor = Marshal.PtrToStringAnsi(gl(GL_VENDOR)) ?? "";
            }
            IsSoftware = new[] { "llvmpipe", "softpipe", "swiftshader", "software" }.Any(k => Renderer.Contains(k, StringComparison.OrdinalIgnoreCase));
            _interface = GRGlInterface.CreateGles(name => eglGetProcAddress(name));
            var gr = _interface == null ? null : GRContext.CreateGl(_interface);
            if (gr == null) { reason = "Skia の GPU コンテキストを作れません"; return false; }
            gr.SetResourceCacheLimit(384L * 1024 * 1024);
            Context = gr;
            return true;
        }
        return false;
    }

    /// <summary>このスレッドで GL を使えるようにする。描画の前に呼ぶ。</summary>
    public void MakeCurrent() => eglMakeCurrent(_display, _surface, _surface, _context);

    public void Dispose()
    {
        if (_display == IntPtr.Zero) return;
        if (_context != IntPtr.Zero)
        {
            eglMakeCurrent(_display, _surface, _surface, _context);
            Context?.Dispose();
            _interface?.Dispose();
            eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            eglDestroyContext(_display, _context);
        }
        if (_surface != IntPtr.Zero) eglDestroySurface(_display, _surface);
        // eglTerminate は呼ばない: 同じディスプレイを Avalonia や別スレッドのコンテキストが使っていることがある
        _display = _context = _surface = IntPtr.Zero;
    }
}
