using VideoBuilder.Core;

namespace VideoBuilder.Editing;

/// <summary>
/// シンプル編集 (ストーリーボード) の操作。どの操作も、ストーリーボードを書き換えてからメインのシーンを作り直すところまでを
/// <see cref="EditorSession.Edit"/> の 1 回にまとめるので、取り消し・やり直し・保存・プレビューはこれまでと同じ仕組みで動く。
/// 画面には依存しない (テストから直接使える)。
/// </summary>
public sealed class StoryboardEditor
{
    private readonly EditorSession _session;
    private string? _selected;
    // 一緒に選んでいるクリップ (Ctrl / Shift を押しながらのクリック)。_selected (主に選んでいるもの) は含めない
    private readonly HashSet<string> _extra = new();
    private List<StoryClip> _clipboard = new();
    private MusicClip? _musicClipboard;

    public StoryboardEditor(EditorSession session)
    {
        _session = session;
        // 取り消しなどで、選んでいたものがなくなったら選択を外す
        session.ProjectChanged += () =>
        {
            int pruned = _extra.RemoveWhere(id => Board?.FindClip(id) == null);
            if (_selected != null && Board?.FindClip(_selected) == null && Board?.FindMusic(_selected) == null) Select(null);
            else if (pruned > 0) SelectionChanged?.Invoke();
        };
        session.ProjectReplaced += () => Select(null);
    }

    public EditorSession Session => _session;
    public Project Project => _session.Project;
    public Storyboard? Board => _session.Project.Storyboard;
    public event Action? SelectionChanged;

    /// <summary>今の配置 (クリップ・音楽が何フレーム目にあるか)。</summary>
    public StoryboardLayout Layout => StoryboardCompiler.Layout(Board ?? new Storyboard(), Project);

    /// <summary>
    /// シンプル編集で続けて編集できる状態か。ストーリーボードから作ったままのシーン、または空のプロジェクトなら true。
    /// 詳細編集でシーンを変えていたら false (続けるには <see cref="Rebuild"/> か <see cref="ImportFromScene"/>)。
    /// </summary>
    public bool CanEdit
    {
        get
        {
            // 作り直して比べるので、履歴が動いていなければ前の結果を使う (メニューの有効・無効の判定で何度も呼ばれる)
            var key = (Project, _session.History.Version, _session.IsEditing);
            if (_canEditKey != key) { _canEdit = StoryboardCompiler.IsInSync(Project); _canEditKey = key; }
            return _canEdit;
        }
    }
    private (Project, long, bool)? _canEditKey;
    private bool _canEdit;

    // ---------------- 選択 ----------------

    /// <summary>選んでいるクリップか音楽の ID。</summary>
    public string? SelectedId => _selected;
    public StoryClip? SelectedClip => _selected == null ? null : Board?.FindClip(_selected);
    public MusicClip? SelectedMusic => _selected == null ? null : Board?.FindMusic(_selected);

    public void Select(string? id)
    {
        if (_selected == id && _extra.Count == 0) return;
        _selected = id;
        _extra.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>選んでいるクリップの ID (並びの順)。音楽を選んでいるときは空。</summary>
    public IReadOnlyList<string> SelectedClipIds =>
        Board?.Clips.Where(c => c.Id == _selected || _extra.Contains(c.Id)).Select(c => c.Id).ToList() ?? new List<string>();

    /// <summary>2 つ以上のクリップを選んでいるか。</summary>
    public bool HasMultipleSelection => _extra.Count > 0;

    public bool IsSelected(string id) => id == _selected || _extra.Contains(id);

    /// <summary>Ctrl を押しながらのクリック: クリップを選択に加える / 外す。音楽はそれだけを選ぶ。</summary>
    public void ToggleSelect(string id)
    {
        if (Board?.FindClip(id) == null || SelectedMusic != null) { Select(id); return; }
        if (_selected == id)
        {
            // 主に選んでいたものを外したら、残りの 1 つを主にする
            var next = SelectedClipIds.FirstOrDefault(x => x != id);
            if (next == null) { Select(null); return; }
            _extra.Remove(next);
            _selected = next;
        }
        else if (!_extra.Remove(id))
        {
            if (_selected != null) _extra.Add(_selected);
            _selected = id;
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>Shift を押しながらのクリック: 主に選んでいるクリップから id までを選ぶ。</summary>
    public void SelectRange(string id)
    {
        var board = Board;
        int to = board?.Clips.FindIndex(c => c.Id == id) ?? -1;
        int from = SelectedClip is { } sel ? board!.Clips.IndexOf(sel) : -1;
        if (to < 0 || from < 0) { Select(id); return; }
        _extra.Clear();
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            if (board!.Clips[i].Id != id) _extra.Add(board.Clips[i].Id);
        _selected = id;
        SelectionChanged?.Invoke();
    }

    /// <summary>すべてのクリップを選ぶ。</summary>
    public void SelectAllClips()
    {
        var clips = Board?.Clips;
        if (clips == null || clips.Count == 0) return;
        _selected = SelectedClip?.Id ?? clips[0].Id;
        _extra.Clear();
        foreach (var c in clips) if (c.Id != _selected) _extra.Add(c.Id);
        SelectionChanged?.Invoke();
    }

    // ---------------- 編集の土台 ----------------

    /// <summary>ストーリーボードを書き換えて、シーンを作り直す (1 回の取り消し)。ストーリーボードがなければ作る。</summary>
    public void Change(string description, Action<Storyboard> action, string? mergeKey = null) =>
        _session.Edit(description, () =>
        {
            var board = Project.Storyboard ??= new Storyboard();
            action(board);
            StoryboardCompiler.Apply(Project);
        }, mergeKey);

    /// <summary>クリップの設定を変える。スライダーなど続けて変えるときは mergeKey を渡すと 1 回の取り消しにまとまる。</summary>
    public void UpdateClip(string id, string description, Action<StoryClip> action, string? mergeKey = null)
    {
        if (Board?.FindClip(id) == null) return;
        Change(description, b => action(b.FindClip(id)!), mergeKey);
    }

    public void UpdateMusic(string id, string description, Action<MusicClip> action, string? mergeKey = null)
    {
        if (Board?.FindMusic(id) == null) return;
        Change(description, b => action(b.FindMusic(id)!), mergeKey);
    }

    /// <summary>選んでいるすべてのクリップの設定を変える (1 回の取り消し)。</summary>
    public void UpdateSelectedClips(string description, Action<StoryClip> action, string? mergeKey = null)
    {
        var ids = SelectedClipIds;
        if (ids.Count == 0) return;
        Change(description, b => { foreach (var id in ids) if (b.FindClip(id) is { } c) action(c); }, mergeKey);
    }

    /// <summary>
    /// クリップの設定を、ほかのクリップにも付ける (「すべてに同じ…を付ける」)。target が true のクリップ (null ならすべて) に、
    /// 元のクリップ (sourceId) を渡して copy を行う。1 回の取り消しにまとめる。設定を変えたクリップの数を返す。
    /// </summary>
    public int ApplyToAll(string sourceId, string description, Action<StoryClip, StoryClip> copy, Func<StoryClip, bool>? target = null)
    {
        var source = Board?.FindClip(sourceId);
        if (source == null) return 0;
        int count = Board!.Clips.Count(c => c.Id != sourceId && (target?.Invoke(c) ?? true));
        if (count == 0) return 0;
        Change(description, b =>
        {
            var src = b.FindClip(sourceId)!;
            foreach (var c in b.Clips)
                if (c.Id != sourceId && (target?.Invoke(c) ?? true)) copy(src, c);
        });
        return count;
    }

    /// <summary>音楽の鳴らし始めを seconds (作品の先頭からの秒) にする。</summary>
    public void MoveMusicTo(string id, double seconds) =>
        UpdateMusic(id, "音楽の移動", m => m.StartSeconds = Math.Max(0, Math.Round(seconds, 3)));

    /// <summary>詳細編集での変更を捨てて、ストーリーボードからシーンを作り直す。</summary>
    public void Rebuild() => Change("シンプル編集の内容に戻す", _ => { });

    /// <summary>今のメインのシーンからストーリーボードを作る (詳細編集のプロジェクトを取り込む)。</summary>
    public void ImportFromScene()
    {
        // シンプル編集で作ってから詳細編集で変えたものは、キャプション・タイトルなどの設定を前のストーリーボードから引き継ぐ
        var board = StoryboardImport.FromScene(Project, Project.Root, Project.Storyboard);
        _session.Edit("シンプル編集に取り込む", () =>
        {
            Project.Storyboard = board;
            StoryboardCompiler.Apply(Project);
        });
        Select(board.Clips.FirstOrDefault()?.Id);
    }

    // ---------------- 追加・削除・並べ替え ----------------

    /// <summary>
    /// クリップを追加する。index が null なら、選んでいるクリップの後ろ (なければ最後) に入れる。
    /// 最後に追加したものを選ぶ。prepare は同じ取り消しの中で先に行う変更 (最初の動画にプロジェクトの画像サイズを合わせるなど)。
    /// </summary>
    public void AddClips(IReadOnlyList<StoryClip> clips, int? index = null, string description = "クリップの追加", Action<Project>? prepare = null)
    {
        if (clips.Count == 0) return;
        int at = index ?? InsertIndex();
        Change(description, b =>
        {
            prepare?.Invoke(Project);
            at = Math.Clamp(at, 0, b.Clips.Count);
            b.Clips.InsertRange(at, clips);
        });
        Select(clips[^1].Id);
    }

    private int InsertIndex()
    {
        var board = Board;
        if (board == null) return 0;
        var sel = SelectedClip;
        return sel != null ? board.Clips.IndexOf(sel) + 1 : board.Clips.Count;
    }

    /// <summary>タイトルを追加する。選んでいるクリップの前 (なければ先頭) に入れる。atEnd なら最後に入れる (終わりのタイトル)。</summary>
    public StoryClip AddTitle(string text, bool atEnd = false)
    {
        var clip = StoryClip.Title(text);
        int at = atEnd ? Board?.Clips.Count ?? 0 : SelectedClip is { } sel ? Board!.Clips.IndexOf(sel) : 0;
        AddClips(new[] { clip }, at, atEnd ? "終わりのタイトルの追加" : "タイトルの追加");
        return clip;
    }

    /// <summary>音楽を追加する。startSeconds から鳴らす。</summary>
    public void AddMusic(MusicClip music)
    {
        Change("音楽の追加", b => b.Music.Add(music));
        Select(music.Id);
    }

    public void Remove(string id)
    {
        var board = Board;
        if (board == null) return;
        int index = board.Clips.FindIndex(c => c.Id == id);
        if (index >= 0)
        {
            Change("クリップの削除", b => b.Clips.RemoveAt(index));
            // 削除したあとは、同じ位置にあるクリップを選ぶ (続けて削除しやすいように)
            var clips = Board!.Clips;
            Select(clips.Count == 0 ? null : clips[Math.Min(index, clips.Count - 1)].Id);
            return;
        }
        if (board.FindMusic(id) != null)
        {
            Change("音楽の削除", b => b.Music.RemoveAll(m => m.Id == id));
            Select(null);
        }
    }

    public void RemoveSelected()
    {
        if (!HasMultipleSelection) { if (_selected != null) Remove(_selected); return; }
        var ids = SelectedClipIds.ToHashSet();
        int index = Board!.Clips.FindIndex(c => ids.Contains(c.Id));
        Change("クリップの削除", b => b.Clips.RemoveAll(c => ids.Contains(c.Id)));
        var clips = Board!.Clips;
        Select(clips.Count == 0 ? null : clips[Math.Min(index, clips.Count - 1)].Id);
    }

    /// <summary>クリップを newIndex (並べ替えたあとの位置) に動かす。</summary>
    public void Move(string id, int newIndex)
    {
        var board = Board;
        int from = board?.Clips.FindIndex(c => c.Id == id) ?? -1;
        if (from < 0) return;
        newIndex = Math.Clamp(newIndex, 0, board!.Clips.Count - 1);
        if (newIndex == from) return;
        Change("クリップの移動", b =>
        {
            var c = b.Clips[from];
            b.Clips.RemoveAt(from);
            b.Clips.Insert(newIndex, c);
        });
    }

    /// <summary>選んでいるクリップを 1 つ前 (-1) / 後ろ (+1) へ動かす。いくつも選んでいるときは、並びの順を保ったまま 1 つずつずらす。</summary>
    public void MoveSelected(int direction)
    {
        if (SelectedClip is not { } c) return;
        if (!HasMultipleSelection) { Move(c.Id, Board!.Clips.IndexOf(c) + direction); return; }
        var ids = SelectedClipIds.ToHashSet();
        var clips = Board!.Clips;
        // 端に着いているものがあれば動かさない (並びが崩れないように)
        if (direction < 0 ? ids.Contains(clips[0].Id) : ids.Contains(clips[^1].Id)) return;
        Change("クリップの移動", b =>
        {
            var list = b.Clips;
            if (direction < 0)
            {
                for (int i = 1; i < list.Count; i++)
                    if (ids.Contains(list[i].Id) && !ids.Contains(list[i - 1].Id)) (list[i - 1], list[i]) = (list[i], list[i - 1]);
            }
            else
            {
                for (int i = list.Count - 2; i >= 0; i--)
                    if (ids.Contains(list[i].Id) && !ids.Contains(list[i + 1].Id)) (list[i + 1], list[i]) = (list[i], list[i + 1]);
            }
        });
    }

    // ---------------- 分割・切り出し ----------------

    /// <summary>
    /// frame の位置で、そこに見えているクリップを 2 つに分ける。分けたら後ろ半分を選ぶ。
    /// 音楽を選んでいて、frame がその中なら音楽を分ける。
    /// </summary>
    public bool SplitAt(int frame)
    {
        if (SelectedMusic is { } music && Layout.FindMusic(music.Id) is { } ms && frame > ms.Start && frame <= ms.End)
            return SplitMusicAt(music.Id, frame);
        return SplitClipAt(frame);
    }

    /// <summary>音楽を frame の位置で 2 つに分ける。繰り返す音楽は、1 回目の中でだけ分けられる (分けると繰り返しは後ろに残る)。</summary>
    public bool SplitMusicAt(string id, int frame)
    {
        var span = Layout.FindMusic(id);
        if (span == null || frame <= span.Start || frame > span.End) return false;
        var m = span.Music;
        double offset = Project.FrameToSeconds(frame - span.Start);
        if (offset < StoryClip.MinSeconds || m.Seconds - offset < StoryClip.MinSeconds) return false;
        var second = m.Clone(newId: true);
        Change("音楽の分割", b =>
        {
            var first = b.FindMusic(id)!;
            double cut = Math.Round(first.TrimStart + offset, 3);
            second.TrimStart = cut;
            second.StartSeconds = Math.Round(first.StartSeconds + offset, 3);
            second.FadeIn = 0;
            first.TrimEnd = cut;
            first.FadeOut = 0;
            first.Loop = false;
            b.Music.Insert(b.Music.IndexOf(first) + 1, second);
        });
        Select(second.Id);
        return true;
    }

    private bool SplitClipAt(int frame)
    {
        var span = Layout.ClipAt(frame);
        if (span == null) return false;
        double offset = Project.FrameToSeconds(frame - span.Start);
        if (offset < StoryClip.MinSeconds || span.Clip.Seconds - offset < StoryClip.MinSeconds) return false;
        var second = span.Clip.Clone(newId: true);
        second.Transition = 0;
        Change("分割", b =>
        {
            var first = b.FindClip(span.Clip.Id)!;
            if (first.Kind == StoryClipKind.Video)
            {
                // 画面の上の時間を、素材の時間に直す (2 倍速なら素材は 2 倍進んでいる)
                double cut = Math.Round(first.TrimStart + offset * first.EffectiveSpeed, 3);
                second.TrimStart = cut;
                first.TrimEnd = cut;
            }
            else
            {
                second.Duration = Math.Round(first.Duration - offset, 3);
                first.Duration = Math.Round(offset, 3);
            }
            b.Clips.Insert(b.Clips.IndexOf(first) + 1, second);
        });
        Select(second.Id);
        return true;
    }

    /// <summary>
    /// 選んでいるクリップの始まり (start = true) / 終わりを、frame の位置にする (ムービーメーカーの「開始位置の設定」)。
    /// 動画なら使う範囲を、写真・タイトルなら表示時間を変える。
    /// </summary>
    public bool TrimSelectedAt(int frame, bool start)
    {
        var clip = SelectedClip;
        var span = clip == null ? null : Layout.Find(clip.Id);
        if (clip == null || span == null || !span.Contains(frame)) return false;
        double offset = Project.FrameToSeconds(frame - span.Start);
        if (start ? clip.Seconds - offset < StoryClip.MinSeconds : offset < StoryClip.MinSeconds) return false;
        UpdateClip(clip.Id, start ? "開始位置の設定" : "終了位置の設定", c =>
        {
            if (c.Kind == StoryClipKind.Video)
            {
                double end = c.EffectiveTrimEnd, source = offset * c.EffectiveSpeed;
                if (start) { c.TrimStart = Math.Round(c.TrimStart + source, 3); if (c.TrimEnd <= 0) c.TrimEnd = end; }
                else c.TrimEnd = Math.Round(c.TrimStart + source, 3);
            }
            else c.Duration = Math.Round(start ? c.Duration - offset : offset, 3);
        });
        return true;
    }

    // ---------------- コピー・貼り付け ----------------

    public bool HasClipboard => _clipboard.Count > 0 || _musicClipboard != null;

    /// <summary>選んでいるクリップ (いくつでも) か音楽をコピーする。</summary>
    public void CopySelected()
    {
        if (SelectedMusic is { } m) { _musicClipboard = m.Clone(); _clipboard = new(); return; }
        var board = Board;
        var ids = SelectedClipIds;
        if (board == null || ids.Count == 0) return;
        _clipboard = ids.Select(id => board.FindClip(id)!.Clone()).ToList();
        _musicClipboard = null;
    }

    public void CutSelected()
    {
        if (SelectedClip == null && SelectedMusic == null) return;
        CopySelected();
        RemoveSelected();
    }

    /// <summary>コピーしたクリップを、選んでいるクリップの後ろに貼り付ける。音楽なら現在位置から鳴らす。</summary>
    public void Paste()
    {
        if (_musicClipboard is { } music)
        {
            var copy = music.Clone(newId: true);
            copy.StartSeconds = Math.Round(Project.FrameToSeconds(_session.CurrentFrame), 3);
            Change("貼り付け", b => b.Music.Add(copy));
            Select(copy.Id);
            return;
        }
        if (_clipboard.Count == 0) return;
        AddClips(_clipboard.Select(c => c.Clone(newId: true)).ToList(), description: "貼り付け");
    }

    // ---------------- 端のドラッグ (長さを変える) ----------------

    /// <summary>
    /// クリップの長さを、元の設定 (original) から変える。startDelta はクリップの始まりを削る長さ、
    /// endDelta は終わりを伸ばす長さ (どちらも画面の上の秒、負なら逆向き)。ドラッグ中は何度も呼んでよい (いつも original から計算する)。
    /// </summary>
    public void ResizeClip(string id, StoryClip original, double startDelta, double endDelta, string? mergeKey = null)
    {
        if (Board?.FindClip(id) == null) return;
        UpdateClip(id, startDelta != 0 ? "開始位置の設定" : "終了位置の設定", c =>
        {
            if (c.Kind == StoryClipKind.Video)
            {
                double speed = original.EffectiveSpeed, min = StoryClip.MinSeconds * speed;
                double end = original.EffectiveTrimEnd > 0 ? original.EffectiveTrimEnd : original.TrimStart + original.Seconds * speed;
                double start = Math.Clamp(original.TrimStart + startDelta * speed, 0, Math.Max(0, end - min));
                double newEnd = end + endDelta * speed;
                if (original.SourceSeconds > 0) newEnd = Math.Min(newEnd, original.SourceSeconds);
                c.TrimStart = Math.Round(start, 3);
                c.TrimEnd = Math.Round(Math.Max(start + min, newEnd), 3);
            }
            else c.Duration = Math.Round(Math.Max(StoryClip.MinSeconds, original.Duration - startDelta + endDelta), 3);
        }, mergeKey);
    }

    /// <summary>
    /// 音楽の使う範囲を、元の設定 (original) から変える。始まりを削ると、残った音が同じ時刻に鳴るように鳴らし始めもずらす。
    /// </summary>
    public void ResizeMusic(string id, MusicClip original, double startDelta, double endDelta, string? mergeKey = null)
    {
        if (Board?.FindMusic(id) == null) return;
        UpdateMusic(id, "音楽の範囲", m =>
        {
            double end = original.EffectiveTrimEnd > 0 ? original.EffectiveTrimEnd : original.TrimStart + original.Seconds;
            // 始まりは、素材の先頭と作品の先頭より前には出せない
            double d = Math.Max(startDelta, -Math.Min(original.TrimStart, original.StartSeconds));
            double start = Math.Min(original.TrimStart + d, end - StoryClip.MinSeconds);
            m.TrimStart = Math.Round(start, 3);
            m.StartSeconds = Math.Round(original.StartSeconds + (start - original.TrimStart), 3);
            double newEnd = end + endDelta;
            if (original.SourceSeconds > 0) newEnd = Math.Min(newEnd, original.SourceSeconds);
            m.TrimEnd = Math.Round(Math.Max(start + StoryClip.MinSeconds, newEnd), 3);
        }, mergeKey);
    }

    // ---------------- 位置 ----------------

    /// <summary>
    /// 前 (direction = -1) / 次 (+1) のクリップを選ぶ。何も選んでいなければ、frame の位置にあるクリップ (次なら、その次ではなくそれ自身)。
    /// 選んだクリップを返す (端に着いていれば今のまま)。
    /// </summary>
    public StoryClip? SelectAdjacent(int direction, int frame)
    {
        var board = Board;
        if (board == null || board.Clips.Count == 0) return null;
        int index;
        if (SelectedClip is { } sel) index = Math.Clamp(board.Clips.IndexOf(sel) + Math.Sign(direction), 0, board.Clips.Count - 1);
        else
        {
            var layout = Layout;
            var at = layout.ClipAt(frame);
            if (at != null) index = direction < 0 && frame > at.Start ? at.Index : Math.Clamp(at.Index + (direction < 0 ? -1 : 0), 0, board.Clips.Count - 1);
            else index = frame >= layout.VideoLength ? board.Clips.Count - 1 : 0;
        }
        Select(board.Clips[index].Id);
        return board.Clips[index];
    }

    /// <summary>クリップの先頭のフレーム (なければ null)。</summary>
    public int? StartOf(string id) => Layout.Find(id)?.Start ?? Layout.FindMusic(id)?.Start;
}
