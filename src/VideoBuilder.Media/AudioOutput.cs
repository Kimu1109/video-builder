using Silk.NET.OpenAL;

namespace VideoBuilder.Media;

/// <summary>
/// OpenAL で音声を流す。別スレッドが fill コールバックでサンプルをもらってキューに積む。
/// 再生位置 (Position) は映像を合わせるための時計として使う。
/// </summary>
public sealed unsafe class AudioOutput : IDisposable
{
    private const int BufferFrames = 2048;
    private const int BufferCount = 4;

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
        var pcm = new short[BufferFrames * 2];
        var free = new Queue<uint>(_buffers);
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
                for (int i = 0; i < data.Length; i++)
                    pcm[i] = (short)Math.Clamp(data[i] * 32767f, short.MinValue, short.MaxValue);
                uint b = free.Dequeue();
                lock (_gate)
                {
                    fixed (short* p = pcm) al.BufferData(b, BufferFormat.Stereo16, p, pcm.Length * sizeof(short), _rate);
                    al.SourceQueueBuffers(_source, 1, &b);
                    _queuedFrames += BufferFrames;
                    al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                    if (state != (int)SourceState.Playing) al.SourcePlay(_source);
                }
            }
            Thread.Sleep(4);
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
