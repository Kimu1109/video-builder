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
    private const int MaxReaders = 12;

    /// <summary>key ごとにデコーダを持つ (同じファイルを別オブジェクトで使ってもシークが競合しない)。</summary>
    public SKImage? GetVideoFrame(string key, string path, double seconds, bool loop)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!_videos.TryGetValue(key, out var e) || e.Path != path)
        {
            e?.Reader?.Dispose(); e?.Image?.Dispose();
            e = new VideoEntry { Path = path };
            try { e.Reader = File.Exists(path) ? new VideoReader(path) : null; }
            catch { e.Reader = null; }
            _videos[key] = e;
            Trim();
        }
        e.LastUsed = ++_tick;
        var r = e.Reader;
        if (r == null) return null;
        if (loop && r.Duration > 0) seconds %= r.Duration;
        var data = r.GetFrame(seconds);
        if (data == null) return e.Image;
        if (e.Image == null || e.FrameId != r.CurrentFrameId)
        {
            e.Image?.Dispose();
            var info = new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            unsafe
            {
                fixed (byte* p = data) e.Image = SKImage.FromPixelCopy(info, (IntPtr)p, r.Width * 4);
            }
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
