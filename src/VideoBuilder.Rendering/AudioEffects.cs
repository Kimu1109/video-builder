using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>音声のエフェクト (組み込み + プラグイン) の実装の登録先。</summary>
public static partial class AudioEffects
{
    private static readonly Dictionary<string, IAudioEffect> Map = new();

    static AudioEffects()
    {
        Register(new Fade());
        Register(new Echo());
        Register(new Equalizer());
        RegisterMore();
    }

    /// <summary>自動音量調整 (ダッキング)。ほかの音声を見て音量を決めるので、AudioMixer が直接処理する。</summary>
    public const string DuckId = "audio.duck";

    public static void Register(IAudioEffect effect) { lock (Map) Map[effect.Id] = effect; }
    public static IAudioEffect? Find(string id) { lock (Map) return Map.GetValueOrDefault(id); }

    /// <summary>音量のフェードイン・フェードアウト (秒)。</summary>
    private sealed class Fade : IAudioEffect
    {
        public string Id => "audio.fade";

        public void Process(AudioBlock b, ref object? state)
        {
            double fin = b.Track("in") * b.SampleRate, fout = b.Track("out") * b.SampleRate;
            long first = b.ObjectSample, last = first + b.FrameCount;
            // このブロックがフェードにかからなければ何もしない
            if ((fin <= 0 || first >= fin) && (fout <= 0 || last <= b.ObjectLength - fout)) return;
            int curve = b.Effect.GetInt("curve");
            var s = b.Samples;
            for (int i = 0; i < b.FrameCount; i++)
            {
                long pos = first + i;
                double g = 1;
                if (fin > 0 && pos < fin) g = pos / fin;
                if (fout > 0 && b.ObjectLength - pos < fout) g = Math.Min(g, Math.Max(0, b.ObjectLength - pos) / fout);
                g = curve switch
                {
                    1 => Math.Sin(g * Math.PI / 2),      // 等パワー: 2 つを重ねたとき、音の強さ (2 乗の和) が一定
                    2 => g * g * (3 - 2 * g),            // なめらか (S 字)
                    _ => g,
                };
                s[i * 2] *= (float)g;
                s[i * 2 + 1] *= (float)g;
            }
        }
    }

    /// <summary>
    /// 3 バンドのイコライザ: 低音 (100 Hz・シェルフ)・中音 (1 kHz・ピーク)・高音 (8 kHz・シェルフ)。
    /// 双 2 次フィルタ (RBJ Audio EQ Cookbook) を左右別に持つ。
    /// </summary>
    private sealed class Equalizer : IAudioEffect
    {
        public string Id => "audio.eq";

        private struct Biquad
        {
            public double B0, B1, B2, A1, A2;
            public double X1L, X2L, Y1L, Y2L, X1R, X2R, Y1R, Y2R;

            public void Set(int kind, double freq, double gainDb, int rate)
            {
                double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * Math.Min(freq, rate * 0.45) / rate, cos = Math.Cos(w), sin = Math.Sin(w);
                double b0, b1, b2, a0, a1, a2;
                if (kind == 1)
                {
                    // ピーク (Q = 0.9)
                    double alpha = sin / (2 * 0.9);
                    b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                    a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                }
                else
                {
                    // シェルフ (S = 1)。kind 0 = 低音, 2 = 高音
                    double alpha = sin / 2 * Math.Sqrt(2), sq = 2 * Math.Sqrt(a) * alpha;
                    if (kind == 0)
                    {
                        b0 = a * ((a + 1) - (a - 1) * cos + sq); b1 = 2 * a * ((a - 1) - (a + 1) * cos); b2 = a * ((a + 1) - (a - 1) * cos - sq);
                        a0 = (a + 1) + (a - 1) * cos + sq; a1 = -2 * ((a - 1) + (a + 1) * cos); a2 = (a + 1) + (a - 1) * cos - sq;
                    }
                    else
                    {
                        b0 = a * ((a + 1) + (a - 1) * cos + sq); b1 = -2 * a * ((a - 1) + (a + 1) * cos); b2 = a * ((a + 1) + (a - 1) * cos - sq);
                        a0 = (a + 1) - (a - 1) * cos + sq; a1 = 2 * ((a - 1) - (a + 1) * cos); a2 = (a + 1) - (a - 1) * cos - sq;
                    }
                }
                B0 = b0 / a0; B1 = b1 / a0; B2 = b2 / a0; A1 = a1 / a0; A2 = a2 / a0;
            }

            public float Left(float x)
            {
                double y = B0 * x + B1 * X1L + B2 * X2L - A1 * Y1L - A2 * Y2L;
                X2L = X1L; X1L = x; Y2L = Y1L; Y1L = y;
                return (float)y;
            }

            public float Right(float x)
            {
                double y = B0 * x + B1 * X1R + B2 * X2R - A1 * Y1R - A2 * Y2R;
                X2R = X1R; X1R = x; Y2R = Y1R; Y1R = y;
                return (float)y;
            }
        }

        private sealed class State
        {
            public Biquad[] Bands = new Biquad[3];
        }

        private static readonly (string Key, int Kind, double Freq)[] BandDefs = { ("low", 0, 100), ("mid", 1, 1000), ("high", 2, 8000) };

        public void Process(AudioBlock b, ref object? state)
        {
            if (state is not State st || !b.Continuous) state = st = new State();
            var s = b.Samples;
            for (int k = 0; k < BandDefs.Length; k++)
            {
                double gain = b.Track(BandDefs[k].Key);
                if (Math.Abs(gain) < 0.01) continue;
                ref var f = ref st.Bands[k];
                // 係数は値が変わっても状態 (直前の値) を残して計算し直す (音が途切れないように)
                f.Set(BandDefs[k].Kind, BandDefs[k].Freq, gain, b.SampleRate);
                for (int i = 0; i < b.FrameCount; i++)
                {
                    s[i * 2] = f.Left(s[i * 2]);
                    s[i * 2 + 1] = f.Right(s[i * 2 + 1]);
                }
            }
        }
    }

    /// <summary>やまびこ。遅らせた音を繰り返し重ねる。</summary>
    private sealed class Echo : IAudioEffect
    {
        public string Id => "audio.echo";

        private sealed class State
        {
            public float[] Ring = Array.Empty<float>();
            public int Pos;
        }

        public void Process(AudioBlock b, ref object? state)
        {
            int delay = Math.Clamp((int)(b.Track("delay") / 1000.0 * b.SampleRate), 1, b.SampleRate * 2);
            float feedback = Math.Clamp(b.Track("feedback") / 100f, 0, 0.95f), mix = Math.Clamp(b.Track("mix") / 100f, 0, 1);
            if (state is not State st || !b.Continuous || st.Ring.Length != delay * 2)
            {
                // 最初・シークした後・遅延を変えたときは、残響を消してやり直す
                state = st = new State { Ring = new float[delay * 2] };
            }
            var s = b.Samples;
            var ring = st.Ring;
            int pos = st.Pos;
            for (int i = 0; i < s.Length; i++)
            {
                float delayed = ring[pos];
                float dry = s[i];
                ring[pos] = dry + delayed * feedback;
                s[i] = dry + delayed * mix;
                if (++pos == ring.Length) pos = 0;
            }
            st.Pos = pos;
        }
    }
}
