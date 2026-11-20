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
    public void Z_and_axis_rotation_give_perspective()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FF0000");            // 100px → x = 110..210
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 115, 120));
        o.Draw!.Tracks["z"] = new AnimatedValue(1024);  // 距離が 2 倍 → 半分の大きさ (135..185)
        Assert.Equal(SKColors.Black, Pixel(p, 0, 125, 120));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 140, 120));

        o.Draw!.Tracks["z"] = new AnimatedValue(0);
        o.Draw!.Tracks["ry"] = new AnimatedValue(60);   // Y 軸回転 60° → 幅がおよそ半分
        Assert.Equal(SKColors.Black, Pixel(p, 0, 115, 120));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 120));
        o.Draw!.Tracks["ry"] = new AnimatedValue(90);   // 真横: 何も見えない (例外にもならない)
        Assert.Equal(SKColors.Black, Pixel(p, 0, 160, 120));
    }

    [Fact]
    public void Camera_control_changes_the_view_of_lower_layers_only()
    {
        var p = Small();
        AddShape(p, 0, "#0000FF", 20).Draw!.Tracks["x"] = new AnimatedValue(-120);   // カメラより上: 影響なし
        var cam = ObjectFactory.Create(EffectRegistry.CameraControl, 1, 0, 60);
        p.Root.Objects.Add(cam);
        AddShape(p, 2, "#FF0000", 40);
        // 既定のカメラは「カメラなし」と同じ絵
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 120));

        // カメラと目標を右へ 100 → 被写体は左へ 100
        cam.Main.Tracks["x"] = new AnimatedValue(100); cam.Main.Tracks["tx"] = new AnimatedValue(100);
        Assert.Equal(SKColors.Black, Pixel(p, 0, 160, 120));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 60, 120));
        Assert.Equal(new SKColor(0, 0, 255), Pixel(p, 0, 34, 120));

        // カメラを半分の距離まで近づける → 2 倍に見える
        cam.Main.Tracks["x"] = new AnimatedValue(0); cam.Main.Tracks["tx"] = new AnimatedValue(0);
        cam.Main.Tracks["z"] = new AnimatedValue(-512);
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 125, 120));
        // 被写体の後ろに回る → 映らない
        cam.Main.Tracks["z"] = new AnimatedValue(500); cam.Main.Tracks["tz"] = new AnimatedValue(1000);
        Assert.Equal(SKColors.Black, Pixel(p, 0, 160, 120));
    }

    [Fact]
    public void Camera_control_draws_objects_from_far_to_near()
    {
        // 上のレイヤーの赤を奥 (z = 300)、下のレイヤーの緑を手前 (z = -300) に置く
        var p = Small();
        p.Root.Objects.Add(ObjectFactory.Create(EffectRegistry.CameraControl, 0, 0, 60));
        AddShape(p, 1, "#00FF00", 60).Draw!.Tracks["z"] = new AnimatedValue(-300);
        AddShape(p, 2, "#FF0000", 60).Draw!.Tracks["z"] = new AnimatedValue(300);
        // 奥行きで並べ替える (既定): 手前の緑が見える
        Assert.Equal(new SKColor(0, 255, 0), Pixel(p, 0, 160, 120));
        // 並べ替えない: レイヤー順なので、あとに描く赤が上に来る
        p.Root.Objects[0].Main.Values["zsort"] = "0";
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 120));

        // カメラの対象外のオブジェクト・調整レイヤーをはさんでも、順番は崩れない
        var q = Small();
        q.Root.Objects.Add(ObjectFactory.Create(EffectRegistry.CameraControl, 0, 0, 60));
        AddShape(q, 1, "#FF0000", 60).Draw!.Tracks["z"] = new AnimatedValue(-300);   // 手前
        var mono = ObjectFactory.Create("filter.mono", 2, 0, 60);                              // ここまでを単色化 (白)
        q.Root.Objects.Add(mono);
        AddShape(q, 3, "#0000FF", 20).Draw!.Tracks["z"] = new AnimatedValue(300);    // 奥だが、単色化のあとに描く
        // 赤は単色化の前に描き終えているので灰色に、青は単色化のあとに描くので、奥にあっても上に乗る
        Assert.Equal(new SKColor(0, 0, 255), Pixel(q, 0, 160, 120));
        var gray = Pixel(q, 0, 190, 120);
        Assert.True(gray.Red > 0 && gray.Red == gray.Green && gray.Green == gray.Blue, gray.ToString());
    }

    [Fact]
    public void Depth_of_field_blurs_objects_away_from_the_focus()
    {
        const float DefaultDistance = RenderEngine.DefaultCameraDistance;
        // 白い四角の縁の明るさで、ぼけたかどうかを見る
        SKColor EdgeAt(float z, float dof, float focus = 0)
        {
            var p = Small();
            var cam = ObjectFactory.Create(EffectRegistry.CameraControl, 0, 0, 60);
            cam.Main.Tracks["dof"] = new AnimatedValue(dof);
            cam.Main.Tracks["focus"] = new AnimatedValue(focus);
            p.Root.Objects.Add(cam);
            var sq = AddShape(p, 1, "#FFFFFF", 100);
            sq.Draw!.Tracks["z"] = new AnimatedValue(z);
            // 四角の右端の少し外側 (中心 160 + 半分の大きさ x 拡大率 + 3)
            float half = 50 * DefaultDistance / (DefaultDistance + z);
            return Pixel(p, 0, (int)(160 + half + 3), 120);
        }
        // ぼけなし: 外側は黒
        Assert.Equal(SKColors.Black, EdgeAt(1000, 0));
        // ピント (目標 = z 0) から離れた奥の四角はぼける
        Assert.True(EdgeAt(1000, 20).Red > 20);
        // ピントが合っている位置ならぼけない
        Assert.Equal(SKColors.Black, EdgeAt(0, 20));
        // ピントの距離を奥に合わせると、奥の四角はぼけない
        Assert.Equal(SKColors.Black, EdgeAt(1000, 20, DefaultDistance + 1000));
    }

    [Fact]
    public void Old_projects_get_newly_added_parameters_on_load()
    {
        var p = Small();
        var o = AddShape(p, 0, "#FF0000");
        o.AddKeyFrame(30);
        o.Draw!.Tracks.Remove("rx");
        var q = ProjectSerializer.FromJson(ProjectSerializer.ToJson(p));
        Assert.Equal(3, q.Root.Objects[0].Draw!.Tracks["rx"].Values.Count);
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

/// <summary>GPU 描画が CPU 描画と同じ絵になることを確かめる。GPU コンテキストを作れない環境では何もしない。</summary>
public class GpuRenderTests
{
    private static Project RichProject()
    {
        var p = new Project();
        p.Settings.Width = 640; p.Settings.Height = 360;
        TimelineObject Shape(int layer, string color, int type, int size, double x = 0, double y = 0)
        {
            var o = ObjectFactory.Create(EffectRegistry.Shape, layer, 0, 60);
            o.Main.Values["color"] = color; o.Main.Values["type"] = type.ToString();
            o.Main.Tracks["size"] = new AnimatedValue(size);
            o.Draw!.Tracks["x"] = new AnimatedValue(x); o.Draw!.Tracks["y"] = new AnimatedValue(y);
            p.Root.Objects.Add(o);
            return o;
        }
        Shape(0, "#1B2A41", 0, 0);
        var circle = Shape(1, "#F2A900", 1, 160, -150, -40);
        ObjectFactory.AddFilter(circle, "filter.shadow");
        var blurred = Shape(2, "#FFFFFF", 2, 80, 150, -60);
        ObjectFactory.AddFilter(blurred, "filter.blur").Tracks["radius"] = new AnimatedValue(12);
        blurred.Draw!.Tracks["rotation"] = new AnimatedValue(30);
        var keyed = Shape(3, "#00FF00", 2, 120, 0, 90);            // クロマキーで消える
        ObjectFactory.AddFilter(keyed, "filter.chromakey");
        var star = Shape(4, "#E53935", 6, 120, 200, 90);
        ObjectFactory.AddFilter(star, "filter.border").Tracks["size"] = new AnimatedValue(4);
        ObjectFactory.AddFilter(star, "filter.color").Tracks["hue"] = new AnimatedValue(40);
        var mosaic = Shape(5, "#3A6EA5", 5, 100, -220, 100);
        ObjectFactory.AddFilter(mosaic, "filter.mosaic");
        mosaic.Draw!.Values["blend"] = "1";                          // 加算
        var sub = Shape(6, "#404040", 2, 60, -40, -120);
        sub.Draw!.Values["blend"] = "2";                             // 減算
        var text = ObjectFactory.Create(EffectRegistry.Text, 7, 0, 60);
        text.Main.Values["text"] = "GPU テスト";
        ObjectFactory.AddFilter(text, "filter.border");
        p.Root.Objects.Add(text);
        var filter = ObjectFactory.Create("filter.color", 8, 0, 60);
        filter.Main.Tracks["contrast"] = new AnimatedValue(120);
        p.Root.Objects.Add(filter);
        return p;
    }

    [Fact]
    public void Gpu_output_matches_cpu_output()
    {
        using var gpu = GpuContext.TryCreate(out var error);
        if (gpu == null) { Console.WriteLine("BENCH gpu unavailable: " + error); return; }
        Console.WriteLine($"BENCH gpu renderer={gpu.Renderer} vendor={gpu.Vendor} software={gpu.IsSoftware}");
        var p = RichProject();
        using var cpuEngine = new RenderEngine();
        using var gpuEngine = new RenderEngine(gpu);
        using var a = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var b = new SKBitmap(a.Info);
        cpuEngine.RenderToBitmap(p, p.Root, 10, a);
        gpuEngine.RenderToBitmap(p, p.Root, 10, b);

        long total = 0; int max = 0, far = 0;
        var pa = a.Bytes; var pb = b.Bytes;
        for (int i = 0; i < pa.Length; i++)
        {
            int d = Math.Abs(pa[i] - pb[i]);
            total += d; max = Math.Max(max, d);
            if (d > 24) far++;
        }
        double mean = (double)total / pa.Length;
        Console.WriteLine($"BENCH gpu-vs-cpu mean={mean:0.000} max={max} far={far}");
        // 輪郭のアンチエイリアスとぼかしの計算方法が少し違うので、わずかな差は許す
        Assert.True(mean < 0.6, $"平均の差が大きい: {mean}");
        Assert.True(far < pa.Length / 400, $"大きく違うピクセルが多い: {far}");

        // 代表点: GPU 側でも背景・図形・クロマキーが正しい
        Assert.Equal(new SKColor(0xF2, 0xA9, 0x00).Red, b.GetPixel(320 - 150, 180 - 40).Red, 40.0);
        var keyedPixel = b.GetPixel(320, 180 + 110);
        Assert.True(keyedPixel.Green < 100, "クロマキーで緑が消えていない: " + keyedPixel);
        Assert.Equal(gpuEngine.LastQuads.Count, cpuEngine.LastQuads.Count);
    }

    [Fact]
    public void Gpu_engine_can_render_repeatedly_at_different_sizes_and_export()
    {
        using var gpu = GpuContext.TryCreate(out _);
        if (gpu == null) return;
        var p = RichProject();
        using (var engine = new RenderEngine(gpu))
        {
            foreach (var (w, h) in new[] { (640, 360), (320, 180), (640, 360) })
            {
                using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                for (int f = 0; f < 5; f++) engine.RenderToBitmap(p, p.Root, f, bmp);
                Assert.NotEqual(SKColors.Black, bmp.GetPixel(2, 2));       // 背景色が入っている
            }
        }
        if (!FFmpegLoader.EnsureLoaded()) return;
        var path = Path.Combine(Path.GetTempPath(), $"vb-gpu-{Guid.NewGuid():N}.mp4");
        try
        {
            // 出力は別スレッドで自分用の GPU コンテキストを作る
            Task.Run(() => Exporter.Run(new ExportRequest
            {
                Project = p, SceneId = "root", StartFrame = 0, EndFrame = 9, UseGpu = true,
                Settings = new ExportSettings { Path = path, AudioCodec = "" },
            }, null, CancellationToken.None)).Wait();
            var info = MediaInfo.Probe(path)!;
            Assert.True(info.HasVideo);
            using var r = new VideoReader(path);
            var px = r.GetFrame(0.1)!;
            Assert.True(px[0] > 40 && px[2] < 60, "出力した動画の背景色が違う");   // #1B2A41 にコントラスト補正: 青が強い
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Report_render_speed()
    {
        var p = RichProject();
        p.Settings.Width = 1920; p.Settings.Height = 1080;
        double Measure(RenderEngine e)
        {
            using var bmp = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
            e.RenderToBitmap(p, p.Root, 0, bmp);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 1; f <= 10; f++) e.RenderToBitmap(p, p.Root, f, bmp);
            return sw.Elapsed.TotalMilliseconds / 10;
        }
        using (var cpu = new RenderEngine()) Console.WriteLine($"BENCH 1080p cpu: {Measure(cpu):0.0} ms/frame");
        using var gpu = GpuContext.TryCreate(out _);
        if (gpu == null) return;
        using var engine = new RenderEngine(gpu);
        Console.WriteLine($"BENCH 1080p gpu ({gpu.Renderer}): {Measure(engine):0.0} ms/frame");
    }
}

public class ScriptAndPluginTests
{
    private static Project Small()
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 240;
        return p;
    }

    private static SKColor Pixel(Project p, int frame, int x, int y)
    {
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, frame);
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    private static (Project, TimelineObject, Effect) WithScript(string code)
    {
        var p = Small();
        var o = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        o.Main.Values["color"] = "#FF0000";
        o.Main.Tracks["size"] = new AnimatedValue(40);
        var fx = ObjectFactory.AddFilter(o, "filter.script");
        fx.Values["code"] = code;
        p.Root.Objects.Add(o);
        return (p, o, fx);
    }

    [Fact]
    public void Lua_script_moves_the_object_using_time()
    {
        var (p, o, _) = WithScript("obj.ox = obj.frame * 2\nobj.oy = -50");
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 70));
        Assert.Equal(SKColors.Black, Pixel(p, 0, 160, 120));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 30, 220, 70));   // 30 フレーム目: 右へ 60
        Assert.Null(ScriptEffect.ErrorOf(o.Id));
    }

    [Fact]
    public void Lua_script_can_scale_fade_and_use_math()
    {
        var (p, _, _) = WithScript("obj.zoom = 2\nobj.alpha = math.max(0, 0.5)");
        var c = Pixel(p, 0, 125, 120);   // 2 倍 (120..200) で半透明
        Assert.InRange(c.Red, 120, 135);
    }

    [Fact]
    public void Lua_errors_and_endless_loops_are_reported_without_stopping_the_render()
    {
        var (p, o, fx) = WithScript("obj.ox = = 1");
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 120));    // スクリプトなしと同じ絵になる
        Assert.NotNull(ScriptEffect.ErrorOf(o.Id));

        fx.Values["code"] = "obj.ox = nothing.here";
        Pixel(p, 0, 160, 120);
        Assert.Contains("nil", ScriptEffect.ErrorOf(o.Id));

        fx.Values["code"] = "while true do end";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 120));
        Assert.True(sw.Elapsed.TotalSeconds < 10, "無限ループが止まらない");
        Assert.Contains("中断", ScriptEffect.ErrorOf(o.Id));

        fx.Values["code"] = "obj.ox = 0";
        Pixel(p, 0, 160, 120);
        Assert.Null(ScriptEffect.ErrorOf(o.Id));                         // 直せばエラーは消える
    }

    [Fact]
    public void Lua_sandbox_has_no_file_or_os_access()
    {
        var (p, o, _) = WithScript("local f = io.open('/etc/passwd')\nobj.ox = 100");
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 120));    // io がないのでエラーになり、動かない
        Assert.NotNull(ScriptEffect.ErrorOf(o.Id));
        var (p2, o2, _) = WithScript("os.execute('true')");
        Pixel(p2, 0, 160, 120);
        Assert.NotNull(ScriptEffect.ErrorOf(o2.Id));
    }

    private static string? SamplePluginDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VideoBuilder.sln"))) dir = dir.Parent;
        if (dir == null) return null;
        var bin = Path.Combine(dir.FullName, "samples", "SamplePlugin", "bin");
        if (!Directory.Exists(bin)) return null;
        return Directory.EnumerateFiles(bin, "SamplePlugin.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).Select(Path.GetDirectoryName).FirstOrDefault();
    }

    [Fact]
    public void Sample_plugin_loads_and_its_filter_source_and_interpolator_work()
    {
        var dir = SamplePluginDir();
        Assert.NotNull(dir);   // ソリューション全体をビルドしていれば必ずある
        if (!EffectRegistry.Exists("sample.vignette"))
        {
            var info = PluginLoader.Load(dir!, Path.Combine(dir!, "plugin.json"));
            Assert.True(info.Loaded, info.Error);
            Assert.Equal("サンプルプラグイン", info.Name);
        }
        Assert.Contains(EffectRegistry.Filters, f => f.Id == "sample.vignette");

        // メディアオブジェクト: 市松模様 (左上のマスは塗られ、その隣は透明 = 黒)
        var p = Small();
        var checker = ObjectFactory.Create("sample.checker", 0, 0, 60);
        checker.Main.Values["color"] = "#00FF00";
        p.Root.Objects.Add(checker);
        Assert.Equal(new SKColor(0, 255, 0), Pixel(p, 0, 10, 10));
        Assert.Equal(SKColors.Black, Pixel(p, 0, 50, 10));

        // フィルタ: 中心はそのまま、隅は暗くなる
        var q = Small();
        var bg = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        bg.Main.Values["type"] = "0";
        ObjectFactory.AddFilter(bg, "sample.vignette").Tracks["strength"] = new AnimatedValue(100);
        q.Root.Objects.Add(bg);
        Assert.Equal(new SKColor(255, 255, 255), Pixel(q, 0, 160, 120));
        Assert.True(Pixel(q, 0, 2, 2).Red < 60);

        // 色の変換 (セピア): 白は黄みがかる
        var sp = Small();
        var white = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        white.Main.Values["type"] = "0";
        ObjectFactory.AddFilter(white, "sample.sepia");
        sp.Root.Objects.Add(white);
        var sepia = Pixel(sp, 0, 160, 120);
        Assert.Equal(255, sepia.Red);
        Assert.InRange(sepia.Blue, 230, 245);

        // ピクセルの書き換え (階調 2): 暗い灰色は黒に、明るい灰色は白になる
        foreach (var (color, expected) in new[] { ("#707070", (byte)0), ("#909090", (byte)255) })
        {
            var pp = Small();
            var g = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
            g.Main.Values["type"] = "0";
            g.Main.Values["color"] = color;
            ObjectFactory.AddFilter(g, "sample.posterize").Tracks["levels"] = new AnimatedValue(2);
            pp.Root.Objects.Add(g);
            Assert.Equal(expected, Pixel(pp, 0, 160, 120).Green);
        }
        Assert.Contains(EffectRegistry.AudioFilters, f => f.Id == "sample.lowpass");

        // 移動方法: 終点に着いたあとも ID で保存・復元できる
        var v = new AnimatedValue { Values = new() { 0, 100 }, Custom = "sample.bounce" };
        Assert.Equal(100, v.Evaluate(new[] { 0, 100 }, 100), 3);
        Assert.True(v.Evaluate(new[] { 0, 100 }, 40) > 40);        // 直線より先に進む
        var o = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        o.Draw!.Tracks["x"].Custom = "sample.bounce";
        var r = new Project(); r.Root.Objects.Add(o);
        Assert.Equal("sample.bounce", ProjectSerializer.FromJson(ProjectSerializer.ToJson(r)).Root.Objects[0].Draw!.Tracks["x"].Custom);
    }

    [Fact]
    public void Broken_plugins_are_reported_not_thrown()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vb-plugins-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            File.WriteAllText(Path.Combine(root, "a", "plugin.json"), "{ not json");
            Directory.CreateDirectory(Path.Combine(root, "b"));
            File.WriteAllText(Path.Combine(root, "b", "plugin.json"), "{\"id\":\"x.b\",\"apiVersion\":99,\"assembly\":\"b.dll\"}");
            Directory.CreateDirectory(Path.Combine(root, "c"));
            File.WriteAllText(Path.Combine(root, "c", "plugin.json"), "{\"id\":\"x.c\",\"apiVersion\":1,\"assembly\":\"../../outside.dll\"}");
            var result = PluginLoader.LoadAll(root);
            Assert.Equal(3, result.Count);
            Assert.All(result, r => Assert.False(r.Loaded));
            Assert.Contains("API", result[1].Error);
            Assert.Contains("見つかりません", result[2].Error);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Missing_interpolator_plugin_falls_back_to_linear()
    {
        var v = new AnimatedValue { Values = new() { 0, 100 }, Custom = "not.installed" };
        Assert.Equal(50, v.Evaluate(new[] { 0, 100 }, 50), 6);
    }
}

/// <summary>高速化のための近道が、元の処理と同じ絵を出すことを確かめる。</summary>
public class OptimizationTests
{
    private static SKBitmap TestImage(int w = 640, int h = 360)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h),
            new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, new SKColor(255, 255, 255, 40) }, null, SKShaderTileMode.Clamp);
        using var p = new SKPaint { Shader = shader };
        c.DrawRect(20, 20, w - 40, h - 40, p);                       // 周囲は透明、内側は半透明を含むグラデーション
        return bmp;
    }

    private static (double Mean, int Max) Diff(SKBitmap a, SKBitmap b)
    {
        var pa = a.Bytes; var pb = b.Bytes;
        Assert.Equal(pa.Length, pb.Length);
        long total = 0; int max = 0;
        for (int i = 0; i < pa.Length; i++) { int d = Math.Abs(pa[i] - pb[i]); total += d; max = Math.Max(max, d); }
        return ((double)total / pa.Length, max);
    }

    [Theory]
    [InlineData(new float[] { 1.3f, 0, 0, 0, -0.1f, 0, 0.8f, 0, 0, 0.05f, 0, 0, 1.1f, 0, 0, 0, 0, 0, 1, 0 })]                 // チャンネルが混ざらない (表引き)
    [InlineData(new float[] { 0.6f, 0.3f, 0.1f, 0, 0, 0.2f, 0.7f, 0.1f, 0, 0.1f, 0.1f, 0.2f, 0.9f, 0, -0.05f, 0, 0, 0, 1, 0 })] // 混ざる (固定小数点)
    public void Parallel_color_matrix_matches_skia(float[] m)
    {
        using var src = TestImage();
        using var mine = src.Copy();
        PixelOps.ColorMatrix(mine.GetPixels(), mine.Width, mine.Height, mine.RowBytes, m);

        using var skia = new SKBitmap(src.Info);
        using (var c = new SKCanvas(skia))
        using (var cf = SKColorFilter.CreateColorMatrix(m))
        using (var p = new SKPaint { ColorFilter = cf, BlendMode = SKBlendMode.Src })
            c.DrawBitmap(src, 0, 0, p);
        var (mean, max) = Diff(mine, skia);
        Assert.True(mean < 0.5 && max <= 6, $"mean={mean:0.000} max={max}");
    }

    [Fact]
    public void Band_parallel_drawing_matches_single_threaded_drawing()
    {
        using var src = TestImage(1280, 720);
        using var image = SKImage.FromBitmap(src);
        using var blur = SKImageFilter.CreateBlur(6, 6);
        using var paint = new SKPaint { ImageFilter = blur };
        using var parallel = PixelOps.DrawParallel(1280, 720, c => { c.RotateDegrees(3, 640, 360); c.DrawImage(image, 0, 0, paint); });
        using var single = new SKBitmap(src.Info);
        using (var c = new SKCanvas(single)) { c.Clear(SKColors.Transparent); c.RotateDegrees(3, 640, 360); c.DrawImage(image, 0, 0, paint); }
        using var parallelBmp = SKBitmap.FromImage(parallel);
        var (mean, max) = Diff(parallelBmp, single);
        Assert.True(mean < 0.05 && max <= 3, $"帯の境目で絵が変わっている: mean={mean:0.000} max={max}");
    }

    private static Project Small()
    {
        var p = new Project();
        p.Settings.Width = 640; p.Settings.Height = 360;
        return p;
    }

    [Fact]
    public void Downscaled_blur_is_close_to_exact_blur()
    {
        using var src = TestImage();
        using var image = SKImage.FromBitmap(src);
        var p = Small();
        var o = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 10);
        var fx = ObjectFactory.AddFilter(o, "filter.blur");
        // 余白を広げる場合 (サイズ固定なし): 結果は元より大きくなり、中心は同じ位置に残る
        foreach (var radius in new[] { 6f, 12f, 30f })
        {
            fx.Tracks["radius"] = new AnimatedValue(radius);
            fx.Values["fixed"] = "0";
            var ctx = new RenderContext { Project = p, Frame = 0 };
            var result = Effects.Find("filter.blur")!.Apply(Sprite.Centered(image), new EffectArgs { Effect = fx, Object = o, Bounds = o.Bounds, Context = ctx });
            int pad = (int)MathF.Ceiling(radius * 1.5f);
            Assert.Equal((src.Width + pad * 2, src.Height + pad * 2), (result.Width, result.Height));
            Assert.Equal((src.Width / 2f + pad, src.Height / 2f + pad), (result.Cx, result.Cy));
            using var fast = SKBitmap.FromImage(result.Image);
            using var exact = new SKBitmap(new SKImageInfo(result.Width, result.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var c = new SKCanvas(exact))
            using (var f = SKImageFilter.CreateBlur(radius / 2, radius / 2))
            using (var paint = new SKPaint { ImageFilter = f })
            { c.Clear(SKColors.Transparent); c.DrawImage(image, pad, pad, paint); }
            var (mean, max) = Diff(fast, exact);
            ctx.DisposeTemporaries();
            Assert.True(mean < 1.5 && max < 40, $"余白あり radius={radius}: mean={mean:0.00} max={max}");
        }
        foreach (var radius in new[] { 6f, 8f, 11f, 24f, 60f })
        {
            fx.Tracks["radius"] = new AnimatedValue(radius);
            fx.Values["fixed"] = "1";
            var ctx = new RenderContext { Project = p, Frame = 0 };
            var result = Effects.Find("filter.blur")!.Apply(Sprite.Centered(image), new EffectArgs { Effect = fx, Object = o, Bounds = o.Bounds, Context = ctx });
            using var fast = SKBitmap.FromImage(result.Image);
            using var exact = new SKBitmap(src.Info);
            using (var c = new SKCanvas(exact))
            using (var f = SKImageFilter.CreateBlur(radius / 2, radius / 2))
            using (var paint = new SKPaint { ImageFilter = f })
            { c.Clear(SKColors.Transparent); c.DrawImage(image, 0, 0, paint); }
            var (mean, max) = Diff(fast, exact);
            ctx.DisposeTemporaries();
            Assert.True(mean < 1.5 && max < 40, $"radius={radius}: mean={mean:0.00} max={max}");
        }
    }

    [Fact]
    public void Filter_cache_is_reused_while_moving_and_rebuilt_when_parameters_change()
    {
        var p = Small();
        var o = ObjectFactory.Create(EffectRegistry.Text, 0, 0, 60);
        var border = ObjectFactory.AddFilter(o, "filter.border");
        ObjectFactory.AddFilter(o, "filter.fade").Tracks["in"] = new AnimatedValue(0);   // 時刻で変わる効果: キャッシュの対象外
        var x = o.Draw!.Tracks["x"]; x.Mode = MovementMode.Linear; x.Values[0] = -100; x.Values[1] = 100;
        p.Root.Objects.Add(o);

        using var cached = new RenderEngine();
        using var bmp = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int f = 0; f < 10; f++) cached.RenderToBitmap(p, p.Root, f, bmp);
        Assert.Equal((9L, 1L), cached.FilterCacheStats);        // 動いているだけなら、縁取りは最初の 1 回しか計算しない

        // キャッシュを使った絵は、毎回作り直した絵と同じ
        using var fresh = new RenderEngine();
        using var expected = new SKBitmap(bmp.Info);
        fresh.RenderToBitmap(p, p.Root, 9, expected);
        Assert.Equal(expected.Bytes, bmp.Bytes);

        // パラメータ・文字を変えたら作り直す
        border.Tracks["size"] = new AnimatedValue(9);
        cached.RenderToBitmap(p, p.Root, 9, bmp);
        Assert.Equal(2L, cached.FilterCacheStats.Misses);
        o.Main.Values["text"] = "別の文字";
        cached.RenderToBitmap(p, p.Root, 9, bmp);
        Assert.Equal(3L, cached.FilterCacheStats.Misses);
        using var fresh2 = new RenderEngine();
        fresh2.RenderToBitmap(p, p.Root, 9, expected);
        Assert.Equal(expected.Bytes, bmp.Bytes);

        // 中間点でパラメータが動いている間は毎フレーム作り直す
        var size = border.Tracks["size"]; size.Mode = MovementMode.Linear; size.Values[0] = 1; size.Values[1] = 10;
        long before = cached.FilterCacheStats.Misses;
        for (int f = 0; f < 30; f += 10) cached.RenderToBitmap(p, p.Root, f, bmp);
        Assert.Equal(before + 3, cached.FilterCacheStats.Misses);
    }

    [Fact]
    public void In_place_frame_filter_matches_the_generic_path()
    {
        var p = Small();
        var bg = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 10); bg.Main.Values["type"] = "0"; bg.Main.Values["color"] = "#3A6EA5";
        var t = ObjectFactory.Create(EffectRegistry.Text, 1, 0, 10);
        var f = ObjectFactory.Create("filter.color", 2, 0, 10);
        f.Main.Tracks["contrast"] = new AnimatedValue(130); f.Main.Tracks["saturation"] = new AnimatedValue(60); f.Main.Tracks["hue"] = new AnimatedValue(25);
        ObjectFactory.AddFilter(f, "filter.blur").Tracks["radius"] = new AnimatedValue(3);     // その場で処理できない効果が続く場合
        ObjectFactory.AddFilter(f, "filter.mono").Tracks["strength"] = new AnimatedValue(40);
        p.Root.Objects.AddRange(new[] { bg, t, f });

        using var cpu = new RenderEngine();
        using var a = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
        cpu.RenderToBitmap(p, p.Root, 0, a);
        // 比較相手: フィルタオブジェクトを外した絵
        using var b = new SKBitmap(a.Info);
        var q = p.Clone(); q.Root.Objects.RemoveAt(2);
        cpu.RenderToBitmap(q, q.Root, 0, b);
        // 厳密な再計算の代わりに、性質を確かめる: 彩度を下げてモノクロを混ぜたので、色の差が元より小さい
        var before = b.GetPixel(10, 10); var after = a.GetPixel(10, 10);
        int Spread(SKColor c) => Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue));
        Assert.True(Spread(after) < Spread(before), $"{before} → {after}");
        Assert.Equal(255, after.Alpha);

        // GPU でも同じ絵になる (GPU 側はその場処理を使わない別経路)
        using var gpu = GpuContext.TryCreate(out _);
        if (gpu == null) return;
        using var gpuEngine = new RenderEngine(gpu);
        using var g = new SKBitmap(a.Info);
        gpuEngine.RenderToBitmap(p, p.Root, 0, g);
        var (mean, _) = Diff(a, g);
        Assert.True(mean < 1.0, $"CPU と GPU の差が大きい: {mean:0.00}");
    }

    [Fact]
    public void Mosaic_fills_each_cell_with_its_average_color()
    {
        // 左半分が赤、右半分が青の 40x20。マス 16px: 2 列目のマス (x=16..31) は赤 4 列 + 青 12 列の平均になる
        using var bmp = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Red);
            using var blue = new SKPaint { Color = SKColors.Blue };
            c.DrawRect(20, 0, 20, 20, blue);
        }
        PixelOps.Mosaic(bmp.GetPixels(), 40, 20, bmp.RowBytes, 16);
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(3, 3));
        var mixed = bmp.GetPixel(17, 5);
        Assert.Equal(mixed, bmp.GetPixel(31, 15));                 // マスの中は同じ色
        Assert.InRange(mixed.Red, 62, 66);                          // 255 * 4/16
        Assert.InRange(mixed.Blue, 189, 193);                       // 255 * 12/16
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel(36, 18)); // 端の半端なマス (幅 8, 高さ 4)
        Assert.Equal(bmp.GetPixel(17, 17), bmp.GetPixel(30, 19));   // 下端の半端なマスも 1 色
    }

    [Fact]
    public void Mosaic_on_the_whole_frame_and_on_an_object_agree_between_cpu_and_gpu()
    {
        var p = Small();
        var bg = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 10); bg.Main.Values["type"] = "0"; bg.Main.Values["color"] = "#204060";
        var circle = ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 10); circle.Main.Values["type"] = "1"; circle.Main.Values["color"] = "#F2A900";
        ObjectFactory.AddFilter(circle, "filter.mosaic").Tracks["size"] = new AnimatedValue(20);
        var text = ObjectFactory.Create(EffectRegistry.Text, 2, 0, 10); text.Draw!.Tracks["y"] = new AnimatedValue(120);
        var frame = ObjectFactory.Create("filter.mosaic", 3, 0, 10); frame.Main.Tracks["size"] = new AnimatedValue(8);
        p.Root.Objects.AddRange(new[] { bg, circle, text, frame });

        using var cpu = new RenderEngine();
        using var a = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
        cpu.RenderToBitmap(p, p.Root, 0, a);
        // 画面全体のモザイク (8px): 同じマスの中は同じ色
        Assert.Equal(a.GetPixel(320, 296), a.GetPixel(327, 303));
        Assert.Equal(a.GetPixel(0, 0), a.GetPixel(7, 7));

        using var gpu = GpuContext.TryCreate(out _);
        if (gpu == null) return;
        using var gpuEngine = new RenderEngine(gpu);
        using var g = new SKBitmap(a.Info);
        gpuEngine.RenderToBitmap(p, p.Root, 0, g);
        var (mean, _) = Diff(a, g);
        Assert.True(mean < 2.0, $"CPU と GPU の差が大きい: {mean:0.00}");
    }

    [Fact]
    public void Fast_chroma_key_for_preview_is_close_to_the_exact_one()
    {
        using var src = TestImage();
        using var exact = src.Copy(); using var fast = src.Copy();
        PixelOps.ChromaKey(exact.GetPixels(), exact.Width, exact.Height, exact.RowBytes, 120, 100, 17, 37, 0.1f);
        PixelOps.ChromaKeyFast(fast.GetPixels(), fast.Width, fast.Height, fast.RowBytes, 120, 100, 17, 37, 0.1f);
        var (mean, _) = Diff(exact, fast);
        Assert.True(mean < 1.0, $"mean={mean:0.00}");
        // 緑の部分は両方とも抜けている
        int removed = 0;
        for (int x = 0; x < src.Width; x += 4) if (fast.GetPixel(x, 180).Alpha < src.GetPixel(x, 180).Alpha) removed++;
        Assert.True(removed > 5);
    }

    [Fact]
    public void Filter_cache_keeps_working_with_many_objects_on_screen()
    {
        var p = Small();
        for (int i = 0; i < 120; i++)
        {
            var o = ObjectFactory.Create(EffectRegistry.Shape, i % 100, 0, 30);
            o.Main.Tracks["size"] = new AnimatedValue(20 + i % 7);
            o.Draw!.Tracks["x"] = new AnimatedValue(-300 + i * 5);
            if (i >= 100) { o.Start = 40; o.End = 70; }      // レイヤーが足りないぶんは後ろの時間に置く
            ObjectFactory.AddFilter(o, "filter.border");
            p.Root.Objects.Add(o);
        }
        using var engine = new RenderEngine();
        using var bmp = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
        engine.RenderToBitmap(p, p.Root, 0, bmp);
        var first = engine.FilterCacheStats;
        Assert.Equal(100, first.Misses);
        for (int f = 1; f <= 5; f++) engine.RenderToBitmap(p, p.Root, f, bmp);
        // 2 フレーム目以降は 1 つも作り直さない (以前は 48 個を超えると毎フレーム作り直していた)
        Assert.Equal(100, engine.FilterCacheStats.Misses);
        Assert.Equal(500, engine.FilterCacheStats.Hits);
    }

    [Fact]
    public void Large_scaled_and_rotated_sprites_drawn_in_parallel_match_gpu_and_keep_positions()
    {
        var p = new Project();     // 1920x1080: 並列描画の条件 (広い範囲) を満たす大きさ
        var bg = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 10); bg.Main.Values["type"] = "0"; bg.Main.Values["color"] = "#102030";
        var big = ObjectFactory.Create(EffectRegistry.Text, 1, 0, 10);
        big.Main.Values["text"] = "並列描画"; big.Main.Tracks["size"] = new AnimatedValue(200);
        big.Draw!.Tracks["scale"] = new AnimatedValue(180); big.Draw!.Tracks["rotation"] = new AnimatedValue(12);
        big.Draw!.Tracks["alpha"] = new AnimatedValue(20);
        var tilted = ObjectFactory.Create(EffectRegistry.Shape, 2, 0, 10);
        tilted.Main.Tracks["size"] = new AnimatedValue(700); tilted.Main.Values["color"] = "#E53935";
        tilted.Draw!.Tracks["ry"] = new AnimatedValue(40); tilted.Draw!.Tracks["x"] = new AnimatedValue(400); tilted.Draw!.Values["blend"] = "4";
        p.Root.Objects.AddRange(new[] { bg, big, tilted });

        using var cpu = new RenderEngine();
        using var a = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
        cpu.RenderToBitmap(p, p.Root, 0, a);
        Assert.Equal(3, cpu.LastQuads.Count);
        Assert.NotEqual(new SKColor(0x10, 0x20, 0x30), a.GetPixel(1300, 540));    // 傾けた四角が描かれている
        Assert.Equal(new SKColor(0x10, 0x20, 0x30), a.GetPixel(5, 5));

        using var gpu = GpuContext.TryCreate(out _);
        if (gpu == null) return;
        using var gpuEngine = new RenderEngine(gpu);
        using var g = new SKBitmap(a.Info);
        gpuEngine.RenderToBitmap(p, p.Root, 0, g);
        var (mean, _) = Diff(a, g);
        Assert.True(mean < 0.8, $"CPU (並列) と GPU の差が大きい: {mean:0.00}");
    }

    [Fact]
    public void Pixel_pool_reuses_returned_buffers_and_images_return_them_on_dispose()
    {
        // ほかのテストと取り合いにならないよう、まず使われない半端な大きさで試す (幅 1237 x 高さ 2503)
        const int W = 1237, H = 2503, RowBytes = W * 4;
        long bytes = (long)RowBytes * H;
        var a = PixelPool.Rent(bytes);
        PixelPool.Return(a, bytes);
        var b = PixelPool.Rent(bytes - 100);         // 同じ大きさの区分なら同じバッファが返る
        Assert.Equal(a, b);
        var image = PixelPool.ToImage(b, new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul), RowBytes);
        image.Dispose();                             // 画像を捨てるとバッファが戻る
        var c = PixelPool.Rent(bytes);
        Assert.Equal(a, c);
        PixelPool.Return(c, bytes);
    }

    [Fact]
    public void Pooled_buffers_do_not_leak_old_pixels_into_new_images()
    {
        // 1 回目: 全面を赤で描いた画像を作って捨てる → 同じバッファで、何も描かない画像を作る
        using (var red = PixelOps.DrawParallel(300, 300, c => c.Clear(SKColors.Red))) { }
        using var empty = PixelOps.DrawParallel(300, 300, _ => { });
        using var bmp = SKBitmap.FromImage(empty);
        Assert.Equal(0, bmp.GetPixel(150, 150).Alpha);
        Assert.Equal(0, bmp.GetPixel(0, 299).Alpha);
    }

    [Fact]
    public void Strongly_downscaled_scene_object_matches_gpu_and_stays_in_place()
    {
        // シーンオブジェクトは「毎フレーム変わる絵」として、半分ずつ縮めてから描く経路を通る
        var p = new Project();
        var sub = new Scene { Id = "s1", Name = "Sub" };
        var inner = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 10); inner.Main.Values["type"] = "0"; inner.Main.Values["color"] = "#00C000";
        var mark = ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 10); mark.Main.Tracks["size"] = new AnimatedValue(600); mark.Main.Values["color"] = "#FF0000";
        sub.Objects.AddRange(new[] { inner, mark });
        p.Scenes.Add(sub);
        var so = ObjectFactory.Create(EffectRegistry.SceneRef, 0, 0, 10);
        so.Main.Values["scene"] = "s1";
        so.Draw!.Tracks["scale"] = new AnimatedValue(22);       // 1/4 にしてから少し縮める
        so.Draw!.Tracks["x"] = new AnimatedValue(300);
        p.Root.Objects.Add(so);

        using var cpu = new RenderEngine();
        using var a = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
        cpu.RenderToBitmap(p, p.Root, 0, a);
        // 縮小後の大きさ 422x238、中心 (1260, 540)
        Assert.Equal(new SKColor(255, 0, 0), a.GetPixel(1260, 540));
        Assert.Equal(new SKColor(0, 0xC0, 0), a.GetPixel(1260 - 200, 540));
        Assert.Equal(SKColors.Black, a.GetPixel(1260 - 216, 540));
        Assert.Equal(SKColors.Black, a.GetPixel(1260, 540 - 124));
        Assert.Equal(new SKColor(0, 0xC0, 0), a.GetPixel(1260, 540 - 114));

        using var gpu = GpuContext.TryCreate(out _);
        if (gpu == null) return;
        using var gpuEngine = new RenderEngine(gpu);
        using var g = new SKBitmap(a.Info);
        gpuEngine.RenderToBitmap(p, p.Root, 0, g);
        var (mean, _) = Diff(a, g);
        Assert.True(mean < 0.5, $"CPU と GPU の差が大きい: {mean:0.00}");
    }

    [Fact]
    public void Frame_filters_on_the_cpu_work_on_the_screen_without_copying_it_and_give_the_same_picture()
    {
        Project With(params bool[] flips)
        {
            var p = Small();
            var shape = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 10);
            shape.Main.Values["color"] = "#FF0000";
            shape.Draw!.Tracks["x"] = new AnimatedValue(-80);   // 左寄りの赤い図形
            p.Root.Objects.Add(shape);
            int layer = 1;
            foreach (bool h in flips)
            {
                var f = ObjectFactory.Create("filter.flip", layer++, 0, 10);
                f.Main.Values["horizontal"] = h ? "1" : "0";        // 0 なら何も変えない
                p.Root.Objects.Add(f);
            }
            return p;
        }
        byte[] Render(Project p)
        {
            using var e = new RenderEngine();
            using var bmp = new SKBitmap(new SKImageInfo(320, 240, SKColorType.Bgra8888, SKAlphaType.Premul));
            e.RenderToBitmap(p, p.Root, 0, bmp);
            return bmp.Bytes;
        }
        var plain = Render(With());
        Assert.Equal(plain, Render(With(false)));            // 何も変えない効果: 画面はそのまま
        Assert.Equal(plain, Render(With(true, true)));       // 左右反転を 2 回: 元どおり
        var flipped = Render(With(true));
        Assert.NotEqual(plain, flipped);
        // 左右反転: 左端の列と右端の列が入れ替わる
        for (int y = 0; y < 240; y += 13)
            for (int x = 0; x < 320; x += 17)
                Assert.Equal(plain.AsSpan((y * 320 + x) * 4, 4).ToArray(), flipped.AsSpan((y * 320 + (319 - x)) * 4, 4).ToArray());
    }

}

