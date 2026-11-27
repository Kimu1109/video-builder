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
    /// <summary>描く先。RenderEngine.RenderToGpuSurface に渡す。</summary>
    public SKSurface Surface { get; }
    /// <summary>読み戻しの途中のフレームの数。</summary>
    public int Pending => _pending.Count;

    private AsyncReadback(GpuContext gpu, GlFunctions gl, int w, int h, uint texture, uint fbo, SKSurface surface)
    {
        _gpu = gpu; _gl = gl; Width = w; Height = h; _texture = texture; _fbo = fbo; Surface = surface;
    }

    /// <summary>作れなければ null (error に理由)。GPU コンテキストを有効にしたスレッドで呼ぶ。</summary>
    public static AsyncReadback? Create(GpuContext gpu, int w, int h, out string? error)
    {
        error = null;
        if (Environment.GetEnvironmentVariable("VIDEO_BUILDER_ASYNC_READBACK") == "0") { error = "VIDEO_BUILDER_ASYNC_READBACK=0"; return null; }
        gpu.MakeCurrent();
        var gl = gpu.Gl;
        if (gl == null) { error = gpu.GlError ?? "OpenGL ES 3 / OpenGL 3 の関数を使えません"; return null; }
        uint texture = 0, fbo = 0;
        var buffers = new uint[Depth];
        SKSurface? surface = null;
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
                gl.BufferData(GlFunctions.PIXEL_PACK_BUFFER, (nint)w * h * 4, null, GlFunctions.STREAM_READ);
            }
            gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, 0);
            if (gl.GetError() != 0) { error = "読み戻し用のバッファを作れません"; return null; }
            gpu.Context.ResetContext();
            var info = new GRGlTextureInfo(GlFunctions.TEXTURE_2D, texture, GlFunctions.RGBA8);
            using var backend = new GRBackendTexture(w, h, false, info);
            // 左上が原点: テクスチャの 0 行目が絵のいちばん上になる (glReadPixels も 0 行目から返す)
            surface = SKSurface.Create(gpu.Context, backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888);
            if (surface == null) { error = "Skia でテクスチャに描けません"; return null; }
            var result = new AsyncReadback(gpu, gl, w, h, texture, fbo, surface);
            Array.Copy(buffers, result._buffers, Depth);
            surface = null; texture = fbo = 0; Array.Clear(buffers);
            return result;
        }
        finally
        {
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
            gl.BindFramebuffer(GlFunctions.READ_FRAMEBUFFER, _fbo);
            gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, _buffers[slot]);
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
    /// 渡したポインタは use の中でだけ使える。読み戻せなければ false (use は呼ばない)。
    /// </summary>
    public bool Finish(Action<IntPtr, int> use)
    {
        if (!_pending.TryDequeue(out int slot)) return false;
        if (TestFailAfter >= 0 && _finished++ >= TestFailAfter) return false;
        var gl = _gl;
        _gpu.MakeCurrent();
        try
        {
            var fence = _fences[slot];
            _fences[slot] = IntPtr.Zero;
            if (fence == IntPtr.Zero) return false;
            // 最大 10 秒待つ (ふつうは数 ms。GPU が止まったときに書き出しごと止まらないように)
            uint wait = gl.ClientWaitSync(fence, GlFunctions.SYNC_FLUSH_COMMANDS_BIT, 10_000_000_000UL);
            gl.DeleteSync(fence);
            if (wait != GlFunctions.ALREADY_SIGNALED && wait != GlFunctions.CONDITION_SATISFIED) return false;
            gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, _buffers[slot]);
            var p = gl.MapBufferRange(GlFunctions.PIXEL_PACK_BUFFER, 0, (nint)RowBytes * Height, GlFunctions.MAP_READ_BIT);
            if (p == null) { gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, 0); return false; }
            try { use((IntPtr)p, RowBytes); }
            finally
            {
                gl.UnmapBuffer(GlFunctions.PIXEL_PACK_BUFFER);
                gl.BindBuffer(GlFunctions.PIXEL_PACK_BUFFER, 0);
            }
            return true;
        }
        finally { _gpu.Context.ResetContext(); }
    }

    public void Dispose()
    {
        _gpu.MakeCurrent();
        var gl = _gl;
        foreach (var f in _fences) if (f != IntPtr.Zero) gl.DeleteSync(f);
        Array.Clear(_fences);
        _pending.Clear();
        Surface.Dispose();
        fixed (uint* b = _buffers) gl.DeleteBuffers(Depth, b);
        uint fbo = _fbo;
        gl.DeleteFramebuffers(1, &fbo);
        gl.DeleteTexture(_texture);
        _gpu.Context.ResetContext();
    }
}
