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
    }

    private readonly Dictionary<string, VideoEntry> _videos = new();
    private readonly Dictionary<string, (SKImage? Image, DateTime Stamp)> _images = new();
    private long _tick;
    private const int MaxReaders = 24;
    private readonly bool _zeroCopy;

    /// <param name="zeroCopy">
    /// 動画フレームを複製せず、デコーダのバッファを直接指す画像を作る。
    /// 画像は「次のフレームを取り出すまで」しか正しくないので、すぐに描き切る CPU 描画でだけ使う。
    /// </param>
    public MediaCache(bool zeroCopy = false) => _zeroCopy = zeroCopy;

    /// <summary>key ごとにデコーダを持つ (同じファイルを別オブジェクトで使ってもシークが競合しない)。</summary>
    /// <summary>プレビュー用: プロキシがあれば元の動画の代わりに使う。</summary>
    public bool UseProxies { get; set; }

    public SKImage? GetVideoFrame(string key, string path, double seconds, bool loop) =>
        GetVideoFrame(key, path, seconds, loop, out _);

    /// <param name="scale">返した画像の 1 ピクセルが、元の動画の何ピクセルにあたるか (プロキシなら 2 や 4)。</param>
    public SKImage? GetVideoFrame(string key, string path, double seconds, bool loop, out float scale)
    {
        scale = 1;
        var e = Ensure(key, EffectivePath(path), path);
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
        // デコーダの用意 (一覧の書き換え) は 1 スレッドで済ませ、デコードだけを並列にする
        var jobs = new List<(VideoEntry Entry, double Seconds, bool Loop)>();
        foreach (var (key, path, seconds, loop) in requests)
        {
            var e = Ensure(key, EffectivePath(path), path);
            if (e?.Reader != null && jobs.All(j => !ReferenceEquals(j.Entry, e))) jobs.Add((e, seconds, loop));
        }
        if (jobs.Count < 2) return;
        Parallel.ForEach(jobs, job => Decode(job.Entry, job.Seconds, job.Loop));
    }

    /// <param name="original">path がプロキシのときの、元の動画のパス。</param>
    private VideoEntry? Ensure(string key, string path, string? original = null)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!_videos.TryGetValue(key, out var e) || e.Path != path)
        {
            e?.Reader?.Dispose(); e?.Image?.Dispose();
            e = new VideoEntry { Path = path };
            try { e.Reader = File.Exists(path) ? new VideoReader(path) : null; }
            catch { e.Reader = null; }
            if (e.Reader == null && original != null && original != path)
            {
                // プロキシが壊れていて開けなければ、元の動画を読む
                e.Path = original;
                try { e.Reader = File.Exists(original) ? new VideoReader(original) : null; }
                catch { e.Reader = null; }
            }
            else if (original != null && original != path)
                e.SourceWidth = MediaInfo.Probe(original)?.Width ?? 0;
            _videos[key] = e;
            e.LastUsed = ++_tick;
            Trim();
        }
        e.LastUsed = ++_tick;
        return e;
    }

    private SKImage? Decode(VideoEntry e, double seconds, bool loop)
    {
        var r = e.Reader;
        if (r == null) return null;
        if (loop && r.Duration > 0) seconds %= r.Duration;
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

    private void Trim()
    {
        while (_videos.Count > MaxReaders)
        {
            var oldest = _videos.MinBy(kv => kv.Value.LastUsed);
            oldest.Value.Reader?.Dispose(); oldest.Value.Image?.Dispose();
            _videos.Remove(oldest.Key);
        }
    }

    public void Dispose()
    {
        foreach (var e in _videos.Values) { e.Reader?.Dispose(); e.Image?.Dispose(); }
        foreach (var (img, _) in _images.Values) img?.Dispose();
        _videos.Clear(); _images.Clear();
    }
}
