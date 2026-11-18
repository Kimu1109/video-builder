using VideoBuilder.Core;

namespace VideoBuilder.Editing;

/// <summary>
/// 編集中のプロジェクト・現在位置・選択・履歴をまとめて持つ。UI スレッドからだけ触る。
/// </summary>
public sealed class EditorSession
{
    public Project Project { get; private set; } = new();
    public UndoStack History { get; } = new();
    public string? FilePath { get; private set; }

    private string _sceneId = "root";
    private int _frame;
    private readonly List<string> _selection = new();
    private Project? _editBefore;
    private string _editDescription = "";
    private List<TimelineObject> _clipboard = new();

    /// <summary>オブジェクトやレイヤーなど、モデルの内容が変わった。</summary>
    public event Action? ProjectChanged;
    /// <summary>別のプロジェクトに差し替わった (新規・読み込み)。</summary>
    public event Action? ProjectReplaced;
    public event Action? FrameChanged;
    public event Action? SelectionChanged;
    public event Action? SceneChanged;

    public Scene Scene => Project.FindScene(_sceneId) ?? Project.Root;
    public bool IsDirty => !History.IsAtSavedState;
    public bool HasClipboard => _clipboard.Count > 0;

    public int CurrentFrame
    {
        get => _frame;
        set
        {
            value = Math.Max(0, value);
            if (_frame == value) return;
            _frame = value;
            FrameChanged?.Invoke();
        }
    }

    /// <summary>シーンの長さ。空のときも最低 1 フレーム。</summary>
    public int SceneLength => Math.Max(1, Scene.Length);

    // ---------------- プロジェクト ----------------

    public void NewProject(ProjectSettings settings)
    {
        Project = new Project { Settings = settings };
        FilePath = null;
        ResetState();
    }

    public void Load(string path)
    {
        Project = ProjectSerializer.Load(path);
        FilePath = path;
        ResetState();
    }

    public void Save(string path)
    {
        ProjectSerializer.Save(Project, path);
        FilePath = path;
        History.MarkSaved();
        ProjectChanged?.Invoke();
    }

    private void ResetState()
    {
        _clean = null;
        _lastSnapshot = null;
        History.Clear();
        _sceneId = Project.Root.Id;
        _frame = 0;
        _selection.Clear();
        _editBefore = null;
        ProjectReplaced?.Invoke();
        SceneChanged?.Invoke();
        ProjectChanged?.Invoke();
        SelectionChanged?.Invoke();
        FrameChanged?.Invoke();
    }

    // ---------------- シーン ----------------

    public void SwitchScene(string id)
    {
        if (Project.FindScene(id) == null || id == _sceneId) return;
        _sceneId = id;
        _selection.Clear();
        _frame = 0;
        SceneChanged?.Invoke();
        SelectionChanged?.Invoke();
        FrameChanged?.Invoke();
        ProjectChanged?.Invoke();
    }

    public Scene AddScene()
    {
        Scene? created = null;
        Edit("シーンの追加", () =>
        {
            int n = Project.Scenes.Count;
            string id;
            do { id = $"scene{n++}"; } while (Project.FindScene(id) != null);
            created = new Scene { Id = id, Name = $"Scene {Project.Scenes.Count}" };
            Project.Scenes.Add(created);
        });
        SwitchScene(created!.Id);
        return created;
    }

    // ---------------- 選択 ----------------

    public IReadOnlyList<string> SelectionIds => _selection;
    public IEnumerable<TimelineObject> SelectedObjects => _selection.Select(id => Scene.Find(id)).Where(o => o != null)!;
    /// <summary>設定ダイアログに表示するオブジェクト (最後に選んだもの)。</summary>
    public TimelineObject? Primary => _selection.Count > 0 ? Scene.Find(_selection[^1]) : null;
    public bool IsSelected(string id) => _selection.Contains(id);

    public void Select(string? id, bool additive = false)
    {
        if (!additive) _selection.Clear();
        if (id != null)
        {
            if (additive && _selection.Contains(id)) _selection.Remove(id);
            else { _selection.Remove(id); _selection.Add(id); }
        }
        SelectionChanged?.Invoke();
    }

    public void SelectMany(IEnumerable<string> ids)
    {
        _selection.Clear();
        _selection.AddRange(ids.Distinct());
        SelectionChanged?.Invoke();
    }

    /// <summary>同じグループのオブジェクト (自分を含む)。</summary>
    public List<TimelineObject> WithGroup(IEnumerable<TimelineObject> objects)
    {
        var result = new List<TimelineObject>();
        foreach (var o in objects)
        {
            if (!result.Contains(o)) result.Add(o);
            if (o.GroupId == null) continue;
            foreach (var g in Scene.Objects)
                if (g.GroupId == o.GroupId && !result.Contains(g)) result.Add(g);
        }
        return result;
    }

    // ---------------- 編集 (Undo 対応) ----------------

    /// <summary>ドラッグなど連続する編集の開始。CommitEdit までが 1 回の Undo になる。</summary>
    public void BeginEdit(string description)
    {
        if (_editBefore != null) return;
        // 直前の編集の「編集後」の複製が今の状態と同じなら、それを「編集前」として使い回す (複製を 1 回減らす)
        _editBefore = _clean ?? Project.CloneSharing(_lastSnapshot);
        _clean = null;
        _editDescription = description;
    }

    // 今のプロジェクトと同じ内容の、書き換えない複製。編集のたびに作り直さずに済むよう覚えておく
    private Project? _clean;
    // 直近に作った複製。次の複製は、内容が変わっていないオブジェクトをこれと共有する (取り消し履歴のメモリを減らす)
    private Project? _lastSnapshot;

    /// <summary>
    /// 今の状態を読むだけの用途 (プレビューの描画など) に渡す複製を返す。
    /// 返したものは書き換えないこと (取り消し履歴と共有している)。
    /// </summary>
    public Project SnapshotForRead()
    {
        if (_editBefore != null) return Project.Clone();   // 編集の途中は毎回作る
        return _clean ??= Project.CloneSharing(_lastSnapshot);
    }

    public bool IsEditing => _editBefore != null;

    /// <summary>BeginEdit 中にモデルを書き換えたあと、画面を更新させる。</summary>
    public void NotifyChanged()
    {
        if (_editBefore == null) _clean = null;   // 履歴に残さない変更があったので、覚えていた複製は使えない
        ProjectChanged?.Invoke();
    }

    public void CommitEdit(string? mergeKey = null)
    {
        if (_editBefore == null) return;
        var before = _editBefore;
        _editBefore = null;
        // 変わったオブジェクトだけを複製し、ほかは「編集前」の複製と共有する
        var after = Project.CloneSharing(before);
        History.Push(new SnapshotCommand(_editDescription, before, after, mergeKey));
        _clean = after;
        _lastSnapshot = after;
        PruneSelection();
        ProjectChanged?.Invoke();
    }

    public void CancelEdit()
    {
        if (_editBefore == null) return;
        var before = _editBefore;
        _editBefore = null;
        SnapshotCommand.Restore(Project, before);
        _clean = before;
        PruneSelection();
        ProjectChanged?.Invoke();
    }

    public void Edit(string description, Action action, string? mergeKey = null)
    {
        if (_editBefore != null) { action(); NotifyChanged(); return; }
        BeginEdit(description);
        try { action(); }
        catch { CancelEdit(); throw; }
        CommitEdit(mergeKey);
    }

    public void Undo()
    {
        if (_editBefore != null) return;
        if (History.Undo(Project)) AfterHistoryMove();
    }

    public void Redo()
    {
        if (_editBefore != null) return;
        if (History.Redo(Project)) AfterHistoryMove();
    }

    private void AfterHistoryMove()
    {
        _clean = null;
        if (Project.FindScene(_sceneId) == null) { _sceneId = Project.Root.Id; SceneChanged?.Invoke(); }
        PruneSelection();
        ProjectChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    private void PruneSelection()
    {
        int n = _selection.RemoveAll(id => Scene.Find(id) == null);
        if (n > 0) SelectionChanged?.Invoke();
    }

    // ---------------- オブジェクト操作 ----------------

    /// <summary>空いている場所にオブジェクトを追加する。置けなければ下のレイヤーを探す。</summary>
    public TimelineObject? AddObject(string definitionId, int layer, int start, int length, Action<TimelineObject>? init = null)
    {
        TimelineObject? created = null;
        Edit("オブジェクトの追加", () => created = PlaceNew(definitionId, layer, start, length, init));
        if (created != null) Select(created.Id);
        return created;
    }

    private TimelineObject? PlaceNew(string definitionId, int layer, int start, int length, Action<TimelineObject>? init)
    {
        start = Math.Max(0, start);
        for (int l = Math.Max(0, layer); l < Scene.LayerCount; l++)
        {
            if (!Scene.IsFree(l, start, start)) continue;
            int end = Scene.MaxEndFrom(l, start, start + Math.Max(1, length) - 1);
            var o = ObjectFactory.Create(definitionId, l, start, end - start + 1);
            init?.Invoke(o);
            Scene.Objects.Add(o);
            return o;
        }
        return null;
    }

    /// <summary>
    /// メディアファイルからオブジェクトを作る。動画で音声があれば、すぐ下のレイヤーに音声オブジェクトも作って連結する。
    /// </summary>
    public TimelineObject? AddMediaFile(string path, int layer, int start, int lengthFrames, bool hasAudio)
    {
        var defId = ObjectFactory.DefinitionForFile(path);
        if (defId == null) return null;
        TimelineObject? created = null;
        Edit("ファイルの追加", () =>
        {
            created = PlaceNew(defId, layer, start, lengthFrames, o => o.Main.Values["file"] = path);
            if (created != null && defId == EffectRegistry.Video && hasAudio)
            {
                var audio = PlaceNew(EffectRegistry.Audio, created.Layer + 1, created.Start, created.Length, o => o.Main.Values["file"] = path);
                if (audio != null)
                {
                    var group = Guid.NewGuid().ToString("N")[..8];
                    created.GroupId = group; audio.GroupId = group;
                }
            }
        });
        if (created != null) Select(created.Id);
        return created;
    }

    public void DeleteSelected()
    {
        var targets = WithGroup(SelectedObjects.ToList());
        if (targets.Count == 0) return;
        Edit("オブジェクトの削除", () => { foreach (var o in targets) Scene.Objects.Remove(o); });
        SelectMany(Array.Empty<string>());
    }

    public void CopySelected()
    {
        var targets = WithGroup(SelectedObjects.ToList());
        if (targets.Count > 0) _clipboard = targets.Select(o => o.Clone()).ToList();
    }

    public void CutSelected()
    {
        CopySelected();
        DeleteSelected();
    }

    /// <summary>コピーしたオブジェクトを、layer / frame を左上として貼り付ける。</summary>
    public void Paste(int layer, int frame) => Insert(_clipboard, layer, frame, "貼り付け");

    /// <summary>
    /// オブジェクトの並びを、相対位置を保ったまま layer / frame を左上として追加する。
    /// 重なる場合は、置けるところまで下のレイヤーにずらす。追加できた数を返す。
    /// </summary>
    public int Insert(IReadOnlyList<TimelineObject> sources, int layer, int frame, string description)
    {
        if (sources.Count == 0) return 0;
        int minLayer = sources.Min(o => o.Layer), minStart = sources.Min(o => o.Start);
        layer = Math.Max(0, layer); frame = Math.Max(0, frame);
        var added = new List<string>();
        Edit(description, () =>
        {
            for (int shift = 0; shift < Scene.LayerCount; shift++)
            {
                bool ok = sources.All(o => Scene.IsFree(o.Layer - minLayer + layer + shift, o.Start - minStart + frame, o.End - minStart + frame));
                if (!ok) continue;
                var groupMap = new Dictionary<string, string>();
                foreach (var src in sources)
                {
                    var o = src.Clone(newId: true);
                    int len = o.Length;
                    o.Layer = src.Layer - minLayer + layer + shift;
                    o.SetRange(src.Start - minStart + frame, src.Start - minStart + frame + len - 1);
                    if (o.GroupId != null)
                    {
                        if (!groupMap.TryGetValue(o.GroupId, out var g)) groupMap[o.GroupId] = g = Guid.NewGuid().ToString("N")[..8];
                        o.GroupId = g;
                    }
                    Scene.Objects.Add(o);
                    added.Add(o.Id);
                }
                break;
            }
        });
        if (added.Count > 0) SelectMany(added);
        return added.Count;
    }

    /// <summary>出力範囲を設定する。start / end のどちらかだけ変えるときは、もう一方に今の値を渡す。</summary>
    public void SetRange(int? start, int? end)
    {
        if (start != null && end != null && end < start) (start, end) = (end, start);
        Edit("範囲設定", () => { Scene.RangeStart = start; Scene.RangeEnd = end; });
    }

    /// <summary>出力範囲 (開始, 終了)。未設定の端はシーンの先頭 / 末尾。</summary>
    public (int Start, int End) EffectiveRange
    {
        get
        {
            int last = Math.Max(0, Scene.Length - 1);
            int start = Math.Clamp(Scene.RangeStart ?? 0, 0, last), end = Math.Clamp(Scene.RangeEnd ?? last, 0, last);
            return end < start ? (0, last) : (start, end);
        }
    }

    public void SplitSelectedAt(int frame)
    {
        var targets = WithGroup(SelectedObjects.ToList()).Where(o => frame > o.Start && frame <= o.End).ToList();
        if (targets.Count == 0) return;
        Edit("分割", () =>
        {
            var groupMap = new Dictionary<string, string>();
            foreach (var o in targets)
            {
                var second = o.SplitAt(frame);
                if (second == null) continue;
                if (second.GroupId != null)
                {
                    if (!groupMap.TryGetValue(second.GroupId, out var g)) groupMap[second.GroupId] = g = Guid.NewGuid().ToString("N")[..8];
                    second.GroupId = g;
                }
                Scene.Objects.Add(second);
            }
        });
    }

    public void AddKeyFrameToSelected(int frame)
    {
        var targets = SelectedObjects.Where(o => frame > o.Start && frame <= o.End && !o.KeyFrames.Contains(frame)).ToList();
        if (targets.Count == 0) return;
        Edit("キーフレームの追加", () => { foreach (var o in targets) o.AddKeyFrame(frame); });
    }

    public void RemoveKeyFrame(TimelineObject obj, int index) =>
        Edit("キーフレームの削除", () => obj.RemoveKeyFrameAt(index));

    public void AddFilter(TimelineObject obj, string definitionId) =>
        Edit("エフェクトの追加", () => ObjectFactory.AddFilter(obj, definitionId));

    public void RemoveEffect(TimelineObject obj, Effect effect)
    {
        int i = obj.Effects.IndexOf(effect);
        if (i <= 0 || effect.Definition.Category != EffectCategory.Filter) return;
        Edit("エフェクトの削除", () => obj.Effects.RemoveAt(i));
    }

    public void MoveEffect(TimelineObject obj, Effect effect, int direction)
    {
        int i = obj.Effects.IndexOf(effect);
        int j = i + direction;
        if (i <= 0 || j <= 0 || j >= obj.Effects.Count) return;
        if (effect.Definition.Category != EffectCategory.Filter || obj.Effects[j].Definition.Category != EffectCategory.Filter) return;
        Edit("エフェクトの移動", () => (obj.Effects[i], obj.Effects[j]) = (obj.Effects[j], obj.Effects[i]));
    }

    public void SetLayerVisible(int layer, bool visible) =>
        Edit("レイヤーの表示切替", () => Scene.EditLayer(layer).Visible = visible);

    public void SetLayerLocked(int layer, bool locked) =>
        Edit("レイヤーのロック", () => Scene.EditLayer(layer).Locked = locked);

    /// <summary>次 / 前の編集点 (オブジェクトの端・中間点) を探す。</summary>
    public int FindEditPoint(int from, int direction)
    {
        var points = new SortedSet<int> { 0 };
        foreach (var o in Scene.Objects)
        {
            points.Add(o.Start); points.Add(o.End + 1);
            foreach (var k in o.KeyFrames) points.Add(k);
        }
        if (direction > 0) { foreach (var p in points) if (p > from) return p; return from; }
        int best = 0;
        foreach (var p in points) { if (p < from) best = p; else break; }
        return best;
    }
}
