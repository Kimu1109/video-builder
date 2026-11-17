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

        // GPU コンテキストは作ったスレッドでしか使えないので、この関数の中で作って捨てる
        using var gpu = req.UseGpu ? GpuContext.TryCreate(out _) : null;
        using var engine = new RenderEngine(gpu);
        using var bitmap = new SKBitmap(Effects.Info(w, h));

        if (req.ImageSequence)
        {
            string dir = Path.GetDirectoryName(req.Settings.Path) ?? ".";
            string name = Path.GetFileNameWithoutExtension(req.Settings.Path);
            for (int i = 0; i < total; i++)
            {
                cancel.ThrowIfCancellationRequested();
                engine.RenderToBitmap(project, scene, req.StartFrame + i, bitmap);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(Path.Combine(dir, $"{name}_{i:00000}.png"));
                data.SaveTo(fs);
                progress?.Report((i + 1, total));
            }
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
        var queue = new System.Collections.Concurrent.BlockingCollection<(SKBitmap? Frame, float[]? Audio)>(QueueSize);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Exception? encodeError = null;
        var encoder = Task.Run(() =>
        {
            try
            {
                int done = 0;
                foreach (var (frame, audio) in queue.GetConsumingEnumerable())
                {
                    if (frame != null)
                    {
                        unsafe
                        {
                            writer.WriteVideoFrame(new ReadOnlySpan<byte>((void*)frame.GetPixels(), frame.RowBytes * h), frame.RowBytes);
                        }
                        pool.Add(frame);
                    }
                    if (audio != null) writer.WriteAudio(audio);
                    progress?.Report((++done, total));
                }
                writer.Finish();
            }
            catch (Exception ex)
            {
                encodeError = ex;
                stop.Cancel();   // 描画側を止める
            }
        });

        bool ok = false;
        try
        {
            try
            {
                for (int i = 0; i < total; i++)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    int frame = req.StartFrame + i;
                    SKBitmap? target = null;
                    if (hasVideo)
                    {
                        if (!pool.TryTake(out target)) target = new SKBitmap(Effects.Info(w, h));
                        engine.RenderToBitmap(project, scene, frame, target);
                    }
                    float[]? audio = null;
                    if (hasAudio)
                    {
                        long from = project.FrameToSample(frame), to = project.FrameToSample(frame + 1);
                        audio = new float[(int)(to - from) * 2];
                        mixer.Mix(project, scene, from, audio);
                    }
                    queue.Add((target, audio), stop.Token);
                }
            }
            finally
            {
                queue.CompleteAdding();
                // エラーや中止のときも、エンコード側が終わるのを待ってから片付ける
                if (stop.IsCancellationRequested) while (queue.TryTake(out var rest)) rest.Frame?.Dispose();
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
            foreach (var b in pool) b.Dispose();
            while (queue.TryTake(out var rest)) rest.Frame?.Dispose();
            queue.Dispose();
            if (!ok)
            {
                writer.Dispose();
                try { File.Delete(s.Path); } catch { /* 途中のファイルが消せなくても続行 */ }
            }
        }
    }
}
