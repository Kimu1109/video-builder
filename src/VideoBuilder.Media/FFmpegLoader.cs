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

    /// <summary>読み込んだ FFmpeg のライセンス ("GPL version 3 or later" など。avcodec_license の値)。</summary>
    public static string License { get; private set; } = "";

    /// <summary>バインディング (FFmpeg.AutoGen) が対応する FFmpeg のメジャーバージョン。</summary>
    public const int RequiredMajor = 9;

    /// <summary>バインディングが読み込む libavcodec のメジャーバージョン (FFmpeg 9 なら 63)。</summary>
    private static int AvcodecMajor => ffmpeg.LibraryVersionMap["avcodec"];

    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_tried) return IsAvailable;
            _tried = true;
            var candidates = new List<string>();
            var env = Environment.GetEnvironmentVariable("VIDEO_BUILDER_FFMPEG");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);
            // 配布物は実行ファイルの隣の ffmpeg フォルダに同梱する (packaging/ffmpeg/build.sh --bundle)
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
            candidates.Add(AppContext.BaseDirectory);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                // /app/lib は Flatpak (FFmpeg 9 を一緒に作って入れる)
                candidates.AddRange(new[] { "/app/lib", "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/local/lib" });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                candidates.AddRange(new[] { "/opt/homebrew/lib", "/usr/local/lib" });

            foreach (var dir in candidates)
            {
                if (!Directory.Exists(dir) || !HasLibrary(dir)) continue;
                try
                {
                    ffmpeg.RootPath = dir;
                    // Windows では、avcodec-63.dll が使う DLL (x264・x265 など) を同じフォルダから探させる
                    // (FFmpeg.AutoGen はフルパスで読むが、そこから読まれる DLL は既定ではアプリのフォルダと PATH からしか探さない)
                    if (OperatingSystem.IsWindows()) SetDllDirectoryW(dir);
                    DynamicallyLoadedBindings.Initialize();
                    Version = ffmpeg.av_version_info() ?? "";
                    License = ffmpeg.avcodec_license() ?? "";
                    ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                    IsAvailable = true;
                    return true;
                }
                catch (Exception ex) { Error = ex.Message; }
            }
            Error ??= $"FFmpeg {RequiredMajor} の共有ライブラリ (libavcodec.so.{AvcodecMajor} など) が見つかりません";
            return false;
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string path);

    private static bool HasLibrary(string dir)
    {
        // libavcodec.so.63 (Linux) / avcodec-63.dll (Windows) / libavcodec.63.dylib (macOS)
        try { return Directory.EnumerateFiles(dir, $"*avcodec*{AvcodecMajor}*").Any(); }
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
