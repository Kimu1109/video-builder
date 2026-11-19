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
    private List<StoryClip> _clipboard = new();

    public StoryboardEditor(EditorSession session)
    {
        _session = session;
        // 取り消しなどで、選んでいたものがなくなったら選択を外す
        session.ProjectChanged += () =>
        {
            if (_selected != null && Board?.FindClip(_selected) == null && Board?.FindMusic(_selected) == null) Select(null);
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
        if (_selected == id) return;
        _selected = id;
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

    /// <summary>詳細編集での変更を捨てて、ストーリーボードからシーンを作り直す。</summary>
    public void Rebuild() => Change("シンプル編集の内容に戻す", _ => { });

    /// <summary>今のメインのシーンからストーリーボードを作る (詳細編集のプロジェクトを取り込む)。</summary>
    public void ImportFromScene()
    {
        var board = StoryboardImport.FromScene(Project, Project.Root);
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

    /// <summary>タイトルを追加する。選んでいるクリップの前 (なければ先頭) に入れる。</summary>
    public StoryClip AddTitle(string text)
    {
        var clip = StoryClip.Title(text);
        int at = SelectedClip is { } sel ? Board!.Clips.IndexOf(sel) : 0;
        AddClips(new[] { clip }, at, "タイトルの追加");
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
        if (_selected != null) Remove(_selected);
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

    /// <summary>選んでいるクリップを 1 つ前 (-1) / 後ろ (+1) へ動かす。</summary>
    public void MoveSelected(int direction)
    {
        if (SelectedClip is not { } c) return;
        Move(c.Id, Board!.Clips.IndexOf(c) + direction);
    }

    // ---------------- 分割・切り出し ----------------

    /// <summary>frame の位置で、そこに見えているクリップを 2 つに分ける。分けたら後ろ半分を選ぶ。</summary>
    public bool SplitAt(int frame)
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

    public bool HasClipboard => _clipboard.Count > 0;

    public void CopySelected()
    {
        if (SelectedClip is { } c) _clipboard = new List<StoryClip> { c.Clone() };
    }

    public void CutSelected()
    {
        if (SelectedClip == null) return;
        CopySelected();
        RemoveSelected();
    }

    /// <summary>コピーしたクリップを、選んでいるクリップの後ろに貼り付ける。</summary>
    public void Paste()
    {
        if (_clipboard.Count == 0) return;
        AddClips(_clipboard.Select(c => c.Clone(newId: true)).ToList(), description: "貼り付け");
    }

    // ---------------- 位置 ----------------

    /// <summary>クリップの先頭のフレーム (なければ null)。</summary>
    public int? StartOf(string id) => Layout.Find(id)?.Start ?? Layout.FindMusic(id)?.Start;
}
