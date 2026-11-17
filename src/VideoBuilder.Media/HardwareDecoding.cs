using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

public enum HardwareDecodeMode
{
    /// <summary>常にソフトウェアでデコードする。</summary>
    Off,
    /// <summary>使える GPU があれば使い、なければ (途中で失敗した場合も) ソフトウェアに戻る。</summary>
    Auto,
}

/// <summary>
/// GPU を使った動画のデコード (NVDEC・VAAPI・Quick Sync・VideoToolbox・D3D11VA など)。
/// VideoReader から使う。デバイスは種類ごとに 1 つだけ作り、すべての動画で共有する。
/// </summary>
public static unsafe class HardwareDecoding
{
    /// <summary>新しく開く動画に使う設定。すでに開いている動画には影響しない。</summary>
    public static HardwareDecodeMode Mode { get; set; } = HardwareDecodeMode.Auto;

    /// <summary>
    /// 試すデバイスを 1 つに絞る ("vaapi" "cuda" など)。null なら OS ごとの既定の順に試す。
    /// 環境変数 VIDEO_BUILDER_HWDEC でも指定できる ("off" でハードウェアデコードを使わない)。
    /// </summary>
    public static string? PreferredDevice { get; set; } = Environment.GetEnvironmentVariable("VIDEO_BUILDER_HWDEC") is { Length: > 0 } v && v != "off" ? v : null;

    internal static bool DisabledByEnvironment => Environment.GetEnvironmentVariable("VIDEO_BUILDER_HWDEC") == "off";

    /// <summary>OS ごとに試す順番。</summary>
    internal static AVHWDeviceType[] DeviceOrder()
    {
        if (PreferredDevice != null)
        {
            var t = ffmpeg.av_hwdevice_find_type_by_name(PreferredDevice);
            return t == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE ? Array.Empty<AVHWDeviceType>() : new[] { t };
        }
        if (OperatingSystem.IsWindows())
            return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 };
        if (OperatingSystem.IsMacOS())
            return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX };
        // Linux: NVIDIA (CUDA) → Intel / AMD (VAAPI)
        return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI };
    }

    public static string NameOf(AVHWDeviceType type) => type switch
    {
        AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => "NVDEC (CUDA)",
        AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI => "VAAPI",
        AVHWDeviceType.AV_HWDEVICE_TYPE_QSV => "Quick Sync",
        AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX => "VideoToolbox",
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => "D3D11VA",
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => "DXVA2",
        AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU => "VDPAU",
        _ => ffmpeg.av_hwdevice_get_type_name(type) ?? type.ToString(),
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<AVHWDeviceType, IntPtr> Devices = new();
    private static readonly Dictionary<AVHWDeviceType, string> Failures = new();

    /// <summary>
    /// 種類ごとに共有するデバイスを返す (呼び出し側は av_buffer_ref して使う)。作れなければ null。
    /// 作れなかった種類は覚えておき、2 回目からは試さない (開くたびに時間がかからないように)。
    /// </summary>
    internal static AVBufferRef* SharedDevice(AVHWDeviceType type, out string? error)
    {
        lock (Gate)
        {
            error = null;
            if (Devices.TryGetValue(type, out var known)) return (AVBufferRef*)known;
            if (Failures.TryGetValue(type, out error)) return null;
            // 使えない環境では FFmpeg がエラーを画面に出すので、確認の間だけ黙らせる
            int level = ffmpeg.av_log_get_level();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            try
            {
                AVBufferRef* device = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&device, type, null, null, 0);
                if (ret < 0 || device == null)
                {
                    error = $"{NameOf(type)} のデバイスを開けません: {FFmpegLoader.ErrorText(ret)}";
                    Failures[type] = error;
                    return null;
                }
                Devices[type] = (IntPtr)device;
                return device;
            }
            finally { ffmpeg.av_log_set_level(level); }
        }
    }

    /// <summary>codec がこのデバイスでデコードできるなら、そのときの (GPU 上の) ピクセル形式を返す。</summary>
    internal static AVPixelFormat? HardwareFormatFor(AVCodec* codec, AVHWDeviceType type)
    {
        for (int i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config == null) return null;
            if ((config->methods & 0x01) != 0 /* AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX */ && config->device_type == type)
                return config->pix_fmt;
        }
    }

    /// <summary>
    /// デコーダが「どの形式で出力するか」を尋ねてきたときの答え。
    /// 望みの GPU 形式があればそれを、なければソフトウェアの形式 (GPU 形式でない最初のもの) を選ぶ。
    /// </summary>
    internal static AVPixelFormat ChooseFormat(IReadOnlyList<AVPixelFormat> offered, AVPixelFormat wanted)
    {
        foreach (var f in offered) if (f == wanted) return f;
        foreach (var f in offered)
        {
            var desc = ffmpeg.av_pix_fmt_desc_get(f);
            if (desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0) return f;
        }
        return offered.Count > 0 ? offered[0] : AVPixelFormat.AV_PIX_FMT_NONE;
    }

    // デコーダから呼ばれる関数。ガベージコレクションで消えないよう、静的に持っておく
    private static readonly AVCodecContext_get_format GetFormatCallback = GetFormat;
    internal static readonly AVCodecContext_get_format_func GetFormatPointer = new() { Pointer = Marshal.GetFunctionPointerForDelegate(GetFormatCallback) };

    private static AVPixelFormat GetFormat(AVCodecContext* ctx, AVPixelFormat* formats)
    {
        var offered = new List<AVPixelFormat>();
        for (var p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++) offered.Add(*p);
        // 望みの GPU 形式は、デコーダを開くときに opaque に入れておく
        var wanted = (AVPixelFormat)(int)(nint)ctx->opaque;
        return ChooseFormat(offered, wanted);
    }

    /// <summary>試したデバイスと結果 (環境設定の表示用)。</summary>
    public static string Describe()
    {
        if (Mode == HardwareDecodeMode.Off || DisabledByEnvironment) return "使わない";
        if (!FFmpegLoader.EnsureLoaded()) return FFmpegLoader.Error ?? "FFmpeg がありません";
        var lines = new List<string>();
        foreach (var type in DeviceOrder())
        {
            bool ok = SharedDevice(type, out var error) != null;
            lines.Add(ok ? $"{NameOf(type)}: 使用可能" : error ?? $"{NameOf(type)}: 使用不可");
        }
        return lines.Count == 0 ? "使えるデバイスがありません" : string.Join("\n", lines);
    }

    /// <summary>テスト用: ソフトウェアでデコードしながら、ハードウェアで開けたように振る舞わせる (戻す処理の確認に使う)。</summary>
    internal static bool TestPretendHardware { get; set; }
    /// <summary>テスト用: ハードウェアの扱いでこの数だけフレームを変換したら、失敗したことにする。</summary>
    internal static int TestFailAfterFrames { get; set; } = -1;
}
