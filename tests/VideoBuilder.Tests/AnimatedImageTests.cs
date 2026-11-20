using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>
/// アニメーション WebP・GIF を動画として読む (設計書 22.32)。
/// 素材 (Assets) は 32x24、0.1 秒ずつ 赤→緑→青→黄 の 4 フレーム。左半分だけ色があり、右半分は透明。
/// </summary>
public class AnimatedImageTests
{
    private static readonly (byte R, byte G, byte B)[] Colors = { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0) };

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static bool Ready()
    {
        if (FFmpegLoader.EnsureLoaded()) return true;
        Assert.NotEqual("1", Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG"));
        return false;
    }

    [Theory]
    [InlineData("anim.webp", true)]
    [InlineData("anim.gif", true)]
    [InlineData("still.webp", false)]
    public void Animated_images_are_detected_from_the_file_header(string name, bool animated)
    {
        Assert.Equal(animated, AnimatedImageFiles.IsAnimated(Asset(name)));
        Assert.Equal(animated ? EffectRegistry.Video : EffectRegistry.Image, ObjectFactory.DefinitionForFile(Asset(name)));
    }

    [Fact]
    public void Broken_or_short_files_are_not_animated()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vb-anim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var gif = Path.Combine(dir, "broken.gif");
            File.WriteAllBytes(gif, File.ReadAllBytes(Asset("anim.gif"))[..20]);
            var webp = Path.Combine(dir, "short.webp");
            File.WriteAllBytes(webp, new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            Assert.False(AnimatedImageFiles.IsAnimated(gif));
            Assert.False(AnimatedImageFiles.IsAnimated(webp));
            Assert.False(AnimatedImageFiles.IsAnimated(Path.Combine(dir, "missing.webp")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("anim.webp")]
    [InlineData("anim.gif")]
    public void Probe_reports_the_length_of_the_animation(string name)
    {
        if (!Ready()) return;
        var info = MediaInfo.Probe(Asset(name))!;
        Assert.True(info.HasVideo);
        Assert.True(info.IsAnimatedImage);
        Assert.Equal(32, info.Width);
        Assert.Equal(0.4, info.Duration, 2);
        Assert.False(ProxyStore.NeedsProxy(info, null));
    }

    [Theory]
    [InlineData("anim.webp")]
    [InlineData("anim.gif")]
    public void Frames_are_read_in_any_order_and_keep_transparency(string name)
    {
        if (!Ready()) return;
        using var r = new VideoReader(Asset(name));
        Assert.True(r.HasAlpha);
        Assert.Equal(0.4, r.Duration, 2);
        // 前へ戻る (シークできない WebP は開き直す) のも含めて、どの順で取り出しても同じフレームになる
        foreach (var i in new[] { 0, 2, 1, 3, 0, 3, 2 })
        {
            var f = r.GetFrame(i * 0.1 + 0.05)!;
            var (cr, cg, cb) = Colors[i];
            int left = (12 * 32 + 4) * 4, right = (12 * 32 + 28) * 4;
            Assert.Equal(new byte[] { cb, cg, cr, 255 }, f[left..(left + 4)]);
            Assert.Equal(0, f[right + 3]);   // 透明 (アルファを掛けてあるので色も 0)
            Assert.Equal(0, f[right] | f[right + 1] | f[right + 2]);
        }
    }

    [Theory]
    [InlineData("anim.webp")]
    [InlineData("anim.gif")]
    public void Rendered_animation_shows_the_layer_below_through_transparent_parts(string name)
    {
        if (!Ready()) return;
        var p = new Project();
        p.Settings.Width = 32; p.Settings.Height = 24; p.Settings.FrameRate = "10";
        p.Root.Objects.Add(ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 4));   // 白い四角 (画面より大きい)
        var video = ObjectFactory.Create(EffectRegistry.Video, 1, 0, 4);
        video.Main.Values["file"] = Asset(name);
        p.Root.Objects.Add(video);
        using var engine = new RenderEngine();
        using var bmp = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int i = 0; i < 4; i++)
        {
            engine.RenderToBitmap(p, p.Root, i, bmp);
            var (cr, cg, cb) = Colors[i];
            Assert.Equal(new SKColor(cr, cg, cb), bmp.GetPixel(4, 12));
            Assert.Equal(SKColors.White, bmp.GetPixel(28, 12));
        }
    }
}
