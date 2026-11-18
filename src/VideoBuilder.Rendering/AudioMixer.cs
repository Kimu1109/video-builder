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
        /// <summary>前回読んだ範囲の終わり (サンプル)。ここから読めばシークしなくてよい。</summary>
        public long NextSample = -1;
        public long UsedInMix = -1;
        public long LastUsed;
    }

    // 読み手はファイルごとに持ち、オブジェクトには Mix のたびに貸す (MediaCache と同じ考え方)。
    // 分割した音声のつなぎ目でも、前のオブジェクトの読み手をそのまま続きから使えるので、開き直し・シークをしない
    private readonly Dictionary<string, List<Entry>> _readers = new();
    private long _mix, _tick;
    private const int MaxReaders = 16;

    /// <summary>開いている読み手の数 (計測・テスト用)。</summary>
    public int ReaderCount { get { lock (_gate) return _readers.Values.Sum(l => l.Count); } }
    private readonly object _gate = new();
    private float[] _temp = Array.Empty<float>();

    /// <summary>startSample から dst.Length / 2 フレーム分を dst に書く (上書き)。</summary>
    public void Mix(Project project, Scene scene, long startSample, Span<float> dst)
    {
        lock (_gate)
        {
            dst.Clear();
            _mix++;
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


                var bounds = obj.Bounds;
                int f = Math.Clamp(frame, obj.Start, obj.End);
                double speed = Math.Max(0.01, obj.Main.Eval("speed", bounds, f) / 100.0);
                double offset = (obj.Main.Eval("position", bounds, obj.Start) + project.FrameToSeconds(obj.SourceOffsetFrames)) * rate;
                bool loop = obj.Main.GetBool("loop");
                string file = obj.Main.GetString("file");
                long total = loop ? (long)(DurationOf(file, rate) * rate) : 0;

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
                var entry = GetReader(file, rate, srcStart);
                if (entry?.Reader == null) continue;
                entry.Reader.Read(srcStart, temp);
                entry.NextSample = srcStart + (long)Math.Floor(count * speed);

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

    /// <summary>このオブジェクトに貸す読み手を選ぶ。続きから読めるもの → 使われていないもの → 新しく開く、の順。</summary>
    private Entry? GetReader(string path, int rate, long start)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!_readers.TryGetValue(path, out var list)) _readers[path] = list = new List<Entry>();
        Entry? best = null;
        long bestCost = long.MaxValue;
        foreach (var e in list)
        {
            if (e.UsedInMix == _mix) continue;
            long cost = e.NextSample >= 0 && start >= e.NextSample - 1 && start - e.NextSample <= rate ? start - e.NextSample : long.MaxValue / 2 - e.LastUsed;
            if (cost < bestCost) { best = e; bestCost = cost; }
        }
        if (best == null)
        {
            best = new Entry { Path = path };
            try { best.Reader = File.Exists(path) ? new AudioReader(path, rate) : null; }
            catch { best.Reader = null; }
            list.Add(best);
            Trim();
        }
        best.UsedInMix = _mix;
        best.LastUsed = ++_tick;
        return best;
    }

    private readonly Dictionary<string, double> _durations = new();

    /// <summary>ファイルの長さ (秒)。ループ再生の折り返しに使う。開けなければ 0。</summary>
    private double DurationOf(string path, int rate)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        if (_durations.TryGetValue(path, out var d)) return d;
        if (_readers.TryGetValue(path, out var list) && list.FirstOrDefault(e => e.Reader != null) is { } open) d = open.Reader!.Duration;
        else
        {
            // まだ開いていなければ開いておく (このあと GetReader でそのまま使われる)
            var e = new Entry { Path = path };
            try { e.Reader = File.Exists(path) ? new AudioReader(path, rate) : null; }
            catch { e.Reader = null; }
            if (!_readers.TryGetValue(path, out list)) _readers[path] = list = new List<Entry>();
            list.Add(e);
            d = e.Reader?.Duration ?? 0;
        }
        _durations[path] = d;
        return d;
    }

    private void Trim()
    {
        while (_readers.Values.Sum(l => l.Count) > MaxReaders)
        {
            var oldest = _readers.SelectMany(kv => kv.Value.Select(e => (kv.Key, e))).Where(x => x.e.UsedInMix != _mix).MinBy(x => x.e.LastUsed);
            if (oldest.e == null) return;
            oldest.e.Reader?.Dispose();
            _readers[oldest.Key].Remove(oldest.e);
            if (_readers[oldest.Key].Count == 0) _readers.Remove(oldest.Key);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var e in _readers.Values.SelectMany(l => l)) e.Reader?.Dispose();
            _readers.Clear();
        }
    }
}
