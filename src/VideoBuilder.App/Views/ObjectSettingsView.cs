using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Rendering;
using Effect = VideoBuilder.Core.Effect;

namespace VideoBuilder.App.Views;

/// <summary>
/// 設定ダイアログ。選択中オブジェクトの効果を上から並べ、トラックバーなどで値を編集する。
/// 表示する値は、現在位置がある区間 (中間点で区切った範囲) の左右の値。
/// </summary>
public sealed class ObjectSettingsView : UserControl
{
    private readonly EditorSession _session = Svc.Session;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    // 組み立て済みの欄をすべて入れておき、表示するもの以外は隠す。
    // 外したり付け直したりすると、そのたびに部品全体へスタイルを当て直すので遅い (1 回 40ms ほど)
    private readonly Avalonia.Controls.Panel _bodies = new();
    private readonly DockPanel _header = new() { Margin = new Thickness(8, 4) };
    private readonly TextBlock _empty;
    // 今表示している欄の更新処理。組み立て中は、組み立てている欄のリストを指す
    private List<Action> _refreshers = new();
    private List<Action> _frameWatchers = new();
    private readonly List<Action> _headerRefreshers = new();
    private readonly HashSet<string> _collapsed = new();

    /// <summary>
    /// 組み立て済みの欄。エフェクトの並びが同じオブジェクトなら、欄を作り直さずに使い回して値だけを入れ直す
    /// (欄の部品は、値を読むたびに「今選んでいるオブジェクト」を見るので、別のオブジェクトでもそのまま使える)。
    /// 部品を作ってレイアウトし直すと 1 回で 100ms 以上かかり、選択を切り替えるたびにもたついていた。
    /// </summary>
    private sealed record Panel(StackPanel Body, List<Action> Refreshers, List<Action> FrameWatchers) { public long LastUsed; }
    private readonly Dictionary<string, Panel> _panels = new();
    private Panel? _current;
    private long _useCounter;
    private const int MaxPanels = 12;
    private string _signature = "";
    private bool _refreshing;

    public ObjectSettingsView()
    {
        _empty = new TextBlock
        {
            Text = "オブジェクトを選択すると、\nここにプロパティが表示されます", TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _empty.Classes.Add("secondary");

        var headerBorder = new Border { Child = _header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.Classes.Add("panel");
        var scroll = _scroll;
        _scroll.Content = _bodies;
        var dock = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        dock.Children.Add(headerBorder);
        dock.Children.Add(new Avalonia.Controls.Panel { Children = { scroll, _empty } });
        var root = new Border { Child = dock };
        root.Classes.Add("panel");
        Content = root;
        MinWidth = 300;

        _session.SelectionChanged += Sync;
        _session.ProjectChanged += Sync;
        _session.FrameChanged += RefreshValuesSoon;
        _refreshTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(RefreshIntervalMs), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            if (_refreshPending) RefreshValues();
        });
        // 描画が終わるたびに確認するもの (スクリプトのエラー表示)
        Svc.Preview.FrameReady += () => { if (_current != null) foreach (var w in _current.FrameWatchers.ToList()) w(); };
        Sync();
    }

    private TimelineObject? Target => _session.Primary;

    private void Sync()
    {
        var o = Target;
        string sig = o == null ? "" : o.Id + "|" + BodySignature(o);
        if (sig != _signature) { _signature = sig; Rebuild(); }
        else RefreshValues();
    }

    // 現在位置が動くたびに全部の入力欄を更新すると、再生やシーク中の負荷が大きい。
    // そこで更新は 1 秒に 15 回程度までに抑える (最後の位置の値は必ず反映する)
    private const int RefreshIntervalMs = 66;
    private readonly Avalonia.Threading.DispatcherTimer? _refreshTimer;
    private bool _refreshPending;
    private long _lastRefresh;

    private void RefreshValuesSoon()
    {
        if (Environment.TickCount64 - _lastRefresh >= RefreshIntervalMs) { RefreshValues(); return; }
        _refreshPending = true;
        if (_refreshTimer is { IsEnabled: false }) _refreshTimer.Start();
    }

    private void RefreshValues()
    {
        _refreshPending = false;
        _lastRefresh = Environment.TickCount64;
        if (Target == null) return;
        _refreshing = true;
        try
        {
            foreach (var r in _headerRefreshers) r();
            if (_current != null) foreach (var r in _current.Refreshers) r();
        }
        finally { _refreshing = false; }
    }

    // ---------------- 組み立て ----------------

    /// <summary>欄の組み立て方が同じになるオブジェクトどうしで等しくなる文字列。</summary>
    private string BodySignature(TimelineObject o) =>
        string.Join(",", o.Effects.Select(e => e.DefinitionId)) + "|" + _session.Project.Scenes.Count + "|" + ExposedSignature(o);

    // ---------------- テンプレートの項目 ----------------

    /// <summary>シーンオブジェクトの参照先のシーンと、その公開項目。</summary>
    private (Scene Scene, List<ExposedParam> Items)? ExposedOf(TimelineObject? o)
    {
        if (o == null || o.Main.DefinitionId != EffectRegistry.SceneRef) return null;
        var scene = _session.Project.FindScene(o.Main.GetString("scene"));
        return scene?.Exposed is { Count: > 0 } items ? (scene, items) : null;
    }

    /// <summary>公開項目の並び (種類と名前)。同じなら欄を使い回せる。</summary>
    private string ExposedSignature(TimelineObject o)
    {
        if (ExposedOf(o) is not { } x) return "";
        return string.Join(";", x.Items.Select(i => i.Resolve(x.Scene) is { } r ? $"{r.Effect.DefinitionId}/{r.Param.Key}/{i.Label}" : "-"));
    }

    private Slot ExposedSlot(int index)
    {
        (TimelineObject Object, Effect Effect, ParamDefinition Param)? Resolve()
        {
            if (ExposedOf(Target) is not { } x || index >= x.Items.Count) return null;
            return x.Items[index].Resolve(x.Scene);
        }
        int Frame()
        {
            var t = Target;
            if (t == null || ExposedOf(t) is not { } x) return 0;
            return SceneTime.InnerFrame(_session.Project, t, x.Scene, t.Bounds, Math.Clamp(_session.CurrentFrame, t.Start, t.End));
        }
        return new Slot(() => Resolve()?.Object, () => Resolve()?.Effect, Frame, "x" + index);
    }

    private static ParamDefinition Relabel(ParamDefinition p, string name) => new()
    {
        Key = p.Key, Name = name.Length > 0 ? name : p.Name, Kind = p.Kind, Min = p.Min, Max = p.Max, Default = p.Default, Decimals = p.Decimals,
        DefaultText = p.DefaultText, Choices = p.Choices, FileExtensions = p.FileExtensions, SoftMin = p.SoftMin, SoftMax = p.SoftMax,
    };

    /// <summary>シーンオブジェクトのプロパティの先頭に出す「テンプレートの項目」欄。</summary>
    private Control? BuildExposed(TimelineObject o)
    {
        if (ExposedOf(o) is not { } x) return null;
        var rows = new StackPanel { Margin = new Thickness(8, 4, 18, 8), Spacing = 2 };
        for (int i = 0; i < x.Items.Count; i++)
        {
            if (x.Items[i].Resolve(x.Scene) is not { } r) continue;
            rows.Children.Add(BuildParam(ExposedSlot(i), Relabel(r.Param, x.Items[i].Label)));
        }
        var head = new DockPanel { Margin = new Thickness(4, 0, 16, 0), Height = 24 };
        var icon = new Icon("Template", 14) { Margin = new Thickness(26, 0, 6, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = "テンプレートの項目", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headBorder[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Bg.Elevated");
        headBorder[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Border.Default");
        return new StackPanel { Children = { headBorder, rows } };
    }

    /// <summary>
    /// パラメータの行の右クリックで「テンプレートの項目として公開」できるようにする。
    /// 欄は別のシーンのオブジェクトでも使い回すので、メニューの中身は開くときに決める。
    /// </summary>
    private Control Exposable(Control row, int effectIndex, ParamDefinition p)
    {
        if (p.Key == "scene") return row;
        row.ContextRequested += (_, e) =>
        {
            var o = Target;
            if (o == null || _session.Scene == _session.Project.Root) return;
            e.Handled = true;
            var current = _session.FindExposed(o, effectIndex, p.Key);
            var menu = new ContextMenu();
            if (current == null)
                menu.Items.Add(Menus.Item("テンプレートの項目として公開...", "Template", async () =>
                {
                    if (TopLevel.GetTopLevel(this) is not Window w) return;
                    var label = await PromptDialog.Show(w, "テンプレートの項目として公開", "表示名", L.T(p.Name));
                    if (label != null && Target is { } t) _session.SetExposed(t, effectIndex, p.Key, label.Trim());
                }));
            else
            {
                menu.Items.Add(Menus.Item($"公開をやめる ({current.Label})", "Remove", () => { if (Target is { } t) _session.SetExposed(t, effectIndex, p.Key, null); }));
                menu.Items.Add(Menus.Item("表示名を変更...", null, async () =>
                {
                    if (TopLevel.GetTopLevel(this) is not Window w) return;
                    var label = await PromptDialog.Show(w, "表示名を変更", "表示名", current.Label);
                    if (label != null && Target is { } t) _session.SetExposed(t, effectIndex, p.Key, label.Trim());
                }));
            }
            menu.Open(row);
        };
        return row;
    }

    // ---------------- スタイル ----------------

    private void OpenStyleMenu(Control anchor, int effectIndex)
    {
        var e = EffectAt(effectIndex);
        if (e == null || !EffectRegistry.Exists(e.DefinitionId)) return;
        string defId = e.DefinitionId;
        var names = Svc.Styles.List(defId);
        var menu = new ContextMenu();
        menu.Items.Add(Menus.Item("スタイルとして保存...", "Style", () => _ = EditorCommands.SaveStyleAsync(effectIndex)));
        menu.Items.Add(Menus.Sub("スタイルを適用", "Style", names.Count == 0
            ? new object[] { Menus.Item("(保存したスタイルはありません)", null, () => { }, null, false) }
            : names.Select(n => (object)Menus.Item(n, null, () => EditorCommands.ApplyStyle(defId, n)))));
        if (names.Count > 0)
            menu.Items.Add(Menus.Sub("スタイルを削除", "Delete", names.Select(n => (object)Menus.Item(n, null, () => Svc.Styles.Delete(defId, n)))));
        menu.Open(anchor);
    }

    private void Rebuild()
    {
        _header.Children.Clear();
        _headerRefreshers.Clear();
        var o = Target;
        _empty.IsVisible = o == null;
        if (o == null)
        {
            if (_current != null) _current.Body.IsVisible = false;
            _current = null;
            var t = new TextBlock { Text = "プロパティ", VerticalAlignment = VerticalAlignment.Center };
            t.Classes.Add("heading");
            _header.Children.Add(t);
            return;
        }

        var def = o.Definition;
        var add = new IconButton("AddBox", "エフェクトを追加");
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var f in EffectRegistry.FiltersFor(o))
                menu.Items.Add(Menus.Item(f.Name, f.Icon, () => { var t = Target; if (t != null) _session.AddFilter(t, f.Id); }));
            menu.Open(add);
        };
        DockPanel.SetDock(add, Dock.Right);
        _header.Children.Add(add);
        var icon = new Icon(def.Icon) { Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        _header.Children.Add(icon);
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Classes.Add("heading");
        var range = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 11 };
        range.Classes.Add("secondary"); range.Classes.Add("mono");
        DockPanel.SetDock(range, Dock.Right);
        _header.Children.Add(range);
        _header.Children.Add(title);
        _headerRefreshers.Add(() =>
        {
            var t = Target; if (t == null) return;
            title.Text = t.Definition.Name;
            var p = _session.Project;
            range.Text = $"{p.FormatTime(t.Start)} - {p.FormatTime(t.End + 1)}";
        });

        string key = BodySignature(o);
        if (!_panels.TryGetValue(key, out var panel))
        {
            panel = new Panel(new StackPanel(), new List<Action>(), new List<Action>());
            _refreshers = panel.Refreshers; _frameWatchers = panel.FrameWatchers;
            if (BuildExposed(o) is { } exposed) panel.Body.Children.Add(exposed);
            for (int i = 0; i < o.Effects.Count; i++) panel.Body.Children.Add(BuildEffect(o, i));
            if (_panels.Count >= MaxPanels)
            {
                var oldest = _panels.Where(kv => kv.Value != _current).MinBy(kv => kv.Value.LastUsed);
                _panels.Remove(oldest.Key);
                _bodies.Children.Remove(oldest.Value.Body);
            }
            _panels[key] = panel;
            panel.Body.IsVisible = false;
            _bodies.Children.Add(panel.Body);
        }
        panel.LastUsed = ++_useCounter;
        if (_current != null && _current != panel) _current.Body.IsVisible = false;
        _current = panel;
        _frameWatchers = panel.FrameWatchers;
        panel.Body.IsVisible = true;
        RefreshValues();
    }

    private Effect? EffectAt(int index)
    {
        var o = Target;
        return o != null && index < o.Effects.Count ? o.Effects[index] : null;
    }

    /// <summary>
    /// 入力欄が編集する場所。ふつうは選択中のオブジェクトのエフェクトだが、
    /// テンプレートの項目では、シーンオブジェクトの参照先のシーンの中のオブジェクトのエフェクトになる。
    /// Frame はそのオブジェクトから見た現在位置 (トラックバーの区間を決める)。Id は取り消しをまとめる鍵に使う。
    /// </summary>
    private sealed record Slot(Func<TimelineObject?> Owner, Func<Effect?> Effect, Func<int> Frame, string Id);

    private Slot MainSlot(int effectIndex) => new(() => Target, () => EffectAt(effectIndex), () => _session.CurrentFrame, effectIndex.ToString());

    private Control BuildEffect(TimelineObject obj, int index)
    {
        var effect = obj.Effects[index];
        var def = effect.Definition;
        bool isFilter = def.Category is EffectCategory.Filter or EffectCategory.AudioFilter;
        string collapseKey = index + "/" + def.Id;

        var rows = new StackPanel { Margin = new Thickness(8, 4, 18, 8), Spacing = 2 };
        foreach (var p in def.Params) rows.Children.Add(Exposable(BuildParam(MainSlot(index), p), index, p));
        if (def.Id == "filter.script")
        {
            // スクリプトのエラーを入力欄の下に出す
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
            error[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Error");
            rows.Children.Add(error);
            void Show()
            {
                var t = Target;
                var message = t == null ? null : ScriptEffect.ErrorOf(t.Id);
                error.Text = message; error.IsVisible = message != null;
            }
            _refreshers.Add(Show);
            _frameWatchers.Add(Show);
        }
        rows.IsVisible = !_collapsed.Contains(collapseKey);

        var head = new DockPanel { Margin = new Thickness(4, 0, 16, 0), Height = 24 };
        var arrow = new IconButton(rows.IsVisible ? "ArrowDown" : "ArrowRight");
        arrow.Click += (_, _) =>
        {
            rows.IsVisible = !rows.IsVisible;
            if (rows.IsVisible) _collapsed.Remove(collapseKey); else _collapsed.Add(collapseKey);
            arrow.Icon = Icons.Get(rows.IsVisible ? "ArrowDown" : "ArrowRight");
        };
        DockPanel.SetDock(arrow, Dock.Left);
        head.Children.Add(arrow);

        if (isFilter)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            // フィルタオブジェクトの先頭の効果は消せない (オブジェクト自体を消す)
            bool removable = index > 0;
            if (removable)
            {
                buttons.Children.Add(new IconButton("ArrowUp", "上へ", () => { var t = Target; var e = EffectAt(index); if (t != null && e != null) _session.MoveEffect(t, e, -1); }));
                buttons.Children.Add(new IconButton("ArrowDown", "下へ", () => { var t = Target; var e = EffectAt(index); if (t != null && e != null) _session.MoveEffect(t, e, 1); }));
                buttons.Children.Add(new IconButton("Delete", "このエフェクトを削除", () => { var t = Target; var e = EffectAt(index); if (t != null && e != null) _session.RemoveEffect(t, e); }));
            }
            var enabled = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(enabled, "エフェクトの有効 / 無効");
            enabled.IsCheckedChanged += (_, _) =>
            {
                if (_refreshing) return;
                var e = EffectAt(index); if (e == null) return;
                bool v = enabled.IsChecked == true;
                _session.Edit("エフェクトの有効 / 無効", () => e.Enabled = v);
            };
            _refreshers.Add(() => { var e = EffectAt(index); if (e != null) enabled.IsChecked = e.Enabled; });
            buttons.Children.Add(enabled);
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
        }
        var icon = new Icon(def.Icon, 14) { Margin = new Thickness(2, 0, 6, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = def.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });

        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headBorder[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Bg.Elevated");
        headBorder[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Border.Default");
        // 見出しの右クリック: スタイルの保存・適用
        headBorder.ContextRequested += (_, e) => { e.Handled = true; OpenStyleMenu(headBorder, index); };
        ToolTip.SetTip(head.Children.OfType<TextBlock>().First(), "右クリックでスタイルの保存・適用");
        return new StackPanel { Children = { headBorder, rows } };
    }

    private Control BuildParam(Slot slot, ParamDefinition p) => p.Kind switch
    {
        ParamKind.Track => BuildTrack(slot, p),
        ParamKind.Check => BuildCheck(slot, p),
        ParamKind.Choice => Labeled(p.Name, BuildChoice(slot, p)),
        ParamKind.Color => Labeled(p.Name, BuildColor(slot, p)),
        ParamKind.File => Labeled(p.Name, BuildFile(slot, p)),
        ParamKind.Font => Labeled(p.Name, BuildFont(slot, p)),
        ParamKind.MultilineText => BuildMultiline(slot, p),
        _ => p.Key == "scene" ? Labeled(p.Name, BuildScene(slot, p)) : Labeled(p.Name, BuildText(slot, p, false)),
    };

    /// <summary>複数行のテキスト。テンプレートの項目では、名前がわかるよう見出しを付ける。</summary>
    private Control BuildMultiline(Slot slot, ParamDefinition p)
    {
        var box = BuildText(slot, p, true);
        if (!slot.Id.StartsWith('x')) return box;
        var label = new TextBlock { Text = p.Name };
        label.Classes.Add("secondary");
        return new StackPanel { Spacing = 2, Children = { label, box } };
    }

    private static Control Labeled(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("88,*"), MinHeight = 24 };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        text.Classes.Add("secondary");
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.Children.Add(control);
        return grid;
    }

    private void SetValue(Slot slot, string key, string value, string? mergeKey = null)
    {
        var e = slot.Effect(); var o = slot.Owner();
        if (e == null || o == null || e.GetString(key) == value && e.Values.ContainsKey(key)) return;
        _session.Edit("設定の変更", () => e.Values[key] = value, mergeKey == null ? null : $"{o.Id}/{slot.Id}/{key}/{mergeKey}");
    }

    // ---------------- トラックバー ----------------

    private Control BuildTrack(Slot slot, ParamDefinition p)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,56,*,Auto,*,56,Auto"), Height = 24 };
        var left = new NumberBox { Minimum = p.Min, Maximum = p.Max, Decimals = p.Decimals };
        var right = new NumberBox { Minimum = p.Min, Maximum = p.Max, Decimals = p.Decimals };
        var leftSlider = new MiniSlider { Minimum = p.SliderMin, Maximum = p.SliderMax, Margin = new Thickness(4, 0) };
        var rightSlider = new MiniSlider { Minimum = p.SliderMin, Maximum = p.SliderMax, Margin = new Thickness(4, 0) };
        var name = new Button { Content = p.Name, Width = 96, Padding = new Thickness(2, 0), Focusable = false };
        ToolTip.SetTip(name, "クリックで移動方法を選択");

        AnimatedValue? Track() => slot.Effect()?.Tracks.GetValueOrDefault(p.Key);
        int Index(bool isRight)
        {
            var o = slot.Owner()!;
            int section = o.SectionAt(Math.Clamp(slot.Frame(), o.Start, o.End));
            return isRight ? section + 1 : section;
        }
        void Write(bool isRight, double v)
        {
            var o = slot.Owner(); var t = Track(); if (o == null || t == null) return;
            t.EnsureSections(o.Sections);
            t.Values[Index(isRight)] = Math.Round(Math.Clamp(v, p.Min, p.Max), p.Decimals);
        }
        void Commit(bool isRight, double v)
        {
            var o = slot.Owner(); if (o == null || Track() == null) return;
            _session.Edit("パラメータの変更", () => Write(isRight, v), $"{o.Id}/{slot.Id}/{p.Key}/{isRight}");
        }

        left.Committed += v => Commit(false, v);
        right.Committed += v => Commit(true, v);
        foreach (var (slider, isRight) in new[] { (leftSlider, false), (rightSlider, true) })
        {
            slider.DragStarted += () => _session.BeginEdit("パラメータの変更");
            slider.Changed += v => { Write(isRight, v); _session.NotifyChanged(); };
            slider.DragCompleted += () => _session.CommitEdit();
        }

        Control Steps(NumberBox box, bool isRight)
        {
            var dec = new RepeatButton { Content = new Icon("StepLeft", 14), Width = 13, MinWidth = 13, Padding = new Thickness(0), Focusable = false };
            var inc = new RepeatButton { Content = new Icon("StepRight", 14), Width = 13, MinWidth = 13, Padding = new Thickness(0), Focusable = false };
            dec.Classes.Add("flat"); inc.Classes.Add("flat");
            dec.Click += (_, _) => { var t = Track(); if (t != null && slot.Owner() != null) Commit(isRight, t.Values[Index(isRight)] - p.Step); };
            inc.Click += (_, _) => { var t = Track(); if (t != null && slot.Owner() != null) Commit(isRight, t.Values[Index(isRight)] + p.Step); };
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { dec, inc } };
        }
        var leftSteps = Steps(left, false);
        var rightSteps = Steps(right, true);

        name.Click += (_, _) => OpenMovementMenu(name, slot, p);

        grid.Children.Add(leftSteps);
        Grid.SetColumn(left, 1); grid.Children.Add(left);
        Grid.SetColumn(leftSlider, 2); grid.Children.Add(leftSlider);
        Grid.SetColumn(name, 3); grid.Children.Add(name);
        Grid.SetColumn(rightSlider, 4); grid.Children.Add(rightSlider);
        Grid.SetColumn(right, 5); grid.Children.Add(right);
        Grid.SetColumn(rightSteps, 6); grid.Children.Add(rightSteps);

        _refreshers.Add(() =>
        {
            var o = slot.Owner(); var t = Track(); if (o == null || t == null) return;
            t.EnsureSections(o.Sections);
            int section = o.SectionAt(Math.Clamp(slot.Frame(), o.Start, o.End));
            double l = t.Values[section], r = t.Values[section + 1];
            bool moving = t.IsMoving;
            left.Value = l; leftSlider.Value = l;
            right.Value = moving ? r : l; rightSlider.Value = moving ? r : l;
            right.IsEnabled = rightSlider.IsEnabled = rightSteps.IsEnabled = moving;
            // 移動方法を設定したパラメータは、名前のボタンを強調する
            name.Classes.Set("accent", moving);
            string modeName = t.Custom != null ? Interpolators.Find(t.Custom)?.DisplayName ?? $"{t.Custom} (プラグインがありません)" : ModeName(t.Mode);
            ToolTip.SetTip(name, moving ? $"移動方法: {modeName}" : "クリックで移動方法を選択");
        });
        return grid;
    }

    private static readonly (MovementMode Mode, string Name)[] Modes =
    {
        (MovementMode.None, "移動無し"), (MovementMode.Linear, "直線移動"), (MovementMode.Curve, "曲線移動"), (MovementMode.Ease, "加減速移動"),
        (MovementMode.Jump, "瞬間移動"), (MovementMode.IgnoreMid, "キーフレームを無視"), (MovementMode.Delta, "移動量指定"),
        (MovementMode.Random, "ランダム移動"), (MovementMode.Repeat, "反復移動"),
    };

    private static string ModeName(MovementMode mode) => Modes.First(m => m.Mode == mode).Name;

    private void OpenMovementMenu(Control anchor, Slot slot, ParamDefinition p)
    {
        var t = slot.Effect()?.Tracks.GetValueOrDefault(p.Key);
        if (t == null) return;
        void Change(string what, Action<AnimatedValue> action)
        {
            var track = slot.Effect()?.Tracks.GetValueOrDefault(p.Key);
            if (track != null) _session.Edit(what, () => action(track));
        }
        var menu = new ContextMenu();
        foreach (var (mode, label) in Modes)
        {
            var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = t.Custom == null && t.Mode == mode };
            item.Click += (_, _) => Change("移動方法の変更", v => { v.Mode = mode; v.Custom = null; });
            menu.Items.Add(item);
        }
        // プラグインが追加した移動方法
        foreach (var custom in Interpolators.All)
        {
            var item = new MenuItem { Header = custom.DisplayName, ToggleType = MenuItemToggleType.Radio, IsChecked = t.Custom == custom.Id };
            item.Click += (_, _) => Change("移動方法の変更", v => v.Custom = custom.Id);
            menu.Items.Add(item);
        }
        menu.Items.Add(Menus.Sep());
        menu.Items.Add(Menus.Item("加速", null, () => Change("加速の切替", v => v.Accel = !v.Accel), isChecked: t.Accel));
        menu.Items.Add(Menus.Item("減速", null, () => Change("減速の切替", v => v.Decel = !v.Decel), isChecked: t.Decel));
        menu.Items.Add(Menus.Sep());
        menu.Items.Add(Menus.Sub($"間隔 ({t.Interval} フレーム)", null, new[] { 1, 2, 5, 10, 15, 30, 60, 120 }.Select(n =>
            (object)Menus.Item($"{n} フレーム", null, () => Change("間隔の変更", v => v.Interval = n), isChecked: t.Interval == n))));
        menu.Open(anchor);
    }

    // ---------------- その他の入力 ----------------

    private Control BuildCheck(Slot slot, ParamDefinition p)
    {
        var box = new CheckBox { Content = p.Name };
        box.IsCheckedChanged += (_, _) => { if (!_refreshing) SetValue(slot, p.Key, box.IsChecked == true ? "1" : "0"); };
        _refreshers.Add(() => { var e = slot.Effect(); if (e != null) box.IsChecked = e.GetBool(p.Key); });
        return box;
    }

    private Control BuildChoice(Slot slot, ParamDefinition p)
    {
        var combo = new ComboBox { ItemsSource = p.Choices };
        combo.SelectionChanged += (_, _) => { if (!_refreshing && combo.SelectedIndex >= 0) SetValue(slot, p.Key, combo.SelectedIndex.ToString()); };
        _refreshers.Add(() => { var e = slot.Effect(); if (e != null) combo.SelectedIndex = Math.Clamp(e.GetInt(p.Key), 0, p.Choices.Length - 1); });
        return combo;
    }

    private Control BuildText(Slot slot, ParamDefinition p, bool multiline)
    {
        var box = new TextBox();
        if (multiline)
        {
            box.AcceptsReturn = true; box.Height = 88; box.TextWrapping = TextWrapping.Wrap;
            box.VerticalContentAlignment = VerticalAlignment.Top; box.FontSize = 13;
        }
        box.TextChanged += (_, _) => { if (!_refreshing) SetValue(slot, p.Key, box.Text ?? "", "text"); };
        _refreshers.Add(() =>
        {
            var e = slot.Effect();
            if (e != null && box.Text != e.GetString(p.Key)) box.Text = e.GetString(p.Key);
        });
        return box;
    }

    /// <summary>
    /// ベクター画像の「色 n」なら、置き換える元の色 (Lottie のファイルの n 番目の色)。
    /// その番号の色がファイルにない (または Lottie でない) ときは Missing が true。ほかのパラメータでは null。
    /// </summary>
    private static (string? Original, bool Missing)? VectorColor(Effect e, string key)
    {
        if (e.DefinitionId != EffectRegistry.Vector) return null;
        int slot = LottieColors.SlotOf(key);
        if (slot < 0) return null;
        var palette = LottieColors.PaletteOfFile(e.GetString("file"));
        return slot < palette.Count ? (palette[slot], false) : (null, true);
    }

    private Control BuildColor(Slot slot, ParamDefinition p)
    {
        var swatch = new Border { Width = 28, Height = 16, BorderThickness = new Thickness(1) };
        swatch[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Border.Strong");
        var hex = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        hex.Classes.Add("mono");
        var button = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 2),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, hex } },
        };
        var view = new ColorView { IsAlphaEnabled = false, IsAlphaVisible = false, Width = 320 };
        view.ColorChanged += (_, e) =>
        {
            if (_refreshing) return;
            SetValue(slot, p.Key, $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}", "color");
        };
        button.Flyout = new Flyout { Content = view, Placement = PlacementMode.BottomEdgeAlignedLeft };
        // ベクター画像の色: ファイルの色に戻すボタン
        var reset = new IconButton("Reset", "ファイルの色に戻す", flat: false) { Margin = new Thickness(4, 0, 0, 0), IsVisible = false };
        reset.Click += (_, _) => SetValue(slot, p.Key, "");
        var dock = new DockPanel();
        DockPanel.SetDock(reset, Dock.Right);
        dock.Children.Add(reset);
        dock.Children.Add(button);
        _refreshers.Add(() =>
        {
            var e = slot.Effect(); if (e == null) return;
            var text = e.GetString(p.Key);
            var vector = VectorColor(e, p.Key);
            if (vector is { } v)
            {
                // ファイルにない番号の色は出さない (Lottie の色の数だけ並ぶ)。行 (ラベルを含む) ごと隠す
                if (dock.Parent is Control row) row.IsVisible = !v.Missing;
                reset.IsVisible = text.Length > 0;
                if (text.Length == 0 && v.Original != null) text = v.Original;
            }
            if (!Color.TryParse(text, out var c)) c = Colors.White;
            swatch.Background = new SolidColorBrush(c);
            hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}" + (vector != null && e.GetString(p.Key).Length == 0 ? L.T(" (ファイルの色)") : "");
            if (view.Color != c) view.Color = c;
        });
        return dock;
    }

    private Control BuildFile(Slot slot, ParamDefinition p)
    {
        var box = new TextBox { IsReadOnly = true, Watermark = "(ファイル未選択)" };
        var browse = new IconButton("FolderOpen", "ファイルを選択", flat: false) { Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = p.Name,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(p.Name) { Patterns = p.FileExtensions.Select(x => "*." + x).ToArray() },
                    FilePickerFileTypes.All,
                },
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path != null) SetValue(slot, p.Key, path);
        };
        _refreshers.Add(() =>
        {
            var e = slot.Effect(); if (e == null) return;
            var path = e.GetString(p.Key);
            box.Text = Path.GetFileName(path);
            ToolTip.SetTip(box, path.Length == 0 ? null : File.Exists(path) ? path : path + "\n(ファイルが見つかりません)");
            box.Classes.Set("missing", path.Length > 0 && !File.Exists(path));
        });
        var dock = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        dock.Children.Add(browse);
        dock.Children.Add(box);
        return dock;
    }

    private static IReadOnlyList<string>? _fonts;

    private Control BuildFont(Slot slot, ParamDefinition p)
    {
        _fonts ??= new[] { $"(既定: {Generators.DefaultFontFamily})" }.Concat(Generators.FontFamilies).ToList();
        var combo = new ComboBox { ItemsSource = _fonts, MaxDropDownHeight = 360 };
        combo.SelectionChanged += (_, _) =>
        {
            if (_refreshing || combo.SelectedIndex < 0) return;
            SetValue(slot, p.Key, combo.SelectedIndex == 0 ? "" : _fonts[combo.SelectedIndex]);
        };
        _refreshers.Add(() =>
        {
            var e = slot.Effect(); if (e == null) return;
            var f = e.GetString(p.Key);
            int i = f.Length == 0 ? 0 : _fonts.ToList().IndexOf(f);
            combo.SelectedIndex = Math.Max(0, i);
        });
        return combo;
    }

    private Control BuildScene(Slot slot, ParamDefinition p)
    {
        var combo = new ComboBox();
        List<Scene> scenes = new();
        combo.SelectionChanged += (_, _) =>
        {
            if (_refreshing || combo.SelectedIndex < 0 || combo.SelectedIndex >= scenes.Count) return;
            SetValue(slot, p.Key, scenes[combo.SelectedIndex].Id);
        };
        _refreshers.Add(() =>
        {
            var e = slot.Effect(); if (e == null) return;
            // 自分自身のシーンは選ばせない (無限に入れ子になるため)
            scenes = _session.Project.Scenes.Where(s => s.Id != _session.Scene.Id).ToList();
            combo.ItemsSource = scenes.Select(s => s.Name).ToList();
            combo.SelectedIndex = scenes.FindIndex(s => s.Id == e.GetString(p.Key));
            combo.PlaceholderText = scenes.Count == 0 ? "(ほかのシーンがありません)" : "(シーンを選択)";
        });
        return combo;
    }
}
