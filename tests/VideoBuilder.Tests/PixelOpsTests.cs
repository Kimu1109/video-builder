using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>CPU 描画の画素の計算 (PixelOps) の、割り算を使わない書き方が、元の割り算と同じ結果になることを確かめる。</summary>
public class PixelOpsTests
{
    /// <summary>
    /// 乗算済みアルファを戻すときの割り算 (n / a) を、逆数の表の掛け算とシフトに置き換えた。
    /// 使うところの n は c * 255 + a / 2 (c は 0〜255) までなので、0〜65535 のすべての n と、1〜255 のすべての a で確かめる。
    /// </summary>
    [Fact]
    public void Dividing_by_alpha_with_reciprocals_matches_integer_division_for_every_input()
    {
        for (int a = 1; a <= 255; a++)
            for (int n = 0; n <= 65535; n++)
            {
                int expected = n / a;
                int actual = PixelOps.DivideByAlpha(n, a);
                if (actual != expected) Assert.Fail($"{n} / {a}: {actual} (正しくは {expected})");
            }
    }

    [Fact]
    public void Bands_are_finer_than_the_cores_but_never_thinner_than_eight_rows_or_more_than_the_rows()
    {
        Assert.Equal(64, PixelOps.BandCount(1080, 8));    // 1080p・8 コア: コア数の 8 倍
        Assert.Equal(135, PixelOps.BandCount(1080, 24));  // 24 コア: 8 行ずつ (1080 / 8)
        Assert.Equal(24, PixelOps.BandCount(100, 24));    // 行が少なくても、コアの数だけは分ける
        Assert.Equal(3, PixelOps.BandCount(3, 8));        // 行の数より多くはしない
        Assert.Equal(1, PixelOps.BandCount(1, 8));
    }

    [Fact]
    public void Unpremultiply_matches_the_rounded_division_for_every_color_and_alpha()
    {
        for (int a = 1; a <= 255; a++)
            for (int c = 0; c <= 255; c++)
                Assert.Equal((c * 255 + a / 2) / a, PixelOps.Unpremultiply(c, a));
    }
}
