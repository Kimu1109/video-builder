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
    /// <summary>
    /// 10 ビットの P010 のフレームも受け取る (VAAPI・CUDA と組み合わせて指定する)。GL で 16 ビットのテクスチャを使えるときだけ (設計書 22.62)。
    /// </summary>
    TenBit = 4,
}

/// <summary>GPU 上のフレームの面の並び (設計書 22.58・22.62)。</summary>
public enum HardwareSurface
{
    /// <summary>8 ビットの NV12 (Y と、U・V が交互の面)。DMA-BUF は R8 + GR88。</summary>
    Nv12,
    /// <summary>8 ビットの I420 (Y・U・V が別々の面。VAAPI だけ。新しい iHD のドライバや MJPEG のデコード)。DMA-BUF は R8 x3。</summary>
    I420,
    /// <summary>10 ビットの P010 (NV12 と同じ並びで 1 画素 16 ビット、上位 10 ビットに値)。DMA-BUF は R16 + GR1616。</summary>
    P010,
}

/// <summary>GPU のデバイスへの参照 (<see cref="HardwareFrame.KeepDevice"/>)。どのスレッドで Dispose してもよい。</summary>
public sealed unsafe class HardwareDeviceRef : IDisposable
{
    private AVBufferRef* _ref;
    internal HardwareDeviceRef(AVBufferRef* r) { _ref = r; }
    public void Dispose()
    {
        var r = _ref;
        _ref = null;
        if (r != null) ffmpeg.av_buffer_unref(&r);
    }
}

/// <summary>DMA-BUF の 1 つの層 (1 つの面)。</summary>
/// <param name="DrmFormat">DRM の形式 (fourcc。'R8  ' や 'GR88')。</param>
/// <param name="Modifier">並び方 (タイル化など)。DRM_FORMAT_MOD_INVALID なら指定なし。</param>
public readonly record struct DmaBufLayer(uint DrmFormat, int Fd, long Offset, long Pitch, ulong Modifier, int Width, int Height);

/// <summary>
/// ハードウェアデコードした NV12・I420・P010 のフレーム (GPU 上のまま。メモリへ写していない)。設計書 22.58。
/// Dispose するまで、デコーダはこのフレームの置き場を使い回さない。
/// どのスレッドで Dispose してもよい。
/// </summary>
public sealed unsafe class HardwareFrame : IDisposable
{
    public const ulong DrmFormatModInvalid = 0x00ffffffffffffffUL;
    /// <summary>DRM_FORMAT_R8 / DRM_FORMAT_GR88 / DRM_FORMAT_NV12。</summary>
    public const uint DrmR8 = 0x20203852, DrmGR88 = 0x38385247, DrmNV12 = 0x3231564E;
    /// <summary>DRM_FORMAT_R16 / DRM_FORMAT_GR1616 / DRM_FORMAT_P010 / DRM_FORMAT_YUV420 (I420)。</summary>
    public const uint DrmR16 = 0x20363152, DrmGR1616 = 0x32335247, DrmP010 = 0x30313050, DrmYUV420 = 0x32315559;

    private AVFrame* _frame;
    private AVFrame* _mapped;

    public HardwareFrameKinds Kind { get; }
    /// <summary>面の並び。</summary>
    public HardwareSurface Surface { get; }
    /// <summary>フルレンジ (0〜255。MJPEG のプロキシなど) か。リミテッドレンジ (16〜235) なら false。</summary>
    public bool FullRange { get; }
    /// <summary>元のフレームの大きさ (縮めない)。</summary>
    public int Width { get; }
    public int Height { get; }

    internal HardwareFrame(AVFrame* frame, HardwareFrameKinds kind)
    {
        _frame = frame; Kind = kind; Width = frame->width; Height = frame->height;
        Surface = SurfaceOf(frame) ?? HardwareSurface.Nv12;
        FullRange = frame->color_range == AVColorRange.AVCOL_RANGE_JPEG;
    }

    /// <summary>GPU 上のフレームの面の並び。扱えない並び (4:2:2 など) なら null。</summary>
    internal static HardwareSurface? SurfaceOf(AVFrame* frame)
    {
        if (frame->hw_frames_ctx == null) return null;
        var fc = (AVHWFramesContext*)frame->hw_frames_ctx->data;
        return fc->sw_format switch
        {
            AVPixelFormat.AV_PIX_FMT_NV12 => HardwareSurface.Nv12,
            // VAAPI の置き場の形式は、ドライバが対応するもののうち元の形式に合うものを FFmpeg が選ぶ (iHD は yuv420p に I420 を出す)
            AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P when (AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_VAAPI => HardwareSurface.I420,
            AVPixelFormat.AV_PIX_FMT_P010LE => HardwareSurface.P010,
            _ => null,
        };
    }

    /// <summary>
    /// フレームが GPU 上の扱える並び (8 ビットの NV12・I420、10 ビットの P010) なら、その種類を返す (VAAPI・CUDA のどちらでもなければ None)。
    /// </summary>
    internal static HardwareFrameKinds KindOf(AVFrame* frame)
    {
        if (SurfaceOf(frame) == null) return HardwareFrameKinds.None;
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
                // NV12・P010・I420 を 1 つの層で返されたら、面ごとの層 (R8・GR88 / R16・GR1616 / R8 x3) として扱う
                uint format = layer.Format switch
                {
                    DrmNV12 => p == 0 ? DrmR8 : DrmGR88,
                    DrmP010 => p == 0 ? DrmR16 : DrmGR1616,
                    DrmYUV420 => DrmR8,
                    _ => layer.Format,
                };
                // 2 つ目の面からは U・V (4:2:0 なので縦横半分)
                bool chroma = list.Count > 0;
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

    /// <summary>
    /// このフレームの GPU のデバイスへの参照 (Dispose で手放す)。持っている間は、デバイス (CUDA のコンテキストなど) が消えない。
    /// デコーダや共有のデバイスが先に手放されても、このデバイスで作ったもの (CUDA に登録した GL のテクスチャなど) を後から片付けられるようにする。
    /// </summary>
    public HardwareDeviceRef? KeepDevice()
    {
        if (_frame == null || _frame->hw_frames_ctx == null) return null;
        var fc = (AVHWFramesContext*)_frame->hw_frames_ctx->data;
        var r = ffmpeg.av_buffer_ref(fc->device_ref);
        return r == null ? null : new HardwareDeviceRef(r);
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
