using System.Diagnostics;
using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// 直近の測定値 (ミリ秒など) を決まった数だけ覚えておき、平均・最大・95 パーセンタイルを出す。
/// どのスレッドから足しても読んでもよい。
/// </summary>
public sealed class RollingStats
{
    private readonly double[] _values;
    private int _next, _count;
    private long _total;

    public RollingStats(int capacity = 240) => _values = new double[Math.Max(1, capacity)];

    public void Add(double value)
    {
        lock (_values)
        {
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
            if (_count < _values.Length) _count++;
            _total++;
        }
    }

    public void Clear()
    {
        lock (_values) { _next = 0; _count = 0; _total = 0; }
    }

    /// <summary>今までに足した数 (覚えている数より多いことがある)。</summary>
    public long Total { get { lock (_values) return _total; } }

    /// <summary>覚えている値 (古い順)。</summary>
    public double[] Values
    {
        get
        {
            lock (_values)
            {
                var result = new double[_count];
                int start = (_next - _count + _values.Length) % _values.Length;
                for (int i = 0; i < _count; i++) result[i] = _values[(start + i) % _values.Length];
                return result;
            }
        }
    }

    public Summary Summarize()
    {
        var v = Values;
        if (v.Length == 0) return new Summary(0, 0, 0, 0, 0);
        var sorted = (double[])v.Clone();
        Array.Sort(sorted);
        double p95 = sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1)];
        return new Summary(v.Length, v.Average(), sorted[0], sorted[^1], p95) { Last = v[^1] };
    }

    public sealed record Summary(int Count, double Average, double Min, double Max, double P95)
    {
        public double Last { get; init; }
    }
}

/// <summary>
/// 出力にかかった時間の内訳。<see cref="Exporter.Run"/> が書き込む (出力の途中でも読める)。
/// 描画・変換・音声は描画のスレッド、エンコードはエンコードのスレッドで進むので、合計は全体の時間より長くなることがある。
/// </summary>
public sealed class ExportStats
{
    private long _render, _convert, _audio, _encode, _wait, _frames;
    private readonly Stopwatch _clock = new();

    public int TotalFrames { get; internal set; }
    /// <summary>書き終えたフレーム数。</summary>
    public long Frames => Interlocked.Read(ref _frames);
    /// <summary>フレームの描画 (合成) の合計。</summary>
    public TimeSpan Render => TimeSpan.FromTicks(Interlocked.Read(ref _render));
    /// <summary>描いた絵をエンコーダの形式 (YUV) に変える合計。連番 PNG では PNG への圧縮。</summary>
    public TimeSpan Convert => TimeSpan.FromTicks(Interlocked.Read(ref _convert));
    /// <summary>音声のミックスの合計。</summary>
    public TimeSpan Audio => TimeSpan.FromTicks(Interlocked.Read(ref _audio));
    /// <summary>エンコード (圧縮して書き込む) の合計。</summary>
    public TimeSpan Encode => TimeSpan.FromTicks(Interlocked.Read(ref _encode));
    /// <summary>エンコードが追いつかず、描画側が待った合計 (大きければエンコードが遅い)。</summary>
    public TimeSpan WaitForEncoder => TimeSpan.FromTicks(Interlocked.Read(ref _wait));
    /// <summary>始めてからの時間 (終わったあとは出力全体の時間)。</summary>
    public TimeSpan Elapsed => _clock.Elapsed;
    public bool Finished { get; internal set; }

    /// <summary>1 秒あたりに書けたフレーム数。</summary>
    public double FramesPerSecond => Elapsed.TotalSeconds > 0 ? Frames / Elapsed.TotalSeconds : 0;

    internal void Start(int total) { TotalFrames = total; _clock.Restart(); }
    internal void Stop() { _clock.Stop(); Finished = true; }
    internal void AddRender(TimeSpan t) => Interlocked.Add(ref _render, t.Ticks);
    internal void AddConvert(TimeSpan t) => Interlocked.Add(ref _convert, t.Ticks);
    internal void AddAudio(TimeSpan t) => Interlocked.Add(ref _audio, t.Ticks);
    internal void AddEncode(TimeSpan t) => Interlocked.Add(ref _encode, t.Ticks);
    internal void AddWait(TimeSpan t) => Interlocked.Add(ref _wait, t.Ticks);
    internal void FrameDone() => Interlocked.Increment(ref _frames);
}

/// <summary>シーンを続けて描画して、描画の速さを測る (プレビューと同じ描き方)。</summary>
public static class RenderBenchmark
{
    public sealed record Result(int Frames, int Width, int Height, bool Gpu, RollingStats.Summary Milliseconds, TimeSpan Elapsed)
    {
        /// <summary>1 秒あたりに描けるフレーム数。</summary>
        public double FramesPerSecond => Elapsed.TotalSeconds > 0 ? Frames / Elapsed.TotalSeconds : 0;
    }

    /// <summary>
    /// scene の start から count フレームを、width x height で描く (count がシーンより長ければ先頭に戻る)。
    /// 最初の 1 フレームは準備 (素材を開くなど) を含むので、結果に入れない。
    /// </summary>
    public static Result Run(Project project, Scene scene, int start, int count, int width, int height, bool useGpu, bool draft,
        IProgress<int>? progress, CancellationToken cancel)
    {
        // GPU コンテキストは作ったスレッドでしか使えないので、この関数の中で作って捨てる
        using var gpu = useGpu ? GpuContext.TryCreate(out _) : null;
        using var engine = new RenderEngine(gpu) { Draft = draft };
        using var bitmap = new SKBitmap(new SKImageInfo(Math.Max(2, width), Math.Max(2, height), SKColorType.Bgra8888, SKAlphaType.Premul));
        int length = Math.Max(1, scene.Length);
        engine.RenderToBitmap(project, scene, start % length, bitmap);
        var stats = new RollingStats(Math.Max(1, count));
        var all = Stopwatch.StartNew();
        var one = new Stopwatch();
        for (int i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            one.Restart();
            engine.RenderToBitmap(project, scene, (start + i) % length, bitmap);
            stats.Add(one.Elapsed.TotalMilliseconds);
            progress?.Report(i + 1);
        }
        return new Result(count, bitmap.Width, bitmap.Height, gpu != null, stats.Summarize(), all.Elapsed);
    }
}
