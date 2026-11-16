using VideoBuilder.Core;
using VideoBuilder.Media;

namespace VideoBuilder.Rendering;

/// <summary>シーン内の音声オブジェクトを混ぜて、ステレオ float のサンプル列を作る。</summary>
public sealed class AudioMixer : IDisposable
{
    private sealed class Entry
    {
        public AudioReader? Reader;
        public string Path = "";
    }

    private readonly Dictionary<string, Entry> _readers = new();
    private readonly object _gate = new();
    private float[] _temp = Array.Empty<float>();

    /// <summary>startSample から dst.Length / 2 フレーム分を dst に書く (上書き)。</summary>
    public void Mix(Project project, Scene scene, long startSample, Span<float> dst)
    {
        lock (_gate)
        {
            dst.Clear();
            int frames = dst.Length / 2;
            int rate = project.Settings.SampleRate;
            long endSample = startSample + frames;
            var fps = project.Settings.Fps;
            if (fps.Num == 0) return;
            int frame = (int)(startSample * fps.Num / ((long)rate * fps.Den));

            foreach (var obj in scene.Objects)
            {
                if (!obj.Enabled || obj.Effects.Count == 0 || obj.Main.DefinitionId != EffectRegistry.Audio) continue;
                if (!scene.GetLayer(obj.Layer).Visible) continue;
                long objStart = project.FrameToSample(obj.Start), objEnd = project.FrameToSample(obj.End + 1);
                long from = Math.Max(startSample, objStart), to = Math.Min(endSample, objEnd);
                if (to <= from) continue;

                var reader = GetReader(obj.Id, obj.Main.GetString("file"), rate);
                if (reader == null) continue;

                var bounds = obj.Bounds;
                int f = Math.Clamp(frame, obj.Start, obj.End);
                double speed = Math.Max(0.01, obj.Main.Eval("speed", bounds, f) / 100.0);
                double offset = (obj.Main.Eval("position", bounds, obj.Start) + project.FrameToSeconds(obj.SourceOffsetFrames)) * rate;
                bool loop = obj.Main.GetBool("loop");
                long total = (long)(reader.Duration * rate);

                float volume = 1, pan = 0;
                var play = obj.FindEffect(EffectRegistry.StandardPlay);
                if (play != null)
                {
                    volume = (float)play.Eval("volume", bounds, f) / 100f;
                    pan = (float)play.Eval("pan", bounds, f) / 100f;
                }
                float gl = volume * Math.Min(1, 1 - pan), gr = volume * Math.Min(1, 1 + pan);

                int count = (int)(to - from);
                double srcPos = offset + (from - objStart) * speed;
                if (loop && total > 0) srcPos %= total;
                int srcCount = (int)Math.Ceiling(count * speed) + 2;
                if (_temp.Length < srcCount * 2) _temp = new float[srcCount * 2];
                var temp = _temp.AsSpan(0, srcCount * 2);
                long srcStart = (long)Math.Floor(srcPos);
                reader.Read(srcStart, temp);

                var o = dst.Slice((int)(from - startSample) * 2, count * 2);
                if (Math.Abs(speed - 1) < 1e-9)
                {
                    for (int i = 0; i < count; i++)
                    {
                        o[i * 2] += temp[i * 2] * gl;
                        o[i * 2 + 1] += temp[i * 2 + 1] * gr;
                    }
                }
                else
                {
                    double frac0 = srcPos - srcStart;
                    for (int i = 0; i < count; i++)
                    {
                        double sp = frac0 + i * speed;
                        int k = (int)sp;
                        float t = (float)(sp - k);
                        o[i * 2] += (temp[k * 2] + (temp[k * 2 + 2] - temp[k * 2]) * t) * gl;
                        o[i * 2 + 1] += (temp[k * 2 + 1] + (temp[k * 2 + 3] - temp[k * 2 + 1]) * t) * gr;
                    }
                }
            }
            for (int i = 0; i < dst.Length; i++) dst[i] = Math.Clamp(dst[i], -1f, 1f);
        }
    }

    private AudioReader? GetReader(string key, string path, int rate)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_readers.TryGetValue(key, out var e) && e.Path == path) return e.Reader;
        e?.Reader?.Dispose();
        e = new Entry { Path = path };
        try { e.Reader = File.Exists(path) ? new AudioReader(path, rate) : null; }
        catch { e.Reader = null; }
        _readers[key] = e;
        return e.Reader;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var e in _readers.Values) e.Reader?.Dispose();
            _readers.Clear();
        }
    }
}
