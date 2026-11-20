using Avalonia.Platform;
using VideoBuilder.App.Services;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Platform;

/// <summary>
/// Windows: 画面の描画 (Avalonia のコンポジタ) に使う GPU を、描画に選んだ GPU (環境設定の「使う GPU」) にそろえる。
/// <para>
/// プレビューを読み戻さずに出す (共有の Direct3D 11 のテクスチャ) には、描画と画面の描画が同じ GPU である必要がある。
/// 何もしないと、画面の描画は DXGI の最初の GPU (内蔵と外付けがあるノート PC では多くが内蔵) になる。
/// 画面の描画の GPU は起動のときにしか決められないので、「使う GPU」を変えたら、起動し直すまでは読み戻して表示する。
/// </para>
/// 環境変数 VIDEO_BUILDER_SCREEN_GPU=default で、これまでどおり (DXGI の最初の GPU) にできる。
/// </summary>
internal static class ScreenGpu
{
    /// <summary>Avalonia の Win32PlatformOptions.GraphicsAdapterSelectionCallback。一覧の番号を返す (0 = これまでどおり)。</summary>
    public static int Choose(IReadOnlyList<PlatformGraphicsDeviceAdapterDescription> adapters)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("VIDEO_BUILDER_SCREEN_GPU") == "default") return 0;
            // この時点ではまだアプリの設定を読んでいない (画面を作る前に呼ばれる) ので、ここで読む
            var settings = AppSettings.Load();
            var args = Environment.GetCommandLineArgs();
            bool cpu = args.Contains("--cpu") || settings.RenderBackend == RenderBackend.Cpu && !args.Contains("--gpu");
            if (cpu) return 0;
            var env = Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU");
            GpuContext.PreferredAdapter = !string.IsNullOrEmpty(env) ? env : settings.GpuAdapter.Length > 0 ? settings.GpuAdapter : null;
            return IndexOf(adapters, GpuContext.SelectedAdapter());
        }
        catch { return 0; }
    }

    /// <summary>Avalonia の一覧から、gpu と同じ GPU の番号を探す (LUID で、なければ名前で)。見つからなければ 0。</summary>
    internal static int IndexOf(IReadOnlyList<PlatformGraphicsDeviceAdapterDescription> adapters, GpuAdapter? gpu)
    {
        if (gpu == null || gpu.IsSoftware) return 0;
        for (int i = 0; i < adapters.Count; i++)
            if (adapters[i].DeviceLuid is { Length: >= 8 } luid && BitConverter.ToInt64(luid, 0) == gpu.Luid) return i;
        for (int i = 0; i < adapters.Count; i++)
            if (adapters[i].Description is { } d && d.Equals(gpu.Name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}
