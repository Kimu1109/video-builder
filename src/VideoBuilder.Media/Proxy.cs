using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>
/// プロキシ (プレビュー用に小さく作り直した動画) の置き場所と、できているかどうかの確認。
///
/// プロキシは元の動画を SegmentSeconds 秒ごとに区切った別々のファイル (区切り) として、1 つのフォルダに置く。
/// 区切りは書き終えたものから使えるので、長い動画でも作り終わるのを待たずに、できた所からプレビューが軽くなる。
/// 途中でやめても、できた区切りは残る (次は続きから作る)。区切りは元の動画と同じ時刻で書く
/// (VideoReader を absoluteTime で開けば、元の動画の時刻でそのまま読める)。
///
/// フォルダ名は「元の動画のパス・大きさ・更新日時・縮小率」から決めるので、
/// 元の動画を書き換えると自動的に別物として扱われる (古いプロキシは使われない)。
/// </summary>
public static class ProxyStore
{
    public const string Extension = ".mkv";

    /// <summary>区切りの長さ (秒)。</summary>
    public const double SegmentSeconds = 20;

    /// <summary>すべての区切りを作り終えた印のファイル名。</summary>
    private const string CompleteMarker = "complete";

    /// <summary>プロキシを置くフォルダ。アプリが起動時に設定する。</summary>
    public static string Directory { get; set; } = Path.Combine(Path.GetTempPath(), "video-builder-proxy");

    /// <summary>プレビューでプロキシを使うか。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>プロキシの縮小率の分母 (2 = 縦横 1/2, 4 = 1/4)。</summary>
    public static int Divisor { get; set; } = 2;

    /// <summary>プロキシ (の区切り) ができた・消えたときに呼ばれる (別スレッドから呼ばれることがある)。</summary>
    public static event Action? Changed;

    /// <summary>1 つの動画の、ある縮小率のプロキシの様子。</summary>
    private sealed class State
    {
        public string? Folder;
        public bool Complete;
        public readonly HashSet<int> Ready = new();
        /// <summary>以前の版で作った、1 つのファイルのプロキシ (あればどの時刻にも使う)。</summary>
        public string? Legacy;
    }

    private static readonly Dictionary<(string, int), State> Lookup = new();
    private static readonly object Gate = new();

    /// <summary>source の、今の縮小率でのプロキシのフォルダ (まだ作られていなくても返す)。元の動画がなければ null。</summary>
    public static string? PathFor(string source) => PathFor(source, Divisor);

    public static string? PathFor(string source, int divisor)
    {
        var info = new FileInfo(source);
        if (!info.Exists) return null;
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{divisor}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(Directory, $"{Path.GetFileNameWithoutExtension(source)}_{hash}_{divisor}");
    }

    /// <summary>フォルダの中の、index 番目の区切りのファイル。</summary>
    public static string SegmentPath(string folder, int index) => Path.Combine(folder, $"{index:00000}{Extension}");

    /// <summary>時刻 (秒) が入る区切りの番号。</summary>
    public static int SegmentIndex(double seconds) => seconds <= 0 || double.IsNaN(seconds) ? 0 : (int)Math.Floor(seconds / SegmentSeconds);

    /// <summary>長さ duration (秒) の動画の区切りの数。</summary>
    public static int SegmentCount(double duration) => Math.Max(1, (int)Math.Ceiling(duration / SegmentSeconds - 1e-9));

    private static State StateOf(string source, int divisor)
    {
        lock (Gate)
        {
            if (Lookup.TryGetValue((source, divisor), out var known)) return known;
        }
        var state = new State { Folder = PathFor(source, divisor) };
        if (state.Folder != null)
        {
            if (File.Exists(state.Folder + Extension)) { state.Legacy = state.Folder + Extension; state.Complete = true; }
            if (System.IO.Directory.Exists(state.Folder))
            {
                foreach (var f in System.IO.Directory.EnumerateFiles(state.Folder, "*" + Extension))
                    if (int.TryParse(Path.GetFileNameWithoutExtension(f), out int i)) state.Ready.Add(i);
                if (File.Exists(Path.Combine(state.Folder, CompleteMarker))) state.Complete = true;
            }
        }
        lock (Gate) return Lookup.TryAdd((source, divisor), state) ? state : Lookup[(source, divisor)];
    }

    /// <summary>
    /// seconds の時刻を含むプロキシのファイルがあれば、そのパスを返す (VideoReader を absoluteTime で開いて、元の時刻で読む)。
    /// 結果は覚えておき、プロキシができた・消えたときに調べ直す。
    /// </summary>
    public static string? FindSegment(string source, double seconds)
    {
        if (string.IsNullOrEmpty(source)) return null;
        var state = StateOf(source, Divisor);
        if (state.Legacy != null) return state.Legacy;
        if (state.Folder == null) return null;
        int index = SegmentIndex(seconds);
        lock (Gate) return state.Ready.Contains(index) ? SegmentPath(state.Folder, index) : null;
    }

    /// <summary>すべての区切りができているか。</summary>
    public static bool Exists(string source) => !string.IsNullOrEmpty(source) && StateOf(source, Divisor).Complete;

    /// <summary>区切りが 1 つでもあるか (作りかけを含む)。</summary>
    public static bool HasAny(string source)
    {
        if (string.IsNullOrEmpty(source)) return false;
        var state = StateOf(source, Divisor);
        lock (Gate) return state.Complete || state.Ready.Count > 0;
    }

    /// <summary>できている区切りの番号 (作る側が、作らずに済むものを知るために使う)。</summary>
    internal static HashSet<int> ReadySegments(string source, int divisor)
    {
        var state = StateOf(source, divisor);
        lock (Gate) return new HashSet<int>(state.Ready);
    }

    /// <summary>区切りを 1 つ書き終えた (作る側が呼ぶ)。</summary>
    internal static void MarkReady(string source, int divisor, int index)
    {
        var state = StateOf(source, divisor);
        lock (Gate) state.Ready.Add(index);
        Changed?.Invoke();
    }

    /// <summary>すべての区切りを書き終えた (作る側が呼ぶ)。</summary>
    internal static void MarkComplete(string source, int divisor)
    {
        var state = StateOf(source, divisor);
        if (state.Folder != null) File.WriteAllText(Path.Combine(state.Folder, CompleteMarker), "");
        lock (Gate) state.Complete = true;
        Changed?.Invoke();
    }

    /// <summary>覚えている結果を捨てて、変化を知らせる。</summary>
    public static void NotifyChanged()
    {
        lock (Gate) Lookup.Clear();
        Changed?.Invoke();
    }

    /// <summary>source のプロキシを (どの縮小率のものも) 消す。消したプロキシの数を返す。</summary>
    public static int Delete(string source)
    {
        int n = 0;
        foreach (var d in new[] { 2, 4 })
        {
            var folder = PathFor(source, d);
            if (folder == null) continue;
            bool any = false;
            if (System.IO.Directory.Exists(folder))
            {
                try { System.IO.Directory.Delete(folder, true); any = true; } catch { /* 使用中などで消せなければ残す */ }
            }
            if (File.Exists(folder + Extension))
            {
                try { File.Delete(folder + Extension); any = true; } catch { /* 同上 */ }
            }
            if (any) n++;
        }
        NotifyChanged();
        return n;
    }

    /// <summary>プロキシのフォルダにあるファイルの合計の大きさ (バイト)。</summary>
    public static long TotalSize()
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;
        return new DirectoryInfo(Directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    }

    /// <summary>すべてのプロキシを消す。</summary>
    public static void Clear()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var f in new DirectoryInfo(Directory).EnumerateFiles("*" + Extension))
                try { f.Delete(); } catch { /* 使用中なら残す */ }
            foreach (var d in new DirectoryInfo(Directory).EnumerateDirectories())
                try { d.Delete(true); } catch { /* 同上 */ }
        }
        NotifyChanged();
    }

    /// <summary>重い動画 (プロキシを作ると効果が大きいもの) か。自動でプロキシを作るかどうかの判断に使う。</summary>
    public static bool IsHeavy(MediaInfo info) =>
        info.HasVideo && ((long)info.Width * info.Height > 1920L * 1080 || info.VideoCodec is "hevc" or "av1" or "prores" or "vp9" or "dnxhd");

    /// <summary>
    /// 自動でプロキシを作るか。重い動画 (IsHeavy) は作る。そうでなくても、この PC で実際に測って (check)、
    /// 再生に追いつかない (1 秒にデコードできるフレームが、動画の fps の 1.5 倍に足りない) か、
    /// キーフレームの間隔が長く (4 秒を超え) シークが遅い 720p 以上の動画なら作る。
    /// </summary>
    public static bool NeedsProxy(MediaInfo info, DecodeCheck? check)
    {
        if (!info.HasVideo) return false;
        if (IsHeavy(info)) return true;
        if (check == null) return false;
        double fps = info.FrameRate > 0 ? info.FrameRate : 30;
        if (check.FramesPerSecond > 0 && check.FramesPerSecond < fps * 1.5) return true;
        return check.KeyframeInterval > 4 && (long)info.Width * info.Height >= 1280L * 720;
    }

    private static readonly Dictionary<string, DecodeCheck?> Checks = new();

    /// <summary>
    /// この PC で動画を少しデコードして、速さとキーフレームの間隔を測る (1 秒前後かかる。UI スレッドから呼ばない)。
    /// デコードは再生と同じ方法 (使えればハードウェア) で行う。結果はファイル (パス・大きさ・更新日時) ごとに覚えておく。
    /// </summary>
    public static DecodeCheck? MeasureDecode(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        string key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{HardwareDecoding.Mode}";
        lock (Checks) if (Checks.TryGetValue(key, out var known)) return known;
        DecodeCheck? result = null;
        try { result = Measure(path); }
        catch { result = null; }
        lock (Checks) Checks[key] = result;
        return result;
    }

    // 同時に測っている数と、その最大 (確認・テスト用。測るのは 1 本ずつのはず)
    private static int _measuring;
    internal static int MaxConcurrentMeasurements;

    private static DecodeCheck? Measure(string path)
    {
        int now = Interlocked.Increment(ref _measuring);
        int max;
        while (now > (max = Volatile.Read(ref MaxConcurrentMeasurements)) && Interlocked.CompareExchange(ref MaxConcurrentMeasurements, now, max) != max) { }
        try { return MeasureCore(path); }
        finally { Interlocked.Decrement(ref _measuring); }
    }

    private static DecodeCheck? MeasureCore(string path)
    {
        if (!FFmpegLoader.EnsureLoaded()) return null;
        double interval = KeyframeInterval(path);
        using var r = new VideoReader(path);
        // 頭は静止していることが多いので、少し進んだ所 (1 割の位置) から測る
        double start = r.Duration > 4 ? r.Duration * 0.1 : 0;
        double step = 1 / Math.Max(1, r.FrameRate);
        if (r.GetFramePointer(start) == IntPtr.Zero) return null;   // シークと最初のフレームは数えない
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        for (double t = start + step; watch.Elapsed.TotalSeconds < 0.6 && frames < 90; t += step)
        {
            if (r.GetFramePointer(t) == IntPtr.Zero) break;
            frames++;
        }
        double fps = frames > 0 ? frames / Math.Max(1e-6, watch.Elapsed.TotalSeconds) : 0;
        return new DecodeCheck(fps, interval, r.DecoderName);
    }

    /// <summary>先頭から 30 秒ほどの映像のパケットを読んで、キーフレームの間隔 (秒) を測る (デコードはしない)。測れなければ 0。</summary>
    private static unsafe double KeyframeInterval(string path)
    {
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return 0;
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return 0;
            int v = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (v < 0) return 0;
            double tb = ffmpeg.av_q2d(fmt->streams[v]->time_base);
            long first = long.MinValue, last = long.MinValue, firstPts = long.MinValue, maxPts = long.MinValue;
            int keys = 0;
            for (int n = 0; n < 5000 && ffmpeg.av_read_frame(fmt, pkt) >= 0; n++)
            {
                try
                {
                    if (pkt->stream_index != v) continue;
                    long ts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                    if (ts == ffmpeg.AV_NOPTS_VALUE) continue;
                    if (firstPts == long.MinValue) firstPts = ts;
                    maxPts = Math.Max(maxPts, ts);
                    if ((pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
                    {
                        if (first == long.MinValue) first = ts;
                        last = ts;
                        keys++;
                    }
                    if ((maxPts - firstPts) * tb > 30) break;
                }
                finally { ffmpeg.av_packet_unref(pkt); }
            }
            if (keys >= 2) return (last - first) * tb / (keys - 1);
            // 読んだ範囲にキーフレームが 1 つしかない: 間隔は少なくとも読んだ長さ
            return keys == 1 && maxPts > firstPts ? (maxPts - firstPts) * tb : 0;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.avformat_close_input(&fmt);
        }
    }
}

/// <summary>この PC での、動画のデコードの速さの測定結果。</summary>
/// <param name="FramesPerSecond">続けてデコードしたとき、1 秒に取り出せたフレームの数 (BGRA への変換を含む)。</param>
/// <param name="KeyframeInterval">キーフレームの間隔 (秒)。長いほどシークが遅い。測れなければ 0。</param>
/// <param name="Decoder">使ったデコーダ ("VAAPI" や "ソフトウェア")。</param>
public sealed record DecodeCheck(double FramesPerSecond, double KeyframeInterval, string Decoder);

/// <summary>
/// プロキシを作る。動画をデコードし、縮小して MJPEG (すべてのフレームが単独で読める形式) で、区切りごとのファイルに書き出す。
/// どの位置へもすぐ移動できるので、プレビューでのシークが速い。音声は入れない (音声は元のファイルから読む)。
///
/// 速くするための工夫:
/// - デコード (このスレッド) と、縮小・エンコード・書き込み (別のスレッド) を同時に進める。
/// - 使える GPU があればハードウェアでデコードする (失敗したらソフトウェアでやり直す)。
/// - ソフトウェアのデコードでは、参照されないフレームのデブロッキングを省く (画質への影響は小さく、ずれも積み重ならない)。
/// - 「今見ている位置」(focus) の区切りから先に作る。
/// </summary>
public static class ProxyGenerator
{
    /// <summary>
    /// source のプロキシの、まだない区切りを folder に作る。区切りは .part に書き、書き終えたら名前を変える。
    /// 中止・失敗したときは書きかけの区切りを消して例外を投げる (書き終えた区切りは残る)。
    /// </summary>
    /// <param name="progress">進み具合 (0〜1)。すべての区切りのうち、できている割合。</param>
    /// <param name="focus">
    /// 次に作る区切りを決めるたびに呼ぶ。元の動画の時刻 (秒) を返すと、その時刻の区切りがまだなら先に作る。null なら順に作る。
    /// 別のスレッドから呼ばれる。
    /// </param>
    public static void Create(string source, string folder, int divisor, IProgress<double>? progress, CancellationToken cancel, Func<double?>? focus = null)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        cancel.ThrowIfCancellationRequested();
        divisor = Math.Clamp(divisor, 1, 16);
        using (var job = new Job(source, folder, divisor, progress, cancel, focus))
            job.Run();
        progress?.Report(1);
    }

    /// <summary>テスト用: ハードウェアでデコードしたことにして、この数だけフレームを送ったら失敗させる (ソフトウェアでのやり直しの確認)。</summary>
    internal static int TestHardwareFailAfterFrames { get; set; } = -1;

    private sealed class HardwareFailed(string message) : Exception(message);

    /// <summary>デコードする側から、書き込む側へ渡すもの。</summary>
    private readonly unsafe record struct Item(ItemKind Kind, int Segment, IntPtr Frame = default)
    {
        public AVFrame* FramePtr => (AVFrame*)Frame;
    }

    private enum ItemKind { Begin, Frame, End, Abort }

    private sealed unsafe class Job : IDisposable
    {
        private readonly string _source, _folder;
        private readonly int _divisor;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _cancel;
        private readonly Func<double?>? _focus;

        private AVFormatContext* _ic;
        private AVCodecContext* _dec;
        private AVCodec* _codec;
        private AVStream* _ist;
        private AVFrame* _frame;
        private AVPacket* _pkt;
        private int _stream;
        private bool _hardware;
        private AVPixelFormat _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private int _sentFrames;
        private long _startPts;
        private double _duration;
        private int _count;
        private int _w, _h;
        private bool _eof, _draining;

        // 区切りをまたいで持ち越すフレーム (デコードを続けて次の区切りを作るとき)
        private AVFrame* _prev;     // 区切りの始まりより前の、いちばん最後のフレーム (区切りの頭に入れる)
        private AVFrame* _carry;    // 前の区切りの終わりを越えて読んだ最初のフレーム
        private int _positionedFor = -1;   // 続けてデコードすればこの番号の区切りになる (-1 ならシークが要る)
        private bool _fresh = true;        // 開いたばかりで、まだ何も読んでいない (先頭から読むならシーク不要)

        private readonly bool[] _done;
        private int _doneCount;
        private double _segmentProgress;
        private double _lastReport = -1;

        public Job(string source, string folder, int divisor, IProgress<double>? progress, CancellationToken cancel, Func<double?>? focus)
        {
            _source = source; _folder = folder; _divisor = divisor; _progress = progress; _cancel = cancel; _focus = focus;
            try { Open(); }
            catch { Dispose(); throw; }
            _done = new bool[_count];
            var ready = ProxyStore.ReadySegments(source, divisor);
            for (int i = 0; i < _count; i++)
                if (ready.Contains(i) || File.Exists(ProxyStore.SegmentPath(folder, i))) { _done[i] = true; _doneCount++; }
        }

        private void Open()
        {
            AVFormatContext* ic = null;
            FFmpegLoader.Check(ffmpeg.avformat_open_input(&ic, _source, null, null), "ファイルを開けません");
            _ic = ic;
            FFmpegLoader.Check(ffmpeg.avformat_find_stream_info(_ic, null), "ストリーム情報を取得できません");
            AVCodec* decoder = null;
            _stream = ffmpeg.av_find_best_stream(_ic, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            FFmpegLoader.Check(_stream, "映像ストリームがありません");
            _codec = decoder;
            _ist = _ic->streams[_stream];
            if (!HardwareDecoding.Wanted || !OpenHardware()) OpenSoftware();
            _startPts = _ist->start_time != ffmpeg.AV_NOPTS_VALUE ? _ist->start_time : 0;
            // VideoReader.Duration と同じ求め方
            _duration = _ist->duration != ffmpeg.AV_NOPTS_VALUE && _ist->duration > 0
                ? _ist->duration * ffmpeg.av_q2d(_ist->time_base)
                : _ic->duration > 0 ? _ic->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            _count = ProxyStore.SegmentCount(_duration);
            // 縮小後の大きさ (偶数にそろえる)
            _w = Math.Max(2, _ist->codecpar->width / _divisor) & ~1;
            _h = Math.Max(2, _ist->codecpar->height / _divisor) & ~1;
            _frame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();
        }

        private void OpenSoftware()
        {
            var ctx = ffmpeg.avcodec_alloc_context3(_codec);
            try
            {
                FFmpegLoader.Check(ffmpeg.avcodec_parameters_to_context(ctx, _ist->codecpar), "デコーダの設定に失敗");
                ctx->thread_count = Math.Min(Environment.ProcessorCount, 16);
                // 参照されないフレームのデブロッキングを省く (プレビュー用の小さな絵なので見分けにくく、後のフレームにも響かない)
                ctx->skip_loop_filter = AVDiscard.AVDISCARD_NONREF;
                FFmpegLoader.Check(ffmpeg.avcodec_open2(ctx, _codec, null), "デコーダを開けません");
            }
            catch { ffmpeg.avcodec_free_context(&ctx); throw; }
            _dec = ctx;
            _hardware = false;
            _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        }

        private bool OpenHardware()
        {
            if (HardwareDecoding.TestPretendHardware)
            {
                OpenSoftware();
                _hardware = true;
                return true;
            }
            // 区切りの頭に入れるフレームと持ち越すフレームを手元に置くので、GPU 上のフレームを少し多めに用意させる
            var ctx = HardwareDecoding.Open(_codec, _ist, out var format, out _, out _, extraFrames: 4);
            if (ctx == null) return false;
            _dec = ctx;
            _hardware = true;
            _hwFormat = format;
            return true;
        }

        private void SwitchToSoftware()
        {
            if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
            OpenSoftware();
            FreeHeld();
            _positionedFor = -1;
            _fresh = false;
        }

        public void Run()
        {
            if (_doneCount == _count) { Complete(); return; }
            System.IO.Directory.CreateDirectory(_folder);
            Report();

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_cancel);
            var queue = new BlockingCollection<Item>(8);
            Exception? writeError = null;
            var writer = Task.Run(() =>
            {
                try { using var w = new SegmentWriter(this); w.Consume(queue); }
                catch (Exception ex) { writeError = ex; stop.Cancel(); }
                finally { foreach (var item in queue.GetConsumingEnumerable()) FreeItem(item); }
            });

            try
            {
                int last = -1;
                while (_doneCount < _count)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    int k = PickNext(last);
                    if (!ProduceSegmentWithFallback(k, queue, stop.Token)) break;
                    _done[k] = true; _doneCount++;
                    last = k;
                }
            }
            catch (OperationCanceledException) when (writeError != null) { /* 書き込み側の失敗は下で投げる */ }
            finally
            {
                queue.CompleteAdding();
                writer.Wait();
                queue.Dispose();
            }
            if (writeError != null) throw writeError;
            _cancel.ThrowIfCancellationRequested();
            if (_doneCount == _count) Complete();
        }

        private void Complete()
        {
            ProxyStore.MarkComplete(_source, _divisor);
        }

        /// <summary>次に作る区切り: 今見ている位置のものがまだなら、それ。なければ前に作ったものの次 (末尾まで行ったら先頭から)。</summary>
        private int PickNext(int last)
        {
            double? f = null;
            try { f = _focus?.Invoke(); } catch { f = null; }
            if (f is { } seconds && !double.IsNaN(seconds))
            {
                int fi = Math.Clamp(ProxyStore.SegmentIndex(seconds), 0, _count - 1);
                // 見ている位置の区切りができていれば、その先 (これから再生されるところ) を優先する
                for (int i = fi; i < _count && i < fi + 3; i++)
                    if (!_done[i]) return i;
            }
            for (int i = 1; i <= _count; i++)
            {
                int k = (last + i + _count) % _count;
                if (last < 0) k = i - 1;
                if (!_done[k]) return k;
            }
            return 0;
        }

        /// <summary>区切りを 1 つ作る。ハードウェアのデコードが失敗したら、ソフトウェアに切り替えて作り直す。</summary>
        private bool ProduceSegmentWithFallback(int k, BlockingCollection<Item> queue, CancellationToken token)
        {
            try { return ProduceSegment(k, queue, token); }
            catch (HardwareFailed)
            {
                queue.Add(new Item(ItemKind.Abort, k), token);
                SwitchToSoftware();
                return ProduceSegment(k, queue, token);
            }
        }

        private long ToTs(double seconds) => _startPts + (long)Math.Round(seconds / ffmpeg.av_q2d(_ist->time_base));

        /// <summary>区切り k のフレームをデコードして、書き込む側へ送る。</summary>
        private bool ProduceSegment(int k, BlockingCollection<Item> queue, CancellationToken token)
        {
            long tsStart = ToTs(k * ProxyStore.SegmentSeconds);
            long tsEnd = k == _count - 1 ? long.MaxValue : ToTs((k + 1) * ProxyStore.SegmentSeconds);
            if (_positionedFor != k && !(k == 0 && _fresh))
            {
                FreeHeld();
                // 区切りの始まりより前のキーフレームから読む (始まりより前の最後のフレームも、区切りの頭に入れるため)
                if (ffmpeg.av_seek_frame(_ic, _stream, tsStart, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0 && k == 0)
                    ffmpeg.av_seek_frame(_ic, -1, 0, ffmpeg.AVSEEK_FLAG_BYTE);
                ffmpeg.avcodec_flush_buffers(_dec);
                _eof = false; _draining = false;
            }
            _fresh = false;
            _positionedFor = -1;
            queue.Add(new Item(ItemKind.Begin, k), token);
            long lastSent = long.MinValue;
            bool started = false;

            void Send(AVFrame* f)
            {
                long pts = f->best_effort_timestamp;
                if (pts <= lastSent) return;   // 時刻が戻るフレーム (壊れた素材など) は書けないので飛ばす
                lastSent = pts;
                queue.Add(new Item(ItemKind.Frame, k, (IntPtr)ToSoftware(f)), token);
                if (_duration > 0)
                {
                    double segStart = k * ProxyStore.SegmentSeconds, segLen = Math.Min(ProxyStore.SegmentSeconds, _duration - segStart);
                    double t = (pts - _startPts) * ffmpeg.av_q2d(_ist->time_base);
                    _segmentProgress = segLen > 0 ? Math.Clamp((t - segStart) / segLen, 0, 1) : 0;
                    Report();
                }
            }

            void Start()
            {
                if (started) return;
                started = true;
                // 区切りの始まりちょうどにフレームがないときのため、その直前のフレームを頭に入れる
                if (k > 0 && _prev != null) Send(_prev);
            }

            if (_carry != null)
            {
                Start();
                Send(_carry);
                ReplacePrev(_carry);
                var c = _carry;
                ffmpeg.av_frame_free(&c);
                _carry = null;
            }
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!DecodeNext()) break;    // 末尾
                long pts = _frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _frame->pts;
                if (pts == ffmpeg.AV_NOPTS_VALUE) pts = lastSent == long.MinValue ? tsStart : lastSent + 1;
                _frame->best_effort_timestamp = pts;
                if (pts < tsStart) { ReplacePrev(_frame); ffmpeg.av_frame_unref(_frame); continue; }
                if (pts >= tsEnd)
                {
                    // 次の区切りの最初のフレーム。続けて次の区切りを作るなら、そのまま使う
                    _carry = ffmpeg.av_frame_clone(_frame);
                    ffmpeg.av_frame_unref(_frame);
                    _positionedFor = k + 1;
                    break;
                }
                Start();
                Send(_frame);
                ReplacePrev(_frame);
                ffmpeg.av_frame_unref(_frame);
            }
            Start();
            queue.Add(new Item(ItemKind.End, k), token);
            _segmentProgress = 0;
            return true;
        }

        private void ReplacePrev(AVFrame* f)
        {
            if (_prev != null) { var p = _prev; ffmpeg.av_frame_free(&p); }
            _prev = ffmpeg.av_frame_clone(f);
        }

        private void FreeHeld()
        {
            if (_prev != null) { var p = _prev; ffmpeg.av_frame_free(&p); _prev = null; }
            if (_carry != null) { var c = _carry; ffmpeg.av_frame_free(&c); _carry = null; }
        }

        /// <summary>デコーダから次のフレームを _frame に取り出す。末尾なら false。</summary>
        private bool DecodeNext()
        {
            if (_eof) return false;
            while (true)
            {
                int ret = ffmpeg.avcodec_receive_frame(_dec, _frame);
                if (ret == 0) return true;
                if (ret == ffmpeg.AVERROR_EOF) { _eof = true; return false; }
                if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    if (_hardware) throw new HardwareFailed("デコードに失敗: " + FFmpegLoader.ErrorText(ret));
                    FFmpegLoader.Check(ret, "デコードに失敗");
                }
                if (_draining) { _eof = true; return false; }
                ret = ffmpeg.av_read_frame(_ic, _pkt);
                if (ret < 0)
                {
                    ffmpeg.avcodec_send_packet(_dec, null);
                    _draining = true;
                    continue;
                }
                if (_pkt->stream_index == _stream)
                {
                    int sent = ffmpeg.avcodec_send_packet(_dec, _pkt);
                    if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN) && _hardware)
                    {
                        ffmpeg.av_packet_unref(_pkt);
                        throw new HardwareFailed("デコードに失敗: " + FFmpegLoader.ErrorText(sent));
                    }
                }
                ffmpeg.av_packet_unref(_pkt);
            }
        }

        /// <summary>書き込む側へ渡すフレームを作る (GPU 上のフレームはメモリへ写す)。受け取った側が av_frame_free する。</summary>
        private AVFrame* ToSoftware(AVFrame* f)
        {
            if (_hardware && TestHardwareFailAfterFrames >= 0 && _sentFrames >= TestHardwareFailAfterFrames)
            {
                TestHardwareFailAfterFrames = -1;
                throw new HardwareFailed("テスト用の失敗");
            }
            _sentFrames++;
            if (_hardware && _hwFormat != AVPixelFormat.AV_PIX_FMT_NONE && (AVPixelFormat)f->format == _hwFormat)
            {
                var sw = ffmpeg.av_frame_alloc();
                int ret = ffmpeg.av_hwframe_transfer_data(sw, f, 0);
                if (ret < 0)
                {
                    ffmpeg.av_frame_free(&sw);
                    throw new HardwareFailed("GPU からフレームを写せません: " + FFmpegLoader.ErrorText(ret));
                }
                ffmpeg.av_frame_copy_props(sw, f);
                sw->best_effort_timestamp = f->best_effort_timestamp;
                return sw;
            }
            return ffmpeg.av_frame_clone(f);
        }

        private void Report()
        {
            if (_progress == null || _count == 0) return;
            double p = Math.Clamp((_doneCount + _segmentProgress) / _count, 0, 1);
            if (p - _lastReport >= 0.005 || p < _lastReport) { _lastReport = p; _progress.Report(p); }
        }

        private static void FreeItem(Item item)
        {
            if (item.Kind == ItemKind.Frame && item.Frame != IntPtr.Zero)
            {
                var f = item.FramePtr;
                ffmpeg.av_frame_free(&f);
            }
        }

        /// <summary>縮小・エンコード・区切りのファイルへの書き込み (デコードとは別のスレッドで動く)。</summary>
        private sealed class SegmentWriter(Job job) : IDisposable
        {
            private AVFormatContext* _oc;
            private AVCodecContext* _enc;
            private AVStream* _ost;
            private SwsContext* _sws;
            private AVFrame* _scaled;
            private AVPacket* _outPkt;
            private string? _part;
            private int _segment = -1;
            private int _written;

            public void Consume(BlockingCollection<Item> queue)
            {
                foreach (var item in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        switch (item.Kind)
                        {
                            case ItemKind.Begin: BeginSegment(item.Segment); break;
                            case ItemKind.Frame: if (_segment == item.Segment) WriteFrame(item.FramePtr); break;
                            case ItemKind.End: EndSegment(item.Segment); break;
                            case ItemKind.Abort: AbortSegment(); break;
                        }
                    }
                    finally { FreeItem(item); }
                }
                AbortSegment();   // 中止で途中までになった区切りは消す
            }

            private void BeginSegment(int k)
            {
                AbortSegment();
                _segment = k;
                _written = 0;
                _part = ProxyStore.SegmentPath(job._folder, k) + ".part";
                AVFormatContext* oc = null;
                FFmpegLoader.Check(ffmpeg.avformat_alloc_output_context2(&oc, null, "matroska", _part), "出力形式を用意できません");
                _oc = oc;
                var encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
                if (encoder == null) throw new InvalidOperationException("MJPEG のエンコーダがありません");
                _ost = ffmpeg.avformat_new_stream(_oc, null);
                _enc = ffmpeg.avcodec_alloc_context3(encoder);
                _enc->width = job._w; _enc->height = job._h;
                _enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUVJ420P;
                // 元の動画と同じ時刻の目盛りを使う (フレームの時刻がずれないように)
                var ist = job._ist;
                _enc->time_base = ist->time_base;
                var rate = ist->avg_frame_rate.num != 0 ? ist->avg_frame_rate : ist->r_frame_rate;
                _enc->framerate = rate;
                _enc->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
                _enc->global_quality = 4 * ffmpeg.FF_QP2LAMBDA;    // 画質 (2 = 高画質 〜 31 = 低画質)
                _enc->thread_count = Math.Min(Environment.ProcessorCount, 16);
                if ((_oc->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0) _enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                FFmpegLoader.Check(ffmpeg.avcodec_open2(_enc, encoder, null), "MJPEG のエンコーダを開けません");
                FFmpegLoader.Check(ffmpeg.avcodec_parameters_from_context(_ost->codecpar, _enc), "出力ストリームの設定に失敗");
                _ost->time_base = _enc->time_base;
                _ost->avg_frame_rate = rate;
                FFmpegLoader.Check(ffmpeg.avio_open(&_oc->pb, _part, ffmpeg.AVIO_FLAG_WRITE), "出力ファイルを開けません");
                FFmpegLoader.Check(ffmpeg.avformat_write_header(_oc, null), "ヘッダの書き込みに失敗");
                if (_scaled == null)
                {
                    _scaled = ffmpeg.av_frame_alloc();
                    _scaled->format = (int)_enc->pix_fmt; _scaled->width = job._w; _scaled->height = job._h;
                    FFmpegLoader.Check(ffmpeg.av_frame_get_buffer(_scaled, 0), "フレームの確保に失敗");
                }
                if (_outPkt == null) _outPkt = ffmpeg.av_packet_alloc();
            }

            private void WriteFrame(AVFrame* f)
            {
                if (_enc == null) return;
                _sws = ffmpeg.sws_getCachedContext(_sws, f->width, f->height, (AVPixelFormat)f->format, job._w, job._h,
                    _enc->pix_fmt, ffmpeg.SWS_BILINEAR, null, null, null);
                if (_sws == null) throw new InvalidOperationException("縮小の準備に失敗");
                FFmpegLoader.Check(ffmpeg.av_frame_make_writable(_scaled), "フレームを書き込めません");
                ffmpeg.sws_scale(_sws, f->data, f->linesize, 0, f->height, _scaled->data, _scaled->linesize);
                // 元の動画の最初の時刻を 0 にそろえる (VideoReader も最初の時刻を引いて扱う)
                _scaled->pts = f->best_effort_timestamp - job._startPts;
                // 画質 (量子化の細かさ) はフレームごとに渡す。渡さないと 0 (= いちばん細かい) として扱われ、ファイルが大きくなる
                _scaled->quality = _enc->global_quality;
                Encode(_scaled);
                _written++;
            }

            private void Encode(AVFrame* f)
            {
                FFmpegLoader.Check(ffmpeg.avcodec_send_frame(_enc, f), "エンコードに失敗");
                while (true)
                {
                    int r = ffmpeg.avcodec_receive_packet(_enc, _outPkt);
                    if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF) break;
                    FFmpegLoader.Check(r, "エンコードに失敗");
                    ffmpeg.av_packet_rescale_ts(_outPkt, _enc->time_base, _ost->time_base);
                    _outPkt->stream_index = _ost->index;
                    FFmpegLoader.Check(ffmpeg.av_interleaved_write_frame(_oc, _outPkt), "書き込みに失敗");
                }
            }

            private void EndSegment(int k)
            {
                if (_segment != k || _oc == null) return;
                // フレームが 1 つもない区切り (長さの情報が実際より長い動画の末尾など) はファイルを作らない。元の動画を読む
                if (_written == 0) { AbortSegment(); return; }
                Encode(null);
                FFmpegLoader.Check(ffmpeg.av_write_trailer(_oc), "書き込みの終了に失敗");
                CloseOutput();
                var final = ProxyStore.SegmentPath(job._folder, k);
                File.Move(_part!, final, overwrite: true);
                _part = null;
                _segment = -1;
                ProxyStore.MarkReady(job._source, job._divisor, k);
            }

            private void AbortSegment()
            {
                CloseOutput();
                if (_part != null) { try { File.Delete(_part); } catch { /* 消せなくても続行 */ } }
                _part = null;
                _segment = -1;
            }

            private void CloseOutput()
            {
                if (_oc != null)
                {
                    if (_oc->pb != null) ffmpeg.avio_closep(&_oc->pb);
                    ffmpeg.avformat_free_context(_oc);
                    _oc = null;
                }
                if (_enc != null) { var c = _enc; ffmpeg.avcodec_free_context(&c); _enc = null; }
            }

            public void Dispose()
            {
                AbortSegment();
                if (_scaled != null) { var f = _scaled; ffmpeg.av_frame_free(&f); _scaled = null; }
                if (_outPkt != null) { var p = _outPkt; ffmpeg.av_packet_free(&p); _outPkt = null; }
                if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            }
        }

        public void Dispose()
        {
            FreeHeld();
            if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
            if (_dec != null) { var c = _dec; ffmpeg.avcodec_free_context(&c); _dec = null; }
            if (_ic != null) { var c = _ic; ffmpeg.avformat_close_input(&c); _ic = null; }
        }
    }
}
