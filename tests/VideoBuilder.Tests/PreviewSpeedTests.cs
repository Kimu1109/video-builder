using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>
/// プレビュー・書き出しを速くする仕組み (縮めたデコード・次のフレームの先読み・シーンオブジェクトの縮小と使い回し) が、
/// 結果を変えずに働くことを確かめる。
/// </summary>
public class PreviewSpeedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-speed-{Guid.NewGuid():N}");
    private readonly string _clip;
    private readonly bool _ready;

    public PreviewSpeedTests()
    {
        Directory.CreateDirectory(_dir);
        _clip = Path.Combine(_dir, "clip.mp4");
        _ready = FFmpegLoader.EnsureLoaded() && MediaTests.RunFfmpeg(
            $"-f lavfi -i testsrc2=size=640x360:rate=30:duration=3 -c:v libx264 -pix_fmt yuv420p -g 30 \"{_clip}\"");
        if (!_ready && Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG") == "1")
            throw new InvalidOperationException("FFmpeg が使えません: " + FFmpegLoader.Error);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private Project WithVideo(params (int Layer, int Offset, double X)[] clips)
    {
        var p = new Project();
        p.Settings.Width = 640; p.Settings.Height = 360;
        foreach (var (layer, offset, x) in clips)
        {
            var v = ObjectFactory.Create(EffectRegistry.Video, layer, 0, 80);
            v.Main.Values["file"] = _clip;
            v.SourceOffsetFrames = offset;
            v.Draw!.Tracks["x"] = new AnimatedValue(x);
            p.Root.Objects.Add(v);
        }
        return p;
    }

    private static byte[] Frame(RenderEngine e, Project p, int f, int w = 640, int h = 360)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        e.RenderToBitmap(p, p.Root, f, bmp);
        return bmp.Bytes;
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }

    // ---------------- VideoReader ----------------

    [Fact]
    public unsafe void Reader_converts_to_a_smaller_size_and_the_current_frame_follows_a_new_divisor()
    {
        if (!_ready) return;
        using var r = new VideoReader(_clip);
        Assert.NotEqual(IntPtr.Zero, r.GetFramePointer(0.5));
        Assert.Equal((640, 360), (r.FrameWidth, r.FrameHeight));
        long id = r.CurrentFrameId;

        r.OutputDivisor = 2;
        Assert.NotEqual(IntPtr.Zero, r.GetFramePointer(0.5));   // 同じフレームを、新しい大きさで変換し直す
        Assert.Equal(id, r.CurrentFrameId);
        Assert.Equal((320, 180), (r.FrameWidth, r.FrameHeight));
        Assert.Equal(320 * 180 * 4, r.GetFrame(0.5)!.Length);

        r.OutputDivisor = 3;
        r.GetFramePointer(0.5);
        Assert.Equal((214, 120), (r.FrameWidth, r.FrameHeight));   // 割り切れないときは切り上げ
    }

    [Fact]
    public unsafe void A_pinned_frame_is_not_overwritten_while_the_reader_decodes_ahead()
    {
        if (!_ready) return;
        using var r = new VideoReader(_clip);
        var p0 = r.GetFramePointer(0.2);
        var before = new ReadOnlySpan<byte>((void*)p0, 640 * 360 * 4).ToArray();
        r.PinCurrentFrame();
        // 何フレームも先へ進めても、押さえたフレームの置き場は書き換わらない
        for (double t = 0.25; t < 1.5; t += 0.1) Assert.NotEqual(p0, r.GetFramePointer(t));
        Assert.Equal(before, new ReadOnlySpan<byte>((void*)p0, before.Length).ToArray());
        r.UnpinFrame();
    }

    // ---------------- 縮めたデコード ----------------

    [Fact]
    public void MediaCache_decodes_only_as_much_detail_as_requested()
    {
        if (!_ready) return;
        using var m = new MediaCache();
        m.BeginFrame();
        var img = m.GetVideoFrame("a", _clip, 0.5, false, out float scale, 0.5f);
        Assert.Equal((320, 180), (img!.Width, img.Height));
        Assert.Equal(2, scale, 3);

        m.BeginFrame();
        img = m.GetVideoFrame("a", _clip, 0.5, false, out scale, 0.6f);   // 1/2 では足りないので元の大きさ
        Assert.Equal(640, img!.Width);
        Assert.Equal(1, scale, 3);

        m.BeginFrame();
        img = m.GetVideoFrame("a", _clip, 0.5, false, out scale, 0.3f);
        Assert.Equal(214, img!.Width);   // 1/3 で足りる (1/4 では足りない)
        Assert.Equal(640 / 214f, scale, 3);
    }

    [Fact]
    public void Draft_preview_at_half_size_decodes_at_half_size_and_looks_the_same()
    {
        if (!_ready) return;
        var p = WithVideo((0, 0, 0));
        using var full = new RenderEngine();                    // 出力用 (縮めずにデコードする)
        using var draft = new RenderEngine { Draft = true };
        for (int f = 0; f < 30; f += 7)
        {
            var a = Frame(full, p, f, 320, 180);
            var b = Frame(draft, p, f, 320, 180);
            Assert.Equal((320, 180), draft.Media.LastFrameSize);
            Assert.Equal((640, 360), full.Media.LastFrameSize);
            Assert.True(MeanDifference(a, b) < 3, $"frame {f}: {MeanDifference(a, b)}");
        }
    }

    [Fact]
    public void Draft_keeps_full_detail_when_the_video_is_enlarged_or_has_a_pixel_filter()
    {
        if (!_ready) return;
        var p = WithVideo((0, 0, 0));
        var v = p.Root.Objects[0];
        using var draft = new RenderEngine { Draft = true };

        v.Draw!.Tracks["scale"] = new AnimatedValue(200);   // 2 倍に拡大して映すので、1/2 の画面でも元の細かさが要る
        Frame(draft, p, 0, 320, 180);
        Assert.Equal((640, 360), draft.Media.LastFrameSize);

        v.Draw.Tracks["scale"] = new AnimatedValue(100);
        var blur = new Effect(EffectRegistry.Get("filter.blur"));
        v.Effects.Insert(1, blur);                           // ぼかしの範囲はピクセル単位なので、縮めない
        Frame(draft, p, 1, 320, 180);
        Assert.Equal((640, 360), draft.Media.LastFrameSize);

        v.Effects[1] = new Effect(EffectRegistry.Get("filter.color"));   // 色だけを変えるものは縮めてよい
        Frame(draft, p, 2, 320, 180);
        Assert.Equal((320, 180), draft.Media.LastFrameSize);
    }

    // ---------------- 先読み ----------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Lookahead_gives_the_same_frames_as_plain_sequential_rendering(int videos)
    {
        if (!_ready) return;
        var p = videos == 1 ? WithVideo((0, 0, 0)) : WithVideo((0, 0, -160), (1, 45, 160));
        using var plain = new RenderEngine();
        using var ahead = new RenderEngine { Lookahead = true };
        for (int f = 0; f < 80; f++)
        {
            Assert.Equal(Frame(plain, p, f), Frame(ahead, p, f));
            if (f < 79) Assert.Equal(videos, ahead.Media.PendingLookaheads);   // 次のフレームを裏で読んでいる
        }
        Assert.Equal(plain.Media.TotalSeeks, ahead.Media.TotalSeeks);   // 先読みで余計なシークをしない
    }

    [Fact]
    public void A_clip_starting_soon_is_opened_ahead_and_the_cut_shows_the_same_frames()
    {
        if (!_ready) return;
        // A を再生したあと、60 フレーム目から B (同じファイルの 0.5 秒目から。A の続きではないのでシークが要る) に切り替わる
        // (A の続きから始まるなら、A のデコーダを読み進めれば届くので、先に開かない)
        var p = new Project();
        p.Settings.Width = 640; p.Settings.Height = 360;
        var a = ObjectFactory.Create(EffectRegistry.Video, 0, 0, 60);
        a.Main.Values["file"] = _clip;
        var b = ObjectFactory.Create(EffectRegistry.Video, 0, 60, 30);
        b.Main.Values["file"] = _clip;
        b.SourceOffsetFrames = 15;
        p.Root.Objects.AddRange(new[] { a, b });
        using var plain = new RenderEngine();
        using var ahead = new RenderEngine { Lookahead = true };
        for (int f = 0; f < 90; f++)
        {
            if (f == 55)
                // 開き終えるのを待つ (再生中なら間に合う)。CI では、ほかのテストと並んで動くので 2 秒では足りないことがあった
                for (int i = 0; i < 1000 && ahead.Media.PreloadsInFlight > 0; i++) Thread.Sleep(10);
            Assert.Equal(Frame(plain, p, f), Frame(ahead, p, f));
        }
        Assert.Equal(1, ahead.Media.PreloadHits);         // 切り替わりで、先に開いておいたデコーダを使った
        Assert.Equal(0, plain.Media.PreloadHits);         // 先読みしないときは開かない
    }

    [Fact]
    public void Lookahead_survives_jumping_to_another_position()
    {
        if (!_ready) return;
        var p = WithVideo((0, 0, 0));
        using var plain = new RenderEngine();
        using var ahead = new RenderEngine { Lookahead = true };
        foreach (int f in new[] { 0, 1, 2, 60, 61, 10, 11, 79, 0 })
            Assert.Equal(Frame(plain, p, f), Frame(ahead, p, f));
    }

    [Fact]
    public void Gpu_rendering_sends_video_frames_straight_to_the_gpu_and_matches_the_cpu()
    {
        if (!_ready) return;
        using var gpu = GpuContext.TryCreate(out _);
        if (gpu == null) return;
        // 2 本の動画 (並列にデコードしてから、描画のスレッドで GPU へ送る) + 先読み
        var p = WithVideo((0, 0, -160), (1, 45, 160));
        using var cpu = new RenderEngine();
        using var onGpu = new RenderEngine(gpu) { Lookahead = true };
        for (int f = 0; f < 40; f += 3)
        {
            var a = Frame(cpu, p, f);
            var b = Frame(onGpu, p, f);
            Assert.True(MeanDifference(a, b) < 1.0, $"frame {f}: {MeanDifference(a, b):0.00}");
        }
        // 複製せずに GPU へ送った (OpenGL ES 3 なら YUV のまま送って GPU で変換する。そうでなければ BGRA を直接送る)
        long sent = onGpu.Media.DirectUploads + onGpu.Media.GpuYuvFrames;
        Assert.True(sent >= 2 * 14, $"{onGpu.Media.DirectUploads} 回 + YUV {onGpu.Media.GpuYuvFrames} 回 ({onGpu.Media.GpuYuvError})");
        Assert.Equal(0, cpu.Media.DirectUploads);
        Assert.Equal(0, cpu.Media.GpuYuvFrames);
    }

    /// <summary>ffmpeg で、ファイルの各フレームの中身の要約 (MD5) を取る。</summary>
    private static string FrameHashes(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg", $"-loglevel error -i \"{path}\" -map 0:v -f framemd5 -") { RedirectStandardOutput = true };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        string text = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return string.Join("\n", text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',').Last().Trim()));
    }

    [Fact]
    public void Color_conversion_with_several_threads_gives_exactly_the_same_video()
    {
        if (!_ready) return;
        var p = WithVideo((0, 0, 0));
        p.Settings.Width = 1280; p.Settings.Height = 720;   // 720p 以上は BT.709 で変換する
        string Export(int threads)
        {
            var old = MediaWriter.ConvertThreads;
            MediaWriter.ConvertThreads = threads;
            try
            {
                var path = Path.Combine(_dir, $"conv{threads}.mp4");
                Exporter.Run(new ExportRequest
                {
                    Project = p, SceneId = p.Root.Id, StartFrame = 0, EndFrame = 9,
                    Settings = new ExportSettings { Path = path, AudioCodec = "", Preset = "ultrafast" },
                }, null, CancellationToken.None);
                return FrameHashes(path);
            }
            finally { MediaWriter.ConvertThreads = old; }
        }
        var single = Export(1);
        Assert.Equal(10, single.Split('\n').Length);
        Assert.Equal(single, Export(4));
    }

    [Fact]
    public void Image_sequence_is_encoded_in_parallel_and_every_frame_is_written_in_order()
    {
        if (!_ready) return;
        var p = WithVideo((0, 0, 0));
        p.Settings.Width = 320; p.Settings.Height = 180;
        var seq = Path.Combine(_dir, "seq");
        Directory.CreateDirectory(seq);
        var reported = new List<int>();
        Exporter.Run(new ExportRequest
        {
            Project = p, SceneId = p.Root.Id, StartFrame = 5, EndFrame = 24, ImageSequence = true,
            Settings = new ExportSettings { Path = Path.Combine(seq, "f.png") },
        }, new SyncProgress(x => { lock (reported) reported.Add(x.Done); }), CancellationToken.None);
        var files = Directory.GetFiles(seq, "*.png").Order().ToArray();
        Assert.Equal(20, files.Length);
        Assert.Equal(Path.Combine(seq, "f_00000.png"), files[0]);
        Assert.Equal(20, reported.Max());
        // 番号どおりの中身になっている (5 + i フレーム目を描いたものと同じ)
        using var e = new RenderEngine();
        foreach (int i in new[] { 0, 7, 19 })
        {
            using var png = SKBitmap.Decode(files[i]);
            Assert.Equal(Frame(e, p, 5 + i, 320, 180), png.Copy(SKColorType.Bgra8888).Bytes);
        }
    }

    private sealed class SyncProgress(Action<(int Done, int Total)> onReport) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => onReport(value);
    }

    [Fact]
    public void Export_with_lookahead_writes_a_playable_file()
    {
        if (!_ready) return;
        var p = WithVideo((0, 0, 0));
        var path = Path.Combine(_dir, "out.mp4");
        Exporter.Run(new ExportRequest
        {
            Project = p, SceneId = p.Root.Id, StartFrame = 0, EndFrame = 59,
            Settings = new ExportSettings { Path = path },
        }, null, CancellationToken.None);
        var info = MediaInfo.Probe(path)!;
        Assert.True(info.HasVideo);
        Assert.InRange(info.Duration, 1.8, 2.2);
    }
}

/// <summary>シーンオブジェクト: プレビューでは表示の大きさで描き、止まっているシーンは描き直さない。</summary>
public class SceneObjectSpeedTests
{
    private static (Project P, TimelineObject Inner, TimelineObject Ref) WithSubScene()
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 240;
        var sub = new Scene { Id = "s1", Name = "Sub" };
        var inner = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        inner.Main.Values["color"] = "#00FF00";
        sub.Objects.Add(inner);
        p.Scenes.Add(sub);
        var so = ObjectFactory.Create(EffectRegistry.SceneRef, 0, 0, 60);
        so.Main.Values["scene"] = "s1";
        p.Root.Objects.Add(so);
        return (p, inner, so);
    }

    private static SKColor Pixel(RenderEngine e, Project p, int frame, int x, int y, int w = 320, int h = 240)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        e.RenderToBitmap(p, p.Root, frame, bmp);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void A_still_scene_is_drawn_once_and_reused_on_later_frames()
    {
        var (p, _, _) = WithSubScene();
        using var e = new RenderEngine();
        for (int f = 0; f < 10; f++) Assert.Equal(new SKColor(0, 255, 0), Pixel(e, p, f, 160, 120));
        Assert.Equal((9L, 1L), e.SceneCacheStats);
    }

    [Fact]
    public void A_moving_scene_is_drawn_every_frame()
    {
        var (p, inner, _) = WithSubScene();
        inner.Draw!.Tracks["x"] = new AnimatedValue { Values = { 0, 100 }, Mode = MovementMode.Linear };
        using var e = new RenderEngine();
        for (int f = 0; f < 10; f++) Pixel(e, p, f, 160, 120);
        Assert.Equal((0L, 10L), e.SceneCacheStats);
        Pixel(e, p, 9, 160, 120);   // 同じフレームなら使い回す
        Assert.Equal((1L, 10L), e.SceneCacheStats);
    }

    [Fact]
    public void Editing_the_inner_scene_draws_it_again()
    {
        var (p, inner, _) = WithSubScene();
        using var e = new RenderEngine();
        Assert.Equal(new SKColor(0, 255, 0), Pixel(e, p, 0, 160, 120));
        // 編集後の複製では、変わったオブジェクトは別の実体になる
        var edited = inner.Clone();
        edited.Main.Values["color"] = "#FF0000";
        var sub = p.FindScene("s1")!;
        sub.Objects[0] = edited;
        Assert.Equal(new SKColor(255, 0, 0), Pixel(e, p, 1, 160, 120));
        sub.EditLayer(0).Visible = false;
        Assert.Equal(new SKColor(0, 0, 0), Pixel(e, p, 2, 160, 120));
        Assert.Equal((0L, 3L), e.SceneCacheStats);
    }

    [Fact]
    public void Draft_preview_draws_the_scene_at_the_displayed_size()
    {
        var (p, _, so) = WithSubScene();
        using var full = new RenderEngine();
        using var draft = new RenderEngine { Draft = true };
        // 半分の大きさのプレビュー: シーンも半分の大きさで描いて、同じ見た目になる
        Assert.Equal(Pixel(full, p, 0, 80, 60, 160, 120), Pixel(draft, p, 0, 80, 60, 160, 120));
        // 拡大して映すときは、拡大したぶん細かく描く (ぼやけない)
        so.Draw!.Tracks["scale"] = new AnimatedValue(300);
        Assert.Equal(Pixel(full, p, 1, 80, 60, 160, 120), Pixel(draft, p, 1, 80, 60, 160, 120));
    }
}
