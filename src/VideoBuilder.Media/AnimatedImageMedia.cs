using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>
/// アニメーション WebP (FFmpeg 9 の webp_anim) を動画として読むための補い。
/// 入れ物に長さが入っておらず、シークもできない (シークすると末尾まで進んでしまう) ので、
/// 長さはパケットを数えて求め、前へ戻るときはファイルを開き直す。どちらも小さなファイルなので速い。
/// </summary>
internal static unsafe class AnimatedImageMedia
{
    /// <summary>アニメーション画像の映像か (透明な部分を残して描く)。</summary>
    public static bool IsAnimatedImage(AVStream* st) => ffmpeg.avcodec_get_name(st->codecpar->codec_id) is "webp_anim" or "gif" or "apng";

    /// <summary>BGRA (アルファを掛けていない) の画素に、アルファを掛ける (Skia の Premul にする)。</summary>
    public static void Premultiply(byte* p, int pixels)
    {
        for (int i = 0; i < pixels; i++, p += 4)
        {
            int a = p[3];
            if (a == 255) continue;
            p[0] = (byte)((p[0] * a + 127) / 255);
            p[1] = (byte)((p[1] * a + 127) / 255);
            p[2] = (byte)((p[2] * a + 127) / 255);
        }
    }

    /// <summary>長さを数えて求め、開き直して戻る必要がある映像か。</summary>
    public static bool NeedsScan(AVStream* st) => ffmpeg.avcodec_get_name(st->codecpar->codec_id) == "webp_anim";

    /// <summary>映像ストリームの最後のパケットの終わりまでの長さ (秒)。読めなければ 0。</summary>
    public static double ScanDuration(string path, int stream)
    {
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return 0;
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0 || stream >= fmt->nb_streams) return 0;
            var st = fmt->streams[stream];
            long start = st->start_time != ffmpeg.AV_NOPTS_VALUE ? st->start_time : 0, end = start;
            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                if (pkt->stream_index == stream && pkt->pts != ffmpeg.AV_NOPTS_VALUE)
                    end = Math.Max(end, pkt->pts + Math.Max(0, pkt->duration));
                ffmpeg.av_packet_unref(pkt);
            }
            return (end - start) * ffmpeg.av_q2d(st->time_base);
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.avformat_close_input(&fmt);
        }
    }
}
