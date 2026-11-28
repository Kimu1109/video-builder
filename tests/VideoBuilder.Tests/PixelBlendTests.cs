using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>CPU 描画の自前の合成 (PixelBlend) が、Skia の合成モードと同じ結果になることを確かめる (設計書 22.53)。</summary>
public unsafe class PixelBlendTests
{
    // RenderEngine の合成モードの番号と、対応する Skia の合成モード (2 = 減算は Skia にない)
    public static TheoryData<int, SKBlendMode> Modes => new()
    {
        { 1, SKBlendMode.Plus }, { 3, SKBlendMode.Multiply }, { 4, SKBlendMode.Screen }, { 5, SKBlendMode.Overlay },
        { 6, SKBlendMode.Lighten }, { 7, SKBlendMode.Darken }, { 8, SKBlendMode.Luminosity }, { 9, SKBlendMode.Color },
        { 10, SKBlendMode.ColorBurn }, { 11, SKBlendMode.HardLight }, { 12, SKBlendMode.Difference },
    };

    /// <summary>乱数の BGRA (乗算済みアルファ)。opaque なら不透明。</summary>
    private static SKBitmap Random(int w, int h, int seed, bool opaque)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var rnd = new Random(seed);
        var p = (byte*)bmp.GetPixels();
        for (int i = 0; i < w * h; i++, p += 4)
        {
            int a = opaque ? 255 : rnd.Next(4) == 0 ? 0 : rnd.Next(4) == 0 ? 255 : rnd.Next(256);
            for (int c = 0; c < 3; c++) p[c] = (byte)rnd.Next(a + 1);
            p[3] = (byte)a;
        }
        return bmp;
    }

    private static (double Mean, int Max) Compare(SKBitmap a, SKBitmap b)
    {
        byte* pa = (byte*)a.GetPixels(), pb = (byte*)b.GetPixels();
        long total = 0; int max = 0, n = a.Width * a.Height * 4;
        for (int i = 0; i < n; i++) { int d = Math.Abs(pa[i] - pb[i]); total += d; max = Math.Max(max, d); }
        return ((double)total / n, max);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Composite_matches_skia_blend_modes(int blend, SKBlendMode mode)
    {
        foreach (bool opaqueBack in new[] { true, false })
        {
            // 幅 67: 8 画素ずつの計算の余り (端数) も通る
            using var src = Random(67, 41, 1, false);
            using var expected = Random(67, 41, 2, opaqueBack);
            using var actual = expected.Copy();
            using (var c = new SKCanvas(expected))
            using (var p = new SKPaint { BlendMode = mode })
                c.DrawBitmap(src, 0, 0, p);
            PixelBlend.Composite((byte*)src.GetPixels(), src.RowBytes, (byte*)actual.GetPixels(), actual.RowBytes, src.Width, src.Height, blend);
            var (mean, max) = Compare(expected, actual);
            Console.WriteLine($"BENCH pixelblend {mode} opaqueBack={opaqueBack} mean={mean:0.000} max={max}");
            // 8 ビットへの丸めの違いだけを許す (Skia の CPU 描画は 16 ビットの近似や浮動小数点で計算する)
            Assert.True(mean < 0.6, $"{mode}: 平均の差が大きい {mean}");
            Assert.True(max <= (mode is SKBlendMode.ColorBurn or SKBlendMode.Luminosity or SKBlendMode.Color ? 4 : 2), $"{mode}: 最大の差が大きい {max}");
        }
    }

    [Fact]
    public void Subtract_matches_the_previous_invert_add_invert_drawing()
    {
        using var src = Random(67, 41, 3, false);
        using var expected = Random(67, 41, 4, true);   // 画面は不透明 (はじめに黒で塗る)
        using var actual = expected.Copy();
        using (var c = new SKCanvas(expected))
        {
            // 以前の描き方: 背景を反転 → 加算 → もう一度反転
            using var invert = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Difference };
            using var plus = new SKPaint { BlendMode = SKBlendMode.Plus };
            c.DrawPaint(invert);
            c.DrawBitmap(src, 0, 0, plus);
            c.DrawPaint(invert);
        }
        PixelBlend.Composite((byte*)src.GetPixels(), src.RowBytes, (byte*)actual.GetPixels(), actual.RowBytes, src.Width, src.Height, 2);
        var (mean, max) = Compare(expected, actual);
        Console.WriteLine($"BENCH pixelblend subtract mean={mean:0.000} max={max}");
        Assert.True(mean < 0.6 && max <= 2, $"減算: mean={mean} max={max}");
    }

    /// <summary>
    /// 整数で計算する合成は、SIMD の幅 (256 = AVX2・128 = ARM64 の NEON・0 = SIMD なし) によらず同じ結果になる。
    /// x64 の上でも、ARM64 で使う 128 ビットの計算を確かめられる。
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(6)] [InlineData(7)] [InlineData(12)]
    [InlineData(20)] [InlineData(21)] [InlineData(22)] [InlineData(23)]
    public void Integer_blends_give_the_same_result_for_every_vector_width(int blend)
    {
        foreach (bool opaqueBack in new[] { true, false })
        {
            // 幅 67: 8 画素ずつ・4 画素ずつの計算の余り (端数) も通る
            using var src = Random(67, 41, 5, false);
            using var back = Random(67, 41, 6, opaqueBack);
            SKBitmap Blend(int bits)
            {
                var r = back.Copy();
                PixelBlend.Composite((byte*)src.GetPixels(), src.RowBytes, (byte*)r.GetPixels(), r.RowBytes, src.Width, src.Height, blend, bits);
                return r;
            }
            using var scalar = Blend(0);
            using var v128 = Blend(128);
            using var v256 = Blend(256);
            Assert.Equal((0.0, 0), Compare(scalar, v128));
            Assert.Equal((0.0, 0), Compare(scalar, v256));
        }
    }

    /// <summary>合成モードのオブジェクト (回転・拡大・半透明) を、自前の合成と Skia の合成で描いて比べる。</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)]
    public void Blended_objects_render_the_same_as_with_skia(int blend)
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 180;
        TimelineObject Shape(int layer, string color, int type, int size, double x, double y)
        {
            var o = ObjectFactory.Create(EffectRegistry.Shape, layer, 0, 60);
            o.Main.Values["color"] = color; o.Main.Values["type"] = type.ToString();
            o.Main.Tracks["size"] = new AnimatedValue(size);
            o.Draw!.Tracks["x"] = new AnimatedValue(x); o.Draw!.Tracks["y"] = new AnimatedValue(y);
            p.Root.Objects.Add(o);
            return o;
        }
        Shape(0, "#1B2A41", 0, 0, 0, 0);
        Shape(1, "#F2A900", 1, 120, -60, 0);
        Shape(2, "#3A6EA5", 2, 100, 50, 20);
        var top = Shape(3, "#C05080", 1, 110, 10, -10);
        top.Draw!.Values["blend"] = blend.ToString();
        top.Draw!.Tracks["rotation"] = new AnimatedValue(20);
        top.Draw!.Tracks["zoom"] = new AnimatedValue(130);
        top.Draw!.Tracks["alpha"] = new AnimatedValue(70);
        SKBitmap Render()
        {
            using var e = new RenderEngine();
            var bmp = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
            e.RenderToBitmap(p, p.Root, 0, bmp);
            return bmp;
        }
        using var mine = Render();
        SKBitmap skia;
        PixelBlend.Disabled = true;
        try { skia = Render(); }
        finally { PixelBlend.Disabled = false; }
        using (skia)
        {
            var (mean, max) = Compare(skia, mine);
            Console.WriteLine($"BENCH pixelblend object blend={blend} mean={mean:0.000} max={max}");
            Assert.True(mean < 0.3 && max <= 4, $"合成モード {blend}: mean={mean} max={max}");
        }
    }

    /// <summary>1080p で、合成モードのオブジェクト (画面の大半を覆う) を重ねる時間を、自前の合成と Skia の合成で比べて表示する。</summary>
    [Fact]
    public void Report_blend_mode_speed()
    {
        var p = new Project();
        p.Settings.Width = 1920; p.Settings.Height = 1080;
        var back = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        back.Main.Values["color"] = "#1B2A41"; back.Main.Values["type"] = "0";
        p.Root.Objects.Add(back);
        var top = ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 60);
        top.Main.Values["color"] = "#C05080"; top.Main.Values["type"] = "1";
        top.Main.Tracks["size"] = new AnimatedValue(1000);
        p.Root.Objects.Add(top);
        double Measure(int blend, int frames = 20)
        {
            top.Draw!.Values["blend"] = blend.ToString();
            using var e = new RenderEngine();
            using var bmp = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
            for (int f = 0; f < 3; f++) e.RenderToBitmap(p, p.Root, f, bmp);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 3; f < 3 + frames; f++) e.RenderToBitmap(p, p.Root, f, bmp);
            return sw.Elapsed.TotalMilliseconds / frames;
        }
        // .NET は最初はコードを簡単にしか最適化しないので、どの合成モードも先に何度も動かしておく (設計書 22.27 の注意)
        for (int blend = 0; blend <= 12; blend++) Measure(blend, 40);
        double normal = Measure(0);
        Console.WriteLine($"BENCH blend-speed normal(0) {normal:0.00} ms/frame");
        foreach (int blend in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })
        {
            double mine = Measure(blend);
            PixelBlend.Disabled = true;
            double skia;
            try { skia = Measure(blend); }
            finally { PixelBlend.Disabled = false; }
            Console.WriteLine($"BENCH blend-speed blend={blend,2} pixelblend {mine,6:0.00} ms/frame   skia {skia,6:0.00} ms/frame");
        }
    }

    // ---- 効果の中の合成 (PixelBlend.Draw・DrawImage・BeginLayer) ----

    /// <summary>効果のパラメータを、トラックか値かに合わせて設定する。</summary>
    private static void Set(Effect e, string key, double value)
    {
        var def = e.Definition.Params.FirstOrDefault(p => p.Key == key);
        if (def != null && def.Kind == ParamKind.Track) e.Tracks[key] = new AnimatedValue(value);
        else e.Values[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? _blendImage;

    /// <summary>画像ファイル合成で重ねる絵 (色の帯)。</summary>
    private static string BlendImage()
    {
        if (_blendImage != null && File.Exists(_blendImage)) return _blendImage;
        var path = Path.Combine(Path.GetTempPath(), $"vb-blend-{Environment.ProcessId}.png");
        using var bmp = new SKBitmap(new SKImageInfo(90, 60, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            using var sh = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(90, 60), new[] { new SKColor(250, 60, 40), new SKColor(40, 200, 250, 180) }, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = sh };
            c.DrawPaint(paint);
        }
        using (var data = bmp.Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.Create(path)) data.SaveTo(fs);
        return _blendImage = path;
    }

    // 効果の ID とパラメータ。どれも合成モードを使う描き方をする設定にする
    public static TheoryData<string, string, double[]> EffectCases => new()
    {
        { "filter.diffuse", "strength,radius", new double[] { 70, 10 } },
        { "filter.flash", "strength,length,threshold", new double[] { 100, 60, 20 } },
        { "filter.mirror", "", Array.Empty<double>() },
        { "filter.innershadow", "density,spread,x,y", new double[] { 90, 6, 5, 4 } },
        { "filter.glow", "", Array.Empty<double>() },
        { "filter.emit", "threshold", new double[] { 20 } },                // 既定のしきい値 (80%) では、この色は光らない
        { "filter.vignette", "strength", new double[] { 100 } },
        { "filter.gradient", "strength", new double[] { 100 } },
        { "filter.ascii", "", Array.Empty<double>() },
        { "filter.lensflare", "strength,x,y", new double[] { 100, -20, -15 } },  // 光源を絵の中に置く (絵の外は描かれない)
        { "filter.lightleak", "strength", new double[] { 100 } },
        { "filter.border", "size,only", new double[] { 6, 1 } },
        { "filter.anim", "type", new double[] { 20 } },                     // 虹色
        { "filter.transition", "type,edge", new double[] { 47, 6 } },       // 形のワイプ (境界線あり)
        { "filter.transition", "type,edge", new double[] { 48, 0 } },       // 形のワイプ (境界線なし)
        { "filter.transition", "type", new double[] { 78 } },               // 光漏れ
        { "filter.imageblend", "blend", new double[] { 2 } },               // 乗算
        { "filter.imageblend", "blend", new double[] { 3 } },               // スクリーン
        { "filter.imageblend", "blend", new double[] { 9 } },               // ハードライト
    };

    [Theory]
    [MemberData(nameof(EffectCases))]
    public void Effects_with_blend_modes_render_the_same_as_with_skia(string id, string keys, double[] values)
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 180;
        var back = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        back.Main.Values["color"] = "#3A5070"; back.Main.Values["type"] = "0";
        p.Root.Objects.Add(back);
        var o = ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 60);
        o.Main.Values["color"] = "#E0A040"; o.Main.Values["type"] = "1";
        o.Main.Tracks["size"] = new AnimatedValue(120);
        p.Root.Objects.Add(o);
        var e = ObjectFactory.AddFilter(o, id);
        var names = keys.Length == 0 ? Array.Empty<string>() : keys.Split(',');
        for (int i = 0; i < names.Length; i++) Set(e, names[i], values[i]);
        if (id == "filter.imageblend") e.Values["file"] = BlendImage();
        int frame = id == "filter.transition" ? 7 : 10;   // トランジションは途中のフレーム
        SKBitmap Render()
        {
            using var engine = new RenderEngine();
            var bmp = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
            engine.RenderToBitmap(p, p.Root, frame, bmp);
            return bmp;
        }
        using var mine = Render();
        SKBitmap skia;
        PixelBlend.Disabled = true;
        try { skia = Render(); }
        finally { PixelBlend.Disabled = false; }
        using (skia)
        {
            var (mean, max) = Compare(skia, mine);
            // 効果が絵を変えていることも確かめる (設定が効いていなければ比べる意味がない)
            using var plain = new SKBitmap(mine.Info);
            o.Effects.Remove(e);
            using (var engine = new RenderEngine()) engine.RenderToBitmap(p, p.Root, frame, plain);
            var (changed, _) = Compare(plain, mine);
            Console.WriteLine($"BENCH pixelblend effect {id} {keys}={string.Join(",", values)} mean={mean:0.000} max={max} effect-changes={changed:0.00}");
            Assert.True(changed > 0.05, $"{id}: 効果がかかっていない");
            Assert.True(mean < 0.3 && max <= 6, $"{id}: Skia で合成したものと違う mean={mean} max={max}");
        }
    }

    /// <summary>1080p で、合成モードを使う効果の描画時間を、自前の合成と Skia の合成で比べて表示する (Release で測ること)。</summary>
    [Fact]
    public void Report_effect_blend_speed()
    {
        var cases = EffectCases.Select(r => ((string)r.Data.Item1, (string)r.Data.Item2, (double[])r.Data.Item3)).ToList();
        double Measure(string id, string keys, double[] values, int frames)
        {
            var p = new Project();
            p.Settings.Width = 1920; p.Settings.Height = 1080;
            var back = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
            back.Main.Values["color"] = "#3A5070"; back.Main.Values["type"] = "0";
            p.Root.Objects.Add(back);
            var o = ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 60);
            o.Main.Values["color"] = "#E0A040"; o.Main.Values["type"] = "1";
            o.Main.Tracks["size"] = new AnimatedValue(800);
            p.Root.Objects.Add(o);
            var e = ObjectFactory.AddFilter(o, id);
            var names = keys.Length == 0 ? Array.Empty<string>() : keys.Split(',');
            for (int i = 0; i < names.Length; i++) Set(e, names[i], values[i]);
            if (id == "filter.imageblend") e.Values["file"] = BlendImage();
            using var engine = new RenderEngine();
            using var bmp = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
            int frame = id == "filter.transition" ? 7 : 10;
            // 効果の結果は使い回されるので、元の図形の色を毎回少し変えて、効果を作り直させる
            void Draw(int k) { o.Main.Values["color"] = $"#E0A0{k % 256:X2}"; engine.RenderToBitmap(p, p.Root, frame, bmp); }
            Draw(0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int k = 1; k <= frames; k++) Draw(k);
            return sw.Elapsed.TotalMilliseconds / frames;
        }
        // .NET の最適化が効くまで動かしてから測る
        foreach (var (id, keys, values) in cases) Measure(id, keys, values, 8);
        foreach (var (id, keys, values) in cases)
        {
            double mine = Measure(id, keys, values, 10);
            PixelBlend.Disabled = true;
            double skia;
            try { skia = Measure(id, keys, values, 10); }
            finally { PixelBlend.Disabled = false; }
            Console.WriteLine($"BENCH effect-speed {id,-18} {keys}={string.Join(",", values),-14} pixelblend {mine,7:0.00} ms   skia {skia,7:0.00} ms");
        }
    }

    /// <summary>縁取りの「縁だけ」を CPU 描画 (自前) と GPU 描画 (Skia の画像フィルタ) で描いて比べる。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Border_only_on_cpu_matches_skia_on_gpu(double blur)
    {
        using var gpu = GpuContext.TryCreate(out var error);
        if (gpu == null) { Assert.False(GpuSelectionTests.GpuRequired, "GPU を作れません: " + error); return; }
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 180;
        var back = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        back.Main.Values["color"] = "#3A5070"; back.Main.Values["type"] = "0";
        p.Root.Objects.Add(back);
        var o = ObjectFactory.Create(EffectRegistry.Text, 1, 0, 60);
        o.Main.Values["text"] = "縁だけ";
        p.Root.Objects.Add(o);
        var e = ObjectFactory.AddFilter(o, "filter.border");
        Set(e, "size", 5); Set(e, "blur", blur); Set(e, "only", 1);
        using var cpu = new RenderEngine();
        using var onGpu = new RenderEngine(gpu);
        using var a = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var b = new SKBitmap(a.Info);
        cpu.RenderToBitmap(p, p.Root, 0, a);
        onGpu.RenderToBitmap(p, p.Root, 0, b);
        var (mean, max) = Compare(a, b);
        Console.WriteLine($"BENCH border-only blur={blur} cpu-vs-gpu mean={mean:0.000} max={max}");
        // ぼかしの計算方法 (自前の 8 ビットの重みと Skia) と、文字の縁のアンチエイリアスの違いだけを許す
        Assert.True(mean < 0.6, $"縁だけの縁取りが違う: mean={mean} max={max}");
    }
}
