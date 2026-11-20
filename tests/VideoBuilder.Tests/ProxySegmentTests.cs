using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>
/// 区切りごとのプロキシ: できた区切りから使えること・区切りの境目で時刻が合うこと・見ている位置から作ること・
/// 途中でやめても続きから作れること・ハードウェアのデコードが失敗してもできあがること。
/// </summary>
[Collection("HardwareDecoding")]   // ハードウェアデコードの設定・プロキシの置き場所 (静的な値) を、ほかのテストと同時に変えない
public class ProxySegmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-seg-{Guid.NewGuid():N}");
    private readonly string _clip;
    private readonly bool _ready;
    private readonly string _oldDirectory = ProxyStore.Directory;
    private const double Length = 50;   // 区切り 3 つ (20 + 20 + 10 秒)

    public ProxySegmentTests()
    {
        Directory.CreateDirectory(_dir);
        ProxyStore.Directory = Path.Combine(_dir, "proxies");
        ProxyStore.Divisor = 2;
        ProxyStore.Enabled = true;
        ProxyStore.NotifyChanged();
        _clip = Path.Combine(_dir, "long.mp4");
        // 時刻ごとに模様が変わる動画。キーフレームの間隔 (3 秒) は区切りの長さ (20 秒) で割り切れない
        _ready = FFmpegLoader.EnsureLoaded() && MediaTests.RunFfmpeg(
            $"-f lavfi -i testsrc2=size=320x180:rate=30:duration={Length} -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 90 \"{_clip}\"");
        if (!_ready && Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG") == "1")
            throw new InvalidOperationException("FFmpeg が使えません: " + FFmpegLoader.Error);
    }

    public void Dispose()
    {
        HardwareDecoding.TestPretendHardware = false;
        ProxyGenerator.TestHardwareFailAfterFrames = -1;
        ProxyStore.Directory = _oldDirectory;
        ProxyStore.NotifyChanged();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Folder => ProxyStore.PathFor(_clip)!;

    private static double MeanDiff(byte[] a, byte[] b)
    {
        long t = 0;
        for (int i = 0; i < a.Length; i++) t += Math.Abs(a[i] - b[i]);
        return (double)t / a.Length;
    }

    /// <summary>元の動画の t 秒のフレームを、プロキシの大きさに縮めたもの。</summary>
    private static byte[] Shrunk(VideoReader original, double t)
    {
        using var src = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(original.GetFrame(t)!, 0, src.GetPixels(), 320 * 180 * 4);
        using var small = src.Resize(new SKImageInfo(160, 90, SKColorType.Bgra8888, SKAlphaType.Opaque), SKFilterQuality.Medium)!;
        return small.Bytes;
    }

    /// <summary>プロキシの区切りから、t 秒のフレームを読む。</summary>
    private static byte[] FromProxy(string clip, double t)
    {
        var segment = ProxyStore.FindSegment(clip, t);
        Assert.NotNull(segment);
        using var r = new VideoReader(segment!, absoluteTime: true);
        return r.GetFrame(t)!.ToArray();
    }

    private void AssertFramesLineUp(params double[] times)
    {
        using var original = new VideoReader(_clip);
        foreach (var t in times)
        {
            var proxy = FromProxy(_clip, t);
            double same = MeanDiff(Shrunk(original, t), proxy);
            double before = t >= 0.1 ? MeanDiff(Shrunk(original, t - 0.1), proxy) : double.MaxValue;
            double after = MeanDiff(Shrunk(original, t + 0.1), proxy);
            Assert.True(same < 10, $"t={t}: 差が大きい {same:0.0}");
            // 3 フレームずれたものよりも、同じ時刻のものに近い (時刻の対応が合っている)
            Assert.True(same < before && same < after, $"t={t}: 同じ時刻 {same:0.00} / 前 {before:0.00} / 後 {after:0.00}");
        }
    }

    [Fact]
    public void A_long_video_is_split_into_segments_whose_frames_line_up_across_the_boundaries()
    {
        if (!_ready) return;
        ProxyGenerator.Create(_clip, Folder, 2, null, CancellationToken.None);
        Assert.True(ProxyStore.Exists(_clip));
        Assert.Equal(3, Directory.GetFiles(Folder, "*.mkv").Length);
        Assert.Equal(ProxyStore.SegmentPath(Folder, 0), ProxyStore.FindSegment(_clip, 19.99));
        Assert.Equal(ProxyStore.SegmentPath(Folder, 1), ProxyStore.FindSegment(_clip, 20.0));
        Assert.Equal(ProxyStore.SegmentPath(Folder, 2), ProxyStore.FindSegment(_clip, 49.9));
        // 区切りの始まり・終わりのあたりも、元の動画と同じ時刻のフレームが読める
        AssertFramesLineUp(0, 5.5, 19.95, 20.0, 20.02, 21.3, 39.97, 40.0, 40.05, 49.9);
    }

    [Fact]
    public void The_segment_at_the_focus_is_made_first_and_is_usable_before_the_rest()
    {
        if (!_ready) return;
        var order = new List<int>();
        bool usableEarly = false;
        void OnChanged()
        {
            lock (order)
            {
                for (int i = 0; i < 3; i++)
                    if (!order.Contains(i) && File.Exists(ProxyStore.SegmentPath(Folder, i))) order.Add(i);
                // 最初の区切りができた時点で、もうその時刻のプロキシが使える (全体はまだ)
                if (order.Count == 1) usableEarly = ProxyStore.FindSegment(_clip, 45) != null && !ProxyStore.Exists(_clip);
            }
        }
        ProxyStore.Changed += OnChanged;
        try { ProxyGenerator.Create(_clip, Folder, 2, null, CancellationToken.None, focus: () => 45); }
        finally { ProxyStore.Changed -= OnChanged; }
        Assert.Equal(2, order[0]);     // 45 秒は区切り 2
        Assert.Equal(3, order.Count);
        Assert.True(usableEarly);
        AssertFramesLineUp(0.5, 20.0, 41.0);
    }

    [Fact]
    public void Stopping_keeps_finished_segments_and_the_next_run_makes_only_the_rest()
    {
        if (!_ready) return;
        using var cts = new CancellationTokenSource();
        void OnChanged() { if (File.Exists(ProxyStore.SegmentPath(Folder, 0))) cts.Cancel(); }
        ProxyStore.Changed += OnChanged;
        try { Assert.ThrowsAny<OperationCanceledException>(() => ProxyGenerator.Create(_clip, Folder, 2, null, cts.Token)); }
        finally { ProxyStore.Changed -= OnChanged; }

        var first = ProxyStore.SegmentPath(Folder, 0);
        Assert.True(File.Exists(first));
        Assert.Empty(Directory.GetFiles(Folder, "*.part"));       // 書きかけは残さない
        Assert.False(ProxyStore.Exists(_clip));
        Assert.NotNull(ProxyStore.FindSegment(_clip, 3));        // できた区切りは使える
        var stamp = File.GetLastWriteTimeUtc(first);

        ProxyStore.NotifyChanged();                                // アプリを開き直したのと同じ (覚えた結果を捨てる)
        Assert.NotNull(ProxyStore.FindSegment(_clip, 3));
        var progress = new List<double>();
        ProxyGenerator.Create(_clip, Folder, 2, new Sync(progress.Add), CancellationToken.None);
        Assert.True(ProxyStore.Exists(_clip));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(first));     // 作り直していない
        Assert.True(progress[0] >= 0.33, $"続きから: {progress[0]}");   // 区切り 3 つのうち 1 つはできている
        AssertFramesLineUp(10, 30, 45);
    }

    [Fact]
    public void A_hardware_decoding_failure_in_the_middle_falls_back_to_software_and_finishes()
    {
        if (!_ready) return;
        HardwareDecoding.TestPretendHardware = true;
        ProxyGenerator.TestHardwareFailAfterFrames = 700;   // 区切り 1 の途中で失敗させる
        ProxyGenerator.Create(_clip, Folder, 2, null, CancellationToken.None);
        Assert.Equal(-1, ProxyGenerator.TestHardwareFailAfterFrames);   // 失敗を起こした
        Assert.True(ProxyStore.Exists(_clip));
        AssertFramesLineUp(10, 20.0, 23.0, 30, 45);
    }

    [Fact]
    public void Preview_plays_across_segment_boundaries_without_piling_up_decoders()
    {
        if (!_ready) return;
        ProxyGenerator.Create(_clip, Folder, 2, null, CancellationToken.None);
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 180;
        var v = ObjectFactory.Create(EffectRegistry.Video, 0, 0, 1500);
        v.Main.Values["file"] = _clip;
        p.Root.Objects.Add(v);
        using var e = new RenderEngine { Draft = true, Lookahead = true };
        using var bmp = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int f = 590; f < 610; f++)
        {
            e.RenderToBitmap(p, p.Root, f, bmp);
            Assert.Equal((160, 90), e.Media.LastFrameSize);   // プロキシを読んでいる
        }
        for (int f = 1190; f < 1210; f++) e.RenderToBitmap(p, p.Root, f, bmp);
        Assert.True(e.Media.ReaderCount <= 2, $"デコーダ {e.Media.ReaderCount} 個");

        // ループ: 50 秒の動画の 65 秒目は 15 秒目 (区切り 0)
        var looped = ObjectFactory.Create(EffectRegistry.Video, 0, 0, 3000);
        looped.Main.Values["file"] = _clip;
        looped.Main.Values["loop"] = "1";
        var q = new Project();
        q.Settings.Width = 320; q.Settings.Height = 180;
        q.Root.Objects.Add(looped);
        using var e2 = new RenderEngine { Draft = true };
        using var e3 = new RenderEngine();
        e2.RenderToBitmap(q, q.Root, 65 * 30, bmp);
        Assert.Equal((160, 90), e2.Media.LastFrameSize);
        var proxyFrame = bmp.Bytes;
        using var full = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        e3.RenderToBitmap(q, q.Root, 15 * 30, full);
        Assert.True(MeanDiff(proxyFrame, full.Bytes) < 10);
    }

    [Fact]
    public void A_single_file_proxy_from_an_older_version_is_still_used()
    {
        if (!_ready) return;
        Directory.CreateDirectory(ProxyStore.Directory);
        var legacy = Folder + ProxyStore.Extension;
        Assert.True(MediaTests.RunFfmpeg($"-i \"{_clip}\" -vf scale=160:90 -c:v mjpeg -pix_fmt yuvj420p \"{legacy}\""));
        ProxyStore.NotifyChanged();
        Assert.True(ProxyStore.Exists(_clip));
        Assert.Equal(legacy, ProxyStore.FindSegment(_clip, 33));
        AssertFramesLineUp(33);
        Assert.Equal(1, ProxyStore.Delete(_clip));
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void Proxy_files_are_as_small_as_ffmpeg_makes_them_at_the_same_quality()
    {
        if (!_ready) return;
        ProxyGenerator.Create(_clip, Folder, 2, null, CancellationToken.None);
        long ours = Directory.GetFiles(Folder, "*.mkv").Sum(f => new FileInfo(f).Length);
        // 同じ画質 (量子化 4)・同じ縮小 (バイリニア) で、ffmpeg のコマンドが作る大きさ
        var reference = Path.Combine(_dir, "reference.mkv");
        Assert.True(MediaTests.RunFfmpeg($"-i \"{_clip}\" -an -vf scale=160:90:flags=bilinear -c:v mjpeg -q:v 4 -pix_fmt yuvj420p \"{reference}\""));
        long theirs = new FileInfo(reference).Length;
        // 以前はフレームごとの画質を渡しておらず、いちばん細かい量子化になって 3〜5 割大きかった
        Assert.True(ours < theirs * 1.15, $"プロキシ {ours / 1024} KB / ffmpeg {theirs / 1024} KB");
    }

    [Fact]
    public void Decode_speed_and_keyframe_interval_are_measured_on_this_machine()
    {
        if (!_ready) return;
        var check = ProxyStore.MeasureDecode(_clip)!;
        Assert.True(check.FramesPerSecond > 30, $"{check.FramesPerSecond:0} fps");   // 320x180 は軽い
        Assert.InRange(check.KeyframeInterval, 2.9, 3.1);                            // -g 90 (30 fps) = 3 秒
        Assert.Same(check, ProxyStore.MeasureDecode(_clip));                         // 2 回目は測り直さない

        // キーフレームが 10 秒に 1 回しかない 720p: シークが遅いのでプロキシを作る
        var sparse = Path.Combine(_dir, "sparse.mp4");
        Assert.True(MediaTests.RunFfmpeg($"-f lavfi -i testsrc2=size=1280x720:rate=30:duration=12 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 300 -keyint_min 300 -sc_threshold 0 \"{sparse}\""));
        var info = MediaInfo.Probe(sparse)!;
        var sparseCheck = ProxyStore.MeasureDecode(sparse)!;
        Assert.True(sparseCheck.KeyframeInterval > 9, $"{sparseCheck.KeyframeInterval:0.0} 秒");
        Assert.False(ProxyStore.IsHeavy(info));
        Assert.True(ProxyStore.NeedsProxy(info, sparseCheck));
        Assert.False(ProxyStore.NeedsProxy(MediaInfo.Probe(_clip)!, check));       // 軽くてシークも速い動画は作らない
    }

    [Fact]
    public void Proxies_are_needed_when_decoding_cannot_keep_up_with_playback()
    {
        var hd = new MediaInfo(true, true, 1920, 1080, 60, 100) { VideoCodec = "h264" };
        Assert.False(ProxyStore.NeedsProxy(hd, null));                                        // 測れなければ従来どおり (重くなければ作らない)
        Assert.False(ProxyStore.NeedsProxy(hd, new DecodeCheck(200, 2, "ソフトウェア")));
        Assert.True(ProxyStore.NeedsProxy(hd, new DecodeCheck(70, 2, "ソフトウェア")));        // 60 fps の 1.5 倍に足りない
        Assert.True(ProxyStore.NeedsProxy(hd, new DecodeCheck(200, 8, "ソフトウェア")));       // キーフレームの間隔が長い
        var small = new MediaInfo(true, true, 640, 360, 30, 100) { VideoCodec = "h264" };
        Assert.False(ProxyStore.NeedsProxy(small, new DecodeCheck(200, 8, "ソフトウェア")));   // 小さい動画はシークも速い
        Assert.True(ProxyStore.NeedsProxy(new MediaInfo(true, true, 3840, 2160, 30, 10) { VideoCodec = "h264" }, null));
        Assert.False(ProxyStore.NeedsProxy(new MediaInfo(false, true, 0, 0, 0, 10), null));
    }

    private sealed class Sync(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
