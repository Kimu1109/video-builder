using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>テキストの文字の出し方・消え方 (単位・順番・カウントアップ・カラオケなど)。</summary>
public class TextAnimationTypeTests
{
    private static Project Small() => new() { Settings = new ProjectSettings { Width = 320, Height = 240, FrameRate = "30" } };

    private static (Project P, TimelineObject T) TextProject(string text, int anim, int length = 90)
    {
        var p = Small();
        var t = ObjectFactory.Create(EffectRegistry.Text, 0, 0, length);
        t.Main.Values["text"] = text;
        t.Main.Values["anim"] = anim.ToString();
        t.Main.Tracks["size"] = new AnimatedValue(40);
        t.Main.Tracks["interval"] = new AnimatedValue(0.1);
        t.Main.Tracks["duration"] = new AnimatedValue(0.3);
        p.Root.Objects.Add(t);
        return (p, t);
    }

    private static SKBitmap Render(Project p, int frame)
    {
        using var e = new RenderEngine();
        using var img = e.RenderImage(p, p.Root, frame);
        return SKBitmap.FromImage(img);
    }

    /// <summary>明るい (文字の) ピクセルの x の範囲。なければ (-1, -1)。</summary>
    private static (int Left, int Right) Extent(SKBitmap b, Func<SKColor, bool>? match = null)
    {
        match ??= c => c.Red > 128 || c.Green > 128;
        int l = -1, r = -1;
        for (int x = 0; x < b.Width; x++) for (int y = 0; y < b.Height; y += 2) if (match(b.GetPixel(x, y))) { if (l < 0) l = x; r = x; break; }
        return (l, r);
    }

    [Fact]
    public void Words_group_kanji_with_okurigana_and_split_latin_by_spaces()
    {
        // 今日は|良い|天気です
        var u = TextAnimation.UnitsOf("今日は良い天気です", 1);
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 2, 2, 2, 2 }, u);
        // 空白は前の単語に含める
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1 }, TextAnimation.UnitsOf("Hello World", 1));
        // 行: 改行は前の行に含める
        Assert.Equal(new[] { 0, 0, 0, 1, 1 }, TextAnimation.UnitsOf("ab\ncd", 2));
        // 文字: 改行も 1 つと数える (以前からの数え方)
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, TextAnimation.UnitsOf("ab\ncd", 0));
    }

    [Fact]
    public void Orders_rank_units_from_the_center_the_edges_or_at_random()
    {
        Assert.Equal(new[] { 2, 1, 0, 1, 2 }, TextAnimation.Ranks(5, 2, false, "x"));   // 中央から
        Assert.Equal(new[] { 0, 1, 2, 1, 0 }, TextAnimation.Ranks(5, 3, false, "x"));   // 両端から
        Assert.Equal(new[] { 0, 1, 2, 1, 0 }, TextAnimation.Ranks(5, 2, true, "x"));    // 中央から + 逆順 = 両端から
        var random = TextAnimation.Ranks(8, 1, false, "abc");
        Assert.Equal(Enumerable.Range(0, 8), random.OrderBy(r => r));                    // 0〜7 を 1 回ずつ
        Assert.NotEqual(Enumerable.Range(0, 8), random);
        Assert.Equal(random, TextAnimation.Ranks(8, 1, false, "abc"));                  // 同じテキストなら同じ順番
    }

    [Fact]
    public void Count_up_keeps_separators_and_decimals()
    {
        Assert.Equal("売上 6,172 円", TextAnimation.CountText("売上 12,345 円", 0.5));
        Assert.Equal("1.25 と 50", TextAnimation.CountText("2.50 と 100", 0.5));
        Assert.Equal("12,345", TextAnimation.CountText("12,345", 1));

        var (p, t) = TextProject("100", 17);
        t.Main.Tracks["countTime"] = new AnimatedValue(1);
        using var start = Render(p, 0);
        using var done = Render(p, 40);
        // 始めは「0」1 文字、数え終わると「100」の 3 文字の幅
        var (l0, r0) = Extent(start);
        var (l1, r1) = Extent(done);
        Assert.True(r1 - l1 > (r0 - l0) * 2, $"幅が増えていない: {r0 - l0} → {r1 - l1}");
    }

    [Fact]
    public void Exit_finishes_exactly_at_the_end_of_the_object()
    {
        var (p, t) = TextProject("ABC", 0, length: 60);
        t.Main.Values["exit"] = "1";   // フェードアウト
        using (var before = Render(p, 20)) Assert.True(Extent(before).Left >= 0, "消える前に見えていない");
        // 最後の文字は、オブジェクトの終わり (59 フレーム目) でちょうど消える
        using (var last = Render(p, 59)) Assert.Equal((-1, -1), Extent(last, c => c.Red > 40));
        // 消え始めは先頭の文字から
        using var mid = Render(p, 52);
        var dim = Extent(mid, c => c.Red > 200);
        using var full = Render(p, 20);
        Assert.True(dim.Left > Extent(full, c => c.Red > 200).Left + 10, "先頭の文字から消えていない");
    }

    [Fact]
    public void Karaoke_fills_with_the_second_color_and_slide_in_comes_from_the_side()
    {
        var (p, t) = TextProject("ABCD", 15);
        t.Main.Values["color2"] = "#FF0000";
        static bool Red(SKColor c) => c.Red > 200 && c.Green < 80;
        using (var start = Render(p, 0)) Assert.Equal((-1, -1), Extent(start, Red));          // 始めは塗られていない
        using (var mid = Render(p, 9))                                                         // 先頭の文字は塗られ、最後はまだ
        {
            var red = Extent(mid, Red);
            var all = Extent(mid);
            Assert.True(red.Left >= 0 && red.Right < all.Right - 5, $"途中まで塗られていない: {red} / {all}");
        }
        using (var done = Render(p, 60)) Assert.Equal(Extent(done), Extent(done, Red));       // 全部塗られる

        // 1 文字ずつ右から: 出始めの文字は、出終わった位置より右にある
        var (q, s) = TextProject("A", 7);
        using var early = Render(q, 4);
        using var settled = Render(q, 30);
        Assert.True(Extent(early, c => c.Red > 40).Left > Extent(settled).Left + 5, "右から入ってこない");
    }

    [Fact]
    public void Decode_shows_other_letters_first_and_outline_draws_before_filling()
    {
        var (p, _) = TextProject("WWWW", 13);
        using var scrambled = Render(p, 3);
        using var done = Render(p, 60);
        int diff = 0;
        for (int y = 0; y < 240; y++) for (int x = 0; x < 320; x++) if ((scrambled.GetPixel(x, y).Red > 128) != (done.GetPixel(x, y).Red > 128)) diff++;
        Assert.True(diff > 200, "文字化けしていない: " + diff);

        // 線で描いてから塗る: 途中は輪郭だけ (塗った後より白いピクセルが少ない)
        var (q, _) = TextProject("■", 16);
        int White(SKBitmap b) { int n = 0; for (int y = 0; y < 240; y++) for (int x = 0; x < 320; x++) if (b.GetPixel(x, y).Red > 200) n++; return n; }
        using var outline = Render(q, 5);
        using var filled = Render(q, 30);
        Assert.True(White(outline) > 0 && White(outline) < White(filled) / 2, $"{White(outline)} / {White(filled)}");
    }
}
