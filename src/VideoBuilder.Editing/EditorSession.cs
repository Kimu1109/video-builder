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
        _touched.Clear();
        _touchedAll = false;
        _hinted = false;
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
        // 編集の途中 (ドラッグ中など) は毎回作る。変わっていないオブジェクトは「編集前」の複製と共有する
        if (_editBefore != null)
            return _touchedAll || !_hinted ? Project.CloneSharing(_editBefore) : Project.CloneSharing(_editBefore, o => _touched.Contains(o.Id));
        return _clean ??= Project.CloneSharing(_lastSnapshot);
    }

    public bool IsEditing => _editBefore != null;

    /// <summary>BeginEdit 中にモデルを書き換えたあと、画面を更新させる。</summary>
    public void NotifyChanged()
    {
        if (_editBefore == null) _clean = null;   // 履歴に残さない変更があったので、覚えていた複製は使えない
        else _touchedAll = true;                  // どれが変わったか分からないので、プレビュー用の複製ではすべて比べる
        ProjectChanged?.Invoke();
    }

    /// <summary>
    /// BeginEdit 中に、changed のオブジェクトだけを書き換えたあと、画面を更新させる。
    /// プレビュー用の複製では、これまでに知らされたオブジェクトだけを比べ、ほかは「編集前」のものをそのまま使う
    /// (オブジェクトが多いときにドラッグが重くならないように)。書き換えたものは漏れなく渡すこと。
    /// CommitEdit ではいつもすべて比べるので、取り消しの履歴には影響しない。
    /// </summary>
    public void NotifyChanged(IEnumerable<TimelineObject> changed)
    {
        if (_editBefore == null) { NotifyChanged(); return; }
        if (!_touchedAll) { foreach (var o in changed) _touched.Add(o.Id); _hinted = true; }
        ProjectChanged?.Invoke();
    }

    // BeginEdit からこれまでに書き換えたと知らされたオブジェクト。_touchedAll なら、どれが変わったか分からない
    private readonly HashSet<string> _touched = new();
    private bool _touchedAll;
    // どれを書き換えたかを知らされたことがあるか (なければ、プレビュー用の複製ではすべて比べる)
    private bool _hinted;

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
    public int Insert(IReadOnlyList<TimelineObject> sources, int layer, int frame, string description) =>
        Insert(sources, layer, frame, description, null);

    /// <summary>
    /// テンプレートを挿入する。足すシーンとオブジェクトの追加を 1 回の取り消しにまとめる。
    /// 置ける場所がなければ何もせず 0 を返す。
    /// </summary>
    public int InsertTemplate(TemplateInstance instance, int layer, int frame, string description = "テンプレートの追加") =>
        Insert(instance.Objects, layer, frame, description, instance.Scenes, instance.Group);

    private int Insert(IReadOnlyList<TimelineObject> sources, int layer, int frame, string description, IReadOnlyList<Scene>? scenes, TemplateGroup? templateGroup = null)
    {
        if (sources.Count == 0) return 0;
        int minLayer = sources.Min(o => o.Layer), minStart = sources.Min(o => o.Start);
        layer = Math.Max(0, layer); frame = Math.Max(0, frame);
        int shift = -1;
        for (int s = 0; s < Scene.LayerCount && shift < 0; s++)
            if (sources.All(o => Scene.IsFree(o.Layer - minLayer + layer + s, o.Start - minStart + frame, o.End - minStart + frame))) shift = s;
        if (shift < 0) return 0;
        var added = new List<string>();
        Edit(description, () =>
        {
            if (scenes != null) foreach (var sc in scenes) Project.Scenes.Add(sc);
            var groupMap = new Dictionary<string, string>();
            var idMap = new Dictionary<string, string>();
            // テンプレートの組は、置くたびに新しい Id にする (同じテンプレートを 2 回置いても、別々に編集できるように)
            var group = templateGroup?.Clone();
            if (group != null) group.Id = Guid.NewGuid().ToString("N")[..8];
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
                idMap[src.Id] = o.Id;
                // テンプレートの組のオブジェクトは新しい組に入れる。貼り付けなどでは、組とのつながりは持ち込まない
                o.TemplateGroup = group != null && src.TemplateGroup == templateGroup!.Id ? group.Id : null;
                Scene.Objects.Add(o);
                added.Add(o.Id);
            }
            if (group != null)
            {
                group.Exposed.RemoveAll(x => !idMap.ContainsKey(x.ObjectId));
                foreach (var x in group.Exposed) x.ObjectId = idMap[x.ObjectId];
                (Scene.TemplateGroups ??= new()).Add(group);
            }
        });
        if (added.Count > 0) SelectMany(added);
        return added.Count;
    }

    /// <summary>
    /// シーンとして作ったテンプレート (Instantiate の Scene の置き方) を、長さ length で置く。
    /// 「長さに合わせて伸縮」をオンにするので、入りと抜けの動きはそのままで、中間が伸び縮みする。
    /// layer から下で、範囲がすべて空いている最初のレイヤーに置く。置けなければ null。Edit の中で呼ぶ。
    /// </summary>
    public TimelineObject? PlaceTemplateScene(TemplateInstance instance, int layer, int start, int length)
    {
        if (instance.Objects.Count != 1 || instance.Objects[0].Main.DefinitionId != EffectRegistry.SceneRef)
            throw new ArgumentException("シーンとして作ったテンプレートではありません", nameof(instance));
        start = Math.Max(0, start);
        int end = start + Math.Max(1, length) - 1;
        int target = FindFreeLayer(layer, start, end);
        if (target < 0) return null;
        foreach (var sc in instance.Scenes) Project.Scenes.Add(sc);
        var o = instance.Objects[0].Clone(newId: true);
        o.Layer = target;
        o.SetRange(start, end);
        o.Main.Values["stretch"] = "1";
        Scene.Objects.Add(o);
        return o;
    }

    /// <summary>
    /// テンプレートから置いたもの (シーンオブジェクト、または「複数のオブジェクト」として置いたもののどれか) の公開項目と、
    /// その項目が指すオブジェクトの並び。テンプレートから置いたものでなければ null。
    /// </summary>
    public (IReadOnlyList<ExposedParam> Items, IReadOnlyList<TimelineObject> Objects)? ExposedOfPlaced(TimelineObject o)
    {
        if (o.Main.DefinitionId == EffectRegistry.SceneRef && Project.FindScene(o.Main.GetString("scene")) is { Exposed: { } items } scene)
            return (items, scene.Objects);
        if (TemplateGroupOf(o) is { } g) return (g.Exposed, Scene.Objects);
        return null;
    }

    // ---------------- テンプレートの更新 ----------------

    /// <summary>シーンオブジェクト o が参照しているシーンの、元のテンプレート。テンプレートから作ったものでなければ null。</summary>
    public TemplateSource? TemplateOf(TimelineObject o) =>
        o.Main.DefinitionId == EffectRegistry.SceneRef ? Project.FindScene(o.Main.GetString("scene"))?.Template : null;

    /// <summary>今のシーンの中で、id のテンプレートから作ったシーンオブジェクト。</summary>
    public List<TimelineObject> TemplateInstances(string templateId) =>
        Scene.Objects.Where(o => TemplateOf(o)?.Id == templateId).ToList();

    /// <summary>
    /// シーンオブジェクト (テンプレートから作ったもの) を、pkg (同じテンプレートの新しい版) に入れ替える。
    /// 新しい版を挿入するのと同じようにシーンを作り、古いシーンの公開項目の値を、同じパラメータで同じ表示名の項目に写す。
    /// 参照先を新しいシーンにして、どこからも参照されなくなった古いシーンを消す。全部で 1 回の取り消し。更新した数を返す。
    /// </summary>
    public int UpdateTemplateInstances(IEnumerable<TimelineObject> objects, TemplatePackage pkg, string assetRoot, bool fitResolution)
    {
        var targets = objects.Where(o => TemplateOf(o)?.Id == pkg.Meta.Id).Select(o => o.Id).ToList();
        if (targets.Count == 0) return 0;
        Edit("テンプレートの更新", () =>
        {
            var oldScenes = new HashSet<string>();
            foreach (var id in targets)
            {
                var o = Scene.Find(id)!;
                var old = Project.FindScene(o.Main.GetString("scene"))!;
                foreach (var sid in Reachable(old.Id)) oldScenes.Add(sid);
                var inst = pkg.Instantiate(Project, assetRoot, TemplateInsertMode.Scene, fitResolution);
                var body = inst.Scenes[0];
                CopyExposedValues(old, body);
                string keepName = old.Name;
                Project.Scenes.AddRange(inst.Scenes);
                o.Main.Values["scene"] = body.Id;
                body.Name = keepName;   // 名前は前のものを引き継ぐ (古いシーンはこのあと消す)
            }
            // どこからも参照されなくなった古いシーンを消す。古いシーン同士の参照 (古い本体 → 古い入れ子) は数えず、
            // 残るシーン (古いもの以外・メイン・開いているシーン) からたどれるものだけを残す
            var keep = new HashSet<string>();
            foreach (var sc in Project.Scenes)
                if (!oldScenes.Contains(sc.Id) || sc == Project.Root || sc.Id == _sceneId)
                    foreach (var sid in Reachable(sc.Id)) keep.Add(sid);
            Project.Scenes.RemoveAll(sc => oldScenes.Contains(sc.Id) && !keep.Contains(sc.Id));
        });
        return targets.Count;
    }

    /// <summary>シーン id と、そこから入れ子でたどれるシーンの id。</summary>
    private HashSet<string> Reachable(string id)
    {
        var result = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!result.Add(cur) || Project.FindScene(cur) is not { } sc) continue;
            foreach (var o in sc.Objects)
                if (o.Main.DefinitionId == EffectRegistry.SceneRef) stack.Push(o.Main.GetString("scene"));
        }
        return result;
    }

    /// <summary>from の公開項目の値を、to の「同じパラメータ (キー) で同じ表示名」の項目に写す。</summary>
    private static void CopyExposedValues(Scene from, Scene to)
    {
        if (from.Exposed == null || to.Exposed == null) return;
        foreach (var x in from.Exposed)
        {
            if (x.Resolve(from) is not { } src) continue;
            var match = to.Exposed.FirstOrDefault(y => y.Key == x.Key && y.Label == x.Label);
            if (match?.Resolve(to) is not { } dst || dst.Effect.DefinitionId != src.Effect.DefinitionId) continue;
            if (src.Param.Kind == ParamKind.Track)
            {
                if (!src.Effect.Tracks.TryGetValue(x.Key, out var v)) continue;
                var c = v.Clone();
                if (c.Values.Count != dst.Object.Sections + 1)
                {
                    // キーフレームの数が違うときは、最初の値で埋める
                    double first = c.Values.Count > 0 ? c.Values[0] : src.Param.Default;
                    c.Values = Enumerable.Repeat(first, dst.Object.Sections + 1).ToList();
                }
                dst.Effect.Tracks[x.Key] = c;
            }
            else if (src.Effect.Values.TryGetValue(x.Key, out var value)) dst.Effect.Values[x.Key] = value;
        }
    }

    // ---------------- トランジション ----------------

    /// <summary>
    /// first と、同じレイヤーのすぐ後ろのクリップをつなぐ (クロスフェード)。後ろのクリップ (連結しているものも一緒に) を
    /// 下のレイヤー (手前に描かれる) の、まとめて入るいちばん近い所に移し、overlap フレームだけ重なる位置まで動かして、
    /// 入りにトランジション (フェード) を付ける。
    /// 音声は、後ろのものにフェードイン、first と連結した音声にフェードアウトを付ける。できなければ理由を返す (null なら成功)。
    /// </summary>
    public string? Crossfade(TimelineObject first, int overlap)
    {
        var next = Scene.Objects.Where(o => o.Layer == first.Layer && o.Start > first.End).MinBy(o => o.Start);
        if (next == null) return "同じレイヤーの後ろにクリップがありません";
        overlap = Math.Clamp(overlap, 1, Math.Max(1, Math.Min(first.Length, next.Length) - 1));
        var moving = WithGroup(new[] { next });
        int delta = first.End + 1 - overlap - next.Start;
        var ignore = moving.Select(o => o.Id).ToHashSet();
        // 映像と音声の組なら、first の音声が 1 つ下にあるので、まとめて入るいちばん近いずらし方を探す
        int shift = 0;
        for (int k = 1; k < Scene.LayerCount && shift == 0; k++)
            if (moving.All(o => o.Start + delta >= 0 && Scene.IsFree(o.Layer + k, o.Start + delta, o.End + delta, ignore))) shift = k;
        if (shift == 0) return "下のレイヤーに空きがありません";
        double seconds = Project.FrameToSeconds(overlap);
        var firstAudio = WithGroup(new[] { first }).Where(o => o.Definition.Category == EffectCategory.AudioSource).ToList();
        Edit("クロスフェード", () =>
        {
            foreach (var o in moving)
            {
                o.Layer += shift;
                o.SetRange(o.Start + delta, o.End + delta);
                if (o.Definition.Category == EffectCategory.AudioSource) SetFade(o, "audio.fade", "in", seconds);
                else if (o.Definition.Category == EffectCategory.VideoSource) SetFade(o, "filter.transition", "in", seconds);
            }
            foreach (var a in firstAudio) SetFade(a, "audio.fade", "out", seconds);
        });
        Select(next.Id);
        return null;

        static void SetFade(TimelineObject o, string definitionId, string key, double seconds)
        {
            var e = o.FindEffect(definitionId);
            if (e == null)
            {
                e = ObjectFactory.AddFilter(o, definitionId);
                // 付けたほうだけを使う (もう一方は 0 にする)
                e.Tracks[key == "in" ? "out" : "in"] = new AnimatedValue(0, o.Sections);
            }
            e.Tracks[key] = new AnimatedValue(Math.Round(seconds, 2), o.Sections);
            // 音声のクロスフェードは等パワーのカーブにする (つなぎ目で音量が下がらない)
            if (definitionId == "audio.fade") e.Values["curve"] = "1";
        }
    }

    // ---------------- 字幕 ----------------

    /// <summary>
    /// 字幕を、1 つにつきテキストオブジェクト 1 つとして置く。layer から下に、すべての字幕が重ならずに入る最初のレイヤーを探す。
    /// style (テキストオブジェクト) があれば、その見た目 (フォント・大きさ・色・位置・エフェクト) をひな形にする。
    /// 置いたレイヤー (置けなければ -1) を返す。1 回で取り消せる。
    /// </summary>
    public int ImportSubtitles(IReadOnlyList<SubtitleCue> cues, int layer, TimelineObject? style = null)
    {
        if (cues.Count == 0) return -1;
        var ranges = cues.Select(c =>
        {
            int start = Math.Max(0, Project.SecondsToFrames(c.Start.TotalSeconds));
            int end = Math.Max(start, Project.SecondsToFrames(c.End.TotalSeconds) - 1);
            return (Cue: c, Start: start, End: end);
        }).OrderBy(r => r.Start).ToList();
        // 字幕同士が重なっていたら、前の字幕を次の字幕の手前で終わらせる (1 つのレイヤーに並べるため)
        for (int i = 0; i + 1 < ranges.Count; i++)
            if (ranges[i].End >= ranges[i + 1].Start) ranges[i] = ranges[i] with { End = Math.Max(ranges[i].Start, ranges[i + 1].Start - 1) };
        ranges.RemoveAll(r => ranges.Count(x => x.Start == r.Start) > 1 && ranges.First(x => x.Start == r.Start) != r);

        int target = -1;
        for (int l = Math.Max(0, layer); l < Scene.LayerCount && target < 0; l++)
            if (ranges.All(r => Scene.IsFree(l, r.Start, r.End))) target = l;
        if (target < 0) return -1;

        var added = new List<string>();
        Edit("字幕の読み込み", () =>
        {
            foreach (var r in ranges)
            {
                var o = CreateSubtitleText(r.Cue.Text, target, r.Start, r.End, style);
                Scene.Objects.Add(o);
                added.Add(o.Id);
            }
        });
        SelectMany(added);
        return target;
    }

    /// <summary>
    /// 字幕用のテキストオブジェクトを作る (シーンにはまだ入れない)。style (テキストオブジェクト) があれば、その見た目をひな形にする。
    /// なければ、画面の下寄りに縁取り付きの文字を置く。
    /// </summary>
    public TimelineObject CreateSubtitleText(string text, int layer, int start, int end, TimelineObject? style = null)
    {
        TimelineObject o;
        if (style != null && style.Main.DefinitionId == EffectRegistry.Text)
        {
            o = style.Clone(newId: true);
            o.GroupId = null;
            o.PluginData = null;
            while (o.KeyFrames.Count > 0) o.RemoveKeyFrameAt(o.KeyFrames.Count - 1);
            o.Start = start; o.End = end;
        }
        else
        {
            o = ObjectFactory.Create(EffectRegistry.Text, layer, start, end - start + 1);
            o.Main.Tracks["size"] = new AnimatedValue(48);
            o.Draw!.Tracks["y"] = new AnimatedValue(Math.Round(Project.Settings.Height * 0.4));
            var border = ObjectFactory.AddFilter(o, "filter.border");
            border.Tracks["size"] = new AnimatedValue(3);
        }
        o.Layer = layer;
        o.Main.Values["text"] = text;
        return o;
    }

    /// <summary>
    /// layer から下で、start〜end がすべて空いている最初のレイヤー (なければ -1)。
    /// </summary>
    public int FindFreeLayer(int layer, int start, int end)
    {
        for (int l = Math.Max(0, layer); l < Scene.LayerCount; l++)
            if (Scene.IsFree(l, start, end)) return l;
        return -1;
    }

    /// <summary>
    /// 長さを削らずにオブジェクトを置く。layer から下で、start から length フレームがすべて空いている最初のレイヤーに置く。
    /// 置けなければ null。Edit の中で呼ぶ (取り消しの単位は呼び出し側の Edit)。
    /// </summary>
    public TimelineObject? PlaceObject(string definitionId, int layer, int start, int length, Action<TimelineObject>? init = null)
    {
        start = Math.Max(0, start);
        int end = start + Math.Max(1, length) - 1;
        int target = FindFreeLayer(layer, start, end);
        if (target < 0) return null;
        var o = ObjectFactory.Create(definitionId, target, start, end - start + 1);
        init?.Invoke(o);
        Scene.Objects.Add(o);
        return o;
    }

    /// <summary>レイヤーのテキストオブジェクトを、時間順に字幕にする。</summary>
    public List<SubtitleCue> ExportSubtitles(int layer) =>
        Scene.Objects.Where(o => o.Layer == layer && o.Enabled && o.Main.DefinitionId == EffectRegistry.Text)
            .OrderBy(o => o.Start)
            .Select(o => new SubtitleCue(TimeSpan.FromSeconds(Project.FrameToSeconds(o.Start)), TimeSpan.FromSeconds(Project.FrameToSeconds(o.End + 1)), o.Main.GetString("text")))
            .ToList();

    // ---------------- テンプレートの公開項目 ----------------

    /// <summary>obj が「複数のオブジェクトとして置いた」テンプレートのものなら、その組。</summary>
    public TemplateGroup? TemplateGroupOf(TimelineObject obj) => Scene.FindTemplateGroup(obj.TemplateGroup);

    /// <summary>obj の項目を公開できるか: テンプレートの組のオブジェクトか、メイン以外のシーンのオブジェクト。</summary>
    public bool CanExpose(TimelineObject obj) => TemplateGroupOf(obj) != null || Scene != Project.Root;

    /// <summary>obj の effectIndex 番目のエフェクトの key が公開されていればその項目 (組のオブジェクトなら組の項目)。</summary>
    public ExposedParam? FindExposed(TimelineObject obj, int effectIndex, string key) =>
        (TemplateGroupOf(obj)?.Exposed ?? Scene.Exposed)?.FirstOrDefault(x => x.ObjectId == obj.Id && x.Effect == effectIndex && x.Key == key);

    /// <summary>公開項目に加える (すでにあれば表示名を変える)。label が null なら外すだけ。組のオブジェクトなら組の項目を変える。</summary>
    public void SetExposed(TimelineObject obj, int effectIndex, string key, string? label)
    {
        var target = new ExposedParam { ObjectId = obj.Id, Effect = effectIndex, Key = key, Label = label ?? "" };
        string? groupId = TemplateGroupOf(obj)?.Id;
        Edit(label == null ? "公開の解除" : "項目の公開", () =>
        {
            var group = Scene.FindTemplateGroup(groupId);
            var list = group?.Exposed ?? (Scene.Exposed ??= new List<ExposedParam>());
            int i = list.FindIndex(x => x.SameTarget(target));
            if (label == null) { if (i >= 0) list.RemoveAt(i); }
            else if (i >= 0) list[i].Label = label;
            else list.Add(target);
            if (group == null && list.Count == 0) Scene.Exposed = null;
        });
    }

    /// <summary>今のシーンのテンプレートの組の公開項目を items に置き換える (並べ替え・グループ・表示名の変更・削除)。</summary>
    public void ReplaceGroupExposed(string groupId, IEnumerable<ExposedParam> items)
    {
        if (Scene.FindTemplateGroup(groupId) == null) return;
        var list = ExposedParam.GroupTogether(items.Select(x => x.Clone()));
        Edit("テンプレートの項目の編集", () => Scene.FindTemplateGroup(groupId)!.Exposed = list);
    }

    /// <summary>
    /// シーンの公開項目を items に置き換える (並べ替え・グループ・表示名の変更・削除)。
    /// 同じグループの項目はまとめる。1 回で取り消せる。
    /// </summary>
    public void ReplaceExposed(string sceneId, IEnumerable<ExposedParam> items)
    {
        var scene = Project.FindScene(sceneId);
        if (scene == null) return;
        var list = ExposedParam.GroupTogether(items.Select(x => x.Clone()));
        Edit("テンプレートの項目の編集", () =>
        {
            var target = Project.FindScene(sceneId)!;
            target.Exposed = list.Count == 0 ? null : list;
        });
    }

    /// <summary>選んだオブジェクトにスタイルを当てる。当てた数を返す。</summary>
    public int ApplyStyle(EffectStyle style)
    {
        var targets = SelectedObjects.ToList();
        int n = 0;
        if (targets.Count == 0) return 0;
        Edit("スタイルの適用", () => { foreach (var o in targets) if (style.ApplyTo(o)) n++; });
        return n;
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
        if (i <= 0 || effect.Definition.Category is not (EffectCategory.Filter or EffectCategory.AudioFilter)) return;
        Edit("エフェクトの削除", () => obj.Effects.RemoveAt(i));
    }

    public void MoveEffect(TimelineObject obj, Effect effect, int direction)
    {
        int i = obj.Effects.IndexOf(effect);
        int j = i + direction;
        if (i <= 0 || j <= 0 || j >= obj.Effects.Count) return;
        if (effect.Definition.Category is not (EffectCategory.Filter or EffectCategory.AudioFilter) || obj.Effects[j].Definition.Category != effect.Definition.Category) return;
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
