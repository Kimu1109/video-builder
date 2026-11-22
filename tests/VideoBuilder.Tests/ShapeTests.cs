using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>図形オブジェクトの種類と見た目の設定。</summary>
public class ShapeTests
{
    private static SKColor At(SKImage img, int x, int y)
    {
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    private static SKImage Render(ShapeType type, ShapeStyle? style = null, float aspect = 0, float line = 4000, int size = 100)
        => Generators.RenderShape((int)type, size, aspect, line, SKColors.Red, 320, 240, style)!;

    [Fact]
    public void Type_list_matches_the_enum()
    {
        Assert.Equal(Enum.GetValues<ShapeType>().Length, EffectRegistry.ShapeTypes.Length);
        // 番号はプロジェクトファイルに残る。既存の番号は変えない
        Assert.Equal("星型", EffectRegistry.ShapeTypes[(int)ShapeType.Star]);
        Assert.Equal("矢印", EffectRegistry.ShapeTypes[(int)ShapeType.Arrow]);
    }

    [Fact]
    public void Every_type_renders_something()
    {
        foreach (var t in Enum.GetValues<ShapeType>())
        {
            using var img = Render(t, aspect: t is ShapeType.Arrow or ShapeType.DoubleArrow or ShapeType.Line ? -50 : 0);
            Assert.NotNull(img);
            using var bmp = SKBitmap.FromImage(img);
            Assert.Contains(bmp.Pixels, p => p.Alpha > 0);
        }
    }

    [Fact]
    public void Default_style_keeps_the_old_look()
    {
        // 既定の設定では、四角形は枠いっぱいに塗る
        using var rect = Render(ShapeType.Rectangle);
        Assert.Equal(SKColors.Red, At(rect, 0, 0));
        Assert.Equal(SKColors.Red, At(rect, 99, 99));
        // ライン幅を細くすると線になる
        using var frame = Render(ShapeType.Rectangle, line: 10);
        Assert.Equal(SKColors.Red, At(frame, 2, 50));
        Assert.Equal(0, At(frame, 50, 50).Alpha);
    }

    [Fact]
    public void Arrow_points_right_and_the_shaft_is_thinner_than_the_head()
    {
        using var img = Render(ShapeType.Arrow, size: 200, aspect: -50);   // 200 x 100
        Assert.Equal(200, img.Width); Assert.Equal(100, img.Height);
        Assert.Equal(SKColors.Red, At(img, 5, 50));        // 軸
        Assert.Equal(0, At(img, 5, 10).Alpha);             // 軸の外
        Assert.Equal(SKColors.Red, At(img, 125, 10));   // 矢じりの根元は広い
        Assert.Equal(SKColors.Red, At(img, 195, 50));      // 先端
        Assert.Equal(0, At(img, 195, 10).Alpha);

        using var thin = Render(ShapeType.Arrow, ShapeStyle.Default with { Thickness = 10 }, size: 200, aspect: -50);
        Assert.Equal(0, At(thin, 5, 40).Alpha);
        Assert.Equal(SKColors.Red, At(thin, 5, 50));
    }

    [Fact]
    public void Double_arrow_has_heads_on_both_sides()
    {
        using var img = Render(ShapeType.DoubleArrow, size: 200, aspect: -50);
        Assert.Equal(SKColors.Red, At(img, 3, 50));
        Assert.Equal(SKColors.Red, At(img, 196, 50));
        Assert.Equal(SKColors.Red, At(img, 70, 10));       // 左の矢じり
        Assert.Equal(0, At(img, 100, 10).Alpha);           // 真ん中は軸だけ
    }

    [Fact]
    public void Line_draws_end_caps()
    {
        var style = ShapeStyle.Default with { StartCap = 3, EndCap = 2 };   // 始点は円、終点は三角
        using var img = Render(ShapeType.Line, style, size: 300, aspect: -80);   // 300 x 60
        Assert.Equal(SKColors.Red, At(img, 30, 10));       // 円 (直径 60)
        Assert.Equal(SKColors.Red, At(img, 150, 30));      // 線
        Assert.Equal(0, At(img, 150, 10).Alpha);
        Assert.Equal(SKColors.Red, At(img, 296, 30));      // 三角の先
    }

    [Fact]
    public void Dashed_line_has_gaps()
    {
        var style = ShapeStyle.Default with { EndCap = 0, Thickness = 100, Dash = 1, DashLength = 20, DashGap = 20 };
        using var img = Render(ShapeType.Line, style, size: 200, aspect: -80);
        Assert.Equal(SKColors.Red, At(img, 10, 20));
        Assert.Equal(0, At(img, 30, 20).Alpha);
        Assert.Equal(SKColors.Red, At(img, 50, 20));
    }

    [Fact]
    public void Corner_rounds_the_rectangle()
    {
        using var img = Render(ShapeType.Rectangle, ShapeStyle.Default with { Corner = 30 });
        Assert.Equal(0, At(img, 1, 1).Alpha);
        Assert.Equal(SKColors.Red, At(img, 50, 1));
    }

    [Fact]
    public void Pie_and_hole()
    {
        // 中心角 90 度: 真上から時計回りに右上の 4 分の 1 だけ
        using var pie = Render(ShapeType.Pie, ShapeStyle.Default with { Sweep = 90 });
        Assert.Equal(SKColors.Red, At(pie, 75, 25));
        Assert.Equal(0, At(pie, 25, 25).Alpha);
        Assert.Equal(0, At(pie, 75, 75).Alpha);
        // 穴の開いた円 (リング)
        using var ring = Render(ShapeType.Circle, ShapeStyle.Default with { Hole = 50 });
        Assert.Equal(0, At(ring, 50, 50).Alpha);
        Assert.Equal(SKColors.Red, At(ring, 50, 10));
    }

    [Fact]
    public void Gradient_and_border()
    {
        var style = ShapeStyle.Default with { Fill = 1, Color2 = SKColors.Blue, GradientAngle = 0, Border = 10, BorderColor = SKColors.Lime };
        using var img = Render(ShapeType.Rectangle, style);
        Assert.Equal(SKColors.Lime, At(img, 2, 50));       // 縁
        var left = At(img, 15, 50); var right = At(img, 85, 50);
        Assert.True(left.Red > left.Blue, $"左は赤寄り: {left}");
        Assert.True(right.Blue > right.Red, $"右は青寄り: {right}");
    }

    [Fact]
    public void Shape_params_are_shown_only_for_the_types_that_use_them()
    {
        var o = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 30);
        var def = EffectRegistry.Get(EffectRegistry.Shape);
        Assert.False(def.Find("head")!.IsShown(o.Main));
        o.Main.Values["type"] = ((int)ShapeType.Arrow).ToString();
        Assert.True(def.Find("head")!.IsShown(o.Main));
        Assert.False(def.Find("color2")!.IsShown(o.Main));
        o.Main.Values["fill"] = "1";
        Assert.True(def.Find("color2")!.IsShown(o.Main));
        Assert.True(def.Find("color")!.IsShown(o.Main));
    }

    [Fact]
    public void Engine_uses_the_shape_style()
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 240;
        var o = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 30);
        o.Main.Values["color"] = "#FF0000";
        o.Main.Values["type"] = ((int)ShapeType.Circle).ToString();
        o.Main.Tracks["hole"] = new AnimatedValue(50);
        p.Root.Objects.Add(o);
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, 0);
        Assert.Equal(SKColors.Black, At(img, 160, 120));        // 穴
        Assert.Equal(new SKColor(255, 0, 0), At(img, 160, 120 - 80));
    }
}
