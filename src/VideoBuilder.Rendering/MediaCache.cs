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
        /// <summary>裏で次のフレームを読んでいる (先読み) 間は、その作業。終わるまでデコーダに触らない。</summary>
        public Task? Pending;
        /// <summary>先読みしている位置 (秒)。</summary>
        public double PendingPosition;
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
        WaitLookahead();
        _frame++;
        _assigned.Clear();
    }

    public SKImage? GetVideoFrame(string key, string path, double seconds, bool loop) =>
        GetVideoFrame(key, path, seconds, loop, out _);

    /// <param name="key">オブジェクトの ID。同じ描画の中で、先に読んでおいた (Prefetch) フレームを見つけるのに使う。</param>
    /// <param name="scale">返した画像の 1 ピクセルが、元の動画の何ピクセルにあたるか (プロキシや縮めたデコードなら 2 や 4)。</param>
    /// <param name="detail">
    /// 元の動画の大きさに対して、どれだけの細かさが要るか (1 = 元の大きさ、0.25 = 縦横 1/4 で足りる)。
    /// 1 より小さければ、足りる範囲で縦横 1/n に縮めて変換する (変換と描画が軽くなる)。
    /// </param>
    public SKImage? GetVideoFrame(string key, string path, double seconds, bool loop, out float scale, float detail = 1)
    {
        scale = 1;
        var e = Acquire(key, EffectivePath(path), path, seconds, loop);
        var image = e == null ? null : Decode(e, seconds, loop, detail, path);
        if (image != null) scale = OriginalWidth(e!, path) / (float)image.Width;
        return image;
    }

    /// <summary>元の動画の幅。プロキシの元の大きさが分からなければ、読んでいるファイルの幅とみなす。</summary>
    private static int OriginalWidth(VideoEntry e, string original) =>
        e.Path != original && e.SourceWidth > 0 ? e.SourceWidth : e.Reader?.Width ?? 1;

    /// <summary>detail の細かさに足りる範囲で、いちばん大きな縮小率 (縦横 1/n) を選ぶ。</summary>
    private static int DivisorFor(VideoEntry e, string original, float detail)
    {
        if (e.Reader is not { } r || detail >= 1 || detail <= 0) return 1;
        double needed = OriginalWidth(e, original) * (double)detail;   // 要る幅 (ピクセル)
        // 丸めの誤差で 1 段細かくならないよう、少しだけ余裕を見る
        int n = (int)Math.Floor(r.Width / needed * 1.02);
        return Math.Clamp(n, 1, 8);
    }

    /// <summary>実際に読むファイル (プロキシを使う設定でプロキシがあれば、プロキシ)。</summary>
    private string EffectivePath(string path) =>
        UseProxies && ProxyStore.Enabled && ProxyStore.FindReady(path) is { } proxy ? proxy : path;

    /// <summary>
    /// 複数の動画のフレームを、動画ごとに別スレッドで同時にデコードしておく。
    /// このあとの GetVideoFrame は、デコード済みのフレームをすぐ返す。
    /// </summary>
    public void Prefetch(IReadOnlyList<(string Key, string Path, double Seconds, bool Loop, float Detail)> requests)
    {
        // デコーダの割り当て (一覧の書き換え) は 1 スレッドで済ませ、デコードだけを並列にする
        var jobs = new List<(VideoEntry Entry, double Seconds, bool Loop, float Detail, string Original)>();
        foreach (var (key, path, seconds, loop, detail) in requests)
        {
            var e = Acquire(key, EffectivePath(path), path, seconds, loop);
            if (e?.Reader != null && jobs.All(j => !ReferenceEquals(j.Entry, e))) jobs.Add((e, seconds, loop, detail, path));
        }
        if (jobs.Count == 1) Decode(jobs[0].Entry, jobs[0].Seconds, jobs[0].Loop, jobs[0].Detail, jobs[0].Original);
        else if (jobs.Count >= 2) Parallel.ForEach(jobs, job => Decode(job.Entry, job.Seconds, job.Loop, job.Detail, job.Original));
    }

    /// <summary>最後に取り出した動画フレームの大きさ (縮めてデコードしたときは縮めた大きさ。計測・テスト用)。</summary>
    public (int Width, int Height) LastFrameSize { get; private set; }

    /// <summary>先読みを裏で走らせている数 (計測・テスト用)。</summary>
    public int PendingLookaheads => _byPath.Values.Sum(l => l.Count(e => e.Pending != null));

    /// <summary>
    /// この描画で使ったデコーダに、次の描画で要るフレームを裏で読ませておく (デコードと変換を、合成と同時に進める)。
    /// key は、この描画で GetVideoFrame / Prefetch に渡したもの。先読みが終わるまで、そのデコーダには触らない
    /// (次の BeginFrame で終わるのを待つ)。
    /// </summary>
    public void Lookahead(IReadOnlyList<(string Key, double Seconds, bool Loop)> next)
    {
        foreach (var (key, seconds, loop) in next)
        {
            if (!_assigned.TryGetValue(key, out var e) || e.Reader is not { } r || e.Pending != null) continue;
            double t = Normalize(e, seconds, loop);
            // 今のフレームの範囲内なら、読むものがない
            if (!double.IsNaN(e.Position) && Math.Abs(t - e.Position) < 1e-9) continue;
            // 今のフレームの画像は、合成が終わるまで読まれる。先読みの変換で上書きしないよう、置き場を押さえておく
            r.PinCurrentFrame();
            e.PendingPosition = t;
            e.Pending = Task.Run(() =>
            {
                try { r.GetFramePointer(t); }
                catch { /* 先読みの失敗は無視する (本番の取り出しで、もう一度試す) */ }
            });
        }
    }

    /// <summary>先読みが終わるのを待って、デコーダを使えるように戻す。</summary>
    private void WaitLookahead()
    {
        foreach (var e in _byPath.Values.SelectMany(l => l))
        {
            if (e.Pending == null) continue;
            try { e.Pending.Wait(); } catch { /* 中で握りつぶしているので来ない */ }
            e.Pending = null;
            e.Reader?.UnpinFrame();
            e.Position = e.PendingPosition;
        }
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

    private SKImage? Decode(VideoEntry e, double seconds, bool loop, float detail = 1, string? original = null)
    {
        var r = e.Reader;
        if (r == null) return null;
        // 先読み中のデコーダには触らない (この描画のフレームは、先読みを始める前に取り出してある)
        if (e.Pending != null) return e.Image;
        seconds = Normalize(e, seconds, loop);
        e.Position = seconds;
        r.OutputDivisor = DivisorFor(e, original ?? e.Path, detail);
        long before = r.CurrentFrameId;
        var data = r.GetFramePointer(seconds);
        if (data == IntPtr.Zero) return _zeroCopy && r.CurrentFrameId != before ? null : e.Image;
        if (e.Image == null || e.FrameId != r.CurrentFrameId || e.Image.Width != r.FrameWidth || e.Image.Height != r.FrameHeight)
        {
            // zeroCopy の古い画像は、デコーダの置き場を指しているだけなので、取り出した後に手放してよい
            e.Image?.Dispose();
            var info = new SKImageInfo(r.FrameWidth, r.FrameHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            e.Image = _zeroCopy
                ? SKImage.FromPixels(info, data, r.FrameWidth * 4)
                : SKImage.FromPixelCopy(info, data, r.FrameWidth * 4);
            e.FrameId = r.CurrentFrameId;
        }
        LastFrameSize = (e.Image.Width, e.Image.Height);
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
        WaitLookahead();
        foreach (var e in _byPath.Values.SelectMany(l => l)) { e.Reader?.Dispose(); e.Image?.Dispose(); }
        foreach (var (img, _) in _images.Values) img?.Dispose();
        _byPath.Clear(); _assigned.Clear(); _images.Clear();
    }
}
