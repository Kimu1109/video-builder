using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SkiaSharp;

namespace VideoBuilder.Rendering;

/// <summary>このマシンの GPU 1 つ (Linux は EGL のデバイス、Windows は DXGI のアダプタ)。</summary>
/// <param name="Index">一覧での位置。Windows では DXGI の列挙の番号でもある (動画のデコード (D3D11VA) で同じ GPU を使うのに使う)。</param>
/// <param name="Name">Linux は GL_RENDERER (例: "NVIDIA GeForce RTX 3060/PCIe/SSE2", "Mesa Intel(R) UHD Graphics 630 (CFL GT2)")、
/// Windows はアダプタの名前 (例: "NVIDIA GeForce RTX 4070 Laptop GPU", "Intel(R) UHD Graphics")。</param>
/// <param name="DrmDevice">DRM のデバイスファイル (例: /dev/dri/card1)。分からなければ空。</param>
/// <param name="RenderNode">DRM のレンダーノード (例: /dev/dri/renderD129)。動画のデコード (VAAPI) で同じ GPU を使うのに使う。</param>
/// <param name="Luid">Windows: アダプタの LUID (開くときに DXGI のアダプタを探すのに使う)。起動するたびに変わるので、設定には覚えない。</param>
/// <param name="PciId">Windows: "ベンダー:デバイス:サブシステム" の ID (例: "10DE:2860:3FC817AA")。設定に覚える ID に使う。</param>
public sealed record GpuAdapter(int Index, string Name, string Vendor, string DrmDevice, string RenderNode, bool IsSoftware, long Luid = 0, string PciId = "")
{
    /// <summary>設定に覚える ID。同じ型番の GPU が 2 つあっても区別できるよう、分かればデバイスファイル (Windows は PCI の ID) を付ける。</summary>
    public string Id => DrmDevice.Length > 0 ? $"{Name}|{DrmDevice}" : PciId.Length > 0 ? $"{Name}|{PciId}" : Name;

    public bool IsNvidia => Has("NVIDIA") || Has("nouveau") || Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || Vendor.Contains("nouveau", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Qualcomm の GPU (Snapdragon の Adreno)。Windows on ARM の PC の GPU は、ほぼこれ。
    /// Windows はアダプタの名前 "Qualcomm(R) Adreno(TM) X1-85 GPU" (ベンダー 0x5143)、Linux は Mesa の freedreno ("FD750" など) か "Adreno"。
    /// </summary>
    public bool IsQualcomm => Has("Adreno") || Vendor.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) || Vendor.Contains("freedreno", StringComparison.OrdinalIgnoreCase)
                              || Name.StartsWith("FD", StringComparison.Ordinal) && Name.Length > 2 && char.IsAsciiDigit(Name[2]);

    /// <summary>
    /// 自動で選ぶときの優先度 (大きいほど優先)。外付けの GPU を、CPU 内蔵の GPU より優先する。
    /// ソフトウェア (llvmpipe・WARP など) は負。
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
            // Snapdragon の Adreno: CPU 内蔵だが、ARM の PC にはふつうこれしかない。ほかの内蔵 GPU と同じ扱いにはせず、既定の 10 にする
            if (IsQualcomm) return 10;
            if (Has("Intel") || Has("Radeon Graphics") || Has("Radeon(TM) Graphics") || Has("Vega") && Has("Graphics")) return 5;   // CPU 内蔵
            return 10;
        }
    }

    private bool Has(string s) => Name.Contains(s, StringComparison.OrdinalIgnoreCase);

    /// <summary>画面に出す名前 (デバイスファイルが分かれば添える)。</summary>
    public string Label => DrmDevice.Length > 0 ? $"{Name} ({DrmDevice})" : Name;

    /// <summary>GL の GPU の名前 (GL_RENDERER) が、この GPU を指しているか。</summary>
    public bool Matches(string renderer) => GpuContext.SameGpu(Name, renderer);
}

/// <summary>
/// EGL の関数。Linux は libEGL.so.1、Windows は Avalonia が同梱する ANGLE (av_libglesv2.dll。OpenGL ES を Direct3D 11 の上で動かす) から取り出す。
/// ANGLE の DLL は EGL の関数を "EGL_Initialize" のような名前で出しているので、その名前か eglGetProcAddress で探す。
/// </summary>
internal static unsafe class EglApi
{
    public static readonly string? LoadError;
    /// <summary>ANGLE (Windows) の EGL。</summary>
    public static readonly bool IsAngle;

    public static readonly delegate* unmanaged<IntPtr, IntPtr> GetDisplay;
    /// <summary>EGL 1.5 の eglGetPlatformDisplay (属性は intptr)。古い EGL にはないので null のことがある。</summary>
    public static readonly delegate* unmanaged<int, IntPtr, IntPtr*, IntPtr> GetPlatformDisplay;
    public static readonly delegate* unmanaged<IntPtr, int*, int*, int> Initialize;
    public static readonly delegate* unmanaged<int, int> BindApi;
    public static readonly delegate* unmanaged<IntPtr, int*, IntPtr*, int, int*, int> ChooseConfig;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr> CreatePbufferSurface;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, int*, IntPtr> CreateContext;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, int> MakeCurrent;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, int> DestroyContext;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, int> DestroySurface;
    public static readonly delegate* unmanaged<IntPtr, int, IntPtr> QueryString;
    public static readonly delegate* unmanaged<IntPtr, int> Terminate;
    public static readonly delegate* unmanaged<int> GetError;
    private static readonly delegate* unmanaged<byte*, IntPtr> GetProcAddressPtr;

    public static bool Available => LoadError == null;

    static EglApi()
    {
        try
        {
            IntPtr lib;
            if (OperatingSystem.IsWindows())
            {
                // アプリ・テストの出力フォルダ (runtimes/win-x64/native など) から探す
                if (!NativeLibrary.TryLoad("av_libglesv2.dll", typeof(EglApi).Assembly, null, out lib)
                    && !NativeLibrary.TryLoad("av_libglesv2", typeof(EglApi).Assembly, null, out lib))
                {
                    LoadError = "ANGLE (av_libglesv2.dll) が見つかりません";
                    return;
                }
                IsAngle = true;
            }
            else if (OperatingSystem.IsLinux())
            {
                if (!NativeLibrary.TryLoad("libEGL.so.1", out lib) && !NativeLibrary.TryLoad("libEGL.so", out lib))
                {
                    LoadError = "EGL (libEGL.so.1) が見つかりません";
                    return;
                }
            }
            else
            {
                LoadError = "この OS では GPU 描画にまだ対応していません";
                return;
            }

            GetProcAddressPtr = (delegate* unmanaged<byte*, IntPtr>)Export(lib, "eglGetProcAddress", null);
            if (GetProcAddressPtr == null) { LoadError = "eglGetProcAddress がありません"; return; }

            var missing = new List<string>();
            IntPtr Need(string name)
            {
                var p = Export(lib, name, GetProcAddressPtr);
                if (p == IntPtr.Zero) missing.Add(name);
                return p;
            }
            GetDisplay = (delegate* unmanaged<IntPtr, IntPtr>)Need("eglGetDisplay");
            Initialize = (delegate* unmanaged<IntPtr, int*, int*, int>)Need("eglInitialize");
            BindApi = (delegate* unmanaged<int, int>)Need("eglBindAPI");
            ChooseConfig = (delegate* unmanaged<IntPtr, int*, IntPtr*, int, int*, int>)Need("eglChooseConfig");
            CreatePbufferSurface = (delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr>)Need("eglCreatePbufferSurface");
            CreateContext = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int*, IntPtr>)Need("eglCreateContext");
            MakeCurrent = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, int>)Need("eglMakeCurrent");
            DestroyContext = (delegate* unmanaged<IntPtr, IntPtr, int>)Need("eglDestroyContext");
            DestroySurface = (delegate* unmanaged<IntPtr, IntPtr, int>)Need("eglDestroySurface");
            QueryString = (delegate* unmanaged<IntPtr, int, IntPtr>)Need("eglQueryString");
            GetError = (delegate* unmanaged<int>)Need("eglGetError");
            Terminate = (delegate* unmanaged<IntPtr, int>)Need("eglTerminate");
            GetPlatformDisplay = (delegate* unmanaged<int, IntPtr, IntPtr*, IntPtr>)Export(lib, "eglGetPlatformDisplay", GetProcAddressPtr);
            if (missing.Count > 0) LoadError = "EGL の関数がありません: " + string.Join(", ", missing);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
    }

    /// <summary>ライブラリが出している関数を探す。なければ (拡張機能の関数など) eglGetProcAddress で探す。</summary>
    private static IntPtr Export(IntPtr lib, string name, delegate* unmanaged<byte*, IntPtr> getProc)
    {
        if (NativeLibrary.TryGetExport(lib, name, out var p)) return p;
        // ANGLE: eglInitialize → EGL_Initialize
        if (name.StartsWith("egl", StringComparison.Ordinal) && NativeLibrary.TryGetExport(lib, "EGL_" + name[3..], out p)) return p;
        return getProc == null ? IntPtr.Zero : Call(getProc, name);
    }

    private static IntPtr Call(delegate* unmanaged<byte*, IntPtr> getProc, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* b = bytes) return getProc(b);
    }

    /// <summary>EGL・OpenGL ES の関数 (拡張機能のものも) を名前で探す。</summary>
    public static IntPtr Proc(string name) => GetProcAddressPtr == null ? IntPtr.Zero : Call(GetProcAddressPtr, name);

    public static string String(IntPtr display, int name)
    {
        var p = QueryString(display, name);
        return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p) ?? "";
    }
}

/// <summary>Windows の GPU (DXGI のアダプタ) の一覧。</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class DxgiAdapters
{
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(Guid* riid, IntPtr* factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static string VendorName(uint id) => id switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x5143 => "Qualcomm",
        _ => $"0x{id:X4}",
    };

    /// <summary>
    /// DXGI の列挙の順に返す (番号は FFmpeg の D3D11VA のデバイス番号と同じ)。
    /// 同じ ID のものが 2 つあれば、2 つ目からは ID に "#2" などを付けて区別する。
    /// ソフトウェアの GPU (WARP) は、画面のない環境 (CI など) で 2 つ出ることがあるので、1 つだけにする。
    /// </summary>
    public static List<GpuAdapter> Enumerate()
    {
        var list = new List<GpuAdapter>();
        foreach (var a in EnumerateRaw())
        {
            int same = list.Count(x => x.Name == a.Name && x.PciId.Split('#')[0] == a.PciId);
            if (same == 0) list.Add(a);
            else if (!a.IsSoftware) list.Add(a with { PciId = $"{a.PciId}#{same + 1}" });
        }
        return list;
    }

    /// <summary>LUID の GPU (IDXGIAdapter1) を返す (使い終えたら <see cref="Release"/>)。見つからなければ IntPtr.Zero。</summary>
    public static IntPtr Open(long luid)
    {
        IntPtr found = IntPtr.Zero;
        Walk((adapter, d) =>
        {
            if ((((long)d.LuidHigh << 32) | d.LuidLow) != luid) return false;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)adapter)[1])(adapter);   // AddRef
            found = adapter;
            return true;
        });
        return found;
    }

    private static List<GpuAdapter> EnumerateRaw()
    {
        var list = new List<GpuAdapter>();
        Walk((_, d) =>
        {
            string name = new string(d.Description).TrimEnd('\0').Trim();
            bool software = (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || d.VendorId == 0x1414 && d.DeviceId == 0x8C;
            long luid = ((long)d.LuidHigh << 32) | d.LuidLow;
            list.Add(new GpuAdapter(list.Count, name, VendorName(d.VendorId), "", "", software, luid, $"{d.VendorId:X4}:{d.DeviceId:X4}:{d.SubSysId:X8}"));
            return false;
        });
        return list;
    }

    private delegate bool Visitor(IntPtr adapter, AdapterDesc1 desc);

    /// <summary>アダプタを順に visit に渡す。true を返したら終わる。</summary>
    private static void Walk(Visitor visit)
    {
        IntPtr factory;
        var iid = IID_IDXGIFactory1;
        int hr = CreateDXGIFactory1(&iid, &factory);
        if (hr < 0 || factory == IntPtr.Zero) throw new InvalidOperationException($"DXGI を使えません (0x{hr:X8})");
        try
        {
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[12];
            for (uint i = 0; i < 32; i++)
            {
                IntPtr adapter;
                hr = enumAdapters1(factory, i, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND || hr < 0 || adapter == IntPtr.Zero) break;
                bool stop = false;
                try
                {
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                    AdapterDesc1 d;
                    if (getDesc1(adapter, &d) >= 0) stop = visit(adapter, d);
                }
                finally { Release(adapter); }
                if (stop) break;
            }
        }
        finally { Release(factory); }
    }

    public static void Release(IntPtr unknown)
    {
        if (unknown != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)unknown)[2])(unknown);
    }

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>Direct3D 11 のデバイスがある GPU の LUID (分からなければ 0)。</summary>
    public static long LuidOf(IntPtr d3dDevice)
    {
        if (d3dDevice == IntPtr.Zero) return 0;
        IntPtr dxgiDevice, adapter;
        var iid = IID_IDXGIDevice;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)d3dDevice)[0])(d3dDevice, &iid, &dxgiDevice) < 0) return 0;
        try
        {
            // IDXGIDevice::GetAdapter (7) → IDXGIAdapter1::GetDesc1 (10)
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)dxgiDevice)[7])(dxgiDevice, &adapter) < 0) return 0;
            try
            {
                AdapterDesc1 d;
                if (((delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10])(adapter, &d) < 0) return 0;
                return ((long)d.LuidHigh << 32) | d.LuidLow;
            }
            finally { Release(adapter); }
        }
        finally { Release(dxgiDevice); }
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* featureLevels, uint levels, uint sdkVersion,
        IntPtr* device, int* featureLevel, IntPtr* immediateContext);

    private const int D3D_DRIVER_TYPE_UNKNOWN = 0, D3D_DRIVER_TYPE_HARDWARE = 1, D3D_DRIVER_TYPE_WARP = 5;
    private const int E_INVALIDARG = unchecked((int)0x80070057);

    /// <summary>
    /// Direct3D 11 のデバイスを作る (使い終えたら <see cref="Release"/>)。adapter が IntPtr.Zero なら Windows が選ぶ GPU。
    /// ANGLE の OpenGL ES 3 には機能レベル 10_0 以上が要る。
    /// </summary>
    public static IntPtr CreateDevice(IntPtr adapter, out string? error)
    {
        error = null;
        int* levels = stackalloc[] { 0xB100, 0xB000, 0xA100, 0xA000 };   // 11_1, 11_0, 10_1, 10_0
        int driver = adapter == IntPtr.Zero ? D3D_DRIVER_TYPE_HARDWARE : D3D_DRIVER_TYPE_UNKNOWN;
        IntPtr device;
        int got;
        int hr = D3D11CreateDevice(adapter, driver, IntPtr.Zero, 0, levels, 4, 7, &device, &got, null);
        // 古い Windows (D3D 11.0 のランタイム) は 11_1 を知らないので、外して試し直す
        if (hr == E_INVALIDARG) hr = D3D11CreateDevice(adapter, driver, IntPtr.Zero, 0, levels + 1, 3, 7, &device, &got, null);
        // GPU を指定していなくて、ハードウェアで作れなければ (GPU のない環境)、ソフトウェア (WARP) で作る
        if ((hr < 0 || device == IntPtr.Zero) && adapter == IntPtr.Zero)
            hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, 0, levels + 1, 3, 7, &device, &got, null);
        if (hr < 0 || device == IntPtr.Zero) { error = $"Direct3D 11 のデバイスを作れません (0x{hr:X8})"; return IntPtr.Zero; }
        return device;
    }
}

/// <summary>
/// 画面に出さない (オフスクリーンの) OpenGL ES コンテキストと、その上の Skia GPU コンテキスト。
/// 作ったスレッドでだけ使う。Linux は EGL、Windows は ANGLE (Direct3D 11 の上の OpenGL ES) で作る。
/// <para>
/// GPU が複数ある (CPU 内蔵 + 外付けなど) ときは、<see cref="PreferredAdapter"/> で選んだもの、なければ外付けを優先して使う。
/// 何もしないと、デスクトップの表示に使っている GPU (多くは CPU 内蔵) になってしまう。
/// Linux は EGL のデバイス列挙 (EGL_EXT_device_enumeration / EGL_EXT_platform_device) で、
/// Windows は DXGI で列挙したアダプタで Direct3D 11 のデバイスを作り、ANGLE に渡して (eglCreateDeviceANGLE) 開く。
/// </para>
/// </summary>
public sealed unsafe class GpuContext : IDisposable
{
    private const int EGL_NONE = 0x3038, EGL_SURFACE_TYPE = 0x3033, EGL_PBUFFER_BIT = 1, EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_OPENGL_ES2_BIT = 4, EGL_OPENGL_ES3_BIT = 0x40, EGL_OPENGL_ES_API = 0x30A0;
    private const int EGL_RED_SIZE = 0x3024, EGL_GREEN_SIZE = 0x3023, EGL_BLUE_SIZE = 0x3022, EGL_ALPHA_SIZE = 0x3021;
    private const int EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056, EGL_CONTEXT_CLIENT_VERSION = 0x3098;
    private const int EGL_PLATFORM_SURFACELESS_MESA = 0x31DD, EGL_PLATFORM_DEVICE_EXT = 0x313F, EGL_EXTENSIONS = 0x3055;
    private const int EGL_DRM_DEVICE_FILE_EXT = 0x3233, EGL_DRM_RENDER_NODE_FILE_EXT = 0x3377;
    // ANGLE (EGL_ANGLE_device_d3d)
    private const int EGL_PLATFORM_ANGLE_ANGLE = 0x3202, EGL_PLATFORM_ANGLE_TYPE_ANGLE = 0x3203, EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE = 0x3208;
    private const int EGL_D3D11_DEVICE_ANGLE = 0x33A1;
    private const int GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01;

    private IntPtr _display, _surface, _context;
    private GRGlInterface? _interface;
    // Windows: このコンテキストだけの Direct3D 11 のデバイスと、それを包む ANGLE のデバイス (Dispose で手放す)
    private IntPtr _d3dDevice, _angleDevice;
    private IntPtr _config;

    internal IntPtr Display => _display;
    internal IntPtr Config => _config;
    internal IntPtr D3DDevice => _d3dDevice;

    /// <summary>
    /// Windows: このコンテキストの Direct3D 11 のデバイスがある GPU の LUID (画面の描画と同じ GPU か比べるのに使う)。それ以外は 0。
    /// </summary>
    public long DeviceLuid { get; private set; }

    /// <summary>
    /// Windows: 画面の描画 (Avalonia) と共有できる Direct3D 11 のテクスチャを作り、このコンテキストで描けるようにする。
    /// このコンテキストを有効にしたスレッドで呼ぶ。作れなければ null (error に理由)。
    /// </summary>
    public SharedD3DTexture? CreateSharedD3DTexture(int width, int height, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows() || _d3dDevice == IntPtr.Zero) { error = "Direct3D 11 のデバイスがありません"; return null; }
        var t = SharedD3DTexture.Create(this, width, height, out error);
        // Skia の知らないところで GL の状態 (結び付けたテクスチャ) を変えたので、知らせる
        Context.ResetContext();
        return t;
    }

    public GRContext Context { get; private set; } = null!;
    public string Renderer { get; private set; } = "";
    public string Vendor { get; private set; } = "";
    /// <summary>CPU でエミュレートしている (llvmpipe・WARP など)。この場合は CPU 描画の方が速い。</summary>
    public bool IsSoftware { get; private set; }

    private GpuContext() { }

    /// <summary>
    /// 使う GPU (<see cref="GpuAdapter.Id"/>)。null か空なら自動 (外付けの GPU を優先)。
    /// 見つからない (取り外した) ときも自動になる。環境変数 VIDEO_BUILDER_GPU でも指定できる (名前の一部でもよい)。
    /// </summary>
    public static string? PreferredAdapter { get; set; } = Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU") is { Length: > 0 } v ? v : null;

    /// <summary>このコンテキストの GPU (列挙した GPU を選んで開いたときだけ。デスクトップの GPU で開いたときは null)。</summary>
    public GpuAdapter? Adapter { get; private set; }

    /// <summary>GL_RENDERER が CPU でエミュレートしている GPU (llvmpipe・WARP など) のものか。</summary>
    public static bool IsSoftwareRenderer(string renderer) =>
        new[] { "llvmpipe", "softpipe", "swiftshader", "software", "Basic Render Driver" }.Any(k => renderer.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 2 つの GPU の名前 (GL_RENDERER やアダプタの名前) が同じ GPU を指しているか。後ろの "/PCIe/SSE2" などは比べない。
    /// ANGLE の GL_RENDERER ("ANGLE (NVIDIA, NVIDIA GeForce RTX 4070 Laptop GPU (0x00002860) Direct3D11 vs_5_0 ps_5_0, D3D11)") にも、
    /// アダプタの名前が含まれていれば同じとみなす。
    /// </summary>
    public static bool SameGpu(string a, string b)
    {
        static string Core(string s) => (s.Split('/')[0]).Trim();
        string x = Core(a), y = Core(b);
        return x.Length > 0 && y.Length > 0 && (x.Contains(y, StringComparison.OrdinalIgnoreCase) || y.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly object EnumerateGate = new();
    private static List<(GpuAdapter Adapter, IntPtr Device)>? _adapters;
    private static string? _enumerateError;

    /// <summary>
    /// このマシンの GPU の一覧。Linux では 1 つずつ開いて名前を調べるので少し時間がかかる。結果は覚えておく。
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
            try
            {
                if (OperatingSystem.IsWindows()) EnumerateWindows(_adapters);
                else if (OperatingSystem.IsLinux()) EnumerateLinux(_adapters);
                else _enumerateError = "この OS では GPU 描画にまだ対応していません";
                if (_enumerateError == null && _adapters.Count == 0) _enumerateError = "開ける GPU がありません";
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                _enumerateError = ex.Message;
            }
            return _adapters;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnumerateWindows(List<(GpuAdapter Adapter, IntPtr Device)> result)
    {
        // DXGI の名前で十分なので、ここでは開かない (開くのは使うときだけ)
        foreach (var a in DxgiAdapters.Enumerate()) result.Add((a, IntPtr.Zero));
    }

    private static void EnumerateLinux(List<(GpuAdapter Adapter, IntPtr Device)> result)
    {
        if (!EglApi.Available) { _enumerateError = EglApi.LoadError; return; }
        var ext = EglApi.String(IntPtr.Zero, EGL_EXTENSIONS);
        if (!ext.Contains("EGL_EXT_device_enumeration") && !ext.Contains("EGL_EXT_device_base") || !ext.Contains("EGL_EXT_platform_device"))
        {
            _enumerateError = "GPU を 1 つずつ選ぶ機能 (EGL_EXT_platform_device) がありません";
            return;
        }
        var queryDevices = (delegate* unmanaged<int, IntPtr*, int*, int>)EglApi.Proc("eglQueryDevicesEXT");
        var queryString = (delegate* unmanaged<IntPtr, int, IntPtr>)EglApi.Proc("eglQueryDeviceStringEXT");
        if (queryDevices == null || queryString == null || EglApi.Proc("eglGetPlatformDisplayEXT") == IntPtr.Zero) { _enumerateError = "EGL の関数がありません"; return; }
        string Str(IntPtr device, int name) { var p = queryString(device, name); return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p) ?? ""; }
        var devices = new IntPtr[16];
        int count;
        fixed (IntPtr* d = devices) if (queryDevices(devices.Length, d, &count) == 0) { _enumerateError = "GPU を列挙できません"; return; }
        for (int i = 0; i < count; i++)
        {
            var device = devices[i];
            string devExt = Str(device, EGL_EXTENSIONS);
            string drm = devExt.Contains("EGL_EXT_device_drm") ? Str(device, EGL_DRM_DEVICE_FILE_EXT) : "";
            string render = devExt.Contains("EGL_EXT_device_drm_render_node") ? Str(device, EGL_DRM_RENDER_NODE_FILE_EXT) : "";
            if (render.Length == 0) render = RenderNodeFor(drm);   // 古いドライバ: sysfs から探す
            // 名前 (GL_RENDERER) は、実際にコンテキストを作って調べる
            using var ctx = OpenDisplay(PlatformDisplayExt(EGL_PLATFORM_DEVICE_EXT, device, null), out _);
            if (ctx == null) continue;
            bool software = ctx.IsSoftware || devExt.Contains("EGL_MESA_device_software");
            result.Add((new GpuAdapter(result.Count, ctx.Renderer, ctx.Vendor, drm, File.Exists(render) ? render : "", software), device));
        }
        var kept = WithoutDisplayOnlyDuplicates(result.Select(x => x.Adapter).ToList());
        var devicesOf = result.ToDictionary(x => x.Adapter.Index, x => x.Device);
        result.Clear();
        foreach (var a in kept) result.Add((a with { Index = result.Count }, devicesOf[a.Index]));
    }

    /// <summary>
    /// DRM のデバイスファイル (/dev/dri/cardN) に対応するレンダーノード (/dev/dri/renderD*) を探す。なければ空。
    /// sysfs の同じ装置の下 (/sys/class/drm/cardN/device/drm/) にあるレンダーノードを使う。
    /// ARM の SoC では、画面を出す装置 (vc4・rockchip-drm など) と GPU (v3d・panfrost など) が別の DRM デバイスなので、
    /// 「card N ↔ renderD(128+N)」の番号の対応は当てにならない。sysfs が読めないときだけ、番号の対応で推測する。
    /// </summary>
    public static string RenderNodeFor(string drmDevice, string sysClassDrm = "/sys/class/drm")
    {
        const string prefix = "/dev/dri/card";
        if (!drmDevice.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(drmDevice[prefix.Length..], out int card)) return "";
        var dir = Path.Combine(sysClassDrm, $"card{card}", "device", "drm");
        try
        {
            if (Directory.Exists(dir))
            {
                var node = Directory.EnumerateFileSystemEntries(dir, "renderD*").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
                return node == null ? "" : $"/dev/dri/{node}";   // 画面を出すだけの装置にはレンダーノードがない
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return $"/dev/dri/renderD{128 + card}";   // sysfs が読めない: 多くの PC では card N ↔ renderD(128+N)
    }

    /// <summary>
    /// 画面を出すだけの DRM デバイス (レンダーノードがない) で開いたものが、ほかの GPU と同じ GPU で描いていれば一覧から外す。
    /// ARM の SoC では、画面を出す装置 (例: vc4) の EGL のデバイスも、実際には GPU (例: V3D) で描くので、同じ名前が 2 つ並んでしまうため。
    /// </summary>
    public static List<GpuAdapter> WithoutDisplayOnlyDuplicates(List<GpuAdapter> adapters) =>
        adapters.Where(a => a.RenderNode.Length > 0 || a.DrmDevice.Length == 0 || a.IsSoftware
                            || !adapters.Any(b => b != a && b.RenderNode.Length > 0 && !b.IsSoftware && SameGpu(a.Name, b.Name))).ToList();

    /// <summary>eglGetPlatformDisplayEXT (属性は int)。</summary>
    private static IntPtr PlatformDisplayExt(int platform, IntPtr native, int[]? attribs)
    {
        var f = (delegate* unmanaged<int, IntPtr, int*, IntPtr>)EglApi.Proc("eglGetPlatformDisplayEXT");
        if (f == null) return IntPtr.Zero;
        fixed (int* a = attribs) return f(platform, native, a);
    }

    /// <summary>EGL のディスプレイを初期化して、その上にコンテキストを作る。</summary>
    private static GpuContext? OpenDisplay(IntPtr display, out string reason)
    {
        reason = "";
        if (display == IntPtr.Zero) { reason = "ディスプレイなし"; return null; }
        int major, minor;
        if (EglApi.Initialize(display, &major, &minor) == 0) { reason = $"初期化に失敗 (0x{EglApi.GetError():X4})"; return null; }
        var ctx = new GpuContext { _display = display };
        if (ctx.Setup(out reason)) return ctx;
        ctx.Dispose();
        return null;
    }

    /// <summary>
    /// Windows: ANGLE (Direct3D 11) で開く。adapter を渡せば、その GPU を使う。null なら Windows が選ぶ GPU。
    /// <para>
    /// コンテキストごとに自分の Direct3D 11 のデバイスを作って ANGLE に渡し (eglCreateDeviceANGLE)、そのデバイスのディスプレイで開く
    /// (Avalonia の画面の描画と同じやり方)。ANGLE の既定のディスプレイは、同じ設定なら同じ Direct3D 11 のデバイスを使い回すので、
    /// プレビューと出力のように別々のスレッドで同時に描くと壊れる (CI で、Skia の flush の中で落ちた)。
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static GpuContext? OpenAngle(GpuAdapter? adapter, out string reason)
    {
        reason = "";
        var createDevice = (delegate* unmanaged<int, IntPtr, IntPtr*, IntPtr>)EglApi.Proc("eglCreateDeviceANGLE");
        if (createDevice == null) { reason = "eglCreateDeviceANGLE がありません"; return null; }
        IntPtr dxgi = IntPtr.Zero;
        if (adapter != null)
        {
            dxgi = DxgiAdapters.Open(adapter.Luid);
            if (dxgi == IntPtr.Zero) { reason = "GPU が見つかりません (取り外された?)"; return null; }
        }
        IntPtr d3d;
        string? error;
        try { d3d = DxgiAdapters.CreateDevice(dxgi, out error); }
        finally { DxgiAdapters.Release(dxgi); }
        if (d3d == IntPtr.Zero) { reason = error ?? "Direct3D 11 のデバイスを作れません"; return null; }
        var angleDevice = createDevice(EGL_D3D11_DEVICE_ANGLE, d3d, null);
        if (angleDevice == IntPtr.Zero)
        {
            DxgiAdapters.Release(d3d);
            reason = $"eglCreateDeviceANGLE に失敗 (0x{EglApi.GetError():X4})";
            return null;
        }
        var display = PlatformDisplayExt(EGL_PLATFORM_DEVICE_EXT, angleDevice, null);
        int major, minor;
        if (display == IntPtr.Zero || EglApi.Initialize(display, &major, &minor) == 0)
        {
            reason = display == IntPtr.Zero ? "ディスプレイなし" : $"初期化に失敗 (0x{EglApi.GetError():X4})";
            ReleaseAngle(display, angleDevice, d3d);
            return null;
        }
        var ctx = new GpuContext { _display = display, _d3dDevice = d3d, _angleDevice = angleDevice, Adapter = adapter, DeviceLuid = DxgiAdapters.LuidOf(d3d) };
        if (ctx.Setup(out reason)) return ctx;
        ctx.Dispose();
        return null;
    }

    private static void ReleaseAngle(IntPtr display, IntPtr angleDevice, IntPtr d3d)
    {
        // このディスプレイはこのコンテキストだけのものなので、終わらせてよい
        if (display != IntPtr.Zero) EglApi.Terminate(display);
        var release = (delegate* unmanaged<IntPtr, int>)EglApi.Proc("eglReleaseDeviceANGLE");
        if (angleDevice != IntPtr.Zero && release != null) release(angleDevice);
        if (OperatingSystem.IsWindows()) DxgiAdapters.Release(d3d);
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
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) { error = "この OS では GPU 描画にまだ対応していません"; return null; }
        if (!EglApi.Available) { error = EglApi.LoadError; return null; }
        var errors = new List<string>();

        // 1. 選んだ GPU (自動なら外付けを優先) を開く
        var devices = EnumerateDevices();
        var chosen = ChooseAdapter(devices.Select(x => x.Adapter).ToList(), PreferredAdapter);
        // ソフトウェアしか見つからず、選んでもいなければ、これまでどおりデスクトップの GPU から試す
        if (chosen is { IsSoftware: true } && string.IsNullOrEmpty(PreferredAdapter)) chosen = null;
        if (chosen != null)
        {
            try
            {
                string reason;
                GpuContext? ctx;
                if (EglApi.IsAngle && OperatingSystem.IsWindows()) ctx = OpenAngle(chosen, out reason);
                else
                {
                    var device = devices.First(x => x.Adapter == chosen).Device;
                    ctx = OpenDisplay(PlatformDisplayExt(EGL_PLATFORM_DEVICE_EXT, device, null), out reason);
                    if (ctx != null) ctx.Adapter = chosen;
                }
                if (ctx != null) return ctx;
                errors.Add($"{chosen.Name}: {reason}");
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                errors.Add($"{chosen.Name}: {ex.Message}");
            }
        }
        else if (_enumerateError != null) errors.Add(_enumerateError);

        // 2. 今のデスクトップの GPU (Windows は Windows が選ぶ GPU、Linux は X11 / Wayland)  3. 画面なしの Mesa (Linux)
        foreach (var source in EglApi.IsAngle ? new[] { "default" } : new[] { "default", "surfaceless" })
        {
            try
            {
                string reason;
                GpuContext? ctx;
                if (EglApi.IsAngle && OperatingSystem.IsWindows()) ctx = OpenAngle(null, out reason);
                else if (source == "default") ctx = OpenDisplay(EglApi.GetDisplay(IntPtr.Zero), out reason);
                else if (EglApi.GetPlatformDisplay != null) ctx = OpenDisplay(EglApi.GetPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, IntPtr.Zero, null), out reason);
                else { ctx = null; reason = "eglGetPlatformDisplay がありません"; }
                if (ctx != null) return ctx;
                errors.Add($"{source}: {reason}");
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
        EglApi.BindApi(EGL_OPENGL_ES_API);
        // pbuffer が使えない GPU (デバイス列挙で開いたものなど) もあるので、画面なしの設定も試す
        foreach (var (bit, version, pbuffer) in new[] { (EGL_OPENGL_ES3_BIT, 3, true), (EGL_OPENGL_ES3_BIT, 3, false), (EGL_OPENGL_ES2_BIT, 2, true), (EGL_OPENGL_ES2_BIT, 2, false) })
        {
            var attribs = new List<int> { EGL_RENDERABLE_TYPE, bit, EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8 };
            if (pbuffer) attribs.InsertRange(0, new[] { EGL_SURFACE_TYPE, EGL_PBUFFER_BIT });
            attribs.Add(EGL_NONE);
            IntPtr config;
            int count;
            fixed (int* a = attribs.ToArray())
                if (EglApi.ChooseConfig(_display, a, &config, 1, &count) == 0 || count == 0) { reason = "使える設定がありません"; continue; }
            int* contextAttribs = stackalloc[] { EGL_CONTEXT_CLIENT_VERSION, version, EGL_NONE };
            _context = EglApi.CreateContext(_display, config, IntPtr.Zero, contextAttribs);
            _config = config;
            if (_context == IntPtr.Zero) { reason = $"OpenGL ES {version} のコンテキストを作れません"; continue; }
            int* surfaceAttribs = stackalloc[] { EGL_WIDTH, 1, EGL_HEIGHT, 1, EGL_NONE };
            _surface = pbuffer ? EglApi.CreatePbufferSurface(_display, config, surfaceAttribs) : IntPtr.Zero;
            if (_surface == IntPtr.Zero || EglApi.MakeCurrent(_display, _surface, _surface, _context) == 0)
            {
                // 描くのは Skia が作るオフスクリーンの絵なので、サーフェスなしで有効にできればよい (EGL_KHR_surfaceless_context)
                if (_surface != IntPtr.Zero) { EglApi.DestroySurface(_display, _surface); _surface = IntPtr.Zero; }
                if (EglApi.MakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, _context) == 0)
                {
                    reason = "コンテキストを有効にできません";
                    EglApi.DestroyContext(_display, _context); _context = IntPtr.Zero;
                    continue;
                }
            }
            var getString = (delegate* unmanaged<int, IntPtr>)EglApi.Proc("glGetString");
            if (getString != null)
            {
                Renderer = Marshal.PtrToStringAnsi(getString(GL_RENDERER)) ?? "";
                Vendor = Marshal.PtrToStringAnsi(getString(GL_VENDOR)) ?? "";
            }
            IsSoftware = IsSoftwareRenderer(Renderer);
            _interface = GRGlInterface.CreateGles(name => EglApi.Proc(name));
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
        EglApi.MakeCurrent(_display, _surface, _surface, _context);
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
            IsSoftware = IsSoftwareRenderer(renderer),
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
            EglApi.MakeCurrent(_display, _surface, _surface, _context);
            Context?.Dispose();
            _interface?.Dispose();
            EglApi.MakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            EglApi.DestroyContext(_display, _context);
        }
        if (_surface != IntPtr.Zero) EglApi.DestroySurface(_display, _surface);
        // Windows: ディスプレイも Direct3D 11 のデバイスも、このコンテキストだけのものなので手放す
        if (_d3dDevice != IntPtr.Zero) ReleaseAngle(_display, _angleDevice, _d3dDevice);
        // Linux では eglTerminate は呼ばない: 同じディスプレイを Avalonia や別スレッドのコンテキストが使っていることがある
        _display = _context = _surface = _d3dDevice = _angleDevice = IntPtr.Zero;
    }
}
