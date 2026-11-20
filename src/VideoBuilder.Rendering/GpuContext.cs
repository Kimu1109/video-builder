using System.Runtime.InteropServices;
using SkiaSharp;

namespace VideoBuilder.Rendering;

/// <summary>このマシンの GPU 1 つ (EGL のデバイス)。</summary>
/// <param name="Name">GL_RENDERER (例: "NVIDIA GeForce RTX 3060/PCIe/SSE2", "Mesa Intel(R) UHD Graphics 630 (CFL GT2)")。</param>
/// <param name="DrmDevice">DRM のデバイスファイル (例: /dev/dri/card1)。分からなければ空。</param>
/// <param name="RenderNode">DRM のレンダーノード (例: /dev/dri/renderD129)。動画のデコード (VAAPI) で同じ GPU を使うのに使う。</param>
public sealed record GpuAdapter(int Index, string Name, string Vendor, string DrmDevice, string RenderNode, bool IsSoftware)
{
    /// <summary>設定に覚える ID。同じ型番の GPU が 2 つあっても区別できるよう、分かればデバイスファイルを付ける。</summary>
    public string Id => DrmDevice.Length > 0 ? $"{Name}|{DrmDevice}" : Name;

    public bool IsNvidia => Has("NVIDIA") || Has("nouveau") || Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || Vendor.Contains("nouveau", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 自動で選ぶときの優先度 (大きいほど優先)。外付けの GPU を、CPU 内蔵の GPU より優先する。
    /// ソフトウェア (llvmpipe など) は負。
    /// </summary>
    public int Score
    {
        get
        {
            if (IsSoftware) return -1;
            if (Has("nouveau") || Vendor.Contains("nouveau", StringComparison.OrdinalIgnoreCase)) return 15;   // NVIDIA だが、オープンソースのドライバは遅い
            if (IsNvidia) return 30;
            if (Has("Radeon RX") || Has("Radeon Pro") || Has("Radeon VII") || Has("FirePro")) return 20;
            if (Has("Intel") && Has("Arc")) return 20;
            if (Has("Intel") || Has("Radeon Graphics") || Has("Radeon(TM) Graphics") || Has("Vega") && Has("Graphics")) return 5;   // CPU 内蔵
            return 10;
        }
    }

    private bool Has(string s) => Name.Contains(s, StringComparison.OrdinalIgnoreCase);

    /// <summary>画面に出す名前 (デバイスファイルが分かれば添える)。</summary>
    public string Label => DrmDevice.Length > 0 ? $"{Name} ({DrmDevice})" : Name;
}

/// <summary>
/// 画面に出さない (オフスクリーンの) OpenGL ES コンテキストと、その上の Skia GPU コンテキスト。
/// 作ったスレッドでだけ使う。現在は EGL が使える環境 (Linux) に対応。
/// <para>
/// GPU が複数ある (CPU 内蔵 + 外付けなど) ときは、EGL のデバイス列挙 (EGL_EXT_device_enumeration / EGL_EXT_platform_device) で
/// 1 つずつ開けるので、<see cref="PreferredAdapter"/> で選んだもの、なければ外付けを優先して使う。
/// 何もしないと、デスクトップの表示に使っている GPU (多くは CPU 内蔵) になってしまう。
/// </para>
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
    [DllImport(Egl)] private static extern IntPtr eglQueryString(IntPtr display, int name);

    // 拡張機能の関数 (eglGetProcAddress で取り出す)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EglQueryDevices(int max, IntPtr* devices, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr EglQueryDeviceString(IntPtr device, int name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr EglGetPlatformDisplayExt(int platform, IntPtr nativeDisplay, int* attribs);

    private const int EGL_NONE = 0x3038, EGL_SURFACE_TYPE = 0x3033, EGL_PBUFFER_BIT = 1, EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_OPENGL_ES2_BIT = 4, EGL_OPENGL_ES3_BIT = 0x40, EGL_OPENGL_ES_API = 0x30A0;
    private const int EGL_RED_SIZE = 0x3024, EGL_GREEN_SIZE = 0x3023, EGL_BLUE_SIZE = 0x3022, EGL_ALPHA_SIZE = 0x3021;
    private const int EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056, EGL_CONTEXT_CLIENT_VERSION = 0x3098;
    private const int EGL_PLATFORM_SURFACELESS_MESA = 0x31DD, EGL_PLATFORM_DEVICE_EXT = 0x313F, EGL_EXTENSIONS = 0x3055;
    private const int EGL_DRM_DEVICE_FILE_EXT = 0x3233, EGL_DRM_RENDER_NODE_FILE_EXT = 0x3377;
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

    /// <summary>
    /// 使う GPU (<see cref="GpuAdapter.Id"/>)。null か空なら自動 (外付けの GPU を優先)。
    /// 見つからない (取り外した) ときも自動になる。環境変数 VIDEO_BUILDER_GPU でも指定できる (名前の一部でもよい)。
    /// </summary>
    public static string? PreferredAdapter { get; set; } = Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU") is { Length: > 0 } v ? v : null;

    /// <summary>このコンテキストの GPU (デバイス列挙で開いたときだけ。デスクトップの GPU で開いたときは null)。</summary>
    public GpuAdapter? Adapter { get; private set; }

    private static readonly object EnumerateGate = new();
    private static List<(GpuAdapter Adapter, IntPtr Device)>? _adapters;
    private static string? _enumerateError;

    /// <summary>
    /// このマシンの GPU の一覧 (EGL のデバイス列挙)。1 つずつ開いて名前を調べるので少し時間がかかる。結果は覚えておく。
    /// GL のコンテキストを一時的に作るので、画面の描画に使っているスレッドからは呼ばないこと (Task.Run などで呼ぶ)。
    /// </summary>
    public static IReadOnlyList<GpuAdapter> Adapters() => EnumerateDevices().Select(x => x.Adapter).ToList();

    /// <summary>GPU の一覧を作れなかった理由 (作れたら null)。</summary>
    public static string? AdaptersError { get { EnumerateDevices(); return _enumerateError; } }

    private static List<(GpuAdapter Adapter, IntPtr Device)> EnumerateDevices()
    {
        lock (EnumerateGate)
        {
            if (_adapters != null) return _adapters;
            _adapters = new List<(GpuAdapter, IntPtr)>();
            if (!OperatingSystem.IsLinux()) { _enumerateError = "この OS では GPU 描画にまだ対応していません"; return _adapters; }
            try
            {
                var ext = Marshal.PtrToStringAnsi(eglQueryString(IntPtr.Zero, EGL_EXTENSIONS)) ?? "";
                if (!ext.Contains("EGL_EXT_device_enumeration") && !ext.Contains("EGL_EXT_device_base") || !ext.Contains("EGL_EXT_platform_device"))
                {
                    _enumerateError = "GPU を 1 つずつ選ぶ機能 (EGL_EXT_platform_device) がありません";
                    return _adapters;
                }
                var queryDevices = Proc<EglQueryDevices>("eglQueryDevicesEXT");
                var queryString = Proc<EglQueryDeviceString>("eglQueryDeviceStringEXT");
                var getDisplay = Proc<EglGetPlatformDisplayExt>("eglGetPlatformDisplayEXT");
                if (queryDevices == null || queryString == null || getDisplay == null) { _enumerateError = "EGL の関数がありません"; return _adapters; }
                var devices = new IntPtr[16];
                int count;
                fixed (IntPtr* d = devices) if (queryDevices(devices.Length, d, out count) == 0) { _enumerateError = "GPU を列挙できません"; return _adapters; }
                for (int i = 0; i < count; i++)
                {
                    var device = devices[i];
                    string devExt = Marshal.PtrToStringAnsi(queryString(device, EGL_EXTENSIONS)) ?? "";
                    string drm = devExt.Contains("EGL_EXT_device_drm") ? Marshal.PtrToStringAnsi(queryString(device, EGL_DRM_DEVICE_FILE_EXT)) ?? "" : "";
                    string render = devExt.Contains("EGL_EXT_device_drm_render_node") ? Marshal.PtrToStringAnsi(queryString(device, EGL_DRM_RENDER_NODE_FILE_EXT)) ?? "" : "";
                    if (render.Length == 0 && drm.StartsWith("/dev/dri/card") && int.TryParse(drm["/dev/dri/card".Length..], out int card))
                        render = $"/dev/dri/renderD{128 + card}";   // 古いドライバ: 番号の対応から推測する (多くの環境で card N ↔ renderD(128+N))
                    // 名前 (GL_RENDERER) は、実際にコンテキストを作って調べる
                    using var ctx = OpenDevice(getDisplay, device, out _);
                    if (ctx == null) continue;
                    bool software = ctx.IsSoftware || devExt.Contains("EGL_MESA_device_software");
                    _adapters.Add((new GpuAdapter(_adapters.Count, ctx.Renderer, ctx.Vendor, drm, File.Exists(render) ? render : "", software), device));
                }
                if (_adapters.Count == 0) _enumerateError = "開ける GPU がありません";
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _enumerateError = ex.Message;
            }
            return _adapters;
        }
    }

    private static T? Proc<T>(string name) where T : Delegate
    {
        var p = eglGetProcAddress(name);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static GpuContext? OpenDevice(EglGetPlatformDisplayExt getDisplay, IntPtr device, out string reason)
    {
        reason = "";
        var display = getDisplay(EGL_PLATFORM_DEVICE_EXT, device, null);
        if (display == IntPtr.Zero) { reason = "ディスプレイなし"; return null; }
        if (eglInitialize(display, out _, out _) == 0) { reason = "初期化に失敗"; return null; }
        var ctx = new GpuContext { _display = display };
        if (ctx.Setup(out reason)) return ctx;
        ctx.Dispose();
        return null;
    }

    /// <summary>設定 (<see cref="PreferredAdapter"/>) と一覧から、使う GPU を決める。一覧がなければ null。</summary>
    public static GpuAdapter? ChooseAdapter(IReadOnlyList<GpuAdapter> adapters, string? preferred)
    {
        if (!string.IsNullOrEmpty(preferred))
        {
            var exact = adapters.FirstOrDefault(a => a.Id == preferred)
                        ?? adapters.FirstOrDefault(a => a.Name == preferred.Split('|')[0])
                        ?? adapters.FirstOrDefault(a => a.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        // 自動: 外付けを優先。ソフトウェアしかなければ、それを返す (使うかどうかは呼ぶ側が決める)
        return adapters.OrderByDescending(a => a.Score).ThenBy(a => a.Index).FirstOrDefault();
    }

    /// <summary>選んだ GPU (自動ならそのとき選ばれるもの)。一覧が作れなければ null。</summary>
    public static GpuAdapter? SelectedAdapter() => ChooseAdapter(Adapters(), PreferredAdapter);

    /// <summary>GPU コンテキストを作る。使えない環境では null を返し、error に理由を入れる。</summary>
    public static GpuContext? TryCreate(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsLinux()) { error = "この OS では GPU 描画にまだ対応していません"; return null; }
        var errors = new List<string>();

        // 1. 選んだ GPU (自動なら外付けを優先) をデバイス列挙で開く
        var devices = EnumerateDevices();
        var chosen = ChooseAdapter(devices.Select(x => x.Adapter).ToList(), PreferredAdapter);
        // ソフトウェアしか見つからず、選んでもいなければ、これまでどおりデスクトップの GPU から試す
        if (chosen is { IsSoftware: true } && string.IsNullOrEmpty(PreferredAdapter)) chosen = null;
        if (chosen != null)
        {
            var getDisplay = Proc<EglGetPlatformDisplayExt>("eglGetPlatformDisplayEXT");
            if (getDisplay != null)
            {
                var ctx = OpenDevice(getDisplay, devices[chosen.Index].Device, out var reason);
                if (ctx != null) { ctx.Adapter = chosen; return ctx; }
                errors.Add($"{chosen.Name}: {reason}");
            }
        }
        else if (_enumerateError != null) errors.Add(_enumerateError);

        // 2. 今のデスクトップ (X11 / Wayland) の GPU  3. 画面なしの Mesa
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
        // pbuffer が使えない GPU (デバイス列挙で開いたものなど) もあるので、画面なしの設定も試す
        foreach (var (bit, version, pbuffer) in new[] { (EGL_OPENGL_ES3_BIT, 3, true), (EGL_OPENGL_ES3_BIT, 3, false), (EGL_OPENGL_ES2_BIT, 2, true), (EGL_OPENGL_ES2_BIT, 2, false) })
        {
            var attribs = new List<int> { EGL_RENDERABLE_TYPE, bit, EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8 };
            if (pbuffer) attribs.InsertRange(0, new[] { EGL_SURFACE_TYPE, EGL_PBUFFER_BIT });
            attribs.Add(EGL_NONE);
            IntPtr config;
            if (eglChooseConfig(_display, attribs.ToArray(), &config, 1, out int count) == 0 || count == 0) { reason = "使える設定がありません"; continue; }
            _context = eglCreateContext(_display, config, IntPtr.Zero, new[] { EGL_CONTEXT_CLIENT_VERSION, version, EGL_NONE });
            if (_context == IntPtr.Zero) { reason = $"OpenGL ES {version} のコンテキストを作れません"; continue; }
            _surface = pbuffer ? eglCreatePbufferSurface(_display, config, new[] { EGL_WIDTH, 1, EGL_HEIGHT, 1, EGL_NONE }) : IntPtr.Zero;
            if (_surface == IntPtr.Zero || eglMakeCurrent(_display, _surface, _surface, _context) == 0)
            {
                // 描くのは Skia が作るオフスクリーンの絵なので、サーフェスなしで有効にできればよい (EGL_KHR_surfaceless_context)
                if (_surface != IntPtr.Zero) { eglDestroySurface(_display, _surface); _surface = IntPtr.Zero; }
                if (eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, _context) == 0)
                {
                    reason = "コンテキストを有効にできません";
                    eglDestroyContext(_display, _context); _context = IntPtr.Zero;
                    continue;
                }
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
    public void MakeCurrent()
    {
        if (_externalMakeCurrent != null) { _externalMakeCurrent(); return; }
        eglMakeCurrent(_display, _surface, _surface, _context);
    }

    // 外から渡された GL コンテキスト (画面の描画と共有するもの) を使うとき
    private Action? _externalMakeCurrent;
    private Action? _externalRelease;

    /// <summary>
    /// ほかのライブラリ (画面の描画) が作った GL コンテキストを使う。描くテクスチャを画面と共有でき、読み戻さずに表示できる。
    /// makeCurrent は、このスレッドでそのコンテキストを使えるようにする。release は Dispose のときに呼ぶ。
    /// </summary>
    public static GpuContext? FromExternal(Func<string, IntPtr> getProcAddress, bool gles, string renderer, string vendor, Action makeCurrent, Action? release, out string? error)
    {
        error = null;
        makeCurrent();
        var gi = gles ? GRGlInterface.CreateGles(name => getProcAddress(name)) : GRGlInterface.CreateOpenGl(name => getProcAddress(name));
        var gr = gi == null ? null : GRContext.CreateGl(gi);
        if (gr == null)
        {
            gi?.Dispose();
            error = "Skia の GPU コンテキストを作れません";
            return null;
        }
        gr.SetResourceCacheLimit(384L * 1024 * 1024);
        return new GpuContext
        {
            Context = gr, _interface = gi, Renderer = renderer, Vendor = vendor,
            IsSoftware = new[] { "llvmpipe", "softpipe", "swiftshader", "software" }.Any(k => renderer.Contains(k, StringComparison.OrdinalIgnoreCase)),
            _externalMakeCurrent = makeCurrent, _externalRelease = release,
        };
    }

    /// <summary>画面の描画と共有している GL コンテキストか (FromExternal で作ったもの)。</summary>
    public bool IsExternal => _externalMakeCurrent != null;

    public void Dispose()
    {
        if (_externalMakeCurrent != null)
        {
            _externalMakeCurrent();
            Context?.Dispose();
            _interface?.Dispose();
            _externalRelease?.Invoke();
            _externalMakeCurrent = null;
            return;
        }
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
