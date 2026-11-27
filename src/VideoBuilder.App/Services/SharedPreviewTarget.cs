using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Services;

/// <summary>
/// プレビューを、画面の描画 (Avalonia のコンポジタ) と共有するテクスチャに直接描くための入れ物。
///
/// 以前は GPU で描いた絵を毎フレーム読み戻し (ReadPixels)、それを Avalonia がまた GPU に送っていた。
/// ここでは画面の描画と共有するテクスチャに描き、そのまま画面に出す (読み戻しも送り直しもない)。
///
/// 共有のしかたは 2 通り:
/// <list type="bullet">
/// <item>GL (Linux): 画面の描画と共有する GL コンテキスト (<see cref="Gl"/>) を作り、共有の GL テクスチャに描く。</item>
/// <item>Direct3D 11 (Windows): Windows の Avalonia (ANGLE) は GL のコンテキストの共有に対応していないので、
/// 描画の GPU コンテキスト (自分の Direct3D 11 のデバイス) で共有の Direct3D 11 のテクスチャ (<see cref="SharedD3DTexture"/>) を作って描き、
/// 共有のハンドルで画面の描画に取り込ませる。受け渡しは keyed mutex。描画と画面の描画が同じ GPU (<see cref="DeviceLuid"/>) のときだけ使える。</item>
/// </list>
///
/// テクスチャは最大 4 枚を順に使う。画面に出しているもの・出す途中のもの・GPU が描き終えるのを待っているもの (PreviewService) には描かない。
/// GL を使う処理 (Acquire・テクスチャの破棄) は描画のスレッドで、画面への受け渡し (Present の中身) は UI スレッドで行う。
/// </summary>
public sealed class SharedPreviewTarget
{
    private const int MaxTextures = 4;
    private const int GL_RENDERER = 0x1F01, GL_VENDOR = 0x1F00;

    private readonly IOpenGlTextureSharingRenderInterfaceContextFeature? _feature;
    private readonly ICompositionGpuInterop _interop;
    private readonly CompositionDrawingSurface _surface;
    private readonly object _gate = new();
    private readonly List<Texture> _textures = new();
    private Texture? _shown;

    /// <summary>画面の描画と共有する GL コンテキスト (GL のとき。描画のスレッドで使う)。Direct3D 11 のときは null。</summary>
    public IGlContext? Gl { get; }
    /// <summary>Direct3D 11 の共有テクスチャで受け渡す (Windows)。</summary>
    public bool IsD3D => Gl == null;
    /// <summary>Direct3D 11 のとき: 画面の描画の GPU の LUID (分からなければ 0)。</summary>
    public long DeviceLuid { get; }
    /// <summary>
    /// Direct3D 11 のとき: 共有テクスチャを作る、描画の GPU コンテキスト (PreviewService が描画のスレッドで設定する)。
    /// </summary>
    public GpuContext? D3DOwner { get; set; }
    /// <summary>プレビューを出す場所 (PreviewView の子として置く)。</summary>
    public CompositionSurfaceVisual Visual { get; }
    public string Renderer { get; } = "";
    public string Vendor { get; } = "";
    /// <summary>まだ一度も絵を出していなければ false (それまでは見せない)。</summary>
    public bool HasFrame { get; private set; }
    /// <summary>画面への受け渡しに失敗した (この入れ物はもう使わない)。</summary>
    public bool IsBroken { get; private set; }
    /// <summary>受け渡しに失敗した理由。</summary>
    public string? BrokenReason { get; private set; }
    /// <summary>Direct3D 11 のとき: 共有のテクスチャを作れなかった理由 (描画のスレッドで設定。作れたら null)。</summary>
    public string? CreateError { get; private set; }
    /// <summary>絵を出すたびに呼ばれる (UI スレッド)。</summary>
    public event Action? Presented;
    /// <summary>画面への受け渡しに失敗して、使えなくなった (UI スレッド)。</summary>
    public event Action? Broken;

    /// <summary>描くテクスチャ。描画のスレッドで Acquire し、描いたら Present する。</summary>
    public sealed class Texture
    {
        /// <summary>GL のとき: 共有の GL テクスチャ。</summary>
        public ICompositionImportableOpenGlSharedTexture? Shared;
        /// <summary>Direct3D 11 のとき: 共有の Direct3D 11 のテクスチャ。</summary>
        public object? D3D;
        public ICompositionImportedGpuImage? Imported;
        public PixelSize Size;
        /// <summary>描いている途中・画面に出す途中・画面に出ている。</summary>
        public bool Busy;
        /// <summary>描画のスレッドの持ち物 (SKSurface など)。</summary>
        public object? Tag;

        /// <summary>Skia が描く GL のテクスチャ。</summary>
        public uint TextureId => Shared != null ? (uint)Shared.TextureId : OperatingSystem.IsWindows() && D3D is SharedD3DTexture d ? d.GlTexture : 0;
        public int InternalFormat => Shared != null ? Shared.InternalFormat : OperatingSystem.IsWindows() ? SharedD3DTexture.GlInternalFormat : 0;
    }

    private SharedPreviewTarget(IOpenGlTextureSharingRenderInterfaceContextFeature? feature, ICompositionGpuInterop interop, Compositor compositor, IGlContext? gl)
    {
        _feature = feature; _interop = interop; Gl = gl;
        _surface = compositor.CreateDrawingSurface();
        Visual = compositor.CreateSurfaceVisual();
        Visual.Surface = _surface;
        Visual.Visible = false;
        if (gl != null)
        {
            using (gl.MakeCurrent())
            {
                Renderer = gl.GlInterface.GetString(GL_RENDERER) ?? "";
                Vendor = gl.GlInterface.GetString(GL_VENDOR) ?? "";
            }
        }
        else if (interop.DeviceLuid is { Length: >= 8 } luid) DeviceLuid = BitConverter.ToInt64(luid, 0);
    }

    /// <summary>
    /// host の画面の描画と、テクスチャを共有できるなら作る。できなければ null (UI スレッドから呼ぶ)。
    /// GL で共有できればそれを、Windows で Direct3D 11 のテクスチャを取り込めればそれを使う。
    /// ヘッドレス・Vulkan・ソフトウェアの描画では使えない。
    /// </summary>
    public static async Task<SharedPreviewTarget?> TryCreateAsync(Visual host)
    {
        try
        {
            var compositor = ElementComposition.GetElementVisual(host)?.Compositor;
            if (compositor == null) return null;
            var interop = await compositor.TryGetCompositionGpuInterop();
            if (interop == null) return null;
            if (await compositor.TryGetRenderInterfaceFeature(typeof(IOpenGlTextureSharingRenderInterfaceContextFeature)) is IOpenGlTextureSharingRenderInterfaceContextFeature feature
                && feature.CanCreateSharedContext)
            {
                var gl = feature.CreateSharedContext(null);
                if (gl != null)
                {
                    try { return new SharedPreviewTarget(feature, interop, compositor, gl); }
                    catch { gl.Dispose(); return null; }
                }
            }
            // Windows: Direct3D 11 の共有テクスチャを、keyed mutex で受け渡せるか
            const string handleType = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle;
            if (OperatingSystem.IsWindows() && interop.SupportedImageHandleTypes.Contains(handleType)
                && interop.GetSynchronizationCapabilities(handleType).HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex))
                return new SharedPreviewTarget(null, interop, compositor, null);
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// size の大きさで、今描いてよいテクスチャを返す (描画のスレッドで、描画の GL を有効にしてから呼ぶ)。
    /// すべて使用中なら、少し待ってもだめなら null。大きさの違う、使っていないテクスチャはここで捨てる。
    /// Direct3D 11 のときは、描くための鍵も取ってから返す。
    /// </summary>
    public Texture? Acquire(PixelSize size, Action<Texture>? disposeTag)
    {
        // 待つのは最大 50ms。空くのは Monitor.PulseAll で知らせるので、すぐに起きる
        // (以前は Thread.Sleep(1) を 50 回。Windows では 1 回が約 15.6ms になり、最大 0.8 秒止まりえた)
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Texture? free = null;
            lock (_gate)
            {
                foreach (var old in _textures.Where(t => !t.Busy && t.Size != size).ToList())
                {
                    disposeTag?.Invoke(old);
                    DisposeTexture(old);
                    _textures.Remove(old);
                }
                free = _textures.FirstOrDefault(t => !t.Busy && t.Size == size);
                if (free == null && _textures.Count < MaxTextures)
                {
                    free = CreateTexture(size);
                    if (free == null) return null;
                    _textures.Add(free);
                }
                if (free != null) free.Busy = true;
            }
            if (free != null)
            {
                // Direct3D 11: 画面の描画が使い終えて鍵を返すのを待つ (返ってこなければ、このフレームは飛ばす)
                if (OperatingSystem.IsWindows() && free.D3D is SharedD3DTexture d && !d.AcquireForDrawing(100))
                {
                    lock (_gate) { free.Busy = false; Monitor.PulseAll(_gate); }
                    return null;
                }
                return free;
            }
            long left = AcquireTimeoutMs - deadline.ElapsedMilliseconds;
            if (left <= 0) return null;
            lock (_gate)
                if (!_textures.Any(t => !t.Busy)) Monitor.Wait(_gate, (int)left);
        }
    }

    /// <summary>新しいテクスチャを作る (_gate の中で、描画のスレッドから)。作れなければ null。</summary>
    private Texture? CreateTexture(PixelSize size)
    {
        if (Gl != null) return new Texture { Shared = _feature!.CreateSharedTextureForComposition(Gl, size), Size = size };
        if (!OperatingSystem.IsWindows() || D3DOwner == null) return null;
        var d = D3DOwner.CreateSharedD3DTexture(size.Width, size.Height, out var error);
        if (d == null) { CreateError = error ?? "共有のテクスチャを作れません"; return null; }
        return new Texture { D3D = d, Size = size };
    }

    /// <summary>
    /// 描き終えたテクスチャを画面に出す (描画のスレッドから呼ぶ。GPU の処理は終わらせておくこと)。
    /// Direct3D 11 のときは、ここで鍵を画面の描画に渡す。
    /// </summary>
    public void Present(Texture texture)
    {
        if (OperatingSystem.IsWindows() && texture.D3D is SharedD3DTexture d) d.ReleaseForPresent();
        Dispatcher.UIThread.Post(async () =>
        {
            Texture? previous;
            ICompositionImportedGpuImage image;
            lock (_gate)
            {
                if (!_textures.Contains(texture) || IsBroken) return;   // 捨てられた (大きさが変わった・終了した)
                try { texture.Imported ??= Import(texture); }
                catch (Exception ex)
                {
                    Fail("画面の描画に渡せません: " + ex.Message);
                    return;
                }
                image = texture.Imported;
                previous = _shown;
                _shown = texture;
            }
            try
            {
                if (texture.D3D != null)
                    await _surface.UpdateWithKeyedMutexAsync(image, (uint)SharedD3DTexture.PresentKey, (uint)SharedD3DTexture.DrawKey);
                else
                    await _surface.UpdateAsync(image);
            }
            catch { /* 画面の描画が失われた (GPU のリセットなど): 次の絵で出し直す */ }
            lock (_gate)
            {
                // 新しい絵に切り替わったので、前の絵には描いてよい
                if (previous != null && !ReferenceEquals(previous, texture)) { previous.Busy = false; Monitor.PulseAll(_gate); }
            }
            HasFrame = true;
            Presented?.Invoke();
        }, DispatcherPriority.Render);
    }

    private ICompositionImportedGpuImage Import(Texture texture)
    {
        if (texture.Shared != null) return _interop.ImportImage((ICompositionImportableSharedGpuContextImage)texture.Shared);
        if (!OperatingSystem.IsWindows() || texture.D3D is not SharedD3DTexture d) throw new InvalidOperationException("テクスチャがありません");
        var properties = new PlatformGraphicsExternalImageProperties
        {
            Width = d.Width, Height = d.Height, Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
        };
        return _interop.ImportImage(new PlatformHandle(d.SharedHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle), properties);
    }

    /// <summary>受け渡しに失敗した。これ以降は使わず、読み戻して表示する方法に戻してもらう (UI スレッド)。</summary>
    private void Fail(string reason)
    {
        if (IsBroken) return;
        IsBroken = true;
        BrokenReason = reason;
        Visual.Visible = false;
        Broken?.Invoke();
    }

    /// <summary>出さなかったテクスチャを返す (描画に失敗したときなど)。</summary>
    public void Release(Texture texture)
    {
        if (OperatingSystem.IsWindows() && texture.D3D is SharedD3DTexture d) d.ReleaseUnused();
        lock (_gate)
            if (!ReferenceEquals(texture, _shown)) { texture.Busy = false; Monitor.PulseAll(_gate); }
    }

    /// <summary>すべてのテクスチャが使用中のとき、空くのを待つ上限 (ms)。</summary>
    private const int AcquireTimeoutMs = 50;

    /// <summary>すべてのテクスチャを捨てる (描画のスレッドで、描画の GL を有効にしてから呼ぶ)。</summary>
    public void DisposeResources(Action<Texture>? disposeTag)
    {
        lock (_gate)
        {
            foreach (var t in _textures)
            {
                disposeTag?.Invoke(t);
                DisposeTexture(t);
            }
            _textures.Clear();
            _shown = null;
        }
        D3DOwner = null;
    }

    /// <summary>GL コンテキストを捨てる (DisposeResources のあと、どのスレッドでもよい)。</summary>
    public void DisposeContext()
    {
        try { Gl?.Dispose(); } catch { /* 終了中など */ }
        Dispatcher.UIThread.Post(() => { try { _surface.Dispose(); } catch { } });
    }

    /// <summary>テクスチャを描画のスレッドの持ち物ごと捨てるための呼び出し口 (Tag の後始末は呼び出し側)。</summary>
    private static void DisposeTexture(Texture t)
    {
        try { (t.Imported as IAsyncDisposable)?.DisposeAsync(); } catch { }
        try { (t.Shared as IDisposable)?.Dispose(); } catch { }
        try { (t.D3D as IDisposable)?.Dispose(); } catch { }
    }
}
