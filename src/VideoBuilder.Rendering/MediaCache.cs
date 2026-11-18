using SkiaSharp;
using VideoBuilder.Media;

namespace VideoBuilder.Rendering;

/// <summary>動画デコーダと画像の置き場。1 つの RenderEngine (= 1 スレッド) から使う。</summary>
public sealed class MediaCache : IDisposable
{
    private sealed class VideoEntry
    {
        public VideoReader? Reader;
        public SKImage? Image;
        public long FrameId = long.MinValue;
        public string Path = "";
        public long LastUsed;
        /// <summary>プロキシを読んでいるときの、元の動画の幅 (元の動画を読んでいるときは 0)。</summary>
        public int SourceWidth;
        /// <summary>最後に取り出した位置 (秒)。まだなら NaN。</summary>
        public double Position = double.NaN;
        /// <summary>最後に使った描画の番号。同じ描画の中で、別のオブジェクトに貸さないために使う。</summary>
        public long UsedInFrame = -1;
    }

    // デコーダはファイルごとに持ち、オブジェクトには描画のたびに「いちばん都合のよいもの」を貸す。
    // 分割したクリップのように、同じファイルの続きを別のオブジェクトが使うときも、
    // 前のオブジェクトが使っていたデコーダをそのまま続きから使えるので、つなぎ目でシークし直さずに済む。
    private readonly Dictionary<string, List<VideoEntry>> _byPath = new();
    private readonly Dictionary<string, VideoEntry> _assigned = new();
    private readonly Dictionary<string, (SKImage? Image, DateTime Stamp)> _images = new();
    private long _tick;
    private long _frame;
    private const int MaxReaders = 24;
    /// <summary>この秒数より先なら、シークせずに読み進めた方が速い (VideoReader の判断と同じ)。</summary>
    private const double SequentialWindow = 2.0;
    private readonly bool _zeroCopy;

    /// <param name="zeroCopy">
    /// 動画フレームを複製せず、デコーダのバッファを直接指す画像を作る。
    /// 画像は「次のフレームを取り出すまで」しか正しくないので、すぐに描き切る CPU 描画でだけ使う。
    /// </param>
    public MediaCache(bool zeroCopy = false) => _zeroCopy = zeroCopy;

    /// <summary>プレビュー用: プロキシがあれば元の動画の代わりに使う。</summary>
    public bool UseProxies { get; set; }

    /// <summary>今開いているデコーダの数 (計測・テスト用)。</summary>
    public int ReaderCount => _byPath.Values.Sum(l => l.Count);
    /// <summary>開いているデコーダがこれまでにシークした回数の合計 (計測・テスト用)。</summary>
    public int TotalSeeks => _byPath.Values.SelectMany(l => l).Sum(e => e.Reader?.SeekCount ?? 0);

    /// <summary>新しいフレームの描画を始める。RenderEngine が描画のはじめに呼ぶ。</summary>
    public void BeginFrame()
    {
        _frame++;
        _assigned.Clear();
    }

    public SKImage? GetVideoFrame(string key, string path, double seconds, bool loop) =>
        GetVideoFrame(key, path, seconds, loop, out _);

    /// <param name="key">オブジェクトの ID。同じ描画の中で、先に読んでおいた (Prefetch) フレームを見つけるのに使う。</param>
    /// <param name="scale">返した画像の 1 ピクセルが、元の動画の何ピクセルにあたるか (プロキシなら 2 や 4)。</param>
    public SKImage? GetVideoFrame(string key, string path, double seconds, bool loop, out float scale)
    {
        scale = 1;
        var e = Acquire(key, EffectivePath(path), path, seconds, loop);
        var image = e == null ? null : Decode(e, seconds, loop);
        if (image != null && e!.Path != path && e.SourceWidth > 0) scale = e.SourceWidth / (float)image.Width;
        return image;
    }

    /// <summary>実際に読むファイル (プロキシを使う設定でプロキシがあれば、プロキシ)。</summary>
    private string EffectivePath(string path) =>
        UseProxies && ProxyStore.Enabled && ProxyStore.FindReady(path) is { } proxy ? proxy : path;

    /// <summary>
    /// 複数の動画のフレームを、動画ごとに別スレッドで同時にデコードしておく。
    /// このあとの GetVideoFrame は、デコード済みのフレームをすぐ返す。
    /// </summary>
    public void Prefetch(IReadOnlyList<(string Key, string Path, double Seconds, bool Loop)> requests)
    {
        // デコーダの割り当て (一覧の書き換え) は 1 スレッドで済ませ、デコードだけを並列にする
        var jobs = new List<(VideoEntry Entry, double Seconds, bool Loop)>();
        foreach (var (key, path, seconds, loop) in requests)
        {
            var e = Acquire(key, EffectivePath(path), path, seconds, loop);
            if (e?.Reader != null && jobs.All(j => !ReferenceEquals(j.Entry, e))) jobs.Add((e, seconds, loop));
        }
        if (jobs.Count < 2) return;
        Parallel.ForEach(jobs, job => Decode(job.Entry, job.Seconds, job.Loop));
    }

    private static double Normalize(VideoEntry e, double seconds, bool loop) =>
        loop && e.Reader is { Duration: > 0 } r ? seconds % r.Duration : seconds;

    /// <summary>
    /// このオブジェクトに貸すデコーダを決める。同じ描画の中では、1 つのデコーダを 2 つのオブジェクトに貸さない。
    /// 選び方: 少し先まで読み進めれば届くもの (シーク不要) → それ以外で使われていないもの → 新しく開く。
    /// </summary>
    /// <param name="original">path がプロキシのときの、元の動画のパス。</param>
    private VideoEntry? Acquire(string key, string path, string original, double seconds, bool loop)
    {
        if (string.IsNullOrEmpty(path)) return null;
        // 同じ描画の中で、先に割り当てたもの (Prefetch) があればそれを使う
        if (_assigned.TryGetValue(key, out var mine) && mine.Path == path && Math.Abs(mine.Position - Normalize(mine, seconds, loop)) < 1e-9)
            return mine;

        if (!_byPath.TryGetValue(path, out var list)) _byPath[path] = list = new List<VideoEntry>();
        VideoEntry? best = null;
        double bestCost = double.MaxValue;
        foreach (var e in list)
        {
            if (e.UsedInFrame == _frame) continue;
            double t = Normalize(e, seconds, loop);
            double cost;
            if (double.IsNaN(e.Position)) cost = 1000;
            else if (t >= e.Position - 1e-9 && t - e.Position <= SequentialWindow) cost = t - e.Position;   // 読み進めれば届く
            else cost = 100 + Math.Abs(t - e.Position) / 3600;                                             // シークが必要
            if (cost < bestCost || (cost == bestCost && best != null && e.LastUsed > best.LastUsed)) { best = e; bestCost = cost; }
        }
        if (best == null)
        {
            best = Open(path, original);
            list.Add(best);
            Trim();
        }
        best.UsedInFrame = _frame;
        best.LastUsed = ++_tick;
        _assigned[key] = best;
        return best;
    }

    private static VideoEntry Open(string path, string original)
    {
        var e = new VideoEntry { Path = path };
        try { e.Reader = File.Exists(path) ? new VideoReader(path) : null; }
        catch { e.Reader = null; }
        if (e.Reader == null && original != path)
        {
            // プロキシが壊れていて開けなければ、元の動画を読む
            e.Path = original;
            try { e.Reader = File.Exists(original) ? new VideoReader(original) : null; }
            catch { e.Reader = null; }
        }
        else if (original != path)
            e.SourceWidth = MediaInfo.Probe(original)?.Width ?? 0;
        return e;
    }

    private SKImage? Decode(VideoEntry e, double seconds, bool loop)
    {
        var r = e.Reader;
        if (r == null) return null;
        seconds = Normalize(e, seconds, loop);
        e.Position = seconds;
        // 先に古い画像を手放してから取り出す (zeroCopy では、取り出すとバッファの中身が書き換わるため)
        long before = r.CurrentFrameId;
        var data = r.GetFramePointer(seconds);
        if (data == IntPtr.Zero) return _zeroCopy && r.CurrentFrameId != before ? null : e.Image;
        if (e.Image == null || e.FrameId != r.CurrentFrameId)
        {
            e.Image?.Dispose();
            var info = new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            e.Image = _zeroCopy
                ? SKImage.FromPixels(info, data, r.Width * 4)
                : SKImage.FromPixelCopy(info, data, r.Width * 4);
            e.FrameId = r.CurrentFrameId;
        }
        return e.Image;
    }

    public SKImage? GetImage(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_images.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Image;
        c.Image?.Dispose();
        SKImage? img = null;
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec != null)
            {
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bmp = SKBitmap.Decode(codec, info);
                if (bmp != null) img = SKImage.FromBitmap(bmp);
            }
        }
        catch { img = null; }
        _images[path] = (img, stamp);
        return img;
    }

    /// <summary>開いているデコーダが多すぎたら、今の描画で使っていないものを古い順に閉じる。</summary>
    private void Trim()
    {
        while (ReaderCount > MaxReaders)
        {
            var oldest = _byPath.SelectMany(kv => kv.Value.Select(e => (kv.Key, e))).Where(x => x.e.UsedInFrame != _frame).MinBy(x => x.e.LastUsed);
            if (oldest.e == null) return;
            oldest.e.Reader?.Dispose(); oldest.e.Image?.Dispose();
            _byPath[oldest.Key].Remove(oldest.e);
            if (_byPath[oldest.Key].Count == 0) _byPath.Remove(oldest.Key);
        }
    }

    public void Dispose()
    {
        foreach (var e in _byPath.Values.SelectMany(l => l)) { e.Reader?.Dispose(); e.Image?.Dispose(); }
        foreach (var (img, _) in _images.Values) img?.Dispose();
        _byPath.Clear(); _assigned.Clear(); _images.Clear();
    }
}
