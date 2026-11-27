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
        /// <summary>元の動画のパス (Path がプロキシの区切りのときに、どの動画のものかを知るため)。</summary>
        public string Original = "";
        /// <summary>最後に取り出した位置 (秒)。まだなら NaN。</summary>
        public double Position = double.NaN;
        /// <summary>最後に使った描画の番号。同じ描画の中で、別のオブジェクトに貸さないために使う。</summary>
        public long UsedInFrame = -1;
        /// <summary>裏で次のフレームを読んでいる (先読み) 間は、その作業。終わるまでデコーダに触らない。</summary>
        public Task? Pending;
        /// <summary>先読みしている位置 (秒)。</summary>
        public double PendingPosition;
        /// <summary>直前の Fetch で取り出したフレーム (デコーダの置き場)。取り出せなければ IntPtr.Zero。</summary>
        public IntPtr Data;
        /// <summary>直前の Fetch で、前の画像が指していた置き場の中身が失われた (zeroCopy で取り出しに失敗した)。</summary>
        public bool Lost;
        /// <summary>これから始まるクリップのために、裏で開いて最初のフレームまで読んでいる間は、その作業 (Preload)。</summary>
        public Task? Preloading;
        /// <summary>先に開いておいたクリップのオブジェクトの ID (まだ使われていなければ)。</summary>
        public string? PreloadKey;
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
    /// <param name="gpu">
    /// GPU 描画のときのコンテキスト。動画のフレームを、デコーダの置き場から直接 GPU に送る
    /// (メモリ上で複製してから送るより 1 回少ない)。描画のスレッドからだけ使う。
    /// </param>
    /// <param name="gpuContext">
    /// gpu の元の GpuContext。渡せば、GPU 描画中は動画のフレームを YUV のまま GPU へ送り、GPU で色を変換する (YuvTextures)。
    /// </param>
    public MediaCache(bool zeroCopy = false, GRContext? gpu = null, GpuContext? gpuContext = null)
    {
        _zeroCopy = zeroCopy;
        _gpu = gpu;
        _gpuContext = gpu != null && !zeroCopy && gpuContext?.Context == gpu ? gpuContext : null;
        if (_gpuContext == null || Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU_YUV") == "0") _yuvState = -1;
    }

    private readonly GRContext? _gpu;
    private readonly GpuContext? _gpuContext;
    private YuvTextures? _yuv;
    // 0: まだ試していない, 1: 使える, -1: 使えない (GPU 描画でない・OpenGL ES 3 / OpenGL 3 でない・シェーダを作れない)
    private volatile int _yuvState;

    /// <summary>YUV のまま GPU へ送れないとき、その理由 (計測・表示用)。</summary>
    public string? GpuYuvError { get; private set; }

    /// <summary>YUV のまま GPU へ送って変換したフレームの数 (計測・テスト用)。</summary>
    public long GpuYuvFrames => _yuv?.Converted ?? 0;

    /// <summary>動画のフレームを YUV のまま GPU へ送るか (GPU 描画中で、使えないと分かっていなければ)。</summary>
    private bool WantsYuv => _yuvState >= 0 && DrawingOnGpu;

    /// <summary>今の描画が GPU で描かれるか (GPU の描画先を作れずに CPU で描くときは false にして、GPU へ送らない)。</summary>
    public bool DrawingOnGpu { get; set; } = true;

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
        FinishPreloads(wait: false);
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
        seconds = Wrap(path, seconds, loop);
        var e = Acquire(key, EffectivePath(path, seconds), path, seconds);
        if (e != null) Fetch(e, seconds, detail, path);
        var image = e == null ? null : ImageOf(e);
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
    private string EffectivePath(string path, double seconds) =>
        UseProxies && ProxyStore.Enabled && ProxyStore.FindSegment(path, seconds) is { } proxy ? proxy : path;

    // 元の動画の情報 (長さ・幅)。プロキシの区切りを読んでいても、ループの長さや大きさは元の動画で決める
    private readonly Dictionary<string, MediaInfo?> _info = new();

    private MediaInfo? InfoOf(string original)
    {
        if (!_info.TryGetValue(original, out var info)) _info[original] = info = MediaInfo.Probe(original);
        return info;
    }

    /// <summary>ループするときは、元の動画の長さで割った余りにする (VideoReader.Duration と同じ長さ)。</summary>
    private double Wrap(string original, double seconds, bool loop) =>
        loop && InfoOf(original) is { VideoDuration: > 0 } info ? seconds % info.VideoDuration : seconds;

    /// <summary>
    /// 複数の動画のフレームを、動画ごとに別スレッドで同時にデコードしておく。
    /// このあとの GetVideoFrame は、デコード済みのフレームをすぐ返す。
    /// </summary>
    public void Prefetch(IReadOnlyList<(string Key, string Path, double Seconds, bool Loop, float Detail)> requests)
    {
        // デコーダの割り当て (一覧の書き換え) は 1 スレッドで済ませ、デコードだけを並列にする
        var jobs = new List<(VideoEntry Entry, double Seconds, bool Loop, float Detail, string Original)>();
        foreach (var (key, path, rawSeconds, loop, detail) in requests)
        {
            double seconds = Wrap(path, rawSeconds, loop);
            var e = Acquire(key, EffectivePath(path, seconds), path, seconds);
            if (e?.Reader != null && jobs.All(j => !ReferenceEquals(j.Entry, e))) jobs.Add((e, seconds, false, detail, path));
        }
        // デコードは並列に、画像を作る (GPU なら送る) のはこのスレッドで (GPU のコンテキストはこのスレッドのもの)
        if (jobs.Count == 1) Fetch(jobs[0].Entry, jobs[0].Seconds, jobs[0].Detail, jobs[0].Original);
        else if (jobs.Count >= 2) Parallel.ForEach(jobs, job => Fetch(job.Entry, job.Seconds, job.Detail, job.Original));
        foreach (var job in jobs) ImageOf(job.Entry);
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
    public void Lookahead(IReadOnlyList<(string Key, string Path, double Seconds, bool Loop)> next)
    {
        foreach (var (key, path, seconds, loop) in next)
        {
            if (!_assigned.TryGetValue(key, out var e) || e.Reader is not { } r || e.Pending != null) continue;
            double t = Wrap(path, seconds, loop);
            // 次のフレームが別のファイル (プロキシの次の区切り・プロキシと元の動画の切り替わり) なら、このデコーダでは読まない
            if (EffectivePath(path, t) != e.Path) continue;
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

    // ---------------- これから始まるクリップを先に開く ----------------

    private const int MaxPreloads = 2;
    private int _preloading;

    /// <summary>裏で開いている途中のデコーダの数 (計測・テスト用)。</summary>
    public int PreloadsInFlight => _byPath.Values.SelectMany(l => l).Count(e => e.Preloading is { IsCompleted: false });

    /// <summary>先に開いておいたデコーダを、クリップが始まったときにそのまま使えた回数 (計測・テスト用)。</summary>
    public long PreloadHits { get; private set; }

    /// <summary>
    /// これから始まるクリップのデコーダを裏で開き、最初のフレームまでデコードしておく (同時に 2 つまで)。
    /// 動画の途中から始まるクリップは、始まった瞬間にデコードすると前のキーフレームから読むので時間がかかり、再生が一瞬止まる。
    /// 開き終えるまでは誰にも貸さず、待ちもしない (間に合わなければ、これまでどおりその場で開く)。
    /// </summary>
    public void Preload(IReadOnlyList<(string Key, string Path, double Seconds, bool Loop)> upcoming)
    {
        foreach (var (key, original, rawSeconds, loop) in upcoming)
        {
            if (_preloading >= MaxPreloads) return;
            if (string.IsNullOrEmpty(original) || !File.Exists(original)) continue;
            double t = Wrap(original, rawSeconds, loop);
            var path = EffectivePath(original, t);
            if (!_byPath.TryGetValue(path, out var list)) _byPath[path] = list = new List<VideoEntry>();
            // すでに開いてある (開いている途中の) もの・空いていて少し読み進めれば届くものがあれば要らない
            if (list.Any(e => e.PreloadKey == key && Math.Abs((e.Preloading != null ? e.PendingPosition : e.Position) - t) < 0.5)) continue;
            if (list.Any(e => e.Preloading == null && e.UsedInFrame != _frame && !double.IsNaN(e.Position) && t >= e.Position - 1e-9 && t - e.Position <= SequentialWindow)) continue;
            int width = InfoOf(original)?.Width ?? 0;
            var entry = new VideoEntry { Path = path, Original = original, PreloadKey = key, PendingPosition = t, LastUsed = ++_tick };
            list.Add(entry);
            _preloading++;
            entry.Preloading = Task.Run(() =>
            {
                var opened = Open(path, original, width);
                entry.Reader = opened.Reader;
                entry.SourceWidth = opened.SourceWidth;
                try { opened.Reader?.GetFramePointer(t); }
                catch { /* 読めなければ、使うときにもう一度試す */ }
            });
        }
    }

    /// <summary>開き終えた先読みのデコーダを、使えるようにする。wait なら終わるのを待つ (破棄のとき)。</summary>
    private void FinishPreloads(bool wait)
    {
        foreach (var e in _byPath.Values.SelectMany(l => l))
        {
            if (e.Preloading == null || (!wait && !e.Preloading.IsCompleted)) continue;
            try { e.Preloading.Wait(); } catch { /* 中で握りつぶしているので来ない */ }
            e.Preloading = null;
            e.Position = e.Reader == null ? double.NaN : e.PendingPosition;
            _preloading--;
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


    /// <summary>
    /// このオブジェクトに貸すデコーダを決める。同じ描画の中では、1 つのデコーダを 2 つのオブジェクトに貸さない。
    /// 選び方: 少し先まで読み進めれば届くもの (シーク不要) → それ以外で使われていないもの → 新しく開く。
    /// </summary>
    /// <param name="original">path がプロキシのときの、元の動画のパス。</param>
    private VideoEntry? Acquire(string key, string path, string original, double seconds)
    {
        if (string.IsNullOrEmpty(path)) return null;
        // 同じ描画の中で、先に割り当てたもの (Prefetch) があればそれを使う
        if (_assigned.TryGetValue(key, out var mine) && mine.Path == path && Math.Abs(mine.Position - seconds) < 1e-9)
            return mine;

        if (!_byPath.TryGetValue(path, out var list)) _byPath[path] = list = new List<VideoEntry>();
        VideoEntry? best = null;
        double bestCost = double.MaxValue;
        foreach (var e in list)
        {
            if (e.UsedInFrame == _frame || e.Preloading != null) continue;
            double t = seconds;
            double cost;
            if (double.IsNaN(e.Position)) cost = 1000;
            else if (t >= e.Position - 1e-9 && t - e.Position <= SequentialWindow) cost = t - e.Position;   // 読み進めれば届く
            else cost = 100 + Math.Abs(t - e.Position) / 3600;                                             // シークが必要
            if (cost < bestCost || (cost == bestCost && best != null && e.LastUsed > best.LastUsed)) { best = e; bestCost = cost; }
        }
        if (best == null)
        {
            best = Open(path, original, InfoOf(original)?.Width ?? 0);
            list.Add(best);
            if (path != original) CloseOtherSegments(best);
            Trim();
        }
        if (best.PreloadKey != null)
        {
            if (best.PreloadKey == key && bestCost < 100) PreloadHits++;
            best.PreloadKey = null;
        }
        best.UsedInFrame = _frame;
        best.LastUsed = ++_tick;
        _assigned[key] = best;
        return best;
    }

    /// <param name="sourceWidth">元の動画の幅 (path がプロキシのとき、縮小率を求めるのに使う)。</param>
    private static VideoEntry Open(string path, string original, int sourceWidth)
    {
        var e = new VideoEntry { Path = path, Original = original };
        // プロキシの区切りは、元の動画と同じ時刻で書いてあるので、時刻をそのまま使って読む
        try { e.Reader = File.Exists(path) ? new VideoReader(path, absoluteTime: path != original) : null; }
        catch { e.Reader = null; }
        if (e.Reader == null && original != path)
        {
            // プロキシが壊れていて開けなければ、元の動画を読む
            e.Path = original;
            try { e.Reader = File.Exists(original) ? new VideoReader(original) : null; }
            catch { e.Reader = null; }
        }
        else if (original != path)
            e.SourceWidth = sourceWidth;
        return e;
    }

    /// <summary>
    /// 同じ動画の、ほかのプロキシの区切りのデコーダを閉じる (この描画で使っていないもの)。
    /// 再生が区切りをまたぐたびに新しく開くので、残しておくと数がふくらむ。
    /// </summary>
    private void CloseOtherSegments(VideoEntry keep)
    {
        foreach (var (path, list) in _byPath.ToList())
        {
            if (path == keep.Path) continue;
            foreach (var e in list.Where(e => e.Original == keep.Original && e.Path != e.Original && e.UsedInFrame != _frame && e.Pending == null && e.Preloading == null && e.PreloadKey == null).ToList())
            {
                e.Reader?.Dispose(); e.Image?.Dispose();
                list.Remove(e);
            }
            if (list.Count == 0) _byPath.Remove(path);
        }
    }

    /// <summary>デコーダから seconds のフレームを取り出す (Data に入れる)。どのスレッドからでもよい (1 つの entry を同時に使わなければ)。</summary>
    private void Fetch(VideoEntry e, double seconds, float detail = 1, string? original = null)
    {
        var r = e.Reader;
        // 先読み中のデコーダには触らない (この描画のフレームは、先読みを始める前に取り出してある)
        if (r == null || e.Pending != null) { e.Data = IntPtr.Zero; e.Lost = false; return; }
        e.Position = seconds;
        r.OutputDivisor = DivisorFor(e, original ?? e.Path, detail);
        r.PreferYuv = WantsYuv;
        long before = r.CurrentFrameId;
        e.Data = r.GetFramePointer(seconds);
        e.Lost = e.Data == IntPtr.Zero && _zeroCopy && r.CurrentFrameId != before;
    }

    /// <summary>直前の Fetch で取り出したフレームの画像を返す (同じフレームなら前の画像)。描画のスレッドから呼ぶ。</summary>
    private SKImage? ImageOf(VideoEntry e)
    {
        var r = e.Reader;
        if (r == null) return null;
        if (e.Data == IntPtr.Zero) return e.Lost ? null : e.Image;
        if (e.Image == null || e.FrameId != r.CurrentFrameId || e.Image.Width != r.FrameWidth || e.Image.Height != r.FrameHeight)
        {
            // zeroCopy の古い画像は、デコーダの置き場を指しているだけなので、取り出した後に手放してよい
            e.Image?.Dispose();
            // アニメーション WebP・GIF は透明な部分を残す (VideoReader がアルファを掛けて渡す)
            var info = new SKImageInfo(r.FrameWidth, r.FrameHeight, SKColorType.Bgra8888, r.HasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque);
            if (_zeroCopy) e.Image = SKImage.FromPixels(info, e.Data, r.FrameWidth * 4);
            else if (r.TryGetYuv(e.Data, out var planes))
            {
                // YUV のまま持っている: GPU で色を変換する。できなければ、ここで BGRA にしてから送る
                e.Image = YuvImage(planes) ?? Upload(info, r.EnsureBgra(e.Data));
            }
            else e.Image = Upload(info, e.Data);
            e.FrameId = r.CurrentFrameId;
        }
        e.Data = IntPtr.Zero;
        LastFrameSize = (e.Image.Width, e.Image.Height);
        return e.Image;
    }

    /// <summary>YUV の面を GPU へ送って、GPU で RGBA にした画像。GPU 描画中でない・使えないときは null。描画のスレッドから呼ぶ。</summary>
    private SKImage? YuvImage(in VideoBuilder.Media.YuvPlanes planes)
    {
        if (_gpuContext == null || !DrawingOnGpu || _yuvState < 0) return null;
        if (_yuv == null)
        {
            _yuv = YuvTextures.Create(_gpuContext, out var error);
            if (_yuv == null) { GpuYuvError = error; _yuvState = -1; return null; }
            _yuvState = 1;
        }
        return _yuv.Convert(planes);
    }

    /// <summary>GPU へ送った回数 (メモリ上で複製せずに直接送ったもの。計測・テスト用)。</summary>
    public long DirectUploads { get; private set; }

    /// <summary>
    /// フレームを、描画が終わるまで使える画像にする。GPU 描画なら、デコーダの置き場から直接 GPU のテクスチャに送る。
    /// 送るのは Flush の中で済むので、そのあとは置き場が書き換わってもよい。GPU に送れなければ、メモリ上に複製する。
    /// </summary>
    private SKImage Upload(SKImageInfo info, IntPtr data)
    {
        if (_gpu != null && DrawingOnGpu)
        {
            using var view = SKImage.FromPixels(info, data, info.Width * 4);
            var texture = view?.ToTextureImage(_gpu);
            if (texture != null)
            {
                _gpu.Flush(true, false);
                DirectUploads++;
                return texture;
            }
        }
        return SKImage.FromPixelCopy(info, data, info.Width * 4);
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
            var oldest = _byPath.SelectMany(kv => kv.Value.Select(e => (kv.Key, e))).Where(x => x.e.UsedInFrame != _frame && x.e.Preloading == null).MinBy(x => x.e.LastUsed);
            if (oldest.e == null) return;
            oldest.e.Reader?.Dispose(); oldest.e.Image?.Dispose();
            _byPath[oldest.Key].Remove(oldest.e);
            if (_byPath[oldest.Key].Count == 0) _byPath.Remove(oldest.Key);
        }
    }

    public void Dispose()
    {
        WaitLookahead();
        FinishPreloads(wait: true);
        foreach (var e in _byPath.Values.SelectMany(l => l)) { e.Reader?.Dispose(); e.Image?.Dispose(); }
        foreach (var (img, _) in _images.Values) img?.Dispose();
        _byPath.Clear(); _assigned.Clear(); _images.Clear();
        _yuv?.Dispose();
        _yuv = null;
    }
}
