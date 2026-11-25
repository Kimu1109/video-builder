using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>
/// GPU 描画で SkSL のシェーダーを使う効果 (設計書 22.54) が、以前の描き方 (読み戻して CPU で処理) と同じ絵になることを確かめる。
/// GPU を作れない環境では何もしない (VB_REQUIRE_GPU=1 なら失敗にする)。
/// </summary>
[Collection(nameof(GpuReadbackCollection))]
public unsafe class GpuShaderTests
{
    private static GpuContext? Gpu()
    {
        var gpu = GpuContext.TryCreate(out var error);
        if (gpu == null) Assert.False(GpuSelectionTests.GpuRequired, "GPU を作れません: " + error);
        return gpu;
    }

    private static void Set(Effect e, string key, double value)
    {
        var def = e.Definition.Params.FirstOrDefault(p => p.Key == key);
        if (def != null && def.Kind == ParamKind.Track) e.Tracks[key] = new AnimatedValue(value);
        else e.Values[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>背景・グラデーションの円 (色と明るさが場所で変わる)・緑の四角 (クロマキーで消える) のプロジェクト。</summary>
    private static (Project Project, Effect Effect) Scene(int w, int h, string id, string keys, double[] values)
    {
        var p = new Project();
        p.Settings.Width = w; p.Settings.Height = h;
        var back = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        back.Main.Values["color"] = "#203040"; back.Main.Values["type"] = "0";
        p.Root.Objects.Add(back);
        // 色の変わる絵: 縁取り付きの文字と、色の違う図形を 1 つのグループにはせず、図形 1 つに効果をかける
        var o = ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 60);
        o.Main.Values["color"] = "#10F020"; o.Main.Values["type"] = "1";   // キー色 (緑) に近い色
        o.Main.Tracks["size"] = new AnimatedValue(h * 0.7);
        p.Root.Objects.Add(o);
        // 色に幅を持たせる (クロマキーの境目・明るさのしきい値の境目を通るように)
        ObjectFactory.AddFilter(o, "filter.gradient").Tracks["strength"] = new AnimatedValue(70);
        var e = ObjectFactory.AddFilter(o, id);
        var names = keys.Length == 0 ? Array.Empty<string>() : keys.Split(',');
        for (int i = 0; i < names.Length; i++)
        {
            // "名前:値" は文字の値 (色など)。values の同じ位置は使わない
            if (names[i].Split(':') is [var key, var text]) e.Values[key] = text;
            else Set(e, names[i], values[i]);
        }
        if (id == "filter.lut") e.Values["file"] = CubeFile();
        return (p, e);
    }

    private static string? _cube;

    /// <summary>テスト用の 3D LUT (.cube)。色を少し入れ替えて、暖かくする。</summary>
    private static string CubeFile()
    {
        if (_cube != null && File.Exists(_cube)) return _cube;
        var path = Path.Combine(Path.GetTempPath(), $"vb-test-{Environment.ProcessId}.cube");
        var sb = new System.Text.StringBuilder("LUT_3D_SIZE 9\n");
        for (int b = 0; b < 9; b++)
            for (int g = 0; g < 9; g++)
                for (int r = 0; r < 9; r++)
                {
                    float R = r / 8f, G = g / 8f, B = b / 8f;
                    sb.Append(System.FormattableString.Invariant($"{Math.Min(1, R * 0.9f + G * 0.2f):0.000000} {G * 0.95f:0.000000} {B * 0.7f + R * 0.1f:0.000000}\n"));
                }
        File.WriteAllText(path, sb.ToString());
        return _cube = path;
    }

    private static (double Mean, int Max) Compare(SKBitmap a, SKBitmap b)
    {
        byte* pa = (byte*)a.GetPixels(), pb = (byte*)b.GetPixels();
        long total = 0; int max = 0, n = a.Width * a.Height * 4;
        for (int i = 0; i < n; i++) { int d = Math.Abs(pa[i] - pb[i]); total += d; max = Math.Max(max, d); }
        return ((double)total / n, max);
    }

    public static TheoryData<string, string, double[]> Cases => new()
    {
        { "filter.chromakey", "hue,saturation,edge", new double[] { 30, 120, 20 } },
        { "filter.glow", "threshold,strength", new double[] { 40, 150 } },
        { "filter.emit", "threshold", new double[] { 30 } },
        { "filter.flash", "strength,length,threshold", new double[] { 100, 50, 30 } },
        { "filter.sharpen", "amount,radius", new double[] { 150, 6 } },
        { "filter.dirblur", "length,angle", new double[] { 40, 30 } },
        { "filter.dirblur", "length,angle,fixed", new double[] { 25, -60, 1 } },     // サイズ固定 (端は延ばす)
        { "filter.radialblur", "amount,x,y", new double[] { 30, 20, -10 } },
        { "filter.chromatic", "amount,angle", new double[] { 6, 30 } },
        { "filter.chromatic", "amount,radial,type", new double[] { 8, 1, 2 } },      // 中心から広がる・緑と青
        { "filter.raster", "amplitude,wavelength", new double[] { 8, 40 } },
        { "filter.raster", "amplitude,wavelength,vertical", new double[] { 6, 30, 1 } },
        { "filter.ripple", "amplitude,wavelength,decay", new double[] { 6, 25, 60 } },
        { "filter.polar", "hole,rotation,swirl", new double[] { 10, 30, 40 } },
        { "filter.displace", "mode,param1,param2,size,feather", new double[] { 0, 15, -10, 120, 40 } },
        { "filter.displace", "mode,param1,size,feather", new double[] { 1, 60, 120, 40 } },   // 拡大
        { "filter.displace", "mode,param1,size,feather", new double[] { 2, 45, 120, 40 } },   // 回転
        { "filter.kaleido", "count,rotation", new double[] { 3, 20 } },
        { "filter.glow", "threshold,shape,radius", new double[] { 40, 1, 6 } },     // 十字の光の筋 (方向ブラーを 2 回)
        { "filter.hueconvert", "from:#10F020,hue,range", new double[] { 0, 90, 40 } },
        { "filter.colorkey", "color:#10F020,luma,chroma,edge", new double[] { 0, 140, 140, 30 } },
        { "filter.lumakey", "level,soft", new double[] { 120, 60 } },
        { "filter.lumakey", "level,soft,type", new double[] { 120, 60, 1 } },
        { "filter.curves", "p1,p3", new double[] { 100, 160 } },
        { "filter.curves", "p1,p3,channel", new double[] { 100, 160, 1 } },
        { "filter.shadowhighlight", "shadows,highlights", new double[] { 40, 30 } },
        { "filter.gradmap", "strength,useMid", new double[] { 80, 1 } },
        { "filter.look", "strength,preset", new double[] { 90, 1 } },
        { "filter.look", "strength,preset", new double[] { 90, 5 } },
        { "filter.look", "strength,preset", new double[] { 90, 7 } },
        { "filter.look", "strength,preset", new double[] { 90, 9 } },
        { "filter.lut", "strength", new double[] { 80 } },
        { "filter.light", "strength,width,direction", new double[] { 120, 10, 50 } },
        { "filter.bevel", "width,height", new double[] { 10, 80 } },
        { "filter.edgeblur", "radius", new double[] { 12 } },
        { "filter.edge", "strength,threshold", new double[] { 150, 10 } },
        { "filter.edge", "strength,threshold,overlay,type", new double[] { 150, 10, 1, 1 } },   // 不透明度の変わり目・重ねる
        { "filter.noise", "amount,size", new double[] { 40, 3 } },
        { "filter.noise", "amount,size,color", new double[] { 40, 1, 1 } },
        { "filter.mosaic", "size", new double[] { 8 } },
        { "filter.mosaic", "size", new double[] { 13 } },     // 割り切れない大きさ (端のマスは欠ける)
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Sksl_effects_on_gpu_match_the_readback_path(string id, string keys, double[] values)
    {
        using var gpu = Gpu();
        if (gpu == null) return;
        var (p, e) = Scene(320, 180, id, keys, values);
        SKBitmap Render()
        {
            using var engine = new RenderEngine(gpu);
            var bmp = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
            engine.RenderToBitmap(p, p.Root, 10, bmp);
            return bmp;
        }
        // 読み戻した回数は、ほかのテストと共有の数なので、同時に動くテストの分が混ざらないよう差だけを見る
        long before = Effects.GpuReadbacks;
        using var sksl = Render();
        long skslReadbacks = Effects.GpuReadbacks - before;
        SKBitmap readback;
        Effects.GpuShadersDisabled = true;
        long before2 = Effects.GpuReadbacks;
        try { readback = Render(); }
        finally { Effects.GpuShadersDisabled = false; }
        long oldReadbacks = Effects.GpuReadbacks - before2;
        Console.WriteLine($"BENCH sksl {id} readbacks sksl={skslReadbacks} before={oldReadbacks}");
        using (readback)
        {
            var (mean, max) = Compare(readback, sksl);
            // 効果がかかっていることも確かめる
            using var plain = new SKBitmap(sksl.Info);
            p.Root.Objects[1].Effects.Remove(e);
            using (var engine = new RenderEngine(gpu)) engine.RenderToBitmap(p, p.Root, 10, plain);
            var (changed, _) = Compare(plain, sksl);
            Console.WriteLine($"BENCH sksl {id} mean={mean:0.000} max={max} effect-changes={changed:0.00}");
            Assert.True(changed > 0.05, $"{id}: 効果がかかっていない");
            // シャープはぼかしを GPU (Skia) で作るので、CPU の自前のぼかしとの違いの分だけ許す
            // (ライト・ベベル・縁ぼかしも、アルファのぼかしを GPU で作る)
            double meanLimit = id is "filter.sharpen" or "filter.light" or "filter.bevel" or "filter.edgeblur" ? 1.0 : 0.3;
            Assert.True(mean < meanLimit, $"{id}: 以前の描き方と違う mean={mean} max={max}");
            // 以前は読み戻していた。SkSL では読み戻さない
            Assert.True(oldReadbacks >= 1, $"{id}: 以前の描き方で読み戻していない");
            Assert.True(skslReadbacks == 0, $"{id}: まだ読み戻している ({skslReadbacks} 回。以前は {oldReadbacks} 回)");
        }
    }

    /// <summary>GPU 描画で、SkSL と読み戻し (以前の描き方) の速さを比べて表示する (1080p)。</summary>
    [Fact]
    public void Report_gpu_shader_speed()
    {
        using var gpu = Gpu();
        if (gpu == null) return;
        Console.WriteLine($"BENCH sksl-speed renderer={gpu.Renderer}");
        foreach (var row in Cases)
        {
            var (id, keys, values) = ((string)row.Data.Item1, (string)row.Data.Item2, (double[])row.Data.Item3);
            var (p, _) = Scene(1920, 1080, id, keys, values);
            var o = p.Root.Objects[1];
            double Measure()
            {
                using var engine = new RenderEngine(gpu);
                using var bmp = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
                // 効果の結果は使い回されるので、元の図形の色を毎回少し変える
                void Draw(int k) { o.Main.Values["color"] = $"#10F0{k % 256:X2}"; engine.RenderToBitmap(p, p.Root, 10, bmp); }
                for (int k = 0; k < 5; k++) Draw(k);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int k = 5; k < 15; k++) Draw(k);
                return sw.Elapsed.TotalMilliseconds / 10;
            }
            double sksl = Measure();
            Effects.GpuShadersDisabled = true;
            double readback;
            try { readback = Measure(); }
            finally { Effects.GpuShadersDisabled = false; }
            Console.WriteLine($"BENCH sksl-speed {id,-18} sksl {sksl,7:0.00} ms   readback {readback,7:0.00} ms");
        }
    }
}

/// <summary>
/// 読み戻した回数 (Effects.GpuReadbacks) は全体で 1 つの数なので、数えるテストはほかのテストと同時に動かさない。
/// </summary>
[CollectionDefinition(nameof(GpuReadbackCollection), DisableParallelization = true)]
public sealed class GpuReadbackCollection;
