using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>YUV のフレームの面の並び。</summary>
public enum YuvLayout
{
    /// <summary>Y と U と V が別々の面 (YUV420P・YUV422P・YUV444P)。</summary>
    Planar,
    /// <summary>Y の面と、U と V が交互に並んだ面 (NV12)。</summary>
    Nv12,
}

/// <summary>
/// デコードしたままの YUV (8 ビット) のフレーム。GPU 描画で、BGRA に変換せずに GPU へ送って、GPU で色を変換するのに使う。
/// 色の変換は、BGRA に変換するとき (sws の既定) と同じく BT.601。FullRange は J 形式 (yuvj420p など) のとき。
/// </summary>
public readonly record struct YuvPlanes(
    int Width, int Height, YuvLayout Layout, int ChromaShiftX, int ChromaShiftY, bool FullRange,
    IntPtr Y, int YStride, IntPtr U, int UStride, IntPtr V, int VStride)
{
    public int ChromaWidth => (Width + (1 << ChromaShiftX) - 1) >> ChromaShiftX;
    public int ChromaHeight => (Height + (1 << ChromaShiftY) - 1) >> ChromaShiftY;
}

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
    /// <summary>シークできない形式 (アニメーション WebP) なので、前へ戻るときは開き直す。</summary>
    private bool _reopenToSeek;
    // 変換済みフレーム (BGRA) の置き場を 2 つ持ち、変換のたびに交互に使う。
    // 直前に返したフレームは、もう 1 回変換するまで書き換わらない (先読みしている間も前のフレームを描ける)。
    // アンマネージドなので GC で動かない。大きさは元の動画の大きさで確保し、縮めて変換するときもそのまま使う
    private readonly byte*[] _native = new byte*[2];
    private int _nativeCapacity;
    private int _nativeIndex;
    private bool _nativeValid;
    private int _divisor = 1;
    private int _pinned = -1;       // 書き換えない置き場 (PinCurrentFrame)。-1 ならなし
    private byte[] _managed = Array.Empty<byte>();
    // PreferYuv のとき: 置き場ごとに、BGRA に変換するかわりに持っている YUV のフレーム (_slotYuv が true の置き場だけ)
    private readonly AVFrame*[] _yuv = new AVFrame*[2];
    private readonly bool[] _slotYuv = new bool[2];
    private SwsContext* _swsYuv;      // YUV のまま縮める
    private SwsContext* _swsLate;     // 持っている YUV を、あとから BGRA にする (先読みと同時に使うので _sws とは別)

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    public double Duration { get; }
    /// <summary>デコードに使っているもの ("VAAPI" や "ソフトウェア")。</summary>
    public string DecoderName { get; private set; } = "ソフトウェア";

    /// <summary>
    /// 透明な部分がある (アニメーション WebP・GIF)。このときフレームは、アルファを掛けた BGRA (Skia の Premul) になる。
    /// ほかの動画は不透明として扱う (アルファがあっても使わない)。
    /// </summary>
    public bool HasAlpha { get; private set; }
    /// <summary>ハードウェアでデコードしているか。途中で失敗してソフトウェアに戻ったら false になる。</summary>
    public bool IsHardware => _hardware;
    /// <summary>ハードウェアデコードを使わなかった (または途中でやめた) 理由。</summary>
    public string? HardwareError { get; private set; }

    /// <summary>
    /// 取り出すフレームを縦横 1/n に縮める (1 = 元の大きさ)。プレビューで、表示より大きくデコードしないために使う。
    /// 変えると、今のフレームも次に取り出すときに新しい大きさで変換し直す。
    /// </summary>
    public int OutputDivisor
    {
        get => _divisor;
        set
        {
            value = Math.Clamp(value, 1, 16);
            if (value == _divisor) return;
            _divisor = value;
            // デコーダにフレームが残っていれば、新しい大きさで変換し直す (末尾を過ぎた後は残っていないので、前の大きさのまま)
            if (_hasFrame && !_eof) _converted = false;
        }
    }

    /// <summary>
    /// 対応する形式 (8 ビットの YUV420P・YUV422P・YUV444P・NV12。透明な部分のある動画は除く) なら、
    /// BGRA に変換せず、デコードしたままの YUV を持つ。GPU 描画で、GPU で色を変換するときに使う。
    /// このとき GetFramePointer が返すポインタは目印で、中身は BGRA ではない。TryGetYuv で面を受け取るか、EnsureBgra で BGRA にすること。
    /// </summary>
    public bool PreferYuv { get; set; }

    /// <summary>OutputDivisor を n にしたときの、取り出すフレームの大きさ。</summary>
    public (int Width, int Height) OutputSizeFor(int divisor)
    {
        divisor = Math.Clamp(divisor, 1, 16);
        return (Math.Max(1, (Width + divisor - 1) / divisor), Math.Max(1, (Height + divisor - 1) / divisor));
    }

    /// <summary>
    /// 今のフレームの置き場を、UnpinFrame するまで書き換えないようにする。
    /// 返したフレームを別のスレッドで使っている間に、このデコーダで先を読ませるときに使う。
    /// </summary>
    public void PinCurrentFrame() => _pinned = _nativeValid ? _nativeIndex : -1;

    public void UnpinFrame() => _pinned = -1;

    /// <summary>最後に取り出したフレームの大きさ (OutputDivisor で縮めた大きさ)。1 行は FrameWidth*4 バイト。</summary>
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }

    /// <summary>最後に GetFrame が返したフレームの時刻 (秒)。同じフレームかどうかの判定に使う。</summary>
    public long CurrentFrameId => _curPts;

    public VideoReader(string path) : this(path, false) { }

    /// <param name="absoluteTime">
    /// 時刻をファイルの時刻そのまま (最初のフレームの時刻を 0 にしない) で扱う。
    /// 元の動画の途中から始まるプロキシの区切り (元の動画と同じ時刻で書いてある) を読むときに使う。
    /// </param>
    public VideoReader(string path, bool absoluteTime)
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
            if (!HardwareDecoding.Wanted || !OpenHardwareDecoder(st)) OpenSoftwareDecoder(st);

            Width = _dec->width; Height = _dec->height;
            FrameWidth = Width; FrameHeight = Height;
            _tb = st->time_base;
            _startPts = !absoluteTime && st->start_time != ffmpeg.AV_NOPTS_VALUE ? st->start_time : 0;
            var r = st->avg_frame_rate.num != 0 ? st->avg_frame_rate : st->r_frame_rate;
            FrameRate = r.den != 0 && r.num != 0 ? (double)r.num / r.den : 30;
            _defaultDur = Math.Max(1, (long)Math.Round(1.0 / FrameRate / ffmpeg.av_q2d(_tb)));
            Duration = st->duration != ffmpeg.AV_NOPTS_VALUE && st->duration > 0
                ? st->duration * ffmpeg.av_q2d(_tb)
                : _fmt->duration > 0 ? _fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            HasAlpha = AnimatedImageMedia.IsAnimatedImage(st);
            if (AnimatedImageMedia.NeedsScan(st))
            {
                _reopenToSeek = true;
                if (Duration <= 0) Duration = AnimatedImageMedia.ScanDuration(path, _stream);
            }
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
        var ctx = HardwareDecoding.Open(_codec, st, out var format, out var name, out var error);
        if (ctx == null) { HardwareError = error; return false; }
        _dec = ctx;
        _hardware = true;
        _hwFormat = format;
        DecoderName = name;
        return true;
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
    /// seconds の位置のフレームを返す (FrameWidth*FrameHeight*4 バイトの BGRA)。
    /// 返す配列は内部バッファなので、次の呼び出しまでに使い切ること。
    /// </summary>
    public byte[]? GetFrame(double seconds)
    {
        var p = EnsureBgra(GetFramePointer(seconds));
        if (p == IntPtr.Zero) return null;
        int size = FrameWidth * FrameHeight * 4;
        if (_managed.Length != size) _managed = new byte[size];
        System.Runtime.InteropServices.Marshal.Copy(p, _managed, 0, size);
        return _managed;
    }

    /// <summary>
    /// seconds の位置のフレーム (BGRA, FrameWidth x FrameHeight, 1 行 FrameWidth*4 バイト) を指すポインタを返す。複製しないので速い。
    /// 中身は、このあと別のフレームを 2 回取り出すと書き換わる (1 回なら前のフレームはそのまま残る)。取り出せなければ IntPtr.Zero。
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
        return _nativeValid ? (IntPtr)_native[_nativeIndex] : IntPtr.Zero;
    }

    /// <summary>シークした回数 (計測・テスト用)。</summary>
    public int SeekCount { get; private set; }

    private void Seek(long target)
    {
        SeekCount++;
        // シークできない形式 (アニメーション WebP) は、開き直して先頭から読む
        if (_reopenToSeek) Reopen();
        else ffmpeg.av_seek_frame(_fmt, _stream, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_dec);
        _hasFrame = false; _draining = false; _eof = false;
    }

    /// <summary>ファイルを開き直して、読む位置を先頭に戻す (デコーダはそのまま使う)。</summary>
    private void Reopen()
    {
        var old = _fmt;
        ffmpeg.avformat_close_input(&old);
        _fmt = null;
        AVFormatContext* fmt = null;
        FFmpegLoader.Check(ffmpeg.avformat_open_input(&fmt, Path, null, null), "ファイルを開けません");
        _fmt = fmt;
        FFmpegLoader.Check(ffmpeg.avformat_find_stream_info(_fmt, null), "ストリーム情報を取得できません");
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
        var (ow, oh) = OutputSizeFor(_divisor);
        // 置き場は元の大きさで確保する (縮める大きさが変わっても確保し直さない)
        int capacity = Width * Height * 4;
        if (_native[0] == null || _nativeCapacity != capacity)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_native[i] != null) System.Runtime.InteropServices.NativeMemory.Free(_native[i]);
                _native[i] = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)capacity);
            }
            _nativeCapacity = capacity;
            _nativeValid = false;
        }
        // 直前に返したフレームを残すため、もう一方の置き場に変換する
        // (押さえられた置き場があれば、何回変換してもそちらには書かない)
        int index = _pinned >= 0 ? _pinned ^ 1 : _nativeValid ? _nativeIndex ^ 1 : _nativeIndex;
        if (PreferYuv && !HasAlpha && StoreYuv(index, source, ow, oh))
        {
            _nativeIndex = index;
            FrameWidth = ow; FrameHeight = oh;
            _converted = true;
            _nativeValid = true;
            _convertedCount++;
            return;
        }
        ReleaseYuv(index);
        _sws = ffmpeg.sws_getCachedContext(_sws, w, h, (AVPixelFormat)source->format, ow, oh,
            AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws == null) return;
        var dst = new byte*[4] { _native[index], null, null, null };
        var stride = new int[4] { ow * 4, 0, 0, 0 };
        ffmpeg.sws_scale(_sws, source->data, source->linesize, 0, h, dst, stride);
        if (HasAlpha) AnimatedImageMedia.Premultiply(_native[index], ow * oh);
        _nativeIndex = index;
        FrameWidth = ow; FrameHeight = oh;
        _converted = true;
        _nativeValid = true;
        _convertedCount++;
    }

    /// <summary>形式が YUV として渡せるものなら、その並び (U・V の間引き方と、フルレンジか)。</summary>
    internal static (YuvLayout Layout, int ShiftX, int ShiftY, bool Full)? YuvFormatOf(AVPixelFormat f) => f switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV420P => (YuvLayout.Planar, 1, 1, false),
        AVPixelFormat.AV_PIX_FMT_YUVJ420P => (YuvLayout.Planar, 1, 1, true),
        AVPixelFormat.AV_PIX_FMT_YUV422P => (YuvLayout.Planar, 1, 0, false),
        AVPixelFormat.AV_PIX_FMT_YUVJ422P => (YuvLayout.Planar, 1, 0, true),
        AVPixelFormat.AV_PIX_FMT_YUV444P => (YuvLayout.Planar, 0, 0, false),
        AVPixelFormat.AV_PIX_FMT_YUVJ444P => (YuvLayout.Planar, 0, 0, true),
        AVPixelFormat.AV_PIX_FMT_NV12 => (YuvLayout.Nv12, 1, 1, false),
        _ => null,
    };

    /// <summary>
    /// source を、置き場 index の YUV のフレームとして持つ。元の大きさならデコーダのフレームを参照するだけ (複製しない)。
    /// 縮めるときは、YUV のまま縮める。持てない形式なら false (BGRA に変換する)。
    /// </summary>
    private bool StoreYuv(int index, AVFrame* source, int ow, int oh)
    {
        var format = (AVPixelFormat)source->format;
        if (YuvFormatOf(format) is not { } layout) return false;
        // U・V を間引いた形式で幅・高さが奇数だと、sws は U・V を補間する別の変換を使うので、GPU の変換と絵が合わない。
        // そのときはこれまでどおり BGRA にする (このような動画はほとんどない)
        if ((layout.ShiftX > 0 && (ow & 1) != 0) || (layout.ShiftY > 0 && (oh & 1) != 0)) return false;
        var dst = _yuv[index];
        if (dst == null) _yuv[index] = dst = ffmpeg.av_frame_alloc();
        ffmpeg.av_frame_unref(dst);
        _slotYuv[index] = false;
        int w = source->width, h = source->height;
        if (ow == w && oh == h && DecoderName != "V4L2")
        {
            // V4L2 のデコーダは受け取り用の置き場が少ないので、参照を持ち続けずに複製する (下)
            if (ffmpeg.av_frame_ref(dst, source) < 0) return false;
        }
        else
        {
            dst->format = (int)format; dst->width = ow; dst->height = oh;
            if (ffmpeg.av_frame_get_buffer(dst, 0) < 0) { ffmpeg.av_frame_unref(dst); return false; }
            if (ow == w && oh == h)
            {
                if (ffmpeg.av_frame_copy(dst, source) < 0) { ffmpeg.av_frame_unref(dst); return false; }
            }
            else
            {
                _swsYuv = ffmpeg.sws_getCachedContext(_swsYuv, w, h, format, ow, oh, format, (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (_swsYuv == null) { ffmpeg.av_frame_unref(dst); return false; }
                ffmpeg.sws_scale(_swsYuv, source->data, source->linesize, 0, h, dst->data, dst->linesize);
            }
        }
        _slotYuv[index] = true;
        return true;
    }

    private void ReleaseYuv(int index)
    {
        _slotYuv[index] = false;
        if (_yuv[index] != null) ffmpeg.av_frame_unref(_yuv[index]);
    }

    /// <summary>GetFramePointer が返したポインタ (目印) の置き場の番号。なければ -1。</summary>
    private int SlotOf(IntPtr frame)
    {
        if (frame == IntPtr.Zero) return -1;
        for (int i = 0; i < 2; i++)
            if ((IntPtr)_native[i] == frame) return i;
        return -1;
    }

    /// <summary>
    /// GetFramePointer が返したフレームが YUV のまま持っているものなら、その面を返す。
    /// 面の中身は、そのフレームの置き場が次に使われるまで (GetFramePointer の中身と同じ) 正しい。
    /// </summary>
    public bool TryGetYuv(IntPtr frame, out YuvPlanes planes)
    {
        planes = default;
        int i = SlotOf(frame);
        if (i < 0 || !_slotYuv[i]) return false;
        var f = _yuv[i];
        var layout = YuvFormatOf((AVPixelFormat)f->format)!.Value;
        planes = new YuvPlanes(f->width, f->height, layout.Layout, layout.ShiftX, layout.ShiftY, layout.Full,
            (IntPtr)f->data[0], f->linesize[0], (IntPtr)f->data[1], f->linesize[1],
            layout.Layout == YuvLayout.Nv12 ? IntPtr.Zero : (IntPtr)f->data[2], layout.Layout == YuvLayout.Nv12 ? 0 : f->linesize[2]);
        return true;
    }

    /// <summary>
    /// GetFramePointer が返したフレームを、BGRA (FrameWidth*4 バイトの行) で読めるようにして、同じポインタを返す。
    /// YUV のまま持っているフレームなら、ここで BGRA に変換する (GPU へ送れなかったときなど)。先読み中に呼んでもよい。
    /// </summary>
    public IntPtr EnsureBgra(IntPtr frame)
    {
        int i = SlotOf(frame);
        if (i < 0 || !_slotYuv[i]) return frame;
        var f = _yuv[i];
        _swsLate = ffmpeg.sws_getCachedContext(_swsLate, f->width, f->height, (AVPixelFormat)f->format, f->width, f->height,
            AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_swsLate == null) return IntPtr.Zero;
        var dst = new byte*[4] { _native[i], null, null, null };
        var stride = new int[4] { f->width * 4, 0, 0, 0 };
        ffmpeg.sws_scale(_swsLate, f->data, f->linesize, 0, f->height, dst, stride);
        return frame;
    }

    public void Dispose()
    {
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        for (int i = 0; i < 2; i++)
            if (_yuv[i] != null) { var f = _yuv[i]; ffmpeg.av_frame_free(&f); _yuv[i] = null; }
        if (_swsYuv != null) { ffmpeg.sws_freeContext(_swsYuv); _swsYuv = null; }
        if (_swsLate != null) { ffmpeg.sws_freeContext(_swsLate); _swsLate = null; }
        if (_swFrame != null) { var f = _swFrame; ffmpeg.av_frame_free(&f); _swFrame = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
        for (int i = 0; i < 2; i++)
            if (_native[i] != null) { System.Runtime.InteropServices.NativeMemory.Free(_native[i]); _native[i] = null; }
    }
}
