using System.Runtime.InteropServices;
using VideoBuilder.Media;

namespace VideoBuilder.Rendering;

/// <summary>
/// Direct3D 11 の COM の呼び出し (vtable の番号で呼ぶ。Windows だけ)。設計書 22.65。
/// </summary>
internal static unsafe class D3D11
{
    public const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28, DXGI_FORMAT_NV12 = 103, DXGI_FORMAT_P010 = 104;
    public const uint BIND_SHADER_RESOURCE = 0x8, BIND_RENDER_TARGET = 0x20;
    public const uint MISC_SHARED_KEYEDMUTEX = 0x100;
    public const uint USAGE_DEFAULT = 0, USAGE_STAGING = 3;
    public const uint CPU_ACCESS_READ = 0x20000, CPU_ACCESS_WRITE = 0x10000;
    public const int WAIT_TIMEOUT = 0x102, WAIT_ABANDONED = 0x80;

    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_IDXGIResource = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
    public static readonly Guid IID_IDXGIKeyedMutex = new("9d8e1289-d7b3-465f-8126-250e349af85d");
    public static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SubresourceData { public void* SysMem; public uint SysMemPitch, SysMemSlicePitch; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MappedSubresource { public void* Data; public uint RowPitch, DepthPitch; }

    private static IntPtr* Vtbl(IntPtr unknown) => *(IntPtr**)unknown;

    public static int QueryInterface(IntPtr unknown, Guid iid, out IntPtr result)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(unknown)[0])(unknown, &iid, &r);
        result = hr < 0 ? IntPtr.Zero : r;
        return hr;
    }

    public static void AddRef(IntPtr unknown)
    {
        if (unknown != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(unknown)[1])(unknown);
    }

    public static void Release(IntPtr unknown)
    {
        if (unknown != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(unknown)[2])(unknown);
    }

    /// <summary>ID3D11Device::CreateTexture2D (5)。</summary>
    public static int CreateTexture2D(IntPtr device, in Texture2DDesc desc, SubresourceData* initial, out IntPtr texture)
    {
        IntPtr t;
        var d = desc;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, SubresourceData*, IntPtr*, int>)Vtbl(device)[5])(device, &d, initial, &t);
        texture = hr < 0 ? IntPtr.Zero : t;
        return hr;
    }

    /// <summary>ID3D11Device::OpenSharedResource (28)。テクスチャ (ID3D11Texture2D) として開く。</summary>
    public static int OpenSharedTexture(IntPtr device, IntPtr handle, out IntPtr texture)
    {
        IntPtr t;
        var iid = IID_ID3D11Texture2D;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(device)[28])(device, handle, &iid, &t);
        texture = hr < 0 ? IntPtr.Zero : t;
        return hr;
    }

    /// <summary>ID3D11Device::GetImmediateContext (40)。使い終えたら Release。</summary>
    public static IntPtr GetImmediateContext(IntPtr device)
    {
        IntPtr c;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Vtbl(device)[40])(device, &c);
        return c;
    }

    /// <summary>ID3D11DeviceContext::CopySubresourceRegion (46)。</summary>
    public static void CopySubresourceRegion(IntPtr context, IntPtr dst, uint dstSub, IntPtr src, uint srcSub, Box* box) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Vtbl(context)[46])(context, dst, dstSub, 0, 0, 0, src, srcSub, box);

    /// <summary>ID3D11DeviceContext::Map (14)。</summary>
    public static int Map(IntPtr context, IntPtr resource, uint sub, int mapType, out MappedSubresource mapped)
    {
        MappedSubresource m;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSubresource*, int>)Vtbl(context)[14])(context, resource, sub, mapType, 0, &m);
        mapped = m;
        return hr;
    }

    /// <summary>ID3D11DeviceContext::Unmap (15)。</summary>
    public static void Unmap(IntPtr context, IntPtr resource, uint sub) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vtbl(context)[15])(context, resource, sub);

    /// <summary>ID3D11DeviceContext::Flush (111)。</summary>
    public static void Flush(IntPtr context) => ((delegate* unmanaged[Stdcall]<IntPtr, void>)Vtbl(context)[111])(context);

    /// <summary>IDXGIResource::GetSharedHandle (8)。ハンドルは閉じなくてよい (共有のハンドルの古い方式)。</summary>
    public static IntPtr SharedHandle(IntPtr texture)
    {
        if (QueryInterface(texture, IID_IDXGIResource, out var resource) < 0) return IntPtr.Zero;
        try
        {
            IntPtr handle;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(resource)[8])(resource, &handle);
            return hr < 0 ? IntPtr.Zero : handle;
        }
        finally { Release(resource); }
    }

    /// <summary>IDXGIKeyedMutex::AcquireSync (8)。取れたら true。</summary>
    public static bool AcquireSync(IntPtr mutex, ulong key, int timeoutMs)
    {
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)Vtbl(mutex)[8])(mutex, key, (uint)timeoutMs);
        return hr >= 0 && hr != WAIT_TIMEOUT && hr != WAIT_ABANDONED;
    }

    /// <summary>IDXGIKeyedMutex::ReleaseSync (9)。</summary>
    public static void ReleaseSync(IntPtr mutex, ulong key) => ((delegate* unmanaged[Stdcall]<IntPtr, ulong, int>)Vtbl(mutex)[9])(mutex, key);

    /// <summary>
    /// デバイスの即時のコンテキストを、複数のスレッドから使えるようにする (ID3D10Multithread::SetMultithreadProtected (5))。
    /// ANGLE (描画のスレッド) と FFmpeg のエンコーダ (書き出しのスレッド) が同じデバイスを使うときに要る。できなければ false。
    /// </summary>
    public static bool SetMultithreadProtected(IntPtr device)
    {
        if (QueryInterface(device, IID_ID3D10Multithread, out var mt) < 0) return false;
        try
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(mt)[5])(mt, 1);
            return true;
        }
        finally { Release(mt); }
    }
}

/// <summary>
/// Direct3D 11 のテクスチャを ANGLE の EGL の画像にし、GL のテクスチャにする (EGL_ANGLE_image_d3d11_texture)。
/// NV12・P010 は面ごと (EGL_D3D11_TEXTURE_PLANE_ANGLE。Y は R8 / R16、U・V は RG8 / RG16)、テクスチャの配列は位置を指定して取り込む。
/// テクスチャは、ANGLE のコンテキスト (GpuContext) の Direct3D 11 のデバイスのものであること。作ったスレッドからだけ使う。設計書 22.65。
/// </summary>
internal sealed unsafe class D3D11Egl
{
    private const int EGL_NONE = 0x3038, EGL_EXTENSIONS = 0x3055;
    private const int EGL_D3D11_TEXTURE_ANGLE = 0x3484, EGL_D3D11_TEXTURE_PLANE_ANGLE = 0x3492, EGL_D3D11_TEXTURE_ARRAY_SLICE_ANGLE = 0x3493;

    private readonly IntPtr _display;
    private readonly delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int*, IntPtr> _createImage;
    private readonly delegate* unmanaged<IntPtr, IntPtr, int> _destroyImage;
    private readonly delegate* unmanaged<uint, IntPtr, void> _targetTexture;

    /// <summary>ANGLE のコンテキストの Direct3D 11 のデバイス。</summary>
    public IntPtr Device { get; }

    private D3D11Egl(IntPtr display, IntPtr device, IntPtr create, IntPtr destroy, IntPtr target)
    {
        _display = display; Device = device;
        _createImage = (delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int*, IntPtr>)create;
        _destroyImage = (delegate* unmanaged<IntPtr, IntPtr, int>)destroy;
        _targetTexture = (delegate* unmanaged<uint, IntPtr, void>)target;
    }

    /// <summary>使えなければ null (error に理由)。</summary>
    public static D3D11Egl? Create(GpuContext gpu, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows() || !EglApi.IsAngle) { error = "Windows の ANGLE でだけ使う"; return null; }
        if (gpu.D3DDevice == IntPtr.Zero || gpu.Display == IntPtr.Zero) { error = "Direct3D 11 のデバイスで開いた GPU コンテキストではありません"; return null; }
        var ext = EglApi.String(gpu.Display, EGL_EXTENSIONS);
        if (!ext.Contains("EGL_ANGLE_image_d3d11_texture")) { error = "EGL_ANGLE_image_d3d11_texture がありません"; return null; }
        var create = EglApi.Proc("eglCreateImageKHR");
        var destroy = EglApi.Proc("eglDestroyImageKHR");
        var target = EglApi.Proc("glEGLImageTargetTexture2DOES");
        if (create == IntPtr.Zero || destroy == IntPtr.Zero || target == IntPtr.Zero) { error = "EGL の画像の関数がありません"; return null; }
        return new D3D11Egl(gpu.Display, gpu.D3DDevice, create, destroy, target);
    }

    /// <summary>テクスチャの 1 つの面 (plane。NV12・P010 でなければ 0) を EGL の画像にする。作れなければ IntPtr.Zero。</summary>
    public IntPtr Import(IntPtr texture, int plane, int slice)
    {
        int* a = stackalloc int[5];
        a[0] = EGL_D3D11_TEXTURE_PLANE_ANGLE; a[1] = plane;
        if (slice != 0) { a[2] = EGL_D3D11_TEXTURE_ARRAY_SLICE_ANGLE; a[3] = slice; a[4] = EGL_NONE; }
        else a[2] = EGL_NONE;
        return _createImage(_display, IntPtr.Zero, EGL_D3D11_TEXTURE_ANGLE, texture, a);
    }

    /// <summary>今結び付けている GL_TEXTURE_2D の中身を、EGL の画像にする。</summary>
    public void BindToTexture(IntPtr image) => _targetTexture(GlFunctions.TEXTURE_2D, image);

    public void Destroy(IntPtr image)
    {
        if (image != IntPtr.Zero) _destroyImage(_display, image);
    }
}

/// <summary>
/// D3D11VA でデコードしたフレーム (GPU 上の NV12・P010) を、メモリへ写さずに描画 (ANGLE) で使う。設計書 22.65。
/// <para>
/// デコーダ (FFmpeg) の Direct3D 11 のデバイスは、描画のデバイス (GPU コンテキストごとに別) と違うので、
/// デコーダのデバイスで共有のテクスチャ (keyed mutex 付き) を作り、デコードしたテクスチャからそこへ GPU の中で写して (CopySubresourceRegion)、
/// 描画のデバイスで開いたもの (OpenSharedResource) を、面ごとに EGL の画像として取り込む。
/// 受け渡しの鍵: デコーダ側が鍵 0 で取って写し、鍵 1 で渡す。描画側は鍵 1 で取って描き、GPU に命令を渡してから鍵 0 で返す。
/// 写したあとはデコーダの置き場を持たない (デコーダの命令は同じデバイスで順に実行されるので、写し終える前に書き換えられない)。
/// </para>
/// 描画のスレッド (GPU コンテキストを有効にしたスレッド) からだけ使う。
/// </summary>
internal sealed unsafe class D3D11FrameImport : IDisposable
{
    /// <summary>共有のテクスチャの数 (描画が使っている間も、次のフレームを写せるように)。</summary>
    private const int Slots = 3;
    private const int AcquireTimeoutMs = 500;

    private readonly D3D11Egl _egl;
    private readonly GlFunctions _gl;
    private readonly List<Slot> _slots = new();
    private int _next;

    /// <summary>共有のテクスチャ 1 枚 (デコーダ側と描画側の両方で開いたもの)。</summary>
    internal sealed class Slot
    {
        public IntPtr SourceDevice, Shared, SharedMutex, Opened, OpenedMutex;
        public int Width, Height;
        public uint Format;
        public readonly IntPtr[] Images = new IntPtr[2];
        public readonly uint[] Textures = new uint[2];
        public bool HeldForDrawing;
    }

    private D3D11FrameImport(D3D11Egl egl, GlFunctions gl) { _egl = egl; _gl = gl; }

    /// <summary>使えなければ null (error に理由)。描画のスレッドで呼ぶ。</summary>
    public static D3D11FrameImport? Create(GpuContext gpu, out string? error)
    {
        var gl = gpu.Gl;
        if (gl == null) { error = gpu.GlError ?? "OpenGL ES 3 の関数を使えません"; return null; }
        var egl = D3D11Egl.Create(gpu, out error);
        return egl == null ? null : new D3D11FrameImport(egl, gl);
    }

    /// <summary>
    /// デコーダのテクスチャ (source の slice 番目、w x h の NV12 か P010) を共有のテクスチャへ写し、描画側で鍵を取って、
    /// Y と U・V の面の GL のテクスチャを返す。描き終えたら (GPU に命令を渡す前でよい) <see cref="Done"/> を呼ぶ。
    /// できなければ null (error に理由)。
    /// </summary>
    public Slot? Import(D3D11DeviceAccess device, IntPtr source, int slice, int w, int h, bool p010, out string? error)
    {
        error = null;
        if (device.Device == IntPtr.Zero || device.Context == IntPtr.Zero || source == IntPtr.Zero) { error = "デコーダの Direct3D 11 のデバイスがありません"; return null; }
        // 4:2:0 の面は、幅・高さが偶数であること (デコーダの置き場は 16 などの倍数で確保されているので、はみ出さない)
        int tw = (w + 1) & ~1, th = (h + 1) & ~1;
        uint format = p010 ? D3D11.DXGI_FORMAT_P010 : D3D11.DXGI_FORMAT_NV12;
        var slot = TakeSlot(device.Device, tw, th, format, out error);
        if (slot == null) return null;
        // デコーダ側: 鍵 0 で取り、写して、鍵 1 で描画側に渡す。FFmpeg も同じ即時のコンテキストを使うので、その排他の中で
        bool copied = false;
        device.Enter();
        try
        {
            if (D3D11.AcquireSync(slot.SharedMutex, 0, AcquireTimeoutMs))
            {
                var box = new D3D11.Box { Left = 0, Top = 0, Front = 0, Right = (uint)tw, Bottom = (uint)th, Back = 1 };
                D3D11.CopySubresourceRegion(device.Context, slot.Shared, 0, source, (uint)slice, &box);
                D3D11.Flush(device.Context);   // 写す命令を GPU に渡してから鍵を渡す (描画側が待ち続けないように)
                D3D11.ReleaseSync(slot.SharedMutex, 1);
                copied = true;
            }
        }
        finally { device.Leave(); }
        if (!copied) { error = "共有のテクスチャの鍵を取れません (デコーダ側)"; return null; }
        if (!D3D11.AcquireSync(slot.OpenedMutex, 1, AcquireTimeoutMs)) { error = "共有のテクスチャの鍵を取れません (描画側)"; return null; }
        slot.HeldForDrawing = true;
        return slot;
    }

    /// <summary>描き終えた。GPU に命令を渡し、鍵 0 でデコーダ側に返す。</summary>
    public void Done(Slot slot)
    {
        if (!slot.HeldForDrawing) return;
        _gl.Flush();
        D3D11.ReleaseSync(slot.OpenedMutex, 0);
        slot.HeldForDrawing = false;
    }

    private Slot? TakeSlot(IntPtr sourceDevice, int w, int h, uint format, out string? error)
    {
        error = null;
        // デコーダのデバイス・大きさ・形式が変わったら作り直す
        if (_slots.Count > 0 && (_slots[0].SourceDevice != sourceDevice || _slots[0].Width != w || _slots[0].Height != h || _slots[0].Format != format))
            ClearSlots();
        if (_slots.Count < Slots)
        {
            var created = CreateSlot(sourceDevice, w, h, format, out error);
            if (created == null) return null;
            _slots.Add(created);
            return created;
        }
        var slot = _slots[_next];
        _next = (_next + 1) % _slots.Count;
        Done(slot);   // 描画側が返し忘れていれば返す (デコーダ側が鍵 0 を待ち続けないように)
        return slot;
    }

    private Slot? CreateSlot(IntPtr sourceDevice, int w, int h, uint format, out string? error)
    {
        error = null;
        Slot? slot = new Slot { SourceDevice = sourceDevice, Width = w, Height = h, Format = format };
        var gl = _gl;
        try
        {
            var desc = new D3D11.Texture2DDesc
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1,
                Usage = D3D11.USAGE_DEFAULT, BindFlags = D3D11.BIND_SHADER_RESOURCE, MiscFlags = D3D11.MISC_SHARED_KEYEDMUTEX,
            };
            int hr = D3D11.CreateTexture2D(sourceDevice, desc, null, out slot.Shared);
            if (hr < 0) { error = $"共有のテクスチャを作れません ({(format == D3D11.DXGI_FORMAT_P010 ? "P010" : "NV12")}, 0x{hr:X8})"; return null; }
            if (D3D11.QueryInterface(slot.Shared, D3D11.IID_IDXGIKeyedMutex, out slot.SharedMutex) < 0) { error = "IDXGIKeyedMutex を取れません (デコーダ側)"; return null; }
            var handle = D3D11.SharedHandle(slot.Shared);
            if (handle == IntPtr.Zero) { error = "共有のハンドルを取れません"; return null; }
            // 描画のデバイスで開く (デコーダと違う GPU なら開けない)
            hr = D3D11.OpenSharedTexture(_egl.Device, handle, out slot.Opened);
            if (hr < 0) { error = $"描画のデバイスで共有のテクスチャを開けません (デコードと描画の GPU が違う? 0x{hr:X8})"; return null; }
            if (D3D11.QueryInterface(slot.Opened, D3D11.IID_IDXGIKeyedMutex, out slot.OpenedMutex) < 0) { error = "IDXGIKeyedMutex を取れません (描画側)"; return null; }
            gl.GetError();
            for (int i = 0; i < 2; i++)
            {
                slot.Images[i] = _egl.Import(slot.Opened, i, 0);
                if (slot.Images[i] == IntPtr.Zero) { error = $"Direct3D 11 のテクスチャを EGL に取り込めません (面 {i}, 0x{EglApi.GetError():X4})"; return null; }
                slot.Textures[i] = gl.GenTexture();
                gl.BindTexture(GlFunctions.TEXTURE_2D, slot.Textures[i]);
                _egl.BindToTexture(slot.Images[i]);
                if (gl.GetError() is var e and not 0) { error = $"取り込んだ画像をテクスチャにできません (0x{e:X4})"; return null; }
            }
            gl.BindTexture(GlFunctions.TEXTURE_2D, 0);
            var result = slot;
            slot = null;
            return result;
        }
        finally { if (slot != null) DisposeSlot(slot); }
    }

    private void DisposeSlot(Slot slot)
    {
        if (slot.HeldForDrawing) { D3D11.ReleaseSync(slot.OpenedMutex, 0); slot.HeldForDrawing = false; }
        for (int i = 0; i < 2; i++)
        {
            _gl.DeleteTexture(slot.Textures[i]);
            _egl.Destroy(slot.Images[i]);
        }
        D3D11.Release(slot.OpenedMutex); D3D11.Release(slot.Opened);
        D3D11.Release(slot.SharedMutex); D3D11.Release(slot.Shared);
        slot.OpenedMutex = slot.Opened = slot.SharedMutex = slot.Shared = IntPtr.Zero;
    }

    private void ClearSlots()
    {
        foreach (var s in _slots) DisposeSlot(s);
        _slots.Clear();
        _next = 0;
    }

    /// <summary>描画のスレッドで、コンテキストを有効にしてから呼ぶ。</summary>
    public void Dispose() => ClearSlots();
}
