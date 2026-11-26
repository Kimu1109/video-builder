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
            if (names[i].Split(':') is ["frame", _]) continue;   // 描くフレーム (Frame)
            if (names[i].Split(':') is [var key, var text]) e.Values[key] = text;
            else Set(e, names[i], values[i]);
        }
        if (id == "filter.lut") e.Values["file"] = CubeFile();
        if (id == "filter.displace" && e.Values.GetValueOrDefault("shape") == "2") e.Values["file"] = MapFile();
        return (p, e);
    }

    private static string? _map;

    /// <summary>テスト用のずらしのマップ (赤と緑が場所で変わる画像)。</summary>
    private static string MapFile()
    {
        if (_map != null && File.Exists(_map)) return _map;
        var path = Path.Combine(Path.GetTempPath(), $"vb-map-{Environment.ProcessId}.png");
        using var bmp = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
                bmp.SetPixel(x, y, new SKColor((byte)(x * 4), (byte)(255 - y * 5), 128, (byte)(x < 8 ? 120 : 255)));
        using (var data = bmp.Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.Create(path)) data.SaveTo(fs);
        return _map = path;
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

    /// <summary>
    /// 描くフレーム。トランジションはイン (0.5 秒 = 15 フレーム) の途中で、後半 (入れ替わってから効果が引いていく所) のフレーム。
    /// 色収差・ピクセル化・波紋・渦巻きなどは後半だけ描く。
    /// </summary>
    /// <summary>描くフレーム。トランジションは後半だけ描くものがあるので 11 (keys の "frame:番号" で変えられる)。</summary>
    private static int Frame(string id, string keys) =>
        keys.Split(',').Select(k => k.Split(':')).FirstOrDefault(k => k is ["frame", _]) is [_, var f] ? int.Parse(f) : id == "filter.transition" ? 11 : 10;

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
        { "filter.anim", "type,strength,speed", new double[] { 24, 12, 1 } },      // 旗のようになびく
        { "filter.transition", "type", new double[] { 81 } },                       // 渦巻き
        { "filter.swirl", "angle,radius", new double[] { 120, 80 } },
        { "filter.bulge", "amount,radius", new double[] { 60, 70 } },
        { "filter.bulge", "amount,radius", new double[] { -60, 70 } },
        { "filter.fisheye", "strength", new double[] { 70 } },
        { "filter.mirrortile", "countX,countY", new double[] { 3, 2 } },
        { "filter.mirrortile", "countX,countY,mirror", new double[] { 3, 2, 1 } },
        { "filter.displace", "shape,param1,param2", new double[] { 2, 12, -8 } },   // マップの画像でずらす
        { "filter.transition", "type", new double[] { 77 } },                       // 色収差
        { "filter.transition", "type", new double[] { 79 } },                       // ピクセル化 (モザイク)
        { "filter.transition", "type", new double[] { 80 } },                       // 波紋
        { "filter.deinterlace", "mode", new double[] { 0 } },
        { "filter.deinterlace", "mode", new double[] { 1 } },
        { "filter.deinterlace", "mode", new double[] { 2 } },
        { "filter.deinterlace", "mode", new double[] { 3 } },
        { "filter.vhs", "strength,noise,wobble", new double[] { 80, 60, 70 } },
        { "filter.vhs", "strength,noise,wobble", new double[] { 100, 0, 100 } },
        { "filter.crt", "scanline,mask,curvature,vignette", new double[] { 60, 50, 40, 50 } },
        { "filter.glitch", "strength,frequency,block", new double[] { 100, 100, 12 } },
        { "filter.halftone", "size,angle,mode", new double[] { 8, 45, 0 } },
        { "filter.halftone", "size,angle,mode", new double[] { 6, 0, 1 } },
        { "filter.halftone", "size,mode", new double[] { 9, 2 } },
        { "filter.pixelart", "size,levels", new double[] { 6, 4 } },
        { "filter.pixelart", "size,levels,dither", new double[] { 5, 3, 1 } },
        { "filter.sketch", "strength", new double[] { 100 } },
        { "filter.sketch", "strength,color", new double[] { 150, 1 } },
        { "filter.paint", "type,radius", new double[] { 0, 6 } },
        { "filter.paint", "type,radius", new double[] { 1, 15 } },
        { "filter.grain", "amount,size", new double[] { 80, 1.5 } },
        { "filter.grain", "amount,size,color", new double[] { 80, 0.7, 1 } },
        { "filter.lensblur", "radius,boost", new double[] { 6, 60 } },
        { "filter.lensblur", "radius,boost,fixed", new double[] { 10, 0, 1 } },
        { "filter.lensblur", "radius,boost", new double[] { 30, 50 } },
        { "filter.transition", "type", new double[] { 65 } },                       // ディゾルブ
        { "filter.transition", "type", new double[] { 66 } },                       // ルールワイプ (画像なし)
        { "filter.transition", "type,ruleInvert,feather", new double[] { 66, 1, 40 } },
        { "filter.transition", "type", new double[] { 76 } },                       // グリッチ
        { "filter.transition", "type,in", new double[] { 82, 1 } },                 // 焼けて消える
        { "filter.transition", "type,in,out,frame:50", new double[] { 82, 0, 1, 0 } }, // 焼けて消える (出る側。縁が燃える)
        { "filter.transition", "type", new double[] { 83 } },                       // フィルム焼け
        { "filter.transition", "type,in,out", new double[] { 65, 0, 2 } },          // ディゾルブ (出る側)
        { "filter.sphere", "rx,ry,shade", new double[] { 20, 40, 60 } },
        { "filter.sphere", "size,rz", new double[] { 90, 30 } },
        { "filter.ascii", "size", new double[] { 10 } },
        { "filter.ascii", "size,color,chars", new double[] { 8, 1, 1 } },
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
            engine.RenderToBitmap(p, p.Root, Frame(id, keys), bmp);
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
            if (Environment.GetEnvironmentVariable("VB_DUMP") is { Length: > 0 } dump)
            {
                foreach (var (name, bmp) in new[] { ("sksl", sksl), ("readback", readback) })
                    using (var data = bmp.Encode(SKEncodedImageFormat.Png, 100))
                    using (var fs = File.Create(Path.Combine(dump, $"{id}-{keys.Replace(',', '_').Replace(':', '-')}-{name}.png"))) data.SaveTo(fs);
            }
            // 効果がかかっていることも確かめる
            using var plain = new SKBitmap(sksl.Info);
            p.Root.Objects[1].Effects.Remove(e);
            using (var engine = new RenderEngine(gpu)) engine.RenderToBitmap(p, p.Root, Frame(id, keys), plain);
            var (changed, _) = Compare(plain, sksl);
            Console.WriteLine($"BENCH sksl {id} mean={mean:0.000} max={max} effect-changes={changed:0.00}");
            Assert.True(changed > 0.05, $"{id}: 効果がかかっていない");
            // シャープはぼかしを GPU (Skia) で作るので、CPU の自前のぼかしとの違いの分だけ許す
            // (ライト・ベベル・縁ぼかしも、アルファのぼかしを GPU で作る。ASCII アートは、文字の縁の濃さ (Skia が塗りの色から決める
            // 文字のガンマ) が、色をシェーダーで決めると中間の値になる分だけ違う)
            double meanLimit = id is "filter.sharpen" or "filter.light" or "filter.bevel" or "filter.edgeblur" or "filter.ascii" ? 1.0 : 0.3;
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
