using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Views;

/// <summary>タイムライン (拡張編集) ウィンドウの中身。本体 + スクロールバー + シーン切替 + 拡大率。</summary>
public sealed class TimelineView : UserControl
{
    private readonly EditorSession _session = Svc.Session;
    private readonly ScrollBar _h = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollBar _v = new() { Orientation = Orientation.Vertical };
    private readonly ComboBox _scenes = new();
    private readonly MiniSlider _zoom = new() { Minimum = 0, Maximum = 1, Height = 16, VerticalAlignment = VerticalAlignment.Center };
    private bool _syncing;

    public TimelineCanvas Canvas { get; } = new();
    /// <summary>最後にクリックしたレイヤー (貼り付け先に使う)。</summary>
    public int LastLayer { get; private set; }

    private const double MinPpf = 0.05, MaxPpf = 40;

    public TimelineView()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(Canvas);

        _v.Margin = new Thickness(0, TimelineCanvas.RulerHeight, 0, 0);
        Grid.SetColumn(_v, 1); grid.Children.Add(_v);

        var zoomOut = new IconButton("ZoomOut", "縮小", () => Canvas.PixelsPerFrame /= 1.25);
        var zoomIn = new IconButton("ZoomIn", "拡大", () => Canvas.PixelsPerFrame *= 1.25);
        var zoomPanel = new DockPanel { Width = TimelineCanvas.HeaderWidth, Height = 18 };
        DockPanel.SetDock(zoomOut, Dock.Left); DockPanel.SetDock(zoomIn, Dock.Right);
        zoomOut.MinHeight = zoomIn.MinHeight = 18; zoomOut.Padding = zoomIn.Padding = new Thickness(2, 0);
        zoomPanel.Children.Add(zoomOut); zoomPanel.Children.Add(zoomIn); zoomPanel.Children.Add(_zoom);
        ToolTip.SetTip(_zoom, "タイムラインの拡大率 (Ctrl + ホイール)");
        var bottom = new DockPanel();
        DockPanel.SetDock(zoomPanel, Dock.Left);
        bottom.Children.Add(zoomPanel); bottom.Children.Add(_h);
        var bottomBorder = new Border { Child = bottom, BorderThickness = new Thickness(0, 1, 0, 0) };
        bottomBorder.Classes.Add("panel");
        Grid.SetRow(bottomBorder, 1); Grid.SetColumnSpan(bottomBorder, 2); grid.Children.Add(bottomBorder);

        // 左上: シーン切替
        _scenes.Width = TimelineCanvas.HeaderWidth - 1;
        _scenes.Height = TimelineCanvas.RulerHeight - 1; _scenes.MinHeight = 0;
        _scenes.HorizontalAlignment = HorizontalAlignment.Left; _scenes.VerticalAlignment = VerticalAlignment.Top;
        _scenes.BorderThickness = new Thickness(0);
        ToolTip.SetTip(_scenes, "シーンの切替");
        grid.Children.Add(_scenes);
        Content = grid;

        Canvas.ViewChanged += SyncBars;
        _h.ValueChanged += (_, _) => { if (!_syncing) Canvas.ScrollX = _h.Value; };
        _v.ValueChanged += (_, _) => { if (!_syncing) Canvas.ScrollY = _v.Value; };
        _zoom.Changed += v => { if (!_syncing) Canvas.PixelsPerFrame = MinPpf * Math.Pow(MaxPpf / MinPpf, v); };
        Canvas.MenuRequested += OnContext;
        Canvas.AddHandler(PointerPressedEvent, (_, e) => LastLayer = Canvas.HitTest(e.GetPosition(Canvas)).Layer, handledEventsToo: true);

        _session.ProjectChanged += SyncScenes;
        _session.SceneChanged += SyncScenes;
        _scenes.SelectionChanged += (_, _) =>
        {
            if (_syncing || _scenes.SelectedIndex < 0) return;
            if (_scenes.SelectedIndex >= _session.Project.Scenes.Count) { _session.AddScene(); return; }
            _session.SwitchScene(_session.Project.Scenes[_scenes.SelectedIndex].Id);
        };
        SyncScenes();
        SyncBars();
    }

    private void SyncBars()
    {
        _syncing = true;
        _h.Maximum = Math.Max(0, Canvas.ExtentWidth - Canvas.ViewportWidth);
        _h.ViewportSize = Canvas.ViewportWidth; _h.LargeChange = Canvas.ViewportWidth * 0.8; _h.SmallChange = 40;
        _h.Value = Canvas.ScrollX;
        _v.Maximum = Math.Max(0, Canvas.ExtentHeight - Canvas.ViewportHeight);
        _v.ViewportSize = Canvas.ViewportHeight; _v.LargeChange = Canvas.ViewportHeight * 0.8; _v.SmallChange = Canvas.LayerHeight;
        _v.Value = Canvas.ScrollY;
        _zoom.Value = Math.Clamp(Math.Log(Canvas.PixelsPerFrame / MinPpf) / Math.Log(MaxPpf / MinPpf), 0, 1);
        _syncing = false;
    }

    private void SyncScenes()
    {
        _syncing = true;
        var names = _session.Project.Scenes.Select(s => s.Name).Append("+ シーンを追加").ToList();
        if (_scenes.ItemsSource is not List<string> old || !old.SequenceEqual(names)) _scenes.ItemsSource = names;
        _scenes.SelectedIndex = _session.Project.Scenes.IndexOf(_session.Scene);
        _syncing = false;
    }

    // ---------------- 右クリックメニュー ----------------

    private void OnContext(TimelineHit hit, PointerEventArgs e)
    {
        var menu = new ContextMenu();
        switch (hit.Area)
        {
            case TimelineArea.Header: BuildLayerMenu(menu, hit.Layer); break;
            case TimelineArea.Content when hit.Object != null: BuildObjectMenu(menu, hit); break;
            case TimelineArea.Content: BuildEmptyMenu(menu, hit); break;
            default: return;
        }
        menu.Open(Canvas);
    }

    private void BuildEmptyMenu(ContextMenu menu, TimelineHit hit)
    {
        int len = _session.Project.SecondsToFrames(Svc.Settings.DefaultObjectSeconds);
        var media = new List<object>();
        foreach (var def in EffectRegistry.MediaObjects)
        {
            if (def.Id == EffectRegistry.GroupControl) media.Add(Menus.Sep());
            bool file = def.Id is EffectRegistry.Video or EffectRegistry.Image or EffectRegistry.Audio;
            media.Add(Menus.Item(def.Name + (file ? "..." : ""), def.Icon, () =>
            {
                if (file) _ = EditorCommands.ImportFilesAsync(hit.Layer, hit.Frame, def.Params.First(p => p.Kind == ParamKind.File).FileExtensions, def.Name);
                else _session.AddObject(def.Id, hit.Layer, hit.Frame, len);
            }));
        }
        menu.Items.Add(Menus.Sub("メディアオブジェクトの追加", "AddBox", media));
        menu.Items.Add(Menus.Sub("フィルタオブジェクトの追加", "Filter",
            EffectRegistry.Filters.Select(f => (object)Menus.Item(f.Name, f.Icon, () => _session.AddObject(f.Id, hit.Layer, hit.Frame, len)))));
        menu.Items.Add(Menus.Sep());
        menu.Items.Add(Menus.Item("貼り付け", "Paste", () => _session.Paste(hit.Layer, hit.Frame), "Ctrl+V", _session.HasClipboard));
        menu.Items.Add(Menus.Sep());
        menu.Items.Add(Menus.Item("ここへ現在位置を移動", "ChevronRight", () => _session.CurrentFrame = hit.Frame));
        menu.Items.Add(Menus.Item("スナップ", "Snap", () => { Svc.Settings.Snap = !Svc.Settings.Snap; Svc.Settings.Save(); }, isChecked: Svc.Settings.Snap));
    }

    private void BuildObjectMenu(ContextMenu menu, TimelineHit hit)
    {
        var o = hit.Object!;
        bool inside = hit.Frame > o.Start && hit.Frame <= o.End;
        menu.Items.Add(Menus.Item("中間点を追加", "AddDiamond", () => { _session.Select(o.Id); _session.AddKeyFrameToSelected(hit.Frame); }, null, inside && !o.KeyFrames.Contains(hit.Frame)));
        if (hit.Part == ObjectPart.KeyFrame)
            menu.Items.Add(Menus.Item("中間点を削除", "Remove", () => _session.RemoveKeyFrame(o, hit.KeyIndex)));
        menu.Items.Add(Menus.Item("分割", "Split", () => _session.SplitSelectedAt(hit.Frame), null, inside));
        menu.Items.Add(Menus.Sep());
        menu.Items.Add(Menus.Item("切り取り", "Cut", _session.CutSelected, "Ctrl+X"));
        menu.Items.Add(Menus.Item("コピー", "Copy", _session.CopySelected, "Ctrl+C"));
        menu.Items.Add(Menus.Item("削除", "Delete", _session.DeleteSelected, "Delete"));
        menu.Items.Add(Menus.Sep());
        menu.Items.Add(Menus.Sub("フィルタ効果の追加", "AddBox",
            EffectRegistry.Filters.Select(f => (object)Menus.Item(f.Name, f.Icon, () => _session.AddFilter(o, f.Id)))));
        menu.Items.Add(Menus.Item("オブジェクトを有効にする", null, () =>
        {
            var targets = _session.WithGroup(_session.SelectedObjects.ToList());
            bool enable = !o.Enabled;
            _session.Edit("有効 / 無効の切替", () => { foreach (var t in targets) t.Enabled = enable; });
        }, isChecked: o.Enabled));
        if (o.GroupId != null)
            menu.Items.Add(Menus.Item("連結を解除", "Link", () => _session.Edit("連結の解除", () =>
            {
                foreach (var t in _session.WithGroup(new[] { o })) t.GroupId = null;
            })));
        menu.Items.Add(Menus.Sep());
        menu.Items.Add(Menus.Item("設定ダイアログを表示", "Tune", () => EditorCommands.ShowSettings()));
    }

    private void BuildLayerMenu(ContextMenu menu, int layer)
    {
        var info = _session.Scene.GetLayer(layer);
        menu.Items.Add(Menus.Item("レイヤーを表示", "Visibility", () => _session.SetLayerVisible(layer, !info.Visible), isChecked: info.Visible));
        menu.Items.Add(Menus.Item("レイヤーをロック", "Lock", () => _session.SetLayerLocked(layer, !info.Locked), isChecked: info.Locked));
        menu.Items.Add(Menus.Sep());
        menu.Items.Add(Menus.Item("すべてのレイヤーを表示", null, () => _session.Edit("すべてのレイヤーを表示", () =>
        {
            foreach (var l in _session.Scene.Layers.Values) l.Visible = true;
        })));
        menu.Items.Add(Menus.Item("ほかのレイヤーを非表示", null, () => _session.Edit("ほかのレイヤーを非表示", () =>
        {
            foreach (var l in _session.Scene.Objects.Select(o => o.Layer).Distinct().ToList())
                _session.Scene.EditLayer(l).Visible = l == layer;
            _session.Scene.EditLayer(layer).Visible = true;
        })));
    }
}
