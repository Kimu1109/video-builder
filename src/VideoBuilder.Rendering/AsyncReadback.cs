using SkiaSharp;

namespace VideoBuilder.Rendering;

/// <summary>
/// 書き出し用: GPU で描いたフレームを、GPU の処理が終わるのを待たずに読み戻す (設計書 22.52)。
/// SKSurface.ReadPixels は GPU が描き終えるまで待ってから写すので、その間 CPU は何もできない。
/// ここでは描き終えたら読み戻しの命令だけを出して (glReadPixels を PBO へ + フェンス)、次のフレームを描いたあとで受け取る。
/// GPU がフレーム N を描いている間に、CPU はフレーム N-1 の色変換を進められる。
/// 描画先は自前の GL テクスチャ (RGBA8) で、Skia にはそれを包んだ SKSurface に描かせる。
/// 作ったスレッド (GPU コンテキストを有効にしたスレッド) からだけ使う。
/// </summary>
internal sealed unsafe class AsyncReadback : IDisposable
{
    /// <summary>同時に読み戻しの途中にしておけるフレームの数。</summary>
    public const int Depth = 2;

    private readonly GpuContext _gpu;
    private readonly GlFunctions _gl;
    private readonly uint _texture, _fbo;
    private readonly uint[] _buffers = new uint[Depth];
    private readonly IntPtr[] _fences = new IntPtr[Depth];
    private readonly Queue<int> _pending = new();
    private int _next;
    private int _finished;

    /// <summary>テスト用: この数のフレームを受け取ったあと、読み戻しに失敗したことにする (-1 なら失敗させない)。</summary>
    internal static int TestFailAfter { get; set; } = -1;

    public int Width { get; }
    public int Height { get; }
    public int RowBytes => Width * 4;
    /// <summary>GPU で YUV に変換してから読み戻すときの変換 (null なら RGBA のまま読み戻す)。設計書 22.58。</summary>
    public GpuYuvExport? Yuv { get; }
    /// <summary>YUV に変換して読み戻せないとき、その理由。</summary>
    public string? YuvError { get; private set; }
    private long BufferBytes => Yuv?.Bytes ?? (long)RowBytes * Height;

    // ---- エンコーダのフレームへ直接描く (読み戻さない) ----
    // VAAPI: フレームを DMA-BUF として取り込んで直接描く (設計書 22.58)。
    // CUDA (NVENC): NV12 にした面を、CUDA と GL の相互運用で GPU の中でフレームへ写す (設計書 22.62)
    private Func<(VideoBuilder.Media.HardwareFrame? Frame, string? Error)>? _allocate;
    private EglDmaBuf? _dmaBuf;
    private CudaGl? _cudaOut;
    /// <summary>直接描くフレームの種類 (VAAPI か CUDA)。</summary>
    public VideoBuilder.Media.HardwareFrameKinds DirectKind { get; private set; }
    private readonly DirectTarget?[] _direct = new DirectTarget?[Depth];

    /// <summary>エンコーダのフレームへ直接描いているか。</summary>
    public bool Direct => _allocate != null;
    /// <summary>エンコーダのフレームへ直接描けない (描けなくなった) 理由。</summary>
    public string? DirectError { get; private set; }

    /// <summary>VAAPI のフレームを DMA-BUF として取り込んだ描画先 (Y と U・V の 2 面)。</summary>
    private sealed class DirectTarget(VideoBuilder.Media.HardwareFrame frame)
    {
        public VideoBuilder.Media.HardwareFrame Frame = frame;
        public readonly IntPtr[] Images = new IntPtr[2];
        public readonly uint[] Textures = new uint[2];
        public readonly uint[] Fbos = new uint[2];
    }

    /// <summary>
    /// これからのフレームを、allocate で取ったエンコーダのフレームに直接描く。
    /// VAAPI: DMA-BUF として取り込んで描く (EGL で DMA-BUF を取り込めるとき)。
    /// CUDA (NVENC): NV12 に変換した面を、GPU の中で CUDA のフレームへ写す (GL のコンテキストが NVIDIA の GPU のとき)。
    /// どちらも Yuv が NV12 のときだけ。使えなければ false (DirectError に理由。これまでどおり読み戻す)。
    /// </summary>
    public bool EnableDirect(Func<(VideoBuilder.Media.HardwareFrame? Frame, string? Error)> allocate,
        VideoBuilder.Media.HardwareFrameKinds kind = VideoBuilder.Media.HardwareFrameKinds.Vaapi)
    {
        bool cuda = kind == VideoBuilder.Media.HardwareFrameKinds.Cuda;
        string env = cuda ? "VIDEO_BUILDER_NVENC_DIRECT" : "VIDEO_BUILDER_VAAPI_DIRECT";
        if (Environment.GetEnvironmentVariable(env) == "0") { DirectError = env + "=0"; return false; }
        if (Yuv == null || Yuv.Layout != VideoBuilder.Media.YuvLayout.Nv12) { DirectError = YuvError ?? "GPU で NV12 に変換できません"; return false; }
        string? error;
        if (cuda) { _cudaOut = CudaGl.Create(_gpu, out error, toFrame: true); if (_cudaOut == null) { DirectError = error; return false; } }
        else { _dmaBuf = EglDmaBuf.Create(_gpu, out error); if (_dmaBuf == null) { DirectError = error; return false; } }
        DirectKind = kind;
        _allocate = allocate;
        return true;
    }

    /// <summary>エンコーダのフレームを取って、そこへ変換して描く。できなければ null (直接描くのをやめる)。</summary>
    private DirectTarget? StartDirect()
    {
        var (frame, error) = _allocate!();
        if (frame == null) { DisableDirect(error ?? "GPU 側のフレームを確保できません"); return null; }
        var d = new DirectTarget(frame);
        var gl = _gl;
        try
        {
            if (_cudaOut != null)
            {
                // NV12 にした面を描き、GPU の中で CUDA のフレームへ写す (GL の描画が終わってから写り、
                // 写し終えるまで GL は次の描画で面を書き換えない。cuGraphicsMapResources / Unmap が保証する)
                if (frame.Kind != VideoBuilder.Media.HardwareFrameKinds.Cuda) { DisableDirect("エンコーダのフレームが CUDA ではありません"); return null; }
                Yuv!.Convert(_texture);
                int r = _cudaOut.Copy(frame, Yuv.PlaneTextures);
                if (r != 0) { DisableDirect($"GL のテクスチャから CUDA のフレームへ写せません (CUDA のエラー {r})"); return null; }
                var copied = d;
                d = null;
                return copied;
            }
            if (!frame.TryMapDmaBuf(out var layers, out error, write: true)) { DisableDirect(error!); return null; }
            if (layers.Length != 2 || layers[0].DrmFormat != VideoBuilder.Media.HardwareFrame.DrmR8 || layers[1].DrmFormat != VideoBuilder.Media.HardwareFrame.DrmGR88)
            {
                DisableDirect("DMA-BUF の形式が NV12 (R8 + GR88) ではありません");
                return null;
            }
            gl.GetError();
            for (int i = 0; i < 2; i++)
            {
                d.Images[i] = _dmaBuf!.Import(layers[i]);
                if (d.Images[i] == IntPtr.Zero) { DisableDirect($"DMA-BUF を EGL に取り込めません (0x{EglApi.GetError():X4}, modifier 0x{layers[i].Modifier:X})"); return null; }
                d.Textures[i] = gl.GenTexture();
                gl.BindTexture(GlFunctions.TEXTURE_2D, d.Textures[i]);
                _dmaBuf.BindToTexture(d.Images[i]);
                uint fbo;
                gl.GenFramebuffers(1, &fbo);
                d.Fbos[i] = fbo;
                gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, fbo);
                gl.FramebufferTexture2D(GlFunctions.FRAMEBUFFER, GlFunctions.COLOR_ATTACHMENT0, GlFunctions.TEXTURE_2D, d.Textures[i], 0);
                bool complete = gl.CheckFramebufferStatus(GlFunctions.FRAMEBUFFER) == GlFunctions.FRAMEBUFFER_COMPLETE;
                gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
                if (!complete || gl.GetError() != 0) { DisableDirect("DMA-BUF に描けません"); return null; }
            }
            Yuv!.ConvertTo(_texture, d.Fbos[0], d.Fbos[1]);
            var result = d;
            d = null;
            return result;
        }
        finally { if (d != null) ReleaseDirect(d, disposeFrame: true); }
    }

    private void DisableDirect(string reason)
    {
        _allocate = null;
        DirectError = reason;
    }

    private void ReleaseDirect(DirectTarget d, bool disposeFrame)
    {
        var gl = _gl;
        for (int i = 0; i < 2; i++)
        {
            uint fbo = d.Fbos[i];
            if (fbo != 0) gl.DeleteFramebuffers(1, &fbo);
            gl.DeleteTexture(d.Textures[i]);
            _dmaBuf?.Destroy(d.Images[i]);
        }
        Array.Clear(d.Fbos); Array.Clear(d.Textures); Array.Clear(d.Images);
        d.Frame.Unmap();
        if (disposeFrame) d.Frame.Dispose();
    }
    /// <summary>描く先。RenderEngine.RenderToGpuSurface に渡す。</summary>
    public SKSurface Surface { get; }
    /// <summary>読み戻しの途中のフレームの数。</summary>
    public int Pending => _pending.Count;

    private AsyncReadback(GpuContext gpu, GlFunctions gl, int w, int h, uint texture, uint fbo, SKSurface surface, GpuYuvExport? yuv, string? yuvError)
    {
        _gpu = gpu; _gl = gl; Width = w; Height = h; _texture = texture; _fbo = fbo; Surface = surface; Yuv = yuv; YuvError = yuvError;
    }

    /// <summary>
    /// 作れなければ null (error に理由)。GPU コンテキストを有効にしたスレッドで呼ぶ。
    /// yuv を渡せば、GPU でその形式に変換してから読み戻す (変換を作れなければ RGBA のまま。理由は YuvError)。
    /// </summary>
    public static AsyncReadback? Create(GpuContext gpu, int w, int h, out string? error, (VideoBuilder.Media.YuvLayout Layout, bool Bt709)? yuv = null)
    {
        error = null;
        if (Environment.GetEnvironmentVariable("VIDEO_BUILDER_ASYNC_READBACK") == "0") { error = "VIDEO_BUILDER_ASYNC_READBACK=0"; return null; }
        gpu.MakeCurrent();
        var gl = gpu.Gl;
        if (gl == null) { error = gpu.GlError ?? "OpenGL ES 3 / OpenGL 3 の関数を使えません"; return null; }
        uint texture = 0, fbo = 0;
        var buffers = new uint[Depth];
        SKSurface? surface = null;
        string? yuvError = yuv == null ? "エンコーダの形式が NV12・YUV420P ではありません" : null;
        var converter = yuv is { } y ? GpuYuvExport.Create(gpu, w, h, y.Layout, y.Bt709, out yuvError) : null;
        long bytes = converter?.Bytes ?? (long)w * h * 4;
        try
        {
            texture = gl.GenTexture();
            gl.BindTexture(GlFunctions.TEXTURE_2D, texture);
            gl.TexImage2D(GlFunctions.TEXTURE_2D, 0, (int)GlFunctions.RGBA8, w, h, 0, GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, null);
            gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MIN_FILTER, GlFunctions.LINEAR);
            gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MAG_FILTER, GlFunctions.LINEAR);
            // 読み戻し用の FBO (Skia は自分の FBO で描く。同じテクスチャをこちらの FBO にも付けて、そこから読む)
            gl.GenFramebuffers(1, &fbo);
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, fbo);
            gl.FramebufferTexture2D(GlFunctions.FRAMEBUFFER, GlFunctions.COLOR_ATTACHMENT0, GlFunctions.TEXTURE_2D, texture, 0);
            bool complete = gl.CheckFramebufferStatus(GlFunctions.FRAMEBUFFER) == GlFunctions.FRAMEBUFFER_COMPLETE;
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
            if (!complete) { error = "読み戻し用の FBO を作れません"; return null; }
            fixed (uint* b = buffers) gl.GenBuffers(Depth, b);
            for (int i = 0; i < Depth; i++)
            {
                gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, buffers[i]);
                gl.BufferData(GlFunctions.PIXEL_PACK_BUFFER, (nint)bytes, null, GlFunctions.STREAM_READ);
            }
            gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, 0);
            if (gl.GetError() != 0) { error = "読み戻し用のバッファを作れません"; return null; }
            gpu.Context.ResetContext();
            var info = new GRGlTextureInfo(GlFunctions.TEXTURE_2D, texture, GlFunctions.RGBA8);
            using var backend = new GRBackendTexture(w, h, false, info);
            // 左上が原点: テクスチャの 0 行目が絵のいちばん上になる (glReadPixels も 0 行目から返す)
            surface = SKSurface.Create(gpu.Context, backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888);
            if (surface == null) { error = "Skia でテクスチャに描けません"; return null; }
            var result = new AsyncReadback(gpu, gl, w, h, texture, fbo, surface, converter, yuvError);
            Array.Copy(buffers, result._buffers, Depth);
            surface = null; texture = fbo = 0; Array.Clear(buffers); converter = null;
            return result;
        }
        finally
        {
            converter?.Dispose();
            surface?.Dispose();
            fixed (uint* b = buffers) if (buffers.Any(x => x != 0)) gl.DeleteBuffers(Depth, b);
            if (fbo != 0) gl.DeleteFramebuffers(1, &fbo);
            gl.DeleteTexture(texture);
            gpu.Context.ResetContext();
        }
    }

    /// <summary>
    /// Surface に描いたフレームの読み戻しを始める (待たない)。途中のフレームが Depth 個あるときは呼ばないこと (先に Finish する)。
    /// </summary>
    public void Start()
    {
        if (_pending.Count >= Depth) throw new InvalidOperationException("読み戻しの途中のフレームが多すぎます");
        var gl = _gl;
        // Skia が溜めている描画の命令を GL に出してから、読み戻しの命令を続ける (命令は出した順に実行される)
        _gpu.Context.Flush(true, false);
        int slot = _next;
        _next = (_next + 1) % Depth;
        try
        {
            if (_allocate != null && StartDirect() is { } direct)
            {
                // エンコーダのフレームに描いた。読み戻さない
                _direct[slot] = direct;
                _fences[slot] = gl.FenceSync(GlFunctions.SYNC_GPU_COMMANDS_COMPLETE, 0);
                gl.Flush();
                _pending.Enqueue(slot);
                return;
            }
            gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, _buffers[slot]);
            if (Yuv != null)
            {
                // GPU で YUV にしてから、面を順にバッファへ読み戻す
                Yuv.ConvertAndRead(_texture);
                gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, 0);
                _fences[slot] = gl.FenceSync(GlFunctions.SYNC_GPU_COMMANDS_COMPLETE, 0);
                _pending.Enqueue(slot);
                return;
            }
            gl.BindFramebuffer(GlFunctions.READ_FRAMEBUFFER, _fbo);
            gl.PixelStorei(GlFunctions.PACK_ALIGNMENT, 4);
            gl.PixelStorei(GlFunctions.PACK_ROW_LENGTH, 0);
            gl.PixelStorei(GlFunctions.PACK_SKIP_ROWS, 0);
            gl.PixelStorei(GlFunctions.PACK_SKIP_PIXELS, 0);
            gl.ReadPixels(0, 0, Width, Height, GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, null);   // バッファの先頭へ
            gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, 0);
            gl.BindFramebuffer(GlFunctions.READ_FRAMEBUFFER, 0);
            _fences[slot] = gl.FenceSync(GlFunctions.SYNC_GPU_COMMANDS_COMPLETE, 0);
        }
        finally { _gpu.Context.ResetContext(); }
        _pending.Enqueue(slot);
    }

    /// <summary>
    /// いちばん古い読み戻しを受け取る。終わるまで待ち、読み戻した RGBA (RowBytes バイトの行が Height 行) を use に渡す。
    /// Yuv があれば、渡すのは YUV の面を並べたバッファの先頭 (Yuv.PlanesAt で面にする。行のバイト数は RowBytes ではない)。
    /// 渡したポインタは use の中でだけ使える。読み戻せなければ false (use は呼ばない)。
    /// </summary>
    /// <param name="useDirect">
    /// エンコーダのフレームへ直接描いたもの (EnableDirect) を受け取る。フレームは useDirect が引き取る。
    /// </param>
    public bool Finish(Action<IntPtr, int> use, Action<VideoBuilder.Media.HardwareFrame>? useDirect = null)
    {
        if (!_pending.TryDequeue(out int slot)) return false;
        var direct = _direct[slot];
        _direct[slot] = null;
        var gl = _gl;
        _gpu.MakeCurrent();
        try
        {
            if (TestFailAfter >= 0 && _finished++ >= TestFailAfter) return false;
            var fence = _fences[slot];
            _fences[slot] = IntPtr.Zero;
            if (fence == IntPtr.Zero) return false;
            // 最大 10 秒待つ (ふつうは数 ms。GPU が止まったときに書き出しごと止まらないように)
            uint wait = gl.ClientWaitSync(fence, GlFunctions.SYNC_FLUSH_COMMANDS_BIT, 10_000_000_000UL);
            gl.DeleteSync(fence);
            if (wait != GlFunctions.ALREADY_SIGNALED && wait != GlFunctions.CONDITION_SATISFIED) return false;
            if (direct != null)
            {
                // GPU が描き終えた。取り込みを外して、フレームをエンコーダへ
                if (useDirect == null) return false;
                // CUDA: フレームへ写す命令が終わるまで待つ (NVENC は別のスレッドで、同じフレームを読む)
                if (_cudaOut != null && _cudaOut.Wait(direct.Frame) is var cr and not 0) { DisableDirect($"CUDA の処理が終わりません (CUDA のエラー {cr})"); return false; }
                ReleaseDirect(direct, disposeFrame: false);
                var frame = direct.Frame;
                direct = null;
                useDirect(frame);
                return true;
            }
            gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, _buffers[slot]);
            var p = gl.MapBufferRange(GlFunctions.PIXEL_PACK_BUFFER, 0, (nint)BufferBytes, GlFunctions.MAP_READ_BIT);
            if (p == null) { gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, 0); return false; }
            try { use((IntPtr)p, RowBytes); }
            finally
            {
                gl.UnmapBuffer(GlFunctions.PIXEL_PACK_BUFFER);
                gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, 0);
            }
            return true;
        }
        finally
        {
            if (direct != null) ReleaseDirect(direct, disposeFrame: true);
            _gpu.Context.ResetContext();
        }
    }

    public void Dispose()
    {
        _gpu.MakeCurrent();
        var gl = _gl;
        foreach (var f in _fences) if (f != IntPtr.Zero) gl.ClientWaitSync(f, GlFunctions.SYNC_FLUSH_COMMANDS_BIT, 1_000_000_000UL);
        foreach (var f in _fences) if (f != IntPtr.Zero) gl.DeleteSync(f);
        Array.Clear(_fences);
        for (int i = 0; i < Depth; i++)
            if (_direct[i] is { } d) { ReleaseDirect(d, disposeFrame: true); _direct[i] = null; }
        _pending.Clear();
        _cudaOut?.Dispose();
        _cudaOut = null;
        Yuv?.Dispose();
        Surface.Dispose();
        fixed (uint* b = _buffers) gl.DeleteBuffers(Depth, b);
        uint fbo = _fbo;
        gl.DeleteFramebuffers(1, &fbo);
        gl.DeleteTexture(_texture);
        _gpu.Context.ResetContext();
    }
}
