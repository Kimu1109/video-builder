using FFmpeg.AutoGen;
using VideoBuilder.Media;

namespace VideoBuilder.Tests;

/// <summary>
/// 読み込んだ FFmpeg が、配布物と同じ作り (packaging/ffmpeg/build.sh: GPL・x264・x265 入り) になっているか (設計書 22.33)。
/// FFmpeg が必ずある環境 (VB_REQUIRE_FFMPEG=1。CI と package.yml) でだけ確かめる。
/// </summary>
public unsafe class FFmpegBuildTests
{
    [Fact]
    public void Loaded_ffmpeg_is_the_gpl_build_with_x264_and_x265()
    {
        if (Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG") != "1") return;
        Assert.True(FFmpegLoader.EnsureLoaded(), FFmpegLoader.Error);
        Assert.StartsWith(FFmpegLoader.RequiredMajor + ".", FFmpegLoader.Version);
        Assert.Contains("GPL version 3", FFmpegLoader.License);
        foreach (var name in new[] { "libx264", "libx265", "libvpx-vp9", "libopus", "aac" })
            Assert.True(ffmpeg.avcodec_find_encoder_by_name(name) != null, $"エンコーダ {name} がありません");
        Assert.True(ffmpeg.avcodec_find_decoder_by_name("libdav1d") != null, "デコーダ libdav1d がありません");
    }
}
