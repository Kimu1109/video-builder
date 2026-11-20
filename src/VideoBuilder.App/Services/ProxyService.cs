using Avalonia.Threading;
using VideoBuilder.Core;
using VideoBuilder.Media;

namespace VideoBuilder.App.Services;

/// <summary>
/// プロキシを裏で 1 本ずつ作る。進み具合は Status で見られる (ツールバーに表示する)。
/// プロキシは区切りごとにでき、できた区切りからプレビューで使われる。今見ている位置 (UpdateFocus) の区切りを先に作る。
/// </summary>
public sealed class ProxyService
{
    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _queued = new();
    private CancellationTokenSource? _cancel;
    private Task? _worker;

    /// <summary>今作っているファイル (なければ null)。</summary>
    public string? Current { get; private set; }
    public double Progress { get; private set; }
    public int Pending => _queue.Count + (Current != null ? 1 : 0);
    /// <summary>直近の失敗 (ファイル名と理由)。</summary>
    public string? LastError { get; private set; }

    /// <summary>状態が変わった (UI スレッドで呼ばれる)。</summary>
    public event Action? Changed;

    /// <summary>作る動画が変わったので、今見ている位置を知らせ直してほしい (UI スレッドで呼ばれる)。</summary>
    public event Action? FocusChanged;

    public string Status => Current == null ? ""
        : $"プロキシ作成中 {L.Name(Path.GetFileName(Current))} {Progress * 100:0}%" + (_queue.Count > 0 ? $" (残り {_queue.Count})" : "");

    /// <summary>Status のツールチップ: できた所から使えることを知らせる。</summary>
    public string StatusDetail => Current == null ? "" : "できた所からプレビューで使われます。今見ている位置を先に作ります";

    public bool IsQueued(string path) => _queued.Contains(path);

    // 今見ている位置: 作っている動画の、現在位置での再生位置 (秒)。作る側のスレッドから読む
    private double _focusSeconds = double.NaN;

    /// <summary>
    /// 今見ている位置を知らせる (UI スレッドから、現在位置やプロジェクトが変わるたびに呼ぶ)。
    /// 作っている動画が現在位置に置いてあれば、その再生位置の区切りを先に作る。
    /// 順番待ちの動画のうち、現在位置に置いてあるものは先頭に回す。
    /// </summary>
    public void UpdateFocus(Project project, Scene scene, int frame)
    {
        double? Focus(string file)
        {
            TimelineObject? best = null;
            int bestDistance = int.MaxValue;
            foreach (var o in scene.Objects)
            {
                if (o.Effects.Count == 0 || o.Main.DefinitionId != EffectRegistry.Video || o.Main.GetString("file") != file) continue;
                int d = o.Contains(frame) ? 0 : Math.Min(Math.Abs(o.Start - frame), Math.Abs(o.End - frame));
                if (d < bestDistance) { best = o; bestDistance = d; }
            }
            if (best == null) return null;
            int f = Math.Clamp(frame, best.Start, best.End);
            return VideoBuilder.Rendering.RenderEngine.SourceTime(project, best, best.Bounds, f);
        }
        Volatile.Write(ref _focusSeconds, Current != null && Focus(Current) is { } t ? t : double.NaN);
        // 順番待ちの中に、現在位置に置いてある動画があれば先に作る
        if (_queue.Count > 1)
        {
            var here = _queue.FirstOrDefault(path => scene.ObjectsAt(frame).Any(o => o.Effects.Count > 0 && o.Main.GetString("file") == path));
            if (here != null && _queue.Peek() != here)
            {
                var rest = _queue.Where(p => p != here).ToList();
                _queue.Clear();
                _queue.Enqueue(here);
                foreach (var p in rest) _queue.Enqueue(p);
            }
        }
    }

    /// <summary>プロキシを作る順番待ちに入れる。すでにあるもの・順番待ちのものは入れない。</summary>
    public void Enqueue(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || _queued.Contains(path) || ProxyStore.Exists(path)) return;
        _queued.Add(path);
        _queue.Enqueue(path);
        Changed?.Invoke();
        if (_worker == null || _worker.IsCompleted) _worker = RunAsync();
    }

    /// <summary>
    /// プロジェクトの動画のうち、プロキシがないものをすべて順番待ちに入れる。
    /// heavyOnly なら、プロキシが要るもの (ProxyStore.NeedsProxy) だけを、裏で調べてから入れる。戻り値は今すぐ入れた数。
    /// </summary>
    public int EnqueueProject(Project project, bool heavyOnly)
    {
        int n = 0;
        foreach (var path in ProjectMedia.UsedFiles(project).Where(f => ObjectFactory.DefinitionForFile(f) == EffectRegistry.Video))
        {
            if (ProxyStore.Exists(path) || _queued.Contains(path)) continue;
            if (heavyOnly) { EnqueueIfNeeded(path); continue; }
            Enqueue(path);
            n++;
        }
        return n;
    }

    // 自動で作るかを調べている動画 (同じものを何度も調べない)
    private readonly HashSet<string> _checking = new();

    // 調べる順番待ち。1 本ずつ、裏のスレッドで調べる
    private readonly Queue<(string Path, MediaInfo? Info)> _checkQueue = new();
    private readonly object _checkGate = new();
    private bool _checkerRunning;

    /// <summary>
    /// プロキシが要る動画なら順番待ちに入れる (自動作成)。UI スレッドから呼ぶ。すぐに戻る。
    /// 裏のスレッドで 1 本ずつ、ファイルを調べ (info がなければ)、4K・H.265 などはそのまま入れ、そうでなければ
    /// この PC でのデコードの速さとキーフレームの間隔を測ってから決める (ProxyStore.NeedsProxy)。
    /// 1 本ずつにするのは、同時に測ると互いに CPU を取り合って遅く測れ、要らないプロキシまで作ってしまうため
    /// (動画 19 本を同時に測ると、デコードの速さが 1 本だけのときの 4 分の 1 ほどに見えた)。
    /// </summary>
    public void EnqueueIfNeeded(string path, MediaInfo? info = null)
    {
        if (string.IsNullOrEmpty(path) || _queued.Contains(path) || _checking.Contains(path) || ProxyStore.Exists(path)) return;
        if (info != null && !info.HasVideo) return;
        _checking.Add(path);
        lock (_checkGate)
        {
            _checkQueue.Enqueue((path, info));
            if (_checkerRunning) return;
            _checkerRunning = true;
        }
        _ = Task.Run(CheckLoop);
    }

    private void CheckLoop()
    {
        while (true)
        {
            (string Path, MediaInfo? Info) item;
            lock (_checkGate)
            {
                if (!_checkQueue.TryDequeue(out item)) { _checkerRunning = false; return; }
            }
            var (path, info) = item;
            DecodeCheck? check = null;
            bool needed = false;
            try
            {
                info ??= MediaInfo.Probe(path);
                if (info is { HasVideo: true, IsAnimatedImage: false })
                {
                    if (ProxyStore.IsHeavy(info)) needed = true;
                    else
                    {
                        check = ProxyStore.MeasureDecode(path);
                        needed = ProxyStore.NeedsProxy(info, check);
                    }
                }
            }
            catch { needed = false; }
            Dispatcher.UIThread.Post(() =>
            {
                _checking.Remove(path);
                if (check != null) LastCheck = (path, check);
                if (needed) Enqueue(path);
                Changed?.Invoke();
            });
        }
    }

    /// <summary>直近に測った動画と結果 (確認・テスト用)。</summary>
    public (string Path, DecodeCheck Check)? LastCheck { get; private set; }

    /// <summary>自動作成のために測っている途中の動画があるか。</summary>
    public bool IsChecking => _checking.Count > 0;

    /// <summary>作成を中止して、順番待ちも空にする。</summary>
    public void CancelAll()
    {
        _queue.Clear();
        _queued.Clear();
        _cancel?.Cancel();
        Changed?.Invoke();
    }

    private async Task RunAsync()
    {
        while (_queue.Count > 0)
        {
            var path = _queue.Dequeue();
            Current = path; Progress = 0;
            Changed?.Invoke();
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            int divisor = ProxyStore.Divisor;
            var destination = ProxyStore.PathFor(path, divisor);
            var progress = new Progress<double>(p => { Progress = p; Changed?.Invoke(); });
            try
            {
                Volatile.Write(ref _focusSeconds, double.NaN);
                FocusChanged?.Invoke();
                double? Focus() { double f = Volatile.Read(ref _focusSeconds); return double.IsNaN(f) ? null : f; }
                if (destination != null)
                    await Task.Run(() => ProxyGenerator.Create(path, destination, divisor, progress, token, Focus), token);
            }
            catch (OperationCanceledException) { /* 中止 */ }
            catch (Exception ex) { LastError = $"{L.Name(Path.GetFileName(path))}: {ex.Message}"; }
            finally
            {
                _queued.Remove(path);
                _cancel.Dispose(); _cancel = null;
                Current = null;
                Changed?.Invoke();
            }
        }
    }
}
