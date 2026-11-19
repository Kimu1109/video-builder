using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using VideoBuilder.Media;

namespace VideoBuilder.App.Services;

/// <summary>
/// タイムラインに描く音声波形 (ピーク値)。裏で 1 ファイルずつ計算し、結果はディスクにも保存する。
/// </summary>
public sealed class WaveformCache
{
    public const int PeaksPerSecond = 50;
    private const int Rate = 8000;

    public sealed record Peaks(byte[] Values, double Duration);

    private readonly ConcurrentDictionary<string, Peaks?> _ready = new();
    private readonly ConcurrentDictionary<string, bool> _pending = new();
    private readonly SemaphoreSlim _gate = new(1);

    /// <summary>新しい波形ができた (UI スレッドで通知)。</summary>
    public event Action? Updated;

    /// <summary>計算済みなら返す。まだなら計算を始めて null を返す。</summary>
    public Peaks? Get(string path)
    {
        if (_ready.TryGetValue(path, out var p)) return p;
        if (path.Length == 0 || !_pending.TryAdd(path, true)) return null;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { _ready[path] = LoadOrCompute(path); }
            catch { _ready[path] = null; }
            finally { _gate.Release(); }
            Dispatcher.UIThread.Post(() => Updated?.Invoke());
        });
        return null;
    }

    private static string CacheFile(string path)
    {
        var info = new FileInfo(path);
        var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{PeaksPerSecond}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppPaths.CacheDir, "waveform", hash + ".peaks");
    }

    private static Peaks? LoadOrCompute(string path)
    {
        if (!File.Exists(path) || !FFmpegLoader.EnsureLoaded()) return null;
        string cache = CacheFile(path);
        if (File.Exists(cache))
        {
            var bytes = File.ReadAllBytes(cache);
            if (bytes.Length >= 8) return new Peaks(bytes[8..], BitConverter.ToDouble(bytes, 0));
        }
        using var reader = new AudioReader(path, Rate);
        double duration = reader.Duration;
        if (duration <= 0) return null;
        int bucket = Rate / PeaksPerSecond;
        int count = (int)Math.Min(int.MaxValue / 2, Math.Ceiling(duration * PeaksPerSecond));
        var values = new byte[count];
        var buffer = new float[Rate * 2];   // 1 秒ずつ読む
        for (long pos = 0; pos < (long)count * bucket; pos += Rate)
        {
            reader.Read(pos, buffer);
            for (int b = 0; b < PeaksPerSecond; b++)
            {
                long index = pos / bucket + b;
                if (index >= count) break;
                float max = 0;
                for (int i = b * bucket * 2, end = (b + 1) * bucket * 2; i < end; i++) max = Math.Max(max, Math.Abs(buffer[i]));
                values[index] = (byte)Math.Min(255, max * 255);
            }
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            using var fs = File.Create(cache);
            fs.Write(BitConverter.GetBytes(duration));
            fs.Write(values);
        }
        catch { /* キャッシュに書けなくても表示はできる */ }
        return new Peaks(values, duration);
    }
}

/// <summary>タイムラインのオブジェクト先頭に出す、動画・画像の小さなサムネイル。</summary>
public sealed class ThumbnailCache
{
    public const int Height = 40;
    private readonly Dictionary<string, Bitmap?> _ready = new();
    private readonly HashSet<string> _pending = new();
    private readonly SemaphoreSlim _gate = new(1);

    public event Action? Updated;

    /// <summary>UI スレッドから呼ぶ。できていれば返し、まだなら作り始めて null を返す。</summary>
    public Bitmap? Get(string path, double seconds, bool isVideo)
    {
        if (path.Length == 0) return null;
        // 0.5 秒単位に丸めて、少しずつ違う時刻の要求が大量に出ないようにする
        double t = Math.Round(Math.Max(0, seconds) * 2) / 2;
        string key = $"{path}|{t:0.0}";
        if (_ready.TryGetValue(key, out var bmp)) return bmp;
        if (!_pending.Add(key)) return null;
        _ = Task.Run(async () =>
        {
            (byte[] Pixels, int W, int H)? result = null;
            await _gate.WaitAsync();
            try { result = isVideo ? FromVideo(path, t) : FromImage(path); }
            catch { result = null; }
            finally { _gate.Release(); }
            Dispatcher.UIThread.Post(() =>
            {
                _ready[key] = result is { } r ? ToBitmap(r.Pixels, r.W, r.H) : null;
                if (_ready.Count > 400) Trim();
                Updated?.Invoke();
            });
        });
        return null;
    }

    private void Trim()
    {
        foreach (var k in _ready.Keys.Take(200).ToList()) { _ready[k]?.Dispose(); _ready.Remove(k); _pending.Remove(k); }
    }

    // 開いた動画を少しのあいだ取っておく (同じ動画の別の位置のサムネイルを、開き直さずに作るため)。
    // 作るのは 1 本ずつ (_gate) なので、ほかのスレッドと取り合わない
    private static readonly Dictionary<string, VideoReader> Readers = new();
    private static readonly Queue<string> ReaderOrder = new();

    private static VideoReader OpenCached(string path)
    {
        if (Readers.TryGetValue(path, out var r)) return r;
        r = new VideoReader(path);
        Readers[path] = r;
        ReaderOrder.Enqueue(path);
        while (ReaderOrder.Count > 4) { var old = ReaderOrder.Dequeue(); Readers[old].Dispose(); Readers.Remove(old); }
        return r;
    }

    private static (byte[], int, int)? FromVideo(string path, double seconds)
    {
        if (!File.Exists(path) || !FFmpegLoader.EnsureLoaded()) return null;
        // プロキシがあれば、小さいプロキシから作る (元の動画をデコードするより速い)
        var source = ProxyStore.FindReady(path) ?? path;
        var reader = OpenCached(source);
        // サムネイルは小さいので、縮めて変換する (縮小に使う大きさの 2 倍は残す)
        reader.OutputDivisor = Math.Max(1, reader.Height / (Height * 2));
        var frame = reader.GetFrame(seconds);
        if (frame == null) return null;
        var info = new SKImageInfo(reader.FrameWidth, reader.FrameHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var src = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(frame, 0, src.GetPixels(), frame.Length);
        return Scale(src);
    }

    private static (byte[], int, int)? FromImage(string path)
    {
        if (!File.Exists(path)) return null;
        using var src = SKBitmap.Decode(path);
        return src == null ? null : Scale(src);
    }

    private static (byte[], int, int)? Scale(SKBitmap src)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * (double)Height / src.Height));
        using var dst = new SKBitmap(new SKImageInfo(w, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!src.ScalePixels(dst, SKFilterQuality.Medium)) return null;
        return (dst.Bytes, w, Height);
    }

    private static Bitmap ToBitmap(byte[] pixels, int w, int h)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(pixels, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        return bmp;
    }
}
