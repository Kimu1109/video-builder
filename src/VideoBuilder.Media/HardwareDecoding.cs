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
/// GPU を使った動画のデコード (NVDEC・VAAPI・Quick Sync・VideoToolbox・D3D11VA・D3D12VA など)。
/// VideoReader から使う。デバイスは種類ごとに 1 つだけ作り、すべての動画で共有する。
/// <para>
/// Linux では最後に V4L2 M2M (h264_v4l2m2m などの専用のデコーダ。Raspberry Pi 4・Qualcomm・Amlogic など ARM の SoC の多く) も試す (設計書 22.37)。
/// こちらは FFmpeg の hwaccel ではなく別のデコーダで、デコードしたフレームはふつうのメモリにある (GPU 上の形式は NONE)。
/// </para>
/// </summary>
public static unsafe class HardwareDecoding
{
    /// <summary>新しく開く動画に使う設定。すでに開いている動画には影響しない。</summary>
    public static HardwareDecodeMode Mode { get; set; } = HardwareDecodeMode.Auto;

    /// <summary>
    /// 試すデバイスを 1 つに絞る ("vaapi" "cuda" "v4l2m2m" など)。null なら OS ごとの既定の順に試す。
    /// 環境変数 VIDEO_BUILDER_HWDEC でも指定できる ("off" でハードウェアデコードを使わない)。
    /// </summary>
    public static string? PreferredDevice { get; set; } = Environment.GetEnvironmentVariable("VIDEO_BUILDER_HWDEC") is { Length: > 0 } v && v != "off" ? v : null;

    /// <summary>
    /// 描画に使う GPU に合わせるための問い合わせ (アプリが設定する)。(種類, デバイス) を返す。
    /// Linux: ("cuda" または "vaapi", VAAPI で開くデバイスファイル)。Windows: ("cuda" または "d3d11va", D3D11VA で開く DXGI のアダプタの番号)。
    /// null を返せば、これまでどおり (OS ごとの既定の順と、既定のデバイス)。
    /// 例: CPU 内蔵の GPU を選んだら ("vaapi", "/dev/dri/renderD128") や ("d3d11va", "0")、NVIDIA を選んだら ("cuda", "") や ("cuda", "1")。
    /// </summary>
    public static Func<(string Kind, string Device)?>? DeviceHint { get; set; }

    internal static bool DisabledByEnvironment => Environment.GetEnvironmentVariable("VIDEO_BUILDER_HWDEC") == "off";

    /// <summary>OS ごとに試す順番。</summary>
    internal static AVHWDeviceType[] DeviceOrder()
    {
        if (PreferredDevice != null)
        {
            if (PreferredDevice == V4l2M2mName) return Array.Empty<AVHWDeviceType>();
            var t = ffmpeg.av_hwdevice_find_type_by_name(PreferredDevice);
            return t == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE ? Array.Empty<AVHWDeviceType>() : new[] { t };
        }
        if (OperatingSystem.IsWindows()) return WindowsOrder(PlatformInfo.IsArm64Process, Hint()?.Kind);
        if (OperatingSystem.IsMacOS())
            return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX };
        // Linux: NVIDIA (CUDA) → Intel / AMD (VAAPI)。描画に Intel / AMD の GPU を選んでいれば、VAAPI を先に試す
        if (Hint() is { Kind: "vaapi" })
            return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA };
        return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI };
    }

    /// <summary>
    /// Windows で試す順番。
    /// x64: NVIDIA (CUDA) → D3D11VA → DXVA2。描画に Intel / AMD の GPU を選んでいれば、D3D11VA を先に試す。
    /// ARM64 (Snapdragon の Adreno など): CUDA (NVIDIA のドライバ) は ARM64 の Windows にないので試さず、
    /// D3D11VA → D3D12VA (新しいドライバで D3D11VA が使えないとき) → DXVA2 の順にする。
    /// </summary>
    internal static AVHWDeviceType[] WindowsOrder(bool arm64, string? hintKind)
    {
        if (arm64)
            return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 };
        return hintKind == "d3d11va"
            ? new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 }
            : new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 };
    }

    public static string NameOf(AVHWDeviceType type) => type switch
    {
        AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => "NVDEC (CUDA)",
        AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI => "VAAPI",
        AVHWDeviceType.AV_HWDEVICE_TYPE_QSV => "Quick Sync",
        AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX => "VideoToolbox",
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => "D3D11VA",
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA => "D3D12VA",
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => "DXVA2",
        AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU => "VDPAU",
        _ => ffmpeg.av_hwdevice_get_type_name(type) ?? type.ToString(),
    };

    /// <summary>
    /// デバイスを開くときに FFmpeg に渡す文字列 (null なら既定のデバイス)。
    /// VAAPI はデバイスファイル、D3D11VA は DXGI のアダプタの番号 (NVIDIA を選んだときも、NVDEC が使えなければ同じ GPU で D3D11VA を使う)。
    /// </summary>
    internal static string? DevicePath(AVHWDeviceType type)
    {
        var h = Hint();
        if (h is not { Device.Length: > 0 } hint) return null;
        if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI && hint.Kind == "vaapi") return hint.Device;
        // D3D12VA も、D3D11VA と同じく DXGI のアダプタの番号で GPU を選ぶ
        if (type is AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA or AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA && OperatingSystem.IsWindows() && hint.Device.All(char.IsAsciiDigit)) return hint.Device;
        return null;
    }

    private static (string Kind, string Device)? Hint()
    {
        try { return DeviceHint?.Invoke(); }
        catch { return null; }   // GPU の一覧を作れなくても、デコードは既定のやり方で続ける
    }

    /// <summary>
    /// 開いてあるデバイスを手放して、次に開くときに作り直す (使う GPU を変えたとき)。
    /// すでに開いている動画は、自分の参照を持っているので影響しない。
    /// </summary>
    public static void ResetDevices()
    {
        lock (Gate)
        {
            foreach (var d in Devices.Values)
            {
                var p = (AVBufferRef*)d;
                ffmpeg.av_buffer_unref(&p);
            }
            Devices.Clear();
            Failures.Clear();
            M2mFailures.Clear();
            M2mOpened.Clear();
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<AVHWDeviceType, IntPtr> Devices = new();
    private static readonly Dictionary<AVHWDeviceType, string> Failures = new();
    /// <summary>開けなかった V4L2 M2M のデコーダ (h264_v4l2m2m など) と理由。2 回目からは試さない。</summary>
    private static readonly Dictionary<string, string> M2mFailures = new();
    /// <summary>開けた V4L2 M2M のデコーダ (環境設定の表示用)。</summary>
    private static readonly HashSet<string> M2mOpened = new();

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
                // VAAPI・D3D11VA は、描画に選んだ GPU と同じデバイスで開く (何も指定しないと、多くは CPU 内蔵の GPU になる)
                string? path = DevicePath(type);
                int ret = ffmpeg.av_hwdevice_ctx_create(&device, type, path, null, 0);
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

    /// <summary>
    /// 使えるデバイスを順に試して、ハードウェアのデコーダを開く。開けなければ null (error に理由)。
    /// format は、デコードしたフレームの (GPU 上の) ピクセル形式。name はデバイスの名前 ("VAAPI" など)。
    /// </summary>
    internal static AVCodecContext* Open(AVCodec* codec, AVStream* st, out AVPixelFormat format, out string name, out string? error, int extraFrames = 0)
    {
        format = AVPixelFormat.AV_PIX_FMT_NONE; name = ""; error = null;
        var reasons = new List<string>();
        foreach (var type in DeviceOrder())
        {
            var wanted = HardwareFormatFor(codec, type);
            if (wanted == null) { reasons.Add($"{NameOf(type)}: この形式には対応していません"); continue; }
            var device = SharedDevice(type, out var deviceError);
            if (device == null) { reasons.Add(deviceError ?? NameOf(type)); continue; }

            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            int level = ffmpeg.av_log_get_level();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            try
            {
                if (ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar) < 0) { reasons.Add("デコーダの設定に失敗"); continue; }
                ctx->hw_device_ctx = ffmpeg.av_buffer_ref(device);
                ctx->get_format = GetFormatPointer;
                ctx->opaque = (void*)(nint)(int)wanted.Value;     // GetFormat で読む
                // GPU がデコードするので、CPU のスレッドは少なくてよい
                ctx->thread_count = 1;
                if (extraFrames > 0) ctx->extra_hw_frames = extraFrames;
                int ret = ffmpeg.avcodec_open2(ctx, codec, null);
                if (ret < 0) { reasons.Add($"{NameOf(type)}: {FFmpegLoader.ErrorText(ret)}"); continue; }
                var opened = ctx;
                ctx = null;
                format = wanted.Value;
                name = NameOf(type);
                return opened;
            }
            finally
            {
                ffmpeg.av_log_set_level(level);
                if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
            }
        }
        if (TriesV4l2M2m)
        {
            var m2m = OpenV4l2M2m(codec, st, extraFrames, out var m2mError);
            if (m2m != null) { name = "V4L2"; return m2m; }
            if (m2mError != null) reasons.Add(m2mError);
        }
        error = reasons.Count == 0 ? "使えるデバイスがありません" : string.Join(" / ", reasons);
        return null;
    }

    /// <summary>VIDEO_BUILDER_HWDEC などで V4L2 M2M を指定するときの名前。</summary>
    internal const string V4l2M2mName = "v4l2m2m";

    /// <summary>V4L2 M2M のデコーダを試すか。Linux だけで、ほかの種類に絞っていなければ試す。</summary>
    internal static bool TriesV4l2M2m => OperatingSystem.IsLinux() && (PreferredDevice == null || PreferredDevice == V4l2M2mName);

    /// <summary>
    /// コーデックに対応する V4L2 M2M のデコーダの名前 (FFmpeg の名前)。対応するものがなければ null。
    /// FFmpeg が V4L2 なしで作られていれば、名前があっても avcodec_find_decoder_by_name で見つからない。
    /// </summary>
    internal static string? V4l2M2mDecoderName(AVCodecID id) => id switch
    {
        AVCodecID.AV_CODEC_ID_H264 => "h264_v4l2m2m",
        AVCodecID.AV_CODEC_ID_HEVC => "hevc_v4l2m2m",
        AVCodecID.AV_CODEC_ID_VP8 => "vp8_v4l2m2m",
        AVCodecID.AV_CODEC_ID_VP9 => "vp9_v4l2m2m",
        AVCodecID.AV_CODEC_ID_MPEG2VIDEO => "mpeg2_v4l2m2m",
        AVCodecID.AV_CODEC_ID_MPEG4 => "mpeg4_v4l2m2m",
        AVCodecID.AV_CODEC_ID_H263 => "h263_v4l2m2m",
        AVCodecID.AV_CODEC_ID_VC1 => "vc1_v4l2m2m",
        AVCodecID.AV_CODEC_ID_MPEG1VIDEO => "mpeg1_v4l2m2m",
        _ => null,
    };

    /// <summary>
    /// V4L2 M2M のデコーダ (h264_v4l2m2m など) を開く。開けなければ null (error に理由。対応するデコーダがなければ error も null)。
    /// デコーダは /dev/video* からデコードできる装置を探すので、ない PC では開くのに失敗する。失敗はデコーダごとに覚える。
    /// </summary>
    private static AVCodecContext* OpenV4l2M2m(AVCodec* codec, AVStream* st, int extraFrames, out string? error)
    {
        error = null;
        var decoderName = V4l2M2mDecoderName(codec->id);
        if (decoderName == null) return null;
        lock (Gate)
        {
            if (M2mFailures.TryGetValue(decoderName, out error)) return null;
        }
        var m2m = ffmpeg.avcodec_find_decoder_by_name(decoderName);
        if (m2m == null) { error = $"V4L2: FFmpeg に {decoderName} がありません"; Remember(decoderName, error); return null; }
        if (!HasV4l2Devices()) { error = NoV4l2Devices; Remember(decoderName, error); return null; }

        var ctx = ffmpeg.avcodec_alloc_context3(m2m);
        AVDictionary* opts = null;
        int level = ffmpeg.av_log_get_level();
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
        try
        {
            if (ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar) < 0) { error = "V4L2: デコーダの設定に失敗"; return null; }
            // 取り出したフレームを手元に置く分 (プロキシの区切りなど) だけ、受け取る側の置き場を増やす (既定は 20)
            if (extraFrames > 0) ffmpeg.av_dict_set(&opts, "num_capture_buffers", (20 + extraFrames).ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
            int ret = ffmpeg.avcodec_open2(ctx, m2m, &opts);
            if (ret < 0)
            {
                // 装置はあるが、この形式をデコードできない (Raspberry Pi 5 の HEVC のようなステートレスの装置も、ここで失敗する)
                error = $"V4L2 ({decoderName}): この形式をデコードできる装置がありません ({FFmpegLoader.ErrorText(ret)})";
                Remember(decoderName, error);
                return null;
            }
            var opened = ctx;
            ctx = null;
            lock (Gate) M2mOpened.Add(decoderName);
            return opened;
        }
        finally
        {
            ffmpeg.av_log_set_level(level);
            ffmpeg.av_dict_free(&opts);
            if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
        }

        static void Remember(string decoder, string reason)
        {
            lock (Gate) M2mFailures[decoder] = reason;
        }
    }

    /// <summary>新しく開く動画でハードウェアデコードを試すか (設定と環境変数)。</summary>
    internal static bool Wanted => Mode == HardwareDecodeMode.Auto && !DisabledByEnvironment;

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
            string where = DevicePath(type) is { } path ? type is AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA or AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA ? $" (GPU {path})" : $" ({path})" : "";
            lines.Add(ok ? $"{NameOf(type)}{where}: 使用可能" : error ?? $"{NameOf(type)}: 使用不可");
        }
        if (TriesV4l2M2m) lines.Add(DescribeV4l2M2m());
        return lines.Count == 0 ? "使えるデバイスがありません" : string.Join("\n", lines);
    }

    private const string NoV4l2Devices = "V4L2: 装置 (/dev/video*) がありません";

    /// <summary>V4L2 の装置 (/dev/video*) があるか。ない PC (多くの x64 の PC) では、デコーダを開いてみるまでもない。</summary>
    private static bool HasV4l2Devices()
    {
        try { return Directory.EnumerateFileSystemEntries("/dev", "video*").Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>環境設定に出す V4L2 M2M の状態。デコーダは動画を開くときに試すので、ここでは FFmpeg にあるかと、これまでの結果だけを出す。</summary>
    private static string DescribeV4l2M2m()
    {
        var names = new[] { AVCodecID.AV_CODEC_ID_H264, AVCodecID.AV_CODEC_ID_HEVC, AVCodecID.AV_CODEC_ID_VP9 }
            .Select(V4l2M2mDecoderName).OfType<string>()
            .Where(n => ffmpeg.avcodec_find_decoder_by_name(n) != null).ToList();
        if (names.Count == 0) return "V4L2: FFmpeg が V4L2 なしで作られています";
        if (!HasV4l2Devices()) return NoV4l2Devices;
        List<string> lines;
        lock (Gate)
        {
            lines = M2mOpened.Order(StringComparer.Ordinal).Select(n => $"V4L2 ({n}): 使用中").ToList();
            lines.AddRange(names.Where(M2mFailures.ContainsKey).Select(n => M2mFailures[n]));
        }
        if (lines.Count > 0) return string.Join("\n", lines);
        return $"V4L2 ({string.Join("・", names)}): 動画を開くときに試します";
    }

    /// <summary>テスト用: ソフトウェアでデコードしながら、ハードウェアで開けたように振る舞わせる (戻す処理の確認に使う)。</summary>
    internal static bool TestPretendHardware { get; set; }
    /// <summary>テスト用: ハードウェアの扱いでこの数だけフレームを変換したら、失敗したことにする。</summary>
    internal static int TestFailAfterFrames { get; set; } = -1;
}
