using System.Diagnostics;
using VideoBuilder.Core;
using VideoBuilder.Media;

namespace VideoBuilder.Tests;

/// <summary>ffmpeg コマンドで作った小さな素材で、読み込みと書き出しを確かめる。ffmpeg がない環境ではスキップ扱い (何もせず成功)。</summary>
public class MediaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-media-{Guid.NewGuid():N}");
    private readonly bool _ready;
    private string Clip => Path.Combine(_dir, "clip.mp4");

    public MediaTests()
    {
        Directory.CreateDirectory(_dir);
        _ready = FFmpegLoader.EnsureLoaded() && RunFfmpeg(
            $"-f lavfi -i testsrc=size=320x240:rate=30:duration=2 -f lavfi -i sine=frequency=440:sample_rate=44100:duration=2 " +
            $"-c:v libx264 -pix_fmt yuv420p -g 15 -c:a aac -shortest \"{Clip}\"");
        // CI など FFmpeg が必ずある環境では VB_REQUIRE_FFMPEG=1 にして、スキップを失敗として扱う
        if (!_ready && Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG") == "1")
            throw new InvalidOperationException("FFmpeg が使えません: " + FFmpegLoader.Error);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    internal static bool RunFfmpeg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-y -loglevel error " + args) { RedirectStandardError = true });
            p!.WaitForExit(30000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public void Probe_reports_streams()
    {
        if (!_ready) return;
        var info = MediaInfo.Probe(Clip)!;
        Assert.True(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Equal(320, info.Width);
        Assert.Equal(30, info.FrameRate, 1);
        Assert.InRange(info.Duration, 1.8, 2.3);
    }

    [Fact]
    public void Video_frames_can_be_read_forward_and_after_seeking_back()
    {
        if (!_ready) return;
        using var r = new VideoReader(Clip);
        var first = r.GetFrame(0)!.ToArray();
        long id0 = r.CurrentFrameId;
        Assert.Equal(320 * 240 * 4, first.Length);
        Assert.Contains(first, b => b != 0);

        r.GetFrame(0.01);
        Assert.Equal(id0, r.CurrentFrameId);          // 同じフレームの範囲内
        r.GetFrame(1.0);
        long id1 = r.CurrentFrameId;
        Assert.NotEqual(id0, id1);
        var later = r.GetFrame(1.5)!.ToArray();
        Assert.NotEqual(first, later);

        var again = r.GetFrame(0)!.ToArray();          // 逆方向シーク
        Assert.Equal(id0, r.CurrentFrameId);
        Assert.Equal(first, again);
        Assert.NotNull(r.GetFrame(100));                // 末尾を超えたら最後のフレーム
    }

    [Fact]
    public void Audio_is_resampled_and_seekable()
    {
        if (!_ready) return;
        using var r = new AudioReader(Clip, 48000);
        var a = new float[4800 * 2];
        r.Read(0, a);
        r.Read(4800, a);
        Assert.Contains(a, s => Math.Abs(s) > 0.01f);
        // 440Hz のサイン波: 0.1 秒で 44 周期 → ゼロ交差はおよそ 88 回
        int crossings = 0;
        for (int i = 2; i < a.Length; i += 2) if (a[i - 2] < 0 != a[i] < 0) crossings++;
        Assert.InRange(crossings, 80, 96);

        var b = new float[4800 * 2];
        r.Read(48000, b);                                // 1 秒地点へシーク
        Assert.Contains(b, s => Math.Abs(s) > 0.01f);
        var c = new float[4800 * 2];
        r.Read(48000 * 10, c);                           // 末尾より後ろは無音
        Assert.All(c, s => Assert.Equal(0f, s));
    }

    [Theory]
    [InlineData("out.mp4", "libx264", "aac")]
    [InlineData("out.webm", "libvpx-vp9", "libopus")]
    [InlineData("out.wav", "", "pcm_s16le")]
    public void Writer_produces_a_readable_file(string name, string vcodec, string acodec)
    {
        if (!_ready) return;
        if (vcodec != "" && !MediaWriter.IsEncoderAvailable(vcodec)) return;
        if (!MediaWriter.IsEncoderAvailable(acodec)) return;
        var path = Path.Combine(_dir, name);
        var s = new ExportSettings { Path = path, Width = 160, Height = 120, Fps = new Rational(30, 1), VideoCodec = vcodec, AudioCodec = acodec };
        using (var w = new MediaWriter(s))
        {
            var frame = new byte[160 * 120 * 4];
            var audio = new float[1600 * 2];
            for (int f = 0; f < 30; f++)
            {
                Array.Fill(frame, (byte)(f * 8));
                w.WriteVideoFrame(frame, 160 * 4);
                for (int i = 0; i < 1600; i++) audio[i * 2] = audio[i * 2 + 1] = MathF.Sin((f * 1600 + i) * 0.05f) * 0.5f;
                w.WriteAudio(audio);
            }
            w.Finish();
        }
        var info = MediaInfo.Probe(path)!;
        Assert.Equal(vcodec != "", info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.InRange(info.Duration, 0.9, 1.15);
        if (info.HasVideo)
        {
            using var r = new VideoReader(path);
            var px = r.GetFrame(0.5)!;
            Assert.InRange(px[0], 100, 140);   // 15 フレーム目 = 120 付近のグレー
        }
    }
}

public class AudioOutputTests
{
    [Fact]
    public void Audio_output_never_throws_even_without_a_device()
    {
        using var output = new AudioOutput(48000);
        Console.WriteLine($"BENCH audio available={output.IsAvailable} error={output.Error}");
        if (!output.IsAvailable) { output.Start(0, (_, b) => Array.Clear(b)); output.Stop(); return; }
        int calls = 0;
        output.Start(1000, (_, b) => { calls++; Array.Clear(b); });
        Thread.Sleep(200);
        long pos = output.Position;
        output.Stop();
        Assert.True(calls > 0);
        Assert.True(pos >= 1000);
    }
}
