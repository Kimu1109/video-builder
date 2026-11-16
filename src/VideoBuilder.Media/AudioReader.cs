using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>
/// 音声を「出力サンプルレート・ステレオ・float」に変換して、サンプル位置を指定して読み出す。スレッドセーフではない。
/// </summary>
public sealed unsafe class AudioReader : IDisposable
{
    private AVFormatContext* _fmt;
    private AVCodecContext* _dec;
    private SwrContext* _swr;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private readonly int _stream;
    private readonly AVRational _tb;
    private readonly long _startPts;
    private readonly int _rate;

    // 変換済みサンプルのバッファ (インターリーブ, 2ch)
    private float[] _buf = new float[1 << 16];
    private long _bufStart;     // _buf[0] のサンプル位置
    private int _bufFrames;
    private bool _needPosition = true;
    private bool _draining, _eof;

    public const int Channels = 2;
    public double Duration { get; }

    public AudioReader(string path, int outputSampleRate)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        _rate = outputSampleRate;
        AVFormatContext* fmt = null;
        FFmpegLoader.Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "ファイルを開けません");
        _fmt = fmt;
        try
        {
            FFmpegLoader.Check(ffmpeg.avformat_find_stream_info(_fmt, null), "ストリーム情報を取得できません");
            AVCodec* codec = null;
            _stream = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            FFmpegLoader.Check(_stream, "音声ストリームがありません");
            var st = _fmt->streams[_stream];
            _dec = ffmpeg.avcodec_alloc_context3(codec);
            FFmpegLoader.Check(ffmpeg.avcodec_parameters_to_context(_dec, st->codecpar), "デコーダの設定に失敗");
            FFmpegLoader.Check(ffmpeg.avcodec_open2(_dec, codec, null), "デコーダを開けません");
            _tb = st->time_base;
            _startPts = st->start_time != ffmpeg.AV_NOPTS_VALUE ? st->start_time : 0;
            Duration = st->duration != ffmpeg.AV_NOPTS_VALUE && st->duration > 0
                ? st->duration * ffmpeg.av_q2d(_tb)
                : _fmt->duration > 0 ? _fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;

            if (_dec->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
                ffmpeg.av_channel_layout_default(&_dec->ch_layout, _dec->ch_layout.nb_channels);
            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, Channels);
            SwrContext* swr = null;
            FFmpegLoader.Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _rate,
                &_dec->ch_layout, _dec->sample_fmt, _dec->sample_rate, 0, null), "リサンプラの作成に失敗");
            _swr = swr;
            FFmpegLoader.Check(ffmpeg.swr_init(_swr), "リサンプラの初期化に失敗");
            _frame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>startSample から dst.Length / 2 フレーム分を読む。データがない部分は 0 で埋める。</summary>
    public void Read(long startSample, Span<float> dst)
    {
        dst.Clear();
        if (_dec == null) return;
        int frames = dst.Length / Channels;
        long end = startSample + frames;

        bool inRange = !_needPosition && startSample >= _bufStart && startSample <= _bufStart + _bufFrames + _rate;
        if (!inRange) Seek(startSample);

        while (!_eof && (_needPosition || _bufStart + _bufFrames < end))
        {
            if (!DecodeMore()) break;
            // 読み終わった部分を捨ててバッファが膨らまないようにする
            if (!_needPosition && startSample > _bufStart)
            {
                int drop = (int)Math.Min(startSample - _bufStart, _bufFrames);
                if (drop > 0) DropFront(drop);
            }
        }
        if (_needPosition) return;

        long from = Math.Max(startSample, _bufStart);
        long to = Math.Min(end, _bufStart + _bufFrames);
        if (to <= from) return;
        new ReadOnlySpan<float>(_buf, (int)(from - _bufStart) * Channels, (int)(to - from) * Channels)
            .CopyTo(dst.Slice((int)(from - startSample) * Channels));
    }

    private void DropFront(int frames)
    {
        Array.Copy(_buf, frames * Channels, _buf, 0, (_bufFrames - frames) * Channels);
        _bufFrames -= frames; _bufStart += frames;
    }

    private void Seek(long sample)
    {
        double sec = Math.Max(0, (double)sample / _rate - 0.1);
        long ts = _startPts + (long)(sec / ffmpeg.av_q2d(_tb));
        ffmpeg.av_seek_frame(_fmt, _stream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_dec);
        // リサンプラに残っているサンプルを捨てる
        ffmpeg.swr_close(_swr); ffmpeg.swr_init(_swr);
        _bufFrames = 0; _needPosition = true; _draining = false; _eof = false;
    }

    private bool DecodeMore()
    {
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_frame(_dec, _frame);
            if (ret == 0) { Append(); return true; }
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) || _draining) { _eof = true; return false; }
            ret = ffmpeg.av_read_frame(_fmt, _pkt);
            if (ret < 0) { ffmpeg.avcodec_send_packet(_dec, null); _draining = true; continue; }
            if (_pkt->stream_index == _stream) ffmpeg.avcodec_send_packet(_dec, _pkt);
            ffmpeg.av_packet_unref(_pkt);
        }
    }

    private void Append()
    {
        if (_needPosition)
        {
            long pts = _frame->best_effort_timestamp;
            double sec = pts == ffmpeg.AV_NOPTS_VALUE ? 0 : (pts - _startPts) * ffmpeg.av_q2d(_tb);
            _bufStart = (long)Math.Round(sec * _rate);
            _bufFrames = 0;
            _needPosition = false;
        }
        int max = ffmpeg.swr_get_out_samples(_swr, _frame->nb_samples);
        if (max <= 0) return;
        int need = (_bufFrames + max) * Channels;
        if (_buf.Length < need) Array.Resize(ref _buf, Math.Max(need, _buf.Length * 2));
        fixed (float* p = &_buf[_bufFrames * Channels])
        {
            byte* outPtr = (byte*)p;
            int got = ffmpeg.swr_convert(_swr, &outPtr, max, _frame->extended_data, _frame->nb_samples);
            if (got > 0) _bufFrames += got;
        }
    }

    public void Dispose()
    {
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
        if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
    }
}
