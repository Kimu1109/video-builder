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

    [Fact]
    public void Enumerated_gpu_can_be_opened_by_its_id()
    {
        // EGL のデバイス列挙ができる環境 (Mesa があれば、少なくともソフトウェアの GPU が出る) でだけ確かめる
        var adapters = GpuContext.Adapters();
        if (adapters.Count == 0) return;
        var target = adapters[^1];
        var old = GpuContext.PreferredAdapter;
        try
        {
            GpuContext.PreferredAdapter = target.Id;
            using var ctx = GpuContext.TryCreate(out var error);
            Assert.True(ctx != null, error);
            Assert.Equal(target, ctx!.Adapter);
            Assert.Equal(target.Name, ctx.Renderer);
        }
        finally { GpuContext.PreferredAdapter = old; }
    }
}
