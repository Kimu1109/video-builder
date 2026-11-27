using System.Collections.Concurrent;
using SkiaSharp;
using VideoBuilder.Media;

namespace VideoBuilder.Rendering;

/// <summary>
/// デコードしたままの YUV の動画フレームを GPU へ送り、GPU で RGBA に変換して Skia の画像にする (設計書 22.52)。
/// CPU で BGRA に変換してから送るのに比べて、CPU の色変換がなくなり、送る量も 4 バイト/画素から 1.5 バイト/画素 (4:2:0) になる。
/// SkiaSharp 2.88 には YUV の画像を作る API (SKYuvaPixmaps) がなく、SkSL のシェーダも描画時に落ちるので、
/// OpenGL ES 3 (画面と共有するときはデスクトップの OpenGL 3) を直接使う (Y・U・V の面を R8 / RG8 のテクスチャに送り、自前のシェーダで RGBA8 のテクスチャに描く)。
/// シェーダは版の指定 (<see cref="GlFunctions.ShaderVersion"/>) を先頭に付けて作る。
/// 色の変換は BGRA に変換するとき (sws の既定) と同じ BT.601 (J 形式はフルレンジ) で、CPU 描画と同じ絵になる。
/// 描画のスレッドからだけ使う。
/// </summary>
internal sealed unsafe class YuvTextures : IDisposable
{
    private const string VertexShader = """
        out vec2 uv;
        void main() {
            vec2 p = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
            uv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string FragmentShader = """
        precision highp float;
        in vec2 uv;
        out vec4 color;
        uniform sampler2D ty;
        uniform sampler2D tu;
        uniform sampler2D tv;
        uniform int nv12;
        uniform mat3 m;
        uniform vec3 off;
        uniform vec2 chroma;
        uniform ivec2 csize;
        uniform float scale;
        void main() {
            vec2 c = uv * chroma;
            float y = texture(ty, uv).r * scale;
            vec2 uvc;
            if (nv12 == 2) {
                // sws の NV12 の変換 (x64) と同じ読み方: 横は 2 画素で同じ値、縦は補間する。
                // U・V の行 k は、Y の行 2k+1 の位置にあるとみなされる (sws で測った結果: 行 2k+1 で 1、行 2k・2k+2 で 0.5)
                float lumaRow = uv.y * float(textureSize(ty, 0).y) - 0.5;
                float row = (lumaRow - 1.0) * 0.5;
                int r0 = int(floor(row));
                int x = min(int(c.x * float(csize.x)), csize.x - 1);
                vec2 a = texelFetch(tu, ivec2(x, clamp(r0, 0, csize.y - 1)), 0).rg;
                vec2 b = texelFetch(tu, ivec2(x, clamp(r0 + 1, 0, csize.y - 1)), 0).rg;
                uvc = mix(a, b, row - float(r0));
            } else if (nv12 == 1) {
                uvc = texture(tu, c).rg;   // 補間しない (arm64 の sws と同じ)
            } else {
                uvc = vec2(texture(tu, c).r, texture(tv, c).r);
            }
            // scale: 10 ビット (P010) の 16 ビットの値を、8 ビットと同じ目盛り (リミテッドの 16〜235 が 64〜940 に当たる) にする
            uvc *= scale;
            color = vec4(clamp(m * (vec3(y, uvc) - off), 0.0, 1.0), 1.0);
        }
        """;

    private readonly GpuContext _gpu;
    private readonly GlFunctions _gl;
    private readonly uint _program, _vao, _fbo;
    private readonly int _locNv12, _locM, _locOff, _locChroma, _locCsize, _locScale;
    // Y・U (NV12 なら UV)・V の面のテクスチャ。大きさ・形式が変わったときだけ作り直す
    private readonly uint[] _planes = new uint[3];
    private readonly (int W, int H, uint Format)[] _planeShape = new (int, int, uint)[3];
    // 変換した絵 (RGBA8) のテクスチャ。Skia が使い終えたら (画像を手放したら) 戻ってくるので使い回す
    private readonly ConcurrentQueue<(uint Texture, int W, int H)> _returned = new();
    private readonly List<(uint Texture, int W, int H)> _free = new();
    private readonly SKImageTextureReleaseDelegate _release;
    private bool _disposed;
    // GPU 上のフレームを取り込むもの (使えなければ null。理由は HardwareError)
    private readonly EglDmaBuf? _dmaBuf;
    private CudaGl? _cuda;
    private readonly uint[] _imported = new uint[3];   // DMA-BUF を取り込むテクスチャ (Y・UV、I420 なら Y・U・V)
    // GPU が読み終えるまで手放さないもの (DMA-BUF の元のフレームはデコーダが使い回すので、描き終えてから返す)
    private readonly Queue<(IntPtr Fence, HardwareFrame Frame, IntPtr[] Images)> _inUse = new();

    /// <summary>GPU 上のまま取り込めるフレームの種類。</summary>
    public HardwareFrameKinds HardwareKinds { get; private set; }
    /// <summary>GPU 上のフレームを取り込めないとき (取り込めなくなったとき)、その理由。</summary>
    public string? HardwareError { get; private set; }
    /// <summary>GPU 上のまま取り込んで変換した回数 (計測・テスト用)。</summary>
    public long HardwareConverted { get; private set; }

    /// <summary>変換した回数 (計測・テスト用)。</summary>
    public long Converted { get; private set; }

    private YuvTextures(GpuContext gpu, GlFunctions gl, uint program)
    {
        _gpu = gpu; _gl = gl; _program = program;
        uint vao, fbo;
        gl.GenVertexArrays(1, &vao);
        gl.GenFramebuffers(1, &fbo);
        _vao = vao; _fbo = fbo;
        gl.UseProgram(program);
        gl.Uniform1i(gl.Uniform(program, "ty"), 0);
        gl.Uniform1i(gl.Uniform(program, "tu"), 1);
        gl.Uniform1i(gl.Uniform(program, "tv"), 2);
        _locNv12 = gl.Uniform(program, "nv12");
        _locM = gl.Uniform(program, "m");
        _locOff = gl.Uniform(program, "off");
        _locChroma = gl.Uniform(program, "chroma");
        _locCsize = gl.Uniform(program, "csize");
        _locScale = gl.Uniform(program, "scale");
        _release = ctx => { if (ctx is (uint t, int w, int h)) _returned.Enqueue((t, w, h)); };
        var errors = new List<string>();
        if (Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU_FRAMES") == "0") errors.Add("VIDEO_BUILDER_GPU_FRAMES=0");
        else
        {
            _dmaBuf = EglDmaBuf.Create(gpu, out var dmaError);
            if (_dmaBuf != null) HardwareKinds |= HardwareFrameKinds.Vaapi; else errors.Add("VAAPI: " + dmaError);
            _cuda = CudaGl.Create(gpu, out var cudaError);
            if (_cuda != null) HardwareKinds |= HardwareFrameKinds.Cuda; else errors.Add("CUDA: " + cudaError);
            // 10 ビット (P010) は 16 ビットのテクスチャ (R16・RG16) で読む (設計書 22.62)
            if (Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU_FRAMES_10BIT") == "0") errors.Add("10 ビット: VIDEO_BUILDER_GPU_FRAMES_10BIT=0");
            else if (!gl.Norm16) errors.Add("10 ビット: 16 ビットのテクスチャ (GL_EXT_texture_norm16) がありません");
            else if (HardwareKinds != HardwareFrameKinds.None) HardwareKinds |= HardwareFrameKinds.TenBit;
        }
        HardwareError = errors.Count == 0 ? null : string.Join(" / ", errors);
    }

    /// <summary>作れなければ null (error に理由)。描画のスレッドで、コンテキストを有効にしてから呼ぶ。</summary>
    public static YuvTextures? Create(GpuContext gpu, out string? error)
    {
        var gl = gpu.Gl;
        if (gl == null) { error = gpu.GlError ?? "OpenGL ES 3 / OpenGL 3 の関数を使えません"; return null; }
        uint program = gl.Program(gl.ShaderVersion + "\n" + VertexShader, gl.ShaderVersion + "\n" + FragmentShader, out error);
        try
        {
            if (program == 0) { error = "YUV の変換のシェーダを作れません: " + error; return null; }
            return new YuvTextures(gpu, gl, program);
        }
        finally { gpu.Context.ResetContext(); }
    }

    /// <summary>
    /// YUV のフレームを GPU で RGBA に変換した画像を返す。面の中身は、この呼び出しの中で GPU へ写し終える
    /// (戻ったあとは、デコーダが置き場を書き換えてもよい)。作れなければ null。
    /// </summary>
    public SKImage? Convert(in YuvPlanes p)
    {
        if (_disposed || p.Width <= 0 || p.Height <= 0) return null;
        var gl = _gl;
        int w = p.Width, h = p.Height, cw = p.ChromaWidth, ch = p.ChromaHeight;
        bool nv12 = p.Layout == YuvLayout.Nv12;
        // 行の長さは画素の数で指定するので、割り切れない並びは送れない (デコーダの置き場ではまず起きない)
        if (p.YStride < w || p.UStride % (nv12 ? 2 : 1) != 0) return null;
        uint output = TakeOutput(w, h);
        try
        {
            gl.BindBuffer(GlFunctions.PIXEL_UNPACK_BUFFER, 0);
            gl.PixelStorei(GlFunctions.UNPACK_ALIGNMENT, 1);
            gl.PixelStorei(GlFunctions.UNPACK_SKIP_ROWS, 0);
            gl.PixelStorei(GlFunctions.UNPACK_SKIP_PIXELS, 0);
            Plane(0, w, h, GlFunctions.R8, GlFunctions.RED, p.Y, p.YStride, 1, GlFunctions.NEAREST);
            // U・V は補間せずに使う (sws の拡大縮小しない変換と同じ。2x2 画素が同じ U・V になる)
            if (nv12) Plane(1, cw, ch, GlFunctions.RG8, GlFunctions.RG, p.U, p.UStride, 2, GlFunctions.NEAREST);
            else
            {
                Plane(1, cw, ch, GlFunctions.R8, GlFunctions.RED, p.U, p.UStride, 1, GlFunctions.NEAREST);
                Plane(2, cw, ch, GlFunctions.R8, GlFunctions.RED, p.V, p.VStride, 1, GlFunctions.NEAREST);
            }
            gl.PixelStorei(GlFunctions.UNPACK_ROW_LENGTH, 0);
            int mode = !nv12 ? 0 : VideoReader.SwsInterpolatesNv12ChromaVertically ? 2 : 1;
            if (!Draw(output, w, h, w, h, cw, ch, p.ChromaShiftX, p.ChromaShiftY, p.FullRange, mode, 1f, _planes[0], _planes[1], nv12 ? _planes[1] : _planes[2]))
            {
                ReturnOutput(output, w, h);
                return null;
            }
        }
        finally
        {
            // Skia が覚えている GL の状態 (結び付けたテクスチャ・描画先など) を捨てさせる
            _gpu.Context.ResetContext();
        }
        return Wrap(output, w, h);
    }

    /// <summary>描いた絵 (RGBA8 のテクスチャ) を Skia の画像にする。</summary>
    private SKImage? Wrap(uint output, int w, int h)
    {
        var info = new GRGlTextureInfo(GlFunctions.TEXTURE_2D, output, GlFunctions.RGBA8);
        using var backend = new GRBackendTexture(w, h, false, info);
        var image = SKImage.FromTexture(_gpu.Context, backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Opaque, null, _release, (output, w, h));
        if (image == null) { ReturnOutput(output, w, h); return null; }
        Converted++;
        return image;
    }

    /// <summary>
    /// Y・U・V のテクスチャから、output (ow x oh の RGBA8) に描く。w・h は元のフレームの大きさ、cw・ch は U・V の面の大きさ。
    /// mode は NV12 の U・V の読み方 (0: NV12 でない, 1: 補間しない/テクスチャの補間に任せる, 2: sws の x64 と同じく縦に補間)。
    /// </summary>
    private bool Draw(uint output, int w, int h, int ow, int oh, int cw, int ch, int shiftX, int shiftY, bool fullRange, int mode, float scale, uint ty, uint tu, uint tv)
    {
        var gl = _gl;
        {
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, _fbo);
            gl.FramebufferTexture2D(GlFunctions.FRAMEBUFFER, GlFunctions.COLOR_ATTACHMENT0, GlFunctions.TEXTURE_2D, output, 0);
            if (gl.CheckFramebufferStatus(GlFunctions.FRAMEBUFFER) != GlFunctions.FRAMEBUFFER_COMPLETE)
            {
                gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
                return false;
            }
            gl.Viewport(0, 0, ow, oh);
            foreach (var cap in new[] { GlFunctions.BLEND, GlFunctions.SCISSOR_TEST, GlFunctions.DEPTH_TEST, GlFunctions.STENCIL_TEST, GlFunctions.CULL_FACE, GlFunctions.DITHER })
                gl.Disable(cap);
            gl.ColorMask(1, 1, 1, 1);
            gl.UseProgram(_program);
            // NV12 の U・V を縦に補間するかは、sws に合わせる (CPU の種類で違う)
            gl.Uniform1i(_locNv12, mode);
            var (matrix, offset) = Coefficients(fullRange);
            fixed (float* mp = matrix) gl.UniformMatrix3fv(_locM, 1, 0, mp);
            gl.Uniform3f(_locOff, offset.X, offset.Y, offset.Z);
            // U・V の面は、Y の面と同じ範囲を指すように (奇数の幅・高さでは、U・V の面の方が少し広い)
            gl.Uniform2f(_locChroma, (float)w / (cw << shiftX), (float)h / (ch << shiftY));
            gl.Uniform2i(_locCsize, cw, ch);
            gl.Uniform1f(_locScale, scale);
            var textures = new[] { ty, tu, tv };
            for (int i = 0; i < 3; i++)
            {
                gl.ActiveTexture(GlFunctions.TEXTURE0 + (uint)i);
                gl.BindTexture(GlFunctions.TEXTURE_2D, textures[i]);
            }
            gl.BindVertexArray(_vao);
            gl.DrawArrays(GlFunctions.TRIANGLE_STRIP, 0, 4);
            gl.BindVertexArray(0);
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
            gl.ActiveTexture(GlFunctions.TEXTURE0);
        }
        return true;
    }

    /// <summary>
    /// GPU 上のフレーム (メモリへ写していないもの。8 ビットの NV12・I420、10 ビットの P010) を、GPU の中で RGBA に変換した画像を返す
    /// (設計書 22.58・22.62)。ow・oh は作る画像の大きさ (縮めてデコードするときは元より小さい)。frame はここで引き取る (GPU が読み終えたら手放す)。
    /// 取り込めなければ null (frame は手放す。その種類 (10 ビットのときは 10 ビット) はこのあと使わない)。
    /// </summary>
    public SKImage? ConvertHardware(HardwareFrame frame, int ow, int oh)
    {
        ReleaseFinished(wait: false);
        bool tenBit = frame.Surface == HardwareSurface.P010;
        if (_disposed || (HardwareKinds & frame.Kind) == 0 || tenBit && (HardwareKinds & HardwareFrameKinds.TenBit) == 0) { frame.Dispose(); return null; }
        var gl = _gl;
        int w = frame.Width, h = frame.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        bool planar = frame.Surface == HardwareSurface.I420;
        // 元の大きさなら、メモリへ写したときと同じ読み方 (CPU 描画と同じ絵)。縮めるときはテクスチャの補間で縮める
        bool scaled = ow != w || oh != h;
        int filter = scaled ? GlFunctions.LINEAR : GlFunctions.NEAREST;
        int mode = planar ? 0 : scaled ? 1 : VideoReader.SwsInterpolatesNv12ChromaVertically ? 2 : 1;
        // 16 ビットの値 (上位 10 ビット) を 8 ビットと同じ目盛りに: リミテッドは X/1020 (64〜940 が 16/255〜235/255)、フルは X/1023
        float scale = !tenBit ? 1f : 65535f / (64f * (frame.FullRange ? 1023f : 1020f));
        uint output = TakeOutput(ow, oh);
        IntPtr[] images = Array.Empty<IntPtr>();
        string? error = null;
        bool keep = false;
        try
        {
            uint ty, tu, tv;
            if (frame.Kind == HardwareFrameKinds.Vaapi)
            {
                if (!frame.TryMapDmaBuf(out var layers, out error)) return Fail();
                var expected = frame.Surface switch
                {
                    HardwareSurface.P010 => new[] { HardwareFrame.DrmR16, HardwareFrame.DrmGR1616 },
                    HardwareSurface.I420 => new[] { HardwareFrame.DrmR8, HardwareFrame.DrmR8, HardwareFrame.DrmR8 },
                    _ => new[] { HardwareFrame.DrmR8, HardwareFrame.DrmGR88 },
                };
                if (!layers.Select(l => l.DrmFormat).SequenceEqual(expected))
                {
                    error = $"DMA-BUF の形式が {frame.Surface} の並びではありません: " + string.Join(", ", layers.Select(l => $"0x{l.DrmFormat:X8}"));
                    return Fail();
                }
                images = new IntPtr[layers.Length];
                for (int i = 0; i < layers.Length; i++)
                {
                    images[i] = _dmaBuf!.Import(layers[i]);
                    if (images[i] == IntPtr.Zero) { error = $"DMA-BUF を EGL に取り込めません (0x{EglApi.GetError():X4}, 0x{layers[i].DrmFormat:X8}, modifier 0x{layers[i].Modifier:X})"; return Fail(); }
                    if (_imported[i] == 0) _imported[i] = gl.GenTexture();
                    gl.GetError();
                    gl.BindTexture(GlFunctions.TEXTURE_2D, _imported[i]);
                    _dmaBuf.BindToTexture(images[i]);
                    if (gl.GetError() is var e and not 0) { error = $"DMA-BUF をテクスチャにできません (0x{e:X4})"; return Fail(); }
                    SetFilter(filter);
                }
                ty = _imported[0]; tu = _imported[1]; tv = planar ? _imported[2] : _imported[1];
            }
            else
            {
                // CUDA (NVDEC) は NV12 か P010
                if (planar) { error = "CUDA のフレームが I420 です"; return Fail(); }
                uint fy = tenBit ? GlFunctions.R16 : GlFunctions.R8, fuv = tenBit ? GlFunctions.RG16 : GlFunctions.RG8;
                uint type = tenBit ? GlFunctions.UNSIGNED_SHORT : GlFunctions.UNSIGNED_BYTE;
                int bytes = tenBit ? 2 : 1;
                EnsurePlane(0, w, h, fy, GlFunctions.RED, type);
                EnsurePlane(1, cw, ch, fuv, GlFunctions.RG, type);
                for (int i = 0; i < 2; i++) { gl.BindTexture(GlFunctions.TEXTURE_2D, _planes[i]); SetFilter(filter); }
                int r = _cuda!.Copy(frame, new[] { (_planes[0], w * bytes, h), (_planes[1], cw * 2 * bytes, ch) });
                if (r != 0) { error = $"CUDA から GL のテクスチャへ写せません (CUDA のエラー {r})"; return Fail(); }
                ty = _planes[0]; tu = tv = _planes[1];
            }
            if (!Draw(output, w, h, ow, oh, cw, ch, 1, 1, frame.FullRange, mode, scale, ty, tu, tv)) { error = "変換の描画先を作れません"; return Fail(); }
            if (frame.Kind == HardwareFrameKinds.Vaapi)
            {
                // デコーダの置き場を GPU が読み終えるまで持っておく
                var fence = gl.FenceSync(GlFunctions.SYNC_GPU_COMMANDS_COMPLETE, 0);
                gl.Flush();
                _inUse.Enqueue((fence, frame, images));
                keep = true;
            }
            // 縮めたときのために、使い回す面のテクスチャの補間を元に戻しておく
            if (frame.Kind == HardwareFrameKinds.Cuda && scaled)
                for (int i = 0; i < 2; i++) { gl.BindTexture(GlFunctions.TEXTURE_2D, _planes[i]); SetFilter(GlFunctions.NEAREST); }
        }
        finally
        {
            if (!keep)
            {
                foreach (var img in images) _dmaBuf?.Destroy(img);
                frame.Dispose();
            }
            _gpu.Context.ResetContext();
        }
        HardwareConverted++;
        if (tenBit) HardwareConverted10Bit++;
        return Wrap(output, ow, oh);

        SKImage? Fail()
        {
            ReturnOutput(output, ow, oh);
            // 10 ビットだけの失敗 (16 ビットの形式を取り込めないなど) なら、8 ビットはこれまでどおり使う
            HardwareKinds &= tenBit ? ~HardwareFrameKinds.TenBit : ~(frame.Kind);
            if ((HardwareKinds & (HardwareFrameKinds.Vaapi | HardwareFrameKinds.Cuda)) == 0) HardwareKinds = HardwareFrameKinds.None;
            HardwareError = (tenBit ? "10 ビット: " : "") + error;
            return null;
        }
    }

    /// <summary>GPU 上のまま変換した 10 ビット (P010) のフレームの数 (計測・テスト用)。</summary>
    public long HardwareConverted10Bit { get; private set; }

    private void SetFilter(int filter)
    {
        _gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MIN_FILTER, filter);
        _gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MAG_FILTER, filter);
        _gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_WRAP_S, GlFunctions.CLAMP_TO_EDGE);
        _gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_WRAP_T, GlFunctions.CLAMP_TO_EDGE);
    }

    /// <summary>GPU が読み終えたフレームを手放す。wait なら、全部読み終えるまで待つ。</summary>
    private void ReleaseFinished(bool wait)
    {
        while (_inUse.TryPeek(out var item))
        {
            if (item.Fence != IntPtr.Zero)
            {
                uint r = _gl.ClientWaitSync(item.Fence, wait ? GlFunctions.SYNC_FLUSH_COMMANDS_BIT : 0, wait ? 1_000_000_000UL : 0);
                // 古いものが溜まりすぎたら待つ (デコーダの置き場が足りなくならないように)
                if (r == GlFunctions.TIMEOUT_EXPIRED && !wait && _inUse.Count > 3)
                    r = _gl.ClientWaitSync(item.Fence, GlFunctions.SYNC_FLUSH_COMMANDS_BIT, 1_000_000_000UL);
                if (r == GlFunctions.TIMEOUT_EXPIRED && !wait) return;
                _gl.DeleteSync(item.Fence);
            }
            _inUse.Dequeue();
            foreach (var img in item.Images) _dmaBuf?.Destroy(img);
            item.Frame.Dispose();
        }
    }

    /// <summary>BT.601 の YUV → RGB (値は 0〜1)。列ごとに並べる (GL の mat3)。</summary>
    internal static (float[] Matrix, (float X, float Y, float Z) Offset) Coefficients(bool fullRange)
    {
        // sws の既定 (SWS_CS_ITU601) と同じ係数。リミテッドは Y が 16〜235、U・V が 16〜240
        float ys = fullRange ? 1f : 255f / 219f;
        float vr = fullRange ? 1.402f : 1.596027f, ug = fullRange ? -0.344136f : -0.391762f;
        float vg = fullRange ? -0.714136f : -0.812968f, ub = fullRange ? 1.772f : 2.017232f;
        var m = new[] { ys, ys, ys, 0, ug, ub, vr, vg, 0 };
        return (m, (fullRange ? 0 : 16f / 255f, 128f / 255f, 128f / 255f));
    }

    /// <summary>面のテクスチャを、この大きさ・形式で用意して結び付ける (違うときだけ作り直す)。</summary>
    private void EnsurePlane(int i, int w, int h, uint internalFormat, uint format, uint type = GlFunctions.UNSIGNED_BYTE)
    {
        var gl = _gl;
        if (_planes[i] == 0) _planes[i] = gl.GenTexture();
        gl.BindTexture(GlFunctions.TEXTURE_2D, _planes[i]);
        if (_planeShape[i] != (w, h, internalFormat))
        {
            _cuda?.Forget(_planes[i]);   // 中身の置き場が変わるので、CUDA への登録をやめる
            gl.TexImage2D(GlFunctions.TEXTURE_2D, 0, (int)internalFormat, w, h, 0, format, type, null);
            SetFilter(GlFunctions.NEAREST);
            _planeShape[i] = (w, h, internalFormat);
        }
    }

    private void Plane(int i, int w, int h, uint internalFormat, uint format, IntPtr data, int stride, int bytesPerPixel, int filter)
    {
        var gl = _gl;
        EnsurePlane(i, w, h, internalFormat, format);
        gl.PixelStorei(GlFunctions.UNPACK_ROW_LENGTH, stride / bytesPerPixel);
        gl.TexSubImage2D(GlFunctions.TEXTURE_2D, 0, 0, 0, w, h, format, GlFunctions.UNSIGNED_BYTE, (void*)data);
    }

    private uint TakeOutput(int w, int h)
    {
        while (_returned.TryDequeue(out var r)) _free.Add(r);
        for (int i = 0; i < _free.Count; i++)
        {
            if (_free[i].W != w || _free[i].H != h) continue;
            uint t = _free[i].Texture;
            _free.RemoveAt(i);
            return t;
        }
        // 大きさの違うものが溜まりすぎないよう、古いものから消す
        while (_free.Count >= 4) { _gl.DeleteTexture(_free[0].Texture); _free.RemoveAt(0); }
        uint texture = _gl.GenTexture();
        _gl.BindTexture(GlFunctions.TEXTURE_2D, texture);
        _gl.TexImage2D(GlFunctions.TEXTURE_2D, 0, (int)GlFunctions.RGBA8, w, h, 0, GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, null);
        _gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MIN_FILTER, GlFunctions.LINEAR);
        _gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MAG_FILTER, GlFunctions.LINEAR);
        return texture;
    }

    private void ReturnOutput(uint texture, int w, int h) => _free.Add((texture, w, h));

    /// <summary>GL のものを消す。描画のスレッドで、コンテキストを有効にしてから呼ぶ。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var gl = _gl;
        ReleaseFinished(wait: true);
        _cuda?.Dispose();
        _cuda = null;
        foreach (var t in _imported) gl.DeleteTexture(t);
        while (_returned.TryDequeue(out var r)) _free.Add(r);
        foreach (var f in _free) gl.DeleteTexture(f.Texture);
        _free.Clear();
        foreach (var t in _planes) gl.DeleteTexture(t);
        uint vao = _vao, fbo = _fbo;
        gl.DeleteVertexArrays(1, &vao);
        gl.DeleteFramebuffers(1, &fbo);
        gl.DeleteProgram(_program);
        _gpu.Context.ResetContext();
    }
}
