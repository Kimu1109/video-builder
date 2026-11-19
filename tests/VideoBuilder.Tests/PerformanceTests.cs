using VideoBuilder.Core;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>パフォーマンスチェックの測定 (直近の値の集計・出力の内訳・ベンチマーク)。</summary>
public class PerformanceTests
{
    [Fact]
    public void Rolling_stats_keep_only_the_latest_values()
    {
        var s = new RollingStats(4);
        foreach (var v in new double[] { 100, 1, 2, 3, 4 }) s.Add(v);
        Assert.Equal(new double[] { 1, 2, 3, 4 }, s.Values);
        var sum = s.Summarize();
        Assert.Equal((4, 2.5, 1, 4, 4, 4), (sum.Count, sum.Average, sum.Min, sum.Max, sum.P95, sum.Last));
        Assert.Equal(5, s.Total);
        s.Clear();
        Assert.Equal(0, s.Summarize().Count);
    }

    private static Project RedProject()
    {
        var p = new Project { Settings = new ProjectSettings { Width = 160, Height = 120, FrameRate = "30" } };
        var o = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 30);
        o.Main.Values["type"] = "0";
        o.Main.Values["color"] = "#FF0000";
        p.Root.Objects.Add(o);
        return p;
    }

    [Fact]
    public void Benchmark_renders_the_requested_number_of_frames()
    {
        var p = RedProject();
        var r = RenderBenchmark.Run(p, p.Root, 25, 10, 80, 60, useGpu: false, draft: true, null, CancellationToken.None);
        Assert.Equal(10, r.Frames);
        Assert.Equal(10, r.Milliseconds.Count);
        Assert.Equal((80, 60), (r.Width, r.Height));
        Assert.True(r.FramesPerSecond > 0);
        Assert.True(r.Milliseconds.Max >= r.Milliseconds.P95 && r.Milliseconds.P95 >= r.Milliseconds.Min);
    }

    [Fact]
    public void Export_reports_the_breakdown_of_its_time()
    {
        if (!FFmpegLoader.EnsureLoaded())
        {
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG"));
            return;
        }
        var p = RedProject();
        var path = Path.Combine(Path.GetTempPath(), $"vb-perf-{Guid.NewGuid():N}.mp4");
        try
        {
            var stats = new ExportStats();
            Exporter.Run(new ExportRequest
            {
                Project = p, SceneId = "root", StartFrame = 0, EndFrame = 29, Stats = stats,
                Settings = new ExportSettings { Path = path },
            }, null, CancellationToken.None);
            Assert.True(stats.Finished);
            Assert.Equal(30, stats.TotalFrames);
            Assert.Equal(30, stats.Frames);
            Assert.True(stats.Render > TimeSpan.Zero && stats.Encode > TimeSpan.Zero && stats.Convert > TimeSpan.Zero && stats.Audio > TimeSpan.Zero);
            Assert.True(stats.Elapsed >= stats.Render);
            Assert.True(stats.FramesPerSecond > 0);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Image_sequence_export_also_reports_its_time()
    {
        var p = RedProject();
        var dir = Path.Combine(Path.GetTempPath(), $"vb-perf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var stats = new ExportStats();
            Exporter.Run(new ExportRequest { Project = p, SceneId = "root", StartFrame = 0, EndFrame = 2, ImageSequence = true, Stats = stats, Settings = new ExportSettings { Path = Path.Combine(dir, "s.png") } }, null, CancellationToken.None);
            Assert.Equal(3, stats.Frames);
            Assert.True(stats.Render > TimeSpan.Zero && stats.Encode > TimeSpan.Zero);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
