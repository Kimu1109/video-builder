using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.App.Shell;
using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Workspaces.Simple;

/// <summary>
/// シンプル編集 (ムービーメーカー風): 上にプレビューとクリップの設定、下にストーリーボード。
/// 動画・写真・タイトルを 1 列に並べ、切り替え効果・キャプション・音楽を付けるだけで動画を作れる。
/// 編集はすべて <see cref="StoryboardEditor"/> を通し、メインのシーンはストーリーボードから作り直す。
/// </summary>
public sealed class SimpleWorkspace : IWorkspace
{
    private readonly StoryboardEditor _editor = Svc.Storyboard;
    private readonly EditorSession _session = Svc.Session;
    private readonly Grid _root = new();
    private readonly Grid _editArea = new();
    private readonly Border _notice = new() { IsVisible = false };
    private readonly ContentControl _previewHost = new();
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Horizontal };
    private bool _syncingScroll;
    private IShell? _shell;

    public StoryboardStrip Strip { get; } = new();
    public ClipPanel Panel { get; } = new();
    public EditorMode Mode => EditorMode.Simple;
    public Control View => _root;

    public SimpleWorkspace()
    {
        // 上: プレビュー | クリップの設定、下: ストーリーボード
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,340") };
        top.ColumnDefinitions[0].MinWidth = 200; top.ColumnDefinitions[2].MinWidth = 260;
        top.Children.Add(_previewHost);
        var vs = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(vs, 1); top.Children.Add(vs);
        Grid.SetColumn(Panel, 2); top.Children.Add(Panel);

        var bottom = new DockPanel();
        var scrollBorder = new Border { Child = _scroll, BorderThickness = new Thickness(0, 1, 0, 0) };
        scrollBorder.Classes.Add("panel");
        DockPanel.SetDock(scrollBorder, Dock.Bottom);
        bottom.Children.Add(scrollBorder);
        bottom.Children.Add(Strip);

        _editArea.RowDefinitions = new RowDefinitions("*,4,Auto");
        _editArea.RowDefinitions[0].MinHeight = 120;
        _editArea.Children.Add(top);
        var hs = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(hs, 1); _editArea.Children.Add(hs);
        Grid.SetRow(bottom, 2); _editArea.Children.Add(bottom);
        _root.Children.Add(_editArea);
        _notice.Classes.Add("panel");
        _root.Children.Add(_notice);

        Strip.ViewChanged += SyncScroll;
        _scroll.ValueChanged += (_, _) => { if (!_syncingScroll) Strip.ScrollX = _scroll.Value; };
        Strip.MenuRequested += OnContext;
        Strip.FilesDropped += (files, at) => AddFiles(files, at);
        _editor.SelectionChanged += () => _shell?.RefreshEnabled();
        _session.ProjectChanged += UpdateNotice;
        _session.ProjectReplaced += UpdateNotice;
        // 再生中は、現在位置が見える所までついていく
        _session.FrameChanged += () => { if (Svc.Preview.IsPlaying && _shell != null) Strip.EnsureFrameVisible(_session.CurrentFrame); };
    }

    private void SyncScroll()
    {
        _syncingScroll = true;
        _scroll.Maximum = Math.Max(0, Strip.ExtentWidth - Strip.ViewportWidth);
        _scroll.ViewportSize = Strip.ViewportWidth; _scroll.LargeChange = Strip.ViewportWidth * 0.8; _scroll.SmallChange = 40;
        _scroll.Value = Strip.ScrollX;
        _syncingScroll = false;
    }

    // ---------------- 切り替え ----------------

    public void Activate(IShell shell)
    {
        _shell = shell;
        (shell.PreviewArea.Parent as Panel)?.Children.Remove(shell.PreviewArea);
        shell.Window.UpdateLayout();
        _previewHost.Content = shell.PreviewArea;
        // タイムラインで選んでいたオブジェクトは外す (プレビューの枠とドラッグは、詳細編集のもの)
        _session.Select(null);
        UpdateNotice();
    }

    public void Deactivate()
    {
        _previewHost.Content = null;
        _shell = null;
    }

    public IEnumerable<Window> ToolWindows => Array.Empty<Window>();

    // ---------------- 詳細編集で変えられたプロジェクト ----------------

    /// <summary>シンプル編集で続けられないプロジェクトなら、編集画面の代わりに案内を出す。</summary>
    private void UpdateNotice()
    {
        if (_shell == null) return;
        bool ok = _editor.CanEdit;
        _editArea.IsVisible = ok;
        if (ok) { _notice.IsVisible = false; _notice.Child = null; return; }
        if (_notice.IsVisible) return;
        _notice.IsVisible = true;

        var panel = new StackPanel { MaxWidth = 560, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        void Text(string s, bool heading = false)
        {
            var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap };
            if (heading) { t.Classes.Add("heading"); t.FontSize = 16; } else t.Classes.Add("secondary");
            panel.Children.Add(t);
        }
        Button Action(string text, Action click, bool accent = false)
        {
            var b = new Button { Content = text, MinWidth = 120 };
            if (accent) b.Classes.Add("accent");
            b.Click += (_, _) => click();
            return b;
        }
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        if (_editor.Board != null)
        {
            Text("このプロジェクトは、詳細編集で変更されています。", heading: true);
            Text("シンプル編集で続けると、詳細編集で変えた内容は失われます (元に戻すで戻せます)。今の内容のまま編集するには、詳細編集に切り替えてください。");
            buttons.Children.Add(Action("シンプル編集の内容に戻す", _editor.Rebuild, accent: true));
            buttons.Children.Add(Action("今の内容から取り込み直す", _editor.ImportFromScene));
        }
        else
        {
            Text("このプロジェクトは詳細編集で作られています。", heading: true);
            Text("シンプル編集に取り込むと、並べた動画・写真と音楽だけが残ります。重なり・エフェクト・キーフレームなどは失われます (元に戻すで戻せます)。");
            buttons.Children.Add(Action("シンプル編集に取り込む", _editor.ImportFromScene, accent: true));
            buttons.Children.Add(Action("新規プロジェクト", () => _ = EditorCommands.NewAsync()));
        }
        buttons.Children.Add(Action("詳細編集に切り替える", () => EditorCommands.SetEditorMode(EditorMode.Advanced)));
        foreach (var b in buttons.Children.OfType<Button>()) b.Margin = new Thickness(0, 0, 8, 8);
        panel.Children.Add(buttons);
        _notice.Child = panel;
    }

    /// <summary>今シンプル編集で編集できるか (詳細編集で変えられていないか)。</summary>
    public bool CanEdit => _editor.CanEdit;

    // ---------------- メニュー・ツールバー ----------------

    private bool Selected() => _editor.SelectedId != null && CanEdit;
    private bool ClipSelected() => _editor.SelectedClip != null && CanEdit;

    public IEnumerable<object> BuildEditMenu(IShell shell) => new object[]
    {
        shell.Track(Menus.Item("切り取り", "Cut", Cut, Shortcuts.First("edit.cut")), ClipSelected),
        shell.Track(Menus.Item("コピー", "Copy", Copy, Shortcuts.First("edit.copy")), ClipSelected),
        shell.Track(Menus.Item("貼り付け", "Paste", Paste, Shortcuts.First("edit.paste")), () => CanPaste),
        shell.Track(Menus.Item("削除", "Delete", Delete, Shortcuts.First("edit.delete")), Selected),
        Menus.Sep(),
        Menus.Item("現在位置で分割", "Split", Split, Shortcuts.First("edit.split")),
        shell.Track(Menus.Item("開始位置を現在位置に", null, () => TrimAtPlayhead(true), Shortcuts.First("simple.trimStart")), ClipSelected),
        shell.Track(Menus.Item("終了位置を現在位置に", null, () => TrimAtPlayhead(false), Shortcuts.First("simple.trimEnd")), ClipSelected),
        shell.Track(Menus.Item("前へ移動", "ChevronLeft", () => _editor.MoveSelected(-1)), ClipSelected),
        shell.Track(Menus.Item("後ろへ移動", "ChevronRight", () => _editor.MoveSelected(1)), ClipSelected),
        Menus.Sep(),
        Menus.Item("ビデオと写真の追加...", "Movie", () => _ = AddVisualsAsync()),
        Menus.Item("タイトルの追加", "Title", AddTitle),
        Menus.Item("音楽の追加...", "MusicNote", () => _ = AddMusicAsync()),
    };

    public IEnumerable<object> BuildViewMenu(IShell shell) => new object[]
    {
        Menus.Item("ストーリーボードを拡大", "ZoomIn", () => Strip.PixelsPerSecond *= 1.25),
        Menus.Item("ストーリーボードを縮小", "ZoomOut", () => Strip.PixelsPerSecond /= 1.25),
    };

    private static IconButton Labeled(string icon, string text, string tip, Action click) =>
        new(icon, tip, click) { Text = text };

    public IEnumerable<Control> BuildToolbar(IShell shell) => new Control[]
    {
        shell.Track(Labeled("Movie", "ビデオと写真", "ビデオと写真の追加", () => _ = AddVisualsAsync()), () => CanEdit),
        shell.Track(Labeled("Title", "タイトル", "タイトルの追加 (選んでいるクリップの前)", AddTitle), () => CanEdit),
        shell.Track(Labeled("Title", "キャプション", "選んでいるクリップにキャプションを付ける", AddCaption), () => ClipSelected() && _editor.SelectedClip!.Kind != StoryClipKind.Title),
        shell.Track(Labeled("MusicNote", "音楽", "音楽の追加 (現在位置から鳴らす)", () => _ = AddMusicAsync()), () => CanEdit),
        ShellToolbar.Divider(),
        shell.Track(new IconButton("Split", "現在位置で分割 (S)", Split), () => CanEdit),
        shell.Track(new IconButton("Delete", "削除 (Delete)", Delete), Selected),
        shell.Track(new IconButton("ChevronLeft", "前へ移動", () => _editor.MoveSelected(-1)), ClipSelected),
        shell.Track(new IconButton("ChevronRight", "後ろへ移動", () => _editor.MoveSelected(1)), ClipSelected),
        ShellToolbar.Divider(),
        Labeled("Output", "動画の出力", "動画の出力 (Ctrl+E)", () => _ = EditorCommands.ExportAsync()),
    };

    private void OnContext(string? id, int frame)
    {
        var menu = new ContextMenu();
        bool clip = id != null && _editor.Board?.FindClip(id) != null;
        if (id != null)
        {
            if (clip)
            {
                menu.Items.Add(Menus.Item("ここで分割", "Split", () => _editor.SplitAt(frame)));
                menu.Items.Add(Menus.Item("前へ移動", "ChevronLeft", () => _editor.MoveSelected(-1)));
                menu.Items.Add(Menus.Item("後ろへ移動", "ChevronRight", () => _editor.MoveSelected(1)));
                menu.Items.Add(Menus.Sep());
                menu.Items.Add(Menus.Item("切り取り", "Cut", Cut, Shortcuts.First("edit.cut")));
                menu.Items.Add(Menus.Item("コピー", "Copy", Copy, Shortcuts.First("edit.copy")));
            }
            menu.Items.Add(Menus.Item("削除", "Delete", () => _editor.Remove(id), Shortcuts.First("edit.delete")));
            menu.Items.Add(Menus.Sep());
        }
        menu.Items.Add(Menus.Item("貼り付け", "Paste", Paste, Shortcuts.First("edit.paste"), CanPaste));
        menu.Items.Add(Menus.Item("ビデオと写真の追加...", "Movie", () => _ = AddVisualsAsync()));
        menu.Items.Add(Menus.Item("タイトルの追加", "Title", AddTitle));
        menu.Items.Add(Menus.Item("音楽の追加...", "MusicNote", () => _ = AddMusicAsync()));
        menu.Open(Strip);
    }

    // ---------------- 追加 ----------------

    private async Task AddVisualsAsync()
    {
        if (!CanEdit) return;
        var files = await EditorCommands.PickMediaFilesAsync(SimpleMedia.VisualExtensions, "ビデオと写真");
        AddFiles(files, null);
    }

    private async Task AddMusicAsync()
    {
        if (!CanEdit) return;
        var files = await EditorCommands.PickMediaFilesAsync(EffectRegistry.AudioExtensions.Concat(EffectRegistry.VideoExtensions).ToArray(), "音楽");
        if (files.Count == 0) return;
        var errors = new List<string>();
        double at = Math.Round(_editor.Project.FrameToSeconds(_session.CurrentFrame), 3);
        foreach (var path in files)
        {
            var r = SimpleMedia.FromFile(path);
            // 動画を選んだときは、その音声を音楽として使う
            var music = r.Music ?? (r.Clip is { Kind: StoryClipKind.Video, HasAudio: true } v ? new MusicClip { File = v.File, SourceSeconds = v.SourceSeconds } : null);
            if (music == null) { errors.Add(r.Error ?? L.Format("音声が含まれていません: {0}", L.Name(Path.GetFileName(path)))); continue; }
            music.StartSeconds = at;
            _editor.AddMusic(music);
        }
        if (errors.Count > 0) await EditorCommands.ShowErrorAsync(string.Join("\n", errors));
    }

    /// <summary>ファイルをストーリーボードに入れる。index は並びの何番目か (null なら選んでいるクリップの後ろ)。</summary>
    public void AddFiles(IReadOnlyList<string> files, int? index)
    {
        if (files.Count == 0) return;
        if (!CanEdit) { _ = EditorCommands.ShowErrorAsync("このプロジェクトは詳細編集で変更されているため、シンプル編集では追加できません。"); return; }
        Svc.Preview.Stop();
        var errors = SimpleMedia.AddFiles(_editor, files, index);
        if (_editor.SelectedId is { } id && _editor.StartOf(id) is { } start) EditorCommands.Seek(start);
        if (errors.Count > 0) _ = EditorCommands.ShowErrorAsync(string.Join("\n", errors));
    }

    private void AddTitle()
    {
        if (!CanEdit) return;
        var clip = _editor.AddTitle("タイトル");
        if (_editor.StartOf(clip.Id) is { } start) EditorCommands.Seek(start);
    }

    private void AddCaption()
    {
        if (_editor.SelectedClip is not { Kind: not StoryClipKind.Title } clip) return;
        if (clip.Caption.Length == 0) _editor.UpdateClip(clip.Id, "キャプションの追加", c => c.Caption = L.T("キャプション"));
        Panel.Refresh();
        Panel.CaptionBox?.Focus();
        Panel.CaptionBox?.SelectAll();
    }

    /// <summary>選んでいるクリップの始まり / 終わりを現在位置にする。</summary>
    public void TrimAtPlayhead(bool start)
    {
        if (!CanEdit) return;
        if (!_editor.TrimSelectedAt(_session.CurrentFrame, start))
            _ = EditorCommands.ShowErrorAsync("現在位置を、選んでいるクリップの中に合わせてください。");
    }

    // ---------------- 編集の操作 ----------------

    public bool HasSelection => Selected();
    public bool CanPaste => _editor.HasClipboard && CanEdit;
    public void Cut() { if (CanEdit) _editor.CutSelected(); }
    public void Copy() => _editor.CopySelected();
    public void Paste() { if (CanEdit) _editor.Paste(); }
    public void Delete() { if (CanEdit) _editor.RemoveSelected(); }
    public void Split() { if (CanEdit) _editor.SplitAt(_session.CurrentFrame); }
    public void SelectAll() { }
    public void Deselect() => _editor.Select(null);

    public Task ImportFilesAsync() => AddFilesFromPickerAsync();

    private async Task AddFilesFromPickerAsync()
    {
        if (!CanEdit) return;
        AddFiles(await EditorCommands.PickMediaFilesAsync(SimpleMedia.AllExtensions, "メディアファイル"), null);
    }

    public void ExternalDrag(Window window, Point point, IReadOnlyList<string>? files)
    {
        Point? onStrip = Strip.IsEffectivelyVisible && window.TranslatePoint(point, Strip) is { } p && new Rect(Strip.Bounds.Size).Contains(p) ? p : null;
        if (files == null) { Strip.ShowDropTarget(onStrip); return; }
        Strip.ShowDropTarget(null);
        AddFiles(files, onStrip is { } at ? Strip.InsertIndexAt(at.X) : null);
    }

    public void ExternalDragLeft() => Strip.ShowDropTarget(null);

    public void RevealFrame(int frame) => Strip.EnsureFrameVisible(frame);

    public void ChangeProjectSettings(ProjectSettings settings)
    {
        // 画像サイズが変わると写真の大きさの合わせ方も変わるので、ストーリーボードから作り直す
        bool rebuild = _editor.Board != null && _editor.CanEdit;
        _session.Edit("プロジェクト設定の変更", () =>
        {
            _session.Project.Settings = settings;
            if (rebuild) StoryboardCompiler.Apply(_session.Project);
        });
    }
}
