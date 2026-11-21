using System.Text.RegularExpressions;

namespace VideoBuilder.Tests;

/// <summary>配布物の作り方の食い違いを見つける (設計書 22.33)。</summary>
public class PackagingTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VideoBuilder.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("リポジトリの場所が分かりません");
    }

    private static string Script => File.ReadAllText(Path.Combine(Root(), "packaging", "ffmpeg", "build.sh"));
    private static string Manifest => File.ReadAllText(Path.Combine(Root(), "packaging", "flatpak", "io.github.kimu1109.VideoBuilder.yml"));

    [Fact]
    public void Flatpak_builds_ffmpeg_with_the_same_configure_flags_as_the_build_script()
    {
        var block = Regex.Match(Script, @"CONFIGURE_FLAGS=\((.*?)\)", RegexOptions.Singleline).Groups[1].Value;
        var scriptFlags = Regex.Matches(block, @"--[a-z0-9-]+").Select(m => m.Value).ToList();
        var section = Regex.Match(Manifest, @"build\.sh の CONFIGURE_FLAGS と同じにする ----(.*?)# ---- ここまで", RegexOptions.Singleline).Groups[1].Value;
        var manifestFlags = Regex.Matches(section, @"- (--[a-z0-9-]+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(scriptFlags);
        Assert.Equal(scriptFlags, manifestFlags);
        Assert.Contains("--enable-libx264", scriptFlags);
        Assert.Contains("--enable-libx265", scriptFlags);
    }

    [Fact]
    public void Flatpak_takes_the_app_from_where_its_build_script_puts_it()
    {
        // マニフェストは決まった場所から入れ、build.sh がそこに x64 か arm64 向けに発行したものを写す (設計書 22.36)
        var buildScript = File.ReadAllText(Path.Combine(Root(), "packaging", "flatpak", "build.sh"));
        Assert.Matches(@"path: \.\./\.\./artifacts/flatpak/publish\s", Manifest);
        Assert.Contains("cp -a \"$root/artifacts/publish/linux-$arch\" \"$out/publish\"", buildScript);
        Assert.Contains("out=\"$root/artifacts/flatpak\"", buildScript);
        Assert.DoesNotContain("artifacts/publish/linux-", Manifest);   // アーキテクチャを決め打ちしない
    }

    [Fact]
    public void Flatpak_and_the_build_script_use_the_same_ffmpeg_source()
    {
        var version = Regex.Match(Script, "FFMPEG_VERSION=\"([^\"]+)\"").Groups[1].Value;
        var sha = Regex.Match(Script, "FFMPEG_SHA256=\"([^\"]+)\"").Groups[1].Value;
        Assert.Contains($"https://ffmpeg.org/releases/ffmpeg-{version}.tar.xz\n        sha256: {sha}", Manifest.Replace("\r\n", "\n"));
        // バインディングのメジャーバージョンとも合わせる
        Assert.StartsWith(VideoBuilder.Media.FFmpegLoader.RequiredMajor + ".", version);
    }
}
