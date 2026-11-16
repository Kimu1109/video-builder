using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

public class RenderTests
{
    private static Project Small()
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 240;
        return p;
    }

    private static TimelineObject AddShape(Project p, int layer, string color, int size = 100, int type = 2)
    {
        var o = ObjectFactory.Create(EffectRegistry.Shape, layer, 0, 60);
        o.Main.Values["color"] = color;
        o.Main.Values["type"] = type.ToString();
        o.Main.Tracks["size"] = new AnimatedValue(size);
        p.Root.Objects.Add(o);
        return o;
    }

    private static SKColor Pixel(Project p, int frame, int x, int y)
    {
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, frame);
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void Empty_scene_is_black()
    {
        Assert.Equal(SKColors.Black, Pixel(Small(), 0, 10, 10));
    }

    [Fact]
    public void Shape_is_drawn_at_screen_center_and_moves_with_x()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FF0000");
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 120));
        Assert.Equal(SKColors.Black, Pixel(p, 0, 20, 120));

        var x = o.Draw!.Tracks["x"];
        x.Mode = MovementMode.Linear; x.Values[0] = -100; x.Values[1] = 100;
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 60, 120));
        Assert.Equal(SKColors.Black, Pixel(p, 0, 160, 120));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 59, 260, 120));
    }

    [Fact]
    public void Upper_layers_are_drawn_first_and_hidden_layers_are_skipped()
    {
        var p = Small();
        AddShape(p, 0, "#FF0000");
        AddShape(p, 1, "#0000FF", 50);
        Assert.Equal(new SKColor(0, 0, 255), Pixel(p, 0, 160, 120));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 120, 120));
        p.Root.EditLayer(1).Visible = false;
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 120));
    }

    [Fact]
    public void Alpha_and_scale_are_applied()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF");
        o.Draw!.Tracks["alpha"] = new AnimatedValue(50);
        o.Draw!.Tracks["scale"] = new AnimatedValue(200);
        var c = Pixel(p, 0, 80, 120);   // 2 倍なので x=60..260 まで広がる
        Assert.InRange(c.Red, 120, 135);
    }

    [Fact]
    public void Color_correction_matrix_uses_normalized_offsets()
    {
        var p = Small();
        var o = AddShape(p, 0, "#808080");
        var fx = ObjectFactory.AddFilter(o, "filter.color");
        fx.Tracks["brightness"] = new AnimatedValue(120);   // +20% → 128 + 51
        var c = Pixel(p, 0, 160, 120);
        Assert.InRange(c.Red, 175, 183);
        fx.Tracks["brightness"] = new AnimatedValue(100);
        fx.Tracks["saturation"] = new AnimatedValue(0);
        o.Main.Values["color"] = "#FF0000";
        c = Pixel(p, 0, 160, 120);
        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Green, c.Blue);
    }

    [Fact]
    public void Blur_spreads_outside_the_shape_and_shadow_is_offset()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FFFFFF");
        Assert.Equal(SKColors.Black, Pixel(p, 0, 105, 120));
        var blur = ObjectFactory.AddFilter(o, "filter.blur");
        blur.Tracks["radius"] = new AnimatedValue(20);
        Assert.True(Pixel(p, 0, 105, 120).Red > 10);

        var q = Small();
        AddShape(q, 0, "#202020", 0, 0);            // 背景
        var s = AddShape(q, 1, "#FFFFFF");
        var sh = ObjectFactory.AddFilter(s, "filter.shadow");
        sh.Tracks["x"] = new AnimatedValue(20); sh.Tracks["y"] = new AnimatedValue(0);
        sh.Tracks["spread"] = new AnimatedValue(0); sh.Tracks["density"] = new AnimatedValue(100);
        Assert.Equal(new SKColor(0, 0, 0), Pixel(q, 0, 220, 120));          // 影
        Assert.Equal(new SKColor(255, 255, 255), Pixel(q, 0, 160, 120));    // 本体は中心のまま
        Assert.Equal(new SKColor(0x20, 0x20, 0x20), Pixel(q, 0, 100, 120)); // 左側には影がない
    }

    [Fact]
    public void Chroma_key_removes_the_key_color()
    {
        var p = Small();
        AddShape(p, 0, "#0000FF", 0, 0);
        var o = AddShape(p, 1, "#00FF00");
        ObjectFactory.AddFilter(o, "filter.chromakey");
        Assert.Equal(new SKColor(0, 0, 255), Pixel(p, 0, 160, 120));
    }

    [Fact]
    public void Filter_object_affects_everything_drawn_above_it()
    {
        var p = Small();
        AddShape(p, 0, "#FF0000");
        var f = ObjectFactory.Create("filter.mono", 1, 0, 60);
        p.Root.Objects.Add(f);
        var c = Pixel(p, 0, 160, 120);
        Assert.Equal(c.Red, c.Green);
        AddShape(p, 2, "#00FF00", 20);    // フィルタより下のレイヤーは影響を受けない
        Assert.Equal(new SKColor(0, 255, 0), Pixel(p, 0, 160, 120));
    }

    [Fact]
    public void Group_control_moves_lower_layers()
    {
        var p = Small();
        var g = ObjectFactory.Create(EffectRegistry.GroupControl, 0, 0, 60);
        g.Main.Tracks["x"] = new AnimatedValue(100);
        p.Root.Objects.Add(g);
        AddShape(p, 1, "#FF0000", 40);
        Assert.Equal(SKColors.Black, Pixel(p, 0, 160, 120));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 260, 120));
    }

    [Fact]
    public void Text_produces_visible_pixels_including_japanese()
    {
        using var img = Generators.RenderText("あA", "", 64, SKColors.White, false, false, 0, 0, 0)!;
        Assert.True(img.Width > 64);
        using var bmp = SKBitmap.FromImage(img);
        int lit = 0, litLeft = 0;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
            if (bmp.GetPixel(x, y).Alpha > 128) { lit++; if (x < bmp.Width / 2) litLeft++; }
        Assert.True(lit > 200);
        Assert.True(litLeft > 80);   // 日本語の字も描けている (豆腐や空白ではない)
    }

    [Fact]
    public void Scene_object_draws_another_scene_and_ignores_self_reference()
    {
        var p = Small();
        var sub = new Scene { Id = "s1", Name = "Sub" };
        var inner = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        inner.Main.Values["color"] = "#00FF00";
        sub.Objects.Add(inner);
        p.Scenes.Add(sub);
        var so = ObjectFactory.Create(EffectRegistry.SceneRef, 0, 0, 60);
        so.Main.Values["scene"] = "s1";
        p.Root.Objects.Add(so);
        Assert.Equal(new SKColor(0, 255, 0), Pixel(p, 0, 160, 120));
        so.Main.Values["scene"] = "root";   // 自分自身: 深さ制限で止まる
        Pixel(p, 0, 160, 120);
    }

    [Fact]
    public void Export_writes_video_with_audio_from_a_clip()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var clip = Path.Combine(dir, "clip.mp4");
            if (!MediaTests.RunFfmpeg($"-f lavfi -i color=c=red:size=320x240:rate=30:duration=2 -f lavfi -i sine=frequency=440:duration=2 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{clip}\"")) return;

            var p = Small();
            var v = ObjectFactory.Create(EffectRegistry.Video, 0, 0, 30);
            v.Main.Values["file"] = clip;
            var a = ObjectFactory.Create(EffectRegistry.Audio, 1, 0, 30);
            a.Main.Values["file"] = clip;
            var t = ObjectFactory.Create(EffectRegistry.Text, 2, 0, 30);
            p.Root.Objects.AddRange(new[] { v, a, t });

            // プレビューと同じ経路: 動画フレームが赤く描かれる
            var px = Pixel(p, 10, 20, 20);
            Assert.True(px.Red > 200 && px.Green < 60 && px.Blue < 60, px.ToString());

            // 音声ミキサー: 無音ではない
            using (var mixer = new AudioMixer())
            {
                var buf = new float[4800 * 2];
                mixer.Mix(p, p.Root, 4800, buf);
                Assert.Contains(buf, s => Math.Abs(s) > 0.01f);
                mixer.Mix(p, p.Root, 48000 * 5, buf);   // オブジェクトの外は無音
                Assert.All(buf, s => Assert.Equal(0f, s));
            }

            var outPath = Path.Combine(dir, "out.mp4");
            int reported = 0;
            Exporter.Run(new ExportRequest
            {
                Project = p, SceneId = "root", StartFrame = 0, EndFrame = 29,
                Settings = new ExportSettings { Path = outPath },
            }, new SyncProgress(x => reported = x.Done), CancellationToken.None);
            Assert.Equal(30, reported);
            var info = MediaInfo.Probe(outPath)!;
            Assert.True(info.HasVideo && info.HasAudio);
            Assert.Equal(320, info.Width);
            Assert.InRange(info.Duration, 0.9, 1.15);
            using var r = new VideoReader(outPath);
            var frame = r.GetFrame(0.5)!;
            Assert.True(frame[2] > 180 && frame[1] < 80, "出力した動画も赤い");   // BGRA の R

            var seq = Path.Combine(dir, "seq.png");
            Exporter.Run(new ExportRequest { Project = p, SceneId = "root", StartFrame = 0, EndFrame = 2, ImageSequence = true, Settings = new ExportSettings { Path = seq } }, null, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(dir, "seq_00002.png")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private sealed class SyncProgress(Action<(int Done, int Total)> onReport) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => onReport(value);
    }
}
