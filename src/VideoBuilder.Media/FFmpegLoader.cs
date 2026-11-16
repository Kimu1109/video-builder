using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>FFmpeg の共有ライブラリを探して読み込む。</summary>
public static unsafe class FFmpegLoader
{
    private static readonly object Gate = new();
    private static bool _tried;

    public static bool IsAvailable { get; private set; }
    public static string? Error { get; private set; }
    public static string Version { get; private set; } = "";

    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_tried) return IsAvailable;
            _tried = true;
            var candidates = new List<string>();
            var env = Environment.GetEnvironmentVariable("VIDEO_BUILDER_FFMPEG");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
            candidates.Add(AppContext.BaseDirectory);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                candidates.AddRange(new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/local/lib" });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                candidates.AddRange(new[] { "/opt/homebrew/lib", "/usr/local/lib" });

            foreach (var dir in candidates)
            {
                if (!Directory.Exists(dir) || !HasLibrary(dir)) continue;
                try
                {
                    ffmpeg.RootPath = dir;
                    DynamicallyLoadedBindings.Initialize();
                    Version = ffmpeg.av_version_info() ?? "";
                    ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                    IsAvailable = true;
                    return true;
                }
                catch (Exception ex) { Error = ex.Message; }
            }
            Error ??= "FFmpeg 6.1 の共有ライブラリ (libavcodec.so.60 など) が見つかりません";
            return false;
        }
    }

    private static bool HasLibrary(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*avcodec*60*").Any(); }
        catch { return false; }
    }

    internal static string ErrorText(int code)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? code.ToString();
    }

    internal static void Check(int code, string what)
    {
        if (code < 0) throw new InvalidOperationException($"{what}: {ErrorText(code)}");
    }
}
