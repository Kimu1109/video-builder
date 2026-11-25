using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>
/// GPU と CPU の間の受け渡しを減らす高速化 (設計書 22.40) が、絵を変えないことを確かめる。
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
}
