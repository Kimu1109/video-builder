using System.Collections.Concurrent;
using SkiaSharp;
using VideoBuilder.Media;

namespace VideoBuilder.Rendering;

/// <summary>
/// デコードしたままの YUV の動画フレームを GPU へ送り、GPU で RGBA に変換して Skia の画像にする (設計書 22.52)。
/// CPU で BGRA に変換してから送るのに比べて、CPU の色変換がなくなり、送る量も 4 バイト/画素から 1.5 バイト/画素 (4:2:0) になる。
/// SkiaSharp 2.88 には YUV の画像を作る API (SKYuvaPixmaps) がなく、SkSL のシェーダも描画時に落ちるので、
/// OpenGL ES 3 を直接使う (Y・U・V の面を R8 / RG8 のテクスチャに送り、自前のシェーダで RGBA8 のテクスチャに描く)。
/// 色の変換は BGRA に変換するとき (sws の既定) と同じ BT.601 (J 形式はフルレンジ) で、CPU 描画と同じ絵になる。
/// 描画のスレッドからだけ使う。
/// </summary>
internal sealed unsafe class YuvTextures : IDisposable
{
    private const string VertexShader = """
        #version 300 es
        out vec2 uv;
        void main() {
            vec2 p = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
            uv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string FragmentShader = """
        #version 300 es
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
        void main() {
            vec2 c = uv * chroma;
            float y = texture(ty, uv).r;
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
            color = vec4(clamp(m * (vec3(y, uvc) - off), 0.0, 1.0), 1.0);
        }
        """;

    private readonly GpuContext _gpu;
    private readonly GlFunctions _gl;
    private readonly uint _program, _vao, _fbo;
    private readonly int _locNv12, _locM, _locOff, _locChroma, _locCsize;
    // Y・U (NV12 なら UV)・V の面のテクスチャ。大きさ・形式が変わったときだけ作り直す
    private readonly uint[] _planes = new uint[3];
    private readonly (int W, int H, uint Format)[] _planeShape = new (int, int, uint)[3];
    // 変換した絵 (RGBA8) のテクスチャ。Skia が使い終えたら (画像を手放したら) 戻ってくるので使い回す
    private readonly ConcurrentQueue<(uint Texture, int W, int H)> _returned = new();
    private readonly List<(uint Texture, int W, int H)> _free = new();
    private readonly SKImageTextureReleaseDelegate _release;
    private bool _disposed;

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
        _release = ctx => { if (ctx is (uint t, int w, int h)) _returned.Enqueue((t, w, h)); };
    }

    /// <summary>作れなければ null (error に理由)。描画のスレッドで、コンテキストを有効にしてから呼ぶ。</summary>
    public static YuvTextures? Create(GpuContext gpu, out string? error)
    {
        var gl = gpu.Gl;
        if (gl == null) { error = gpu.GlError ?? "OpenGL ES 3 の関数を使えません"; return null; }
        uint program = gl.Program(VertexShader, FragmentShader, out error);
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

            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, _fbo);
            gl.FramebufferTexture2D(GlFunctions.FRAMEBUFFER, GlFunctions.COLOR_ATTACHMENT0, GlFunctions.TEXTURE_2D, output, 0);
            if (gl.CheckFramebufferStatus(GlFunctions.FRAMEBUFFER) != GlFunctions.FRAMEBUFFER_COMPLETE)
            {
                ReturnOutput(output, w, h);
                return null;
            }
            gl.Viewport(0, 0, w, h);
            foreach (var cap in new[] { GlFunctions.BLEND, GlFunctions.SCISSOR_TEST, GlFunctions.DEPTH_TEST, GlFunctions.STENCIL_TEST, GlFunctions.CULL_FACE, GlFunctions.DITHER })
                gl.Disable(cap);
            gl.ColorMask(1, 1, 1, 1);
            gl.UseProgram(_program);
            // NV12 の U・V を縦に補間するかは、sws に合わせる (CPU の種類で違う)
            gl.Uniform1i(_locNv12, !nv12 ? 0 : VideoReader.SwsInterpolatesNv12ChromaVertically ? 2 : 1);
            var (matrix, offset) = Coefficients(p.FullRange);
            fixed (float* mp = matrix) gl.UniformMatrix3fv(_locM, 1, 0, mp);
            gl.Uniform3f(_locOff, offset.X, offset.Y, offset.Z);
            // U・V の面は、Y の面と同じ範囲を指すように (奇数の幅・高さでは、U・V の面の方が少し広い)
            gl.Uniform2f(_locChroma, (float)w / (cw << p.ChromaShiftX), (float)h / (ch << p.ChromaShiftY));
            gl.Uniform2i(_locCsize, cw, ch);
            for (int i = 0; i < 3; i++)
            {
                gl.ActiveTexture(GlFunctions.TEXTURE0 + (uint)i);
                gl.BindTexture(GlFunctions.TEXTURE_2D, i == 2 && nv12 ? _planes[1] : _planes[i]);
            }
            gl.BindVertexArray(_vao);
            gl.DrawArrays(GlFunctions.TRIANGLE_STRIP, 0, 4);
            gl.BindVertexArray(0);
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
        }
        finally
        {
            // Skia が覚えている GL の状態 (結び付けたテクスチャ・描画先など) を捨てさせる
            _gpu.Context.ResetContext();
        }

        var info = new GRGlTextureInfo(GlFunctions.TEXTURE_2D, output, GlFunctions.RGBA8);
        using var backend = new GRBackendTexture(w, h, false, info);
        var image = SKImage.FromTexture(_gpu.Context, backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Opaque, null, _release, (output, w, h));
        if (image == null) { ReturnOutput(output, w, h); return null; }
        Converted++;
        return image;
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

    private void Plane(int i, int w, int h, uint internalFormat, uint format, IntPtr data, int stride, int bytesPerPixel, int filter)
    {
        var gl = _gl;
        if (_planes[i] == 0) _planes[i] = gl.GenTexture();
        gl.BindTexture(GlFunctions.TEXTURE_2D, _planes[i]);
        if (_planeShape[i] != (w, h, internalFormat))
        {
            gl.TexImage2D(GlFunctions.TEXTURE_2D, 0, (int)internalFormat, w, h, 0, format, GlFunctions.UNSIGNED_BYTE, null);
            gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MIN_FILTER, filter);
            gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MAG_FILTER, filter);
            gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_WRAP_S, GlFunctions.CLAMP_TO_EDGE);
            gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_WRAP_T, GlFunctions.CLAMP_TO_EDGE);
            _planeShape[i] = (w, h, internalFormat);
        }
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
