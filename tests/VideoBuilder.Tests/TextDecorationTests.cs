using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>文字を飾る機能: 背景と被る色を反転・背景の帯・内側シャドー・立体・縁だけ・縦書き・下線・取り消し線。</summary>
public class TextDecorationTests
{
    private static Project Small()
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 240;
        return p;
    }

    /// <summary>四角形 (図形) を置く。中心 (x, y)、一辺 size。</summary>
    private static TimelineObject Rect(Project p, int layer, string color, float x, float y, float size)
    {
        var o = ObjectFactory.Create(EffectRegistry.Shape, layer, 0, 60);
        o.Main.Values["type"] = "2";
        o.Main.Values["color"] = color;
        o.Main.Tracks["size"] = new AnimatedValue(size);
        o.Draw!.Tracks["x"] = new AnimatedValue(x);
        o.Draw!.Tracks["y"] = new AnimatedValue(y);
        p.Root.Objects.Add(o);
        return o;
    }

    private static SKBitmap Frame(Project p)
    {
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, 0);
        return SKBitmap.FromImage(img);
    }

    private static void Near(SKColor expected, SKColor actual, int tolerance = 6)
    {
        Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"{expected} を期待しましたが {actual} でした");
    }

    // ---------------- 背景と被る色を反転 ----------------

    private static unsafe SKColor InvertOne(SKColor text, SKColor back, Effects.OverlapInvertParams p)
    {
        byte* layer = stackalloc byte[4] { text.Blue, text.Green, text.Red, 255 };
        byte* bg = stackalloc byte[4] { back.Blue, back.Green, back.Red, 255 };
        Effects.OverlapInvertPixels((IntPtr)layer, (IntPtr)bg, 1, 1, 4, p);
        return new SKColor(layer[2], layer[1], layer[0]);
    }

    [Fact]
    public void Overlap_invert_changes_only_colors_close_to_the_background()
    {
        var p = new Effects.OverlapInvertParams(0.2f, 0.1f, 1, 0);
        Near(SKColors.Black, InvertOne(SKColors.White, SKColors.White, p));                          // 白の上の白 → 背景の反転 (黒)
        Near(SKColors.White, InvertOne(SKColors.White, SKColors.Black, p));                          // 違う色はそのまま
        Near(new SKColor(0, 255, 255), InvertOne(new SKColor(250, 10, 10), SKColors.Red, p));       // 赤の上の赤っぽい色 → 水色
        // なめらかさの間は混ぜる
        var mid = InvertOne(SKColors.White, new SKColor(190, 190, 190), p);                         // 距離 0.25 (0.2〜0.3 の間)
        Assert.InRange(mid.Red, 100, 230);
        // 強さ 0 なら何もしない。白か黒: 明るい背景には黒、暗い背景には白
        Near(SKColors.White, InvertOne(SKColors.White, SKColors.White, p with { Strength = 0 }));
        Near(SKColors.Black, InvertOne(new SKColor(230, 230, 100), new SKColor(240, 240, 110), p with { Mode = 1 }));
        Near(SKColors.White, InvertOne(new SKColor(30, 30, 60), new SKColor(20, 20, 50), p with { Mode = 1 }));
    }

    [Fact]
    public void Overlap_invert_reads_the_background_while_drawing()
    {
        var p = Small();
        // 背景: 全体が黒、左半分に白い四角
        Rect(p, 0, "#000000", 0, 0, 1000);
        Rect(p, 1, "#FFFFFF", -80, 0, 160);
        // 白い四角が白と黒の境目にまたがる → 白の上だけ黒くなる
        var box = Rect(p, 2, "#FFFFFF", 0, 0, 80);
        ObjectFactory.AddFilter(box, Effects.OverlapInvertId);
        using var bmp = Frame(p);
        Near(SKColors.Black, bmp.GetPixel(140, 120));   // 白い背景の上 (x < 160)
        Near(SKColors.White, bmp.GetPixel(180, 120));   // 黒い背景の上
        Near(SKColors.White, bmp.GetPixel(60, 120));    // 四角の外 (背景の白のまま)

        // 効果を無効にすれば、ふつうに白く描く
        box.Effects.First(e => e.DefinitionId == Effects.OverlapInvertId).Enabled = false;
        using var plain = Frame(p);
        Near(SKColors.White, plain.GetPixel(140, 120));
    }

    // ---------------- 背景の帯・内側シャドー・立体・縁だけ ----------------

    [Fact]
    public void Backplate_draws_a_rounded_band_behind()
    {
        var p = Small();
        var box = Rect(p, 0, "#FFFFFF", 0, 0, 40);
        var plate = ObjectFactory.AddFilter(box, "filter.backplate");
        plate.Values["color"] = "#FF0000";
        plate.Tracks["alpha"] = new AnimatedValue(0);
        plate.Tracks["padx"] = new AnimatedValue(30);
        plate.Tracks["pady"] = new AnimatedValue(10);
        plate.Tracks["radius"] = new AnimatedValue(0);
        using var bmp = Frame(p);
        Near(SKColors.White, bmp.GetPixel(160, 120));            // 中身は上に描く
        Near(SKColors.Red, bmp.GetPixel(160 + 20 + 25, 120));    // 横の余白 (20 + 30 の内側)
        Near(SKColors.Red, bmp.GetPixel(160, 120 - 20 - 6));     // 縦の余白 (20 + 10 の内側)
        Near(SKColors.Black, bmp.GetPixel(160, 120 - 20 - 15));  // 縦の余白の外
        Near(SKColors.Black, bmp.GetPixel(160 + 20 + 35, 120));  // 横の余白の外
    }

    [Fact]
    public void Inner_shadow_darkens_the_inside_edge_only()
    {
        var p = Small();
        var box = Rect(p, 0, "#FFFFFF", 0, 0, 80);
        var fx = ObjectFactory.AddFilter(box, "filter.innershadow");
        fx.Tracks["x"] = new AnimatedValue(8); fx.Tracks["y"] = new AnimatedValue(8);
        fx.Tracks["spread"] = new AnimatedValue(0); fx.Tracks["density"] = new AnimatedValue(100);
        using var bmp = Frame(p);
        Near(SKColors.Black, bmp.GetPixel(160 - 40 + 3, 120));   // 左の内側の縁 (影)
        Near(SKColors.White, bmp.GetPixel(160 + 40 - 3, 120));   // 右の内側の縁 (影なし)
        Near(SKColors.White, bmp.GetPixel(160, 120));
        Near(SKColors.Black, bmp.GetPixel(160 - 40 - 3, 120));   // 外 (はみ出さない)
    }

    [Fact]
    public void Extrude_pushes_the_shape_in_the_given_direction()
    {
        var p = Small();
        var box = Rect(p, 0, "#FFFFFF", 0, 0, 40);
        var fx = ObjectFactory.AddFilter(box, "filter.extrude");
        fx.Tracks["depth"] = new AnimatedValue(16);
        fx.Tracks["angle"] = new AnimatedValue(0);   // 右へ
        fx.Values["color"] = "#00FF00"; fx.Values["color2"] = "#00FF00";
        using var bmp = Frame(p);
        // 押し出したぶん、絵が右に広がる (中心は広がった絵の中心になるので、手前の絵は左へ 8 ずれる)
        int left = Enumerable.Range(0, 320).First(x => bmp.GetPixel(x, 120).Red > 128);
        int right = Enumerable.Range(0, 320).Last(x => bmp.GetPixel(x, 120).Green > 128);
        Assert.InRange(right - left, 40 + 16 - 3, 40 + 16 + 3);
        Near(new SKColor(0, 255, 0), bmp.GetPixel(right - 3, 120));   // 奥 (右端) は押し出しの色
        Near(SKColors.White, bmp.GetPixel(left + 3, 120));             // 手前は元の絵
    }

    [Fact]
    public void Border_can_leave_only_the_outline()
    {
        var p = Small();
        var box = Rect(p, 0, "#FFFFFF", 0, 0, 60);
        var border = ObjectFactory.AddFilter(box, "filter.border");
        border.Values["color"] = "#FF0000";
        border.Tracks["size"] = new AnimatedValue(6);
        using (var filled = Frame(p)) Near(SKColors.White, filled.GetPixel(160, 120));
        border.Values["only"] = "1";
        using var hollow = Frame(p);
        Near(SKColors.Black, hollow.GetPixel(160, 120));        // 中は抜ける
        Near(SKColors.Red, hollow.GetPixel(160 + 30 + 3, 120)); // 縁は残る
    }

    // ---------------- 縦書き・下線・取り消し線 ----------------

    private static SKRectI Bounds(SKImage img)
    {
        using var bmp = SKBitmap.FromImage(img);
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 64) { l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); }
        return new SKRectI(l, t, r + 1, b + 1);
    }

    private static SKImage Text(string text, Generators.TextDecor decor) =>
        Generators.RenderText(text, "", 48, SKColors.White, false, false, 1, 0, 0, null, decor)!;

    [Fact]
    public void Vertical_text_stacks_characters_and_rotates_long_vowels()
    {
        using var horizontal = Text("あいう", default);
        using var vertical = Text("あいう", new Generators.TextDecor(true, false, false));
        Assert.True(horizontal.Width > horizontal.Height * 2);
        Assert.True(vertical.Height > vertical.Width * 2);

        // 長音は縦書きでは縦向きになる
        var flat = Bounds(Text("ー", default));
        var standing = Bounds(Text("ー", new Generators.TextDecor(true, false, false)));
        Assert.True(flat.Width > flat.Height * 2, $"横書きの長音: {flat}");
        Assert.True(standing.Height > standing.Width * 2, $"縦書きの長音: {standing}");

        // 2 行目は左の列になる
        using var two = Text("あ\nい", new Generators.TextDecor(true, false, false));
        Assert.True(two.Width > two.Height);
    }

    [Fact]
    public void Underline_and_strike_add_lines_across_the_text()
    {
        var plain = Bounds(Text("あいう", default));
        var under = Bounds(Text("あいう", new Generators.TextDecor(false, true, false)));
        Assert.True(under.Bottom > plain.Bottom, "下線は文字の下に引く");
        Assert.True(under.Width >= plain.Width - 2);

        using var strikeImg = Text("あいう", new Generators.TextDecor(false, false, true));
        using var plainImg = Text("あいう", default);
        using var a = SKBitmap.FromImage(strikeImg);
        using var b = SKBitmap.FromImage(plainImg);
        // 取り消し線: 中ほどの行が、端から端まで埋まる
        int row = Enumerable.Range(0, a.Height).OrderByDescending(y => Enumerable.Range(0, a.Width).Count(x => a.GetPixel(x, y).Alpha > 128)).First();
        int filled = Enumerable.Range(plain.Left, plain.Width).Count(x => a.GetPixel(x, row).Alpha > 128);
        Assert.True(filled > plain.Width * 0.9, $"取り消し線の行 {row}: {filled}/{plain.Width}");
        Assert.InRange(row, plain.Top + plain.Height / 4, plain.Bottom - plain.Height / 4);

        // 縦書きの傍線は、文字の右に縦に引く
        var vplain = Bounds(Text("あいう", new Generators.TextDecor(true, false, false)));
        var vunder = Bounds(Text("あいう", new Generators.TextDecor(true, true, false)));
        Assert.True(vunder.Right > vplain.Right, $"{vplain} / {vunder}");
    }

    [Fact]
    public void Text_object_uses_the_new_parameters()
    {
        var p = Small();
        var t = ObjectFactory.Create(EffectRegistry.Text, 0, 0, 60);
        t.Main.Values["text"] = "あいう";
        t.Main.Tracks["size"] = new AnimatedValue(40);
        p.Root.Objects.Add(t);
        int Count(SKBitmap bmp, Func<int, int, bool> where) =>
            Enumerable.Range(0, bmp.Height).Sum(y => Enumerable.Range(0, bmp.Width).Count(x => where(x, y) && bmp.GetPixel(x, y).Red > 128));
        using var flat = Frame(p);
        int wide = Count(flat, (x, y) => Math.Abs(y - 120) > 40);   // 横書きなら、上下にはほとんどない
        t.Main.Values["vertical"] = "1";
        using var tall = Frame(p);
        int high = Count(tall, (x, y) => Math.Abs(y - 120) > 40);
        Assert.True(high > wide + 100, $"縦書き: {wide} → {high}");
    }

    [Fact]
    public void Gpu_output_of_text_decorations_matches_cpu()
    {
        using var gpu = GpuContext.TryCreate(out var error);
        if (gpu == null)
        {
            Assert.False(GpuSelectionTests.GpuRequired, "GPU を作れません: " + error);
            return;
        }
        var p = Small();
        Rect(p, 0, "#000000", 0, 0, 1000);
        Rect(p, 1, "#FFFFFF", -80, 0, 160);
        var inv = Rect(p, 2, "#FFFFFF", 0, -60, 80);
        ObjectFactory.AddFilter(inv, Effects.OverlapInvertId);
        var plate = Rect(p, 3, "#FFCC00", -100, 70, 30);
        ObjectFactory.AddFilter(plate, "filter.backplate").Values["color"] = "#3366FF";
        var inner = Rect(p, 4, "#FFFFFF", 0, 70, 50);
        ObjectFactory.AddFilter(inner, "filter.innershadow");
        var ext = Rect(p, 5, "#E53935", 100, 70, 40);
        ObjectFactory.AddFilter(ext, "filter.extrude");
        var hollow = Rect(p, 6, "#FFFFFF", 110, -60, 40);
        var border = ObjectFactory.AddFilter(hollow, "filter.border");
        border.Values["only"] = "1";
        border.Tracks["size"] = new AnimatedValue(4);

        using var cpuEngine = new RenderEngine();
        using var gpuEngine = new RenderEngine(gpu);
        using var x = cpuEngine.RenderImage(p, p.Root, 0);
        using var y = gpuEngine.RenderImage(p, p.Root, 0);
        using var bx = SKBitmap.FromImage(x);
        using var by = SKBitmap.FromImage(y);
        var px = bx.Bytes; var py = by.Bytes;
        long total = 0;
        for (int i = 0; i < px.Length; i++) total += Math.Abs(px[i] - py[i]);
        double mean = (double)total / px.Length;
        Assert.True(mean < 1.0, $"平均の差が大きい: {mean}");
        // 反転は GPU でも効いている (白い背景の上の白は黒になる)
        Near(SKColors.Black, by.GetPixel(140, 60));
    }
}
