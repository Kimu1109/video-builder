using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.App.Workspaces.Advanced;
using VideoBuilder.App.Workspaces.Simple;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Shell;

/// <summary>ツールバーの部品。</summary>
public static class ShellToolbar
{
    public static Control Divider() => new Border { Width = 1, Margin = new Thickness(4, 4), [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Border.Default") };
}

/// <summary>
/// メインウィンドウ (シェル)。メニュー・ツールバー・プレビュー・保存の確認・ほかのアプリからのドロップなど、
/// 編集画面によらない部分を持つ。中央には今の編集画面 (<see cref="IWorkspace"/>) を置き、
/// 環境設定の「編集モード」で詳細編集とシンプル編集を切り替える。
/// </summary>
public sealed class MainWindow : Window, IShell
{
    private readonly EditorSession _session = Svc.Session;
    private readonly ContentControl _workspaceHost = new();
    private readonly ContentControl _menuHost = new(), _toolbarHost = new();
    private bool _closingConfirmed;

    private readonly TextBlock _projectInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _proxyStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly List<(Control Control, Func<bool> Enabled)> _enablers = new();
    private MenuItem? _undoItem, _redoItem;
    private readonly IconButton _themeButton;
    private readonly IconToggle _perfToggle = new("Info", "パフォーマンスチェック (Ctrl+Shift+P)");
    private MenuItem? _perfItem;

    public PreviewArea PreviewArea { get; } = new();
    public AdvancedWorkspace Advanced { get; } = new();
    private SimpleWorkspace? _simple;
    public SimpleWorkspace Simple => _simple ??= new SimpleWorkspace();
    /// <summary>今の編集画面。</summary>
    public IWorkspace Workspace { get; private set; }

    /// <summary>詳細編集のタイムライン。</summary>
    public TimelineView Timeline => Advanced.Timeline;

    Window IShell.Window => this;

    public MainWindow()
    {
        EditorCommands.Main = this;
        EditorCommands.RegisterCommands();
        Width = 1360; Height = 860; MinWidth = 640; MinHeight = 360;
        _themeButton = new IconButton("ThemeAuto", "テーマの切替", CycleTheme);

        var root = new DockPanel();
        DockPanel.SetDock(_menuHost, Dock.Top);
        root.Children.Add(_menuHost);
        DockPanel.SetDock(_toolbarHost, Dock.Top);
        root.Children.Add(_toolbarHost);
        root.Children.Add(_workspaceHost);
        Content = root;

        AddHandler(KeyDownEvent, (_, e) => { if (EditorCommands.HandleKey(e, this)) e.Handled = true; }, RoutingStrategies.Tunnel);
        _session.ProjectChanged += UpdateState;
        _session.SelectionChanged += UpdateState;
        _session.SceneChanged += UpdateState;
        Svc.Theme.Changed += UpdateThemeIcon;
        _perfToggle.IsChecked = Svc.Performance.Enabled;
        _perfToggle.IsCheckedChanged += (_, _) => Svc.Performance.Enabled = _perfToggle.IsChecked == true;
        Svc.Performance.EnabledChanged += () =>
        {
            _perfToggle.IsChecked = Svc.Performance.Enabled;
            if (_perfItem != null) _perfItem.IsChecked = Svc.Performance.Enabled;
            EditorCommands.OnPerformanceModeChanged();
        };

        Workspace = Svc.Settings.EditorMode == EditorMode.Simple ? Simple : Advanced;
        Rebuild();
        Workspace.Activate(this);
        UpdateThemeIcon();
        Closing += OnClosing;
        Svc.Proxies.Changed += () =>
        {
            _proxyStatus.Text = Svc.Proxies.Status;
            ToolTip.SetTip(_proxyStatus, Svc.Proxies.LastError ?? (Svc.Proxies.StatusDetail.Length > 0 ? Svc.Proxies.StatusDetail : null));
        };
        Closed += (_, _) =>
        {
            _xdnd?.Dispose();
            _xdnd = null;
            Svc.Proxies.CancelAll();
            Advanced.CloseWindows();
            EditorCommands.ClosePerformanceWindow();
            Svc.Preview.Dispose();
        };
        Opened += (_, _) =>
        {
            Advanced.OnShellOpened();
            AttachDropTarget(this);
            if (Svc.Performance.Enabled) EditorCommands.ShowPerformanceWindow();
        };
    }

    // ---------------- 編集画面の切り替え ----------------

    /// <summary>編集画面を切り替える (設定には書かない。書くのは EditorCommands.SetEditorMode)。</summary>
    public void SwitchWorkspace(EditorMode mode)
    {
        IWorkspace next = mode == EditorMode.Simple ? Simple : Advanced;
        if (next == Workspace) return;
        Svc.Preview.Stop();
        Workspace.Deactivate();
        Workspace = next;
        Rebuild();
        Workspace.Activate(this);
        UpdateState();
    }

    /// <summary>メニュー・ツールバー・中央を、今の編集画面のもので作り直す。</summary>
    private void Rebuild()
    {
        _enablers.Clear();
        _workspaceHost.Content = null;
        _menuHost.Content = BuildMenu();
        _toolbarHost.Content = BuildToolbar();
        _workspaceHost.Content = Workspace.View;
    }

    /// <summary>詳細編集のウィンドウ構成 (分ける / まとめる) を設定に合わせる。</summary>
    public void ApplyWindowMode() => Advanced.ApplyWindowMode();

    public IEnumerable<Window> ToolWindows => Workspace.ToolWindows.Concat(EditorCommands.PerformanceWindow is { } pw ? new Window[] { pw } : Array.Empty<Window>());
    public void ShowSettingsPanel() => Advanced.ShowSettingsPanel();
    public void ShowTimelinePanel() => Advanced.ShowTimelinePanel();

    // ---------------- ほかのアプリからのファイルのドロップ (Linux / X11) ----------------

    private Platform.XdndDropTarget? _xdnd;
    private bool _xdndTried;
    private readonly Dictionary<IntPtr, Window> _dropWindows = new();

    /// <summary>
    /// X11 では Avalonia がドロップを受け取れないので、自前の受け取り口 (XdndDropTarget) をウィンドウに付ける。
    /// Windows・macOS では Avalonia の DragDrop がそのまま使われる。
    /// </summary>
    public void AttachDropTarget(Window window)
    {
        if (!_xdndTried)
        {
            _xdndTried = true;
            if (OperatingSystem.IsLinux() && window.TryGetPlatformHandle() is { HandleDescriptor: "XID" })
            {
                _xdnd = Platform.XdndDropTarget.TryCreate();
                if (_xdnd != null)
                {
                    _xdnd.Moved += (w, x, y) => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnExternalDrag(w, x, y, null));
                    _xdnd.Left += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => { Workspace.ExternalDragLeft(); FileDropZone.Highlight(null); });
                    _xdnd.Dropped += (w, x, y, files) => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnExternalDrag(w, x, y, files));
                }
            }
        }
        if (_xdnd == null || window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle) return;
        _dropWindows[handle.Handle] = window;
        _xdnd.Attach(handle.Handle);
    }

    /// <summary>X11 からのドラッグ位置・ドロップを振り分ける。files が null ならドラッグ中。</summary>
    private void OnExternalDrag(IntPtr windowId, int x, int y, IReadOnlyList<string>? files)
    {
        var window = _dropWindows.GetValueOrDefault(windowId) ?? this;
        // X11 の位置は物理ピクセルなので、画面の拡大率で割って Avalonia の座標にする
        var p = new Point(x / window.RenderScaling, y / window.RenderScaling);
        // プロパティのファイルの欄などの上なら、そちらに渡す
        if (FileDropZone.HandleExternal(window, p, files))
        {
            Workspace.ExternalDragLeft();
            if (files != null) window.Activate();
            return;
        }
        if (files != null) window.Activate();
        Workspace.ExternalDrag(window, p, files);
    }

    // ---------------- メニュー ----------------

    public T Track<T>(T control, Func<bool> enabled) where T : Control
    {
        _enablers.Add((control, enabled));
        control.IsEnabled = enabled();
        return control;
    }

    private Menu BuildMenu()
    {
        bool advanced = Workspace.Mode == EditorMode.Advanced;
        var recent = new MenuItem { Header = "最近使ったプロジェクト", Icon = new Icon("History") };
        recent.Items.Add(new MenuItem { Header = "(なし)", IsEnabled = false });

        var fileItems = new List<object>
        {
            Menus.Item("新規プロジェクト...", "NoteAdd", () => _ = EditorCommands.NewAsync(), Shortcuts.First("file.new")),
            Menus.Item("開く...", "FolderOpen", () => _ = EditorCommands.OpenAsync(), Shortcuts.First("file.open")),
            recent,
            Menus.Sep(),
            Menus.Item("保存", "Save", () => _ = EditorCommands.SaveAsync(), Shortcuts.First("file.save")),
            Menus.Item("名前を付けて保存...", "SaveAs", () => _ = EditorCommands.SaveAsAsync(), Shortcuts.First("file.saveAs")),
            Menus.Item("自動保存から復元...", "Reset", () => _ = EditorCommands.OpenAutoSaveAsync()),
            Menus.Item("素材を収集して保存...", "FolderOpen", () => _ = EditorCommands.CollectAndSaveAsync()),
            Menus.Item("素材の付け替え...", "Link", () => _ = EditorCommands.CheckMissingFilesAsync(always: true)),
            Menus.Sub("プロキシ", "Movie", new object[]
            {
                Menus.Item("すべての動画のプロキシを作成", null, () => Svc.Proxies.EnqueueProject(_session.Project, heavyOnly: false)),
                Menus.Item("プロキシの作成を中止", null, () => Svc.Proxies.CancelAll()),
                Menus.Item("プレビューでプロキシを使う", null, () =>
                {
                    Svc.Settings.UseProxies = !Svc.Settings.UseProxies;
                    VideoBuilder.Media.ProxyStore.Enabled = Svc.Settings.UseProxies;
                    Svc.Settings.Save();
                    Svc.Preview.Request();
                }, isChecked: Svc.Settings.UseProxies),
            }),
            Menus.Sep(),
            Menus.Item("メディアファイルを読み込む...", "Movie", () => _ = Workspace.ImportFilesAsync()),
        };
        if (advanced)
        {
            fileItems.Add(Menus.Item("字幕を読み込む...", "Title", () => _ = EditorCommands.ImportSubtitlesAsync()));
            fileItems.Add(Menus.Item("字幕を書き出す...", "Title", () => _ = EditorCommands.ExportSubtitlesAsync()));
        }
        fileItems.Add(Menus.Item("動画の出力...", "Output", () => _ = EditorCommands.ExportAsync(), Shortcuts.First("file.export")));
        fileItems.Add(Menus.Sep());
        fileItems.Add(Menus.Item("終了", "Exit", Close));
        var file = Menus.Sub("ファイル(_F)", null, fileItems);
        file.SubmenuOpened += (_, _) =>
        {
            recent.Items.Clear();
            foreach (var path in Svc.Settings.RecentFiles.Where(File.Exists))
                recent.Items.Add(Menus.Item(Path.GetFileName(path), null, async () => { if (await EditorCommands.ConfirmDiscardAsync()) await EditorCommands.OpenPathAsync(path); }, verbatim: true));
            if (recent.Items.Count == 0) recent.Items.Add(new MenuItem { Header = "(なし)", IsEnabled = false });
        };

        _undoItem = Track(Menus.Item("元に戻す", "Undo", _session.Undo, Shortcuts.First("edit.undo")), () => _session.History.CanUndo);
        _redoItem = Track(Menus.Item("やり直し", "Redo", _session.Redo, Shortcuts.First("edit.redo")), () => _session.History.CanRedo);
        var editItems = new List<object> { _undoItem, _redoItem, Menus.Sep() };
        editItems.AddRange(Workspace.BuildEditMenu(this));
        editItems.Add(Menus.Sep());
        editItems.Add(Menus.Item("プロジェクト設定...", "Tune", () => _ = EditorCommands.ProjectSettingsAsync()));
        var edit = Menus.Sub("編集(_E)", null, editItems);

        MenuItem Zoom(string label, double zoom) => Menus.Item(label, null, () => { PreviewArea.Preview.Zoom = zoom; PreviewArea.Preview.InvalidateVisual(); });
        var viewItems = new List<object>
        {
            Menus.Sub("編集モード", "MovieEdit", new object[]
            {
                Menus.Item("詳細編集 (AviUtl 風)", "Layers", () => EditorCommands.SetEditorMode(EditorMode.Advanced), isChecked: advanced),
                Menus.Item("シンプル編集 (ムービーメーカー風)", "Movie", () => EditorCommands.SetEditorMode(EditorMode.Simple), isChecked: !advanced),
            }),
            Menus.Sub("テーマ", "Palette", new object[]
            {
                Menus.Item("システムに従う", "ThemeAuto", () => EditorCommands.SetTheme(ThemeMode.System)),
                Menus.Item("ライト", "LightMode", () => EditorCommands.SetTheme(ThemeMode.Light)),
                Menus.Item("ダーク", "DarkMode", () => EditorCommands.SetTheme(ThemeMode.Dark)),
            }),
            Menus.Sub("プレビューの拡大率", "ZoomIn", new object[]
            {
                Zoom("ウィンドウに合わせる", 0), Zoom("25%", 0.25), Zoom("50%", 0.5), Zoom("100%", 1), Zoom("200%", 2),
            }),
        };
        _perfItem = Menus.Item("パフォーマンスチェック", "Info", EditorCommands.TogglePerformanceMode, Shortcuts.First("view.performance"), isChecked: Svc.Performance.Enabled);
        viewItems.Add(_perfItem);
        viewItems.Add(Menus.Item("パフォーマンスの詳細...", null, EditorCommands.ShowPerformanceWindow));
        var extra = Workspace.BuildViewMenu(this).ToList();
        if (extra.Count > 0) { viewItems.Add(Menus.Sep()); viewItems.AddRange(extra); }
        var view = Menus.Sub("表示(_V)", null, viewItems);
        var settings = Menus.Sub("設定(_S)", null, new object[] { Menus.Item("環境設定...", "Settings", () => _ = EditorCommands.PreferencesAsync()) });
        var help = Menus.Sub("ヘルプ(_H)", null, new object[] { Menus.Item("バージョン情報...", "Info", () => _ = EditorCommands.AboutAsync()) });

        var menu = new Menu();
        foreach (var m in new[] { file, edit, view, settings, help }) menu.Items.Add(m);
        return menu;
    }

    // ---------------- ツールバー ----------------

    private Control BuildToolbar()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0) };
        left.Children.AddRange(new Control[]
        {
            new IconButton("NoteAdd", "新規プロジェクト (Ctrl+N)", () => _ = EditorCommands.NewAsync()),
            new IconButton("FolderOpen", "開く (Ctrl+O)", () => _ = EditorCommands.OpenAsync()),
            new IconButton("Save", "保存 (Ctrl+S)", () => _ = EditorCommands.SaveAsync()),
            ShellToolbar.Divider(),
            Track(new IconButton("Undo", "元に戻す (Ctrl+Z)", _session.Undo), () => _session.History.CanUndo),
            Track(new IconButton("Redo", "やり直し (Ctrl+Y)", _session.Redo), () => _session.History.CanRedo),
            ShellToolbar.Divider(),
        });
        left.Children.AddRange(Workspace.BuildToolbar(this));

        _projectInfo.Classes.Add("secondary"); _projectInfo.Classes.Add("mono");
        _proxyStatus.Classes.Add("secondary");
        (_proxyStatus.Parent as Panel)?.Children.Remove(_proxyStatus);
        (_projectInfo.Parent as Panel)?.Children.Remove(_projectInfo);
        (_themeButton.Parent as Panel)?.Children.Remove(_themeButton);
        (_perfToggle.Parent as Panel)?.Children.Remove(_perfToggle);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0) };
        right.Children.Add(_proxyStatus);
        right.Children.Add(_projectInfo);
        right.Children.Add(_perfToggle);
        right.Children.Add(_themeButton);
        right.Children.Add(new IconButton("Settings", "環境設定", () => _ = EditorCommands.PreferencesAsync()));

        var dock = new DockPanel { Height = 28 };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right); dock.Children.Add(left);
        var border = new Border { Child = dock, BorderThickness = new Thickness(0, 1, 0, 1) };
        border.Classes.Add("panel");
        return border;
    }

    // ---------------- 状態の反映 ----------------

    private void UpdateState()
    {
        var p = _session.Project;
        string name = _session.FilePath != null ? L.Name(Path.GetFileName(_session.FilePath)) : L.T("無題");
        Title = $"{name}{(_session.IsDirty ? " *" : "")} - video-builder";
        _projectInfo.Text = $"{p.Settings.Width}x{p.Settings.Height}  {p.Settings.Fps.Value:0.###}fps";
        RefreshEnabled();
        if (_undoItem != null) _undoItem.Header = _session.History.UndoDescription is { } u ? $"元に戻す: {u}" : "元に戻す";
        if (_redoItem != null) _redoItem.Header = _session.History.RedoDescription is { } r ? $"やり直し: {r}" : "やり直し";
    }

    /// <summary>登録したコントロールの「使えるかどうか」を今の状態に合わせる。</summary>
    public void RefreshEnabled()
    {
        foreach (var (c, enabled) in _enablers) c.IsEnabled = enabled();
    }

    private void CycleTheme()
    {
        var next = Svc.Settings.Theme switch { ThemeMode.System => ThemeMode.Light, ThemeMode.Light => ThemeMode.Dark, _ => ThemeMode.System };
        EditorCommands.SetTheme(next);
    }

    private void UpdateThemeIcon()
    {
        var (icon, label) = Svc.Settings.Theme switch
        {
            ThemeMode.Light => ("LightMode", "ライト"),
            ThemeMode.Dark => ("DarkMode", "ダーク"),
            _ => ("ThemeAuto", "システムに従う"),
        };
        _themeButton.Icon = Icons.Get(icon);
        ToolTip.SetTip(_themeButton, $"テーマ: {label} (クリックで切替)");
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed || !_session.IsDirty) return;
        e.Cancel = true;
        if (await EditorCommands.ConfirmDiscardAsync())
        {
            _closingConfirmed = true;
            Close();
        }
    }
}
