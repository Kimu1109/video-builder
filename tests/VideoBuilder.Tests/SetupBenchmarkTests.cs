using VideoBuilder.Media;
using VideoBuilder.Rendering;
using Xunit;

namespace VideoBuilder.Tests;

/// <summary>おすすめの構成のための計測 (設計書 22.63)。短い設定で、測った値がそろうかを確かめる。</summary>
public class SetupBenchmarkTests
{
    [Fact]
    public void Benchmark_measures_rendering_decoding_and_export_and_reports_progress()
    {
        var steps = new List<(string Step, string? Name, double Fraction)>();
        var progress = new SyncProgress<(string, string?, double)>(steps.Add);
        var r = SetupBenchmark.Run(new SetupBenchmark.Options { RenderFrames = 3, DecodeFrames = 3, ExportFrames = 4 }, progress);
        Console.WriteLine($"BENCH setup elapsed={r.Elapsed.TotalSeconds:0.0}s " + string.Join(" ", r.Render.Select(t => $"{t.Renderer}={t.WithReadbackMs:0.0}/{t.RenderOnlyMs:0.0}{t.Error}")));

        var cpu = Assert.Single(r.Render, t => t.IsCpu);
        Assert.True(cpu.Ok && cpu.WithReadbackMs > 0 && cpu.RenderOnlyMs == cpu.WithReadbackMs, cpu.Error);
        // GPU は、開けたものなら描くだけの方が読み戻すより速い (読み戻さない分)
        foreach (var g in r.Render.Where(t => !t.IsCpu && t.Ok))
            Assert.True(g.RenderOnlyMs <= g.WithReadbackMs * 1.5, $"{g.Renderer}: {g.RenderOnlyMs} / {g.WithReadbackMs}");
        if (GpuSelectionTests.GpuRequired) Assert.Contains(r.Render, t => !t.IsCpu && t.Ok);

        if (FFmpegLoader.EnsureLoaded() && MediaWriter.IsEncoderAvailable("libx264"))
        {
            Assert.NotNull(r.Decode);
            Assert.True(r.Decode!.SoftwareMs > 0);
            Assert.True(r.Decode.HardwareName == null || r.Decode.HardwareMs > 0);
            var x264 = Assert.Single(r.Encoders, e => e.Encoder == "libx264");
            Assert.True(x264.Ok && x264.Fps > 0, x264.Error);
        }
        // 進み具合は 0 から 1 まで増えていき、最後に「完了」
        Assert.Equal("完了", steps[^1].Step);
        Assert.Equal(1, steps[^1].Fraction);
        Assert.True(steps.Zip(steps.Skip(1)).All(p => p.First.Fraction <= p.Second.Fraction));
        Assert.Contains(steps, p => p.Name != null && p.Step.Contains("{0}"));
    }

    [Fact]
    public void Benchmark_scene_changes_every_frame_so_caches_do_not_hide_the_cost()
    {
        var p = SetupBenchmark.Scene(10);
        using var engine = new RenderEngine();
        using var a = engine.RenderImage(p, p.Root, 1);
        using var b = engine.RenderImage(p, p.Root, 8);
        using var pa = a.PeekPixels(); using var pb = b.PeekPixels();
        Assert.NotEqual(pa.GetPixelSpan().ToArray(), pb.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Benchmark_can_be_cancelled()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SetupBenchmark.Run(new SetupBenchmark.Options { RenderFrames = 3, ExportFrames = 0 }, null, cancel.Token));
    }

    /// <summary>呼んだスレッドですぐに報告する IProgress (Progress は別のスレッドで呼ぶことがある)。</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
