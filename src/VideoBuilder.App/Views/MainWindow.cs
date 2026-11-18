using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Views;

/// <summary>タイムラインや設定ダイアログを入れる、別ウィンドウ用の枠。閉じても隠すだけ。</summary>
public sealed class ToolWindow : Window
{
    public bool AllowClose { get; set; }

    public ToolWindow(string title, double width, double height)
    {
        Title = title; Width = width; Height = height;
        ShowInTaskbar = false;
        KeyDown += (_, e) => { if (EditorCommands.HandleKey(e, this)) e.Handled = true; };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // このウィンドウだけを閉じようとしたとき (閉じるボタンなど) は、閉じずに隠す。
        // メインウィンドウの終了やアプリの終了に伴うときは、そのまま閉じる。
        // ここで閉じるのを取りやめると、メインウィンドウの終了処理 (保存の確認) まで届かず、終了できなくなる
        // (Avalonia は、持ち主のウィンドウを閉じる前に、子ウィンドウに「閉じてよいか」を問うため)。
        if (!AllowClose && e.CloseReason == WindowCloseReason.WindowClosing) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}

public sealed class MainWindow : Window
{
    private readonly EditorSession _session = Svc.Session;
    private readonly PreviewView _preview = new();
    private readonly ObjectSettingsView _settingsView = new();
    public TimelineView Timeline { get; } = new();

    private readonly Grid _center = new();
    private readonly ContentControl _timelineHost = new(), _settingsHost = new();
    private ToolWindow? _timelineWindow, _settingsWindow;
    private bool _docked = true;
    private bool _closingConfirmed;

    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _frameText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), MinWidth = 64, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _projectInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _proxyStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly MiniSlider _seek = new() { Margin = new Thickness(4, 0) };
    private readonly IconButton _play;
    private readonly List<(Control Control, Func<bool> Enabled)> _enablers = new();
    private MenuItem? _undoItem, _redoItem;
    private readonly IconToggle _snap = new("Snap", "スナップ (Alt を押している間は無効)");
    private readonly IconButton _themeButton;

    public MainWindow()
    {
        EditorCommands.Main = this;
        EditorCommands.RegisterCommands();
        Width = 1360; Height = 860; MinWidth = 640; MinHeight = 360;
        _play = new IconButton("Play.Fill", "再生 / 停止 (Space)", () => Svc.Preview.TogglePlay()) { IconSize = 20 };
        _themeButton = new IconButton("ThemeAuto", "テーマの切替", CycleTheme);

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(_center);
        Content = root;

        var playbar = BuildPlaybar();
        var previewArea = new DockPanel();
        DockPanel.SetDock(playbar, Dock.Bottom);
        previewArea.Children.Add(playbar);
        previewArea.Children.Add(_preview);
        _previewArea = previewArea;

        AddHandler(KeyDownEvent, (_, e) => { if (EditorCommands.HandleKey(e, this)) e.Handled = true; }, RoutingStrategies.Tunnel);
        Timeline.Canvas.ObjectActivated += _ => ShowSettingsPanel();
        Timeline.Canvas.ErrorRaised += m => _ = EditorCommands.ShowErrorAsync(m);

        _session.ProjectChanged += UpdateState;
        _session.SelectionChanged += UpdateState;
        _session.FrameChanged += UpdateTime;
        _session.SceneChanged += UpdateState;
        Svc.Preview.PlayStateChanged += () => _play.Icon = Icons.Get(Svc.Preview.IsPlaying ? "Pause.Fill" : "Play.Fill");
        Svc.Theme.Changed += UpdateThemeIcon;
        _seek.Changed += v => { Svc.Preview.Stop(); _session.CurrentFrame = (int)Math.Round(v); };

        ApplyWindowMode(force: true);
        UpdateState();
        UpdateThemeIcon();
        Closing += OnClosing;
        Svc.Proxies.Changed += () =>
        {
            _proxyStatus.Text = Svc.Proxies.Status;
            ToolTip.SetTip(_proxyStatus, Svc.Proxies.LastError);
        };
        Closed += (_, _) =>
        {
            _xdnd?.Dispose();
            _xdnd = null;
            Svc.Proxies.CancelAll();
            foreach (var w in new[] { _timelineWindow, _settingsWindow }) { if (w != null) { w.AllowClose = true; w.Close(); } }
            Svc.Preview.Dispose();
        };
        Opened += (_, _) =>
        {
            if (!_docked) ShowToolWindows();
            AttachDropTarget(this);
        };
    }

    private readonly Control _previewArea;

    // ---------------- ウィンドウ構成 (分離 / ドッキング) ----------------

    public static bool ResolveDocked()
    {
        switch (Svc.Settings.WindowMode)
        {
            case WindowMode.Docked: return true;
            case WindowMode.Separate: return false;
        }
        // 自動: 1 つのウィンドウにまとめる (一般的な動画編集アプリと同じ)。分けたい人は設定で選ぶ
        return true;
    }

    public void ApplyWindowMode(bool force = false)
    {
        bool docked = ResolveDocked();
        if (!force && docked == _docked) return;
        _docked = docked;

        // いったんすべて外してから、置き直す
        _timelineHost.Content = null; _settingsHost.Content = null;
        if (_timelineWindow != null) _timelineWindow.Content = null;
        if (_settingsWindow != null) _settingsWindow.Content = null;
        _center.Children.Clear();
        _center.RowDefinitions.Clear(); _center.ColumnDefinitions.Clear();
        foreach (var c in new Control[] { _previewArea, _settingsHost, _timelineHost })
            (c.Parent as Panel)?.Children.Remove(c);
        // 別のウィンドウへ移す前に、元のウィンドウで予約済みのレイアウト処理を済ませておく
        // (移動後に古い側のレイアウトが走ると Avalonia が例外を出す)
        UpdateLayout();
        _timelineWindow?.UpdateLayout(); _settingsWindow?.UpdateLayout();

        if (docked)
        {
            _timelineWindow?.Hide(); _settingsWindow?.Hide();
            _center.RowDefinitions = new RowDefinitions("*,4,300");
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,420") };
            top.ColumnDefinitions[0].MinWidth = 200; top.ColumnDefinitions[2].MinWidth = 300;
            top.Children.Add(_previewArea);
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
            _center.Children.Add(_previewArea);
            _timelineWindow ??= new ToolWindow("タイムライン - video-builder", 1100, 340);
            _settingsWindow ??= new ToolWindow("プロパティ - video-builder", 440, 560);
            _timelineWindow.Content = Timeline;
            _settingsWindow.Content = _settingsView;
            if (IsVisible) ShowToolWindows();
        }
    }

    private void ShowToolWindows()
    {
        if (_timelineWindow is { IsVisible: false })
        {
            _timelineWindow.Position = new PixelPoint(Position.X, Position.Y + (int)(Height * RenderScaling) + 40);
            _timelineWindow.Show(this);
            AttachDropTarget(_timelineWindow);
        }
        if (_settingsWindow is { IsVisible: false })
        {
            _settingsWindow.Position = new PixelPoint(Position.X + (int)(Width * RenderScaling) + 12, Position.Y);
            _settingsWindow.Show(this);
            AttachDropTarget(_settingsWindow);
        }
    }

    // ---------------- ほかのアプリからのファイルのドロップ (Linux / X11) ----------------

    private Platform.XdndDropTarget? _xdnd;
    private bool _xdndTried;
    private readonly Dictionary<IntPtr, Window> _dropWindows = new();

    /// <summary>
    /// X11 では Avalonia がドロップを受け取れないので、自前の受け取り口 (XdndDropTarget) をウィンドウに付ける。
    /// Windows・macOS では Avalonia の DragDrop がそのまま使われる。
    /// </summary>
    private void AttachDropTarget(Window window)
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
                    _xdnd.Left += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => Timeline.Canvas.ShowDropTarget(null));
                    _xdnd.Dropped += (w, x, y, files) => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnExternalDrag(w, x, y, files));
                }
            }
        }
        if (_xdnd == null || window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle) return;
        _dropWindows[handle.Handle] = window;
        _xdnd.Attach(handle.Handle);
    }

    /// <summary>X11 からのドラッグ位置・ドロップを、タイムライン上の位置に直して処理する。files が null ならドラッグ中。</summary>
    private void OnExternalDrag(IntPtr windowId, int x, int y, IReadOnlyList<string>? files)
    {
        var canvas = Timeline.Canvas;
        Point? onCanvas = null;
        if (_dropWindows.TryGetValue(windowId, out var window))
        {
            // X11 の位置は物理ピクセルなので、画面の拡大率で割って Avalonia の座標にする
            var p = new Point(x / window.RenderScaling, y / window.RenderScaling);
            if (canvas.IsEffectivelyVisible && window.TranslatePoint(p, canvas) is { } c && new Rect(canvas.Bounds.Size).Contains(c)) onCanvas = c;
        }
        if (files == null) { canvas.ShowDropTarget(onCanvas); return; }
        canvas.ShowDropTarget(null);
        window?.Activate();
        // タイムラインの外 (プレビューなど) に落としたときは、現在位置に置く
        canvas.DropFiles(files, onCanvas);
    }

    public IEnumerable<Window> ToolWindows => new Window?[] { _timelineWindow, _settingsWindow }.Where(w => w is { IsVisible: true })!;

    public void ShowSettingsPanel()
    {
        if (_docked) return;
        ShowToolWindows();
        _settingsWindow?.Activate();
    }

    public void ShowTimelinePanel()
    {
        if (_docked) return;
        ShowToolWindows();
        _timelineWindow?.Activate();
    }

    // ---------------- メニュー ----------------

    private T Track<T>(T control, Func<bool> enabled) where T : Control
    {
        _enablers.Add((control, enabled));
        return control;
    }

    private Menu BuildMenu()
    {
        bool HasSelection() => _session.SelectionIds.Count > 0;
        var recent = new MenuItem { Header = "最近使ったプロジェクト", Icon = new Icon("History") };
        recent.Items.Add(new MenuItem { Header = "(なし)", IsEnabled = false });

        var file = Menus.Sub("ファイル(_F)", null, new object[]
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
            Menus.Item("メディアファイルを読み込む...", "Movie", () => _ = EditorCommands.ImportFilesAsync(Timeline.LastLayer, _session.CurrentFrame)),
            Menus.Item("動画の出力...", "Output", () => _ = EditorCommands.ExportAsync(), Shortcuts.First("file.export")),
            Menus.Sep(),
            Menus.Item("終了", "Exit", Close),
        });
        file.SubmenuOpened += (_, _) =>
        {
            recent.Items.Clear();
            foreach (var path in Svc.Settings.RecentFiles.Where(File.Exists))
                recent.Items.Add(Menus.Item(Path.GetFileName(path), null, async () => { if (await EditorCommands.ConfirmDiscardAsync()) await EditorCommands.OpenPathAsync(path); }));
            if (recent.Items.Count == 0) recent.Items.Add(new MenuItem { Header = "(なし)", IsEnabled = false });
        };

        _undoItem = Track(Menus.Item("元に戻す", "Undo", _session.Undo, Shortcuts.First("edit.undo")), () => _session.History.CanUndo);
        _redoItem = Track(Menus.Item("やり直し", "Redo", _session.Redo, Shortcuts.First("edit.redo")), () => _session.History.CanRedo);
        var edit = Menus.Sub("編集(_E)", null, new object[]
        {
            _undoItem, _redoItem,
            Menus.Sep(),
            Track(Menus.Item("切り取り", "Cut", _session.CutSelected, Shortcuts.First("edit.cut")), HasSelection),
            Track(Menus.Item("コピー", "Copy", _session.CopySelected, Shortcuts.First("edit.copy")), HasSelection),
            Track(Menus.Item("貼り付け", "Paste", EditorCommands.Paste, Shortcuts.First("edit.paste")), () => _session.HasClipboard),
            Track(Menus.Item("削除", "Delete", _session.DeleteSelected, Shortcuts.First("edit.delete")), HasSelection),
            Menus.Sep(),
            Track(Menus.Item("現在位置で分割", "Split", EditorCommands.Split, Shortcuts.First("edit.split")), HasSelection),
            Track(Menus.Item("キーフレームを追加", "AddDiamond", EditorCommands.AddKeyFrame, Shortcuts.First("edit.addKey")), HasSelection),
            Menus.Sep(),
            Menus.Sub("出力範囲", "Crop", new object[]
            {
                Menus.Item("開始を現在位置に", null, EditorCommands.SetRangeStart, Shortcuts.First("range.start")),
                Menus.Item("終了を現在位置に", null, EditorCommands.SetRangeEnd, Shortcuts.First("range.end")),
                Menus.Item("解除", null, EditorCommands.ClearRange, Shortcuts.First("range.clear")),
            }),
            Track(Menus.Item("プリセットとして保存...", "Layers", () => _ = EditorCommands.CreateAliasAsync(), Shortcuts.First("alias.create")), HasSelection),
            Menus.Item("グリッドの設定...", "GridOn", () => _ = EditorCommands.GridSettingsAsync()),
            Menus.Sep(),
            Menus.Item("プロジェクト設定...", "Tune", () => _ = EditorCommands.ProjectSettingsAsync()),
        });

        MenuItem Zoom(string label, double zoom) => Menus.Item(label, null, () => { _preview.Zoom = zoom; _preview.InvalidateVisual(); });
        var view = Menus.Sub("表示(_V)", null, new object[]
        {
            Menus.Sub("テーマ", "Palette", new object[]
            {
                Menus.Item("システムに従う", "ThemeAuto", () => EditorCommands.SetTheme(ThemeMode.System)),
                Menus.Item("ライト", "LightMode", () => EditorCommands.SetTheme(ThemeMode.Light)),
                Menus.Item("ダーク", "DarkMode", () => EditorCommands.SetTheme(ThemeMode.Dark)),
            }),
            Menus.Sub("ウィンドウ", "Dock", new object[]
            {
                Menus.Item("ウィンドウを分ける", "OpenInNew", () => SetWindowMode(WindowMode.Separate)),
                Menus.Item("1 つのウィンドウにまとめる", "Dock", () => SetWindowMode(WindowMode.Docked)),
            }),
            Menus.Sub("プレビューの拡大率", "ZoomIn", new object[]
            {
                Zoom("ウィンドウに合わせる", 0), Zoom("25%", 0.25), Zoom("50%", 0.5), Zoom("100%", 1), Zoom("200%", 2),
            }),
            Menus.Sep(),
            Menus.Item("タイムラインを表示", "Layers", ShowTimelinePanel),
            Menus.Item("プロパティを表示", "Tune", ShowSettingsPanel),
        });
        var settings = Menus.Sub("設定(_S)", null, new object[] { Menus.Item("環境設定...", "Settings", () => _ = EditorCommands.PreferencesAsync()) });
        var help = Menus.Sub("ヘルプ(_H)", null, new object[] { Menus.Item("バージョン情報...", "Info", () => _ = EditorCommands.AboutAsync()) });

        var menu = new Menu();
        foreach (var m in new[] { file, edit, view, settings, help }) menu.Items.Add(m);
        return menu;
    }

    private void SetWindowMode(WindowMode mode)
    {
        Svc.Settings.WindowMode = mode;
        Svc.Settings.Save();
        ApplyWindowMode();
    }

    // ---------------- ツールバー / 再生バー ----------------

    private static Control Divider() => new Border { Width = 1, Margin = new Thickness(4, 4), [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Border.Default") };

    private Control BuildToolbar()
    {
        bool HasSelection() => _session.SelectionIds.Count > 0;
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0) };
        left.Children.AddRange(new Control[]
        {
            new IconButton("NoteAdd", "新規プロジェクト (Ctrl+N)", () => _ = EditorCommands.NewAsync()),
            new IconButton("FolderOpen", "開く (Ctrl+O)", () => _ = EditorCommands.OpenAsync()),
            new IconButton("Save", "保存 (Ctrl+S)", () => _ = EditorCommands.SaveAsync()),
            Divider(),
            Track(new IconButton("Undo", "元に戻す (Ctrl+Z)", _session.Undo), () => _session.History.CanUndo),
            Track(new IconButton("Redo", "やり直し (Ctrl+Y)", _session.Redo), () => _session.History.CanRedo),
            Divider(),
            Track(new IconButton("Cut", "切り取り (Ctrl+X)", _session.CutSelected), HasSelection),
            Track(new IconButton("Copy", "コピー (Ctrl+C)", _session.CopySelected), HasSelection),
            Track(new IconButton("Paste", "貼り付け (Ctrl+V)", EditorCommands.Paste), () => _session.HasClipboard),
            Track(new IconButton("Delete", "削除 (Delete)", _session.DeleteSelected), HasSelection),
            Divider(),
            Track(new IconButton("Split", "現在位置で分割 (S)", EditorCommands.Split), HasSelection),
            Track(new IconButton("AddDiamond", "キーフレームを追加 (P)", EditorCommands.AddKeyFrame), HasSelection),
            Divider(),
            _snap,
            Divider(),
            new IconButton("Movie", "メディアファイルを読み込む", () => _ = EditorCommands.ImportFilesAsync(Timeline.LastLayer, _session.CurrentFrame)),
            new IconButton("Output", "動画の出力 (Ctrl+E)", () => _ = EditorCommands.ExportAsync()),
        });
        _snap.IsChecked = Svc.Settings.Snap;
        _snap.IsCheckedChanged += (_, _) => { Svc.Settings.Snap = _snap.IsChecked == true; Svc.Settings.Save(); };

        _projectInfo.Classes.Add("secondary"); _projectInfo.Classes.Add("mono");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0) };
        _proxyStatus.Classes.Add("secondary");
        right.Children.Add(_proxyStatus);
        right.Children.Add(_projectInfo);
        right.Children.Add(_themeButton);
        right.Children.Add(new IconButton("Settings", "環境設定", () => _ = EditorCommands.PreferencesAsync()));

        var dock = new DockPanel { Height = 28 };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right); dock.Children.Add(left);
        var border = new Border { Child = dock, BorderThickness = new Thickness(0, 1, 0, 1) };
        border.Classes.Add("panel");
        return border;
    }

    private Control BuildPlaybar()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0) };
        buttons.Children.AddRange(new Control[]
        {
            new IconButton("FirstPage", "先頭へ (Home)", () => EditorCommands.Seek(0)),
            new IconButton("ChevronLeft", "1 フレーム戻る (←)", () => EditorCommands.Step(-1)),
            _play,
            new IconButton("ChevronRight", "1 フレーム進む (→)", () => EditorCommands.Step(1)),
            new IconButton("LastPage", "末尾へ (End)", () => EditorCommands.Seek(_session.SceneLength - 1)),
        });
        _time.Classes.Add("mono"); _frameText.Classes.Add("mono"); _frameText.Classes.Add("secondary");
        var dock = new DockPanel { Height = 28 };
        DockPanel.SetDock(buttons, Dock.Left); DockPanel.SetDock(_time, Dock.Left); DockPanel.SetDock(_frameText, Dock.Right);
        dock.Children.Add(buttons); dock.Children.Add(_time); dock.Children.Add(_frameText); dock.Children.Add(_seek);
        var border = new Border { Child = dock, BorderThickness = new Thickness(0, 1, 0, 0) };
        border.Classes.Add("panel");
        return border;
    }

    // ---------------- 状態の反映 ----------------

    private void UpdateState()
    {
        var p = _session.Project;
        string name = _session.FilePath != null ? Path.GetFileName(_session.FilePath) : L.T("無題");
        Title = $"{name}{(_session.IsDirty ? " *" : "")} - video-builder";
        _projectInfo.Text = $"{p.Settings.Width}x{p.Settings.Height}  {p.Settings.Fps.Value:0.###}fps";
        foreach (var (c, enabled) in _enablers) c.IsEnabled = enabled();
        if (_undoItem != null) _undoItem.Header = _session.History.UndoDescription is { } u ? $"元に戻す: {u}" : "元に戻す";
        if (_redoItem != null) _redoItem.Header = _session.History.RedoDescription is { } r ? $"やり直し: {r}" : "やり直し";
        if (_snap.IsChecked != Svc.Settings.Snap) _snap.IsChecked = Svc.Settings.Snap;
        UpdateTime();
    }

    private void UpdateTime()
    {
        var p = _session.Project;
        int length = _session.SceneLength;
        _time.Text = $"{p.FormatTime(_session.CurrentFrame)} / {p.FormatTime(_session.Scene.Length)}";
        _frameText.Text = $"F {_session.CurrentFrame}";
        _seek.Maximum = Math.Max(1, length - 1);
        _seek.Value = Math.Min(_session.CurrentFrame, _seek.Maximum);
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
