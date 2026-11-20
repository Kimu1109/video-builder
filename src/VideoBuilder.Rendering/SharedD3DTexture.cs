using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VideoBuilder.Rendering;

/// <summary>
/// Windows: 画面の描画 (Avalonia のコンポジタ) と共有する Direct3D 11 のテクスチャ。GPU 描画 (ANGLE) で直接描き、読み戻さずに画面に出す。
/// <para>
/// 作り方: 描画の Direct3D 11 のデバイスで、共有できる (keyed mutex 付きの) テクスチャを作る (D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX)。
/// ANGLE で pbuffer として包み (eglCreatePbufferFromClientBuffer + EGL_D3D_TEXTURE_ANGLE)、GL のテクスチャに結び付ける (eglBindTexImage)。
/// Skia はこの GL のテクスチャに描く。画面の描画は、共有のハンドル (<see cref="SharedHandle"/>) で同じテクスチャを開く。
/// </para>
/// <para>
/// 受け渡し (keyed mutex): 描く側は鍵 0 で取って描き、鍵 1 で返す。画面の描画は鍵 1 で取って表示し、鍵 0 で返す
/// (Avalonia の <c>UpdateWithKeyedMutexAsync(image, 1, 0)</c>)。
/// </para>
/// 作ったコンテキストを有効にしたスレッドで使い、捨てる。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class SharedD3DTexture : IDisposable
{
    /// <summary>描く側が取る鍵。画面の描画が使い終えると、この鍵で返ってくる。</summary>
    public const ulong DrawKey = 0;
    /// <summary>描き終えて画面の描画に渡す鍵。</summary>
    public const ulong PresentKey = 1;

    private const int GL_TEXTURE_2D = 0x0DE1, GL_RGBA8 = 0x8058;
    private const int EGL_NONE = 0x3038, EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056;
    private const int EGL_TEXTURE_FORMAT = 0x3080, EGL_TEXTURE_TARGET = 0x3081, EGL_TEXTURE_RGBA = 0x305E, EGL_TEXTURE_2D = 0x305F, EGL_BACK_BUFFER = 0x3084;
    private const int EGL_D3D_TEXTURE_ANGLE = 0x33A3;
    private const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8, D3D11_BIND_RENDER_TARGET = 0x20, D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX = 0x100;
    private const int WAIT_TIMEOUT = 0x102, WAIT_ABANDONED = 0x80;
    private static readonly Guid IID_IDXGIResource = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
    private static readonly Guid IID_IDXGIKeyedMutex = new("9d8e1289-d7b3-465f-8126-250e349af85d");

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    private readonly GpuContext _owner;
    private IntPtr _texture, _mutex, _pbuffer;
    private uint _glTexture;

    public int Width { get; }
    public int Height { get; }
    /// <summary>画面の描画がテクスチャを開くための共有のハンドル (D3D11TextureGlobalSharedHandle)。</summary>
    public IntPtr SharedHandle { get; private set; }
    /// <summary>Skia が描く GL のテクスチャ。</summary>
    public uint GlTexture => _glTexture;
    public static int GlInternalFormat => GL_RGBA8;
    /// <summary>今、描く側が鍵を持っているか。</summary>
    public bool Held { get; private set; }

    private SharedD3DTexture(GpuContext owner, int width, int height) { _owner = owner; Width = width; Height = height; }

    internal static SharedD3DTexture? Create(GpuContext owner, int width, int height, out string? error)
    {
        error = null;
        var t = new SharedD3DTexture(owner, width, height);
        try
        {
            if (t.Init(out error)) return t;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = ex.Message;
        }
        t.Dispose();
        return null;
    }

    private bool Init(out string? error)
    {
        error = null;
        var device = _owner.D3DDevice;
        // ID3D11Device::CreateTexture2D (5)
        var desc = new Texture2DDesc
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleCount = 1, SampleQuality = 0, Usage = 0, BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
            CpuAccessFlags = 0, MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX,
        };
        IntPtr texture;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void*, IntPtr*, int>)(*(IntPtr**)device)[5])(device, &desc, null, &texture);
        if (hr < 0 || texture == IntPtr.Zero) { error = $"共有のテクスチャを作れません (0x{hr:X8})"; return false; }
        _texture = texture;

        // 共有のハンドル: IDXGIResource::GetSharedHandle (8)
        IntPtr resource;
        var iid = IID_IDXGIResource;
        if (QueryInterface(texture, &iid, &resource) < 0) { error = "IDXGIResource を取れません"; return false; }
        IntPtr handle;
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)resource)[8])(resource, &handle);
        DxgiAdapters.Release(resource);
        if (hr < 0 || handle == IntPtr.Zero) { error = $"共有のハンドルを取れません (0x{hr:X8})"; return false; }
        SharedHandle = handle;

        iid = IID_IDXGIKeyedMutex;
        IntPtr mutex;
        if (QueryInterface(texture, &iid, &mutex) < 0) { error = "IDXGIKeyedMutex を取れません"; return false; }
        _mutex = mutex;

        // ANGLE で包んで、GL のテクスチャに結び付ける
        var fromClient = (delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, int*, IntPtr>)EglApi.Proc("eglCreatePbufferFromClientBuffer");
        var bindTex = (delegate* unmanaged<IntPtr, IntPtr, int, int>)EglApi.Proc("eglBindTexImage");
        var genTextures = (delegate* unmanaged<int, uint*, void>)EglApi.Proc("glGenTextures");
        var bindTexture = (delegate* unmanaged<int, uint, void>)EglApi.Proc("glBindTexture");
        if (fromClient == null || bindTex == null || genTextures == null || bindTexture == null) { error = "ANGLE の関数がありません"; return false; }
        int* attribs = stackalloc[]
        {
            EGL_WIDTH, Width, EGL_HEIGHT, Height, EGL_TEXTURE_FORMAT, EGL_TEXTURE_RGBA, EGL_TEXTURE_TARGET, EGL_TEXTURE_2D, EGL_NONE,
        };
        _pbuffer = fromClient(_owner.Display, EGL_D3D_TEXTURE_ANGLE, texture, _owner.Config, attribs);
        if (_pbuffer == IntPtr.Zero) { error = $"ANGLE でテクスチャを包めません (0x{EglApi.GetError():X4})"; return false; }
        uint id;
        genTextures(1, &id);
        _glTexture = id;
        bindTexture(GL_TEXTURE_2D, id);
        if (bindTex(_owner.Display, _pbuffer, EGL_BACK_BUFFER) == 0) { error = $"eglBindTexImage に失敗 (0x{EglApi.GetError():X4})"; return false; }
        bindTexture(GL_TEXTURE_2D, 0);
        return true;
    }

    private static int QueryInterface(IntPtr unknown, Guid* iid, IntPtr* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)unknown)[0])(unknown, iid, result);

    /// <summary>描くために鍵を取る (画面の描画が使い終えるまで、最大 timeoutMs 待つ)。取れなければ false。</summary>
    public bool AcquireForDrawing(int timeoutMs)
    {
        if (Held) return true;
        // IDXGIKeyedMutex::AcquireSync (8)
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)(*(IntPtr**)_mutex)[8])(_mutex, DrawKey, (uint)timeoutMs);
        if (hr == WAIT_TIMEOUT || hr == WAIT_ABANDONED || hr < 0) return false;
        Held = true;
        return true;
    }

    /// <summary>描き終えたので、画面の描画に渡す (GPU の処理は終わらせておくこと)。</summary>
    public void ReleaseForPresent() => ReleaseWith(PresentKey);

    /// <summary>描かずに返す (描画に失敗したときなど)。次もまた描く側が取れる。</summary>
    public void ReleaseUnused() => ReleaseWith(DrawKey);

    private void ReleaseWith(ulong key)
    {
        if (!Held) return;
        // IDXGIKeyedMutex::ReleaseSync (9)
        ((delegate* unmanaged[Stdcall]<IntPtr, ulong, int>)(*(IntPtr**)_mutex)[9])(_mutex, key);
        Held = false;
    }

    /// <summary>テクスチャを手放す。作ったコンテキストを有効にしてから呼ぶ。</summary>
    public void Dispose()
    {
        ReleaseUnused();
        // GL のテクスチャから pbuffer を外してから、両方を捨てる
        if (_pbuffer != IntPtr.Zero)
        {
            var releaseTex = (delegate* unmanaged<IntPtr, IntPtr, int, int>)EglApi.Proc("eglReleaseTexImage");
            if (releaseTex != null && _glTexture != 0) releaseTex(_owner.Display, _pbuffer, EGL_BACK_BUFFER);
            EglApi.DestroySurface(_owner.Display, _pbuffer);
            _pbuffer = IntPtr.Zero;
        }
        if (_glTexture != 0)
        {
            var deleteTextures = (delegate* unmanaged<int, uint*, void>)EglApi.Proc("glDeleteTextures");
            uint id = _glTexture;
            if (deleteTextures != null) deleteTextures(1, &id);
            _glTexture = 0;
        }
        DxgiAdapters.Release(_mutex); _mutex = IntPtr.Zero;
        DxgiAdapters.Release(_texture); _texture = IntPtr.Zero;
        SharedHandle = IntPtr.Zero;
    }
}
