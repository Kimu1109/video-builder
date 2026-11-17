using System.Security.Cryptography;
using System.Text;
using FFmpeg.AutoGen;

namespace VideoBuilder.Media;

/// <summary>
/// プロキシ (プレビュー用に小さく作り直した動画) の置き場所と、できているかどうかの確認。
/// プロキシのファイル名は「元の動画のパス・大きさ・更新日時・縮小率」から決めるので、
/// 元の動画を書き換えると自動的に別物として扱われる (古いプロキシは使われない)。
/// </summary>
public static class ProxyStore
{
    public const string Extension = ".mkv";

    /// <summary>プロキシを置くフォルダ。アプリが起動時に設定する。</summary>
    public static string Directory { get; set; } = Path.Combine(Path.GetTempPath(), "video-builder-proxy");

    /// <summary>プレビューでプロキシを使うか。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>プロキシの縮小率の分母 (2 = 縦横 1/2, 4 = 1/4)。</summary>
    public static int Divisor { get; set; } = 2;

    /// <summary>プロキシができた・消えたときに呼ばれる (別スレッドから呼ばれることがある)。</summary>
    public static event Action? Changed;

    private static readonly Dictionary<(string, int), string?> Lookup = new();
    private static readonly object Gate = new();

    /// <summary>source の、今の縮小率でのプロキシのパス (まだ作られていなくても返す)。元の動画がなければ null。</summary>
    public static string? PathFor(string source) => PathFor(source, Divisor);

    public static string? PathFor(string source, int divisor)
    {
        var info = new FileInfo(source);
        if (!info.Exists) return null;
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{divisor}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(Directory, $"{Path.GetFileNameWithoutExtension(source)}_{hash}_{divisor}{Extension}");
    }

    /// <summary>使えるプロキシがあればそのパスを返す。結果は覚えておき、プロキシができた・消えたときに調べ直す。</summary>
    public static string? FindReady(string source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        int divisor = Divisor;
        lock (Gate)
        {
            if (Lookup.TryGetValue((source, divisor), out var known)) return known;
        }
        var path = PathFor(source, divisor);
        string? ready = path != null && File.Exists(path) ? path : null;
        lock (Gate) Lookup[(source, divisor)] = ready;
        return ready;
    }

    public static bool Exists(string source) => FindReady(source) != null;

    /// <summary>覚えている結果を捨てて、変化を知らせる。</summary>
    public static void NotifyChanged()
    {
        lock (Gate) Lookup.Clear();
        Changed?.Invoke();
    }

    /// <summary>source のプロキシを (どの縮小率のものも) 消す。消した数を返す。</summary>
    public static int Delete(string source)
    {
        int n = 0;
        foreach (var d in new[] { 2, 4 })
        {
            var path = PathFor(source, d);
            if (path == null || !File.Exists(path)) continue;
            try { File.Delete(path); n++; } catch { /* 使用中などで消せなければ残す */ }
        }
        NotifyChanged();
        return n;
    }

    /// <summary>プロキシのフォルダにあるファイルの合計の大きさ (バイト)。</summary>
    public static long TotalSize()
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;
        return new DirectoryInfo(Directory).EnumerateFiles("*" + Extension).Sum(f => f.Length);
    }

    /// <summary>すべてのプロキシを消す。</summary>
    public static void Clear()
    {
        if (System.IO.Directory.Exists(Directory))
            foreach (var f in new DirectoryInfo(Directory).EnumerateFiles("*" + Extension))
                try { f.Delete(); } catch { /* 使用中なら残す */ }
        NotifyChanged();
    }

    /// <summary>重い動画 (プロキシを作ると効果が大きいもの) か。自動でプロキシを作るかどうかの判断に使う。</summary>
    public static bool IsHeavy(MediaInfo info) =>
        info.HasVideo && ((long)info.Width * info.Height > 1920L * 1080 || info.VideoCodec is "hevc" or "av1" or "prores" or "vp9" or "dnxhd");
}

/// <summary>
/// プロキシを作る。動画を頭から順にデコードし、縮小して MJPEG (すべてのフレームが単独で読める形式) で書き出す。
/// どの位置へもすぐ移動できるので、プレビューでのシークが速い。音声は入れない (音声は元のファイルから読む)。
/// </summary>
public static class ProxyGenerator
{
    /// <summary>
    /// source のプロキシを destination に作る。途中のファイルは destination + ".part" に書き、完成したら名前を変える。
    /// 中止・失敗したときは途中のファイルを消して例外を投げる。
    /// </summary>
    /// <param name="progress">進み具合 (0〜1)。</param>
    public static void Create(string source, string destination, int divisor, IProgress<double>? progress, CancellationToken cancel)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        string part = destination + ".part";
        bool ok = false;
        using (var job = new Job(progress, cancel))
        {
            try
            {
                job.Run(source, part, Math.Clamp(divisor, 1, 16));
                ok = true;
            }
            finally
            {
                job.Dispose();     // ファイルを閉じてから、名前を変える・消す
                if (ok) File.Move(part, destination, overwrite: true);
                else try { File.Delete(part); } catch { /* 消せなくても続行 */ }
            }
        }
        progress?.Report(1);
        ProxyStore.NotifyChanged();
    }

    private sealed unsafe class Job : IDisposable
    {
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _cancel;
        private AVFormatContext* _ic, _oc;
        private AVCodecContext* _dec, _enc;
        private AVStream* _ist, _ost;
        private SwsContext* _sws;
        private AVFrame* _frame, _scaled;
        private AVPacket* _pkt, _outPkt;
        private long _startPts, _lastPts = long.MinValue;
        private double _duration, _lastReport = -1;
        private int _w, _h;

        public Job(IProgress<double>? progress, CancellationToken cancel)
        {
            _progress = progress; _cancel = cancel;
        }

        public void Run(string source, string part, int divisor)
        {
            AVFormatContext* ic = null;
            FFmpegLoader.Check(ffmpeg.avformat_open_input(&ic, source, null, null), "ファイルを開けません");
            _ic = ic;
            FFmpegLoader.Check(ffmpeg.avformat_find_stream_info(_ic, null), "ストリーム情報を取得できません");
            AVCodec* decoder = null;
            int stream = ffmpeg.av_find_best_stream(_ic, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            FFmpegLoader.Check(stream, "映像ストリームがありません");
            _ist = _ic->streams[stream];
            _dec = ffmpeg.avcodec_alloc_context3(decoder);
            FFmpegLoader.Check(ffmpeg.avcodec_parameters_to_context(_dec, _ist->codecpar), "デコーダの設定に失敗");
            _dec->thread_count = Math.Min(Environment.ProcessorCount, 8);
            FFmpegLoader.Check(ffmpeg.avcodec_open2(_dec, decoder, null), "デコーダを開けません");
            _startPts = _ist->start_time != ffmpeg.AV_NOPTS_VALUE ? _ist->start_time : 0;
            _duration = _ist->duration != ffmpeg.AV_NOPTS_VALUE && _ist->duration > 0
                ? _ist->duration * ffmpeg.av_q2d(_ist->time_base)
                : _ic->duration > 0 ? _ic->duration / (double)ffmpeg.AV_TIME_BASE : 0;

            // 縮小後の大きさ (偶数にそろえる)
            _w = Math.Max(2, _dec->width / divisor) & ~1;
            _h = Math.Max(2, _dec->height / divisor) & ~1;

            AVFormatContext* oc = null;
            FFmpegLoader.Check(ffmpeg.avformat_alloc_output_context2(&oc, null, "matroska", part), "出力形式を用意できません");
            _oc = oc;
            var encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
            if (encoder == null) throw new InvalidOperationException("MJPEG のエンコーダがありません");
            _ost = ffmpeg.avformat_new_stream(_oc, null);
            _enc = ffmpeg.avcodec_alloc_context3(encoder);
            _enc->width = _w; _enc->height = _h;
            _enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            // 元の動画と同じ時刻の目盛りを使う (フレームの時刻がずれないように)
            _enc->time_base = _ist->time_base;
            var rate = _ist->avg_frame_rate.num != 0 ? _ist->avg_frame_rate : _ist->r_frame_rate;
            _enc->framerate = rate;
            _enc->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            _enc->global_quality = 4 * ffmpeg.FF_QP2LAMBDA;    // 画質 (2 = 高画質 〜 31 = 低画質)
            _enc->thread_count = Math.Min(Environment.ProcessorCount, 8);
            if ((_oc->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0) _enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            FFmpegLoader.Check(ffmpeg.avcodec_open2(_enc, encoder, null), "MJPEG のエンコーダを開けません");
            FFmpegLoader.Check(ffmpeg.avcodec_parameters_from_context(_ost->codecpar, _enc), "出力ストリームの設定に失敗");
            _ost->time_base = _enc->time_base;
            _ost->avg_frame_rate = rate;
            FFmpegLoader.Check(ffmpeg.avio_open(&_oc->pb, part, ffmpeg.AVIO_FLAG_WRITE), "出力ファイルを開けません");
            FFmpegLoader.Check(ffmpeg.avformat_write_header(_oc, null), "ヘッダの書き込みに失敗");

            _frame = ffmpeg.av_frame_alloc();
            _scaled = ffmpeg.av_frame_alloc();
            _scaled->format = (int)_enc->pix_fmt; _scaled->width = _w; _scaled->height = _h;
            FFmpegLoader.Check(ffmpeg.av_frame_get_buffer(_scaled, 0), "フレームの確保に失敗");
            _pkt = ffmpeg.av_packet_alloc();
            _outPkt = ffmpeg.av_packet_alloc();

            while (ffmpeg.av_read_frame(_ic, _pkt) >= 0)
            {
                _cancel.ThrowIfCancellationRequested();
                if (_pkt->stream_index == stream && ffmpeg.avcodec_send_packet(_dec, _pkt) >= 0) Drain();
                ffmpeg.av_packet_unref(_pkt);
            }
            ffmpeg.avcodec_send_packet(_dec, null);
            Drain();
            Write(null);
            FFmpegLoader.Check(ffmpeg.av_write_trailer(_oc), "書き込みの終了に失敗");
        }

        private void Drain()
        {
            while (true)
            {
                int r = ffmpeg.avcodec_receive_frame(_dec, _frame);
                if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF) return;
                FFmpegLoader.Check(r, "デコードに失敗");
                _cancel.ThrowIfCancellationRequested();
                long pts = _frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _lastPts == long.MinValue ? _startPts : _lastPts + 1;
                // 時刻が戻るフレーム (壊れた素材など) は書けないので飛ばす
                if (pts <= _lastPts) { ffmpeg.av_frame_unref(_frame); continue; }
                _lastPts = pts;
                _sws = ffmpeg.sws_getCachedContext(_sws, _frame->width, _frame->height, (AVPixelFormat)_frame->format, _w, _h,
                    _enc->pix_fmt, ffmpeg.SWS_BILINEAR, null, null, null);
                if (_sws == null) throw new InvalidOperationException("縮小の準備に失敗");
                FFmpegLoader.Check(ffmpeg.av_frame_make_writable(_scaled), "フレームを書き込めません");
                ffmpeg.sws_scale(_sws, _frame->data, _frame->linesize, 0, _frame->height, _scaled->data, _scaled->linesize);
                // 元の動画の最初の時刻を 0 にそろえる (VideoReader も最初の時刻を引いて扱う)
                _scaled->pts = pts - _startPts;
                Write(_scaled);
                ffmpeg.av_frame_unref(_frame);
                if (_progress != null && _duration > 0)
                {
                    double p = Math.Clamp((pts - _startPts) * ffmpeg.av_q2d(_ist->time_base) / _duration, 0, 1);
                    if (p - _lastReport >= 0.01) { _lastReport = p; _progress.Report(p); }
                }
            }
        }

        private void Write(AVFrame* f)
        {
            FFmpegLoader.Check(ffmpeg.avcodec_send_frame(_enc, f), "エンコードに失敗");
            while (true)
            {
                int r = ffmpeg.avcodec_receive_packet(_enc, _outPkt);
                if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF) break;
                FFmpegLoader.Check(r, "エンコードに失敗");
                ffmpeg.av_packet_rescale_ts(_outPkt, _enc->time_base, _ost->time_base);
                _outPkt->stream_index = _ost->index;
                FFmpegLoader.Check(ffmpeg.av_interleaved_write_frame(_oc, _outPkt), "書き込みに失敗");
            }
        }

        public void Dispose()
        {
            if (_oc != null)
            {
                if (_oc->pb != null) ffmpeg.avio_closep(&_oc->pb);
                ffmpeg.avformat_free_context(_oc);
                _oc = null;
            }
            if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_scaled != null) { var f = _scaled; ffmpeg.av_frame_free(&f); _scaled = null; }
            if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
            if (_outPkt != null) { var p = _outPkt; ffmpeg.av_packet_free(&p); _outPkt = null; }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_dec != null) { var c = _dec; ffmpeg.avcodec_free_context(&c); _dec = null; }
            if (_enc != null) { var c = _enc; ffmpeg.avcodec_free_context(&c); _enc = null; }
            if (_ic != null) { var c = _ic; ffmpeg.avformat_close_input(&c); _ic = null; }
        }
    }
}
