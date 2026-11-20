using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace VideoBuilder.App.Services;

/// <summary>
/// プレビューを、画面の描画 (Avalonia のコンポジタ) と共有する GL のテクスチャに直接描くための入れ物。
///
/// 以前は GPU で描いた絵を毎フレーム読み戻し (ReadPixels)、それを Avalonia がまた GPU に送っていた。
/// ここでは画面の描画と共有する GL コンテキストで、共有のテクスチャに描き、そのまま画面に出す (読み戻しも送り直しもない)。
///
/// テクスチャは最大 3 枚を順に使う。画面に出しているもの・出す途中のものには描かない。
/// GL を使う処理 (Acquire・テクスチャの破棄) は描画のスレッドで、画面への受け渡し (Present の中身) は UI スレッドで行う。
/// </summary>
public sealed class SharedPreviewTarget
{
    private const int MaxTextures = 3;
    private const int GL_RENDERER = 0x1F01, GL_VENDOR = 0x1F00;

    private readonly IOpenGlTextureSharingRenderInterfaceContextFeature _feature;
    private readonly ICompositionGpuInterop _interop;
    private readonly CompositionDrawingSurface _surface;
    private readonly object _gate = new();
    private readonly List<Texture> _textures = new();
    private Texture? _shown;

    /// <summary>画面の描画と共有する GL コンテキスト (描画のスレッドで使う)。</summary>
    public IGlContext Gl { get; }
    /// <summary>プレビューを出す場所 (PreviewView の子として置く)。</summary>
    public CompositionSurfaceVisual Visual { get; }
    public string Renderer { get; }
    public string Vendor { get; }
    /// <summary>まだ一度も絵を出していなければ false (それまでは見せない)。</summary>
    public bool HasFrame { get; private set; }
    /// <summary>絵を出すたびに呼ばれる (UI スレッド)。</summary>
    public event Action? Presented;

    /// <summary>描くテクスチャ。描画のスレッドで Acquire し、描いたら Present する。</summary>
    public sealed class Texture
    {
        public required ICompositionImportableOpenGlSharedTexture Shared;
        public ICompositionImportedGpuImage? Imported;
        public PixelSize Size;
        /// <summary>描いている途中・画面に出す途中・画面に出ている。</summary>
        public bool Busy;
        /// <summary>描画のスレッドの持ち物 (SKSurface など)。</summary>
        public object? Tag;
    }

    private SharedPreviewTarget(IOpenGlTextureSharingRenderInterfaceContextFeature feature, ICompositionGpuInterop interop, Compositor compositor, IGlContext gl)
    {
        _feature = feature; _interop = interop; Gl = gl;
        _surface = compositor.CreateDrawingSurface();
        Visual = compositor.CreateSurfaceVisual();
        Visual.Surface = _surface;
        Visual.Visible = false;
        using (gl.MakeCurrent())
        {
            Renderer = gl.GlInterface.GetString(GL_RENDERER) ?? "";
            Vendor = gl.GlInterface.GetString(GL_VENDOR) ?? "";
        }
    }

    /// <summary>
    /// host の画面の描画が OpenGL で、テクスチャを共有できるなら作る。できなければ null (UI スレッドから呼ぶ)。
    /// ヘッドレスや Vulkan・ソフトウェアの描画では使えない。
    /// </summary>
    public static async Task<SharedPreviewTarget?> TryCreateAsync(Visual host)
    {
        try
        {
            var compositor = ElementComposition.GetElementVisual(host)?.Compositor;
            if (compositor == null) return null;
            var interop = await compositor.TryGetCompositionGpuInterop();
            if (interop == null) return null;
            if (await compositor.TryGetRenderInterfaceFeature(typeof(IOpenGlTextureSharingRenderInterfaceContextFeature)) is not IOpenGlTextureSharingRenderInterfaceContextFeature feature
                || !feature.CanCreateSharedContext) return null;
            var gl = feature.CreateSharedContext(null);
            if (gl == null) return null;
            try { return new SharedPreviewTarget(feature, interop, compositor, gl); }
            catch { gl.Dispose(); return null; }
        }
        catch { return null; }
    }

    /// <summary>
    /// size の大きさで、今描いてよいテクスチャを返す (描画のスレッドで、Gl を有効にしてから呼ぶ)。
    /// 3 枚とも使用中なら、少し待ってもだめなら null。大きさの違う、使っていないテクスチャはここで捨てる。
    /// </summary>
    public Texture? Acquire(PixelSize size, Action<Texture>? disposeTag)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            lock (_gate)
            {
                foreach (var old in _textures.Where(t => !t.Busy && t.Size != size).ToList())
                {
                    disposeTag?.Invoke(old);
                    DisposeTexture(old);
                    _textures.Remove(old);
                }
                var free = _textures.FirstOrDefault(t => !t.Busy && t.Size == size);
                if (free == null && _textures.Count < MaxTextures)
                {
                    var shared = _feature.CreateSharedTextureForComposition(Gl, size);
                    free = new Texture { Shared = shared, Size = size };
                    _textures.Add(free);
                }
                if (free != null) { free.Busy = true; return free; }
            }
            Thread.Sleep(1);
        }
        return null;
    }

    /// <summary>描き終えたテクスチャを画面に出す (描画のスレッドから呼ぶ。GPU の処理は終わらせておくこと)。</summary>
    public void Present(Texture texture)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            Texture? previous;
            ICompositionImportedGpuImage image;
            lock (_gate)
            {
                if (!_textures.Contains(texture)) return;   // 捨てられた (大きさが変わった・終了した)
                texture.Imported ??= _interop.ImportImage((ICompositionImportableSharedGpuContextImage)texture.Shared);
                image = texture.Imported;
                previous = _shown;
                _shown = texture;
            }
            try { await _surface.UpdateAsync(image); }
            catch { /* 画面の描画が失われた (GPU のリセットなど): 次の絵で出し直す */ }
            lock (_gate)
            {
                // 新しい絵に切り替わったので、前の絵には描いてよい
                if (previous != null && !ReferenceEquals(previous, texture)) previous.Busy = false;
            }
            HasFrame = true;
            Presented?.Invoke();
        }, DispatcherPriority.Render);
    }

    /// <summary>出さなかったテクスチャを返す (描画に失敗したときなど)。</summary>
    public void Release(Texture texture)
    {
        lock (_gate) if (!ReferenceEquals(texture, _shown)) texture.Busy = false;
    }

    /// <summary>すべてのテクスチャと GL コンテキストを捨てる (描画のスレッドで、Gl を有効にしてから呼ぶ)。</summary>
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
    }

    /// <summary>GL コンテキストを捨てる (DisposeResources のあと、どのスレッドでもよい)。</summary>
    public void DisposeContext()
    {
        try { Gl.Dispose(); } catch { /* 終了中など */ }
        Dispatcher.UIThread.Post(() => { try { _surface.Dispose(); } catch { } });
    }

    /// <summary>テクスチャを描画のスレッドの持ち物ごと捨てるための呼び出し口 (Tag の後始末は呼び出し側)。</summary>
    private static void DisposeTexture(Texture t)
    {
        try { (t.Imported as IAsyncDisposable)?.DisposeAsync(); } catch { }
        try { (t.Shared as IDisposable)?.Dispose(); } catch { }
    }
}
