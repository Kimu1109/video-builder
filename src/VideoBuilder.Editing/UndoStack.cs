using VideoBuilder.Core;

namespace VideoBuilder.Editing;

public interface IEditCommand
{
    string Description { get; }
    void Execute(Project project);
    void Undo(Project project);
    /// <summary>直後のコマンドを自分にまとめられるなら取り込んで true を返す。</summary>
    bool TryMerge(IEditCommand next);
}

/// <summary>編集前後のプロジェクト内容を丸ごと持つコマンド。</summary>
public sealed class SnapshotCommand : IEditCommand
{
    private readonly Project _before;
    private Project _after;
    private readonly string? _mergeKey;
    private DateTime _time = DateTime.UtcNow;

    public string Description { get; }

    public SnapshotCommand(string description, Project before, Project after, string? mergeKey = null)
    {
        Description = description; _before = before; _after = after; _mergeKey = mergeKey;
    }

    public void Execute(Project project) => Restore(project, _after);
    public void Undo(Project project) => Restore(project, _before);

    public bool TryMerge(IEditCommand next)
    {
        if (next is not SnapshotCommand n || _mergeKey == null || n._mergeKey != _mergeKey) return false;
        if ((n._time - _time).TotalSeconds > 1.5) return false;
        _after = n._after; _time = n._time;
        return true;
    }

    /// <summary>source の内容を target に写す。source 自体は書き換えられないよう、複製を入れる。</summary>
    public static void Restore(Project target, Project source)
    {
        var copy = source.Clone();
        target.Settings = copy.Settings;
        target.Scenes = copy.Scenes;
        target.Grid = copy.Grid;
        target.MediaPaths = copy.MediaPaths;
    }
}

public sealed class UndoStack
{
    private readonly List<IEditCommand> _undo = new();
    private readonly List<IEditCommand> _redo = new();
    private IEditCommand? _savedMarker;

    public int Limit { get; set; } = 200;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;
    public bool IsAtSavedState => (_undo.Count > 0 ? _undo[^1] : null) == _savedMarker;

    /// <summary>実行済みのコマンドを履歴に積む。</summary>
    public void Push(IEditCommand command)
    {
        _redo.Clear();
        if (_undo.Count > 0 && _undo[^1] != _savedMarker && _undo[^1].TryMerge(command)) return;
        _undo.Add(command);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
    }

    public bool Undo(Project project)
    {
        if (_undo.Count == 0) return false;
        var c = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        c.Undo(project); _redo.Add(c);
        return true;
    }

    public bool Redo(Project project)
    {
        if (_redo.Count == 0) return false;
        var c = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        c.Execute(project); _undo.Add(c);
        return true;
    }

    public void MarkSaved() => _savedMarker = _undo.Count > 0 ? _undo[^1] : null;

    public void Clear()
    {
        _undo.Clear(); _redo.Clear(); _savedMarker = null;
    }
}
