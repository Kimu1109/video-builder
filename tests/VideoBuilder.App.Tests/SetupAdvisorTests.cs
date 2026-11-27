using VideoBuilder.App.Services;
using VideoBuilder.App.Views;
using VideoBuilder.Media;
using VideoBuilder.Rendering;
using Xunit;

namespace VideoBuilder.App.Tests;

/// <summary>おすすめの構成の決め方 (設計書 22.63)。計測の結果を作って渡す。</summary>
public class SetupAdvisorTests
{
    private static readonly GpuAdapter Intel = new(0, "Mesa Intel(R) UHD Graphics 770 (ADL-S GT1)", "Intel", "/dev/dri/card1", "/dev/dri/renderD128", false);
    private static readonly GpuAdapter Nvidia = new(1, "NVIDIA GeForce RTX 4070 Laptop GPU/PCIe/SSE2", "NVIDIA", "/dev/dri/card2", "/dev/dri/renderD129", false);
    private static readonly GpuAdapter Llvmpipe = new(2, "llvmpipe (LLVM 17.0.6, 256 bits)", "Mesa", "", "", true);
    private static readonly GpuAdapter[] All = { Intel, Nvidia, Llvmpipe };

    private static SetupBenchmarkResult Result(double cpu, (GpuAdapter A, double Readback, double Only)[] gpus,
        DecodeTiming? decode = null, params (string Encoder, string Label, double Fps)[] encoders)
    {
        var r = new SetupBenchmarkResult();
        r.Render.Add(new RenderTiming(null, "CPU", cpu, cpu));
        foreach (var g in gpus) r.Render.Add(new RenderTiming(g.A, g.A.Name, g.Readback, g.Only));
        r.Decode = decode;
        foreach (var e in encoders) r.Encoders.Add(new EncoderTiming(e.Encoder, e.Label, e.Fps));
        return r;
    }

    private static SetupEnvironment Linux(string? screen = "Mesa Intel(R) UHD Graphics 770 (ADL-S GT1)") => new(true, false, screen, All);

    private static SetupItem Find(List<SetupItem> items, string name) => Assert.Single(items, i => i.Name == name);

    [Fact]
    public void The_fastest_gpu_is_chosen_and_kept_automatic_when_auto_already_picks_it()
    {
        var s = new AppSettings();
        var r = Result(13, new[] { (Intel, 11.5, 3.0), (Nvidia, 4.0, 0.5) });
        var items = SetupAdvisor.Recommend(r, s, Linux());
        // 自動で NVIDIA が選ばれるので、変えない
        var gpu = Find(items, "使う GPU");
        Assert.False(gpu.Changes);
        Assert.Contains("NVIDIA", gpu.Recommended);
        Assert.False(Find(items, "描画方式").Changes);

        // 決め打ちで Intel にしていれば、自動 (NVIDIA) に戻す
        s.GpuAdapter = Intel.Id;
        gpu = Find(SetupAdvisor.Recommend(r, s, Linux()), "使う GPU");
        Assert.True(gpu.Changes);
        gpu.Apply!(s);
        Assert.Equal("", s.GpuAdapter);
    }

    [Fact]
    public void A_slower_dedicated_gpu_is_not_chosen()
    {
        // 外付けの方が遅い (自動では外付けを選ぶ) なら、内蔵の GPU を決め打ちで選ぶ
        var s = new AppSettings();
        var items = SetupAdvisor.Recommend(Result(30, new[] { (Intel, 4.0, 1.0), (Nvidia, 9.0, 2.0) }), s, Linux());
        var gpu = Find(items, "使う GPU");
        Assert.True(gpu.Changes);
        gpu.Apply!(s);
        Assert.Equal(Intel.Id, s.GpuAdapter);
    }

    [Fact]
    public void The_cpu_is_recommended_only_when_it_is_clearly_faster()
    {
        var s = new AppSettings();
        var cpuFaster = SetupAdvisor.Recommend(Result(3, new[] { (Intel, 10.0, 6.0) }), s, Linux());
        var backend = Find(cpuFaster, "描画方式");
        Assert.True(backend.Changes);
        backend.Apply!(s);
        Assert.Equal(RenderBackend.Cpu, s.RenderBackend);
        // CPU を選んだら、GPU の項目は出さない
        Assert.DoesNotContain(cpuFaster, i => i.Name == "使う GPU" || i.Name == "プレビューを描く GPU");

        // ほぼ同じ速さなら GPU のまま (自動)
        s = new AppSettings();
        Assert.False(Find(SetupAdvisor.Recommend(Result(9.5, new[] { (Intel, 10.0, 6.0) }), s, Linux()), "描画方式").Changes);

        // GPU がなければ自動 (CPU で描く)。GPU に決め打ちしていれば自動に戻す
        s = new AppSettings { RenderBackend = RenderBackend.Gpu };
        var none = Find(SetupAdvisor.Recommend(Result(10, Array.Empty<(GpuAdapter, double, double)>()), s, Linux()), "描画方式");
        Assert.True(none.Changes);
        none.Apply!(s);
        Assert.Equal(RenderBackend.Auto, s.RenderBackend);
    }

    [Fact]
    public void Preview_moves_to_the_screen_gpu_only_when_it_stays_faster_for_heavier_scenes()
    {
        var s = new AppSettings();
        // 画面の GPU (Intel) は描くだけで 3.0 ms、NVIDIA は描いて読み戻して 4.0 ms (描くだけなら 0.5 ms)。
        // 2 倍重い場面では Intel 6.0 ms / NVIDIA 1.0 + 3.5 = 4.5 ms なので、使う GPU のまま
        var keep = Find(SetupAdvisor.Recommend(Result(13, new[] { (Intel, 11.5, 3.0), (Nvidia, 4.0, 0.5) }), s, Linux()), "プレビューを描く GPU");
        Assert.False(keep.Changes);

        // 読み戻しがとても重い (PCIe が遅いなど) なら、画面の GPU で描く
        var move = Find(SetupAdvisor.Recommend(Result(13, new[] { (Intel, 11.5, 2.0), (Nvidia, 10.0, 0.5) }), s, Linux()), "プレビューを描く GPU");
        Assert.True(move.Changes);
        move.Apply!(s);
        Assert.True(s.PreviewOnScreenGpu);

        // 画面の GPU と使う GPU が同じ・画面の GPU が分からない・Windows のときは出さない
        var r = Result(13, new[] { (Intel, 11.5, 2.0), (Nvidia, 10.0, 0.5) });
        Assert.DoesNotContain(SetupAdvisor.Recommend(r, s, Linux("NVIDIA GeForce RTX 4070 Laptop GPU/PCIe/SSE2")), i => i.Name == "プレビューを描く GPU");
        Assert.DoesNotContain(SetupAdvisor.Recommend(r, s, Linux(null)), i => i.Name == "プレビューを描く GPU");
        Assert.DoesNotContain(SetupAdvisor.Recommend(r, s, new SetupEnvironment(false, true, null, All)), i => i.Name == "プレビューを描く GPU");
    }

    [Fact]
    public void Hardware_decoding_is_kept_unless_it_is_much_slower()
    {
        var s = new AppSettings();
        var gpus = new[] { (Nvidia, 4.0, 0.5) };
        Assert.False(Find(SetupAdvisor.Recommend(Result(13, gpus, new DecodeTiming("NVDEC (CUDA)", 4.5, 6.3, null)), s, Linux()), "動画のデコード").Changes);
        // 少し遅いだけなら使う (CPU を空けられる)
        Assert.False(Find(SetupAdvisor.Recommend(Result(13, gpus, new DecodeTiming("VAAPI", 7.0, 6.0, null)), s, Linux()), "動画のデコード").Changes);
        var slow = Find(SetupAdvisor.Recommend(Result(13, gpus, new DecodeTiming("VAAPI", 12.0, 6.0, null)), s, Linux()), "動画のデコード");
        Assert.True(slow.Changes);
        slow.Apply!(s);
        Assert.Equal(HardwareDecodeMode.Off, s.HardwareDecode);
        // 使えないときは変えない (自動でもソフトウェアに戻る)
        s = new AppSettings();
        Assert.False(Find(SetupAdvisor.Recommend(Result(13, gpus, new DecodeTiming(null, double.NaN, 6.0, "VAAPI がありません")), s, Linux()), "動画のデコード").Changes);
    }

    [Fact]
    public void The_fastest_encoder_becomes_the_first_export_format()
    {
        var s = new AppSettings();
        var gpus = new[] { (Nvidia, 4.0, 0.5) };
        var fast = Find(SetupAdvisor.Recommend(Result(13, gpus, null, ("libx264", "H.264 (x264)", 89), ("h264_nvenc", "H.264 (NVENC)", 165), ("h264_vaapi", "H.264 (VAAPI)", 9)), s, Linux()),
            "書き出しの形式 (最初に選ぶもの)");
        Assert.True(fast.Changes);
        Assert.Equal("H.264 (NVENC)", fast.Recommended);
        fast.Apply!(s);
        Assert.Equal("h264_nvenc", s.PreferredVideoEncoder);

        // x264 がいちばん速ければ、既定 (空) に戻す。失敗したエンコーダは数えない
        var r = Result(13, gpus, null, ("libx264", "H.264 (x264)", 89));
        r.Encoders.Add(new EncoderTiming("h264_nvenc", "H.264 (NVENC)", double.NaN, "開けません"));
        var back = Find(SetupAdvisor.Recommend(r, s, Linux()), "書き出しの形式 (最初に選ぶもの)");
        back.Apply!(s);
        Assert.Equal("", s.PreferredVideoEncoder);
    }

    [Fact]
    public void Settings_fixed_by_the_environment_or_already_best_are_not_changed()
    {
        var s = new AppSettings { GpuAdapter = Intel.Id, ScreenGl = ScreenGlMode.Glx };
        var r = Result(13, new[] { (Intel, 11.5, 3.0), (Nvidia, 4.0, 0.5) });
        var items = SetupAdvisor.Recommend(r, s, new SetupEnvironment(true, false, null, All, GpuFixedByEnvironment: true, ScreenGlFixedByEnvironment: true));
        Assert.False(Find(items, "使う GPU").Changes);
        Assert.DoesNotContain(items, i => i.Name == "画面の描画 (Linux)");

        // GLX なら EGL を勧める (起動し直しが要る)
        var gl = Find(SetupAdvisor.Recommend(r, s, Linux()), "画面の描画 (Linux)");
        Assert.True(gl.Changes && gl.NeedsRestart);

        // すべておすすめどおりなら、使っても何も変わらない
        var best = new AppSettings();
        Assert.False(SetupAdvisor.ApplyAll(SetupAdvisor.Recommend(r, best, Linux()), best));
    }

    [Fact]
    public void Recommended_setup_is_suggested_once_on_startup_but_not_for_tool_launches()
    {
        Assert.True(SetupDialog.ShouldSuggestOnStartup(new AppSettings(), Array.Empty<string>()));
        Assert.False(SetupDialog.ShouldSuggestOnStartup(new AppSettings { SetupSuggested = true }, Array.Empty<string>()));
        foreach (var arg in new[] { "--screenshot", "--startup-time", "--report-preview", "--report-setup", "--no-setup" })
            Assert.False(SetupDialog.ShouldSuggestOnStartup(new AppSettings(), new[] { arg }));
    }
}
