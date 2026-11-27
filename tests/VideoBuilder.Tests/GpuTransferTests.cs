using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>
/// GPU と CPU の間の受け渡しを減らす高速化 (設計書 22.52) が、絵を変えないことを確かめる。
/// GPU コンテキストを作れない環境では何もしない (VB_REQUIRE_GPU=1 なら失敗にする)。
/// </summary>
public class GpuTransferTests
{
    private static GpuContext? Gpu()
    {
        var gpu = GpuContext.TryCreate(out var error);
        if (gpu == null) Assert.False(GpuSelectionTests.GpuRequired, "GPU を作れません: " + error);
        return gpu;
    }

    private static TimelineObject Shape(Project p, int layer, string color, int type, int size, double x = 0, double y = 0)
    {
        var o = ObjectFactory.Create(EffectRegistry.Shape, layer, 0, 60);
        o.Main.Values["color"] = color; o.Main.Values["type"] = type.ToString();
        o.Main.Tracks["size"] = new AnimatedValue(size);
        o.Draw!.Tracks["x"] = new AnimatedValue(x); o.Draw!.Tracks["y"] = new AnimatedValue(y);
        p.Root.Objects.Add(o);
        return o;
    }

    /// <summary>色の違う図形を並べたプロジェクト (半透明のものも入れる)。</summary>
    private static Project Colorful()
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 180;
        Shape(p, 0, "#1B2A41", 0, 0);
        Shape(p, 1, "#F2A900", 1, 90, -90, -20);
        Shape(p, 2, "#3A6EA5", 2, 80, 0, 20);
        Shape(p, 3, "#C0392B", 3, 70, 90, -10);
        var faded = Shape(p, 4, "#27AE60", 1, 60, 40, -50);
        faded.Draw!.Tracks["alpha"] = new AnimatedValue(55);
        return p;
    }

    private static (double Mean, int Max) Compare(SKBitmap a, SKBitmap b)
    {
        long total = 0; int max = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                SKColor c = a.GetPixel(x, y), g = b.GetPixel(x, y);
                foreach (int d in new[] { c.Red - g.Red, c.Green - g.Green, c.Blue - g.Blue, c.Alpha - g.Alpha })
                {
                    total += Math.Abs(d); max = Math.Max(max, Math.Abs(d));
                }
            }
        return ((double)total / (a.Width * a.Height * 4), max);
    }

    private static SKBitmap Render(RenderEngine engine, Project p, int frame, SKColorType type = SKColorType.Bgra8888)
    {
        var bmp = new SKBitmap(new SKImageInfo(p.Settings.Width, p.Settings.Height, type, SKAlphaType.Premul));
        engine.RenderToBitmap(p, p.Root, frame, bmp);
        return bmp;
    }

    // ---- 3: 書き出しで RGBA のまま読み戻す ----

    [Fact]
    public void Readback_color_type_is_rgba_on_gpu_and_bgra_on_cpu()
    {
        using (var cpu = new RenderEngine()) Assert.Equal(SKColorType.Bgra8888, cpu.ReadbackColorType);
        using var gpu = Gpu();
        if (gpu == null) return;
        using var engine = new RenderEngine(gpu);
        Assert.Equal(SKColorType.Rgba8888, engine.ReadbackColorType);
    }

    [Fact]
    public void Rgba_destination_gets_the_same_picture_on_cpu_and_gpu()
    {
        var p = Colorful();
        using var cpu = new RenderEngine();
        using var bgra = Render(cpu, p, 0);
        // CPU 描画に RGBA を渡しても (GPU が途中で使えなくなったとき)、同じ絵になる
        using var cpuRgba = Render(cpu, p, 0, SKColorType.Rgba8888);
        Assert.Equal((0.0, 0), Compare(bgra, cpuRgba));

        using var gpu = Gpu();
        if (gpu == null) return;
        using var engine = new RenderEngine(gpu);
        using var gpuBgra = Render(engine, p, 0);
        using var gpuRgba = Render(engine, p, 0, SKColorType.Rgba8888);
        Assert.Equal((0.0, 0), Compare(gpuBgra, gpuRgba));
        var (mean, _) = Compare(bgra, gpuRgba);
        Assert.True(mean < 0.6, $"CPU と GPU の差が大きい: {mean}");
    }

    [Fact]
    public void Gpu_export_reads_back_rgba_and_keeps_colors()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        using (var probe = Gpu()) if (probe == null) return;
        var p = Colorful();
        var dir = Path.Combine(Path.GetTempPath(), $"vb-rgba-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string Export(bool useGpu)
            {
                var path = Path.Combine(dir, useGpu ? "gpu.mp4" : "cpu.mp4");
                // 出力は別スレッドで自分用の GPU コンテキストを作る
                Task.Run(() => Exporter.Run(new ExportRequest
                {
                    Project = p, SceneId = "root", StartFrame = 0, EndFrame = 4, UseGpu = useGpu,
                    Settings = new ExportSettings { Path = path, AudioCodec = "", Crf = 10 },
                }, null, CancellationToken.None)).Wait();
                return path;
            }
            using var cpuReader = new VideoReader(Export(false));
            using var gpuReader = new VideoReader(Export(true));
            var a = (byte[])cpuReader.GetFrame(0.05)!.Clone();
            var b = gpuReader.GetFrame(0.05)!;
            long total = 0;
            for (int i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
            double mean = (double)total / a.Length;
            Assert.True(mean < 1.5, $"GPU で書き出した動画の色が違う: {mean}");
            // 赤い図形 (右) は赤のまま (R と B が入れ替わっていない)
            int x = 160 + 90, y = 90 - 10, i0 = (y * 320 + x) * 4;
            Assert.True(b[i0 + 2] > 150 && b[i0] < 100, $"赤が赤でない: B={b[i0]} R={b[i0 + 2]}");

            // 連番 PNG も RGBA のビットマップから書ける
            var seq = Path.Combine(dir, "seq.png");
            Task.Run(() => Exporter.Run(new ExportRequest
            {
                Project = p, SceneId = "root", StartFrame = 0, EndFrame = 0, UseGpu = true, ImageSequence = true,
                Settings = new ExportSettings { Path = seq },
            }, null, CancellationToken.None)).Wait();
            using var png = SKBitmap.Decode(Path.Combine(dir, "seq_00000.png"));
            var red = png.GetPixel(x, y);
            Assert.True(red.Red > 150 && red.Blue < 100, "PNG の赤が赤でない: " + red);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- 6: レベル補正・ポスタリゼーションを GPU のまま処理する ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lut_effects_on_gpu_match_cpu(bool asFilterObject)
    {
        using var gpu = Gpu();
        if (gpu == null) return;
        var p = Colorful();
        if (asFilterObject)
        {
            var levels = ObjectFactory.Create("filter.levels", 8, 0, 60);
            levels.Main.Tracks["inBlack"] = new AnimatedValue(20);
            levels.Main.Tracks["inWhite"] = new AnimatedValue(200);
            levels.Main.Tracks["gamma"] = new AnimatedValue(1.4);
            p.Root.Objects.Add(levels);
            var poster = ObjectFactory.Create("filter.posterize", 9, 0, 60);
            poster.Main.Tracks["levels"] = new AnimatedValue(5);
            p.Root.Objects.Add(poster);
        }
        else
        {
            foreach (var o in p.Root.Objects.Skip(1))
            {
                var l = ObjectFactory.AddFilter(o, "filter.levels");
                l.Tracks["inWhite"] = new AnimatedValue(160);
                l.Tracks["gamma"] = new AnimatedValue(0.7);
                ObjectFactory.AddFilter(o, "filter.posterize").Tracks["levels"] = new AnimatedValue(4);
            }
        }
        using var cpu = new RenderEngine();
        using var engine = new RenderEngine(gpu);
        using var a = Render(cpu, p, 0);
        using var b = Render(engine, p, 0);
        var (mean, max) = Compare(a, b);
        Console.WriteLine($"BENCH lut gpu-vs-cpu filterObject={asFilterObject} mean={mean:0.000} max={max}");
        Assert.True(mean < 0.6, $"平均の差が大きい: {mean}");
    }

    // ---- 4a: 動画のフレームを YUV のまま GPU へ送り、GPU で色を変換する ----

    /// <summary>色の変化 (横・縦のグラデーションと色の帯) のある動画を書き出す。</summary>
    private static string WriteVideo(string dir, string name, string codec, int w, int h)
    {
        var path = Path.Combine(dir, name);
        var s = new ExportSettings { Path = path, Width = w, Height = h, Fps = new Rational(30, 1), VideoCodec = codec, AudioCodec = "", Crf = 8 };
        using var writer = new MediaWriter(s);
        var frame = new byte[w * h * 4];
        for (int f = 0; f < 6; f++)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    bool band = (x / 24 + y / 24 + f) % 5 == 0;
                    frame[i + 0] = (byte)(band ? 40 : 255 * y / h);            // B
                    frame[i + 1] = (byte)(band ? 200 : 128 + f * 10);          // G
                    frame[i + 2] = (byte)(band ? 230 : 255 * x / w);           // R
                    frame[i + 3] = 255;
                }
            writer.WriteVideoFrame(frame, w * 4);
        }
        writer.Finish();
        return path;
    }

    private static Project VideoProject(string video, int w, int h, int length = 6)
    {
        var p = new Project();
        p.Settings.Width = w; p.Settings.Height = h;
        var o = ObjectFactory.Create(EffectRegistry.Video, 0, 0, length);
        o.Main.Values["file"] = video;
        p.Root.Objects.Add(o);
        return p;
    }

    [Theory]
    [InlineData("yuv420.mp4", "libx264", 320, 180)]
    [InlineData("vp9.webm", "libvpx-vp9", 256, 144)]
    public void Video_frames_converted_on_gpu_match_cpu(string name, string codec, int w, int h)
    {
        if (!FFmpegLoader.EnsureLoaded() || !MediaWriter.IsEncoderAvailable(codec)) return;
        using var gpu = Gpu();
        if (gpu == null) return;
        CheckVideoFrames(gpu, name, codec, w, h);
    }

    /// <summary>画面と共有するデスクトップの OpenGL でも、動画のフレームを YUV のまま送って、CPU 描画と同じ色になる。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Video_frames_converted_on_desktop_opengl_match_cpu(bool core)
    {
        if (!FFmpegLoader.EnsureLoaded() || !MediaWriter.IsEncoderAvailable("libx264")) return;
        using var gpu = DesktopGl(core);
        if (gpu == null) return;
        CheckVideoFrames(gpu, "yuv420.mp4", "libx264", 320, 180);
    }

    private static void CheckVideoFrames(GpuContext gpu, string name, string codec, int w, int h)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vb-yuv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var video = WriteVideo(dir, name, codec, w, h);
            var p = VideoProject(video, w, h);
            using var cpu = new RenderEngine();
            using var engine = new RenderEngine(gpu);
            for (int f = 0; f < 4; f++)
            {
                using var a = Render(cpu, p, f);
                using var b = Render(engine, p, f);
                var (mean, max) = Compare(a, b);
                Console.WriteLine($"BENCH yuv {name} frame={f} mean={mean:0.000} max={max}");
                Assert.True(mean < 1.0, $"GPU で変換した色が違う (frame {f}): mean={mean} max={max}");
                Assert.True(max <= 24, $"大きく違う画素がある (frame {f}): max={max}");
            }
            Assert.True(engine.Media.GpuYuvFrames >= 4, $"YUV のまま送っていない: {engine.Media.GpuYuvFrames} {engine.Media.GpuYuvError}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// ハードウェアデコードしたフレームを、メモリへ写さずに GPU 上のまま変換しても (VAAPI は DMA-BUF、NVDEC は CUDA と GL の相互運用)、
    /// CPU 描画 (メモリへ写して sws で変換) と同じ色になる (設計書 22.58)。
    /// ハードウェアデコードが使えない環境では何もしない (VB_REQUIRE_GPU_FRAMES=1 なら、GPU 上のまま変換できなければ失敗にする)。
    /// VAAPI は VIDEO_BUILDER_GPU=Intel VIDEO_BUILDER_HWDEC=vaapi のように、描画とデコードを同じ GPU にして確かめる。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Hardware_decoded_frames_are_converted_without_copying_to_memory(int divisor)
    {
        if (!FFmpegLoader.EnsureLoaded() || !MediaWriter.IsEncoderAvailable("libx264")) return;
        using var gpu = Gpu();
        if (gpu == null) return;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-hwframe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            const int w = 640, h = 360;
            var video = WriteVideo(dir, "src.mp4", "libx264", w, h);
            using (var probe = new VideoReader(video))
                if (!probe.IsHardware)
                {
                    Console.WriteLine($"BENCH hwframe skipped: {probe.HardwareError}");
                    Assert.False(Environment.GetEnvironmentVariable("VB_REQUIRE_GPU_FRAMES") == "1", "ハードウェアデコードできません: " + probe.HardwareError);
                    return;
                }
            // 縮めてデコードするとき (プレビュー) は、出力の大きさ自体を小さくする
            var p = VideoProject(video, w / divisor, h / divisor);
            p.Root.Objects[0].Draw!.Tracks["scale"] = new AnimatedValue(100.0 / divisor);
            using var cpu = new RenderEngine();
            using var engine = new RenderEngine(gpu);
            engine.Media.UseProxies = false;
            for (int f = 0; f < 6; f++)
            {
                using var a = Render(cpu, p, f);
                using var b = Render(engine, p, f);
                var (mean, max) = Compare(a, b);
                Console.WriteLine($"BENCH hwframe divisor={divisor} frame={f} mean={mean:0.000} max={max} gpuFrames={engine.Media.GpuHardwareFrames} error={engine.Media.GpuHardwareError}");
                Assert.True(mean < (divisor == 1 ? 1.0 : 3.0), $"GPU 上のまま変換した色が違う (frame {f}): mean={mean} max={max}");
            }
            if (Environment.GetEnvironmentVariable("VB_REQUIRE_GPU_FRAMES") == "1")
                Assert.True(engine.Media.GpuHardwareFrames >= 4, $"GPU 上のまま変換していない: {engine.Media.GpuHardwareFrames} {engine.Media.GpuHardwareError}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Yuv_frames_can_still_be_read_as_bgra()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-yuv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var video = WriteVideo(dir, "v.mp4", "libx264", 320, 180);
            using var plain = new VideoReader(video);
            using var yuv = new VideoReader(video) { PreferYuv = true };
            foreach (int divisor in new[] { 1, 2 })
            {
                plain.OutputDivisor = yuv.OutputDivisor = divisor;
                var expected = (byte[])plain.GetFrame(0.1)!.Clone();
                var token = yuv.GetFramePointer(0.1);
                Assert.True(yuv.TryGetYuv(token, out var planes));
                Assert.Equal((320 / divisor, 180 / divisor), (planes.Width, planes.Height));
                Assert.Equal((yuv.FrameWidth, yuv.FrameHeight), (planes.Width, planes.Height));
                // BGRA が要るとき (GPU へ送れなかったとき) は、あとから変換できる
                var bgra = yuv.GetFrame(0.1)!;
                long total = 0;
                for (int i = 0; i < expected.Length; i++) total += Math.Abs(expected[i] - bgra[i]);
                double mean = (double)total / expected.Length;
                // 元の大きさなら同じ変換なので一致する。縮めるときは YUV のまま縮めるので、わずかに違う
                if (divisor == 1) Assert.Equal(0, total);
                else Assert.True(mean < 2.0, $"縮めた絵が違う: {mean}");
            }
            // U・V を間引いた形式で奇数の大きさに縮めるときは、YUV で持たない (BGRA にする)
            yuv.OutputDivisor = 3;   // 107x60
            Assert.False(yuv.TryGetYuv(yuv.GetFramePointer(0.1), out _));
            // PreferYuv でなければ、これまでどおり BGRA だけ
            Assert.False(plain.TryGetYuv(plain.GetFramePointer(0.1), out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV420P, 64, 36)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUVJ420P, 64, 36)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_NV12, 64, 36)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV422P, 64, 36)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUVJ444P, 64, 36)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV444P, 37, 23)]
    public void Gpu_yuv_conversion_matches_ffmpeg(FFmpeg.AutoGen.AVPixelFormat format, int w, int h)
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        using var gpu = Gpu();
        if (gpu == null) return;
        CheckYuvConversion(gpu, format, w, h);
    }

    /// <summary>
    /// 画面と共有するデスクトップの OpenGL (Linux の GLX) でも、YUV のまま送って GPU で変換できる (以前は OpenGL ES 3 だけ)。
    /// GLX の代わりに、EGL でデスクトップの OpenGL のコンテキストを作って確かめる (コア プロファイル 3.3 と互換プロファイル)。
    /// </summary>
    [Theory]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV420P, 64, 36, true)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_NV12, 64, 36, true)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV444P, 37, 23, true)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUVJ420P, 64, 36, false)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_NV12, 64, 36, false)]
    [InlineData(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV422P, 64, 36, false)]
    public void Gpu_yuv_conversion_on_desktop_opengl_matches_ffmpeg(FFmpeg.AutoGen.AVPixelFormat format, int w, int h, bool core)
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        using var gpu = DesktopGl(core);
        if (gpu == null) return;
        var gl = gpu.Gl;
        Assert.True(gl != null, gpu.GlError);
        Assert.False(gl!.IsGles);
        Assert.StartsWith("#version 330", gl.ShaderVersion);   // Mesa はどちらのプロファイルでも 3.3 以上
        CheckYuvConversion(gpu, format, w, h);
    }

    /// <summary>
    /// デスクトップの OpenGL のコンテキスト (画面と共有するコンテキストの代わり)。Linux で作れなければ null
    /// (Linux で VB_REQUIRE_GPU=1 なら失敗にする)。
    /// </summary>
    private static GpuContext? DesktopGl(bool core)
    {
        var gpu = DesktopGlContext.Create(core, out var error);
        if (gpu == null) Assert.False(GpuSelectionTests.GpuRequired && OperatingSystem.IsLinux(), "デスクトップの OpenGL を作れません: " + error);
        else Console.WriteLine($"BENCH desktop-gl core={core} renderer={gpu.Renderer}");
        return gpu;
    }

    [Theory]
    [InlineData("OpenGL ES 3.2 Mesa 24.0.5", true, 3, 2, "#version 300 es")]
    [InlineData("OpenGL ES 3.0 (ANGLE 2.1.27548 git hash: 1a2b3c)", true, 3, 0, "#version 300 es")]
    [InlineData("OpenGL ES 2.0 Mesa 24.0.5", true, 2, 0, null)]
    [InlineData("4.6.0 NVIDIA 550.54.14", false, 4, 6, "#version 330 core")]
    [InlineData("4.5 (Compatibility Profile) Mesa 24.0.5", false, 4, 5, "#version 330 core")]
    [InlineData("3.3 (Core Profile) Mesa 24.0.5", false, 3, 3, "#version 330 core")]
    [InlineData("3.1 Mesa 24.0.5", false, 3, 1, "#version 130")]
    [InlineData("2.1 Mesa 24.0.5", false, 2, 1, null)]
    public void Gl_version_decides_the_shader_language(string version, bool gles, int major, int minor, string? shader)
    {
        Assert.Equal((gles, major, minor), GlFunctions.ParseVersion(version));
        Assert.Equal(shader, GlFunctions.ShaderVersionFor(gles, major, minor));
    }

    [Fact]
    public void Gl_version_that_cannot_be_read_is_not_used()
    {
        Assert.Null(GlFunctions.ParseVersion(""));
        Assert.Null(GlFunctions.ParseVersion("OpenGL ES"));
        Assert.Null(GlFunctions.ParseVersion("unknown"));
    }

    private static unsafe void CheckYuvConversion(GpuContext gpu, FFmpeg.AutoGen.AVPixelFormat format, int w, int h)
    {
        var layout = VideoReader.YuvFormatOf(format)!.Value;
        int cw = (w + (1 << layout.ShiftX) - 1) >> layout.ShiftX, ch = (h + (1 << layout.ShiftY) - 1) >> layout.ShiftY;
        bool nv12 = layout.Layout == YuvLayout.Nv12;
        // 行の終わりに余白のある面 (デコーダの置き場と同じ)
        int ys = w + 13, us = (nv12 ? cw * 2 + 8 : cw + 7), vs = cw + 5;
        var y = new byte[ys * h]; var u = new byte[us * ch]; var v = new byte[vs * ch];
        var rnd = new Random(7);
        for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) y[r * ys + c] = (byte)(16 + (c * 7 + r * 3) % 220);
        for (int r = 0; r < ch; r++)
            for (int c = 0; c < cw; c++)
            {
                byte cu = (byte)rnd.Next(16, 241), cv = (byte)rnd.Next(16, 241);
                if (nv12) { u[r * us + c * 2] = cu; u[r * us + c * 2 + 1] = cv; }
                else { u[r * us + c] = cu; v[r * vs + c] = cv; }
            }
        var expected = new byte[w * h * 4];
        fixed (byte* py = y, pu = u, pv = v, pe = expected)
        {
            // CPU の変換 (VideoReader と同じ sws の呼び方)
            var sws = FFmpeg.AutoGen.ffmpeg.sws_getContext(w, h, format, w, h, FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)FFmpeg.AutoGen.SwsFlags.SWS_BILINEAR, null, null, null);
            var src = new byte*[] { py, pu, nv12 ? null : pv, null };
            var srcStride = new[] { ys, us, nv12 ? 0 : vs, 0 };
            var dst = new byte*[] { pe, null, null, null };
            var dstStride = new[] { w * 4, 0, 0, 0 };
            FFmpeg.AutoGen.ffmpeg.sws_scale(sws, src, srcStride, 0, h, dst, dstStride);
            FFmpeg.AutoGen.ffmpeg.sws_freeContext(sws);

            var planes = new YuvPlanes(w, h, layout.Layout, layout.ShiftX, layout.ShiftY, layout.Full,
                (IntPtr)py, ys, (IntPtr)pu, us, nv12 ? IntPtr.Zero : (IntPtr)pv, nv12 ? 0 : vs);
            gpu.MakeCurrent();
            using var converter = YuvTextures.Create(gpu, out var error);
            Assert.True(converter != null, error);
            using var image = converter!.Convert(planes);
            Assert.NotNull(image);
            using var actual = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            Assert.True(image!.ReadPixels(actual.Info, actual.GetPixels(), actual.RowBytes, 0, 0));
            var got = actual.Bytes;
            // sws は NV12 の下端 2 行だけ、U・V の重みの付け方が違う (端の扱い)。そこは比べない
            int rows = nv12 ? h - 2 : h;
            long total = 0; int max = 0;
            for (int i = 0; i < rows * w * 4; i++) { int d = Math.Abs(expected[i] - got[i]); total += d; max = Math.Max(max, d); }
            double mean = (double)total / (rows * w * 4);
            Console.WriteLine($"BENCH yuv-planes {format} {w}x{h} mean={mean:0.000} max={max}");
            Assert.True(mean < 1.0 && max <= 4, $"sws と違う: mean={mean} max={max}");
        }
    }

    // ---- 1: 書き出しで、GPU の読み戻しを待たずに次のフレームを描く ----

    /// <summary>フレームごとに動く図形 (フレームの順番がずれれば絵が変わる)。</summary>
    private static Project Moving()
    {
        var p = Colorful();
        var mover = Shape(p, 6, "#FFFFFF", 2, 40);
        mover.Draw!.Tracks["x"] = new AnimatedValue { Values = new() { -140, 140 }, Mode = MovementMode.Linear };
        return p;
    }

    /// <summary>動画の全フレームを BGRA で読む。</summary>
    private static List<byte[]> AllFrames(string path, int count, double fps)
    {
        using var r = new VideoReader(path);
        var list = new List<byte[]>();
        for (int i = 0; i < count; i++) list.Add((byte[])r.GetFrame((i + 0.5) / fps)!.Clone());
        return list;
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Gpu_export_with_async_readback_matches_cpu_frame_by_frame(int failAfter)
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        using (var probe = Gpu()) if (probe == null) return;
        var p = Moving();
        const int Frames = 12;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-async-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var old = AsyncReadback.TestFailAfter;
        try
        {
            ExportStats Export(string name, bool useGpu)
            {
                var stats = new ExportStats();
                Task.Run(() => Exporter.Run(new ExportRequest
                {
                    Project = p, SceneId = "root", StartFrame = 0, EndFrame = Frames - 1, UseGpu = useGpu, Stats = stats,
                    Settings = new ExportSettings { Path = Path.Combine(dir, name), Crf = 10 },
                }, null, CancellationToken.None)).Wait();
                return stats;
            }
            Export("cpu.mp4", false);
            AsyncReadback.TestFailAfter = failAfter;
            var stats = Export("gpu.mp4", true);
            AsyncReadback.TestFailAfter = old;
            Console.WriteLine($"BENCH async-readback failAfter={failAfter} frames={stats.AsyncReadbackFrames} error={stats.AsyncReadbackError} gpuYuv={stats.GpuYuvFrames} {stats.GpuYuvError}");
            // x264 (YUV420P) なら、GPU で YUV にしてから読み戻す (設計書 22.58)
            if (Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU_YUV_EXPORT") != "0") Assert.Equal(stats.AsyncReadbackFrames, stats.GpuYuvFrames);
            if (failAfter < 0)
            {
                Assert.Equal(Frames, stats.AsyncReadbackFrames);
                Assert.Null(stats.AsyncReadbackError);
            }
            else
            {
                // 途中で読み戻せなくなったら、残りはこれまでどおり描いて読み戻す
                Assert.Equal(failAfter, stats.AsyncReadbackFrames);
                Assert.NotNull(stats.AsyncReadbackError);
            }
            Assert.Equal(Frames, stats.Frames);
            var fps = p.Settings.Fps.Value;
            var a = AllFrames(Path.Combine(dir, "cpu.mp4"), Frames, fps);
            var b = AllFrames(Path.Combine(dir, "gpu.mp4"), Frames, fps);
            for (int f = 0; f < Frames; f++)
            {
                long total = 0;
                for (int i = 0; i < a[f].Length; i++) total += Math.Abs(a[f][i] - b[f][i]);
                double mean = (double)total / a[f].Length;
                Assert.True(mean < 1.5, $"frame {f} が違う (順番のずれ?): {mean}");
            }
            // 音声も最後まで入っている
            var info = MediaInfo.Probe(Path.Combine(dir, "gpu.mp4"))!;
            Assert.True(info.HasAudio);
        }
        finally
        {
            AsyncReadback.TestFailAfter = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// VAAPI でエンコードするとき、GPU で NV12 に変換し、エンコーダのフレームへ DMA-BUF で直接描いても (読み戻さない)、
    /// CPU で描いて sws で変換したものと同じ絵になる (設計書 22.58)。direct が false なら、NV12 で読み戻す経路を確かめる。
    /// VAAPI のエンコーダがない環境では何もしない (VB_REQUIRE_VAAPI_ENCODE=1 なら失敗にする)。
    /// 直接描くのは、描画の GPU とエンコードの GPU が同じとき (VIDEO_BUILDER_GPU=Intel など)。違えば読み戻しに戻ることも確かめる。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gpu_export_to_vaapi_matches_cpu(bool direct)
    {
        if (!FFmpegLoader.EnsureLoaded() || !MediaWriter.IsEncoderAvailable("h264_vaapi")) return;
        using (var probe = Gpu()) if (probe == null) return;
        bool required = Environment.GetEnvironmentVariable("VB_REQUIRE_VAAPI_ENCODE") == "1";
        var p = Moving();
        p.Settings.Width = 640; p.Settings.Height = 360;
        const int Frames = 12;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-vaapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var old = Environment.GetEnvironmentVariable("VIDEO_BUILDER_VAAPI_DIRECT");
        try
        {
            ExportStats Export(string name, bool useGpu)
            {
                var stats = new ExportStats();
                Task.Run(() => Exporter.Run(new ExportRequest
                {
                    Project = p, SceneId = "root", StartFrame = 0, EndFrame = Frames - 1, UseGpu = useGpu, Stats = stats,
                    Settings = new ExportSettings { Path = Path.Combine(dir, name), VideoCodec = "h264_vaapi", Crf = 10, AudioCodec = "" },
                }, null, CancellationToken.None)).Wait();
                return stats;
            }
            try { Export("cpu.mp4", false); }
            catch (AggregateException ex) when (!required)
            {
                Console.WriteLine($"BENCH vaapi-export skipped: {ex.InnerException?.Message}");
                return;
            }
            Environment.SetEnvironmentVariable("VIDEO_BUILDER_VAAPI_DIRECT", direct ? null : "0");
            var stats = Export("gpu.mp4", true);
            Console.WriteLine($"BENCH vaapi-export direct={direct} async={stats.AsyncReadbackFrames} gpuYuv={stats.GpuYuvFrames} vaapiDirect={stats.VaapiDirectFrames} " +
                $"directError={stats.VaapiDirectError} yuvError={stats.GpuYuvError}");
            Assert.Equal(Frames, stats.Frames);
            Assert.Equal(stats.AsyncReadbackFrames, stats.GpuYuvFrames);
            if (!direct) Assert.Equal(0, stats.VaapiDirectFrames);
            else if (Environment.GetEnvironmentVariable("VB_REQUIRE_VAAPI_DIRECT") == "1")
                Assert.True(stats.VaapiDirectFrames == Frames, $"直接描いていない: {stats.VaapiDirectFrames} {stats.VaapiDirectError}");
            var fps = p.Settings.Fps.Value;
            var a = AllFrames(Path.Combine(dir, "cpu.mp4"), Frames, fps);
            var b = AllFrames(Path.Combine(dir, "gpu.mp4"), Frames, fps);
            for (int f = 0; f < Frames; f++)
            {
                long total = 0; int max = 0;
                for (int i = 0; i < a[f].Length; i++) { int d = Math.Abs(a[f][i] - b[f][i]); total += d; max = Math.Max(max, d); }
                double mean = (double)total / a[f].Length;
                Console.WriteLine($"BENCH vaapi-export direct={direct} frame={f} mean={mean:0.000} max={max}");
                Assert.True(mean < 2.0, $"frame {f} が違う: mean={mean} max={max}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIDEO_BUILDER_VAAPI_DIRECT", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 動画 (環境変数 VB_BENCH_VIDEO) を GPU で 1 フレームずつ描く速さを表示する (設定しなければ何もしない)。
    /// VIDEO_BUILDER_GPU_FRAMES=0 で、ハードウェアデコードのフレームをメモリへ写す以前の方法と比べられる (設計書 22.58)。
    /// </summary>
    [Fact]
    public void Report_hardware_decode_speed()
    {
        var video = Environment.GetEnvironmentVariable("VB_BENCH_VIDEO");
        if (string.IsNullOrEmpty(video) || !File.Exists(video) || !FFmpegLoader.EnsureLoaded()) return;
        using var gpu = Gpu();
        if (gpu == null) return;
        var info = MediaInfo.Probe(video)!;
        int frames = Math.Min(240, (int)(info.Duration * 30) - 1);
        var p = VideoProject(video, 1920, 1080, frames + 1);
        p.Root.Objects[0].Draw!.Tracks["scale"] = new AnimatedValue(100.0 * 1920 / info.Width);
        using var engine = new RenderEngine(gpu) { Lookahead = true };
        using var bmp = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int f = 0; f < 10; f++) engine.RenderToBitmap(p, p.Root, f, bmp);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        for (int f = 10; f < frames; f++) engine.RenderToBitmap(p, p.Root, f, bmp);
        double ms = watch.Elapsed.TotalMilliseconds / (frames - 10);
        double cpuMs = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / (frames - 10);
        Console.WriteLine($"BENCH hwdecode {info.Width}x{info.Height} {gpu.Renderer} ms/frame={ms:0.00} cpu-ms/frame={cpuMs:0.00} " +
            $"gpuFrames={engine.Media.GpuHardwareFrames} yuvFrames={engine.Media.GpuYuvFrames} error={engine.Media.GpuHardwareError} " +
            $"env={Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU_FRAMES") ?? "on"}");
    }

    /// <summary>
    /// 1080p の動画 + 図形を GPU で書き出す速さ (内訳つき) を表示する。
    /// VIDEO_BUILDER_GPU_YUV=0・VIDEO_BUILDER_ASYNC_READBACK=0 で、それぞれ以前の方法と比べられる。
    /// </summary>
    [Fact]
    public void Report_gpu_export_speed()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        using (var probe = Gpu()) if (probe == null) return;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-speed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var video = WriteVideo(dir, "src.mp4", "libx264", 1920, 1080);
            var p = VideoProject(video, 1920, 1080, 30);
            Shape(p, 1, "#F2A900", 1, 300, -400, -100);
            var stats = new ExportStats();
            Task.Run(() => Exporter.Run(new ExportRequest
            {
                Project = p, SceneId = "root", StartFrame = 0, EndFrame = 29, UseGpu = true, Stats = stats,
                // VB_BENCH_CODEC でエンコーダを変えられる (h264_vaapi など)
                Settings = new ExportSettings { Path = Path.Combine(dir, "out.mp4"), AudioCodec = "", Preset = "ultrafast",
                    VideoCodec = Environment.GetEnvironmentVariable("VB_BENCH_CODEC") is { Length: > 0 } c ? c : "libx264" },
            }, null, CancellationToken.None)).Wait();
            double Ms(TimeSpan t) => t.TotalMilliseconds / stats.Frames;
            Console.WriteLine($"BENCH gpu-export 1080p fps={stats.FramesPerSecond:0.0} render={Ms(stats.Render):0.0}ms convert={Ms(stats.Convert):0.0}ms " +
                $"encode={Ms(stats.Encode):0.0}ms wait={Ms(stats.WaitForEncoder):0.0}ms async={stats.AsyncReadbackFrames} gpuYuv={stats.GpuYuvFrames} direct={stats.VaapiDirectFrames} codec={Environment.GetEnvironmentVariable("VB_BENCH_CODEC")} " +
                $"yuv={Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU_YUV") ?? "on"} readback={Environment.GetEnvironmentVariable("VIDEO_BUILDER_ASYNC_READBACK") ?? "on"}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
