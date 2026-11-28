using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen;
using VideoBuilder.Media;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>Windows on ARM (Snapdragon の Adreno などの ARM64 の PC) での GPU の使い方。</summary>
public class WindowsArmTests
{
    private static readonly GpuAdapter Adreno = new(0, "Qualcomm(R) Adreno(TM) X1-85 GPU", "Qualcomm", "", "", false, 0x1234, "5143:36334330:00000000");
    private static readonly GpuAdapter Warp = new(1, "Microsoft Basic Render Driver", "Microsoft", "", "", true, 0x9ABC, "1414:008C:00000000");

    [Fact]
    public void Adreno_is_recognized_and_chosen_over_the_software_renderer()
    {
        Assert.True(Adreno.IsQualcomm);
        Assert.False(Adreno.IsNvidia);
        Assert.Equal(Adreno, GpuContext.ChooseAdapter(new[] { Warp, Adreno }, null));
        Assert.Equal("Qualcomm(R) Adreno(TM) X1-85 GPU|5143:36334330:00000000", Adreno.Id);
        // Linux (Mesa の freedreno) の名前も Qualcomm の GPU として扱う
        Assert.True(new GpuAdapter(0, "FD750", "freedreno", "/dev/dri/card0", "/dev/dri/renderD128", false).IsQualcomm);
        Assert.False(new GpuAdapter(0, "FDX", "", "", "", false).IsQualcomm);
        // ANGLE の GL_RENDERER から同じ GPU だと分かる
        Assert.True(GpuContext.SameGpu(Adreno.Name, "ANGLE (Qualcomm, Qualcomm(R) Adreno(TM) X1-85 GPU (0x36334330) Direct3D11 vs_5_0 ps_5_0, D3D11)"));
    }

    [Fact]
    public void Hardware_decoding_on_windows_arm64_skips_cuda()
    {
        var arm = HardwareDecoding.WindowsOrder(arm64: true, hintKind: "d3d11va");
        Assert.Equal(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, arm[0]);
        Assert.DoesNotContain(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, arm);
        Assert.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA, arm);
        // 一覧を作れなくても (ヒントなし)、CUDA は試さない
        Assert.DoesNotContain(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, HardwareDecoding.WindowsOrder(true, null));

        // x64: D3D11VA のフレームはメモリへ写さずに描画へ渡せるので、NVIDIA でも D3D11VA を先に (CUDA はその次。設計書 22.65)
        Assert.Equal(new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 },
            HardwareDecoding.WindowsOrder(false, "cuda"));
        // GPU 上のまま使わない設定 (VIDEO_BUILDER_GPU_FRAMES=0) なら以前どおり、NVIDIA (CUDA) が先
        Assert.Equal(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, HardwareDecoding.WindowsOrder(false, null, gpuFrames: false)[0]);
        Assert.Equal(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, HardwareDecoding.WindowsOrder(false, "d3d11va", gpuFrames: false)[0]);
        Assert.Equal("D3D12VA", HardwareDecoding.NameOf(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA));
    }

    [Fact]
    public void Media_foundation_encoders_are_hardware_only_and_rate_controlled_by_bitrate()
    {
        Assert.Equal("mf", HardwareEncoders.FamilyOf("h264_mf"));
        Assert.True(HardwareEncoders.IsHardware("hevc_mf"));
        Assert.Contains(HardwareEncoders.Candidates, c => c.Encoder == "h264_mf" && c.Codec == "H.264");
        var o = VideoEncoderSetup.OptionsFor("h264_mf", 23, "medium");
        Assert.Equal("1", o["hw_encoding"]);   // CPU で動く Media Foundation のエンコーダには落ちない
        Assert.Equal("pc_vbr", o["rate_control"]);
        Assert.Equal(0, VideoEncoderSetup.MediaFoundationQualityVsSpeed("ultrafast"));
        Assert.Equal(100, VideoEncoderSetup.MediaFoundationQualityVsSpeed("veryslow"));
        Assert.Equal(60, VideoEncoderSetup.MediaFoundationQualityVsSpeed("unknown"));
    }

    [Fact]
    public void Media_foundation_is_listed_only_when_no_vendor_encoder_covers_the_codec()
    {
        HardwareEncoder E(string name) => HardwareEncoders.Candidates.First(c => c.Encoder == name);
        // Snapdragon: Media Foundation しかない → そのまま出す
        var snapdragon = new List<HardwareEncoder> { E("h264_mf"), E("hevc_mf") };
        Assert.Equal(snapdragon, HardwareEncoders.WithoutCoveredMediaFoundation(snapdragon));
        // NVIDIA: H.264・H.265 は NVENC があるので Media Foundation は出さない。AV1 は NVENC がなければ出す
        var nvidia = new List<HardwareEncoder> { E("h264_nvenc"), E("hevc_nvenc"), E("h264_mf"), E("hevc_mf"), E("av1_mf") };
        Assert.Equal(new[] { "h264_nvenc", "hevc_nvenc", "av1_mf" }, HardwareEncoders.WithoutCoveredMediaFoundation(nvidia).Select(e => e.Encoder));
    }

    [Fact]
    public void Running_the_x64_build_on_arm64_windows_counts_as_emulated()
    {
        Assert.True(PlatformInfo.IsEmulated(true, Architecture.Arm64, Architecture.X64));
        Assert.False(PlatformInfo.IsEmulated(true, Architecture.Arm64, Architecture.Arm64));
        Assert.False(PlatformInfo.IsEmulated(true, Architecture.X64, Architecture.X64));
        Assert.False(PlatformInfo.IsEmulated(false, Architecture.Arm64, Architecture.X64));   // Windows のときだけ
    }

    [Fact]
    public void Windows_builds_of_ffmpeg_enable_d3d11va_and_media_foundation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "VideoBuilder.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var script = File.ReadAllText(Path.Combine(root!.FullName, "packaging", "ffmpeg", "build.sh"));
        var flags = Regex.Match(script, @"WINDOWS_FLAGS=\((.*?)\)").Groups[1].Value;
        Assert.Contains("--enable-d3d11va", flags);
        Assert.Contains("--enable-mediafoundation", flags);
        Assert.Contains("--enable-d3d12va", flags);
        // x64 は NVIDIA・AMD・Intel (Quick Sync) の GPU も明示する (黙って無効にならないように)。ARM64 には足さない
        var x64 = Regex.Match(script, @"WINDOWS_X64_FLAGS=\((.*?)\)").Groups[1].Value;
        foreach (var flag in new[] { "--enable-nvdec", "--enable-nvenc", "--enable-amf", "--enable-libvpl" }) Assert.Contains(flag, x64);
        Assert.Contains("[ \"${MSYSTEM_CARCH:-$(uname -m)}\" = x86_64 ] && extra+=(\"${WINDOWS_X64_FLAGS[@]}\")", script);
        // 配布物も、CI も ARM64 の Windows 向けを作る
        Assert.Contains("win-arm64", File.ReadAllText(Path.Combine(root.FullName, "packaging", "publish-all.sh")));
        var package = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "package.yml"));
        Assert.Contains("rid: win-arm64", package);
        Assert.Contains("msystem: CLANGARM64", package);
        Assert.Contains("libvpl:p", package);   // Quick Sync (x64 の MSYS2 の UCRT64)
    }
}
