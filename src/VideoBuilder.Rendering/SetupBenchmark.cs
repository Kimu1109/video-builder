using System.Diagnostics;
using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Media;

namespace VideoBuilder.Rendering;

/// <summary>CPU か 1 つの GPU で、決まったシーン (1080p) を描いた時間。</summary>
/// <param name="Adapter">GPU (null なら CPU)。</param>
/// <param name="Renderer">実際に開いた GL の GPU の名前 (CPU なら "CPU")。</param>
/// <param name="WithReadbackMs">描いて、メモリへ読み戻すまでの 1 フレームの時間 (プレビューを読み戻して出すとき・CPU 描画)。</param>
/// <param name="RenderOnlyMs">GPU で描き終えるまでの時間 (画面と共有して読み戻さないとき)。CPU なら WithReadbackMs と同じ。</param>
public sealed record RenderTiming(GpuAdapter? Adapter, string Renderer, double WithReadbackMs, double RenderOnlyMs, string? Error = null)
{
    public bool IsCpu => Adapter == null;
    public bool Ok => Error == null;
}

/// <summary>1080p の H.264 を 1 フレームずつ読む時間 (ハードウェアデコードとソフトウェア)。</summary>
/// <param name="HardwareName">使ったハードウェアのデコーダ ("VAAPI" など)。使えなければ null。</param>
public sealed record DecodeTiming(string? HardwareName, double HardwareMs, double SoftwareMs, string? HardwareError);

/// <summary>1 つのエンコーダで 1080p を書き出したときの速さ (描画を含む)。</summary>
public sealed record EncoderTiming(string Encoder, string Label, double Fps, string? Error = null)
{
    public bool Ok => Error == null;
}

/// <summary>おすすめの構成を決めるための計測の結果 (設計書 22.63)。</summary>
public sealed class SetupBenchmarkResult
{
    public List<RenderTiming> Render { get; } = new();
    public DecodeTiming? Decode { get; set; }
    public List<EncoderTiming> Encoders { get; } = new();
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// このパソコンでいちばん速い構成を調べるための、短い計測 (設計書 22.63)。全部で数秒。
/// 描画 (CPU と GPU ごと)、動画のデコード (ハードウェアとソフトウェア)、書き出し (使える H.264 のエンコーダごと) を測る。
/// どのスレッドから呼んでもよい (GPU のコンテキストは計測ごとに、そのスレッドで作って捨てる)。
/// 今の設定 (GpuContext.PreferredAdapter など) は変えない。ただし書き出しの計測は、今の「使う GPU」で描く。
/// </summary>
public static class SetupBenchmark
{
    public const int Width = 1920, Height = 1080;

    /// <summary>計測の内容 (テストで短くするため)。</summary>
    public sealed class Options
    {
        /// <summary>描画で測るフレームの数 (最初の 2 フレームは数えない)。時間がかかりすぎたら途中でやめる。</summary>
        public int RenderFrames { get; init; } = 10;
        /// <summary>1 つの描画の計測にかける時間の上限。</summary>
        public TimeSpan RenderBudget { get; init; } = TimeSpan.FromSeconds(1.5);
        /// <summary>デコード (動画を敷いたシーンを描く) で測るフレームの数。</summary>
        public int DecodeFrames { get; init; } = 30;
        /// <summary>書き出しで描くフレームの数。0 なら書き出しは測らない。</summary>
        public int ExportFrames { get; init; } = 30;
        /// <summary>書き出しを GPU で描くか (今の設定の描画方式)。</summary>
        public bool ExportOnGpu { get; init; } = true;
        /// <summary>ソフトウェアのエンコーダの速度の指定 (書き出しの画面の既定と同じ medium)。</summary>
        public string SoftwarePreset { get; init; } = "medium";
        /// <summary>GPU を測るか (false なら CPU だけ)。</summary>
        public bool IncludeGpus { get; init; } = true;
        /// <summary>計測の動画などを置くフォルダ (null なら一時フォルダ)。</summary>
        public string? WorkDir { get; init; }
    }

    /// <summary>
    /// 計測に使うシーン (1080p。図形・文字・ぼかし)。どの物もフレームごとに動き、ぼかしの範囲も変わる
    /// (変わらない物は描画のキャッシュで速くなり、実際の編集より速く見えてしまうため)。video を渡せば、いちばん下に動画を敷く。
    /// </summary>
    public static Project Scene(int length = 60, string? video = null)
    {
        var p = new Project();
        p.Settings.Width = Width; p.Settings.Height = Height;
        AnimatedValue Move(double from, double to) => new() { Values = new() { from, to }, Mode = MovementMode.Linear };
        TimelineObject Shape(int layer, string color, int type, double size, double x, double y, double dx)
        {
            var o = ObjectFactory.Create(EffectRegistry.Shape, layer, 0, length);
            o.Main.Values["color"] = color; o.Main.Values["type"] = type.ToString();
            o.Main.Tracks["size"] = Move(size, size * 1.3);
            o.Draw!.Tracks["x"] = Move(x, x + dx); o.Draw!.Tracks["y"] = new AnimatedValue(y);
            p.Root.Objects.Add(o);
            return o;
        }
        if (video != null)
        {
            var v = ObjectFactory.Create(EffectRegistry.Video, 0, 0, length);
            v.Main.Values["file"] = video;
            p.Root.Objects.Add(v);
        }
        else Shape(0, "#1B2A41", 0, 0, 0, 0, 0);
        Shape(1, "#F2A900", 1, 420, -600, -120, 1200);
        Shape(2, "#3A6EA5", 2, 360, 300, 160, -600);
        Shape(3, "#C0392B", 3, 300, 520, -60, -400).Draw!.Tracks["alpha"] = Move(40, 90);
        var blurred = Shape(4, "#27AE60", 1, 500, -200, -200, 500);
        ObjectFactory.AddFilter(blurred, "filter.blur").Tracks["radius"] = Move(12, 40);
        var text = ObjectFactory.Create(EffectRegistry.Text, 5, 0, length);
        text.Main.Values["text"] = "Video Builder 1080p ベンチマーク";
        text.Main.Tracks["size"] = Move(64, 96);
        text.Draw!.Tracks["y"] = Move(300, 200);
        p.Root.Objects.Add(text);
        return p;
    }

    public static SetupBenchmarkResult Run(Options? options = null, IProgress<(string Step, string? Name, double Fraction)>? progress = null, CancellationToken cancel = default)
    {
        options ??= new Options();
        cancel.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var result = new SetupBenchmarkResult();
        var adapters = options.IncludeGpus ? SafeAdapters().Where(a => !a.IsSoftware).ToList() : new List<GpuAdapter>();
        bool ffmpegOk = FFmpegLoader.EnsureLoaded();
        var encoders = ffmpegOk && options.ExportFrames > 0 ? EncoderCandidates() : new List<(string Encoder, string Label)>();
        int steps = 1 + adapters.Count + (ffmpegOk ? 1 : 0) + encoders.Count, done = 0;
        // Step は訳す文 ({0} に Name が入る)。Name は GPU やエンコーダの名前 (訳さない)
        void Step(string text, string? name = null) => progress?.Report((text, name, (double)done++ / Math.Max(1, steps)));

        var project = Scene();
        Step("CPU の描画を測っています");
        result.Render.Add(MeasureRender(null, project, options, cancel));
        foreach (var a in adapters)
        {
            cancel.ThrowIfCancellationRequested();
            Step("GPU ({0}) の描画を測っています", a.Name);
            result.Render.Add(MeasureRender(a, project, options, cancel));
        }

        string dir = options.WorkDir ?? Path.Combine(Path.GetTempPath(), $"vb-setup-{Guid.NewGuid():N}");
        bool ownDir = options.WorkDir == null;
        Directory.CreateDirectory(dir);
        try
        {
            if (ffmpegOk)
            {
                cancel.ThrowIfCancellationRequested();
                Step("動画のデコードを測っています");
                result.Decode = MeasureDecode(dir, options);
            }
            foreach (var (encoder, label) in encoders)
            {
                cancel.ThrowIfCancellationRequested();
                Step("書き出し ({0}) を測っています", label);
                result.Encoders.Add(MeasureExport(dir, encoder, label, options, cancel));
            }
        }
        finally
        {
            if (ownDir) try { Directory.Delete(dir, true); } catch { /* 消せなくても続ける */ }
        }
        progress?.Report(("完了", null, 1));
        result.Elapsed = sw.Elapsed;
        return result;
    }

    private static IReadOnlyList<GpuAdapter> SafeAdapters()
    {
        try { return GpuContext.Adapters(); } catch { return Array.Empty<GpuAdapter>(); }
    }

    /// <summary>測る H.264 のエンコーダ (ソフトウェアの x264 と、使えるハードウェアのもの)。</summary>
    internal static List<(string Encoder, string Label)> EncoderCandidates()
    {
        var list = new List<(string, string)>();
        if (MediaWriter.IsEncoderAvailable("libx264")) list.Add(("libx264", "H.264 (x264)"));
        List<HardwareEncoder> hardware;
        try { hardware = HardwareEncoders.FindAvailable(); } catch { hardware = new(); }
        foreach (var h in hardware.Where(h => h.Codec == "H.264")) list.Add((h.Encoder, $"H.264 ({h.Api})"));
        return list;
    }

    /// <summary>描画を測る。adapter が null なら CPU。</summary>
    internal static RenderTiming MeasureRender(GpuAdapter? adapter, Project project, Options options, CancellationToken cancel)
    {
        // GPU のコンテキストは作ったスレッドでしか使えないので、専用のスレッドで測る
        RenderTiming? result = null;
        bool cancelled = false;
        var thread = new Thread(() =>
        {
            try { result = MeasureRenderHere(adapter, project, options, cancel); }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { result = new RenderTiming(adapter, adapter?.Name ?? "CPU", double.NaN, double.NaN, ex.Message); }
        }) { IsBackground = true, Name = "SetupBenchmark" };
        thread.Start();
        thread.Join();
        if (cancelled) throw new OperationCanceledException(cancel);
        return result!;
    }

    private static RenderTiming MeasureRenderHere(GpuAdapter? adapter, Project project, Options options, CancellationToken cancel)
    {
        GpuContext? gpu = null;
        if (adapter != null)
        {
            gpu = GpuContext.TryCreate(adapter.Id, out var error);
            if (gpu == null) return new RenderTiming(adapter, adapter.Name, double.NaN, double.NaN, error ?? "GPU を使えません");
            if (gpu.Adapter != adapter)
            {
                var renderer = gpu.Renderer;
                gpu.Dispose();
                return new RenderTiming(adapter, renderer, double.NaN, double.NaN, $"この GPU で開けません (開いたのは {renderer})");
            }
        }
        try
        {
            using var engine = new RenderEngine(gpu) { Draft = true };
            var scene = project.Root;
            var info = Effects.Info(Width, Height).WithColorType(engine.ReadbackColorType);
            using var bitmap = new SKBitmap(info);
            double withReadback = Measure(f => engine.RenderToBitmap(project, scene, f, bitmap));
            double renderOnly = withReadback;
            if (gpu != null)
            {
                // 読み戻さない: GPU の描画先に描いて、GPU が描き終えるのを待つ
                gpu.MakeCurrent();
                using var surface = SKSurface.Create(gpu.Context, true, new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                if (surface != null)
                    renderOnly = Measure(f =>
                    {
                        engine.RenderToGpuSurface(project, scene, f, surface);
                        gpu.Context.Flush(true, true);
                    });
            }
            return new RenderTiming(adapter, gpu?.Renderer ?? "CPU", withReadback, renderOnly);
        }
        finally { gpu?.Dispose(); }

        double Measure(Action<int> render)
        {
            // はじめの数フレームは、シェーダの用意や GPU のクロックが上がるのを待つ間なので数えない (2 フレーム以上、0.25 秒まで)
            var warm = Stopwatch.StartNew();
            for (int f = 0; f < 2 || f < 12 && warm.Elapsed < TimeSpan.FromSeconds(0.25); f++) render(f);
            var budget = Stopwatch.StartNew();
            var times = new List<double>();
            for (int f = 2; f < 2 + options.RenderFrames; f++)
            {
                cancel.ThrowIfCancellationRequested();
                var t = Stopwatch.StartNew();
                render(f);
                times.Add(t.Elapsed.TotalMilliseconds);
                if (budget.Elapsed > options.RenderBudget && times.Count >= 3) break;
            }
            return Median(times);
        }
    }

    /// <summary>
    /// 1080p の H.264 (計測用に作る) を敷いたシーンを、ハードウェアとソフトウェアのデコードで描く時間 (再生と同じく先読みする)。
    /// 描画は今の「使う GPU」(gpuForDecode が false なら CPU)。GPU 描画なら、ハードウェアデコードのフレームを GPU 上のまま使う分も含めて比べる。
    /// </summary>
    private static DecodeTiming MeasureDecode(string dir, Options options)
    {
        string path = Path.Combine(dir, "decode.mp4");
        try { WriteClip(path, options.DecodeFrames + 4); }
        catch (Exception ex) { return new DecodeTiming(null, double.NaN, double.NaN, "計測用の動画を作れません: " + ex.Message); }

        DecodeTiming? result = null;
        var thread = new Thread(() =>
        {
            try { result = MeasureDecodeHere(path, options); }
            catch (Exception ex) { result = new DecodeTiming(null, double.NaN, double.NaN, ex.Message); }
        }) { IsBackground = true, Name = "SetupBenchmark" };
        thread.Start();
        thread.Join();
        return result!;
    }

    private static DecodeTiming MeasureDecodeHere(string path, Options options)
    {
        // ハードウェアデコードが使えるか (使えなければ、ソフトウェアだけ測る)
        string? name = null, error = null;
        using (var probe = new VideoReader(path))
        {
            if (probe.IsHardware) name = probe.DecoderName;
            else error = probe.HardwareError ?? "ハードウェアデコードを使えません";
        }
        var project = Scene(options.DecodeFrames + 4, path);
        double Measure(bool hardware, out bool usedHardware)
        {
            using var gpu = options.ExportOnGpu ? GpuContext.TryCreate(out _) : null;
            using var engine = new RenderEngine(gpu) { Draft = true, Lookahead = true };
            engine.Media.UseProxies = false;
            engine.Media.AllowHardwareDecoding = hardware;
            var info = Effects.Info(Width, Height).WithColorType(engine.ReadbackColorType);
            using var bitmap = new SKBitmap(info);
            for (int f = 0; f < 2; f++) engine.RenderToBitmap(project, project.Root, f, bitmap);
            var t = Stopwatch.StartNew();
            int n = 0;
            for (int f = 2; f < 2 + options.DecodeFrames; f++, n++) engine.RenderToBitmap(project, project.Root, f, bitmap);
            usedHardware = engine.Media.DecoderSummary.Length > 0 && !engine.Media.DecoderSummary.StartsWith("ソフトウェア", StringComparison.Ordinal);
            return t.Elapsed.TotalMilliseconds / Math.Max(1, n);
        }
        double software = Measure(false, out _);
        if (name == null) return new DecodeTiming(null, double.NaN, software, error);
        double hardware = Measure(true, out bool used);
        // 途中でソフトウェアに切り替わったら、ハードウェアは使えないとみなす
        return used ? new DecodeTiming(name, hardware, software, null) : new DecodeTiming(null, double.NaN, software, "ハードウェアデコードに失敗しました");
    }

    /// <summary>
    /// 計測用の 1080p・30fps の H.264 を作る (x264 の ultrafast)。動く模様に粒状のノイズを足して、
    /// 撮影した動画に近いビットレート (デコードの重さ) にする (なめらかな模様だけだと、ほとんど 0 の時間で読めてしまう)。
    /// </summary>
    private static void WriteClip(string path, int frames)
    {
        var s = new ExportSettings { Path = path, Width = Width, Height = Height, Fps = new Rational(30, 1), VideoCodec = "libx264", AudioCodec = "", Crf = 20, Preset = "ultrafast" };
        using var writer = new MediaWriter(s);
        var data = new byte[Width * Height * 4];
        var noise = new byte[Width * Height + 4096];
        new Random(1).NextBytes(noise);
        for (int f = 0; f < frames; f++)
        {
            int shift = f * 7919 % 4096;
            Parallel.For(0, Height, y =>
            {
                int row = y * Width * 4;
                for (int x = 0; x < Width; x++)
                {
                    int i = row + x * 4;
                    int grain = (noise[y * Width + x + shift] & 31) - 16;
                    data[i] = (byte)Math.Clamp((x + f * 4) % 256 + grain, 0, 255);
                    data[i + 1] = (byte)Math.Clamp((y + f * 2) % 256 + grain, 0, 255);
                    data[i + 2] = (byte)Math.Clamp(((x ^ y) + f) % 256 + grain, 0, 255);
                    data[i + 3] = 255;
                }
            });
            writer.WriteVideoFrame(data, Width * 4);
        }
        writer.Finish();
    }

    /// <summary>計測のシーンを 1080p で書き出す速さ (描画を含む)。</summary>
    private static EncoderTiming MeasureExport(string dir, string encoder, string label, Options options, CancellationToken cancel)
    {
        string path = Path.Combine(dir, $"export-{encoder}.mp4");
        try
        {
            var stats = new ExportStats();
            Exporter.Run(new ExportRequest
            {
                Project = Scene(options.ExportFrames), SceneId = "root", StartFrame = 0, EndFrame = options.ExportFrames - 1,
                UseGpu = options.ExportOnGpu, Stats = stats,
                Settings = new ExportSettings { Path = path, VideoCodec = encoder, AudioCodec = "", Crf = 20, Preset = options.SoftwarePreset },
            }, null, cancel);
            // エンコーダを開く時間 (NVENC の CUDA の用意など) は、短い書き出しでは大きく効くので除く
            return new EncoderTiming(encoder, label, stats.SteadyFramesPerSecond);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new EncoderTiming(encoder, label, double.NaN, ex.Message); }
        finally { try { File.Delete(path); } catch { } }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToList();
        int m = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2;
    }
}
