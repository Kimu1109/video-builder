using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>音声のエフェクト (組み込み + プラグイン) の実装の登録先。</summary>
public static class AudioEffects
{
    private static readonly Dictionary<string, IAudioEffect> Map = new();

    static AudioEffects()
    {
        Register(new Fade());
        Register(new Echo());
    }

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
            var s = b.Samples;
            for (int i = 0; i < b.FrameCount; i++)
            {
                long pos = first + i;
                double g = 1;
                if (fin > 0 && pos < fin) g = pos / fin;
                if (fout > 0 && b.ObjectLength - pos < fout) g = Math.Min(g, Math.Max(0, b.ObjectLength - pos) / fout);
                s[i * 2] *= (float)g;
                s[i * 2 + 1] *= (float)g;
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
