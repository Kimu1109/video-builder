using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>
/// Windows: Direct3D 11 のフレームを GPU 上のまま描画 (ANGLE) と受け渡す仕組み (設計書 22.65)。CI では WARP で確かめる。
/// FFmpeg を使わずに、デコーダ・エンコーダの代わりのテクスチャを直接作って確かめる (CI の windows-gpu には FFmpeg がないため)。
/// </summary>
public unsafe class GpuD3D11FramesTests
{
    private const int W = 64, H = 36;

    /// <summary>NV12 の Y・U・V の模様 (Y は位置で、U・V は列で変わる)。</summary>
    private static (byte[] Y, byte[] UV) Pattern(int w, int h, int seed)
    {
        var y = new byte[w * h];
        var uv = new byte[w * (h / 2)];
        for (int r = 0; r < h; r++)
            for (int x = 0; x < w; x++) y[r * w + x] = (byte)(16 + (x * 3 + r * 5 + seed) % 220);
        for (int r = 0; r < h / 2; r++)
            for (int x = 0; x < w / 2; x++)
            {
                uv[r * w + x * 2] = (byte)(40 + (x * 7 + r + seed) % 180);
                uv[r * w + x * 2 + 1] = (byte)(200 - (x * 5 + r * 3 + seed) % 150);
            }
        return (y, uv);
    }

    /// <summary>GL のテクスチャ (w x h) を RGBA で読み戻す。</summary>
    private static byte[] ReadTexture(GlFunctions gl, uint texture, int w, int h)
    {
        var result = new byte[w * h * 4];
        uint fbo;
        gl.GenFramebuffers(1, &fbo);
        try
        {
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, fbo);
            gl.FramebufferTexture2D(GlFunctions.FRAMEBUFFER, GlFunctions.COLOR_ATTACHMENT0, GlFunctions.TEXTURE_2D, texture, 0);
            Assert.Equal(GlFunctions.FRAMEBUFFER_COMPLETE, gl.CheckFramebufferStatus(GlFunctions.FRAMEBUFFER));
            gl.PixelStorei(GlFunctions.PACK_ALIGNMENT, 1);
            gl.PixelStorei(GlFunctions.PACK_ROW_LENGTH, 0);
            fixed (byte* p = result) gl.ReadPixels(0, 0, w, h, GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, p);
        }
        finally
        {
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
            gl.DeleteFramebuffers(1, &fbo);
        }
        return result;
    }

    /// <summary>
    /// 読み取りにしか使えないテクスチャ (共有の NV12 の面など。描画先にはできない) を、シェーダで RGBA8 に写してから読み戻す。
    /// </summary>
    private static byte[] SampleTexture(GpuContext gpu, uint texture, int w, int h)
    {
        var gl = gpu.Gl!;
        const string vs = "void main() { vec2 p = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1)); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }";
        const string fs = "precision highp float; uniform sampler2D t; out vec4 color; void main() { color = texelFetch(t, ivec2(gl_FragCoord.xy), 0); }";
        uint program = gl.Program(gl.ShaderVersion + "\n" + vs, gl.ShaderVersion + "\n" + fs, out var error);
        Assert.True(program != 0, error);
        uint output = gl.GenTexture(), vao, fbo;
        gl.GenVertexArrays(1, &vao);
        gl.GenFramebuffers(1, &fbo);
        try
        {
            gl.BindTexture(GlFunctions.TEXTURE_2D, output);
            gl.TexImage2D(GlFunctions.TEXTURE_2D, 0, (int)GlFunctions.RGBA8, w, h, 0, GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, null);
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, fbo);
            gl.FramebufferTexture2D(GlFunctions.FRAMEBUFFER, GlFunctions.COLOR_ATTACHMENT0, GlFunctions.TEXTURE_2D, output, 0);
            Assert.Equal(GlFunctions.FRAMEBUFFER_COMPLETE, gl.CheckFramebufferStatus(GlFunctions.FRAMEBUFFER));
            gl.Viewport(0, 0, w, h);
            gl.Disable(GlFunctions.BLEND);
            gl.UseProgram(program);
            gl.Uniform1i(gl.Uniform(program, "t"), 0);
            gl.ActiveTexture(GlFunctions.TEXTURE0);
            gl.BindTexture(GlFunctions.TEXTURE_2D, texture);
            gl.BindVertexArray(vao);
            gl.DrawArrays(GlFunctions.TRIANGLE_STRIP, 0, 4);
            gl.BindVertexArray(0);
            gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
            return ReadTexture(gl, output, w, h);
        }
        finally
        {
            gl.DeleteFramebuffers(1, &fbo);
            gl.DeleteVertexArrays(1, &vao);
            gl.DeleteTexture(output);
            gl.DeleteProgram(program);
            gpu.Context.ResetContext();
        }
    }

    private static GpuContext? OpenGpu()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var gpu = GpuContext.TryCreate(out var error);
        Assert.True(gpu != null || !GpuSelectionTests.GpuRequired, error);
        return gpu;
    }

    [Fact]
    public void Gpu_d3d11_decoded_frames_are_imported_through_a_shared_texture()
    {
        using var gpu = OpenGpu();
        if (gpu == null || !OperatingSystem.IsWindows()) return;
        gpu.MakeCurrent();
        var gl = gpu.Gl!;
        using var import = D3D11FrameImport.Create(gpu, out var error);
        Assert.True(import != null, error);

        // デコーダの代わり: 別の Direct3D 11 のデバイス (同じ GPU) に、2 枚の配列の NV12 (高さは置き場のように 16 の倍数) を作る
        IntPtr adapter = gpu.Adapter != null ? DxgiAdapters.Open(gpu.Adapter.Luid) : IntPtr.Zero;
        IntPtr device;
        try { device = DxgiAdapters.CreateDevice(adapter, out error); }
        finally { DxgiAdapters.Release(adapter); }
        Assert.True(device != IntPtr.Zero, error);
        IntPtr context = D3D11.GetImmediateContext(device), source = IntPtr.Zero;
        try
        {
            const int padded = 48;
            var slices = new[] { Pattern(W, padded, 0), Pattern(W, padded, 11) };
            var data = slices.Select(s => s.Y.Concat(s.UV).ToArray()).ToArray();
            fixed (byte* p0 = data[0], p1 = data[1])
            {
                var initial = stackalloc D3D11.SubresourceData[2];
                initial[0] = new D3D11.SubresourceData { SysMem = p0, SysMemPitch = W };
                initial[1] = new D3D11.SubresourceData { SysMem = p1, SysMemPitch = W };
                var desc = new D3D11.Texture2DDesc
                {
                    Width = W, Height = padded, MipLevels = 1, ArraySize = 2, Format = D3D11.DXGI_FORMAT_NV12, SampleCount = 1,
                    Usage = D3D11.USAGE_DEFAULT, BindFlags = D3D11.BIND_SHADER_RESOURCE,
                };
                int hr = D3D11.CreateTexture2D(device, desc, initial, out source);
                Assert.True(hr >= 0, $"NV12 のテクスチャを作れません (0x{hr:X8})");
            }
            var access = new D3D11DeviceAccess(device, context, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            // 共有のテクスチャ (3 枚) を何周か使っても、鍵の受け渡しが止まらず、毎回そのフレームの中身になる
            for (int round = 0; round < 7; round++)
            {
                int slice = round % 2;
                var slot = import!.Import(access, source, slice, W, H, p010: false, out error);
                Assert.True(slot != null, error);
                try
                {
                    var (y, uv) = slices[slice];
                    var ry = SampleTexture(gpu, slot!.Textures[0], W, H);
                    for (int r = 0; r < H; r++)
                        for (int x = 0; x < W; x++)
                            Assert.True(ry[(r * W + x) * 4] == y[r * W + x], $"Y ({x}, {r}) 周 {round}: {ry[(r * W + x) * 4]} != {y[r * W + x]}");
                    var ruv = SampleTexture(gpu, slot.Textures[1], W / 2, H / 2);
                    for (int r = 0; r < H / 2; r++)
                        for (int x = 0; x < W / 2; x++)
                        {
                            Assert.Equal(uv[r * W + x * 2], ruv[(r * (W / 2) + x) * 4]);
                            Assert.Equal(uv[r * W + x * 2 + 1], ruv[(r * (W / 2) + x) * 4 + 1]);
                        }
                }
                finally { import.Done(slot!); }
            }
        }
        finally
        {
            gpu.MakeCurrent();
            import?.Dispose();
            D3D11.Release(source);
            D3D11.Release(context);
            DxgiAdapters.Release(device);
            gpu.Context.ResetContext();
        }
    }

    [Fact]
    public void Gpu_draws_nv12_directly_into_d3d11_encoder_frames()
    {
        using var gpu = OpenGpu();
        if (gpu == null || !OperatingSystem.IsWindows()) return;
        gpu.MakeCurrent();
        var gl = gpu.Gl!;
        var egl = D3D11Egl.Create(gpu, out var error);
        Assert.True(egl != null, error);
        using var yuv = GpuYuvExport.Create(gpu, W, H, YuvLayout.Nv12, bt709: false, out error);
        Assert.True(yuv != null, error);

        // 描く絵 (RGBA8 のテクスチャ)
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            rgba[i * 4] = (byte)(i * 7); rgba[i * 4 + 1] = (byte)(255 - i * 3); rgba[i * 4 + 2] = (byte)(i * 13 / 5); rgba[i * 4 + 3] = 255;
        }
        uint src = gl.GenTexture();
        gl.BindTexture(GlFunctions.TEXTURE_2D, src);
        gl.PixelStorei(GlFunctions.UNPACK_ALIGNMENT, 1);
        fixed (byte* p = rgba) gl.TexImage2D(GlFunctions.TEXTURE_2D, 0, (int)GlFunctions.RGBA8, W, H, 0, GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, p);

        // 期待値: これまでの経路 (RGBA8 に 4 バイトずつ詰めた NV12)
        yuv!.Convert(src);
        var planes = yuv.PlaneTextures;
        var packedY = ReadTexture(gl, planes[0].Texture, W / 4, H);
        var packedUV = ReadTexture(gl, planes[1].Texture, W / 4, H / 2);

        // エンコーダの置き場の代わり: 描画のデバイスに、描画先にできる NV12 の配列 (2 枚) を作り、2 枚目へ直接描く
        IntPtr frame = IntPtr.Zero, staging = IntPtr.Zero, context = D3D11.GetImmediateContext(gpu.D3DDevice);
        var images = new IntPtr[2];
        var textures = new uint[2];
        var fbos = new uint[2];
        try
        {
            var desc = new D3D11.Texture2DDesc
            {
                Width = W, Height = H, MipLevels = 1, ArraySize = 2, Format = D3D11.DXGI_FORMAT_NV12, SampleCount = 1,
                Usage = D3D11.USAGE_DEFAULT, BindFlags = D3D11.BIND_RENDER_TARGET | D3D11.BIND_SHADER_RESOURCE,
            };
            int hr = D3D11.CreateTexture2D(gpu.D3DDevice, desc, null, out frame);
            Assert.True(hr >= 0, $"描画先にできる NV12 のテクスチャを作れません (0x{hr:X8})");
            for (int i = 0; i < 2; i++)
            {
                images[i] = egl!.Import(frame, i, slice: 1);
                Assert.True(images[i] != IntPtr.Zero, $"面 {i} を EGL に取り込めません (0x{EglApi.GetError():X4})");
                textures[i] = gl.GenTexture();
                gl.BindTexture(GlFunctions.TEXTURE_2D, textures[i]);
                egl.BindToTexture(images[i]);
                uint fbo;
                gl.GenFramebuffers(1, &fbo);
                fbos[i] = fbo;
                gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, fbo);
                gl.FramebufferTexture2D(GlFunctions.FRAMEBUFFER, GlFunctions.COLOR_ATTACHMENT0, GlFunctions.TEXTURE_2D, textures[i], 0);
                Assert.Equal(GlFunctions.FRAMEBUFFER_COMPLETE, gl.CheckFramebufferStatus(GlFunctions.FRAMEBUFFER));
                gl.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
            }
            yuv.ConvertTo(src, fbos[0], fbos[1]);
            gl.Flush();

            // Direct3D 11 で読み戻す (同じデバイスの即時のコンテキストなので、ANGLE の描画のあとに写る)
            desc.ArraySize = 1; desc.Usage = D3D11.USAGE_STAGING; desc.BindFlags = 0; desc.CpuAccessFlags = D3D11.CPU_ACCESS_READ;
            hr = D3D11.CreateTexture2D(gpu.D3DDevice, desc, null, out staging);
            Assert.True(hr >= 0, $"読み戻し用の NV12 を作れません (0x{hr:X8})");
            D3D11.CopySubresourceRegion(context, staging, 0, frame, 1, null);
            hr = D3D11.Map(context, staging, 0, 1 /* D3D11_MAP_READ */, out var mapped);
            Assert.True(hr >= 0, $"Map に失敗 (0x{hr:X8})");
            try
            {
                var bytes = (byte*)mapped.Data;
                int pitch = (int)mapped.RowPitch;
                for (int r = 0; r < H; r++)
                    for (int x = 0; x < W; x++)
                        Assert.True(Math.Abs(bytes[r * pitch + x] - packedY[r * W + x]) <= 1, $"Y ({x}, {r}): {bytes[r * pitch + x]} != {packedY[r * W + x]}");
                var uvRows = bytes + pitch * H;
                for (int r = 0; r < H / 2; r++)
                    for (int x = 0; x < W; x++)
                        Assert.True(Math.Abs(uvRows[r * pitch + x] - packedUV[r * W + x]) <= 1, $"UV ({x}, {r}): {uvRows[r * pitch + x]} != {packedUV[r * W + x]}");
            }
            finally { D3D11.Unmap(context, staging, 0); }
        }
        finally
        {
            gpu.MakeCurrent();
            for (int i = 0; i < 2; i++)
            {
                uint fbo = fbos[i];
                if (fbo != 0) gl.DeleteFramebuffers(1, &fbo);
                gl.DeleteTexture(textures[i]);
                egl?.Destroy(images[i]);
            }
            gl.DeleteTexture(src);
            D3D11.Release(staging);
            D3D11.Release(frame);
            D3D11.Release(context);
            gpu.Context.ResetContext();
        }
    }

    [Fact]
    public void Windows_scheduling_switches_on_for_the_first_user_and_back_after_the_last()
    {
        var calls = new List<string>();
        var counter = new WindowsScheduling.Counter(() => calls.Add("on"), () => calls.Add("off"));
        var a = counter.Use();
        var b = counter.Use();
        Assert.True(counter.Active);
        a.Dispose();
        a.Dispose();   // 2 回呼んでも 1 回分
        Assert.True(counter.Active);
        b.Dispose();
        Assert.False(counter.Active);
        Assert.Equal(new[] { "on", "off" }, calls);
        // 実際の切り替え (Windows 以外では何もしない) も、入れて戻せる
        using (WindowsScheduling.NoPowerThrottling()) using (WindowsScheduling.FineTimer()) { }
    }
}
