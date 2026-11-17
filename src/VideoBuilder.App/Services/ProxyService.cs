using Avalonia.Threading;
using VideoBuilder.Core;
using VideoBuilder.Media;

namespace VideoBuilder.App.Services;

/// <summary>
/// プロキシを裏で 1 本ずつ作る。進み具合は Status で見られる (ツールバーに表示する)。
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

    public string Status => Current == null ? ""
        : $"プロキシ作成中 {Path.GetFileName(Current)} {Progress * 100:0}%" + (_queue.Count > 0 ? $" (残り {_queue.Count})" : "");

    public bool IsQueued(string path) => _queued.Contains(path);

    /// <summary>プロキシを作る順番待ちに入れる。すでにあるもの・順番待ちのものは入れない。</summary>
    public void Enqueue(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || _queued.Contains(path) || ProxyStore.Exists(path)) return;
        _queued.Add(path);
        _queue.Enqueue(path);
        Changed?.Invoke();
        if (_worker == null || _worker.IsCompleted) _worker = RunAsync();
    }

    /// <summary>プロジェクトの動画のうち、プロキシがないものをすべて順番待ちに入れる。heavyOnly なら重い動画だけ。</summary>
    public int EnqueueProject(Project project, bool heavyOnly)
    {
        int n = 0;
        foreach (var path in ProjectMedia.UsedFiles(project).Where(f => ObjectFactory.DefinitionForFile(f) == EffectRegistry.Video))
        {
            if (ProxyStore.Exists(path) || _queued.Contains(path)) continue;
            if (heavyOnly && (MediaInfo.Probe(path) is not { } info || !ProxyStore.IsHeavy(info))) continue;
            Enqueue(path);
            n++;
        }
        return n;
    }

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
                if (destination != null)
                    await Task.Run(() => ProxyGenerator.Create(path, destination, divisor, progress, token), token);
            }
            catch (OperationCanceledException) { /* 中止 */ }
            catch (Exception ex) { LastError = $"{Path.GetFileName(path)}: {ex.Message}"; }
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
