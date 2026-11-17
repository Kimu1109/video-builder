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
    public SKImage? GetVideoFrame(string key, string path, double seconds, bool loop)
    {
        var e = Ensure(key, path);
        return e == null ? null : Decode(e, seconds, loop);
    }

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
            var e = Ensure(key, path);
            if (e?.Reader != null && jobs.All(j => !ReferenceEquals(j.Entry, e))) jobs.Add((e, seconds, loop));
        }
        if (jobs.Count < 2) return;
        Parallel.ForEach(jobs, job => Decode(job.Entry, job.Seconds, job.Loop));
    }

    private VideoEntry? Ensure(string key, string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!_videos.TryGetValue(key, out var e) || e.Path != path)
        {
            e?.Reader?.Dispose(); e?.Image?.Dispose();
            e = new VideoEntry { Path = path };
            try { e.Reader = File.Exists(path) ? new VideoReader(path) : null; }
            catch { e.Reader = null; }
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
