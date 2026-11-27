using Silk.NET.OpenAL;

namespace VideoBuilder.Media;

/// <summary>
/// OpenAL で音声を流す。別スレッドが fill コールバックでサンプルをもらってキューに積む。
/// 再生位置 (Position) は映像を合わせるための時計として使う。
/// <para>
/// OpenAL が float の形式 (AL_EXT_FLOAT32。OpenAL Soft にはある) に対応していれば、ミックスした float をそのまま渡す
/// (16 ビットの整数に変換しない。変換の処理がなくなり、小さな音の細かさも落ちない)。環境変数 VIDEO_BUILDER_AUDIO_FLOAT=0 で以前どおり 16 ビット。
/// </para>
/// </summary>
public sealed unsafe class AudioOutput : IDisposable
{
    private const int BufferFrames = 2048;
    private const int BufferCount = 4;
    /// <summary>AL_FORMAT_STEREO_FLOAT32 (AL_EXT_FLOAT32)。Silk.NET の BufferFormat にはないので値で指定する。</summary>
    private const BufferFormat StereoFloat32 = (BufferFormat)0x10011;

    private readonly int _rate;
    private ALContext? _alc;
    private AL? _al;
    private Device* _device;
    private Context* _context;
    private uint _source;
    private uint[] _buffers = Array.Empty<uint>();

    private Thread? _thread;
    private volatile bool _running;
    private long _startSample;
    private long _processedFrames;
    private long _queuedFrames;
    private readonly object _gate = new();

    public bool IsAvailable { get; }
    public string? Error { get; }
    /// <summary>float のまま OpenAL に渡しているか (false なら 16 ビットの整数に変換して渡す)。</summary>
    public bool UsesFloat { get; }

    public AudioOutput(int sampleRate)
    {
        _rate = sampleRate;
        try
        {
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);
            _device = _alc.OpenDevice("");
            if (_device == null) throw new InvalidOperationException("音声デバイスを開けません");
            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);
            _source = _al.GenSource();
            _buffers = _al.GenBuffers(BufferCount);
            UsesFloat = Environment.GetEnvironmentVariable("VIDEO_BUILDER_AUDIO_FLOAT") != "0" && _al.IsExtensionPresent("AL_EXT_FLOAT32");
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsAvailable = false;
        }
    }

    /// <summary>再生中のサンプル位置。</summary>
    public long Position
    {
        get
        {
            if (!IsAvailable || !_running) return _startSample;
            lock (_gate)
            {
                _al!.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int offset);
                return _startSample + _processedFrames + offset;
            }
        }
    }

    /// <param name="fill">(開始サンプル位置, インターリーブ 2ch バッファ) を受け取って埋める。</param>
    public void Start(long startSample, Action<long, float[]> fill)
    {
        Stop();
        if (!IsAvailable) return;
        _startSample = startSample; _processedFrames = 0; _queuedFrames = 0;
        _running = true;
        _thread = new Thread(() => Loop(fill)) { IsBackground = true, Name = "Audio" };
        _thread.Start();
    }

    private void Loop(Action<long, float[]> fill)
    {
        var al = _al!;
        var data = new float[BufferFrames * 2];
        var pcm = UsesFloat ? Array.Empty<short>() : new short[BufferFrames * 2];
        var free = new Queue<uint>(_buffers);
        // 1 つのバッファを鳴らし終える時間 (48kHz で約 43ms) の 1/4 ごとに確かめる。
        // バッファは 4 つ積んでおくので、少し遅れて確かめても音は途切れない (以前は 4ms ごとに起きていた)
        int idleMs = Math.Max(1, BufferFrames * 1000 / _rate / 4);
        while (_running)
        {
            lock (_gate)
            {
                al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
                while (processed-- > 0)
                {
                    uint b = 0;
                    al.SourceUnqueueBuffers(_source, 1, &b);
                    free.Enqueue(b);
                    _processedFrames += BufferFrames;
                }
            }
            while (free.Count > 0 && _running)
            {
                fill(_startSample + _queuedFrames, data);
                if (!UsesFloat)
                    for (int i = 0; i < data.Length; i++)
                        pcm[i] = (short)Math.Clamp(data[i] * 32767f, short.MinValue, short.MaxValue);
                uint b = free.Dequeue();
                lock (_gate)
                {
                    if (UsesFloat) fixed (float* p = data) al.BufferData(b, StereoFloat32, p, data.Length * sizeof(float), _rate);
                    else fixed (short* p = pcm) al.BufferData(b, BufferFormat.Stereo16, p, pcm.Length * sizeof(short), _rate);
                    al.SourceQueueBuffers(_source, 1, &b);
                    _queuedFrames += BufferFrames;
                    al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                    if (state != (int)SourceState.Playing) al.SourcePlay(_source);
                }
            }
            Thread.Sleep(idleMs);
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(500);
        _thread = null;
        if (!IsAvailable) return;
        lock (_gate)
        {
            var al = _al!;
            al.SourceStop(_source);
            al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            while (queued-- > 0) { uint b = 0; al.SourceUnqueueBuffers(_source, 1, &b); }
        }
    }

    public void Dispose()
    {
        Stop();
        if (!IsAvailable) return;
        try
        {
            _al!.DeleteSource(_source);
            _al.DeleteBuffers(_buffers);
            _alc!.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
        }
        catch { /* 終了時の失敗は無視 */ }
    }
}
