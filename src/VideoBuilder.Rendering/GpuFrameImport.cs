using System.Runtime.InteropServices;
using VideoBuilder.Media;

namespace VideoBuilder.Rendering;

/// <summary>
/// DMA-BUF (VAAPI のフレームなど) を EGL の画像として取り込み、GL のテクスチャにする (EGL_EXT_image_dma_buf_import。設計書 22.58)。
/// Linux の EGL で作った GpuContext でだけ使える (画面と共有する GLX のコンテキストや、Windows の ANGLE では使えない)。
/// 作ったスレッド (GpuContext を有効にしたスレッド) からだけ使う。
/// </summary>
internal sealed unsafe class EglDmaBuf
{
    private const int EGL_NONE = 0x3038, EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056, EGL_EXTENSIONS = 0x3055;
    private const int EGL_LINUX_DMA_BUF_EXT = 0x3270, EGL_LINUX_DRM_FOURCC_EXT = 0x3271;
    private const int EGL_DMA_BUF_PLANE0_FD_EXT = 0x3272, EGL_DMA_BUF_PLANE0_OFFSET_EXT = 0x3273, EGL_DMA_BUF_PLANE0_PITCH_EXT = 0x3274;
    private const int EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT = 0x3443, EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT = 0x3444;

    private readonly IntPtr _display;
    private readonly delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int*, IntPtr> _createImage;
    private readonly delegate* unmanaged<IntPtr, IntPtr, int> _destroyImage;
    private readonly delegate* unmanaged<uint, IntPtr, void> _targetTexture;
    private readonly bool _modifiers;

    private EglDmaBuf(IntPtr display, IntPtr create, IntPtr destroy, IntPtr target, bool modifiers)
    {
        _display = display;
        _createImage = (delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int*, IntPtr>)create;
        _destroyImage = (delegate* unmanaged<IntPtr, IntPtr, int>)destroy;
        _targetTexture = (delegate* unmanaged<uint, IntPtr, void>)target;
        _modifiers = modifiers;
    }

    /// <summary>使えなければ null (error に理由)。</summary>
    public static EglDmaBuf? Create(GpuContext gpu, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsLinux() || gpu.IsExternal || gpu.Display == IntPtr.Zero) { error = "EGL で作った GPU コンテキストではありません"; return null; }
        var ext = EglApi.String(gpu.Display, EGL_EXTENSIONS);
        if (!ext.Contains("EGL_EXT_image_dma_buf_import")) { error = "EGL_EXT_image_dma_buf_import がありません"; return null; }
        var create = EglApi.Proc("eglCreateImageKHR");
        var destroy = EglApi.Proc("eglDestroyImageKHR");
        var target = EglApi.Proc("glEGLImageTargetTexture2DOES");
        if (create == IntPtr.Zero || destroy == IntPtr.Zero || target == IntPtr.Zero) { error = "EGL の画像の関数がありません"; return null; }
        return new EglDmaBuf(gpu.Display, create, destroy, target, ext.Contains("EGL_EXT_image_dma_buf_import_modifiers"));
    }

    /// <summary>1 つの層を EGL の画像にする。作れなければ IntPtr.Zero。</summary>
    public IntPtr Import(in DmaBufLayer layer)
    {
        var a = new List<int>
        {
            EGL_WIDTH, layer.Width, EGL_HEIGHT, layer.Height, EGL_LINUX_DRM_FOURCC_EXT, (int)layer.DrmFormat,
            EGL_DMA_BUF_PLANE0_FD_EXT, layer.Fd, EGL_DMA_BUF_PLANE0_OFFSET_EXT, (int)layer.Offset, EGL_DMA_BUF_PLANE0_PITCH_EXT, (int)layer.Pitch,
        };
        if (layer.Modifier != HardwareFrame.DrmFormatModInvalid)
        {
            // タイル化された並び (Intel の Y タイルなど) は、並び方を伝えないと正しく読めない
            if (!_modifiers && layer.Modifier != 0) return IntPtr.Zero;
            if (_modifiers)
            {
                a.AddRange(new[] { EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT, (int)(uint)layer.Modifier });
                a.AddRange(new[] { EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT, (int)(uint)(layer.Modifier >> 32) });
            }
        }
        a.Add(EGL_NONE);
        fixed (int* p = a.ToArray()) return _createImage(_display, IntPtr.Zero, EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, p);
    }

    /// <summary>今結び付けている GL_TEXTURE_2D の中身を、EGL の画像にする。</summary>
    public void BindToTexture(IntPtr image) => _targetTexture(GlFunctions.TEXTURE_2D, image);

    public void Destroy(IntPtr image)
    {
        if (image != IntPtr.Zero) _destroyImage(_display, image);
    }
}

/// <summary>
/// CUDA と GL の相互運用: NVDEC (CUDA) のフレームを、GPU の中で GL のテクスチャへ写す (設計書 22.58)。
/// GL のコンテキストが NVIDIA の GPU のものであること。作ったスレッドからだけ使う。
/// </summary>
internal sealed unsafe class CudaGl : IDisposable
{
    private const int CU_GRAPHICS_REGISTER_FLAGS_WRITE_DISCARD = 2;
    private const int CU_MEMORYTYPE_DEVICE = 2, CU_MEMORYTYPE_ARRAY = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Memcpy2D
    {
        public nuint SrcXInBytes, SrcY;
        public int SrcMemoryType;
        public IntPtr SrcHost;
        public ulong SrcDevice;
        public IntPtr SrcArray;
        public nuint SrcPitch;
        public nuint DstXInBytes, DstY;
        public int DstMemoryType;
        public IntPtr DstHost;
        public ulong DstDevice;
        public IntPtr DstArray;
        public nuint DstPitch;
        public nuint WidthInBytes, Height;
    }

    private static IntPtr _lib;
    private static string? _loadError;
    private static readonly object Gate = new();
    private static delegate* unmanaged<IntPtr, int> _push;
    private static delegate* unmanaged<IntPtr*, int> _pop;
    private static delegate* unmanaged<IntPtr*, uint, uint, uint, int> _register;
    private static delegate* unmanaged<IntPtr, int> _unregister;
    private static delegate* unmanaged<uint, IntPtr*, IntPtr, int> _map, _unmap;
    private static delegate* unmanaged<IntPtr*, IntPtr, uint, uint, int> _array;
    private static delegate* unmanaged<Memcpy2D*, IntPtr, int> _copy;

    // テクスチャごとの登録 (テクスチャを作り直したら登録し直す)
    private readonly Dictionary<uint, IntPtr> _registered = new();
    private IntPtr _context;

    private static bool Load(out string? error)
    {
        lock (Gate)
        {
            if (_lib != IntPtr.Zero) { error = null; return true; }
            if (_loadError != null) { error = _loadError; return false; }
            if (!NativeLibrary.TryLoad("libcuda.so.1", out var lib)) { error = _loadError = "libcuda.so.1 がありません"; return false; }
            IntPtr F(string name) => NativeLibrary.TryGetExport(lib, name, out var p) ? p : IntPtr.Zero;
            var names = new[] { "cuCtxPushCurrent_v2", "cuCtxPopCurrent_v2", "cuGraphicsGLRegisterImage", "cuGraphicsUnregisterResource",
                "cuGraphicsMapResources", "cuGraphicsUnmapResources", "cuGraphicsSubResourceGetMappedArray", "cuMemcpy2DAsync_v2" };
            var p = names.Select(F).ToArray();
            var missing = names.Where((_, i) => p[i] == IntPtr.Zero).ToList();
            if (missing.Count > 0) { error = _loadError = "CUDA の関数がありません: " + string.Join(", ", missing); return false; }
            _push = (delegate* unmanaged<IntPtr, int>)p[0];
            _pop = (delegate* unmanaged<IntPtr*, int>)p[1];
            _register = (delegate* unmanaged<IntPtr*, uint, uint, uint, int>)p[2];
            _unregister = (delegate* unmanaged<IntPtr, int>)p[3];
            _map = (delegate* unmanaged<uint, IntPtr*, IntPtr, int>)p[4];
            _unmap = (delegate* unmanaged<uint, IntPtr*, IntPtr, int>)p[5];
            _array = (delegate* unmanaged<IntPtr*, IntPtr, uint, uint, int>)p[6];
            _copy = (delegate* unmanaged<Memcpy2D*, IntPtr, int>)p[7];
            _lib = lib;
            error = null;
            return true;
        }
    }

    /// <summary>使えなければ null (error に理由)。</summary>
    public static CudaGl? Create(GpuContext gpu, out string? error)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) { error = "この OS では使えません"; return null; }
        if (!gpu.Renderer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) { error = "GL のコンテキストが NVIDIA の GPU ではありません"; return null; }
        if (!OperatingSystem.IsLinux()) { error = "Linux でだけ使う"; return null; }
        if (!Load(out error)) return null;
        return new CudaGl();
    }

    /// <summary>
    /// CUDA のフレームの面を、GL のテクスチャ (同じ大きさの R8 か RG8) へ写す。
    /// 写す命令はフレームのストリームに出し、GL がテクスチャを使う前に終わる (cuGraphicsUnmapResources が保証する)。失敗したら CUDA のエラー番号。
    /// </summary>
    public int Copy(HardwareFrame frame, (uint Texture, int WidthBytes, int Height)[] planes)
    {
        var (ctx, stream) = frame.CudaContext;
        if (ctx == IntPtr.Zero) return -1;
        int r = _push(ctx);
        if (r != 0) return r;
        try
        {
            if (_context != ctx)
            {
                // 別の CUDA のコンテキスト (デバイスを開き直したなど) になったら、登録し直す
                foreach (var res in _registered.Values) _unregister(res);
                _registered.Clear();
                _context = ctx;
            }
            var resources = stackalloc IntPtr[planes.Length];
            for (int i = 0; i < planes.Length; i++)
            {
                if (!_registered.TryGetValue(planes[i].Texture, out var res))
                {
                    r = _register(&res, planes[i].Texture, GlFunctions.TEXTURE_2D, CU_GRAPHICS_REGISTER_FLAGS_WRITE_DISCARD);
                    if (r != 0) return r;
                    _registered[planes[i].Texture] = res;
                }
                resources[i] = res;
            }
            r = _map((uint)planes.Length, resources, stream);
            if (r != 0) return r;
            try
            {
                for (int i = 0; i < planes.Length; i++)
                {
                    IntPtr array;
                    r = _array(&array, resources[i], 0, 0);
                    if (r != 0) return r;
                    var (src, pitch) = frame.CudaPlane(i);
                    var c = new Memcpy2D
                    {
                        SrcMemoryType = CU_MEMORYTYPE_DEVICE, SrcDevice = src, SrcPitch = (nuint)pitch,
                        DstMemoryType = CU_MEMORYTYPE_ARRAY, DstArray = array,
                        WidthInBytes = (nuint)planes[i].WidthBytes, Height = (nuint)planes[i].Height,
                    };
                    r = _copy(&c, stream);
                    if (r != 0) return r;
                }
            }
            finally { _unmap((uint)planes.Length, resources, stream); }
            return 0;
        }
        finally { IntPtr popped; _pop(&popped); }
    }

    /// <summary>テクスチャを消す前に呼ぶ (登録を外す)。</summary>
    public void Forget(uint texture)
    {
        if (!_registered.Remove(texture, out var res)) return;
        if (_push(_context) == 0) { _unregister(res); IntPtr p; _pop(&p); }
    }

    public void Dispose()
    {
        if (_registered.Count == 0) return;
        if (_push(_context) == 0)
        {
            foreach (var res in _registered.Values) _unregister(res);
            IntPtr p; _pop(&p);
        }
        _registered.Clear();
    }
}
