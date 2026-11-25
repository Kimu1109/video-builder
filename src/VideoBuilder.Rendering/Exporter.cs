using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Media;

namespace VideoBuilder.Rendering;

public sealed class ExportRequest
{
    public required Project Project { get; init; }
    public required string SceneId { get; init; }
    public required ExportSettings Settings { get; init; }
    public int StartFrame { get; init; }
    /// <summary>最後のフレーム (含む)。</summary>
    public int EndFrame { get; init; }
    /// <summary>連番 PNG で書き出す (Settings.Path の拡張子の前に番号を付ける)。</summary>
    public bool ImageSequence { get; init; }
    /// <summary>GPU で描画する (使えなければ自動的に CPU になる)。</summary>
    public bool UseGpu { get; init; }
    /// <summary>かかった時間の内訳を書き込む先 (パフォーマンスチェック用)。null なら測らない。</summary>
    public ExportStats? Stats { get; init; }
}

/// <summary>UI なしでプロジェクトをファイルに書き出す。</summary>
public static class Exporter
{
    /// <param name="progress">(書き終えたフレーム数, 全フレーム数)</param>
    public static void Run(ExportRequest req, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        var project = req.Project;
        var scene = project.FindScene(req.SceneId) ?? project.Root;
        int total = Math.Max(0, req.EndFrame - req.StartFrame + 1);
        if (total == 0) throw new InvalidOperationException("出力するフレームがありません");
        int w = project.Settings.Width, h = project.Settings.Height;
        var stats = req.Stats;
        stats?.Start(total);
        try { RunCore(req, project, scene, total, w, h, stats, progress, cancel); }
        finally { stats?.Stop(); }
    }

    /// <summary>
    /// 連番 PNG を書き出す。描画はこのスレッドで順に、PNG のエンコードと書き込みは複数のスレッドで同時に行う
    /// (PNG のエンコードは 1 枚ずつ独立していて重い。1080p で 1 枚 100 ms ほど)。
    /// </summary>
    private static void WriteImageSequence(ExportRequest req, RenderEngine engine, Project project, Scene scene, int total, int w, int h,
        ExportStats? stats, IProgress<(int Done, int Total)>? progress, CancellationToken cancel, Action<Action, Action<TimeSpan>?> measure)
    {
        string dir = Path.GetDirectoryName(req.Settings.Path) ?? ".";
        string name = Path.GetFileNameWithoutExtension(req.Settings.Path);
        int workers = Math.Clamp(Environment.ProcessorCount, 1, 8);
        // 描いたフレームの待ち行列。エンコードが追いつかなければ描画側が待つ (使うビットマップは workers * 2 + 1 枚まで)
        using var queue = new System.Collections.Concurrent.BlockingCollection<(int Index, SKBitmap Bitmap)>(workers);
        var pool = new System.Collections.Concurrent.ConcurrentBag<SKBitmap>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Exception? error = null;
        int done = 0;
        var encoders = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            var watch = new System.Diagnostics.Stopwatch();
            foreach (var (index, bitmap) in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (stop.IsCancellationRequested) continue;
                    watch.Restart();
                    using (var pixmap = bitmap.PeekPixels())
                    using (var data = pixmap?.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("PNG に圧縮できません"))
                    using (var fs = File.Create(Path.Combine(dir, $"{name}_{index:00000}.png")))
                        data.SaveTo(fs);
                    stats?.AddEncode(watch.Elapsed);
                    stats?.FrameDone();
                    progress?.Report((Interlocked.Increment(ref done), total));
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref error, ex, null);
                    stop.Cancel();
                }
                finally { pool.Add(bitmap); }
            }
        })).ToArray();

        try
        {
            for (int i = 0; i < total; i++)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (!pool.TryTake(out var bitmap)) bitmap = new SKBitmap(ReadbackInfo(engine, w, h));
                try { measure(() => engine.RenderToBitmap(project, scene, req.StartFrame + i, bitmap), stats == null ? null : stats.AddRender); }
                catch { pool.Add(bitmap); throw; }
                try { queue.Add((i, bitmap), stop.Token); }
                catch { pool.Add(bitmap); throw; }
            }
        }
        catch (OperationCanceledException) when (error != null) { /* エンコード側の失敗は下で投げる */ }
        finally
        {
            queue.CompleteAdding();
            Task.WaitAll(encoders);
            foreach (var b in pool) b.Dispose();
        }
        if (error != null) throw error;
        cancel.ThrowIfCancellationRequested();
    }

    /// <summary>描いたフレームを受け取るビットマップの形式。GPU 描画なら RGBA (読み戻しで並べ替えない)。</summary>
    private static SKImageInfo ReadbackInfo(RenderEngine engine, int w, int h) =>
        Effects.Info(w, h).WithColorType(engine.ReadbackColorType);

    private static void RunCore(ExportRequest req, Project project, Scene scene, int total, int w, int h, ExportStats? stats,
        IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        var watch = new System.Diagnostics.Stopwatch();
        // 測るときだけ時間を数える
        void Measure(Action action, Action<TimeSpan>? add)
        {
            if (stats == null || add == null) { action(); return; }
            watch.Restart();
            action();
            add(watch.Elapsed);
        }

        // GPU コンテキストは作ったスレッドでしか使えないので、この関数の中で作って捨てる
        using var gpu = req.UseGpu ? GpuContext.TryCreate(out _) : null;
        // フレームを順に描くので、動画の次のフレームを裏でデコードしておく
        using var engine = new RenderEngine(gpu) { Lookahead = true };

        if (req.ImageSequence)
        {
            WriteImageSequence(req, engine, project, scene, total, w, h, stats, progress, cancel, Measure);
            return;
        }

        var s = req.Settings;
        s.Width = w; s.Height = h; s.Fps = project.Settings.Fps; s.SampleRate = project.Settings.SampleRate;
        using var mixer = new AudioMixer();
        using var writer = new MediaWriter(s);
        bool hasVideo = writer.HasVideo, hasAudio = writer.HasAudio;

        // 描画 (このスレッド) とエンコード (別スレッド) を同時に進める。
        // 描いたフレームは数枚ぶんだけ待ち行列に置き、エンコードが追いつかなければ描画側が待つ
        const int QueueSize = 3;
        var pool = new System.Collections.Concurrent.ConcurrentBag<SKBitmap>();
        var audioPool = new System.Collections.Concurrent.ConcurrentBag<float[]>();
        // 描画したフレームは、描画側のスレッドでエンコーダ用の形式 (YUV) に変換してから渡す。
        // 変換をエンコード側でやると、その間エンコーダへ次のフレームを渡せないため
        var queue = new System.Collections.Concurrent.BlockingCollection<(IntPtr Frame, float[]? Audio)>(QueueSize);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Exception? encodeError = null;
        var encoder = Task.Run(() =>
        {
            try
            {
                int done = 0;
                var encodeWatch = new System.Diagnostics.Stopwatch();
                foreach (var (frame, audio) in queue.GetConsumingEnumerable())
                {
                    encodeWatch.Restart();
                    if (frame != IntPtr.Zero) writer.WritePreparedVideoFrame(frame);
                    if (audio != null) { writer.WriteAudio(audio); audioPool.Add(audio); }
                    stats?.AddEncode(encodeWatch.Elapsed);
                    stats?.FrameDone();
                    progress?.Report((++done, total));
                }
                encodeWatch.Restart();
                writer.Finish();
                stats?.AddEncode(encodeWatch.Elapsed);
            }
            catch (Exception ex)
            {
                encodeError = ex;
                stop.Cancel();   // 描画側を止める
            }
        });

        // GPU 描画なら、読み戻しを待たずに次のフレームを描く (GPU がフレーム N を描く間に、フレーム N-1 を色変換する)
        AsyncReadback? readback = null;
        if (gpu != null && hasVideo)
        {
            readback = AsyncReadback.Create(gpu, w, h, out var readbackError);
            if (readback == null && stats != null) stats.AsyncReadbackError = readbackError;
        }
        // 読み戻しの途中のフレーム (描いた順)
        var inFlight = new Queue<int>();

        // フレームを描いて、エンコーダ用の形式に変換する (読み戻しを使わないとき、使えなくなったとき)
        IntPtr RenderNow(int frame)
        {
            if (!pool.TryTake(out var target)) target = new SKBitmap(ReadbackInfo(engine, w, h));
            try
            {
                Measure(() => engine.RenderToBitmap(project, scene, frame, target), stats == null ? null : stats.AddRender);
                watch.Restart();
                IntPtr prepared;
                unsafe
                {
                    prepared = writer.PrepareVideoFrame(new ReadOnlySpan<byte>((void*)target.GetPixels(), target.RowBytes * h), target.RowBytes,
                        rgba: target.ColorType == SKColorType.Rgba8888);
                }
                stats?.AddConvert(watch.Elapsed);
                return prepared;
            }
            finally { pool.Add(target); }
        }

        // 映像 (変換済み) と、そのフレームの音声を、エンコードの待ち行列に入れる
        void Enqueue(int frame, IntPtr prepared)
        {
            float[]? audio = null;
            if (hasAudio)
            {
                long from = project.FrameToSample(frame), to = project.FrameToSample(frame + 1);
                int n = (int)(to - from) * 2;
                // 音声の入れ物も使い回す (長さはフレームごとに 1 サンプル違うことがある)
                if (!audioPool.TryTake(out audio) || audio.Length != n) audio = new float[n];
                var buffer = audio;
                Measure(() => mixer.Mix(project, scene, from, buffer), stats == null ? null : stats.AddAudio);
            }
            watch.Restart();
            queue.Add((prepared, audio), stop.Token);
            stats?.AddWait(watch.Elapsed);
        }

        // いちばん古い読み戻しを受け取って、待ち行列に入れる。読み戻せなければ、読み戻しをやめて描き直す
        void FinishOldest()
        {
            int frame = inFlight.Dequeue();
            IntPtr prepared = IntPtr.Zero;
            watch.Restart();
            bool done = readback!.Finish((pixels, rowBytes) =>
            {
                unsafe { prepared = writer.PrepareVideoFrame(new ReadOnlySpan<byte>((void*)pixels, rowBytes * h), rowBytes, rgba: true); }
            });
            stats?.AddConvert(watch.Elapsed);
            if (!done)
            {
                if (stats != null) stats.AsyncReadbackError = "読み戻せませんでした";
                // 途中のものも含めて、これまでどおりの描き方に戻す
                var rest = inFlight.ToList();
                inFlight.Clear();
                readback.Dispose();
                readback = null;
                Enqueue(frame, RenderNow(frame));
                foreach (var f in rest) Enqueue(f, RenderNow(f));
                return;
            }
            stats?.AddAsyncReadback();
            Enqueue(frame, prepared);
        }

        bool ok = false;
        try
        {
            try
            {
                for (int i = 0; i < total; i++)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    int frame = req.StartFrame + i;
                    if (!hasVideo) { Enqueue(frame, IntPtr.Zero); continue; }
                    if (readback == null) { Enqueue(frame, RenderNow(frame)); continue; }
                    if (inFlight.Count >= AsyncReadback.Depth) FinishOldest();
                    if (readback == null) { Enqueue(frame, RenderNow(frame)); continue; }
                    var target = readback;
                    Measure(() =>
                    {
                        engine.RenderToGpuSurface(project, scene, frame, target.Surface);
                        target.Start();
                    }, stats == null ? null : stats.AddRender);
                    inFlight.Enqueue(frame);
                    // 1 つ前のフレームは、このフレームを GPU が描いている間に受け取る
                    if (inFlight.Count >= 2) FinishOldest();
                }
                while (inFlight.Count > 0 && readback != null) FinishOldest();
            }
            finally
            {
                queue.CompleteAdding();
                // エラーや中止のときも、エンコード側が終わるのを待ってから片付ける
                if (stop.IsCancellationRequested) while (queue.TryTake(out _)) { }
                encoder.Wait();
            }
            if (encodeError != null) throw encodeError;
            cancel.ThrowIfCancellationRequested();
            ok = true;
        }
        catch (OperationCanceledException) when (encodeError != null)
        {
            throw encodeError;
        }
        finally
        {
            readback?.Dispose();
            foreach (var b in pool) b.Dispose();
            while (queue.TryTake(out _)) { }
            queue.Dispose();
            if (!ok)
            {
                writer.Dispose();
                try { File.Delete(s.Path); } catch { /* 途中のファイルが消せなくても続行 */ }
            }
        }
    }
}
