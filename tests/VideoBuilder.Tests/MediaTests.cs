using System.Diagnostics;
using VideoBuilder.Core;
using SkiaSharp;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

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
    public void Readers_skip_the_streams_they_do_not_decode()
    {
        if (!_ready) return;
        // 映像と音声の入った動画から音声だけを読むとき、映像のパケットは読み込まない (設計書 22.59)
        using (var a = new AudioReader(Clip, 48000))
        {
            var buf = new float[48000 * 2];
            a.Read(0, buf);
            a.Read(48000, buf);
            Assert.Contains(buf, s => Math.Abs(s) > 0.01f);
            Assert.Equal(0, a.OtherStreamPackets);
        }
        using (var v = new VideoReader(Clip))
        {
            Assert.NotNull(v.GetFrame(1.5));
            Assert.Equal(0, v.OtherStreamPackets);
        }
    }

    [Fact]
    public void Thumbnail_frames_use_a_nearby_keyframe_and_leave_later_frames_exact()
    {
        if (!_ready) return;
        // キーフレームは 0.5 秒 (15 フレーム) ごと。testsrc はフレームごとに絵が変わる
        byte[] Exact(double t) { using var r = new VideoReader(Clip); return r.GetFrame(t)!.ToArray(); }
        using var thumbs = new VideoReader(Clip, absoluteTime: false, allowHardware: false);
        Assert.False(thumbs.IsHardware);

        // 0.9 秒の 1 秒以内前にキーフレーム (0.5 秒) がある → キーフレームをそのまま使う
        var near = thumbs.GetFrameNear(0.9, 1.0)!.ToArray();
        Assert.Equal(1, thumbs.KeyframeShortcuts);
        Assert.Equal(Exact(0.5), near);
        // 同じキーフレームで足りる時刻は、デコードし直さない
        Assert.Equal(near, thumbs.GetFrameNear(0.7, 1.0)!.ToArray());
        Assert.Equal(1, thumbs.KeyframeShortcuts);

        // キーフレーム (1.0 秒) が許す差 (0.1 秒) より前 → ふつうにデコードして、その時刻のフレーム
        Assert.Equal(Exact(1.2), thumbs.GetFrameNear(1.2, 0.1)!.ToArray());
        Assert.Equal(1, thumbs.KeyframeShortcuts);

        // キーフレームだけをデコードしたあとでも、続けて読むフレームは正しい (参照の欠けたまま続きを読まない)
        thumbs.GetFrameNear(0.6, 1.0);
        Assert.Equal(Exact(0.8), thumbs.GetFrame(0.8)!.ToArray());
        Assert.Equal(Exact(1.6), thumbs.GetFrame(1.6)!.ToArray());
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
        Console.WriteLine($"BENCH audio available={output.IsAvailable} float={output.UsesFloat} error={output.Error}");
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

/// <summary>
/// ハードウェアエンコード。テスト環境には GPU がないので、「使えない環境で安全に失敗すること」と
/// 「エンコーダごとの設定の組み立て」を確かめる (実際のエンコード結果は GPU のある環境でしか確認できない)。
/// </summary>
public class HardwareEncoderTests
{
    [Fact]
    public void Family_is_recognized_from_the_encoder_name()
    {
        Assert.Equal("nvenc", HardwareEncoders.FamilyOf("hevc_nvenc"));
        Assert.Equal("vaapi", HardwareEncoders.FamilyOf("h264_vaapi"));
        Assert.Equal("", HardwareEncoders.FamilyOf("libx264"));
        Assert.False(HardwareEncoders.IsHardware("libsvtav1"));
        Assert.All(HardwareEncoders.Candidates, c => Assert.True(HardwareEncoders.IsHardware(c.Encoder)));
        Assert.Equal(HardwareEncoders.Candidates.Length, HardwareEncoders.Candidates.Select(c => c.Encoder).Distinct().Count());
    }

    [Fact]
    public void Quality_and_speed_are_translated_for_each_encoder_family()
    {
        var nv = VideoEncoderSetup.OptionsFor("h264_nvenc", 20, "slow");
        Assert.Equal(("vbr", "20", "p5"), (nv["rc"], nv["cq"], nv["preset"]));
        Assert.Equal("1", VideoEncoderSetup.OptionsFor("h264_nvenc", 0, "medium")["cq"]);      // 0 は「自動」なので避ける
        Assert.Equal("p4", VideoEncoderSetup.OptionsFor("hevc_nvenc", 20, "unknown")["preset"]);

        var va = VideoEncoderSetup.OptionsFor("h264_vaapi", 22, "medium");
        Assert.Equal(("CQP", "22"), (va["rc_mode"], va["qp"]));
        Assert.Equal("110", VideoEncoderSetup.OptionsFor("av1_vaapi", 22, "medium")["qp"]);    // AV1 は 0〜255 の目盛り

        Assert.Equal("veryfast", VideoEncoderSetup.OptionsFor("h264_qsv", 20, "ultrafast")["preset"]);
        var amf = VideoEncoderSetup.OptionsFor("h264_amf", 18, "veryslow");
        Assert.Equal(("cqp", "18", "quality"), (amf["rc"], amf["qp_p"], amf["quality"]));
        Assert.Empty(VideoEncoderSetup.OptionsFor("h264_videotoolbox", 20, "medium"));           // ビットレートで指定

        // ソフトウェアエンコーダの設定は従来どおり
        var x264 = VideoEncoderSetup.OptionsFor("libx264", 20, "fast");
        Assert.Equal(("20", "fast"), (x264["crf"], x264["preset"]));
    }

    [Fact]
    public void Bitrate_estimate_doubles_every_six_quality_steps()
    {
        long a = VideoEncoderSetup.EstimateBitrate(1920, 1080, 30, 23), b = VideoEncoderSetup.EstimateBitrate(1920, 1080, 30, 17);
        Assert.InRange(a, 6_000_000, 6_500_000);
        Assert.InRange((double)b / a, 1.99, 2.01);
    }

    [Fact]
    public void Probing_never_throws_and_reports_why_an_encoder_is_unusable()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        Assert.False(HardwareEncoders.Probe("no_such_encoder", out var missing));
        Assert.NotNull(missing);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var available = HardwareEncoders.FindAvailable();
        Console.WriteLine($"BENCH hardware encoders usable here: {(available.Count == 0 ? "(none)" : string.Join(", ", available.Select(a => a.Encoder)))} (probe {sw.ElapsedMilliseconds} ms)");
        foreach (var c in HardwareEncoders.Candidates)
        {
            bool ok = HardwareEncoders.Probe(c.Encoder, out var error);     // 2 回目は覚えている結果を返す
            Assert.Equal(available.Contains(c), ok);
            if (!ok) Assert.False(string.IsNullOrEmpty(error));
        }
    }

    [Fact]
    public void Export_with_an_unusable_hardware_encoder_fails_cleanly()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        // この環境で使えないものを 1 つ選ぶ (全部使える環境ではテストしない)
        var unusable = HardwareEncoders.Candidates.FirstOrDefault(c => MediaWriter.IsEncoderAvailable(c.Encoder) && !HardwareEncoders.Probe(c.Encoder, out _));
        if (unusable == null) return;
        var path = Path.Combine(Path.GetTempPath(), $"vb-hw-{Guid.NewGuid():N}.mp4");
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var w = new MediaWriter(new ExportSettings { Path = path, Width = 320, Height = 240, VideoCodec = unusable.Encoder, AudioCodec = "" });
        });
        Assert.False(string.IsNullOrEmpty(ex.Message));
        try { File.Delete(path); } catch { }
    }

    [Fact]
    public void Usable_hardware_encoders_produce_a_readable_file()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        // GPU のある環境でだけ実行される
        foreach (var h in HardwareEncoders.FindAvailable())
        {
            var path = Path.Combine(Path.GetTempPath(), $"vb-hw-{h.Encoder}-{Guid.NewGuid():N}.mp4");
            try
            {
                using (var w = new MediaWriter(new ExportSettings { Path = path, Width = 640, Height = 360, Fps = new Rational(30, 1), VideoCodec = h.Encoder, AudioCodec = "aac" }))
                {
                    var frame = new byte[640 * 360 * 4];
                    var audio = new float[1600 * 2];
                    for (int f = 0; f < 30; f++)
                    {
                        Array.Fill(frame, (byte)(f * 8));
                        w.WriteVideoFrame(frame, 640 * 4);
                        w.WriteAudio(audio);
                    }
                    w.Finish();
                }
                var info = MediaInfo.Probe(path)!;
                Assert.True(info.HasVideo, h.Encoder);
                Assert.Equal(640, info.Width);
                using var r = new VideoReader(path);
                Assert.InRange(r.GetFrame(0.5)![0], 90, 150);      // 15 フレーム目 = 120 付近のグレー
            }
            finally { try { File.Delete(path); } catch { } }
        }
    }
}


[CollectionDefinition("HardwareDecoding", DisableParallelization = true)]
public class HardwareDecodingCollection { }

/// <summary>
/// ハードウェアデコード。テスト環境には GPU がないので、「使えない環境ではソフトウェアと同じ結果になること」と
/// 「途中で失敗したときにソフトウェアへ戻って同じ絵を返すこと」を確かめる。
/// 設定 (静的な値) を書き換えるので、ほかのテストと同時には動かさない。
/// </summary>
[Collection("HardwareDecoding")]
public class HardwareDecodingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-hwdec-{Guid.NewGuid():N}");
    private readonly string _clip;
    private readonly bool _ready;

    public HardwareDecodingTests()
    {
        Directory.CreateDirectory(_dir);
        _clip = Path.Combine(_dir, "clip.mp4");
        _ready = FFmpegLoader.EnsureLoaded() && MediaTests.RunFfmpeg(
            $"-f lavfi -i testsrc=size=320x240:rate=30:duration=2 -c:v libx264 -pix_fmt yuv420p -g 15 \"{_clip}\"");
    }

    public void Dispose()
    {
        HardwareDecoding.Mode = HardwareDecodeMode.Auto;
        HardwareDecoding.TestPretendHardware = false;
        HardwareDecoding.TestFailAfterFrames = -1;
        HardwareDecoding.PreferredDevice = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly double[] Times = { 0, 0.1, 0.5, 1.0, 1.9, 0.2, 1.5, 0.0 };   // 前へ・後ろへのシークを含む

    private List<byte[]> ReadAll(HardwareDecodeMode mode, out VideoReader reader)
    {
        HardwareDecoding.Mode = mode;
        reader = new VideoReader(_clip);
        var frames = new List<byte[]>();
        foreach (var t in Times) frames.Add(reader.GetFrame(t)!.ToArray());
        return frames;
    }

    [Fact]
    public void Without_a_gpu_auto_mode_decodes_in_software_with_identical_frames()
    {
        if (!_ready) return;
        var software = ReadAll(HardwareDecodeMode.Off, out var off);
        var auto = ReadAll(HardwareDecodeMode.Auto, out var r);
        using (off) using (r)
        {
            Console.WriteLine($"BENCH hwdec here: {r.DecoderName} ({r.HardwareError ?? "-"})");
            Assert.Equal("ソフトウェア", off.DecoderName);
            Assert.Null(off.HardwareError);                           // 使わない設定なら試してもいない
            if (!r.IsHardware) Assert.False(string.IsNullOrEmpty(r.HardwareError));
            for (int i = 0; i < Times.Length; i++) Assert.Equal(software[i], auto[i]);
        }
    }

    [Fact]
    public void Failure_in_the_middle_falls_back_to_software_and_returns_the_same_frames()
    {
        if (!_ready) return;
        var software = ReadAll(HardwareDecodeMode.Off, out var off);
        off.Dispose();
        HardwareDecoding.TestPretendHardware = true;
        HardwareDecoding.TestFailAfterFrames = 3;                       // 4 枚目を取り出すときに失敗させる
        var mixed = ReadAll(HardwareDecodeMode.Auto, out var r);
        using (r)
        {
            Assert.False(r.IsHardware);
            Assert.Equal("ソフトウェア", r.DecoderName);
            Assert.Equal("テスト用の失敗", r.HardwareError);
            for (int i = 0; i < Times.Length; i++) Assert.Equal(software[i], mixed[i]);
        }
    }

    [Fact]
    public unsafe void V4l2_decoders_are_named_after_the_codec()
    {
        Assert.Equal("h264_v4l2m2m", HardwareDecoding.V4l2M2mDecoderName(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264));
        Assert.Equal("hevc_v4l2m2m", HardwareDecoding.V4l2M2mDecoderName(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_HEVC));
        Assert.Equal("mpeg2_v4l2m2m", HardwareDecoding.V4l2M2mDecoderName(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_MPEG2VIDEO));
        Assert.Null(HardwareDecoding.V4l2M2mDecoderName(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_PRORES));
        if (!FFmpegLoader.EnsureLoaded()) return;
        // 名前を間違えていないこと: FFmpeg のデコーダの一覧にある名前か (V4L2 なしで作った FFmpeg では見つからないので、Linux の CI でだけ確かめる)
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG") == "1")
            foreach (var id in new[] { FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_HEVC, FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_VP9 })
                Assert.True(FFmpeg.AutoGen.ffmpeg.avcodec_find_decoder_by_name(HardwareDecoding.V4l2M2mDecoderName(id)) != null, $"{HardwareDecoding.V4l2M2mDecoderName(id)} がありません");
    }

    [Fact]
    public void V4l2_decoding_is_tried_on_linux_and_falls_back_to_software_without_a_device()
    {
        if (!_ready || !OperatingSystem.IsLinux()) return;
        var software = ReadAll(HardwareDecodeMode.Off, out var off);
        off.Dispose();
        HardwareDecoding.PreferredDevice = HardwareDecoding.V4l2M2mName;
        Assert.Empty(HardwareDecoding.DeviceOrder());                   // hwaccel (VAAPI など) は試さない
        Assert.True(HardwareDecoding.TriesV4l2M2m);
        var frames = ReadAll(HardwareDecodeMode.Auto, out var r);
        using (r)
        {
            Console.WriteLine($"V4L2 here: {r.DecoderName} ({r.HardwareError ?? "-"})");
            if (r.IsHardware) Assert.Equal("V4L2", r.DecoderName);     // ARM の SoC など、デコードできる装置がある
            else Assert.Contains("V4L2", r.HardwareError);             // CI など: 装置がないので、ソフトウェアに戻る
            // H.264 のデコードは結果が決まっているので、どちらでも同じ絵になる
            for (int i = 0; i < Times.Length; i++) Assert.Equal(software[i], frames[i]);
        }
        if (r.IsHardware) return;
        // 開けなかったことは覚えておき、2 回目はすぐに諦める
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var again = new VideoReader(_clip)) Assert.False(again.IsHardware);
        Assert.Contains("V4L2", HardwareDecoding.Describe());
        Console.WriteLine($"2 回目: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Format_choice_prefers_the_gpu_format_and_otherwise_a_software_one()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        var vaapi = FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_VAAPI;
        var cuda = FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_CUDA;
        var yuv = FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV420P;
        var nv12 = FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_NV12;
        Assert.Equal(vaapi, HardwareDecoding.ChooseFormat(new[] { vaapi, yuv }, vaapi));
        Assert.Equal(yuv, HardwareDecoding.ChooseFormat(new[] { vaapi, yuv }, cuda));      // 望みの形式がない → ソフトウェア
        Assert.Equal(nv12, HardwareDecoding.ChooseFormat(new[] { cuda, nv12 }, FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_NONE));
    }

    [Fact]
    public unsafe void Device_failures_are_remembered_and_unknown_devices_are_skipped()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        foreach (var type in HardwareDecoding.DeviceOrder())
        {
            var first = HardwareDecoding.SharedDevice(type, out var e1);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var second = HardwareDecoding.SharedDevice(type, out var e2);
            Assert.True(first == second);
            Assert.Equal(e1, e2);
            Assert.True(sw.ElapsedMilliseconds < 50, "2 回目は覚えている結果を返す");
        }
        HardwareDecoding.PreferredDevice = "no-such-device";
        Assert.Empty(HardwareDecoding.DeviceOrder());
        Assert.False(HardwareDecoding.TriesV4l2M2m);                    // ほかの種類に絞ったら V4L2 も試さない
        if (!_ready) return;
        HardwareDecoding.Mode = HardwareDecodeMode.Auto;
        using var r = new VideoReader(_clip);
        Assert.False(r.IsHardware);
        Assert.NotNull(r.GetFrame(0.5));
    }
}

/// <summary>プロキシの生成と、プレビューでの差し替え。ProxyStore の設定 (静的な値) を書き換えるので、ほかと同時には動かさない。</summary>
[Collection("HardwareDecoding")]
public class ProxyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-proxy-{Guid.NewGuid():N}");
    private readonly string _clip;
    private readonly bool _ready;
    private readonly string _oldDirectory = ProxyStore.Directory;

    public ProxyTests()
    {
        Directory.CreateDirectory(_dir);
        ProxyStore.Directory = Path.Combine(_dir, "proxies");
        ProxyStore.Divisor = 2;
        ProxyStore.Enabled = true;
        _clip = Path.Combine(_dir, "clip.mp4");
        // 色が帯状に並ぶ 640x360 の動画 (フレームごとに模様が動く)
        _ready = FFmpegLoader.EnsureLoaded() && MediaTests.RunFfmpeg(
            $"-f lavfi -i testsrc2=size=640x360:rate=30:duration=2 -f lavfi -i sine=frequency=440:duration=2 -c:v libx264 -pix_fmt yuv420p -g 30 -c:a aac -shortest \"{_clip}\"");
    }

    public void Dispose()
    {
        ProxyStore.Directory = _oldDirectory;
        ProxyStore.Divisor = 2;
        ProxyStore.Enabled = true;
        ProxyStore.NotifyChanged();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string MakeProxy(int divisor = 2)
    {
        ProxyStore.Divisor = divisor;
        var path = ProxyStore.PathFor(_clip)!;
        ProxyGenerator.Create(_clip, path, divisor, null, CancellationToken.None);
        return path;
    }

    private static double MeanDiff(byte[] a, byte[] b)
    {
        long t = 0;
        for (int i = 0; i < a.Length; i++) t += Math.Abs(a[i] - b[i]);
        return (double)t / a.Length;
    }

    [Fact]
    public void Proxy_is_half_size_and_its_frames_line_up_with_the_original()
    {
        if (!_ready) return;
        var reported = new List<double>();
        ProxyStore.Divisor = 2;
        var folder = ProxyStore.PathFor(_clip)!;
        Assert.Null(ProxyStore.FindSegment(_clip, 0));
        ProxyGenerator.Create(_clip, folder, 2, new SyncProgress(reported.Add), CancellationToken.None);
        // 2 秒の動画は区切り 1 つ
        var path = ProxyStore.SegmentPath(folder, 0);
        Assert.Equal(path, ProxyStore.FindSegment(_clip, 0));
        Assert.Equal(path, ProxyStore.FindSegment(_clip, 1.9));
        Assert.True(ProxyStore.Exists(_clip));
        Assert.Equal(1.0, reported[^1]);
        Assert.Empty(Directory.GetFiles(folder, "*.part"));

        var info = MediaInfo.Probe(path)!;
        Assert.Equal((320, 180, "mjpeg"), (info.Width, info.Height, info.VideoCodec));
        Assert.False(info.HasAudio);
        Assert.InRange(info.Duration, 1.9, 2.1);

        // 同じ時刻のフレームが、元の動画を縮小したものとほぼ同じ (別の時刻のフレームとは違う)
        using var original = new VideoReader(_clip);
        using var proxy = new VideoReader(path, absoluteTime: true);
        byte[] Shrunk(double t)
        {
            using var src = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Opaque));
            System.Runtime.InteropServices.Marshal.Copy(original.GetFrame(t)!, 0, src.GetPixels(), 640 * 360 * 4);
            using var small = src.Resize(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Opaque), SKFilterQuality.Medium)!;
            return small.Bytes;
        }
        foreach (var t in new[] { 0.0, 0.5, 1.23, 1.9, 0.3 })
        {
            var expected = Shrunk(t);
            var fromProxy = proxy.GetFrame(t)!.ToArray();
            double same = MeanDiff(expected, fromProxy);
            Assert.True(same < 6, $"t={t}: 差が大きい {same:0.0}");
            // 1 秒ずれたフレームとは、同じ時刻のフレームよりはっきり違う (時刻の対応が合っている)
            double shifted = MeanDiff(Shrunk((t + 1) % 2), fromProxy);
            Assert.True(shifted > same * 2, $"t={t}: 同じ時刻 {same:0.00} / 1 秒ずれ {shifted:0.00}");
        }
    }

    [Fact]
    public void Cancelling_leaves_no_files_and_changed_sources_get_a_new_proxy_path()
    {
        if (!_ready) return;
        var path = ProxyStore.PathFor(_clip)!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ProxyGenerator.Create(_clip, path, 2, null, cts.Token));
        Assert.True(!Directory.Exists(path) || Directory.GetFiles(path).Length == 0);

        MakeProxy();
        Assert.NotNull(ProxyStore.FindSegment(_clip, 0));
        File.SetLastWriteTimeUtc(_clip, DateTime.UtcNow.AddMinutes(1));     // 元の動画が書き換えられた
        ProxyStore.NotifyChanged();
        Assert.NotEqual(path, ProxyStore.PathFor(_clip));
        Assert.Null(ProxyStore.FindSegment(_clip, 0));                     // 古いプロキシは使わない

        MakeProxy();
        Assert.Equal(1, ProxyStore.Delete(_clip));
        Assert.Null(ProxyStore.FindSegment(_clip, 0));
        Assert.False(Directory.Exists(ProxyStore.PathFor(_clip)));
    }

    [Fact]
    public void Preview_uses_the_proxy_at_the_original_size_and_export_never_does()
    {
        if (!_ready) return;
        // 目印として、プロキシの代わりに「真っ赤な動画」をプロキシの場所に置く
        ProxyStore.Divisor = 2;
        var folder = ProxyStore.PathFor(_clip)!;
        Directory.CreateDirectory(folder);
        var proxyPath = ProxyStore.SegmentPath(folder, 0);
        Assert.True(MediaTests.RunFfmpeg($"-f lavfi -i color=c=red:size=320x180:rate=30:duration=2 -c:v mjpeg -pix_fmt yuvj420p \"{proxyPath}\""));
        ProxyStore.NotifyChanged();

        var p = new Project();
        p.Settings.Width = 1280; p.Settings.Height = 720;
        var v = ObjectFactory.Create(EffectRegistry.Video, 0, 0, 60);
        v.Main.Values["file"] = _clip;
        p.Root.Objects.Add(v);

        SKColor Render(bool draft, int x, int y, out SKPoint[] quad)
        {
            using var e = new RenderEngine { Draft = draft };
            using var bmp = new SKBitmap(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));
            e.RenderToBitmap(p, p.Root, 10, bmp);
            quad = e.LastQuads.Single().Quad;
            return bmp.GetPixel(x, y);
        }

        var preview = Render(true, 640, 360, out var previewQuad);
        var final = Render(false, 640, 360, out var finalQuad);
        Assert.True(preview.Red > 200 && preview.Green < 60, $"プレビューはプロキシ (赤) を使う: {preview}");
        Assert.False(final.Red > 200 && final.Green < 60 && final.Blue < 60, "出力は元の動画を使う");
        // 小さいプロキシでも、画面上の大きさ・位置は元の動画と同じ (640x360 を中心に置く)
        Assert.Equal(finalQuad.Select(q => (Math.Round(q.X), Math.Round(q.Y))), previewQuad.Select(q => (Math.Round(q.X), Math.Round(q.Y))));
        Assert.Equal(new SKColor(0, 0, 0), Render(true, 310, 360, out _));      // 動画の外 (左端 320 より左) は黒のまま
        Assert.True(Render(true, 330, 360, out _).Red > 200);                    // 動画の左端の内側は赤

        // フィルタがあるときも同じ大きさ (元の大きさに戻してからフィルタをかける)
        ObjectFactory.AddFilter(v, "filter.border").Tracks["size"] = new AnimatedValue(10);
        Render(true, 640, 360, out var filtered);
        Render(false, 640, 360, out var filteredFinal);
        Assert.Equal(filteredFinal.Select(q => Math.Round(q.X)), filtered.Select(q => Math.Round(q.X)));

        // プロキシを使わない設定なら、プレビューも元の動画
        ProxyStore.Enabled = false;
        var off = Render(true, 640, 360, out _);
        Assert.False(off.Red > 200 && off.Green < 60 && off.Blue < 60);
    }

    [Fact]
    public void Heavy_videos_are_recognized_for_automatic_proxies()
    {
        Assert.True(ProxyStore.IsHeavy(new MediaInfo(true, true, 3840, 2160, 30, 10) { VideoCodec = "h264" }));
        Assert.True(ProxyStore.IsHeavy(new MediaInfo(true, true, 1920, 1080, 30, 10) { VideoCodec = "hevc" }));
        Assert.False(ProxyStore.IsHeavy(new MediaInfo(true, true, 1920, 1080, 30, 10) { VideoCodec = "h264" }));
        Assert.False(ProxyStore.IsHeavy(new MediaInfo(false, true, 0, 0, 0, 10)));
    }

    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}

/// <summary>デコーダの使い回し: 同じファイルを使うオブジェクトどうしでデコーダを貸し借りする。</summary>
public class DecoderSharingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-share-{Guid.NewGuid():N}");
    private readonly string _clip;
    private readonly bool _ready;

    public DecoderSharingTests()
    {
        Directory.CreateDirectory(_dir);
        _clip = Path.Combine(_dir, "clip.mp4");
        _ready = FFmpegLoader.EnsureLoaded() && MediaTests.RunFfmpeg(
            $"-f lavfi -i testsrc2=size=320x180:rate=30:duration=4 -c:v libx264 -pix_fmt yuv420p -g 120 \"{_clip}\"");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private Project WithVideo(params (int Layer, int Start, int Length, int Offset, double X)[] clips)
    {
        var p = new Project();
        p.Settings.Width = 640; p.Settings.Height = 360;
        foreach (var (layer, start, length, offset, x) in clips)
        {
            var v = ObjectFactory.Create(EffectRegistry.Video, layer, start, length);
            v.Main.Values["file"] = _clip;
            v.SourceOffsetFrames = offset;
            v.Draw!.Tracks["x"] = new AnimatedValue(x);
            p.Root.Objects.Add(v);
        }
        return p;
    }

    private static byte[] Frame(RenderEngine e, Project p, int f)
    {
        using var bmp = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
        e.RenderToBitmap(p, p.Root, f, bmp);
        return bmp.Bytes;
    }

    [Fact]
    public void A_split_clip_plays_across_the_cut_with_one_decoder_and_no_extra_seek()
    {
        if (!_ready) return;
        var whole = WithVideo((0, 0, 90, 0, 0));
        var split = WithVideo((0, 0, 45, 0, 0), (0, 45, 45, 45, 0));    // 45 フレーム目で分割したのと同じ
        using var a = new RenderEngine();
        using var b = new RenderEngine();
        for (int f = 0; f < 90; f++) Assert.Equal(Frame(a, whole, f), Frame(b, split, f));
        Assert.Equal(1, b.Media.ReaderCount);
        Assert.Equal(1, b.Media.TotalSeeks);          // 最初の 1 回だけ (つなぎ目でシークし直さない)
    }

    [Fact]
    public void Two_objects_of_the_same_file_shown_together_get_their_own_decoders_and_correct_frames()
    {
        if (!_ready) return;
        var both = WithVideo((0, 0, 60, 0, -160), (1, 0, 60, 60, 160));   // 同じ動画の 0 秒目と 2 秒目を並べる
        var left = WithVideo((0, 0, 60, 0, -160));
        var right = WithVideo((1, 0, 60, 60, 160));
        using var e = new RenderEngine();
        using var l = new RenderEngine();
        using var r = new RenderEngine();
        for (int f = 0; f < 60; f += 7)
        {
            var bothBytes = Frame(e, both, f);
            using var bm = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(bothBytes, 0, bm.GetPixels(), bothBytes.Length);
            var lb = Frame(l, left, f); var rb = Frame(r, right, f);
            // 左半分は「左だけ」と、右半分は「右だけ」と同じ
            for (int y = 0; y < 360; y += 37)
            {
                Assert.Equal(lb.AsSpan(y * 640 * 4 + 40 * 4, 4).ToArray(), bothBytes.AsSpan(y * 640 * 4 + 40 * 4, 4).ToArray());
                Assert.Equal(rb.AsSpan(y * 640 * 4 + 600 * 4, 4).ToArray(), bothBytes.AsSpan(y * 640 * 4 + 600 * 4, 4).ToArray());
            }
        }
        Assert.Equal(2, e.Media.ReaderCount);
        Assert.Equal(2, e.Media.TotalSeeks);          // それぞれ最初の 1 回だけ (毎フレーム取り合ってシークしない)
    }
}

public class AudioReaderSharingTests
{
    [Fact]
    public void Split_audio_mixes_the_same_samples_with_one_reader_and_loop_still_wraps()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-amix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var clip = Path.Combine(dir, "a.wav");
            if (!MediaTests.RunFfmpeg($"-f lavfi -i \"sine=frequency=330:sample_rate=48000:duration=3\" \"{clip}\"")) return;
            Project Make(params (int Start, int Length, int Offset)[] parts)
            {
                var p = new Project();
                foreach (var (start, length, offset) in parts)
                {
                    var a = ObjectFactory.Create(EffectRegistry.Audio, 0, start, length);
                    a.Main.Values["file"] = clip; a.SourceOffsetFrames = offset;
                    p.Root.Objects.Add(a);
                }
                return p;
            }
            var whole = Make((0, 90, 0));
            var split = Make((0, 40, 0), (40, 50, 40));
            using var m1 = new AudioMixer();
            using var m2 = new AudioMixer();
            var b1 = new float[1600 * 2]; var b2 = new float[1600 * 2];
            for (int f = 0; f < 90; f++)
            {
                m1.Mix(whole, whole.Root, f * 1600, b1);
                m2.Mix(split, split.Root, f * 1600, b2);
                Assert.Equal(b1, b2);
            }
            Assert.Equal(1, m2.ReaderCount);

            // ループ再生: 3 秒の素材を 4 秒置くと、3 秒目からは頭に戻る
            var loop = Make((0, 120, 0));
            loop.Root.Objects[0].Main.Values["loop"] = "1";
            using var m3 = new AudioMixer();
            var head = new float[1600 * 2]; var wrapped = new float[1600 * 2];
            m3.Mix(loop, loop.Root, 0, head);
            m3.Mix(loop, loop.Root, 48000 * 3, wrapped);
            Assert.Contains(wrapped, x => Math.Abs(x) > 0.01f);
            Assert.Equal(head, wrapped);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class AudioEffectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-afx-{Guid.NewGuid():N}");
    public AudioEffectTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Sine(string name, int freq, double seconds)
    {
        var path = Path.Combine(_dir, name);
        return MediaTests.RunFfmpeg($"-f lavfi -i \"sine=frequency={freq}:sample_rate=48000:duration={seconds}\" \"{path}\"") ? path : "";
    }

    private static TimelineObject Audio(Project p, string file, int layer, int start, int length)
    {
        var a = ObjectFactory.Create(EffectRegistry.Audio, layer, start, length);
        a.Main.Values["file"] = file;
        p.Root.Objects.Add(a);
        return a;
    }

    /// <summary>from 秒から seconds 秒ぶんを混ぜる (1600 サンプルずつ続けて)。</summary>
    private static float[] Render(Project p, double from, double seconds)
    {
        using var mixer = new AudioMixer();
        int total = (int)(seconds * 48000);
        var all = new float[total * 2];
        var buf = new float[1600 * 2];
        for (int i = 0; i < total; i += 1600)
        {
            mixer.Mix(p, p.Root, (long)(from * 48000) + i, buf);
            buf.AsSpan(0, Math.Min(buf.Length, all.Length - i * 2)).CopyTo(all.AsSpan(i * 2));
        }
        return all;
    }

    private static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var x in s) sum += x * x;
        return Math.Sqrt(sum / Math.Max(1, s.Length));
    }

    [Fact]
    public void Equalizer_boosts_only_the_chosen_band()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        var low = Sine("low.wav", 40, 1);   // シェルフの角 (100 Hz) より十分低い音
        if (low.Length == 0) return;
        var p = new Project();
        var a = Audio(p, low, 0, 0, 30);
        double plain = Rms(Render(p, 0.2, 0.5));
        var eq = ObjectFactory.AddFilter(a, "audio.eq");
        eq.Tracks["low"] = new AnimatedValue(12);
        double boosted = Rms(Render(p, 0.2, 0.5));
        Assert.InRange(boosted / plain, 3, 4.5);        // +12 dB ≒ 4 倍 (角の近くでは少し小さい)
        eq.Tracks["low"] = new AnimatedValue(0);
        eq.Tracks["high"] = new AnimatedValue(12);
        Assert.InRange(Rms(Render(p, 0.2, 0.5)) / plain, 0.9, 1.2);   // 40 Hz は高音のつまみでは変わらない
    }

    [Fact]
    public void Ducking_lowers_the_music_only_while_another_sound_plays()
    {
        if (!FFmpegLoader.EnsureLoaded()) return;
        var music = Sine("music.wav", 220, 4);
        var voice = Sine("voice.wav", 1000, 1);
        if (music.Length == 0 || voice.Length == 0) return;
        Project Make(bool duck)
        {
            var p = new Project();
            var m = Audio(p, music, 0, 0, 120);                 // 0〜4 秒
            Audio(p, voice, 1, 45, 30);                         // 1.5〜2.5 秒
            if (duck) ObjectFactory.AddFilter(m, "audio.duck");  // 既定: 12 dB 下げる
            return p;
        }
        var plain = Render(Make(false), 0, 4);
        var ducked = Render(Make(true), 0, 4);
        var diff = new float[plain.Length];
        for (int i = 0; i < diff.Length; i++) diff[i] = plain[i] - ducked[i];   // = 音楽 × (1 − 音量)
        double musicRms = Rms(plain.AsSpan(0, 48000 * 2));                       // 声のない所 = 音楽だけ
        double Seg(double a, double b) => Rms(diff.AsSpan((int)(a * 48000) * 2, (int)((b - a) * 48000) * 2));
        Assert.True(Seg(0.2, 1.4) < musicRms * 0.01);           // 声の前は下げない
        Assert.InRange(Seg(1.7, 2.4) / musicRms, 0.65, 0.85);   // 声の間は 12 dB (1/4) 下がる → 差は 3/4
        Assert.True(Seg(3.5, 3.9) < musicRms * 0.1);            // 声のあとは戻る (戻す速さ 0.4 秒の時定数で、1 秒後には 9 割以上)
    }
}

