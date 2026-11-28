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
        new("h264_v4l2m2m", "H.264", "V4L2"), new("hevc_v4l2m2m", "H.265", "V4L2"),   // ARM の SoC (設計書 22.37)
        // Windows の Media Foundation (GPU のドライバが入れるハードウェアのエンコーダ)。
        // Windows on ARM (Snapdragon の Adreno など) には NVENC・Quick Sync・AMF がないので、これで GPU のエンコーダを使う。
        // ほかの仕組み (NVENC など) で同じ方式が使えるときは、そちらを使う (FindAvailable)
        new("h264_mf", "H.264", "Media Foundation"), new("hevc_mf", "H.265", "Media Foundation"), new("av1_mf", "AV1", "Media Foundation"),
    };

    /// <summary>エンコーダ名から仕組みの種類を返す ("nvenc" など)。ソフトウェアエンコーダなら空文字。</summary>
    public static string FamilyOf(string encoder)
    {
        foreach (var family in new[] { "nvenc", "qsv", "vaapi", "amf", "videotoolbox", "v4l2m2m", "mf" })
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

    /// <summary>
    /// このマシンで使えるハードウェアエンコーダの一覧。初回は確認に少し時間がかかる。
    /// Media Foundation のものは、同じ方式 (H.264 など) をほかの仕組み (NVENC など、GPU の会社のもの) で使えないときだけ入れる
    /// (同じ GPU を通すので、細かく設定できる方を使う)。
    /// </summary>
    public static List<HardwareEncoder> FindAvailable() => WithoutCoveredMediaFoundation(Candidates.Where(c => Probe(c.Encoder, out _)).ToList());

    internal static List<HardwareEncoder> WithoutCoveredMediaFoundation(List<HardwareEncoder> found) =>
        found.Where(e => FamilyOf(e.Encoder) != "mf" || !found.Any(o => FamilyOf(o.Encoder) != "mf" && o.Codec == e.Codec)).ToList();
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
        if (s.D3D11Device == IntPtr.Zero || !OperatingSystem.IsWindows()) return OpenCore(s, width, height, globalHeader, d3d11: false, out hwDevice, out softwareFormat);
        // Direct3D 11 のフレームで開けなければ (ドライバ・エンコーダが受け取らない)、これまでどおりメモリ上のフレームで開き直す
        try { return OpenCore(s, width, height, globalHeader, d3d11: true, out hwDevice, out softwareFormat); }
        catch (InvalidOperationException ex)
        {
            var result = OpenCore(s, width, height, globalHeader, d3d11: false, out hwDevice, out softwareFormat);
            LastD3D11Error = "Direct3D 11 のフレームでエンコーダを開けません: " + ex.Message;
            return result;
        }
    }

    private static AVCodecContext* OpenCore(ExportSettings s, int width, int height, bool globalHeader, bool d3d11, out AVBufferRef* hwDevice, out AVPixelFormat softwareFormat)
    {
        hwDevice = null;
        LastD3D11Error = null;
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
                if (s.PixelFormat.Length > 0 && ffmpeg.av_get_pix_fmt(s.PixelFormat) is var wanted and not AVPixelFormat.AV_PIX_FMT_NONE
                    && CodecSupport.PixelFormats(codec).Contains(wanted))
                    softwareFormat = wanted;
                ctx->pix_fmt = softwareFormat;
                LastCudaError = null;
                // NVENC に CUDA のフレームを渡す (GPU で描いた絵を、GPU の中で写して渡すため。設計書 22.62)
                if (family == "nvenc" && s.CudaFrames)
                {
                    if (softwareFormat != AVPixelFormat.AV_PIX_FMT_NV12) LastCudaError = "エンコーダの形式が NV12 ではありません";
                    else if (!CodecSupport.PixelFormats(codec).Contains(AVPixelFormat.AV_PIX_FMT_CUDA)) LastCudaError = "エンコーダが CUDA のフレームを受け取りません";
                    else if (CudaFrames(ctx, out device, out frames, out var cudaError)) ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_CUDA;
                    else LastCudaError = cudaError;
                }
                // Windows: 描画のデバイスの Direct3D 11 のフレームを渡す (GPU で描いた絵を、エンコーダのフレームへ直接描くため。設計書 22.65)
                if (d3d11)
                {
                    if (family is not ("nvenc" or "amf" or "qsv" or "mf")) LastD3D11Error = "GPU のエンコーダではありません";
                    else if (softwareFormat != AVPixelFormat.AV_PIX_FMT_NV12) LastD3D11Error = "エンコーダの形式が NV12 ではありません";
                    else if (!CodecSupport.PixelFormats(codec).Contains(AVPixelFormat.AV_PIX_FMT_D3D11)) LastD3D11Error = "エンコーダが Direct3D 11 のフレームを受け取りません";
                    else if (D3D11Frames(ctx, s.D3D11Device, out device, out frames, out var d3dError)) ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                    else LastD3D11Error = d3dError;
                }
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
                case "mf":
                    // ドライバのエンコーダによって使える品質の指定が違うので、どれでも通じるビットレートで指定する。
                    // 上限は目安の 2 倍まで (ピーク制限付き VBR。対応していなければドライバの既定の方式になる)
                    ctx->bit_rate = EstimateBitrate(ctx->width, ctx->height, s.Fps.Value, s.Crf);
                    ctx->rc_max_rate = ctx->bit_rate * 2;
                    ctx->max_b_frames = 0;   // B フレームに対応していないエンコーダが多い
                    // 速度と品質の釣り合い (0 = 速い 〜 100 = 高画質)
                    ctx->compression_level = MediaFoundationQualityVsSpeed(s.Preset);
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

    /// <summary>直前の Open で、CUDA のフレームを使えなかった理由 (ExportSettings.CudaFrames のとき。同じスレッドで読む)。</summary>
    [ThreadStatic] internal static string? LastCudaError;

    /// <summary>直前の Open で、Direct3D 11 のフレームを使えなかった理由 (ExportSettings.D3D11Device のとき。同じスレッドで読む)。</summary>
    [ThreadStatic] internal static string? LastD3D11Error;

    // libavutil/hwcontext_d3d11va.h の AVD3D11VAFramesContext (FFmpeg.AutoGen にはない)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct AVD3D11VAFramesContextLayout { public IntPtr Texture; public uint BindFlags, MiscFlags; public IntPtr TextureInfos; }

    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8, D3D11_BIND_RENDER_TARGET = 0x20;
    private static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    /// <summary>
    /// d3dDevice (描画の ANGLE の ID3D11Device) を包んだ FFmpeg の D3D11VA のデバイスと、NV12 のフレームの置き場 (描画先にできるテクスチャ) を
    /// 作って ctx に付ける。作れなければ false (何も付けない)。設計書 22.65。
    /// </summary>
    private static bool D3D11Frames(AVCodecContext* ctx, IntPtr d3dDevice, out AVBufferRef* device, out AVBufferRef* frames, out string? error)
    {
        device = null; frames = null; error = null;
        var d = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (d == null) { error = "D3D11VA のデバイスを作れません"; return false; }
        var hw = (HardwareFrame.AVD3D11VADeviceContextLayout*)((AVHWDeviceContext*)d->data)->hwctx;
        // FFmpeg が手放すときに Release するので、参照を 1 つ増やして渡す
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)d3dDevice)[1])(d3dDevice);
        hw->Device = d3dDevice;
        // 描画 (ANGLE。描画のスレッド) とエンコーダ (書き出しのスレッド) が同じ即時のコンテキストを使うので、複数のスレッドから使えるようにする
        IntPtr mt;
        var iid = IID_ID3D10Multithread;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)d3dDevice)[0])(d3dDevice, &iid, &mt) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)(*(IntPtr**)mt)[5])(mt, 1);   // SetMultithreadProtected(TRUE)
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)mt)[2])(mt);
        }
        int ret = ffmpeg.av_hwdevice_ctx_init(d);
        if (ret < 0) { ffmpeg.av_buffer_unref(&d); error = "D3D11VA のデバイスを初期化できません (描画のデバイスに動画の機能がない?): " + FFmpegLoader.ErrorText(ret); return false; }
        var f = ffmpeg.av_hwframe_ctx_alloc(d);
        if (f == null) { ffmpeg.av_buffer_unref(&d); error = "Direct3D 11 のフレーム置き場を作れません"; return false; }
        var fc = (AVHWFramesContext*)f->data;
        fc->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        fc->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        fc->width = ctx->width; fc->height = ctx->height;
        fc->initial_pool_size = 20;
        // ANGLE で Y・U・V の面へ描く (描画先) ので、描画先と読み取りに使えるテクスチャにする
        ((AVD3D11VAFramesContextLayout*)fc->hwctx)->BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        ret = ffmpeg.av_hwframe_ctx_init(f);
        if (ret < 0)
        {
            ffmpeg.av_buffer_unref(&f); ffmpeg.av_buffer_unref(&d);
            error = "Direct3D 11 のフレーム置き場を初期化できません (NV12 を描画先にできない?): " + FFmpegLoader.ErrorText(ret);
            return false;
        }
        ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(f);
        device = d; frames = f;
        return true;
    }

    /// <summary>CUDA のデバイスと、NV12 のフレームの置き場を作って ctx に付ける。作れなければ false (何も付けない)。</summary>
    private static bool CudaFrames(AVCodecContext* ctx, out AVBufferRef* device, out AVBufferRef* frames, out string? error)
    {
        device = null; frames = null; error = null;
        AVBufferRef* d = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(&d, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, null, null, 0);
        if (ret < 0) { error = "CUDA のデバイスを開けません: " + FFmpegLoader.ErrorText(ret); return false; }
        var f = ffmpeg.av_hwframe_ctx_alloc(d);
        if (f == null) { ffmpeg.av_buffer_unref(&d); error = "CUDA のフレーム置き場を作れません"; return false; }
        var fc = (AVHWFramesContext*)f->data;
        fc->format = AVPixelFormat.AV_PIX_FMT_CUDA;
        fc->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        fc->width = ctx->width; fc->height = ctx->height;
        ret = ffmpeg.av_hwframe_ctx_init(f);
        if (ret < 0)
        {
            ffmpeg.av_buffer_unref(&f); ffmpeg.av_buffer_unref(&d);
            error = "CUDA のフレーム置き場を初期化できません: " + FFmpegLoader.ErrorText(ret);
            return false;
        }
        ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(f);
        device = d; frames = f;
        return true;
    }

    /// <summary>
    /// VAAPI のデバイスを開くときに試す順番。描画に選んだ GPU のレンダーノード (chosen。動画のデコードと同じ
    /// <see cref="HardwareDecoding.DevicePath"/>) → 既定のデバイス → よくあるレンダーノード。同じものは 2 回試さない。
    /// 選んだ GPU で開かないと、描いた絵を別の GPU (多くは CPU 内蔵の GPU) へ送ってエンコードすることになる。
    /// </summary>
    internal static List<string?> VaapiDeviceCandidates(string? chosen)
    {
        var list = new List<string?>();
        if (!string.IsNullOrEmpty(chosen)) list.Add(chosen);
        foreach (var path in new string?[] { null, "/dev/dri/renderD128", "/dev/dri/renderD129" })
            if (!list.Contains(path)) list.Add(path);
        return list;
    }

    private static AVBufferRef* OpenVaapiDevice()
    {
        int last = 0;
        foreach (var path in VaapiDeviceCandidates(HardwareDecoding.DevicePath(AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI)))
        {
            AVBufferRef* device = null;
            last = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, path, null, 0);
            if (last >= 0) return device;
        }
        throw new InvalidOperationException("VAAPI のデバイスを開けません: " + FFmpegLoader.ErrorText(last));
    }

    private static AVPixelFormat PickPixelFormat(AVCodec* codec, bool hardware)
    {
        var formats = CodecSupport.PixelFormats(codec);
        if (formats.Count == 0) return AVPixelFormat.AV_PIX_FMT_YUV420P;
        // ソフトウェアは YUV420P、ハードウェアは NV12 (GPU が直接扱える並び) を優先する
        var preferred = hardware
            ? new[] { AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_YUV420P }
            : new[] { AVPixelFormat.AV_PIX_FMT_YUV420P };
        foreach (var want in preferred)
            if (formats.Contains(want)) return want;
        return formats[0];
    }

    /// <summary>速度の指定 (ultrafast … veryslow) を、Media Foundation の「速度と品質の釣り合い」(0 = 速い 〜 100 = 高画質) にする。</summary>
    internal static int MediaFoundationQualityVsSpeed(string preset)
    {
        int speed = Array.IndexOf(new[] { "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow" }, preset);
        return (speed < 0 ? 3 : speed) * 20;
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
            case "mf":
                // ソフトウェアの Media Foundation のエンコーダ (CPU で動く) に黙って落ちないよう、ハードウェアに限る
                o["hw_encoding"] = "1";
                o["rate_control"] = "pc_vbr";
                o["scenario"] = "archive";
                break;
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
