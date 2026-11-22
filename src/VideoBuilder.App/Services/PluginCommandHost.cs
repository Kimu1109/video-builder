using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Views;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Media;
using VideoBuilder.Plugins;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Services;

/// <summary>
/// プラグインのコマンドに渡す窓口 (IPluginCommandContext) の実装。編集は EditorSession を通すので、取り消せる。
/// owner が null (テストなど) のときは、フォームは取り消し扱い、メッセージは出さず、進み具合のダイアログも出さない。
/// </summary>
public sealed class PluginCommandContext : IPluginCommandContext
{
    private readonly EditorSession _session;
    private readonly Window? _owner;
    private readonly string _dataRoot;

    public PluginCommandContext(EditorSession session, Window? owner, string pluginId, string dataRoot)
    {
        _session = session;
        _owner = owner;
        PluginId = pluginId;
        _dataRoot = dataRoot;
    }

    public string PluginId { get; }
    public Project Project => _session.Project;
    public Scene Scene => _session.Scene;
    public int CurrentFrame => _session.CurrentFrame;
    public IReadOnlyList<TimelineObject> SelectedObjects => _session.SelectedObjects.ToList();
    public int TargetLayer => _session.Primary?.Layer ?? 0;

    public string? ProjectDirectory => _session.FilePath is { } f ? Path.GetDirectoryName(Path.GetFullPath(f)) : null;

    public string DataDirectory
    {
        get
        {
            // ID にフォルダの区切りなどが入っていても、置き場所の外に出ないようにする
            var safe = string.Concat(PluginId.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c)).Trim('.', ' ');
            var dir = Path.Combine(_dataRoot, safe.Length > 0 ? safe : "_");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public double? MediaDuration(string path)
    {
        if (!File.Exists(path) || !FFmpegLoader.EnsureLoaded()) return null;
        var info = MediaInfo.Probe(path);
        return info is { Duration: > 0 } ? info.Duration : null;
    }

    public void Edit(string description, Action<IPluginEditor> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var editor = new Editor(_session, this);
        _session.Edit(description, () => edit(editor));
        var ids = editor.ToSelect.Where(id => _session.Scene.Find(id) != null).ToList();
        if (ids.Count > 0) _session.SelectMany(ids);
    }

    public async Task<bool> ShowFormAsync(PluginForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (_owner == null) return false;
        var d = new PluginFormDialog(form);
        await d.ShowDialog(_owner);
        return d.Accepted;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (_owner == null) return;
        await MessageDialog.Show(_owner, L.Name(title), L.Name(message));
    }

    public async Task<T> RunWithProgressAsync<T>(string title, Func<IProgress<PluginProgress>, CancellationToken, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var cts = new CancellationTokenSource();
        if (_owner == null) return await work(new Progress<PluginProgress>(), cts.Token);

        var dialog = new PluginProgressDialog(title, cts);
        // Progress は作ったスレッド (UI) で通知するので、work がどのスレッドから報告してもよい
        var progress = new Progress<PluginProgress>(dialog.Report);
        Task<T> task;
        try { task = work(progress, cts.Token); }
        catch (Exception ex) { task = Task.FromException<T>(ex); }
        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(dialog.Finish), TaskScheduler.Default);
            await dialog.ShowDialog(_owner);
        }
        return await task;
    }

    /// <summary>Edit の中で渡す書き換え口。</summary>
    private sealed class Editor(EditorSession session, PluginCommandContext context) : IPluginEditor
    {
        public readonly List<string> ToSelect = new();

        public Scene Scene => session.Scene;

        public int FindFreeLayer(int layer, int start, int end) => session.FindFreeLayer(layer, start, Math.Max(start, end));

        public TimelineObject? AddObject(string definitionId, int layer, int frame, int lengthFrames, Action<TimelineObject>? init = null)
        {
            if (!EffectRegistry.Exists(definitionId)) throw new ArgumentException($"オブジェクトの種類がありません: {definitionId}");
            return session.PlaceObject(definitionId, layer, frame, lengthFrames, init);
        }

        public TimelineObject? AddAudioFile(string path, int layer, int frame, int? lengthFrames = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            int length;
            if (lengthFrames is { } given) length = Math.Max(1, given);
            else
            {
                var seconds = context.MediaDuration(path) ?? throw new InvalidOperationException($"音声ファイルを読めません: {path}");
                // 終わりが切れないよう、端数は切り上げる
                length = Math.Max(1, (int)Math.Ceiling(seconds * session.Project.Settings.Fps.Value - 1e-6));
            }
            return session.PlaceObject(EffectRegistry.Audio, layer, frame, length, o => o.Main.Values["file"] = path);
        }

        public TimelineObject? AddText(string text, int layer, int frame, int lengthFrames, TimelineObject? style = null)
        {
            frame = Math.Max(0, frame);
            int end = frame + Math.Max(1, lengthFrames) - 1;
            int target = session.FindFreeLayer(layer, frame, end);
            if (target < 0) return null;
            var o = session.CreateSubtitleText(text ?? "", target, frame, end, style);
            session.Scene.Objects.Add(o);
            return o;
        }

        public void Group(IEnumerable<TimelineObject> objects)
        {
            var list = objects.Where(o => o != null).Distinct().ToList();
            if (list.Count < 2) return;
            var group = Guid.NewGuid().ToString("N")[..8];
            foreach (var o in list) o.GroupId = group;
        }

        public void Select(IEnumerable<TimelineObject> objects)
        {
            ToSelect.Clear();
            ToSelect.AddRange(objects.Where(o => o != null).Select(o => o.Id));
        }
    }
}

/// <summary>「プラグイン」メニューの組み立てと、コマンドの実行。</summary>
public static class PluginMenu
{
    /// <summary>コマンドがあれば「プラグイン」メニューを作る。なければ null。開くたびに、今選べるかを確かめ直す。</summary>
    public static MenuItem? Build(Func<PluginCommandEntry, PluginCommandContext> contextFor, Func<PluginCommandEntry, Task> run)
    {
        if (PluginCommands.All.Count == 0) return null;
        var menu = Menus.Sub("プラグイン(_P)", null, Array.Empty<object>());
        void Fill()
        {
            menu.Items.Clear();
            foreach (var item in Items(contextFor, run)) menu.Items.Add(item);
        }
        Fill();
        // サブメニュー (Group) を開いたときにも届くので、このメニュー自身のときだけ作り直す
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.Source, menu)) Fill(); };
        return menu;
    }

    public static List<object> Items(Func<PluginCommandEntry, PluginCommandContext> contextFor, Func<PluginCommandEntry, Task> run)
    {
        MenuItem Leaf(PluginCommandEntry e)
        {
            bool enabled;
            try { enabled = e.Command.CanRun(contextFor(e)); }
            catch (Exception ex) { enabled = false; PluginLoader.Log(e.PluginId, $"{e.Command.Id}.CanRun: {ex.Message}"); }
            return Menus.Item(e.Command.Name, null, () => _ = run(e), enabled: enabled, verbatim: true);
        }
        var result = new List<object>();
        // 分類のないものを先に、分類のあるものは登録した順にサブメニューにまとめる
        var all = PluginCommands.All;
        foreach (var e in all.Where(e => string.IsNullOrEmpty(e.Command.Group))) result.Add(Leaf(e));
        foreach (var g in all.Where(e => !string.IsNullOrEmpty(e.Command.Group)).GroupBy(e => e.Command.Group!))
            result.Add(Menus.Sub(g.Key, null, g.Select(Leaf).ToList(), verbatim: true));
        return result;
    }

    /// <summary>コマンドを実行する。例外はメッセージで知らせ、ログに残す (中止は知らせない)。</summary>
    public static async Task RunAsync(PluginCommandEntry entry, PluginCommandContext context, Window? owner)
    {
        try
        {
            await entry.Command.RunAsync(context);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PluginLoader.Log(entry.PluginId, $"{entry.Command.Id}: {ex}");
            if (owner != null)
                await MessageDialog.Show(owner, "プラグインのエラー", L.Format("{0} を実行できませんでした。\n{1}", L.Name(entry.Command.Name), L.Name(ex.Message)));
        }
    }
}
