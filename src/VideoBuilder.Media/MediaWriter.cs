using FFmpeg.AutoGen;
using VideoBuilder.Core;

namespace VideoBuilder.Media;

public sealed class ExportSettings
{
    public string Path { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public Rational Fps { get; set; } = new(30, 1);
    public int SampleRate { get; set; } = 48000;
    /// <summary>FFmpeg のエンコーダ名。空なら映像なし。</summary>
    public string VideoCodec { get; set; } = "libx264";
    /// <summary>品質 (CRF)。小さいほど高画質。</summary>
    public int Crf { get; set; } = 20;
    public string Preset { get; set; } = "medium";
    /// <summary>FFmpeg のエンコーダ名。空なら音声なし。</summary>
    public string AudioCodec { get; set; } = "aac";
    public int AudioBitrate { get; set; } = 192_000;
}

/// <summary>BGRA のフレームと float の音声を受け取って、動画ファイルに書き出す。</summary>
public sealed unsafe class MediaWriter : IDisposable
{
    private AVFormatContext* _oc;
    private AVCodecContext* _venc, _aenc;
    private AVStream* _vst, _ast;
    private SwsContext* _sws;
    private SwrContext* _swr;
    private AVFrame* _vframe, _aframe;
    private AVPacket* _pkt;
    private long _videoPts, _audioPts;
    private readonly int _width, _height;
    private int _audioFrameSize;
    private float[] _audioQueue = new float[1 << 15];
    private int _audioQueued;   // フレーム数
    private bool _headerWritten, _finished;
    private AVBufferRef* _hwDevice;
    private bool _uploadToHardware;

    public bool HasVideo => _venc != null;
    public bool HasAudio => _aenc != null;

    public MediaWriter(ExportSettings s)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        _width = s.Width; _height = s.Height;
        AVFormatContext* oc = null;
        FFmpegLoader.Check(ffmpeg.avformat_alloc_output_context2(&oc, null, null, s.Path), "出力形式を判定できません");
        _oc = oc;
        try
        {
            bool globalHeader = (_oc->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;
            if (!string.IsNullOrEmpty(s.VideoCodec)) OpenVideo(s, globalHeader);
            if (!string.IsNullOrEmpty(s.AudioCodec)) OpenAudio(s, globalHeader);
            if (_venc == null && _aenc == null) throw new InvalidOperationException("映像も音声も出力対象になっていません");

            if ((_oc->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                FFmpegLoader.Check(ffmpeg.avio_open(&_oc->pb, s.Path, ffmpeg.AVIO_FLAG_WRITE), "出力ファイルを開けません");
            FFmpegLoader.Check(ffmpeg.avformat_write_header(_oc, null), "ヘッダの書き込みに失敗");
            _headerWritten = true;
            _pkt = ffmpeg.av_packet_alloc();
        }
        catch { Dispose(); throw; }
    }

    private void OpenVideo(ExportSettings s, bool globalHeader)
    {
        _vst = ffmpeg.avformat_new_stream(_oc, null);
        AVBufferRef* device;
        _venc = VideoEncoderSetup.Open(s, _width, _height, globalHeader, out device, out var softwareFormat);
        _hwDevice = device;
        _uploadToHardware = _venc->hw_frames_ctx != null;
        FFmpegLoader.Check(ffmpeg.avcodec_parameters_from_context(_vst->codecpar, _venc), "映像ストリームの設定に失敗");
        _vst->time_base = _venc->time_base;
        _vst->avg_frame_rate = _venc->framerate;

        // BGRA から変換した結果を入れるフレーム (ハードウェアへ送る場合も、まずここに作る)
        _vframe = ffmpeg.av_frame_alloc();
        _vframe->format = (int)softwareFormat;
        _vframe->width = _venc->width; _vframe->height = _venc->height;
        FFmpegLoader.Check(ffmpeg.av_frame_get_buffer(_vframe, 0), "フレームの確保に失敗");
        _sws = ffmpeg.sws_getContext(_width, _height, AVPixelFormat.AV_PIX_FMT_BGRA, _venc->width, _venc->height, softwareFormat,
            ffmpeg.SWS_BICUBIC, null, null, null);
        if (_sws == null) throw new InvalidOperationException("色変換の準備に失敗");
        // BT.709 で変換する (HD 以上の標準)
        if (_height >= 720)
        {
            var coef = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            var arr = new int_array4(); for (uint i = 0; i < 4; i++) arr[i] = coef[i];
            ffmpeg.sws_setColorspaceDetails(_sws, arr, 1, arr, 0, 0, 1 << 16, 1 << 16);
        }
    }

    private void OpenAudio(ExportSettings s, bool globalHeader)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(s.AudioCodec);
        if (codec == null) throw new InvalidOperationException($"エンコーダ {s.AudioCodec} が使えません");
        _ast = ffmpeg.avformat_new_stream(_oc, null);
        _aenc = ffmpeg.avcodec_alloc_context3(codec);
        _aenc->sample_fmt = codec->sample_fmts != null ? codec->sample_fmts[0] : AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _aenc->sample_rate = s.SampleRate;
        ffmpeg.av_channel_layout_default(&_aenc->ch_layout, 2);
        _aenc->bit_rate = s.AudioBitrate;
        _aenc->time_base = new AVRational { num = 1, den = s.SampleRate };
        if (globalHeader) _aenc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        FFmpegLoader.Check(ffmpeg.avcodec_open2(_aenc, codec, null), $"音声エンコーダ {s.AudioCodec} を開けません");
        FFmpegLoader.Check(ffmpeg.avcodec_parameters_from_context(_ast->codecpar, _aenc), "音声ストリームの設定に失敗");
        _ast->time_base = _aenc->time_base;
        _audioFrameSize = _aenc->frame_size > 0 ? _aenc->frame_size : 1024;

        _aframe = ffmpeg.av_frame_alloc();
        _aframe->format = (int)_aenc->sample_fmt;
        _aframe->sample_rate = _aenc->sample_rate;
        _aframe->nb_samples = _audioFrameSize;
        ffmpeg.av_channel_layout_copy(&_aframe->ch_layout, &_aenc->ch_layout);
        FFmpegLoader.Check(ffmpeg.av_frame_get_buffer(_aframe, 0), "音声フレームの確保に失敗");

        SwrContext* swr = null;
        FFmpegLoader.Check(ffmpeg.swr_alloc_set_opts2(&swr, &_aenc->ch_layout, _aenc->sample_fmt, _aenc->sample_rate,
            &_aenc->ch_layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, s.SampleRate, 0, null), "リサンプラの作成に失敗");
        _swr = swr;
        FFmpegLoader.Check(ffmpeg.swr_init(_swr), "リサンプラの初期化に失敗");
    }

    /// <summary>Width*Height*4 バイトの BGRA を 1 フレームとして書き込む。</summary>
    public void WriteVideoFrame(ReadOnlySpan<byte> bgra, int stride)
    {
        if (_venc == null) return;
        FFmpegLoader.Check(ffmpeg.av_frame_make_writable(_vframe), "フレームを書き込めません");
        fixed (byte* p = bgra)
        {
            var src = new byte*[4] { p, null, null, null };
            var strides = new int[4] { stride, 0, 0, 0 };
            ffmpeg.sws_scale(_sws, src, strides, 0, _height, _vframe->data, _vframe->linesize);
        }
        _vframe->pts = _videoPts++;
        if (!_uploadToHardware) { Encode(_venc, _vst, _vframe); return; }

        // VAAPI など: メモリ上のフレームを GPU 側のフレームへ送ってから、エンコーダに渡す
        var hw = ffmpeg.av_frame_alloc();
        try
        {
            FFmpegLoader.Check(ffmpeg.av_hwframe_get_buffer(_venc->hw_frames_ctx, hw, 0), "GPU 側のフレームを確保できません");
            FFmpegLoader.Check(ffmpeg.av_hwframe_transfer_data(hw, _vframe, 0), "GPU へフレームを送れません");
            hw->pts = _vframe->pts;
            Encode(_venc, _vst, hw);
        }
        finally { ffmpeg.av_frame_free(&hw); }
    }

    /// <summary>インターリーブのステレオ float を追記する。</summary>
    public void WriteAudio(ReadOnlySpan<float> samples)
    {
        if (_aenc == null) return;
        int frames = samples.Length / 2;
        int need = (_audioQueued + frames) * 2;
        if (_audioQueue.Length < need) Array.Resize(ref _audioQueue, Math.Max(need, _audioQueue.Length * 2));
        samples.CopyTo(_audioQueue.AsSpan(_audioQueued * 2));
        _audioQueued += frames;
        int offset = 0;
        while (_audioQueued - offset >= _audioFrameSize)
        {
            EncodeAudioChunk(offset, _audioFrameSize);
            offset += _audioFrameSize;
        }
        if (offset > 0)
        {
            Array.Copy(_audioQueue, offset * 2, _audioQueue, 0, (_audioQueued - offset) * 2);
            _audioQueued -= offset;
        }
    }

    private void EncodeAudioChunk(int offsetFrames, int frames)
    {
        FFmpegLoader.Check(ffmpeg.av_frame_make_writable(_aframe), "音声フレームを書き込めません");
        _aframe->nb_samples = frames;
        fixed (float* p = &_audioQueue[offsetFrames * 2])
        {
            byte* inPtr = (byte*)p;
            ffmpeg.swr_convert(_swr, _aframe->extended_data, frames, &inPtr, frames);
        }
        _aframe->pts = _audioPts;
        _audioPts += frames;
        Encode(_aenc, _ast, _aframe);
    }

    private void Encode(AVCodecContext* enc, AVStream* st, AVFrame* frame)
    {
        FFmpegLoader.Check(ffmpeg.avcodec_send_frame(enc, frame), "エンコードに失敗");
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_packet(enc, _pkt);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
            FFmpegLoader.Check(ret, "エンコードに失敗");
            ffmpeg.av_packet_rescale_ts(_pkt, enc->time_base, st->time_base);
            _pkt->stream_index = st->index;
            FFmpegLoader.Check(ffmpeg.av_interleaved_write_frame(_oc, _pkt), "書き込みに失敗");
        }
    }

    /// <summary>残りを書き出してファイルを閉じる。</summary>
    public void Finish()
    {
        if (_finished || !_headerWritten) return;
        _finished = true;
        if (_aenc != null)
        {
            if (_audioQueued > 0)
            {
                // 最後の端数。フレームサイズ固定のエンコーダには無音を足して渡す
                bool variable = (_aenc->codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) != 0;
                int frames = _audioQueued;
                if (!variable && _aenc->frame_size > 0)
                {
                    Array.Clear(_audioQueue, frames * 2, Math.Min(_audioQueue.Length, _audioFrameSize * 2) - frames * 2);
                    frames = _audioFrameSize;
                }
                EncodeAudioChunk(0, frames);
                _audioQueued = 0;
            }
            Encode(_aenc, _ast, null);
        }
        if (_venc != null) Encode(_venc, _vst, null);
        ffmpeg.av_write_trailer(_oc);
    }

    public void Dispose()
    {
        if (_vframe != null) { var f = _vframe; ffmpeg.av_frame_free(&f); _vframe = null; }
        if (_aframe != null) { var f = _aframe; ffmpeg.av_frame_free(&f); _aframe = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
        if (_venc != null) { var c = _venc; ffmpeg.avcodec_free_context(&c); _venc = null; }
        if (_hwDevice != null) { var d = _hwDevice; ffmpeg.av_buffer_unref(&d); _hwDevice = null; }
        if (_aenc != null) { var c = _aenc; ffmpeg.avcodec_free_context(&c); _aenc = null; }
        if (_oc != null)
        {
            if ((_oc->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && _oc->pb != null) ffmpeg.avio_closep(&_oc->pb);
            ffmpeg.avformat_free_context(_oc); _oc = null;
        }
    }

    /// <summary>この環境の FFmpeg で使えるエンコーダか。</summary>
    public static bool IsEncoderAvailable(string name) =>
        FFmpegLoader.EnsureLoaded() && ffmpeg.avcodec_find_encoder_by_name(name) != null;
}
