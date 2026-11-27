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
        // ---- 先読み (設計書 22.61) ----
        // 裏のスレッドが Targets の位置を順に取り出し、取り出したフレームの置き場を押さえて Ready に並べる。
        // 裏のスレッドが動いている (Pending) 間、描画のスレッドはデコーダの状態に触らず、Ready に並んだフレームだけを使う
        // (置き場ごとの読み出し TryGetYuv・EnsureBgra・TryTakeHardwareFrame は、ほかの置き場への書き込みと同時でよい)。
        // Gate で守る: Pending・Targets・Working・Ready
        public readonly object Gate = new();
        /// <summary>裏で先読みしている間は、その作業。</summary>
        public Task? Pending;
        /// <summary>これから先読みする位置 (秒。古い順)。</summary>
        public readonly List<double> Targets = new();
        /// <summary>今先読みしている位置。なければ NaN。</summary>
        public double Working = double.NaN;
        /// <summary>先読みし終えたフレーム (古い順)。置き場は押さえてある。</summary>
        public readonly List<Ahead> Ready = new();
        /// <summary>今の描画で使っているフレームの置き場 (先読みしている間は押さえておく)。なければ IntPtr.Zero。</summary>
        public IntPtr Current;
        /// <summary>開いておく位置 (Preload)。</summary>
        public double PendingPosition;
        /// <summary>直前の Fetch で取り出したフレーム (デコーダの置き場)。取り出せなければ IntPtr.Zero。</summary>
        public IntPtr Data;
        /// <summary>Data のフレームの目印と大きさ (先読みのスレッドが動いている間も読めるよう、取り出したときに控える)。</summary>
        public long DataFrameId;
        public int DataWidth, DataHeight, DataDivisor;
        /// <summary>今の画像を作った置き場 (zeroCopy の画像は置き場を指すので、置き場が変われば作り直す)。</summary>
        public IntPtr ImageData;
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

    /// <summary>ハードウェアデコードしたフレームを、メモリへ写さずに GPU 上のまま変換した数 (計測・テスト用。設計書 22.58)。</summary>
    public long GpuHardwareFrames => _yuv?.HardwareConverted ?? 0;

    /// <summary>そのうち 10 ビット (P010) のもの (計測・テスト用。設計書 22.62)。</summary>
    public long GpuHardwareFrames10Bit => _yuv?.HardwareConverted10Bit ?? 0;

    /// <summary>ハードウェアデコードしたフレームを GPU 上のまま使えないとき、その理由。</summary>
    public string? GpuHardwareError => _yuv?.HardwareError;

    /// <summary>開いている動画のデコーダ (確認用。描画のスレッドから読む)。例: "VAAPI x1, ソフトウェア x1"。</summary>
    public string DecoderSummary => string.Join(", ", _byPath.Values.SelectMany(l => l).Where(e => e.Reader != null)
        .GroupBy(e => e.Reader!.DecoderName).Select(g => $"{g.Key} x{g.Count()} (GPU 上のまま {g.Sum(e => e.Reader!.HardwareFramesKept)})"));

    // GPU 上のまま受け取るフレームの種類 (YuvTextures を作ってから決まる。取り込めなくなった種類は外れる)
    private volatile int _hardwareKinds;

    /// <summary>動画のフレームを YUV のまま GPU へ送るか (GPU 描画中で、使えないと分かっていなければ)。</summary>
    private bool WantsYuv => _yuvState >= 0 && DrawingOnGpu;

    /// <summary>今の描画が GPU で描かれるか (GPU の描画先を作れずに CPU で描くときは false にして、GPU へ送らない)。</summary>
    public bool DrawingOnGpu { get; set; } = true;

    /// <summary>プレビュー用: プロキシがあれば元の動画の代わりに使う。</summary>
    public bool UseProxies { get; set; }

    /// <summary>
    /// 動画をハードウェアでデコードしてよいか (既定 true。設定の「動画のデコード」に従う)。
    /// false なら、このキャッシュで開く動画はソフトウェアでデコードする (おすすめの構成の計測で比べるため。設計書 22.63)。
    /// </summary>
    public bool AllowHardwareDecoding { get; set; } = true;

    /// <summary>今開いているデコーダの数 (計測・テスト用)。</summary>
    public int ReaderCount => _byPath.Values.Sum(l => l.Count);
    /// <summary>開いているデコーダがこれまでにシークした回数の合計 (計測・テスト用)。</summary>
    public int TotalSeeks => _byPath.Values.SelectMany(l => l).Sum(e => e.Reader?.SeekCount ?? 0);

    /// <summary>新しいフレームの描画を始める。RenderEngine が描画のはじめに呼ぶ。</summary>
    public void BeginFrame()
    {
        // 先読みは待たない (要るフレームだけを、取り出すときに待つ。設計書 22.61)
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
    public int PendingLookaheads => _byPath.Values.Sum(l => l.Count(e => { lock (e.Gate) return e.Pending != null || e.Ready.Count > 0; }));

    /// <summary>先読みし終えた・先読みしている・する予定のフレームの数の合計 (計測・テスト用)。</summary>
    internal int AheadFrames => _byPath.Values.Sum(l => l.Sum(e => { lock (e.Gate) return e.Ready.Count + (double.IsNaN(e.Working) ? 0 : 1) + e.Targets.Count; }));

    /// <summary>裏の先読みが一区切りつくまで待つ (テスト用。先読みは続けたまま)。</summary>
    internal void WaitForLookahead()
    {
        foreach (var e in _byPath.Values.SelectMany(l => l))
        {
            Task? pending;
            lock (e.Gate) pending = e.Pending;
            try { pending?.Wait(); } catch { }
        }
    }

    /// <summary>先読みしておいたフレームをそのまま使えた回数 (計測・テスト用)。</summary>
    public long LookaheadHits => Interlocked.Read(ref _lookaheadHits);
    private long _lookaheadHits;

    /// <summary>
    /// 何フレーム先まで先読みするか。1 なら次のフレームだけ (以前と同じ)。2 なら、次のフレームを読み終えたら、その次も続けて読む
    /// (デコードに時間のかかるフレームがあっても、前のフレームの合成の間に追いつける)。環境変数 VIDEO_BUILDER_LOOKAHEAD_DEPTH で変えられる。
    /// </summary>
    public static int LookaheadDepth { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("VIDEO_BUILDER_LOOKAHEAD_DEPTH"), out var d) ? Math.Clamp(d, 1, VideoReader.Slots - 1) : 2;

    /// <summary>
    /// この描画で使ったデコーダに、次の描画で要るフレームを裏で読ませておく (デコードと変換を、合成と同時に進める)。
    /// key は、この描画で GetVideoFrame / Prefetch に渡したもの。先読みが終わるまで、そのデコーダには触らない
    /// (次の BeginFrame で終わるのを待つ)。
    /// </summary>
    /// <param name="next">オブジェクトごとに、次の描画 (と、その次の描画) で要る位置。近い順。</param>
    public void Lookahead(IReadOnlyList<(string Key, string Path, IReadOnlyList<double> Seconds, bool Loop)> next)
    {
        foreach (var (key, path, times, loop) in next)
        {
            if (!_assigned.TryGetValue(key, out var e) || e.Reader is not { } r) continue;
            var wanted = new List<double>();
            foreach (var seconds in times.Take(LookaheadDepth))
            {
                double t = Wrap(path, seconds, loop);
                // 別のファイル (プロキシの次の区切り・プロキシと元の動画の切り替わり) なら、このデコーダではそこから先を読まない
                if (EffectivePath(path, t) != e.Path) break;
                // 今のフレームより前 (ループで先頭に戻るなど) なら、読み進められないのでやめる
                if (!double.IsNaN(e.Position) && t < e.Position - 1e-9) break;
                // 今のフレームの範囲内なら、読むものがない
                if (!double.IsNaN(e.Position) && Math.Abs(t - e.Position) < 1e-9) continue;
                wanted.Add(t);
            }
            if (wanted.Count == 0) continue;
            lock (e.Gate)
            {
                // すでに読んだもの・読む予定のものは足さない。先読みの数は LookaheadDepth まで (置き場の数で決まる)
                int planned = e.Ready.Count + (double.IsNaN(e.Working) ? 0 : 1) + e.Targets.Count;
                foreach (var t in wanted)
                {
                    if (planned >= LookaheadDepth) break;
                    if (IsPlanned(e, t)) continue;
                    // 予定より前の位置を後から足すと、読む順が戻ってシークになるので足さない
                    double last = e.Targets.Count > 0 ? e.Targets[^1] : !double.IsNaN(e.Working) ? e.Working : e.Ready.Count > 0 ? e.Ready[^1].Seconds : double.NegativeInfinity;
                    if (t < last) continue;
                    e.Targets.Add(t);
                    planned++;
                }
                if (e.Targets.Count == 0 || e.Pending != null) continue;
                // 今のフレームの画像は、合成が終わるまで読まれる。先読みの変換で上書きしないよう、置き場を押さえておく
                if (e.Current == IntPtr.Zero) { r.PinCurrentFrame(); e.Current = r.CurrentFramePointer; }
                e.Pending = Task.Run(() => LookaheadLoop(e, r));
            }
        }
    }

    private static bool IsPlanned(VideoEntry e, double t) =>
        e.Ready.Any(a => Math.Abs(a.Seconds - t) < 1e-9) || Math.Abs(e.Working - t) < 1e-9 || e.Targets.Any(x => Math.Abs(x - t) < 1e-9);

    /// <summary>先読みし終えたフレーム。</summary>
    private readonly record struct Ahead(double Seconds, IntPtr Data, long FrameId, int Width, int Height, int Divisor);

    /// <summary>裏のスレッドで、Targets の位置を順に取り出す。取り出したフレームの置き場は押さえて Ready に並べる。</summary>
    private void LookaheadLoop(VideoEntry e, VideoReader r)
    {
        while (true)
        {
            double t;
            lock (e.Gate)
            {
                if (e.Targets.Count == 0) { e.Pending = null; Monitor.PulseAll(e.Gate); return; }
                t = e.Targets[0];
                e.Targets.RemoveAt(0);
                e.Working = t;
            }
            var item = new Ahead(t, IntPtr.Zero, long.MinValue, 0, 0, r.OutputDivisor);
            try
            {
                // GPU 上のまま受け取れる種類は、最初のフレームを変換したあと (YuvTextures を作ったとき) に決まり、取り込めなくなると減る。
                // 先読みが続くと描画のスレッドはデコーダに触らないので、ここで今の値にする (設計書 22.62)
                if (r.PreferYuv) r.AcceptHardwareFrames = (HardwareFrameKinds)_hardwareKinds;
                var p = r.GetFramePointer(t);
                if (p != IntPtr.Zero) r.PinFrame(p);
                item = item with { Data = p, FrameId = r.CurrentFrameId, Width = r.FrameWidth, Height = r.FrameHeight };
            }
            catch { /* 先読みの失敗は無視する (本番の取り出しで、もう一度試す) */ }
            lock (e.Gate)
            {
                e.Working = double.NaN;
                // 取り出せなかったものは並べない (本番の取り出しで、もう一度試す)
                if (item.Data != IntPtr.Zero) e.Ready.Add(item);
                Monitor.PulseAll(e.Gate);
            }
        }
    }

    /// <summary>
    /// 先読みしておいた seconds のフレームがあれば (読んでいる途中なら読み終えるのを待って) 受け取る。なければ false。
    /// 受け取ったフレームより前の先読みは捨て、前のフレームの置き場を放す。
    /// </summary>
    private bool TakeAhead(VideoEntry e, double seconds, int divisor, out Ahead found)
    {
        found = default;
        var r = e.Reader!;
        lock (e.Gate)
        {
            if (e.Pending == null && e.Ready.Count == 0) return false;
            // 読む予定 (または読んでいる途中) なら、読み終えるのを待つ
            while ((Math.Abs(e.Working - seconds) < 1e-9 || e.Targets.Any(x => Math.Abs(x - seconds) < 1e-9)) && e.Pending != null)
                Monitor.Wait(e.Gate);
            int i = e.Ready.FindIndex(a => Math.Abs(a.Seconds - seconds) < 1e-9);
            if (i < 0 || e.Ready[i].Divisor != divisor) return false;
            found = e.Ready[i];
            for (int k = 0; k < i; k++) r.UnpinFrame(e.Ready[k].Data);
            e.Ready.RemoveRange(0, i + 1);
            if (e.Current != IntPtr.Zero && e.Current != found.Data && e.Ready.All(a => a.Data != e.Current)) r.UnpinFrame(e.Current);
            e.Current = found.Data;
            Interlocked.Increment(ref _lookaheadHits);
            return true;
        }
    }

    /// <summary>先読みをやめて、裏のスレッドが終わるのを待つ。押さえた置き場もすべて放す (デコーダを描画のスレッドで使えるようにする)。</summary>
    private static void StopLookahead(VideoEntry e)
    {
        Task? pending;
        lock (e.Gate)
        {
            e.Targets.Clear();
            pending = e.Pending;
        }
        try { pending?.Wait(); } catch { /* 中で握りつぶしているので来ない */ }
        lock (e.Gate)
        {
            if (e.Ready.Count == 0 && e.Current == IntPtr.Zero) return;
            e.Ready.Clear();
            e.Current = IntPtr.Zero;
        }
        e.Reader?.UnpinFrame();
    }

    /// <summary>先読みしていない (裏のスレッドがデコーダを使っていない) か。</summary>
    private static bool Idle(VideoEntry e)
    {
        lock (e.Gate) return e.Pending == null;
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

    /// <summary>すべての先読みをやめる。</summary>
    private void StopAllLookahead()
    {
        foreach (var e in _byPath.Values.SelectMany(l => l)) StopLookahead(e);
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
            bool planned;
            lock (e.Gate) planned = IsPlanned(e, t);
            if (planned) cost = 0;                                                                          // 先読みしてある
            else if (double.IsNaN(e.Position)) cost = 1000;
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
    private VideoEntry Open(string path, string original, int sourceWidth)
    {
        var e = new VideoEntry { Path = path, Original = original };
        bool hw = AllowHardwareDecoding;
        // プロキシの区切りは、元の動画と同じ時刻で書いてあるので、時刻をそのまま使って読む
        try { e.Reader = File.Exists(path) ? new VideoReader(path, absoluteTime: path != original, allowHardware: hw) : null; }
        catch { e.Reader = null; }
        if (e.Reader == null && original != path)
        {
            // プロキシが壊れていて開けなければ、元の動画を読む
            e.Path = original;
            try { e.Reader = File.Exists(original) ? new VideoReader(original, false, allowHardware: hw) : null; }
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
            foreach (var e in list.Where(e => e.Original == keep.Original && e.Path != e.Original && e.UsedInFrame != _frame && Idle(e) && e.Preloading == null && e.PreloadKey == null).ToList())
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
        e.Lost = false;
        if (r == null) { e.Data = IntPtr.Zero; return; }
        int divisor = DivisorFor(e, original ?? e.Path, detail);
        // 前と同じ位置 (Prefetch のあとの GetVideoFrame・止めたまま描き直すとき): 作った画像をそのまま使う
        // (以前はデコーダに同じフレームを取り出させて、同じフレームだったので画像を使い回していた。先読みの最中は、デコーダに触らない)
        if (e.Image != null && !double.IsNaN(e.Position) && Math.Abs(seconds - e.Position) < 1e-9 && e.DataDivisor == divisor)
        {
            e.Data = IntPtr.Zero;
            return;
        }
        // 先読みしてあれば、それを使う (裏のスレッドは、そのまま次を読み続ける)
        if (TakeAhead(e, seconds, divisor, out var ahead))
        {
            e.Position = seconds;
            e.Data = ahead.Data;
            e.DataFrameId = ahead.FrameId; e.DataWidth = ahead.Width; e.DataHeight = ahead.Height; e.DataDivisor = divisor;
            return;
        }
        // 先読みしていない位置 (シーク・コマ飛ばし・細かさが変わった): 先読みをやめて、このスレッドで取り出す
        StopLookahead(e);
        e.Position = seconds;
        r.OutputDivisor = divisor;
        // GPU 上のまま受け取れる種類は、YUV の変換 (YuvTextures) を作ると決まる。ハードウェアデコードなら先に作っておく
        // (10 ビットの動画は、メモリ上の 8 ビットの YUV にならないので、作るきっかけがない。設計書 22.62)
        if (r.IsHardware && WantsYuv) EnsureYuv();
        r.PreferYuv = WantsYuv;
        r.AcceptHardwareFrames = WantsYuv ? (HardwareFrameKinds)_hardwareKinds : HardwareFrameKinds.None;
        long before = r.CurrentFrameId;
        e.Data = r.GetFramePointer(seconds);
        e.DataFrameId = r.CurrentFrameId; e.DataWidth = r.FrameWidth; e.DataHeight = r.FrameHeight; e.DataDivisor = divisor;
        e.Lost = e.Data == IntPtr.Zero && _zeroCopy && r.CurrentFrameId != before;
    }

    /// <summary>直前の Fetch で取り出したフレームの画像を返す (同じフレームなら前の画像)。描画のスレッドから呼ぶ。</summary>
    private SKImage? ImageOf(VideoEntry e)
    {
        var r = e.Reader;
        if (r == null) return null;
        if (e.Data == IntPtr.Zero) return e.Lost ? null : e.Image;
        // 先読みのスレッドが動いていることがあるので、デコーダの今の状態ではなく、取り出したときに控えた目印と大きさを使う
        int w = e.DataWidth, h = e.DataHeight;
        if (e.Image == null || e.FrameId != e.DataFrameId || e.Image.Width != w || e.Image.Height != h || (_zeroCopy && e.ImageData != e.Data))
        {
            // zeroCopy の古い画像は、デコーダの置き場を指しているだけなので、取り出した後に手放してよい
            e.Image?.Dispose();
            // アニメーション WebP・GIF は透明な部分を残す (VideoReader がアルファを掛けて渡す)
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, r.HasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque);
            if (_zeroCopy) e.Image = SKImage.FromPixels(info, e.Data, w * 4);
            else if (HardwareImage(r, e.Data, w, h) is { } gpuFrame) e.Image = gpuFrame;
            else if (r.TryGetYuv(e.Data, out var planes))
            {
                // YUV のまま持っている: GPU で色を変換する。できなければ、ここで BGRA にしてから送る
                e.Image = YuvImage(planes) ?? Upload(info, r.EnsureBgra(e.Data));
            }
            else e.Image = Upload(info, e.Data);
            e.FrameId = e.DataFrameId;
            e.ImageData = e.Data;
        }
        e.Data = IntPtr.Zero;
        LastFrameSize = (e.Image.Width, e.Image.Height);
        return e.Image;
    }

    /// <summary>
    /// フレームを GPU 上のまま持っていれば、GPU の中で RGBA にした画像。持っていない・取り込めないときは null
    /// (取り込めなければ、TryGetYuv でメモリへ写して続ける)。描画のスレッドから呼ぶ。
    /// </summary>
    private SKImage? HardwareImage(VideoReader r, IntPtr data, int width, int height)
    {
        if (_yuv == null || !DrawingOnGpu) return null;
        var frame = r.TryTakeHardwareFrame(data);
        if (frame == null) return null;
        var image = _yuv.ConvertHardware(frame, width, height);
        _hardwareKinds = (int)_yuv.HardwareKinds;
        return image;
    }

    /// <summary>YUV の面を GPU へ送って、GPU で RGBA にした画像。GPU 描画中でない・使えないときは null。描画のスレッドから呼ぶ。</summary>
    private SKImage? YuvImage(in VideoBuilder.Media.YuvPlanes planes)
    {
        if (_gpuContext == null || !DrawingOnGpu || _yuvState < 0) return null;
        if (!EnsureYuv()) return null;
        return _yuv!.Convert(planes);
    }

    /// <summary>YUV の変換 (GPU) を用意する。作れなければ false (このあと YUV のまま送らない)。描画のスレッドから呼ぶ。</summary>
    private bool EnsureYuv()
    {
        if (_yuv != null) return true;
        if (_gpuContext == null || _yuvState < 0) return false;
        _yuv = YuvTextures.Create(_gpuContext, out var error);
        if (_yuv == null) { GpuYuvError = error; _yuvState = -1; return false; }
        _yuvState = 1;
        _hardwareKinds = (int)_yuv.HardwareKinds;
        return true;
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
            StopLookahead(oldest.e);
            oldest.e.Reader?.Dispose(); oldest.e.Image?.Dispose();
            _byPath[oldest.Key].Remove(oldest.e);
            if (_byPath[oldest.Key].Count == 0) _byPath.Remove(oldest.Key);
        }
    }

    public void Dispose()
    {
        StopAllLookahead();
        FinishPreloads(wait: true);
        foreach (var e in _byPath.Values.SelectMany(l => l)) { e.Reader?.Dispose(); e.Image?.Dispose(); }
        foreach (var (img, _) in _images.Values) img?.Dispose();
        _byPath.Clear(); _assigned.Clear(); _images.Clear();
        _yuv?.Dispose();
        _yuv = null;
    }
}
