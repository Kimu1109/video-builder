using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>GPU 上のフレームの種類 (VideoReader.AcceptHardwareFrames で、受け取れるものを指定する)。</summary>
[Flags]
public enum HardwareFrameKinds
{
    None = 0,
    /// <summary>VAAPI のフレーム。DRM PRIME (DMA-BUF) として取り出し、EGL で GL のテクスチャに取り込む。</summary>
    Vaapi = 1,
    /// <summary>NVDEC (CUDA) のフレーム。CUDA と GL の相互運用で、GPU の中で GL のテクスチャへ写す。</summary>
    Cuda = 2,
}

/// <summary>DMA-BUF の 1 つの層 (1 つの面)。</summary>
/// <param name="DrmFormat">DRM の形式 (fourcc。'R8  ' や 'GR88')。</param>
/// <param name="Modifier">並び方 (タイル化など)。DRM_FORMAT_MOD_INVALID なら指定なし。</param>
public readonly record struct DmaBufLayer(uint DrmFormat, int Fd, long Offset, long Pitch, ulong Modifier, int Width, int Height);

/// <summary>
/// ハードウェアデコードした NV12 のフレーム (GPU 上のまま。メモリへ写していない)。設計書 22.58。
/// Dispose するまで、デコーダはこのフレームの置き場を使い回さない。
/// どのスレッドで Dispose してもよい。
/// </summary>
public sealed unsafe class HardwareFrame : IDisposable
{
    public const ulong DrmFormatModInvalid = 0x00ffffffffffffffUL;
    /// <summary>DRM_FORMAT_R8 / DRM_FORMAT_GR88 / DRM_FORMAT_NV12。</summary>
    public const uint DrmR8 = 0x20203852, DrmGR88 = 0x38385247, DrmNV12 = 0x3231564E;

    private AVFrame* _frame;
    private AVFrame* _mapped;

    public HardwareFrameKinds Kind { get; }
    /// <summary>元のフレームの大きさ (縮めない)。</summary>
    public int Width { get; }
    public int Height { get; }

    internal HardwareFrame(AVFrame* frame, HardwareFrameKinds kind)
    {
        _frame = frame; Kind = kind; Width = frame->width; Height = frame->height;
    }

    /// <summary>
    /// フレームが GPU 上の 8 ビットの NV12 なら、その種類を返す (VAAPI・CUDA のどちらでもなければ None)。
    /// </summary>
    internal static HardwareFrameKinds KindOf(AVFrame* frame)
    {
        if (frame->hw_frames_ctx == null) return HardwareFrameKinds.None;
        var fc = (AVHWFramesContext*)frame->hw_frames_ctx->data;
        if (fc->sw_format != AVPixelFormat.AV_PIX_FMT_NV12) return HardwareFrameKinds.None;
        return (AVPixelFormat)frame->format switch
        {
            AVPixelFormat.AV_PIX_FMT_VAAPI => HardwareFrameKinds.Vaapi,
            AVPixelFormat.AV_PIX_FMT_CUDA => HardwareFrameKinds.Cuda,
            _ => HardwareFrameKinds.None,
        };
    }

    // ---------------- VAAPI: DMA-BUF として取り出す ----------------

    /// <summary>
    /// VAAPI のフレームを DRM PRIME (DMA-BUF) として取り出す (av_hwframe_map。GPU のデコードが終わるのも待つ)。
    /// 取り出した fd は Dispose まで開いている。取り出せなければ false (error に理由)。
    /// write なら、書き込む (中身は捨ててよい) つもりで取り出す (エンコーダに渡すフレームに GPU で描くとき)。
    /// </summary>
    public bool TryMapDmaBuf(out DmaBufLayer[] layers, out string? error, bool write = false)
    {
        layers = Array.Empty<DmaBufLayer>();
        error = null;
        if (Kind != HardwareFrameKinds.Vaapi || _frame == null) { error = "VAAPI のフレームではありません"; return false; }
        if (_mapped == null)
        {
            var m = ffmpeg.av_frame_alloc();
            m->format = (int)AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
            int ret = ffmpeg.av_hwframe_map(m, _frame, write ? 2 | 4 /* AV_HWFRAME_MAP_WRITE | OVERWRITE */ : 1 /* AV_HWFRAME_MAP_READ */);
            if (ret < 0)
            {
                ffmpeg.av_frame_free(&m);
                error = "DMA-BUF として取り出せません: " + FFmpegLoader.ErrorText(ret);
                return false;
            }
            _mapped = m;
        }
        var d = (DrmFrameDescriptor*)_mapped->data[0];
        if (d == null || d->NbLayers <= 0 || d->NbLayers > 4 || d->NbObjects <= 0 || d->NbObjects > 4) { error = "DMA-BUF の中身が読めません"; return false; }
        int cw = (Width + 1) / 2, ch = (Height + 1) / 2;
        var list = new List<DmaBufLayer>();
        for (int l = 0; l < d->NbLayers; l++)
        {
            ref var layer = ref d->Layers[l];
            for (int p = 0; p < layer.NbPlanes && p < 4; p++)
            {
                ref var plane = ref layer.Planes[p];
                if (plane.ObjectIndex < 0 || plane.ObjectIndex >= d->NbObjects) { error = "DMA-BUF の中身が読めません"; return false; }
                ref var obj = ref d->Objects[plane.ObjectIndex];
                // NV12 を 1 つの層で返されたら、面ごとに R8・GR88 の層として扱う
                uint format = layer.Format == DrmNV12 ? (p == 0 ? DrmR8 : DrmGR88) : layer.Format;
                bool chroma = format == DrmGR88;
                list.Add(new DmaBufLayer(format, obj.Fd, plane.Offset, plane.Pitch, obj.FormatModifier, chroma ? cw : Width, chroma ? ch : Height));
            }
        }
        layers = list.ToArray();
        return true;
    }

    // ---------------- CUDA ----------------

    /// <summary>CUDA のコンテキスト (CUcontext) とストリーム (CUstream)。フレームは、このストリームの上で使う。</summary>
    public (IntPtr Context, IntPtr Stream) CudaContext
    {
        get
        {
            if (Kind != HardwareFrameKinds.Cuda || _frame == null) return default;
            var fc = (AVHWFramesContext*)_frame->hw_frames_ctx->data;
            var cu = (IntPtr*)fc->device_ctx->hwctx;   // AVCUDADeviceContext { CUcontext cuda_ctx; CUstream stream; ... }
            return (cu[0], cu[1]);
        }
    }

    /// <summary>CUDA のフレームの面 (デバイスのアドレスと 1 行のバイト数)。0 が Y、1 が UV。</summary>
    public (ulong Pointer, int Pitch) CudaPlane(int i) =>
        Kind == HardwareFrameKinds.Cuda && _frame != null ? ((ulong)_frame->data[(uint)i], _frame->linesize[(uint)i]) : default;

    /// <summary>DMA-BUF として取り出したものを閉じる (書き込んだ中身を VAAPI 側に戻す)。</summary>
    public void Unmap()
    {
        if (_mapped != null) { var m = _mapped; ffmpeg.av_frame_free(&m); _mapped = null; }
    }

    /// <summary>元のフレームを引き取る (このオブジェクトは空になる)。</summary>
    internal AVFrame* Detach()
    {
        Unmap();
        var f = _frame;
        _frame = null;
        return f;
    }

    public void Dispose()
    {
        if (_mapped != null) { var m = _mapped; ffmpeg.av_frame_free(&m); _mapped = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
    }

    // libavutil/hwcontext_drm.h の構造体 (FFmpeg.AutoGen にはない)
    [StructLayout(LayoutKind.Sequential)]
    private struct DrmObject { public int Fd; public nuint Size; public ulong FormatModifier; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrmPlane { public int ObjectIndex; public nint Offset; public nint Pitch; }

    [InlineArray(4)] private struct DrmPlanes { private DrmPlane _p; }
    [InlineArray(4)] private struct DrmObjects { private DrmObject _o; }
    [InlineArray(4)] private struct DrmLayers { private DrmLayer _l; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrmLayer { public uint Format; public int NbPlanes; public DrmPlanes Planes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrmFrameDescriptor { public int NbObjects; public DrmObjects Objects; public int NbLayers; public DrmLayers Layers; }
}
