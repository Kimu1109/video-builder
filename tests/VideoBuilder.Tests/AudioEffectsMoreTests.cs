using VideoBuilder.Core;
using VideoBuilder.Plugins;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>追加した音声のエフェクト (リバーブ・ダイナミクス・ノイズ除去・ピッチ・フィルタ・ラウドネス) とフェードのカーブ。</summary>
public class AudioEffectsMoreTests
{
    private const int Rate = 48000;

    /// <summary>samples (ステレオ) に、effectId のエフェクトを 1024 サンプルずつ続けてかける。</summary>
    private static float[] Run(string effectId, float[] samples, Action<Effect>? setup = null)
    {
        var p = new Project();
        p.Settings.SampleRate = Rate;
        var o = ObjectFactory.Create(EffectRegistry.Audio, 0, 0, Math.Max(2, (int)Math.Ceiling(samples.Length / 2.0 / Rate * 30)));
        var e = ObjectFactory.AddFilter(o, effectId);
        setup?.Invoke(e);
        var fx = AudioEffects.Find(effectId)!;
        object? state = null;
        var data = (float[])samples.Clone();
        long total = data.Length / 2;
        for (long pos = 0; pos < total; pos += 1024)
        {
            int n = (int)Math.Min(1024, total - pos);
            var block = new AudioBlock
            {
                Samples = data.AsSpan((int)pos * 2, n * 2), SampleRate = Rate, ObjectSample = pos, ObjectLength = total,
                Continuous = pos > 0, Object = o, Effect = e, Project = p, Frame = 0,
            };
            fx.Process(block, ref state);
        }
        return data;
    }

    private static float[] Sine(double freq, double seconds, double amp)
    {
        int n = (int)(seconds * Rate);
        var s = new float[n * 2];
        for (int i = 0; i < n; i++) s[i * 2] = s[i * 2 + 1] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / Rate));
        return s;
    }

    private static float[] Noise(double seconds, double amp, int seed = 1)
    {
        var rnd = new Random(seed);
        int n = (int)(seconds * Rate);
        var s = new float[n * 2];
        for (int i = 0; i < s.Length; i++) s[i] = (float)((rnd.NextDouble() * 2 - 1) * amp);
        return s;
    }

    /// <summary>[from 秒, to 秒) の、左右の 2 乗平均の平方根。</summary>
    private static double Rms(float[] s, double from, double to)
    {
        int a = (int)(from * Rate) * 2, b = Math.Min(s.Length, (int)(to * Rate) * 2);
        double sum = 0;
        for (int i = a; i < b; i++) sum += s[i] * s[i];
        return Math.Sqrt(sum / Math.Max(1, b - a));
    }

    private static double Db(double v) => 20 * Math.Log10(Math.Max(1e-9, v));

    [Fact]
    public void Equal_power_fade_keeps_the_crossfade_level()
    {
        var ones = new float[Rate * 2 * 2];
        Array.Fill(ones, 1f);
        // 2 秒のオブジェクトに 2 秒のフェードイン: 真ん中 (1 秒) で、直線は 0.5、等パワーは 0.707
        var linear = Run("audio.fade", ones, e => { e.Tracks["in"] = new AnimatedValue(2); e.Tracks["out"] = new AnimatedValue(0); });
        var power = Run("audio.fade", ones, e => { e.Tracks["in"] = new AnimatedValue(2); e.Tracks["out"] = new AnimatedValue(0); e.Values["curve"] = "1"; });
        Assert.Equal(0.5, linear[Rate * 2], 2);
        Assert.Equal(Math.Sqrt(0.5), power[Rate * 2], 2);
    }

    [Fact]
    public void Reverb_leaves_a_tail_after_the_sound_stops()
    {
        var s = new float[Rate * 2 * 2];
        for (int i = 0; i < Rate / 10 * 2; i++) s[i] = 0.5f;   // 100ms の音のあとは無音
        var wet = Run("audio.reverb", s);
        Assert.True(Rms(wet, 0.3, 0.8) > 0.003, "残響がない: " + Rms(wet, 0.3, 0.8));
        Assert.True(Rms(wet, 1.8, 2.0) < Rms(wet, 0.3, 0.5), "残響が減っていかない");
    }

    [Fact]
    public void Compressor_limiter_and_gate_shape_the_level()
    {
        // コンプレッサー: −6 dB の音を、しきい値 −20 dB・圧縮比 4 で → −20 + 14/4 = −16.5 dB 付近 (ピーク)
        Assert.Equal(10.5, AudioEffects.CompressorReduction(-6, -20, 4, 0), 3);
        var loud = Sine(440, 1, 0.5);
        var comp = Run("audio.compressor", loud, e => e.Tracks["knee"] = new AnimatedValue(0));
        double peak = comp.Skip(Rate).Max(Math.Abs);
        Assert.InRange(Db(peak), -18, -15);

        // リミッター: 入力を +6 dB 上げても、上限 −1 dB を超えない
        var lim = Run("audio.limiter", loud, e => e.Tracks["gain"] = new AnimatedValue(6));
        Assert.True(lim.Max(Math.Abs) <= Math.Pow(10, -1 / 20.0) + 1e-6);
        Assert.True(Db(lim.Skip(Rate / 2).Max(Math.Abs)) > -2, "下げすぎている");

        // ノイズゲート: −60 dB の雑音は 30 dB 下がり、−6 dB の音はそのまま
        var hiss = Run("audio.gate", Noise(1, 0.001));
        Assert.True(Db(Rms(hiss, 0.5, 1)) < Db(0.001 / Math.Sqrt(3)) - 25, "雑音が下がっていない");
        var voice = Run("audio.gate", loud);
        Assert.Equal(Rms(loud, 0.2, 1), Rms(voice, 0.2, 1), 3);
    }

    [Fact]
    public void Denoise_removes_steady_noise_but_keeps_the_tone()
    {
        var noise = Noise(3, 0.05);
        var cleaned = Run("audio.denoise", noise, e => e.Tracks["amount"] = new AnimatedValue(100));
        Assert.True(Db(Rms(cleaned, 2, 3)) < Db(Rms(noise, 2, 3)) - 10, $"雑音が減っていない: {Db(Rms(noise, 2, 3)):F1} → {Db(Rms(cleaned, 2, 3)):F1} dB");
        // 雑音の上で、0.25 秒ごとに鳴ったり止んだりする音 (話し声のように): 鳴っている所は残り、止んでいる所の雑音は減る
        // (ずっと同じ強さで鳴り続ける音は、ハムのような雑音として消える)
        var mixed = Noise(3, 0.02, seed: 2);
        for (int i = 0; i < mixed.Length / 2; i++)
            if ((i / (Rate / 4)) % 2 == 0) { float v = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / Rate)); mixed[i * 2] += v; mixed[i * 2 + 1] += v; }
        var kept = Run("audio.denoise", mixed);
        // 2.0〜2.25 秒は鳴っている、2.25〜2.5 秒は止んでいる (処理の遅れ 1024 サンプルぶん、少し内側を測る)
        Assert.True(Rms(kept, 2.05, 2.2) > 0.3 / Math.Sqrt(2) * 0.8, "音まで消えている: " + Rms(kept, 2.05, 2.2));
        Assert.True(Db(Rms(kept, 2.3, 2.45)) < Db(Rms(mixed, 2.3, 2.45)) - 6, "止んでいる所の雑音が減っていない");
    }

    [Fact]
    public void Pitch_and_filters_change_the_frequency_content()
    {
        static int Crossings(float[] s, double from, double to)
        {
            int n = 0;
            for (int i = (int)(from * Rate) + 1; i < (int)(to * Rate); i++) if ((s[(i - 1) * 2] < 0) != (s[i * 2] < 0)) n++;
            return n;
        }
        var tone = Sine(220, 2, 0.5);
        var up = Run("audio.pitch", tone, e => e.Tracks["semitones"] = new AnimatedValue(12));
        // 1 オクターブ上: 0 をまたぐ回数がほぼ 2 倍
        double ratio = Crossings(up, 0.5, 1.5) / (double)Crossings(tone, 0.5, 1.5);
        Assert.InRange(ratio, 1.8, 2.2);

        // ローパス 500 Hz: 5 kHz は 20 dB 以上小さく、100 Hz はほぼそのまま
        var high = Run("audio.filter", Sine(5000, 1, 0.5), e => e.Tracks["frequency"] = new AnimatedValue(500));
        Assert.True(Db(Rms(high, 0.5, 1)) < Db(0.5 / Math.Sqrt(2)) - 20);
        var low = Run("audio.filter", Sine(100, 1, 0.5), e => e.Tracks["frequency"] = new AnimatedValue(500));
        Assert.True(Db(Rms(low, 0.5, 1)) > Db(0.5 / Math.Sqrt(2)) - 1);
        // ハイパス 500 Hz (急な傾き): 100 Hz は大きく下がる
        var cut = Run("audio.filter", Sine(100, 1, 0.5), e => { e.Values["type"] = "1"; e.Values["steep"] = "1"; e.Tracks["frequency"] = new AnimatedValue(500); });
        Assert.True(Db(Rms(cut, 0.5, 1)) < Db(0.5 / Math.Sqrt(2)) - 40);
    }

    [Fact]
    public void Loudness_is_measured_with_k_weighting_and_gating()
    {
        // 1 kHz・振幅 0.1 (−20 dBFS) の音が左右に 3 秒: 約 −20 LUFS
        var tone = Sine(1000, 3, 0.1);
        double lufs = AudioEffects.IntegratedLoudness(start =>
        {
            var buf = new float[Rate / 10 * 2];
            int from = (int)start * 2;
            Array.Copy(tone, from, buf, 0, Math.Min(buf.Length, tone.Length - from));
            return buf;
        }, tone.Length / 2, Rate);
        Assert.InRange(lufs, -21, -19);
        // 無音は測れない (−∞)
        Assert.True(double.IsNegativeInfinity(AudioEffects.IntegratedLoudness(_ => new float[Rate / 10 * 2], Rate * 2, Rate)));
    }

    [Fact]
    public void Loudness_normalization_reads_the_file_and_reaches_the_target()
    {
        // 音声ファイルを読むには FFmpeg のライブラリが要る (ほかのメディアのテストと同じく、なければ飛ばす。CI では必須)
        if (!VideoBuilder.Media.FFmpegLoader.EnsureLoaded())
        {
            if (Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG") == "1") throw new InvalidOperationException("FFmpeg が使えません: " + VideoBuilder.Media.FFmpegLoader.Error);
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "vb-lufs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 1 kHz・振幅 0.05 (約 −26 LUFS) の 16 ビット WAV を 3 秒
            string path = Path.Combine(dir, "quiet.wav");
            int n = Rate * 3;
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 4); w.Write("WAVE"u8.ToArray());
                w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
                w.Write("data"u8.ToArray()); w.Write(n * 4);
                for (int i = 0; i < n; i++) { short v = (short)(0.05 * 32767 * Math.Sin(2 * Math.PI * 1000 * i / Rate)); w.Write(v); w.Write(v); }
            }
            var p = new Project();
            p.Settings.SampleRate = Rate;
            var a = ObjectFactory.Create(EffectRegistry.Audio, 0, 0, 90);
            a.Main.Values["file"] = path;
            ObjectFactory.AddFilter(a, "audio.loudness").Tracks["target"] = new AnimatedValue(-14);
            p.Root.Objects.Add(a);
            using var mixer = new AudioMixer();
            var buf = new float[Rate * 2];
            mixer.Mix(p, p.Root, Rate, buf);   // 1〜2 秒
            // −26 → −14 LUFS: 12 dB 上がる (振幅 0.05 → 約 0.2)
            double rms = Math.Sqrt(buf.Average(v => (double)v * v));
            Assert.InRange(Db(rms * Math.Sqrt(2)), Db(0.2) - 1, Db(0.2) + 1);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
