using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>追加したフィルタ (色・時間・合成・生成) と、動画の速度の変化・逆再生。</summary>
public class FilterTests
{
    private static Project Small() => new() { Settings = new ProjectSettings { Width = 320, Height = 240, FrameRate = "30" } };

    private static TimelineObject Shape(Project p, int layer, string color, int size = 100, int type = 2, int length = 60)
    {
        var o = ObjectFactory.Create(EffectRegistry.Shape, layer, 0, length);
        o.Main.Values["color"] = color; o.Main.Values["type"] = type.ToString();
        o.Main.Tracks["size"] = new AnimatedValue(size);
        p.Root.Objects.Add(o);
        return o;
    }

    private static SKBitmap Render(Project p, int frame = 0)
    {
        using var e = new RenderEngine();
        using var img = e.RenderImage(p, p.Root, frame);
        return SKBitmap.FromImage(img);
    }

    [Fact]
    public void Lut_curves_and_gradient_map_change_colors()
    {
        // 2×2×2 の LUT: 色を反転する
        var dir = Path.Combine(Path.GetTempPath(), "vb-lut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cube = Path.Combine(dir, "invert.cube");
            File.WriteAllText(cube, "TITLE \"invert\"\nLUT_3D_SIZE 2\n1 1 1\n0 1 1\n1 0 1\n0 0 1\n1 1 0\n0 1 0\n1 0 0\n0 0 0\n");
            var p = Small();
            var o = Shape(p, 0, "#FF0000");
            var lut = ObjectFactory.AddFilter(o, "filter.lut");
            lut.Values["file"] = cube;
            using (var bmp = Render(p)) Assert.Equal(new SKColor(0, 255, 255), bmp.GetPixel(160, 120));   // 赤 → シアン
            lut.Tracks["strength"] = new AnimatedValue(50);
            using (var bmp = Render(p)) { var c = bmp.GetPixel(160, 120); Assert.InRange(c.Red, 120, 135); Assert.InRange(c.Green, 120, 135); }
        }
        finally { Directory.Delete(dir, true); }

        // トーンカーブ: 中間を持ち上げると、灰色が明るくなる。曲線は 5 点を通る
        var lutTable = Effects.CurveLut(new float[] { 0, 64, 200, 192, 255 });
        Assert.Equal(200, lutTable[128]);
        Assert.Equal(0, lutTable[0]); Assert.Equal(255, lutTable[255]);
        var q = Small();
        var gray = Shape(q, 0, "#808080");
        var curves = ObjectFactory.AddFilter(gray, "filter.curves");
        curves.Tracks["p2"] = new AnimatedValue(200);
        using (var bmp = Render(q)) Assert.True(bmp.GetPixel(160, 120).Red > 180);

        // グラデーションマップ (2 色): 黒は暗い所の色に、白は明るい所の色に
        var r = Small();
        var black = Shape(r, 0, "#000000");
        var gm = ObjectFactory.AddFilter(black, "filter.gradmap");
        gm.Values["useMid"] = "0"; gm.Values["dark"] = "#203040"; gm.Values["light"] = "#FFFFFF";
        using (var bmp = Render(r)) Assert.Equal(new SKColor(0x20, 0x30, 0x40), bmp.GetPixel(160, 120));
    }

    [Fact]
    public void Strobe_holds_the_motion_and_trail_leaves_faint_copies()
    {
        TimelineObject Moving(Project p)
        {
            var o = Shape(p, 0, "#FFFFFF", 20);
            var x = o.Draw!.Tracks["x"];
            x.Mode = MovementMode.Linear; x.Values[0] = -120; x.Values[1] = 120;
            return o;
        }
        int Center(SKBitmap b) { int sum = 0, n = 0; for (int x = 0; x < 320; x++) if (b.GetPixel(x, 120).Red > 200) { sum += x; n++; } return n == 0 ? -1 : sum / n; }

        var p = Small();
        var o = Moving(p);
        ObjectFactory.AddFilter(o, "filter.strobe").Tracks["step"] = new AnimatedValue(6);
        using (var a = Render(p, 12)) using (var b = Render(p, 15)) using (var c = Render(p, 18))
        {
            Assert.Equal(Center(a), Center(b));        // 12〜17 フレームは同じ位置
            Assert.True(Center(c) > Center(a) + 10);   // 18 フレームで進む
        }

        var q = Small();
        var t = Moving(q);
        var trail = ObjectFactory.AddFilter(t, "filter.trail");
        trail.Tracks["count"] = new AnimatedValue(3); trail.Tracks["interval"] = new AnimatedValue(4);
        using var bmp = Render(q, 30);
        int now = Center(bmp);
        // 今の位置より左 (前の時刻の位置) に、薄い残像がある
        int faint = 0;
        for (int x = 0; x < now - 20; x++) { var px = bmp.GetPixel(x, 120); if (px.Red is > 20 and < 200) faint++; }
        Assert.True(faint > 10, "残像がない: " + faint);
    }

    [Fact]
    public void Track_matte_cuts_the_object_with_the_layer_above()
    {
        var p = Small();
        var matte = Shape(p, 0, "#FFFFFF", 80, 1);                 // 円 (レイヤー 1)
        p.Root.Layers[0] = new LayerInfo { Visible = false };      // マットのレイヤーは隠す
        var red = Shape(p, 1, "#FF0000", 0, 0);                    // 画面いっぱいの赤 (レイヤー 2)
        ObjectFactory.AddFilter(red, "filter.trackmatte");
        using (var bmp = Render(p))
        {
            Assert.Equal(SKColors.Red, bmp.GetPixel(160, 120));    // 円の中は赤
            Assert.Equal(SKColors.Black, bmp.GetPixel(20, 20));    // 円の外は切り抜かれる
        }
        red.FindEffect("filter.trackmatte")!.Values["mode"] = "1";   // 反転
        using (var bmp = Render(p))
        {
            Assert.Equal(SKColors.Black, bmp.GetPixel(160, 120));
            Assert.Equal(SKColors.Red, bmp.GetPixel(20, 20));
        }
    }

    [Fact]
    public void Video_speed_changes_are_accumulated_and_reverse_plays_backwards()
    {
        var p = Small();
        var v = ObjectFactory.Create(EffectRegistry.Video, 0, 0, 90);
        // 前半は 100%、後半は 0% (止まる): 中間点 45 フレーム
        v.AddKeyFrame(45);
        var speed = v.Main.Tracks["speed"];
        speed.Values = new List<double> { 100, 0, 0 };        // 区間 0 は 100%、区間 1 は 0% (区間ごとの値)
        double At(int f) => RenderEngine.SourceTime(p, v, v.Bounds, f);
        Assert.Equal(1.0, At(30), 2);
        Assert.InRange(At(45), 1.45, 1.51);                   // 境目の 1 フレームは台形で足すので、ほぼ 1.5 秒
        Assert.Equal(At(45), At(80), 6);                      // 止まったまま (以前は経過時間 × 0% で 0 に戻っていた)
        // 直線で 100% → 300% に上げると、なめらかに速くなる (飛ばない)
        speed.Values = new List<double> { 100, 300, 300 };
        speed.Mode = MovementMode.Linear;
        double prev = -1;
        for (int f = 0; f < 90; f++) { double t = At(f); Assert.True(t >= prev); prev = t; }
        // 逆再生: 終わりで開始位置、始めで終わりの位置
        var r = ObjectFactory.Create(EffectRegistry.Video, 0, 0, 60);
        r.Main.Values["reverse"] = "1";
        Assert.Equal(0, RenderEngine.SourceTime(p, r, r.Bounds, 60), 2);
        Assert.Equal(2, RenderEngine.SourceTime(p, r, r.Bounds, 0), 2);
    }

    [Fact]
    public void Particles_speed_lines_and_frames_draw_on_the_object()
    {
        int Bright(SKBitmap b) { int n = 0; for (int y = 0; y < 240; y += 2) for (int x = 0; x < 320; x += 2) if (b.GetPixel(x, y).Red > 128) n++; return n; }
        var p = Small();
        var bg = Shape(p, 0, "#000020", 0, 0);
        var snow = ObjectFactory.AddFilter(bg, "filter.particles");
        snow.Values["type"] = "1";
        using (var bmp = Render(p, 30)) Assert.True(Bright(bmp) > 20, "雪が降っていない");
        snow.Enabled = false;
        ObjectFactory.AddFilter(bg, "filter.speedlines");
        using (var bmp = Render(p, 0))
        {
            Assert.True(Bright(bmp) > 50, "集中線がない");
            Assert.True(bmp.GetPixel(160, 120).Red < 50, "中心は空いている");
        }

        // 枠 (ポラロイド): 絵の外に白い縁が付き、下が広い
        var q = Small();
        var photo = Shape(q, 0, "#2060C0", 100);
        var frame = ObjectFactory.AddFilter(photo, "filter.frame");
        frame.Values["type"] = "1"; frame.Values["shadow"] = "0"; frame.Tracks["width"] = new AnimatedValue(10);
        using var framed = Render(q);
        Assert.Equal(SKColors.White, framed.GetPixel(160, 120 - 55));   // 上の縁
        Assert.Equal(SKColors.White, framed.GetPixel(160, 120 + 80));   // 下の広い縁
    }
}
