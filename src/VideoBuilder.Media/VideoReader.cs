using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>動画ファイルから、指定した時刻のフレームを BGRA で取り出す。スレッドセーフではない。</summary>
public sealed unsafe class VideoReader : IDisposable
{
    private AVFormatContext* _fmt;
    private AVCodecContext* _dec;
    private SwsContext* _sws;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private readonly int _stream;
    private readonly AVRational _tb;
    private readonly long _startPts;
    private readonly long _defaultDur;

    private long _curPts = long.MinValue;
    private long _curDur;
    private bool _hasFrame;
    private bool _converted;
    private bool _draining;
    private bool _eof;
    private byte[] _buffer = Array.Empty<byte>();

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    public double Duration { get; }
    /// <summary>最後に GetFrame が返したフレームの時刻 (秒)。同じフレームかどうかの判定に使う。</summary>
    public long CurrentFrameId => _curPts;

    public VideoReader(string path)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        Path = path;
        AVFormatContext* fmt = null;
        FFmpegLoader.Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "ファイルを開けません");
        _fmt = fmt;
        try
        {
            FFmpegLoader.Check(ffmpeg.avformat_find_stream_info(_fmt, null), "ストリーム情報を取得できません");
            AVCodec* codec = null;
            _stream = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            FFmpegLoader.Check(_stream, "映像ストリームがありません");
            var st = _fmt->streams[_stream];
            _dec = ffmpeg.avcodec_alloc_context3(codec);
            FFmpegLoader.Check(ffmpeg.avcodec_parameters_to_context(_dec, st->codecpar), "デコーダの設定に失敗");
            _dec->thread_count = Math.Min(Environment.ProcessorCount, 8);
            FFmpegLoader.Check(ffmpeg.avcodec_open2(_dec, codec, null), "デコーダを開けません");

            Width = _dec->width; Height = _dec->height;
            _tb = st->time_base;
            _startPts = st->start_time != ffmpeg.AV_NOPTS_VALUE ? st->start_time : 0;
            var r = st->avg_frame_rate.num != 0 ? st->avg_frame_rate : st->r_frame_rate;
            FrameRate = r.den != 0 && r.num != 0 ? (double)r.num / r.den : 30;
            _defaultDur = Math.Max(1, (long)Math.Round(1.0 / FrameRate / ffmpeg.av_q2d(_tb)));
            Duration = st->duration != ffmpeg.AV_NOPTS_VALUE && st->duration > 0
                ? st->duration * ffmpeg.av_q2d(_tb)
                : _fmt->duration > 0 ? _fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            _frame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>
    /// seconds の位置のフレームを返す (Width*Height*4 バイトの BGRA)。
    /// 返す配列は内部バッファなので、次の呼び出しまでに使い切ること。
    /// </summary>
    public byte[]? GetFrame(double seconds)
    {
        if (_dec == null) return null;
        long target = _startPts + (long)Math.Floor(Math.Max(0, seconds) / ffmpeg.av_q2d(_tb) + 0.5 * 1e-6);
        long seekAhead = (long)(2.0 / ffmpeg.av_q2d(_tb));

        bool covers = _hasFrame && target >= _curPts && target < _curPts + _curDur;
        if (!covers)
        {
            if (!_hasFrame || target < _curPts || target > _curPts + seekAhead)
                Seek(target);
            while (!(_hasFrame && target < _curPts + _curDur))
            {
                if (!DecodeNext()) break;   // 末尾: 最後のフレームをそのまま使う
            }
        }
        if (!_hasFrame) return null;
        if (!_converted) Convert();
        return _buffer;
    }

    private void Seek(long target)
    {
        ffmpeg.av_seek_frame(_fmt, _stream, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_dec);
        _hasFrame = false; _draining = false; _eof = false;
    }

    private bool DecodeNext()
    {
        if (_eof) return false;
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_frame(_dec, _frame);
            if (ret == 0)
            {
                long pts = _frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _hasFrame ? _curPts + _curDur : _startPts;
                _curPts = pts;
                _curDur = _frame->duration > 0 ? _frame->duration : _defaultDur;
                _hasFrame = true; _converted = false;
                return true;
            }
            if (ret == ffmpeg.AVERROR_EOF) { _eof = true; return false; }
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) { _eof = true; return false; }
            if (_draining) { _eof = true; return false; }

            ret = ffmpeg.av_read_frame(_fmt, _pkt);
            if (ret < 0)
            {
                ffmpeg.avcodec_send_packet(_dec, null);
                _draining = true;
                continue;
            }
            if (_pkt->stream_index == _stream) ffmpeg.avcodec_send_packet(_dec, _pkt);
            ffmpeg.av_packet_unref(_pkt);
        }
    }

    private void Convert()
    {
        int w = _frame->width, h = _frame->height;
        if (w <= 0 || h <= 0) return;
        _sws = ffmpeg.sws_getCachedContext(_sws, w, h, (AVPixelFormat)_frame->format, Width, Height,
            AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);
        if (_sws == null) return;
        int size = Width * Height * 4;
        if (_buffer.Length != size) _buffer = new byte[size];
        fixed (byte* p = _buffer)
        {
            var dst = new byte*[4] { p, null, null, null };
            var stride = new int[4] { Width * 4, 0, 0, 0 };
            ffmpeg.sws_scale(_sws, _frame->data, _frame->linesize, 0, h, dst, stride);
        }
        _converted = true;
    }

    public void Dispose()
    {
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
    }
}
