using VideoBuilder.Media;

namespace VideoBuilder.Rendering;

/// <summary>
/// 書き出し用: GPU で描いた RGBA の絵を、GPU のシェーダでエンコーダの形式 (NV12 か YUV420P) に変換する (設計書 22.58)。
/// 読み戻す量が 4 バイト/画素から 1.5 バイト/画素になり、CPU の色変換 (sws) もなくなる。
/// 変換先は RGBA8 のテクスチャで、1 つの画素に 4 つの値を詰める (OpenGL ES 3 で必ず読み戻せる形式が RGBA だけのため)。
/// 色の式は sws と同じ (BT.709 か BT.601、リミテッドレンジ)。U・V は 2x2 画素の平均。
/// 作ったスレッド (GPU コンテキストを有効にしたスレッド) からだけ使う。
/// </summary>
internal sealed unsafe class GpuYuvExport : IDisposable
{
    private const string VertexShader = """
        void main() {
            vec2 p = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    // pass 0: Y (4 画素), 1: NV12 の U・V (U・V の 2 組), 2: U (4 つ), 3: V (4 つ)
    // 詰めずに描くとき (VAAPI の面へ直接): 4: Y (R8 へ), 5: U・V (GR88 へ)
    private const string FragmentShader = """
        precision highp float;
        out vec4 color;
        uniform sampler2D src;
        uniform int pass;
        uniform vec3 ky;
        uniform vec3 ku;
        uniform vec3 kv;
        uniform vec3 off;
        uniform ivec2 size;
        vec3 px(int x, int y) { return texelFetch(src, ivec2(clamp(x, 0, size.x - 1), clamp(y, 0, size.y - 1)), 0).rgb; }
        vec3 avg(int cx, int cy) {
            int x = cx * 2, y = cy * 2;
            return (px(x, y) + px(x + 1, y) + px(x, y + 1) + px(x + 1, y + 1)) * 0.25;
        }
        void main() {
            ivec2 t = ivec2(gl_FragCoord.xy);
            if (pass == 0) {
                int x = t.x * 4;
                color = vec4(dot(ky, px(x, t.y)), dot(ky, px(x + 1, t.y)), dot(ky, px(x + 2, t.y)), dot(ky, px(x + 3, t.y))) + off.x;
            } else if (pass == 1) {
                int cx = t.x * 2;
                vec3 a = avg(cx, t.y), b = avg(cx + 1, t.y);
                color = vec4(dot(ku, a) + off.y, dot(kv, a) + off.z, dot(ku, b) + off.y, dot(kv, b) + off.z);
            } else if (pass == 4) {
                color = vec4(dot(ky, px(t.x, t.y)) + off.x, 0.0, 0.0, 1.0);
            } else if (pass == 5) {
                vec3 a = avg(t.x, t.y);
                color = vec4(dot(ku, a) + off.y, dot(kv, a) + off.z, 0.0, 1.0);
            } else {
                vec3 k = pass == 2 ? ku : kv;
                float o = pass == 2 ? off.y : off.z;
                int cx = t.x * 4;
                color = vec4(dot(k, avg(cx, t.y)), dot(k, avg(cx + 1, t.y)), dot(k, avg(cx + 2, t.y)), dot(k, avg(cx + 3, t.y))) + o;
            }
        }
        """;

    private readonly GpuContext _gpu;
    private readonly GlFunctions _gl;
    private readonly uint _program, _vao;
    private readonly int _locPass, _locKy, _locKu, _locKv, _locOff, _locSize;
    // 面ごとの変換先 (テクスチャ・FBO・大きさ (テクセル)・読み戻したときの位置)
    private readonly (uint Texture, uint Fbo, int W, int H, int Pass, long Offset)[] _targets;

    public int Width { get; }
    public int Height { get; }
    public YuvLayout Layout { get; }
    public bool Bt709 { get; }
    /// <summary>読み戻すバイト数 (全部の面)。</summary>
    public long Bytes { get; }

    private GpuYuvExport(GpuContext gpu, GlFunctions gl, uint program, int w, int h, YuvLayout layout, bool bt709)
    {
        _gpu = gpu; _gl = gl; _program = program; Width = w; Height = h; Layout = layout; Bt709 = bt709;
        uint vao;
        gl.GenVertexArrays(1, &vao);
        _vao = vao;
        gl.UseProgram(program);
        gl.Uniform1i(gl.Uniform(program, "src"), 0);
        _locPass = gl.Uniform(program, "pass");
        _locKy = gl.Uniform(program, "ky"); _locKu = gl.Uniform(program, "ku"); _locKv = gl.Uniform(program, "kv");
        _locOff = gl.Uniform(program, "off"); _locSize = gl.Uniform(program, "size");
        int cw = w / 2, ch = h / 2;
        var shapes = layout == YuvLayout.Nv12
            ? new[] { (Div4(w), h, 0), (Div4(w), ch, 1) }               // U・V の 1 行は w バイト (U・V の組が cw 個)
            : new[] { (Div4(w), h, 0), (Div4(cw), ch, 2), (Div4(cw), ch, 3) };
        _targets = new (uint, uint, int, int, int, long)[shapes.Length];
        long offset = 0;
        for (int i = 0; i < shapes.Length; i++)
        {
            var (tw, th, pass) = shapes[i];
            uint tex = gl.GenTexture();
            gl.BindTexture(GlFunctions.TEXTURE_2D, tex);
            gl.TexImage2D(GlFunctions.TEXTURE_2D, 0, (int)GlFunctions.RGBA8, tw, th, 0, GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, null);
            gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MIN_FILTER, GlFunctions.NEAREST);
            gl.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEXTURE_MAG_FILTER, GlFunctions.NEAREST);
            uint fbo;
            gl.GenFramebuffers(1, &fbo);
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, fbo);
            gl.FramebufferTexture2D(GlFunctions.FRAMEBUFFER, GlFunctions.COLOR_ATTACHMENT0, GlFunctions.TEXTURE_2D, tex, 0);
            _targets[i] = (tex, fbo, tw, th, pass, offset);
            offset += (long)tw * 4 * th;
        }
        gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
        Bytes = offset;
    }

    private static int Div4(int n) => (n + 3) / 4;

    /// <summary>
    /// 作れなければ null (error に理由)。layout はエンコーダの形式 (NV12 か YUV420P)。幅と高さは偶数であること。
    /// GPU コンテキストを有効にしたスレッドで呼ぶ。
    /// </summary>
    public static GpuYuvExport? Create(GpuContext gpu, int w, int h, YuvLayout layout, bool bt709, out string? error)
    {
        error = null;
        if (Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU_YUV_EXPORT") == "0") { error = "VIDEO_BUILDER_GPU_YUV_EXPORT=0"; return null; }
        if ((w & 1) != 0 || (h & 1) != 0) { error = "幅か高さが奇数"; return null; }
        var gl = gpu.Gl;
        if (gl == null) { error = gpu.GlError ?? "OpenGL ES 3 / OpenGL 3 の関数を使えません"; return null; }
        try
        {
            uint program = gl.Program(gl.ShaderVersion + "\n" + VertexShader, gl.ShaderVersion + "\n" + FragmentShader, out error);
            if (program == 0) { error = "YUV への変換のシェーダを作れません: " + error; return null; }
            var result = new GpuYuvExport(gpu, gl, program, w, h, layout, bt709);
            foreach (var t in result._targets)
            {
                gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, t.Fbo);
                bool complete = gl.CheckFramebufferStatus(GlFunctions.FRAMEBUFFER) == GlFunctions.FRAMEBUFFER_COMPLETE;
                gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
                if (!complete) { result.Dispose(); error = "YUV への変換の描画先を作れません"; return null; }
            }
            return result;
        }
        finally { gpu.Context.ResetContext(); }
    }

    /// <summary>
    /// 係数 (Y・U・V それぞれ、0〜1 の R・G・B に掛けるもの) と足す値。sws の RGB → YUV (リミテッドレンジ) と同じ式。
    /// </summary>
    internal static ((float R, float G, float B) Y, (float R, float G, float B) U, (float R, float G, float B) V, (float Y, float U, float V) Offset) Coefficients(bool bt709)
    {
        double kr = bt709 ? 0.2126 : 0.299, kb = bt709 ? 0.0722 : 0.114, kg = 1 - kr - kb;
        double ys = 219.0 / 255, cs = 224.0 / 255;
        double us = cs / (2 * (1 - kb)), vs = cs / (2 * (1 - kr));
        return (((float)(ys * kr), (float)(ys * kg), (float)(ys * kb)),
                ((float)(-us * kr), (float)(-us * kg), (float)(us * (1 - kb))),
                ((float)(vs * (1 - kr)), (float)(-vs * kg), (float)(-vs * kb)),
                (16f / 255, 128f / 255, 128f / 255));
    }

    /// <summary>
    /// source (RGBA8、左上が原点のテクスチャ) を変換し、今結び付けている PIXEL_PACK_BUFFER の先頭から、面を順に読み戻す命令を出す (待たない)。
    /// </summary>
    public void ConvertAndRead(uint source)
    {
        var gl = _gl;
        Begin(source);
        foreach (var t in _targets)
        {
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, t.Fbo);
            gl.Viewport(0, 0, t.W, t.H);
            gl.Uniform1i(_locPass, t.Pass);
            gl.DrawArrays(GlFunctions.TRIANGLE_STRIP, 0, 4);
        }
        gl.BindVertexArray(0);
        gl.PixelStorei(GlFunctions.PACK_ALIGNMENT, 4);
        gl.PixelStorei(GlFunctions.PACK_ROW_LENGTH, 0);
        gl.PixelStorei(GlFunctions.PACK_SKIP_ROWS, 0);
        gl.PixelStorei(GlFunctions.PACK_SKIP_PIXELS, 0);
        foreach (var t in _targets)
        {
            gl.BindFramebuffer(GlFunctions.READ_FRAMEBUFFER, t.Fbo);
            gl.ReadPixels(0, 0, t.W, t.H, GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, (void*)t.Offset);
        }
        gl.BindFramebuffer(GlFunctions.READ_FRAMEBUFFER, 0);
        gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
    }

    /// <summary>
    /// source を NV12 に変換して、Y の面 (R8、Width x Height) と U・V の面 (GR88、半分の大きさ) の FBO に直接描く
    /// (VAAPI のフレームを DMA-BUF として取り込んだもの)。
    /// </summary>
    public void ConvertTo(uint source, uint fboY, uint fboUV)
    {
        var gl = _gl;
        Begin(source);
        gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, fboY);
        gl.Viewport(0, 0, Width, Height);
        gl.Uniform1i(_locPass, 4);
        gl.DrawArrays(GlFunctions.TRIANGLE_STRIP, 0, 4);
        gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, fboUV);
        gl.Viewport(0, 0, Width / 2, Height / 2);
        gl.Uniform1i(_locPass, 5);
        gl.DrawArrays(GlFunctions.TRIANGLE_STRIP, 0, 4);
        gl.BindVertexArray(0);
        gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
    }

    private void Begin(uint source)
    {
        var gl = _gl;
        foreach (var cap in new[] { GlFunctions.BLEND, GlFunctions.SCISSOR_TEST, GlFunctions.DEPTH_TEST, GlFunctions.STENCIL_TEST, GlFunctions.CULL_FACE, GlFunctions.DITHER })
            gl.Disable(cap);
        gl.ColorMask(1, 1, 1, 1);
        gl.UseProgram(_program);
        var (ky, ku, kv, off) = Coefficients(Bt709);
        gl.Uniform3f(_locKy, ky.R, ky.G, ky.B);
        gl.Uniform3f(_locKu, ku.R, ku.G, ku.B);
        gl.Uniform3f(_locKv, kv.R, kv.G, kv.B);
        gl.Uniform3f(_locOff, off.Y, off.U, off.V);
        gl.Uniform2i(_locSize, Width, Height);
        gl.ActiveTexture(GlFunctions.TEXTURE0);
        gl.BindTexture(GlFunctions.TEXTURE_2D, source);
        gl.BindVertexArray(_vao);
    }

    /// <summary>読み戻したバッファ (先頭が data) の面。</summary>
    public YuvPlanes PlanesAt(IntPtr data)
    {
        IntPtr At(int i) => data + (nint)_targets[i].Offset;
        int Stride(int i) => _targets[i].W * 4;
        return Layout == YuvLayout.Nv12
            ? new YuvPlanes(Width, Height, YuvLayout.Nv12, 1, 1, false, At(0), Stride(0), At(1), Stride(1), IntPtr.Zero, 0)
            : new YuvPlanes(Width, Height, YuvLayout.Planar, 1, 1, false, At(0), Stride(0), At(1), Stride(1), At(2), Stride(2));
    }

    public void Dispose()
    {
        var gl = _gl;
        foreach (var t in _targets)
        {
            uint fbo = t.Fbo;
            if (fbo != 0) gl.DeleteFramebuffers(1, &fbo);
            gl.DeleteTexture(t.Texture);
        }
        uint vao = _vao;
        gl.DeleteVertexArrays(1, &vao);
        gl.DeleteProgram(_program);
        _gpu.Context.ResetContext();
    }
}
