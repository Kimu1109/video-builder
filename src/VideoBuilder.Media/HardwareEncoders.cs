using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>GPU などのハードウェアを使う映像エンコーダ。</summary>
/// <param name="Encoder">FFmpeg のエンコーダ名 (ExportSettings.VideoCodec に入れる)。</param>
/// <param name="Codec">"H.264" などの方式名。</param>
/// <param name="Api">"NVENC" などの仕組みの名前。</param>
public sealed record HardwareEncoder(string Encoder, string Codec, string Api);

public static class HardwareEncoders
{
    /// <summary>対応しているハードウェアエンコーダ。実際に使えるかは環境ごとに Probe で確かめる。</summary>
    public static readonly HardwareEncoder[] Candidates =
    {
        new("h264_nvenc", "H.264", "NVENC"), new("hevc_nvenc", "H.265", "NVENC"), new("av1_nvenc", "AV1", "NVENC"),
        new("h264_qsv", "H.264", "Quick Sync"), new("hevc_qsv", "H.265", "Quick Sync"), new("av1_qsv", "AV1", "Quick Sync"),
        new("h264_vaapi", "H.264", "VAAPI"), new("hevc_vaapi", "H.265", "VAAPI"), new("av1_vaapi", "AV1", "VAAPI"),
        new("h264_amf", "H.264", "AMF"), new("hevc_amf", "H.265", "AMF"), new("av1_amf", "AV1", "AMF"),
        new("h264_videotoolbox", "H.264", "VideoToolbox"), new("hevc_videotoolbox", "H.265", "VideoToolbox"),
        new("h264_v4l2m2m", "H.264", "V4L2"),
    };

    /// <summary>エンコーダ名から仕組みの種類を返す ("nvenc" など)。ソフトウェアエンコーダなら空文字。</summary>
    public static string FamilyOf(string encoder)
    {
        foreach (var family in new[] { "nvenc", "qsv", "vaapi", "amf", "videotoolbox", "v4l2m2m" })
            if (encoder.EndsWith("_" + family, StringComparison.Ordinal)) return family;
        return "";
    }

    public static bool IsHardware(string encoder) => FamilyOf(encoder).Length > 0;

    private static readonly Dictionary<string, (bool Ok, string? Error)> Cache = new();
    private static readonly object Gate = new();

    /// <summary>
    /// このマシンで実際に使えるかを、小さな設定でエンコーダを開いてみて確かめる。結果は覚えておく。
    /// (FFmpeg に組み込まれていても、対応する GPU やドライバがなければ開けない。)
    /// </summary>
    public static unsafe bool Probe(string encoder, out string? error)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(encoder, out var known)) { error = known.Error; return known.Ok; }
            bool ok = false;
            error = null;
            if (!FFmpegLoader.EnsureLoaded()) error = FFmpegLoader.Error;
            else if (ffmpeg.avcodec_find_encoder_by_name(encoder) == null) error = "この FFmpeg には含まれていません";
            else
            {
                // 使えない環境では FFmpeg がエラーを画面に出すので、確認の間だけ黙らせる
                int level = ffmpeg.av_log_get_level();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
                AVCodecContext* ctx = null;
                AVBufferRef* device = null;
                try
                {
                    var settings = new ExportSettings { VideoCodec = encoder, Fps = new Core.Rational(30, 1) };
                    ctx = VideoEncoderSetup.Open(settings, 640, 360, false, out device, out _);
                    ok = true;
                }
                catch (Exception ex) { error = ex.Message; }
                finally
                {
                    if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
                    if (device != null) ffmpeg.av_buffer_unref(&device);
                    ffmpeg.av_log_set_level(level);
                }
            }
            Cache[encoder] = (ok, error);
            return ok;
        }
    }

    /// <summary>このマシンで使えるハードウェアエンコーダの一覧。初回は確認に少し時間がかかる。</summary>
    public static List<HardwareEncoder> FindAvailable() => Candidates.Where(c => Probe(c.Encoder, out _)).ToList();
}

/// <summary>映像エンコーダの設定と開始。ソフトウェア・ハードウェアの違いをここで吸収する。</summary>
internal static unsafe class VideoEncoderSetup
{
    /// <summary>
    /// エンコーダを開いて返す。失敗したら例外を投げ、途中で作ったものは片付ける。
    /// </summary>
    /// <param name="hwDevice">ハードウェアのデバイス (VAAPI のときだけ)。呼び出し側が av_buffer_unref する。</param>
    /// <param name="softwareFormat">メモリ上で用意するフレームの形式 (BGRA からここへ変換する)。</param>
    public static AVCodecContext* Open(ExportSettings s, int width, int height, bool globalHeader, out AVBufferRef* hwDevice, out AVPixelFormat softwareFormat)
    {
        hwDevice = null;
        softwareFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
        string name = s.VideoCodec;
        var codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec == null) throw new InvalidOperationException($"エンコーダ {name} が使えません");
        string family = HardwareEncoders.FamilyOf(name);

        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        AVBufferRef* device = null, frames = null;
        AVDictionary* opts = null;
        try
        {
            ctx->width = width & ~1;
            ctx->height = height & ~1;
            ctx->time_base = new AVRational { num = (int)s.Fps.Den, den = (int)s.Fps.Num };
            ctx->framerate = new AVRational { num = (int)s.Fps.Num, den = (int)s.Fps.Den };
            ctx->gop_size = (int)Math.Max(1, Math.Round(s.Fps.Value * 2));
            ctx->thread_count = 0;
            if (globalHeader) ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            if (family == "vaapi")
            {
                // VAAPI は GPU 上のフレームしか受け取らない。デバイスと、フレームの置き場 (プール) を用意する
                softwareFormat = AVPixelFormat.AV_PIX_FMT_NV12;
                device = OpenVaapiDevice();
                frames = ffmpeg.av_hwframe_ctx_alloc(device);
                if (frames == null) throw new InvalidOperationException("GPU 側のフレーム置き場を作れません");
                var fc = (AVHWFramesContext*)frames->data;
                fc->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
                fc->sw_format = softwareFormat;
                fc->width = ctx->width; fc->height = ctx->height;
                fc->initial_pool_size = 20;
                FFmpegLoader.Check(ffmpeg.av_hwframe_ctx_init(frames), "GPU 側のフレーム置き場を初期化できません");
                ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
                ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(frames);
            }
            else
            {
                softwareFormat = PickPixelFormat(codec, family.Length > 0);
                ctx->pix_fmt = softwareFormat;
            }

            foreach (var (key, value) in OptionsFor(name, s.Crf, s.Preset)) ffmpeg.av_dict_set(&opts, key, value, 0);
            switch (family)
            {
                case "nvenc" or "vaapi" or "amf":
                    ctx->bit_rate = 0;                        // 品質を指定するので、既定のビットレート上限を外す
                    break;
                case "qsv":
                    ctx->global_quality = Math.Clamp(s.Crf, 1, 51);   // 品質一定 (ICQ)
                    break;
                case "videotoolbox" or "v4l2m2m":
                    ctx->bit_rate = EstimateBitrate(ctx->width, ctx->height, s.Fps.Value, s.Crf);
                    break;
                case "" when name is not ("libx264" or "libx265" or "libvpx-vp9" or "libsvtav1" or "prores_ks"):
                    ctx->bit_rate = (long)width * height * 4;
                    break;
            }

            int ret = ffmpeg.avcodec_open2(ctx, codec, &opts);
            FFmpegLoader.Check(ret, $"映像エンコーダ {name} を開けません");
            hwDevice = device;
            device = null;
            var result = ctx;
            ctx = null;
            return result;
        }
        finally
        {
            ffmpeg.av_dict_free(&opts);
            if (frames != null) ffmpeg.av_buffer_unref(&frames);
            if (device != null) ffmpeg.av_buffer_unref(&device);
            if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
        }
    }

    private static AVBufferRef* OpenVaapiDevice()
    {
        // 既定のデバイス → よくある描画ノードの順に試す
        int last = 0;
        foreach (var path in new string?[] { null, "/dev/dri/renderD128", "/dev/dri/renderD129" })
        {
            AVBufferRef* device = null;
            last = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, path, null, 0);
            if (last >= 0) return device;
        }
        throw new InvalidOperationException("VAAPI のデバイスを開けません: " + FFmpegLoader.ErrorText(last));
    }

    private static AVPixelFormat PickPixelFormat(AVCodec* codec, bool hardware)
    {
        if (codec->pix_fmts == null) return AVPixelFormat.AV_PIX_FMT_YUV420P;
        // ソフトウェアは YUV420P、ハードウェアは NV12 (GPU が直接扱える並び) を優先する
        var preferred = hardware
            ? new[] { AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_YUV420P }
            : new[] { AVPixelFormat.AV_PIX_FMT_YUV420P };
        foreach (var want in preferred)
            for (var p = codec->pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
                if (*p == want) return want;
        return codec->pix_fmts[0];
    }

    /// <summary>品質の値 (小さいほど高画質) から、ビットレート指定しかできないエンコーダ用の目安を出す。</summary>
    internal static long EstimateBitrate(int width, int height, double fps, int quality)
    {
        // 品質 23 で 1 ピクセルあたり 0.1 bit。6 下がるごとに 2 倍
        double bitsPerPixel = 0.1 * Math.Pow(2, (23 - quality) / 6.0);
        return (long)Math.Clamp(width * (double)height * fps * bitsPerPixel, 200_000, 400_000_000);
    }

    /// <summary>エンコーダごとの品質・速度のオプション。quality は x264 の CRF と同じ目盛り (0〜51)。</summary>
    internal static Dictionary<string, string> OptionsFor(string encoder, int quality, string preset)
    {
        var o = new Dictionary<string, string>();
        // 速度の指定 (ultrafast … veryslow) を 0 (速い) 〜 5 (遅い) の番号にする
        int speed = Array.IndexOf(new[] { "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow" }, preset);
        if (speed < 0) speed = 3;   // 不明な指定は medium 扱い
        string q = Math.Clamp(quality, 0, 51).ToString();
        switch (HardwareEncoders.FamilyOf(encoder))
        {
            case "nvenc":
                o["preset"] = new[] { "p1", "p2", "p3", "p4", "p5", "p7" }[speed];
                o["tune"] = "hq";
                o["rc"] = "vbr";
                o["cq"] = Math.Clamp(quality, 1, 51).ToString();    // 0 は「自動」の意味なので避ける
                break;
            case "qsv":
                o["preset"] = new[] { "veryfast", "veryfast", "fast", "medium", "slow", "veryslow" }[speed];
                break;
            case "vaapi":
                o["rc_mode"] = "CQP";
                // AV1 の量子化の目盛りは 0〜255
                o["qp"] = encoder.StartsWith("av1", StringComparison.Ordinal) ? Math.Clamp(quality * 5, 1, 255).ToString() : Math.Clamp(quality, 1, 51).ToString();
                break;
            case "amf":
                o["rc"] = "cqp";
                o["qp_i"] = q; o["qp_p"] = q; o["qp_b"] = q;
                o["quality"] = speed <= 1 ? "speed" : speed <= 3 ? "balanced" : "quality";
                break;
            case "videotoolbox":
            case "v4l2m2m":
                break;   // ビットレートで指定する (Open で設定)
            default:
                switch (encoder)
                {
                    case "libx264" or "libx265":
                        o["crf"] = quality.ToString(); o["preset"] = preset;
                        break;
                    case "libvpx-vp9":
                        o["crf"] = Math.Clamp(quality + 10, 0, 63).ToString(); o["b"] = "0"; o["row-mt"] = "1"; o["cpu-used"] = "4";
                        break;
                    case "libsvtav1":
                        o["crf"] = Math.Clamp(quality + 10, 0, 63).ToString(); o["preset"] = "8";
                        break;
                    case "prores_ks":
                        o["profile"] = "3";
                        break;
                }
                break;
        }
        return o;
    }
}
