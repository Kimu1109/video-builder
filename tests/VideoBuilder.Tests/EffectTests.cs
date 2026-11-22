using System.Diagnostics;
using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>追加した組み込みエフェクト (グロー・ブラー・色収差など) とアニメーション効果・トランジションのテスト。</summary>
public class EffectTests
{
    private static Project Small(int w = 320, int h = 240)
    {
        var p = new Project();
        p.Settings.Width = w; p.Settings.Height = h;
        return p;
    }

    private static TimelineObject AddShape(Project p, int layer, string color, int size = 100, int type = 2, int length = 60)
    {
        var o = ObjectFactory.Create(EffectRegistry.Shape, layer, 0, length);
        o.Main.Values["color"] = color;
        o.Main.Values["type"] = type.ToString();
        o.Main.Tracks["size"] = new AnimatedValue(size);
        p.Root.Objects.Add(o);
        return o;
    }

    private static Effect Filter(TimelineObject o, string id, params (string Key, double Value)[] tracks)
    {
        var e = ObjectFactory.AddFilter(o, id);
        foreach (var (k, v) in tracks) e.Tracks[k] = new AnimatedValue(v);
        return e;
    }

    private static SKBitmap Render(Project p, int frame = 0, RenderEngine? engine = null)
    {
        using var own = engine == null ? new RenderEngine() : null;
        using var img = (engine ?? own!).RenderImage(p, p.Root, frame);
        return SKBitmap.FromImage(img);
    }

    private static readonly string[] NewFilters =
    {
        "filter.glow", "filter.dirblur", "filter.radialblur", "filter.sharpen", "filter.chromatic", "filter.raster", "filter.noise",
        "filter.vignette", "filter.gradient", "filter.diagclip", "filter.levels", "filter.posterize", "filter.anim",
    };

    [Fact]
    public void New_filters_are_registered_with_definitions_and_renderers()
    {
        foreach (var id in NewFilters)
        {
            Assert.True(EffectRegistry.Exists(id), id);
            Assert.True(EffectRegistry.IsBuiltin(id), id);
            Assert.Equal(EffectCategory.Filter, EffectRegistry.Get(id).Category);
            Assert.NotNull(Effects.Find(id));
        }
    }

    [Fact]
    public void Every_new_filter_renders_on_objects_and_on_the_frame_buffer()
    {
        foreach (var id in NewFilters)
        {
            var p = Small();
            AddShape(p, 0, "#204060", 0, 0);                       // 背景
            var star = AddShape(p, 1, "#FFD040", 120, 6);
            Filter(star, id);
            var text = ObjectFactory.Create(EffectRegistry.Text, 2, 0, 60);
            ObjectFactory.AddFilter(text, id);
            p.Root.Objects.Add(text);
            p.Root.Objects.Add(ObjectFactory.Create(id, 3, 0, 60)); // 画面全体にかける
            foreach (int f in new[] { 0, 7, 59 })
            {
                using var bmp = Render(p, f);
                Assert.Equal(320, bmp.Width);
            }
        }
    }

    [Fact]
    public void Every_animation_and_transition_type_renders()
    {
        for (int type = 0; type < EffectRegistry.AnimationTypes.Length; type++)
        {
            var p = Small();
            var o = AddShape(p, 0, "#FFFFFF", 80);
            ObjectFactory.AddFilter(o, "filter.anim").Values["type"] = type.ToString();
            for (int f = 0; f < 60; f += 9) Render(p, f).Dispose();
        }
        for (int type = 0; type < EffectRegistry.TransitionTypes.Length; type++)
        {
            var p = Small();
            var o = AddShape(p, 0, "#FFFFFF", 80);
            var e = ObjectFactory.AddFilter(o, "filter.transition");
            e.Values["type"] = type.ToString();
            e.Tracks["out"] = new AnimatedValue(0.5);
            using (var mid = Render(p, 6)) Assert.Equal(320, mid.Width);      // 入りの途中
            using (var full = Render(p, 30))                                   // 入り終わった後は元のまま
                Assert.Equal(SKColors.White, full.GetPixel(160, 120));
            using var last = Render(p, 55);                                    // 出の途中
        }
    }

    [Fact]
    public void Glow_lights_up_the_area_around_bright_shapes()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 60);
        using (var plain = Render(p)) Assert.Equal(SKColors.Black, plain.GetPixel(160 + 40, 120));
        Filter(o, "filter.glow", ("radius", 30), ("threshold", 50));
        using var lit = Render(p);
        Assert.True(lit.GetPixel(160 + 40, 120).Red > 20, "周りが光っていない: " + lit.GetPixel(200, 120));
        Assert.Equal(SKColors.White, lit.GetPixel(160, 120));

        // しきい値より暗い色は光らない
        var dark = Small();
        Filter(AddShape(dark, 0, "#404040", 60), "filter.glow", ("radius", 30), ("threshold", 50));
        using var d = Render(dark);
        Assert.Equal(SKColors.Black, d.GetPixel(160 + 40, 120));
    }

    [Fact]
    public void Directional_blur_smears_only_along_its_angle()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 60);
        Filter(o, "filter.dirblur", ("length", 40), ("angle", 0));
        using var bmp = Render(p);
        Assert.True(bmp.GetPixel(160 + 40, 120).Red > 30, "横に伸びていない");
        Assert.True(bmp.GetPixel(160 + 40, 120).Red < 200);
        Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120 + 40));        // 縦には伸びない
        Assert.Equal(SKColors.White, bmp.GetPixel(160, 120));
    }

    [Fact]
    public unsafe void Repeated_averaging_keeps_flat_colors_unchanged()
    {
        // 2 か所の平均を何度も繰り返すので、丸め誤差で暗くなったり明るくなったりしないこと
        const int W = 64, H = 32;
        var src = PixelPool.Rent(W * H * 4);
        var tmp = PixelPool.Rent(W * H * 4);
        try
        {
            var px = new Span<uint>((void*)src, W * H);
            px.Fill(0xFF7F3F1Fu);
            var r = PixelFx.DirectionalBlur(src, tmp, W, H, W * 4, 0.6f, 0.8f, 200, clamp: true);
            foreach (var v in new Span<uint>((void*)r, W * H))
            {
                Assert.InRange((int)(v >> 16 & 0xFF), 0x7E, 0x80);
                Assert.InRange((int)(v & 0xFF), 0x1E, 0x20);
                Assert.Equal(0xFFu, v >> 24);
            }
            px = new Span<uint>((void*)src, W * H);
            px.Fill(0xFF7F3F1Fu);
            r = PixelFx.RadialBlur(src, tmp, W, H, W * 4, 10, 20, 0.8f, clamp: true);
            foreach (var v in new Span<uint>((void*)r, W * H)) Assert.InRange((int)(v >> 8 & 0xFF), 0x3E, 0x40);
        }
        finally
        {
            PixelPool.Return(src, W * H * 4);
            PixelPool.Return(tmp, W * H * 4);
        }
    }

    [Fact]
    public void Radial_blur_keeps_the_center_and_blurs_the_edges()
    {
        var p = Small();
        AddShape(p, 0, "#FFFFFF", 200);
        p.Root.Objects.Add(ObjectFactory.Create("filter.radialblur", 1, 0, 60));
        p.Root.Objects[1].Main.Tracks["amount"] = new AnimatedValue(40);
        using var bmp = Render(p);
        Assert.Equal(SKColors.White, bmp.GetPixel(160, 120));
        var edge = bmp.GetPixel(160 + 100, 120);
        Assert.InRange(edge.Red, 30, 225);   // 境目がなだらかになっている
    }

    [Fact]
    public void Chromatic_aberration_splits_red_and_blue_at_edges()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 100);
        Filter(o, "filter.chromatic", ("amount", 6), ("angle", 0));
        using var bmp = Render(p);
        var left = bmp.GetPixel(160 - 50 - 3, 120);   // 左の縁の外: 青だけが出ている
        var right = bmp.GetPixel(160 + 50 + 3, 120);  // 右の縁の外: 赤だけが出ている
        Assert.True(left.Blue > 200 && left.Red < 40, "左: " + left);
        Assert.True(right.Red > 200 && right.Blue < 40, "右: " + right);
        Assert.Equal(SKColors.White, bmp.GetPixel(160, 120));
    }

    [Fact]
    public void Levels_and_posterize_remap_tones()
    {
        var p = Small();
        var o = AddShape(p, 0, "#808080", 0, 0);
        Filter(o, "filter.levels", ("inWhite", 128));
        using (var bmp = Render(p)) Assert.Equal(SKColors.White, bmp.GetPixel(10, 10));

        var q = Small();
        AddShape(q, 0, "#606060", 0, 0);
        var f = ObjectFactory.Create("filter.posterize", 1, 0, 60);
        f.Main.Tracks["levels"] = new AnimatedValue(2);
        q.Root.Objects.Add(f);
        using (var bmp = Render(q)) Assert.Equal(SKColors.Black, bmp.GetPixel(10, 10));
    }

    [Fact]
    public void Sharpen_increases_edge_contrast()
    {
        var p = Small();
        AddShape(p, 0, "#808080", 0, 0);
        AddShape(p, 1, "#C0C0C0", 100);
        using var before = Render(p);
        p.Root.Objects.Add(ObjectFactory.Create("filter.sharpen", 2, 0, 60));
        p.Root.Objects[2].Main.Tracks["radius"] = new AnimatedValue(4);
        using var after = Render(p);
        // 明るい側の縁はより明るく、暗い側の縁はより暗く
        Assert.True(after.GetPixel(160 + 48, 120).Red > before.GetPixel(160 + 48, 120).Red);
        Assert.True(after.GetPixel(160 + 52, 120).Red < before.GetPixel(160 + 52, 120).Red);
    }

    [Fact]
    public void Noise_is_reproducible_and_changes_per_frame_only_when_animated()
    {
        var p = Small();
        var o = AddShape(p, 0, "#808080", 0, 0);
        var e = Filter(o, "filter.noise", ("amount", 50));
        using var a0 = Render(p, 3);
        using var a1 = Render(p, 3);
        using var b = Render(p, 4);
        Assert.Equal(a0.Bytes, a1.Bytes);
        Assert.NotEqual(a0.Bytes, b.Bytes);
        Assert.Contains(Enumerable.Range(0, 20), x => a0.GetPixel(x, 5) != new SKColor(0x80, 0x80, 0x80));
        e.Values["animate"] = "0";
        using var c0 = Render(p, 3);
        using var c1 = Render(p, 4);
        Assert.Equal(c0.Bytes, c1.Bytes);
    }

    [Fact]
    public void Vignette_darkens_corners_but_not_the_center()
    {
        var p = Small();
        AddShape(p, 0, "#FFFFFF", 0, 0);
        var v = ObjectFactory.Create("filter.vignette", 1, 0, 60);
        v.Main.Tracks["strength"] = new AnimatedValue(100);
        p.Root.Objects.Add(v);
        using var bmp = Render(p);
        Assert.Equal(SKColors.White, bmp.GetPixel(160, 120));
        Assert.True(bmp.GetPixel(1, 1).Red < 60, "角が暗くない: " + bmp.GetPixel(1, 1));
    }

    [Fact]
    public void Diagonal_clip_and_gradient_affect_only_their_side()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 100);
        Filter(o, "filter.diagclip");
        using (var bmp = Render(p))
        {
            Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120 - 30));   // 上半分は消える
            Assert.Equal(SKColors.White, bmp.GetPixel(160, 120 + 30));
        }

        var q = Small();
        var g = AddShape(q, 0, "#FFFFFF", 100);
        var e = Filter(g, "filter.gradient", ("width", 100));
        e.Values["color1"] = "#FF0000"; e.Values["color2"] = "#0000FF";
        using var gb = Render(q);
        Assert.True(gb.GetPixel(160, 120 - 45).Red > 200, "上が色 1 でない");
        Assert.True(gb.GetPixel(160, 120 + 45).Blue > 200, "下が色 2 でない");
        Assert.Equal(SKColors.Black, gb.GetPixel(160, 120 - 70));      // 図形の外には塗らない
    }

    [Fact]
    public void Animation_effect_moves_rotates_and_blinks_without_keyframes()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 40);
        var e = ObjectFactory.AddFilter(o, "filter.anim");
        e.Values["type"] = "7";                                         // 横揺れ: 1 秒に 1 回
        e.Tracks["strength"] = new AnimatedValue(100);
        int quarter = (int)Math.Round(p.Settings.Fps.Value / 4);
        using (var bmp = Render(p, quarter))
        {
            Assert.Equal(SKColors.White, bmp.GetPixel(160 + 100, 120));
            Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120));
        }

        e.Values["type"] = "1";                                         // 点滅: 後半は消える
        using (var on = Render(p, 0)) Assert.Equal(SKColors.White, on.GetPixel(160, 120));
        using (var off = Render(p, (int)Math.Round(p.Settings.Fps.Value * 0.75))) Assert.Equal(SKColors.Black, off.GetPixel(160, 120));

        e.Values["type"] = "0";                                         // 振動: 同じフレームなら同じ位置
        e.Tracks["strength"] = new AnimatedValue(30);
        using var s0 = Render(p, 10);
        using var s1 = Render(p, 10);
        Assert.Equal(s0.Bytes, s1.Bytes);
    }

    [Fact]
    public void Gpu_output_of_new_effects_matches_cpu()
    {
        using var gpu = GpuContext.TryCreate(out var error);
        if (gpu == null)
        {
            Assert.False(GpuSelectionTests.GpuRequired, "GPU を作れません: " + error);
            return;
        }
        var p = Small(640, 360);
        AddShape(p, 0, "#1B2A41", 0, 0);
        var a = AddShape(p, 1, "#F2A900", 120, 1); a.Draw!.Tracks["x"] = new AnimatedValue(-200);
        Filter(a, "filter.glow", ("radius", 24));
        var b = AddShape(p, 2, "#3A6EA5", 120, 6);
        Filter(b, "filter.dirblur", ("length", 50), ("angle", 30));
        Filter(b, "filter.chromatic", ("amount", 5));
        var c = AddShape(p, 3, "#E53935", 120, 2); c.Draw!.Tracks["x"] = new AnimatedValue(200);
        Filter(c, "filter.levels", ("gamma", 1.6));
        Filter(c, "filter.diagclip", ("angle", 30), ("feather", 6));
        p.Root.Objects.Add(ObjectFactory.Create("filter.vignette", 4, 0, 60));
        using var cpuEngine = new RenderEngine();
        using var gpuEngine = new RenderEngine(gpu);
        using var x = Render(p, 5, cpuEngine);
        using var y = Render(p, 5, gpuEngine);
        long total = 0;
        var px = x.Bytes; var py = y.Bytes;
        for (int i = 0; i < px.Length; i++) total += Math.Abs(px[i] - py[i]);
        double mean = (double)total / px.Length;
        Assert.True(mean < 1.0, $"平均の差が大きい: {mean}");
    }

    [Fact]
    public void Preview_shortcut_for_large_blurs_looks_like_the_export()
    {
        foreach (var (id, key, value) in new[] { ("filter.dirblur", "length", 80.0), ("filter.radialblur", "amount", 40.0) })
        {
            var p = Small(640, 360);
            AddShape(p, 0, "#404040", 0, 0);
            AddShape(p, 1, "#FFFFFF", 160, 6);
            var f = ObjectFactory.Create(id, 2, 0, 60);
            f.Main.Tracks[key] = new AnimatedValue(value);
            p.Root.Objects.Add(f);
            using var exportEngine = new RenderEngine();
            using var previewEngine = new RenderEngine { Draft = true };
            using var x = Render(p, 0, exportEngine);
            using var y = Render(p, 0, previewEngine);
            long total = 0;
            var px = x.Bytes; var py = y.Bytes;
            for (int i = 0; i < px.Length; i++) total += Math.Abs(px[i] - py[i]);
            double mean = (double)total / px.Length;
            Console.WriteLine($"BENCH {id} preview-vs-export mean={mean:0.000}");
            Assert.True(mean < 1.5, $"{id}: プレビューと出力の差が大きい: {mean}");
        }
    }

    [Fact]
    public void Heavy_effects_render_full_hd_in_reasonable_time()
    {
        // 1080p の画面全体にかける。時間は表示するだけ (CI の速さはまちまちなので、極端に遅くないことだけ確かめる)
        foreach (var (id, key, value) in new[]
        {
            ("filter.glow", "radius", 40.0), ("filter.dirblur", "length", 120.0), ("filter.radialblur", "amount", 30.0),
            ("filter.sharpen", "radius", 3.0), ("filter.chromatic", "amount", 8.0), ("filter.raster", "amplitude", 30.0), ("filter.noise", "amount", 30.0),
        })
        {
            var p = Small(1920, 1080);
            AddShape(p, 0, "#808080", 0, 0);
            AddShape(p, 1, "#FFFFFF", 500, 6);
            var f = ObjectFactory.Create(id, 2, 0, 60);
            f.Main.Tracks[key] = new AnimatedValue(value);
            p.Root.Objects.Add(f);
            foreach (bool draft in new[] { false, true })
            {
                using var engine = new RenderEngine { Draft = draft };
                Render(p, 0, engine).Dispose();   // 初回 (JIT など) を除く
                var sw = Stopwatch.StartNew();
                for (int i = 1; i <= 3; i++) Render(p, i, engine).Dispose();
                double ms = sw.Elapsed.TotalMilliseconds / 3;
                Console.WriteLine($"BENCH {id} 1080p {(draft ? "preview" : "export")} {ms:0.0} ms/frame");
                Assert.True(ms < 5000, $"{id} が遅すぎる: {ms} ms");
            }
        }
    }
}
