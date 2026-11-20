using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>
/// エンコーダが受け付ける形式を調べる。
/// FFmpeg 7.1 で AVCodec の pix_fmts / sample_fmts が非推奨になり、8.0 で削除されたので、
/// 代わりの avcodec_get_supported_config を使う。
/// </summary>
internal static unsafe class CodecSupport
{
    /// <summary>受け付ける画素形式。空なら「制限なし (不明)」。</summary>
    public static List<AVPixelFormat> PixelFormats(AVCodec* codec)
    {
        var list = new List<AVPixelFormat>();
        void* configs = null; int count = 0;
        if (ffmpeg.avcodec_get_supported_config(null, codec, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0, &configs, &count) < 0
            || configs == null) return list;
        var p = (AVPixelFormat*)configs;
        for (var i = 0; i < count; i++) list.Add(p[i]);
        return list;
    }

    /// <summary>受け付ける音声のサンプル形式。空なら「制限なし (不明)」。</summary>
    public static List<AVSampleFormat> SampleFormats(AVCodec* codec)
    {
        var list = new List<AVSampleFormat>();
        void* configs = null; int count = 0;
        if (ffmpeg.avcodec_get_supported_config(null, codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_FORMAT, 0, &configs, &count) < 0
            || configs == null) return list;
        var p = (AVSampleFormat*)configs;
        for (var i = 0; i < count; i++) list.Add(p[i]);
        return list;
    }
}
