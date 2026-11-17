using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>動画ファイルから、指定した時刻のフレームを BGRA で取り出す。スレッドセーフではない。</summary>
public sealed unsafe class VideoReader : IDisposable
{
    private AVFormatContext* _fmt;
    private AVCodecContext* _dec;
    private SwsContext* _sws;
    private AVFrame* _frame;
    private AVFrame* _swFrame;      // GPU からメモリへ写したフレーム (ハードウェアデコードのときだけ使う)
    private AVPacket* _pkt;
    private AVCodec* _codec;
    private AVPixelFormat _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _hardware;
    private int _convertedCount;
    private bool _fellBack;
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
    private byte* _native;          // 変換済みフレーム (BGRA)。アンマネージドなので GC で動かない
    private int _nativeSize;
    private bool _nativeValid;
    private byte[] _managed = Array.Empty<byte>();

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    public double Duration { get; }
    /// <summary>デコードに使っているもの ("VAAPI" や "ソフトウェア")。</summary>
    public string DecoderName { get; private set; } = "ソフトウェア";
    /// <summary>ハードウェアでデコードしているか。途中で失敗してソフトウェアに戻ったら false になる。</summary>
    public bool IsHardware => _hardware;
    /// <summary>ハードウェアデコードを使わなかった (または途中でやめた) 理由。</summary>
    public string? HardwareError { get; private set; }

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
            _codec = codec;
            bool wantHardware = HardwareDecoding.Mode == HardwareDecodeMode.Auto && !HardwareDecoding.DisabledByEnvironment;
            if (!wantHardware || !OpenHardwareDecoder(st)) OpenSoftwareDecoder(st);

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

    private void OpenSoftwareDecoder(AVStream* st)
    {
        var ctx = ffmpeg.avcodec_alloc_context3(_codec);
        try
        {
            FFmpegLoader.Check(ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar), "デコーダの設定に失敗");
            ctx->thread_count = Math.Min(Environment.ProcessorCount, 8);
            FFmpegLoader.Check(ffmpeg.avcodec_open2(ctx, _codec, null), "デコーダを開けません");
        }
        catch { ffmpeg.avcodec_free_context(&ctx); throw; }
        _dec = ctx;
        _hardware = false;
        _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        DecoderName = "ソフトウェア";
    }

    /// <summary>使えるデバイスを順に試して、ハードウェアのデコーダを開く。開けなければ false。</summary>
    private bool OpenHardwareDecoder(AVStream* st)
    {
        if (HardwareDecoding.TestPretendHardware)
        {
            OpenSoftwareDecoder(st);
            _hardware = true;
            DecoderName = "TEST";
            return true;
        }
        var reasons = new List<string>();
        foreach (var type in HardwareDecoding.DeviceOrder())
        {
            var format = HardwareDecoding.HardwareFormatFor(_codec, type);
            if (format == null) { reasons.Add($"{HardwareDecoding.NameOf(type)}: この形式には対応していません"); continue; }
            var device = HardwareDecoding.SharedDevice(type, out var error);
            if (device == null) { reasons.Add(error ?? HardwareDecoding.NameOf(type)); continue; }

            var ctx = ffmpeg.avcodec_alloc_context3(_codec);
            int level = ffmpeg.av_log_get_level();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            try
            {
                if (ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar) < 0) { reasons.Add("デコーダの設定に失敗"); continue; }
                ctx->hw_device_ctx = ffmpeg.av_buffer_ref(device);
                ctx->get_format = HardwareDecoding.GetFormatPointer;
                ctx->opaque = (void*)(nint)(int)format.Value;     // GetFormat で読む
                // GPU がデコードするので、CPU のスレッドは少なくてよい
                ctx->thread_count = 1;
                int ret = ffmpeg.avcodec_open2(ctx, _codec, null);
                if (ret < 0) { reasons.Add($"{HardwareDecoding.NameOf(type)}: {FFmpegLoader.ErrorText(ret)}"); continue; }
                _dec = ctx;
                ctx = null;
                _hardware = true;
                _hwFormat = format.Value;
                DecoderName = HardwareDecoding.NameOf(type);
                return true;
            }
            finally
            {
                ffmpeg.av_log_set_level(level);
                if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
            }
        }
        HardwareError = reasons.Count == 0 ? "使えるデバイスがありません" : string.Join(" / ", reasons);
        return false;
    }

    /// <summary>
    /// ハードウェアデコードが途中で失敗したとき、ソフトウェアのデコーダに切り替える。
    /// 呼び出し側は、同じ位置をもう一度取り出し直す。
    /// </summary>
    private void FallBackToSoftware(string reason)
    {
        HardwareError = reason;
        if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
        OpenSoftwareDecoder(_fmt->streams[_stream]);
        _hasFrame = false; _converted = false; _draining = false; _eof = false;
        _fellBack = true;
    }

    /// <summary>
    /// seconds の位置のフレームを返す (Width*Height*4 バイトの BGRA)。
    /// 返す配列は内部バッファなので、次の呼び出しまでに使い切ること。
    /// </summary>
    public byte[]? GetFrame(double seconds)
    {
        var p = GetFramePointer(seconds);
        if (p == IntPtr.Zero) return null;
        if (_managed.Length != _nativeSize) _managed = new byte[_nativeSize];
        System.Runtime.InteropServices.Marshal.Copy(p, _managed, 0, _nativeSize);
        return _managed;
    }

    /// <summary>
    /// seconds の位置のフレーム (BGRA, 1 行 Width*4 バイト) を指すポインタを返す。複製しないので速い。
    /// 中身は次に別のフレームを取り出すと書き換わる。取り出せなければ IntPtr.Zero。
    /// </summary>
    public IntPtr GetFramePointer(double seconds)
    {
        // ハードウェアデコードが途中で失敗したら、ソフトウェアに切り替えて 1 回だけやり直す
        _fellBack = false;
        var result = GetFramePointerCore(seconds);
        if (_fellBack)
        {
            _fellBack = false;   // 切り替えは済んだので、ここからは普通に変換する
            result = GetFramePointerCore(seconds);
        }
        return result;
    }

    private IntPtr GetFramePointerCore(double seconds)
    {
        if (_dec == null) return IntPtr.Zero;
        long target = _startPts + (long)Math.Floor(Math.Max(0, seconds) / ffmpeg.av_q2d(_tb) + 0.5 * 1e-6);
        long seekAhead = (long)(2.0 / ffmpeg.av_q2d(_tb));

        bool covers = _hasFrame && target >= _curPts && target < _curPts + _curDur;
        if (!covers)
        {
            if (!_hasFrame || target < _curPts || target > _curPts + seekAhead)
                Seek(target);
            while (!(_hasFrame && target < _curPts + _curDur))
            {
                // 末尾が近いときは、今のフレームを先に変換しておく (末尾に達するとデコーダ側のフレームが消えるため)
                if (_draining && _hasFrame && !_converted) Convert();
                if (!DecodeNext()) break;   // 末尾: 最後のフレームをそのまま使う
            }
        }
        if (!_hasFrame) return IntPtr.Zero;
        if (!_converted) Convert();
        // 変換できなかった場合 (末尾を過ぎた後など) は、最後に変換できたフレームを返す
        return _nativeValid ? (IntPtr)_native : IntPtr.Zero;
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
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                // ハードウェアでのデコードに失敗した (対応していないプロファイルなど) → ソフトウェアでやり直す
                if (_hardware) { FallBackToSoftware("デコードに失敗: " + FFmpegLoader.ErrorText(ret)); return false; }
                _eof = true; return false;
            }
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
        if (_fellBack) return;
        var source = _frame;
        if (_hardware)
        {
            if (HardwareDecoding.TestFailAfterFrames >= 0 && _convertedCount >= HardwareDecoding.TestFailAfterFrames)
            {
                FallBackToSoftware("テスト用の失敗");
                return;
            }
            if ((AVPixelFormat)_frame->format == _hwFormat && _hwFormat != AVPixelFormat.AV_PIX_FMT_NONE)
            {
                // GPU 上のフレームをメモリへ写す (変換に使うのは表示するフレームだけ。シーク途中のフレームは写さない)
                if (_swFrame == null) _swFrame = ffmpeg.av_frame_alloc();
                ffmpeg.av_frame_unref(_swFrame);
                int ret = ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0);
                if (ret < 0) { FallBackToSoftware("GPU からフレームを写せません: " + FFmpegLoader.ErrorText(ret)); return; }
                source = _swFrame;
            }
        }
        int w = source->width, h = source->height;
        if (w <= 0 || h <= 0) return;
        _sws = ffmpeg.sws_getCachedContext(_sws, w, h, (AVPixelFormat)source->format, Width, Height,
            AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);
        if (_sws == null) return;
        int size = Width * Height * 4;
        if (_native == null || _nativeSize != size)
        {
            if (_native != null) System.Runtime.InteropServices.NativeMemory.Free(_native);
            _native = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)size);
            _nativeSize = size;
            _nativeValid = false;
        }
        var dst = new byte*[4] { _native, null, null, null };
        var stride = new int[4] { Width * 4, 0, 0, 0 };
        ffmpeg.sws_scale(_sws, source->data, source->linesize, 0, h, dst, stride);
        _converted = true;
        _nativeValid = true;
        _convertedCount++;
    }

    public void Dispose()
    {
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_swFrame != null) { var f = _swFrame; ffmpeg.av_frame_free(&f); _swFrame = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
        if (_native != null) { System.Runtime.InteropServices.NativeMemory.Free(_native); _native = null; }
    }
}
