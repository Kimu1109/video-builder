using System.Runtime.InteropServices;

namespace VideoBuilder.Media;

/// <summary>
/// 動いている OS と CPU の種類。Windows on ARM (Snapdragon などの ARM64 の PC) で、使うハードウェアの仕組みを変えるのに使う。
/// </summary>
public static class PlatformInfo
{
    /// <summary>このプロセスが ARM64 のコードで動いているか。</summary>
    public static bool IsArm64Process => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>Windows on ARM (ARM64 の Windows) の上で、ARM64 版として動いているか。</summary>
    public static bool IsWindowsArm64 => OperatingSystem.IsWindows() && IsArm64Process;

    /// <summary>
    /// ARM64 の Windows の上で、x64 版をエミュレーション (Prism) で動かしているか。
    /// (.NET の OSArchitecture は、エミュレーションの中でも本当の CPU を返す。)
    /// この場合、GPU のドライバへの呼び出しや画素の処理もエミュレーションを通るので、ARM64 版の方がずっと速い。
    /// </summary>
    public static bool IsEmulatedOnArm64 => IsEmulated(OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture);

    internal static bool IsEmulated(bool windows, Architecture os, Architecture process) =>
        windows && os == Architecture.Arm64 && process is Architecture.X64 or Architecture.X86;
}
