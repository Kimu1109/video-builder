using Avalonia;
using Avalonia.Controls;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.App.Shell;
using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Workspaces.Advanced;

/// <summary>
/// 詳細編集 (AviUtl 風): プレビュー・プロパティ・タイムライン。1 つのウィンドウにまとめるか、
/// タイムラインとプロパティを別ウィンドウにするかを選べる (<see cref="WindowMode"/>)。
/// </summary>
public sealed class AdvancedWorkspace : IWorkspace
{
    private readonly EditorSession _session = Svc.Session;
    private readonly ObjectSettingsView _settingsView = new();
    private readonly Grid _center = new();
    private readonly ContentControl _timelineHost = new(), _settingsHost = new();
    private ToolWindow? _timelineWindow, _settingsWindow;
    private IShell? _shell;
    private bool _docked = true, _placed;
    private readonly IconToggle _snap = new("Snap", "スナップ (Alt を押している間は無効)");

    public TimelineView Timeline { get; } = new();
    public EditorMode Mode => EditorMode.Advanced;
    public Control View => _center;

    public AdvancedWorkspace()
    {
        Timeline.Canvas.ObjectActivated += _ => ShowSettingsPanel();
        Timeline.Canvas.ErrorRaised += m => _ = EditorCommands.ShowErrorAsync(m);
        _snap.IsChecked = Svc.Settings.Snap;
        _snap.IsCheckedChanged += (_, _) => { Svc.Settings.Snap = _snap.IsChecked == true; Svc.Settings.Save(); };
        _session.ProjectChanged += () => { if (_snap.IsChecked != Svc.Settings.Snap) _snap.IsChecked = Svc.Settings.Snap; };
    }

    // ---------------- 切り替え ----------------

    public void Activate(IShell shell)
    {
        _shell = shell;
        _placed = false;
        ApplyWindowMode(force: true);
    }

    public void Deactivate()
    {
        _timelineWindow?.Hide(); _settingsWindow?.Hide();
        Detach();
        _placed = false;
        _shell = null;
    }

    /// <summary>アプリの終了時: 別ウィンドウも閉じる。</summary>
    public void CloseWindows()
    {
        foreach (var w in new[] { _timelineWindow, _settingsWindow }) { if (w != null) { w.AllowClose = true; w.Close(); } }
    }

    // ---------------- ウィンドウ構成 (分離 / ドッキング) ----------------

    public static bool ResolveDocked() => Svc.Settings.WindowMode switch
    {
        WindowMode.Separate => false,
        // 自動: 1 つのウィンドウにまとめる (一般的な動画編集アプリと同じ)。分けたい人は設定で選ぶ
        _ => true,
    };

    private void Detach()
    {
        _timelineHost.Content = null; _settingsHost.Content = null;
        if (_timelineWindow != null) _timelineWindow.Content = null;
        if (_settingsWindow != null) _settingsWindow.Content = null;
        _center.Children.Clear();
        _center.RowDefinitions.Clear(); _center.ColumnDefinitions.Clear();
        if (_shell != null) (_shell.PreviewArea.Parent as Panel)?.Children.Remove(_shell.PreviewArea);
        foreach (var c in new Control[] { _settingsHost, _timelineHost }) (c.Parent as Panel)?.Children.Remove(c);
    }

    public void ApplyWindowMode(bool force = false)
    {
        if (_shell == null) return;   // 今は使われていない (シンプル編集のとき)
        bool docked = ResolveDocked();
        if (!force && _placed && docked == _docked) return;
        _docked = docked;
        _placed = true;
        var window = _shell.Window;

        // いったんすべて外してから、置き直す
        Detach();
        // 別のウィンドウへ移す前に、元のウィンドウで予約済みのレイアウト処理を済ませておく
        // (移動後に古い側のレイアウトが走ると Avalonia が例外を出す)
        window.UpdateLayout();
        _timelineWindow?.UpdateLayout(); _settingsWindow?.UpdateLayout();

        if (docked)
        {
            _timelineWindow?.Hide(); _settingsWindow?.Hide();
            _center.RowDefinitions = new RowDefinitions("*,4,300");
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,420") };
            top.ColumnDefinitions[0].MinWidth = 200; top.ColumnDefinitions[2].MinWidth = 300;
            top.Children.Add(_shell.PreviewArea);
            var vs = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(vs, 1); top.Children.Add(vs);
            Grid.SetColumn(_settingsHost, 2); top.Children.Add(_settingsHost);
            _center.RowDefinitions[0].MinHeight = 120; _center.RowDefinitions[2].MinHeight = 80;
            _center.Children.Add(top);
            var hs = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
            Grid.SetRow(hs, 1); _center.Children.Add(hs);
            Grid.SetRow(_timelineHost, 2); _center.Children.Add(_timelineHost);
            _settingsHost.Content = _settingsView;
            _timelineHost.Content = Timeline;
        }
        else
        {
            _center.Children.Add(_shell.PreviewArea);
            _timelineWindow ??= new ToolWindow("タイムライン - video-builder", 1100, 340);
            _settingsWindow ??= new ToolWindow("プロパティ - video-builder", 440, 560);
            _timelineWindow.Content = Timeline;
            _settingsWindow.Content = _settingsView;
            if (window.IsVisible) ShowToolWindows();
        }
    }

    /// <summary>メインウィンドウが表示されたとき (別ウィンドウの位置を、メインウィンドウに合わせて決める)。</summary>
    public void OnShellOpened()
    {
        if (_shell != null && !_docked) ShowToolWindows();
    }

    private void ShowToolWindows()
    {
        if (_shell == null) return;
        var main = _shell.Window;
        if (_timelineWindow is { IsVisible: false })
        {
            _timelineWindow.Position = new PixelPoint(main.Position.X, main.Position.Y + (int)(main.Height * main.RenderScaling) + 40);
            _timelineWindow.Show(main);
            _shell.AttachDropTarget(_timelineWindow);
        }
        if (_settingsWindow is { IsVisible: false })
        {
            _settingsWindow.Position = new PixelPoint(main.Position.X + (int)(main.Width * main.RenderScaling) + 12, main.Position.Y);
            _settingsWindow.Show(main);
            _shell.AttachDropTarget(_settingsWindow);
        }
    }

    public IEnumerable<Window> ToolWindows => new Window?[] { _timelineWindow, _settingsWindow }.Where(w => w is { IsVisible: true })!;

    public void ShowSettingsPanel()
    {
        if (_docked || _shell == null) return;
        ShowToolWindows();
        _settingsWindow?.Activate();
    }

    public void ShowTimelinePanel()
    {
        if (_docked || _shell == null) return;
        ShowToolWindows();
        _timelineWindow?.Activate();
    }

    private static void SetWindowMode(WindowMode mode)
    {
        Svc.Settings.WindowMode = mode;
        Svc.Settings.Save();
        EditorCommands.ApplyWindowMode();
    }

    // ---------------- メニュー・ツールバー ----------------

    private bool Selected() => _session.SelectionIds.Count > 0;

    public IEnumerable<object> BuildEditMenu(IShell shell) => new object[]
    {
        shell.Track(Menus.Item("切り取り", "Cut", Cut, Shortcuts.First("edit.cut")), Selected),
        shell.Track(Menus.Item("コピー", "Copy", Copy, Shortcuts.First("edit.copy")), Selected),
        shell.Track(Menus.Item("貼り付け", "Paste", Paste, Shortcuts.First("edit.paste")), () => CanPaste),
        shell.Track(Menus.Item("削除", "Delete", Delete, Shortcuts.First("edit.delete")), Selected),
        Menus.Sep(),
        shell.Track(Menus.Item("現在位置で分割", "Split", Split, Shortcuts.First("edit.split")), Selected),
        shell.Track(Menus.Item("キーフレームを追加", "AddDiamond", EditorCommands.AddKeyFrame, Shortcuts.First("edit.addKey")), Selected),
        Menus.Sep(),
        Menus.Sub("出力範囲", "Crop", new object[]
        {
            Menus.Item("開始を現在位置に", null, EditorCommands.SetRangeStart, Shortcuts.First("range.start")),
            Menus.Item("終了を現在位置に", null, EditorCommands.SetRangeEnd, Shortcuts.First("range.end")),
            Menus.Item("解除", null, EditorCommands.ClearRange, Shortcuts.First("range.clear")),
        }),
        shell.Track(Menus.Item("テンプレートとして保存...", "Template", () => _ = EditorCommands.SaveTemplateAsync(), Shortcuts.First("alias.create")), Selected),
        Menus.Item("テンプレートライブラリ...", "Template", () => _ = EditorCommands.ShowTemplateLibraryAsync(), Shortcuts.First("template.library")),
        Menus.Item("グリッドの設定...", "GridOn", () => _ = EditorCommands.GridSettingsAsync()),
    };

    public IEnumerable<object> BuildViewMenu(IShell shell) => new object[]
    {
        Menus.Sub("ウィンドウ", "Dock", new object[]
        {
            Menus.Item("ウィンドウを分ける", "OpenInNew", () => SetWindowMode(WindowMode.Separate)),
            Menus.Item("1 つのウィンドウにまとめる", "Dock", () => SetWindowMode(WindowMode.Docked)),
        }),
        Menus.Item("タイムラインを表示", "Layers", ShowTimelinePanel),
        Menus.Item("プロパティを表示", "Tune", ShowSettingsPanel),
    };

    public IEnumerable<Control> BuildToolbar(IShell shell)
    {
        // スナップのボタンは作り直さずに使い回すので、前のツールバーから外しておく
        (_snap.Parent as Panel)?.Children.Remove(_snap);
        return ToolbarItems(shell);
    }

    private Control[] ToolbarItems(IShell shell) => new Control[]
    {
        shell.Track(new IconButton("Cut", "切り取り (Ctrl+X)", Cut), Selected),
        shell.Track(new IconButton("Copy", "コピー (Ctrl+C)", Copy), Selected),
        shell.Track(new IconButton("Paste", "貼り付け (Ctrl+V)", Paste), () => CanPaste),
        shell.Track(new IconButton("Delete", "削除 (Delete)", Delete), Selected),
        ShellToolbar.Divider(),
        shell.Track(new IconButton("Split", "現在位置で分割 (S)", Split), Selected),
        shell.Track(new IconButton("AddDiamond", "キーフレームを追加 (P)", EditorCommands.AddKeyFrame), Selected),
        ShellToolbar.Divider(),
        _snap,
        ShellToolbar.Divider(),
        new IconButton("Movie", "メディアファイルを読み込む", () => _ = ImportFilesAsync()),
        new IconButton("Output", "動画の出力 (Ctrl+E)", () => _ = EditorCommands.ExportAsync()),
    };

    // ---------------- 編集の操作 ----------------

    public bool HasSelection => Selected();
    public bool CanPaste => _session.HasClipboard;
    public void Cut() => _session.CutSelected();
    public void Copy() => _session.CopySelected();
    public void Paste() => _session.Paste(Timeline.LastLayer, _session.CurrentFrame);
    public void Delete() => _session.DeleteSelected();
    public void Split() => _session.SplitSelectedAt(_session.CurrentFrame);
    public void SelectAll() => _session.SelectMany(_session.Scene.Objects.Where(o => !_session.Scene.GetLayer(o.Layer).Locked).Select(o => o.Id));
    public void Deselect() => _session.Select(null);

    public Task ImportFilesAsync() => EditorCommands.ImportFilesAsync(Timeline.LastLayer, _session.CurrentFrame);

    public void ExternalDrag(Window window, Point point, IReadOnlyList<string>? files)
    {
        var canvas = Timeline.Canvas;
        Point? onCanvas = canvas.IsEffectivelyVisible && window.TranslatePoint(point, canvas) is { } c && new Rect(canvas.Bounds.Size).Contains(c) ? c : null;
        if (files == null) { canvas.ShowDropTarget(onCanvas); return; }
        canvas.ShowDropTarget(null);
        // タイムラインの外 (プレビューなど) に落としたときは、現在位置に置く
        canvas.DropFiles(files, onCanvas);
    }

    public void ExternalDragLeft() => Timeline.Canvas.ShowDropTarget(null);

    public void RevealFrame(int frame) => Timeline.Canvas.EnsureFrameVisible(frame);

    public void ChangeProjectSettings(ProjectSettings settings) =>
        _session.Edit("プロジェクト設定の変更", () => _session.Project.Settings = settings);
}
