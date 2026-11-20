using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

public sealed record MediaInfo(bool HasVideo, bool HasAudio, int Width, int Height, double FrameRate, double Duration)
{
    /// <summary>映像の圧縮方式の名前 ("h264" "hevc" など)。映像がなければ空。</summary>
    public string VideoCodec { get; init; } = "";

    /// <summary>映像の長さ (秒)。VideoReader.Duration と同じ求め方 (映像ストリームの長さ、なければファイルの長さ)。</summary>
    public double VideoDuration { get; init; }

    /// <summary>ファイルの概要を調べる。開けなければ null。</summary>
    public static unsafe MediaInfo? Probe(string path)
    {
        if (!FFmpegLoader.EnsureLoaded() || !File.Exists(path)) return null;
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
        try
        {
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return null;
            int v = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            int a = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            int w = 0, h = 0; double fps = 0, videoDuration = 0;
            string codec = "";
            if (v >= 0)
            {
                var st = fmt->streams[v];
                // カバー画像 (音声ファイルのアートワーク) は動画として扱わない
                if ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0) v = -1;
                else
                {
                    w = st->codecpar->width; h = st->codecpar->height;
                    codec = ffmpeg.avcodec_get_name(st->codecpar->codec_id) ?? "";
                    var r = st->avg_frame_rate.num != 0 ? st->avg_frame_rate : st->r_frame_rate;
                    fps = r.den != 0 ? (double)r.num / r.den : 0;
                    videoDuration = st->duration != ffmpeg.AV_NOPTS_VALUE && st->duration > 0 ? st->duration * ffmpeg.av_q2d(st->time_base) : 0;
                }
            }
            double dur = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            if (videoDuration <= 0) videoDuration = dur;
            return new MediaInfo(v >= 0, a >= 0, w, h, fps, dur) { VideoCodec = codec, VideoDuration = videoDuration };
        }
        finally { ffmpeg.avformat_close_input(&fmt); }
    }
}
