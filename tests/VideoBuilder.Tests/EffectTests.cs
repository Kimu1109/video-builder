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
        "filter.emit", "filter.diffuse", "filter.flash", "filter.light", "filter.bevel", "filter.edge", "filter.edgeblur",
        "filter.extcolor", "filter.hueconvert", "filter.colorkey", "filter.lumakey", "filter.lensblur", "filter.motionblur",
        "filter.ripple", "filter.polar", "filter.displace", "filter.mirror", "filter.kaleido", "filter.sphere", "filter.freeform",
        "filter.basic", "filter.shake", "filter.loop", "filter.imageblend", "filter.videoblend", "filter.deinterlace",
        "filter.shatter", "filter.scatter", "filter.circle", "filter.random",
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
            star.Draw!.Tracks["x"] = new AnimatedValue(0) { Mode = MovementMode.Linear };
            star.Draw!.Tracks["x"].Values[0] = -60; star.Draw!.Tracks["x"].Values[1] = 60;   // モーションブラー用に動かす
            Filter(star, id);
            // 選択肢のあるパラメータは、いちばん後ろの選択肢でも描けること
            var last = Filter(star, id);
            foreach (var d in EffectRegistry.Get(id).Params.Where(d => d.Kind == ParamKind.Choice)) last.Values[d.Key] = (d.Choices.Length - 1).ToString();
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
    public void Keys_remove_the_chosen_colors_and_brightness()
    {
        var p = Small();
        var o = AddShape(p, 0, "#20C040", 100);
        var e = Filter(o, "filter.colorkey"); e.Values["color"] = "#20C040";
        using (var bmp = Render(p)) Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120));

        var q = Small();
        var dark = AddShape(q, 0, "#202020", 100);
        Filter(dark, "filter.lumakey");                                       // 暗い部分を透明に
        using (var bmp = Render(q)) Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120));
        dark.Main.Values["color"] = "#E0E0E0";
        using (var bmp = Render(q)) Assert.Equal(new SKColor(0xE0, 0xE0, 0xE0), bmp.GetPixel(160, 120));
    }

    [Fact]
    public void Hue_conversion_changes_only_the_chosen_hue()
    {
        var p = Small();
        var red = AddShape(p, 0, "#FF0000", 80); red.Draw!.Tracks["x"] = new AnimatedValue(-80);
        var green = AddShape(p, 1, "#00FF00", 80); green.Draw!.Tracks["x"] = new AnimatedValue(80);
        p.Root.Objects.Add(ObjectFactory.Create("filter.hueconvert", 2, 0, 60));   // 赤 → 青
        using var bmp = Render(p);
        var r = bmp.GetPixel(80, 120);
        Assert.True(r.Blue > 200 && r.Red < 40, "赤が青にならない: " + r);
        Assert.Equal(new SKColor(0, 255, 0), bmp.GetPixel(240, 120));
    }

    [Fact]
    public void Mirror_draws_a_fading_reflection_below()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 60); o.Draw!.Tracks["y"] = new AnimatedValue(-40);
        Filter(o, "filter.mirror", ("alpha", 0), ("fade", 100));
        using var bmp = Render(p);
        var near = bmp.GetPixel(160, 120 - 40 + 32);    // 映り込みの上の方 (明るい)
        var far = bmp.GetPixel(160, 120 - 40 + 85);     // 下の方 (薄い)
        Assert.True(near.Red > 180, "映り込みがない: " + near);
        Assert.True(far.Red < near.Red - 60, $"離れても薄くならない: {near} {far}");
    }

    [Fact]
    public void Sphere_and_polar_and_freeform_change_the_shape()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 160);
        Filter(o, "filter.sphere", ("size", 100));
        using (var bmp = Render(p))
        {
            Assert.Equal(SKColors.Black, bmp.GetPixel(160 - 48, 120 - 48));   // 角は丸く欠ける
            Assert.True(bmp.GetPixel(160, 120).Red > 200);
            Assert.Equal(SKColors.Black, bmp.GetPixel(160 + 70, 120));       // 直径 100 の外
        }

        var q = Small();
        var f = AddShape(q, 0, "#FFFFFF", 100);
        Filter(f, "filter.freeform", ("x1", 40), ("x4", 40));                // 左の辺を右へ
        using (var bmp = Render(q))
        {
            Assert.Equal(SKColors.Black, bmp.GetPixel(160 - 45, 120));
            Assert.Equal(SKColors.White, bmp.GetPixel(160 + 45, 120));
        }
    }

    [Fact]
    public void Arrangements_draw_copies_and_shatter_scatters_pieces()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 20);
        Filter(o, "filter.circle", ("count", 4), ("radius", 80));
        using (var bmp = Render(p))
        {
            Assert.Equal(SKColors.White, bmp.GetPixel(160, 40));     // 上
            Assert.Equal(SKColors.White, bmp.GetPixel(240, 120));    // 右
            Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120));    // 中心には描かない
        }

        var q = Small();
        var s = AddShape(q, 0, "#FFFFFF", 100);
        s.Id = "scatter-test";   // 破片の動きはオブジェクトの ID から決まる乱数なので、毎回同じになるよう固定する
        Filter(s, "filter.scatter", ("start", 0.5), ("fade", 0), ("speed", 60), ("gravity", 0), ("size", 20));
        using (var before = Render(q, 10)) Assert.Equal(SKColors.White, before.GetPixel(160 - 45, 120 - 45));
        using var after = Render(q, 50);
        int white = 0;
        for (int y = 0; y < 240; y += 2) for (int x = 0; x < 320; x += 2) if (after.GetPixel(x, y).Red > 200) white++;
        Assert.InRange(white, 50, 2400);                              // 破片は残っている
        // 元の四角形の範囲は、半分以上が空いている (破片が外へ散った)
        int inside = 0;
        for (int y = 70; y < 170; y += 2) for (int x = 110; x < 210; x += 2) if (after.GetPixel(x, y).Red > 200) inside++;
        Assert.True(inside < 50 * 50 / 2, "散っていない: " + inside);
    }

    [Fact]
    public void Shatter_breaks_from_the_center_outward_and_pieces_fall()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 160);
        o.Id = "shatter-test";   // 乱数はオブジェクトの ID から決まるので固定する
        Filter(o, "filter.shatter", ("start", 0.5), ("speed", 40), ("delay", 300), ("size", 20));
        using (var before = Render(p, 10)) Assert.Equal(SKColors.White, before.GetPixel(160, 120));
        // 割れ始めてすぐ: 中心の近くは割れて動き出しているが、外側 (角) はまだそのまま
        using (var early = Render(p, 18))
            Assert.Equal(SKColors.White, early.GetPixel(160 - 78, 120 - 78));
        // しばらくすると重力で落ちて、上の方には何も残らない
        using var late = Render(p, 59);
        Assert.NotEqual(SKColors.White, late.GetPixel(160, 120 - 70));
        int white = 0;
        for (int y = 0; y < 240; y += 2) for (int x = 0; x < 320; x += 2) if (late.GetPixel(x, y).Red > 200) white++;
        Assert.True(white > 20, "破片が描かれていない");
    }

    [Fact]
    public void Bounce_in_comes_from_the_right_and_ninja_in_lands_in_place()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 30);
        var e = ObjectFactory.AddFilter(o, "filter.transition"); e.Values["type"] = "26"; e.Tracks["in"] = new AnimatedValue(1);
        int frames = (int)Math.Round(p.Settings.Fps.Value);
        // 入りの途中は右側にいる
        using (var mid = Render(p, frames / 4))
        {
            int rightmost = -1, leftmost = 999;
            for (int x = 0; x < 320; x++) for (int y = 0; y < 240; y += 2) if (mid.GetPixel(x, y).Red > 200) { rightmost = Math.Max(rightmost, x); leftmost = Math.Min(leftmost, x); }
            Assert.True(leftmost > 160, "右から入ってこない: " + leftmost);
        }
        using (var done = Render(p, frames + 2)) Assert.Equal(SKColors.White, done.GetPixel(160, 120));

        var q = Small();
        var n = AddShape(q, 0, "#FFFFFF", 30);
        n.Id = "ninja-test";     // 移動先はオブジェクトの ID から決まる乱数なので固定する
        var f = ObjectFactory.AddFilter(n, "filter.transition"); f.Values["type"] = "27"; f.Tracks["in"] = new AnimatedValue(1);
        // 移動の途中: 中心から離れた所にいる。入り終わると元の位置
        var positions = new HashSet<(int, int)>();
        for (int fr = 1; fr < frames; fr += 3)
        {
            using var bmp = Render(q, fr);
            long sx = 0, sy = 0, cnt = 0;
            for (int y = 0; y < 240; y += 2) for (int x = 0; x < 320; x += 2) if (bmp.GetPixel(x, y).Red > 100) { sx += x; sy += y; cnt++; }
            if (cnt > 0) positions.Add(((int)(sx / cnt) / 10, (int)(sy / cnt) / 10));
        }
        Assert.True(positions.Count >= 3, "あちこちへ移動していない: " + positions.Count);
        using (var done = Render(q, frames + 2)) Assert.Equal(SKColors.White, done.GetPixel(160, 120));
    }

    [Fact]
    public void Reel_spin_turns_with_blur_and_stops_upright()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 100);
        var e = ObjectFactory.AddFilter(o, "filter.anim");
        e.Values["type"] = "13"; e.Tracks["turns"] = new AnimatedValue(3);
        using (var start = Render(p, 0)) Assert.Equal(SKColors.White, start.GetPixel(160, 120 - 45));
        using (var end = Render(p, 59)) Assert.Equal(SKColors.White, end.GetPixel(160, 120 - 45));   // 最後は元の向き
        // 速く回っている途中は、縦にぼけて (縁がなだらか) いる
        using var mid = Render(p, 30);
        bool soft = false;
        for (int y = 0; y < 240; y++) { var c = mid.GetPixel(160, y); if (c.Red > 30 && c.Red < 220) soft = true; }
        Assert.True(soft, "回転中にぼけていない");
    }

    [Fact]
    public void Motion_blur_follows_the_objects_movement()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 40);
        var x = o.Draw!.Tracks["x"]; x.Mode = MovementMode.Linear; x.Values[0] = -300; x.Values[1] = 300;   // 1 フレーム約 10px 右へ
        Filter(o, "filter.motionblur", ("shutter", 300));
        using var bmp = Render(p, 30);
        float cx = 160 + (float)(-300 + 600.0 * 30 / 59);
        Assert.True(bmp.GetPixel((int)cx - 30, 120).Red > 20, "後ろに尾を引いていない");
        Assert.Equal(SKColors.Black, bmp.GetPixel((int)cx, 120 + 30));
    }

    [Fact]
    public void Edge_detection_and_lens_blur_and_bevel_work()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 100);
        Filter(o, "filter.edge");
        using (var bmp = Render(p))
        {
            Assert.True(bmp.GetPixel(160 - 50, 120).Red > 100, "縁が線にならない");
            Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120));   // 中は消える
        }

        var q = Small();
        Filter(AddShape(q, 0, "#FFFFFF", 100), "filter.lensblur", ("radius", 30));
        using (var bmp = Render(q))
        {
            Assert.InRange(bmp.GetPixel(160 + 50, 120).Red, 60, 200);   // 縁がぼける
            Assert.Equal(SKColors.White, bmp.GetPixel(160, 120));
        }

        var r = Small();
        AddShape(r, 0, "#808080", 0, 0);
        Filter(AddShape(r, 1, "#808080", 100), "filter.bevel", ("width", 16));
        using (var bmp = Render(r))
        {
            Assert.True(bmp.GetPixel(160 - 46, 120 - 46).Red > 0x90, "光の側が明るくない");
            Assert.True(bmp.GetPixel(160 + 46, 120 + 46).Red < 0x70, "影の側が暗くない");
        }
    }

    [Fact]
    public void Image_loop_tiles_and_image_blend_uses_the_file()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 40);
        Filter(o, "filter.loop", ("countX", 3), ("countY", 1));
        using (var bmp = Render(p))
        {
            Assert.Equal(SKColors.White, bmp.GetPixel(160 - 40, 120));
            Assert.Equal(SKColors.White, bmp.GetPixel(160 + 40, 120));
        }

        var file = Path.Combine(Path.GetTempPath(), $"vb-blend-{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new SKBitmap(16, 16)) { bmp.Erase(SKColors.Red); File.WriteAllBytes(file, bmp.Encode(SKEncodedImageFormat.Png, 100).ToArray()); }
            var q = Small();
            var s = AddShape(q, 0, "#FFFFFF", 60);
            var e = Filter(s, "filter.imageblend", ("scale", 1000));
            e.Values["file"] = file; e.Values["blend"] = "2";               // 乗算
            using var bmp2 = Render(q);
            Assert.Equal(new SKColor(255, 0, 0), bmp2.GetPixel(160, 120));
            Assert.Equal(SKColors.Black, bmp2.GetPixel(160 + 50, 120));      // 形の外には描かない
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Cube_faces_and_basic_effect_and_new_transitions_place_objects()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", 40);
        var e = Filter(o, "filter.basic", ("x", 80), ("aspect", 50));
        using (var bmp = Render(p))
        {
            Assert.Equal(SKColors.White, bmp.GetPixel(240, 120 - 15));
            Assert.Equal(SKColors.Black, bmp.GetPixel(240 + 15, 120));      // 横が半分になる
        }

        // 立方体の右の面は、横を向く (正面からは線のように細く見える)
        var c = Small();
        var face = AddShape(c, 0, "#FFFFFF", 80);
        var anim = ObjectFactory.AddFilter(face, "filter.anim");
        anim.Values["type"] = "12"; anim.Values["face"] = "2"; anim.Tracks["speed"] = new AnimatedValue(0);
        using (var bmp = Render(c)) Assert.Equal(SKColors.Black, bmp.GetPixel(160 + 40 - 20, 120));

        // 弾んで登場: 入りの途中は上にいて、入り終わると元の位置
        var t = Small();
        var d = AddShape(t, 0, "#FFFFFF", 40);
        var tr = ObjectFactory.AddFilter(d, "filter.transition"); tr.Values["type"] = "21"; tr.Tracks["in"] = new AnimatedValue(1);
        using (var bmp = Render(t, 3)) Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120));
        using (var bmp = Render(t, 40)) Assert.Equal(SKColors.White, bmp.GetPixel(160, 120));
    }

    [Fact]
    public void Heavy_effects_render_full_hd_in_reasonable_time()
    {
        // 1080p の画面全体にかける。時間は表示するだけ (CI の速さはまちまちなので、極端に遅くないことだけ確かめる)
        foreach (var (id, key, value) in new[]
        {
            ("filter.glow", "radius", 40.0), ("filter.dirblur", "length", 120.0), ("filter.radialblur", "amount", 30.0),
            ("filter.sharpen", "radius", 3.0), ("filter.chromatic", "amount", 8.0), ("filter.raster", "amplitude", 30.0), ("filter.noise", "amount", 30.0),
            ("filter.lensblur", "radius", 40.0), ("filter.ripple", "amplitude", 20.0), ("filter.kaleido", "count", 6.0), ("filter.edge", "strength", 100.0),
            ("filter.bevel", "width", 12.0), ("filter.light", "width", 20.0), ("filter.flash", "length", 60.0), ("filter.diffuse", "radius", 40.0),
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

    /// <summary>白い正方形 (40) に、アニメーション効果 type をかける。</summary>
    private static (Project P, Effect E, int Fps) Animated(int type, int size = 40)
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF", size, length: 600);
        var e = ObjectFactory.AddFilter(o, "filter.anim");
        e.Values["type"] = type.ToString();
        return (p, e, (int)Math.Round(p.Settings.Fps.Value));
    }

    [Fact]
    public void Animation_delay_count_phase_and_anchor_apply_to_every_type()
    {
        // 円運動 (半径 100): 遅延の間は元の位置、遅延のあとは円の上
        var (p, e, fps) = Animated(14);
        e.Tracks["strength"] = new AnimatedValue(100);
        e.Tracks["delay"] = new AnimatedValue(1);
        using (var bmp = Render(p, fps / 2)) Assert.Equal(SKColors.White, bmp.GetPixel(160, 120));
        using (var bmp = Render(p, fps)) Assert.Equal(SKColors.White, bmp.GetPixel(260, 120));   // 位相 0 は右端

        // 位相 90 度: 始めから円の下にいる
        e.Tracks["delay"] = new AnimatedValue(0);
        e.Tracks["phase"] = new AnimatedValue(90);
        using (var bmp = Render(p, 0)) Assert.Equal(SKColors.White, bmp.GetPixel(160, 220));

        // 繰り返す回数: 横揺れ 1 回 (1 秒) で止まり、元の位置に戻る
        var (q, f, qfps) = Animated(7);
        f.Tracks["strength"] = new AnimatedValue(100);
        f.Tracks["count"] = new AnimatedValue(1);
        using (var moving = Render(q, qfps / 4)) Assert.Equal(SKColors.Black, moving.GetPixel(160, 120));
        using (var stopped = Render(q, qfps * 5 / 4)) Assert.Equal(SKColors.White, stopped.GetPixel(160, 120));

        // 回転・拡大の中心: 正方形 (80) を左上の角を中心に 90 度 (時計回り) 回すと、角の左下へ回り込む
        var (r, g, rfps) = Animated(2, 80);
        g.Tracks["speed"] = new AnimatedValue(0.25);
        g.Tracks["ax"] = new AnimatedValue(-40);
        g.Tracks["ay"] = new AnimatedValue(-40);
        using (var bmp = Render(r, rfps))
        {
            Assert.Equal(SKColors.White, bmp.GetPixel(120 - 40, 80 + 40));   // 角の左下 (回り込んだ先)
            Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120));            // 元の中心はもう覆っていない
        }
    }

    [Fact]
    public void Jelly_keeps_the_anchor_point_fixed_while_stretching()
    {
        var (p, e, fps) = Animated(17, 80);
        e.Tracks["strength"] = new AnimatedValue(50);
        e.Tracks["ay"] = new AnimatedValue(40);                      // 下の端を中心にする
        using var bmp = Render(p, fps / 4);                          // いちばん横に伸びたところ (1.5 倍)
        int top = -1, bottom = -1, left = 999;
        for (int y = 0; y < 240; y++) if (bmp.GetPixel(160, y).Red > 128) { if (top < 0) top = y; bottom = y; }
        for (int x = 0; x < 320; x++) if (bmp.GetPixel(x, 150).Red > 128) { left = x; break; }
        Assert.InRange(bottom, 158, 160);                            // 下の端は動かない
        Assert.InRange(top, 104, 110);                               // 高さは 80 / 1.5 ≒ 53
        Assert.InRange(left, 99, 102);                               // 幅は 120
    }

    [Fact]
    public void Color_animations_change_the_colors_and_the_cursor_follows_the_text()
    {
        // 色相が回る: 赤が 1/3 回ると緑になる
        var p = Small();
        var o = AddShape(p, 0, "#FF0000", 80, length: 600);
        var e = Filter(o, "filter.anim", ("speed", 1.0 / 3));
        e.Values["type"] = "19";
        int fps = (int)Math.Round(p.Settings.Fps.Value);
        using (var bmp = Render(p, fps))
        {
            var c = bmp.GetPixel(160, 120);
            Assert.True(c.Green > 90 && c.Red < 30 && c.Blue < 30, "緑になっていない: " + c);   // 明るさを保つので暗めの緑
        }
        // 明るさの脈動: 半周期で白に近づく
        e.Values["type"] = "21";
        e.Tracks["strength"] = new AnimatedValue(100);
        e.Tracks["speed"] = new AnimatedValue(1);
        using (var bmp = Render(p, fps / 2)) Assert.True(bmp.GetPixel(160, 120).Green > 200, "明るくなっていない");

        // カーソル点滅: 文字の右に棒が出て、後半は消える
        var q = Small();
        var t = ObjectFactory.Create(EffectRegistry.Text, 0, 0, 600);
        t.Main.Values["text"] = "ABC";
        t.Main.Values["color"] = "#000000";       // 文字は黒 (背景と同じ) にして、棒だけを見る
        p = q;
        q.Root.Objects.Add(t);
        var c2 = Filter(t, "filter.anim");
        c2.Values["type"] = "28";
        c2.Values["color"] = "#FFFFFF";
        int White(SKBitmap b) { int n = 0; for (int y = 0; y < 240; y++) for (int x = 0; x < 320; x++) if (b.GetPixel(x, y).Red > 200) n++; return n; }
        using (var on = Render(q, 0))
        {
            int rightmost = -1;
            for (int y = 0; y < 240; y++) for (int x = 0; x < 320; x++) if (on.GetPixel(x, y).Red > 200) rightmost = Math.Max(rightmost, x);
            Assert.True(White(on) > 50, "カーソルが描かれていない");
            Assert.True(rightmost > 180, "カーソルが文字の右にない: " + rightmost);
        }
        using (var off = Render(q, fps * 3 / 4)) Assert.Equal(0, White(off));
    }
}
