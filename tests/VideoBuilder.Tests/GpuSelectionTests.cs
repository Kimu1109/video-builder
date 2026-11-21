using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>GPU が複数あるときの選び方 (CPU 内蔵より外付けを優先・設定で選んだもの)。</summary>
public class GpuSelectionTests
{
    private static readonly GpuAdapter Intel = new(0, "Mesa Intel(R) UHD Graphics 630 (CFL GT2)", "Intel", "/dev/dri/card0", "/dev/dri/renderD128", false);
    private static readonly GpuAdapter Nvidia = new(1, "NVIDIA GeForce RTX 3060/PCIe/SSE2", "NVIDIA Corporation", "/dev/dri/card1", "", false);
    private static readonly GpuAdapter Llvmpipe = new(2, "llvmpipe (LLVM 20.1.2, 256 bits)", "Mesa", "", "", true);

    [Fact]
    public void Automatic_choice_prefers_a_dedicated_gpu_over_the_integrated_one()
    {
        // 内蔵 GPU が先に列挙されても、外付けを選ぶ
        Assert.Equal(Nvidia, GpuContext.ChooseAdapter(new[] { Intel, Nvidia, Llvmpipe }, null));
        Assert.Equal(Nvidia, GpuContext.ChooseAdapter(new[] { Intel, Nvidia }, ""));

        var apu = new GpuAdapter(0, "AMD Radeon Graphics (radeonsi, renoir, LLVM 17)", "AMD", "/dev/dri/card0", "", false);
        var rx = new GpuAdapter(1, "AMD Radeon RX 6600 (radeonsi, navi23, LLVM 17)", "AMD", "/dev/dri/card1", "", false);
        Assert.Equal(rx, GpuContext.ChooseAdapter(new[] { apu, rx }, null));
        var arc = new GpuAdapter(1, "Mesa Intel(R) Arc(tm) A770 Graphics (DG2)", "Intel", "/dev/dri/card1", "", false);
        Assert.Equal(arc, GpuContext.ChooseAdapter(new[] { Intel, arc }, null));

        // 本物の GPU がなければソフトウェア (使うかどうかは呼ぶ側が決める)
        Assert.Equal(Llvmpipe, GpuContext.ChooseAdapter(new[] { Llvmpipe }, null));
        Assert.Null(GpuContext.ChooseAdapter(Array.Empty<GpuAdapter>(), null));
    }

    [Fact]
    public void Chosen_gpu_is_used_and_found_again_by_name_when_its_device_file_changes()
    {
        var list = new[] { Intel, Nvidia };
        Assert.Equal(Intel, GpuContext.ChooseAdapter(list, Intel.Id));
        // 再起動などでデバイスファイルの番号が変わっても、同じ名前の GPU を選ぶ
        Assert.Equal(Intel, GpuContext.ChooseAdapter(list, Intel.Name + "|/dev/dri/card7"));
        // 環境変数などで名前の一部を書いてもよい
        Assert.Equal(Intel, GpuContext.ChooseAdapter(list, "intel"));
        // 見つからなければ自動
        Assert.Equal(Nvidia, GpuContext.ChooseAdapter(list, "Some Removed GPU|/dev/dri/card3"));
    }

    [Fact]
    public void Hardware_decoding_tries_the_chosen_gpu_first()
    {
        if (!OperatingSystem.IsLinux() || !FFmpegLoader.EnsureLoaded()) return;
        try
        {
            HardwareDecoding.DeviceHint = () => ("vaapi", "/dev/dri/renderD128");
            Assert.Equal(FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, HardwareDecoding.DeviceOrder()[0]);
            HardwareDecoding.DeviceHint = () => ("cuda", "");
            Assert.Equal(FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, HardwareDecoding.DeviceOrder()[0]);
            HardwareDecoding.DeviceHint = () => throw new InvalidOperationException("一覧を作れない");
            Assert.Equal(FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, HardwareDecoding.DeviceOrder()[0]);   // 既定の順に戻る
        }
        finally { HardwareDecoding.DeviceHint = null; HardwareDecoding.ResetDevices(); }
    }

    // Windows (DXGI) のアダプタ: 名前は DXGI のもの、ID は PCI の ID
    private static readonly GpuAdapter WinIntel = new(0, "Intel(R) UHD Graphics", "Intel", "", "", false, 0x0000_0000_0001_2345, "8086:A788:3FC817AA");
    private static readonly GpuAdapter WinNvidia = new(1, "NVIDIA GeForce RTX 4070 Laptop GPU", "NVIDIA", "", "", false, 0x0000_0000_0001_6789, "10DE:2860:3FC817AA");
    private static readonly GpuAdapter Warp = new(2, "Microsoft Basic Render Driver", "Microsoft", "", "", true, 0x0000_0000_0000_9ABC, "1414:008C:00000000");

    [Fact]
    public void Windows_adapters_prefer_the_dedicated_gpu_and_are_found_again_after_a_reboot()
    {
        var list = new[] { WinIntel, WinNvidia, Warp };
        Assert.Equal(WinNvidia, GpuContext.ChooseAdapter(list, null));
        Assert.Equal("Intel(R) UHD Graphics|8086:A788:3FC817AA", WinIntel.Id);
        Assert.Equal(WinIntel, GpuContext.ChooseAdapter(list, WinIntel.Id));
        // LUID は起動するたびに変わるが、ID には入れていないので、同じ GPU が選ばれる
        var afterReboot = new[] { WinIntel with { Luid = 0x7777 }, WinNvidia with { Luid = 0x8888 }, Warp };
        Assert.Equal(afterReboot[0], GpuContext.ChooseAdapter(afterReboot, WinIntel.Id));
        Assert.Equal(Warp, GpuContext.ChooseAdapter(new[] { Warp }, null));
        Assert.Equal("Intel(R) UHD Graphics", WinIntel.Label);
    }

    [Fact]
    public void Angle_renderer_names_are_matched_to_the_adapter_and_warp_counts_as_software()
    {
        const string angleNvidia = "ANGLE (NVIDIA, NVIDIA GeForce RTX 4070 Laptop GPU (0x00002860) Direct3D11 vs_5_0 ps_5_0, D3D11)";
        const string angleIntel = "ANGLE (Intel, Intel(R) UHD Graphics (0x0000A788) Direct3D11 vs_5_0 ps_5_0, D3D11)";
        const string angleWarp = "ANGLE (Microsoft, Microsoft Basic Render Driver (0x0000008C) Direct3D11 vs_5_0 ps_5_0, D3D11)";
        Assert.True(WinNvidia.Matches(angleNvidia));
        Assert.True(WinIntel.Matches(angleIntel));
        Assert.False(WinIntel.Matches(angleNvidia));
        Assert.False(WinNvidia.Matches(angleIntel));
        Assert.True(GpuContext.IsSoftwareRenderer(angleWarp));
        Assert.True(GpuContext.IsSoftwareRenderer("llvmpipe (LLVM 20.1.2, 256 bits)"));
        Assert.False(GpuContext.IsSoftwareRenderer(angleNvidia));
    }

    [Fact]
    public void Hardware_decoding_on_windows_uses_d3d11va_on_the_chosen_gpu()
    {
        if (!OperatingSystem.IsWindows() || !FFmpegLoader.EnsureLoaded()) return;
        try
        {
            HardwareDecoding.DeviceHint = () => ("d3d11va", "0");
            Assert.Equal(FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, HardwareDecoding.DeviceOrder()[0]);
            Assert.Equal("0", HardwareDecoding.DevicePath(FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA));
            // NVIDIA を選んだら NVDEC が先。D3D11VA に戻るときも同じ GPU (番号) を使う
            HardwareDecoding.DeviceHint = () => ("cuda", "1");
            Assert.Equal(FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, HardwareDecoding.DeviceOrder()[0]);
            Assert.Equal("1", HardwareDecoding.DevicePath(FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA));
            Assert.Null(HardwareDecoding.DevicePath(FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA));
        }
        finally { HardwareDecoding.DeviceHint = null; HardwareDecoding.ResetDevices(); }
    }

    /// <summary>CI (Windows) では、GPU を作れないことを失敗にする (VB_REQUIRE_GPU=1)。</summary>
    internal static bool GpuRequired => Environment.GetEnvironmentVariable("VB_REQUIRE_GPU") == "1";

    [Fact]
    public void Windows_lists_gpus_with_dxgi_and_opens_them_with_angle()
    {
        if (!OperatingSystem.IsWindows()) return;
        var adapters = GpuContext.Adapters();
        Assert.True(adapters.Count > 0, GpuContext.AdaptersError);
        // ソフトウェアの GPU (WARP) は、GPU のない CI でも必ずある
        var warp = adapters.FirstOrDefault(a => a.IsSoftware);
        Assert.NotNull(warp);
        Assert.Equal(1, adapters.Count(a => a.Id == warp!.Id));
        var old = GpuContext.PreferredAdapter;
        try
        {
            GpuContext.PreferredAdapter = warp!.Id;
            using var ctx = GpuContext.TryCreate(out var error);
            Assert.True(ctx != null, error);
            Assert.Equal(warp, ctx!.Adapter);
            Assert.StartsWith("ANGLE", ctx.Renderer);
            Assert.True(ctx.IsSoftware, ctx.Renderer);
        }
        finally { GpuContext.PreferredAdapter = old; }
    }

    [Fact]
    public void Enumerated_gpu_can_be_opened_by_its_id()
    {
        // GPU を列挙できる環境 (Linux は Mesa があれば、Windows は WARP で、少なくともソフトウェアの GPU が出る) でだけ確かめる
        var adapters = GpuContext.Adapters();
        if (GpuRequired) Assert.True(adapters.Count > 0, GpuContext.AdaptersError);
        if (adapters.Count == 0) return;
        var target = adapters[^1];
        var old = GpuContext.PreferredAdapter;
        try
        {
            GpuContext.PreferredAdapter = target.Id;
            using var ctx = GpuContext.TryCreate(out var error);
            Assert.True(ctx != null, error);
            Assert.Equal(target, ctx!.Adapter);
            Assert.True(target.Matches(ctx.Renderer), $"{target.Name} / {ctx.Renderer}");
        }
        finally { GpuContext.PreferredAdapter = old; }
    }

    [Fact]
    public void Render_node_is_found_under_the_same_device_in_sysfs()
    {
        // ARM の SoC (Raspberry Pi 5 など): card0 が GPU (v3d、renderD128)、card1 が画面を出すだけの装置 (vc4、レンダーノードなし)
        var sys = Path.Combine(Path.GetTempPath(), $"vb-sysfs-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(sys, "card0", "device", "drm", "card0"));
            Directory.CreateDirectory(Path.Combine(sys, "card0", "device", "drm", "renderD128"));
            Directory.CreateDirectory(Path.Combine(sys, "card1", "device", "drm", "card1"));
            Assert.Equal("/dev/dri/renderD128", GpuContext.RenderNodeFor("/dev/dri/card0", sys));
            Assert.Equal("", GpuContext.RenderNodeFor("/dev/dri/card1", sys));               // 番号から renderD129 と推測しない
            Assert.Equal("/dev/dri/renderD130", GpuContext.RenderNodeFor("/dev/dri/card2", sys));   // sysfs にない: これまでどおり番号で推測
            Assert.Equal("", GpuContext.RenderNodeFor("", sys));
        }
        finally { Directory.Delete(sys, true); }
    }

    [Fact]
    public void A_display_only_device_drawing_with_the_same_gpu_is_not_listed_twice()
    {
        var v3d = new GpuAdapter(0, "V3D 7.1.7.0", "Broadcom", "/dev/dri/card0", "/dev/dri/renderD128", false);
        var vc4 = new GpuAdapter(1, "V3D 7.1.7.0", "Broadcom", "/dev/dri/card1", "", false);
        Assert.Equal(new[] { v3d }, GpuContext.WithoutDisplayOnlyDuplicates(new List<GpuAdapter> { v3d, vc4 }));
        // 名前の違う GPU (NVIDIA のプロプライエタリのドライバはレンダーノードを返さないことがある) は残す
        Assert.Equal(new[] { Intel, Nvidia, Llvmpipe }, GpuContext.WithoutDisplayOnlyDuplicates(new List<GpuAdapter> { Intel, Nvidia, Llvmpipe }));
        // レンダーノードが分からないものしかなければ、そのまま
        Assert.Equal(new[] { vc4 }, GpuContext.WithoutDisplayOnlyDuplicates(new List<GpuAdapter> { vc4 }));
    }

    [Fact]
    public void Arm_gpus_are_chosen_automatically_over_software_rendering()
    {
        foreach (var name in new[] { "V3D 7.1.7.0", "V3D 4.2.14.0", "Mali-G610 (Panfrost)", "FD618", "Mali-G52 r1 (Panfrost)" })
        {
            var gpu = new GpuAdapter(0, name, "Mesa", "/dev/dri/card0", "/dev/dri/renderD128", false);
            Assert.True(gpu.Score > 0, name);
            Assert.Equal(gpu, GpuContext.ChooseAdapter(new[] { Llvmpipe, gpu }, null));
        }
    }
}
