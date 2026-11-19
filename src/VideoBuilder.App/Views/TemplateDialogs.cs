using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Views;

/// <summary>テンプレートとして保存するときの設定 (名前・カテゴリ・置き方・伸縮・公開する項目)。</summary>
public sealed class TemplateSaveDialog : DialogBase
{
    public TemplateOptions? Result { get; private set; }

    private sealed record Candidate(CheckBox Check, TextBox Label, string ObjectId, int Effect, string Key);

    public TemplateSaveDialog(Project project, IReadOnlyList<TimelineObject> targets, string suggestedName, IReadOnlyList<string> categories)
        : base("テンプレートとして保存", 560)
    {
        // シーンオブジェクト 1 つから作るときは、その中身が本体になる
        var single = targets.Count == 1 && targets[0].Main.DefinitionId == EffectRegistry.SceneRef
            ? project.FindScene(targets[0].Main.GetString("scene")) : null;
        var objects = single?.Objects ?? targets.ToList();

        var name = new TextBox { Text = single?.Name ?? suggestedName };
        var category = new AutoCompleteBox { ItemsSource = categories, FilterMode = AutoCompleteFilterMode.StartsWith, MinimumPrefixLength = 0, Watermark = "(なし)", ItemTemplate = Menus.VerbatimItems(TemplateStore.LegacyCategory) };
        var description = new TextBox { Watermark = "(任意)" };
        var asScene = new RadioButton { Content = "まとめて置く (シーンオブジェクト 1 つ。公開した項目だけを編集する)", GroupName = "insertAs", IsChecked = true };
        var asObjects = new RadioButton { Content = "展開して置く (オブジェクトをそのまま並べる)", GroupName = "insertAs" };
        double intro0 = 0, outro0 = 0;
        if (single != null && targets[0].Main.GetBool("stretch"))
        {
            intro0 = targets[0].Main.Eval("intro", targets[0].Bounds, targets[0].Start);
            outro0 = targets[0].Main.Eval("outro", targets[0].Bounds, targets[0].Start);
        }
        var intro = new NumberBox { Minimum = 0, Maximum = 3600, Decimals = 2, Value = intro0, Width = 80 };
        var outro = new NumberBox { Minimum = 0, Maximum = 3600, Decimals = 2, Value = outro0, Width = 80 };

        Body.Children.Add(Row("名前", name));
        Body.Children.Add(Row("カテゴリ", category));
        Body.Children.Add(Row("説明", description));
        Body.Children.Add(Heading("置き方"));
        Body.Children.Add(asScene);
        Body.Children.Add(asObjects);
        Body.Children.Add(Row("固定する先頭 (秒)", intro, 180));
        Body.Children.Add(Row("固定する末尾 (秒)", outro, 180));
        var stretchNote = new TextBlock { Text = "まとめて置いたテンプレートの長さを変えたとき、先頭と末尾 (入りと抜けの動き) はそのままにして、中間だけを伸び縮みさせます。", TextWrapping = TextWrapping.Wrap };
        stretchNote.Classes.Add("secondary");
        Body.Children.Add(stretchNote);

        Body.Children.Add(Heading("公開する項目"));
        var list = new StackPanel { Spacing = 2 };
        var candidates = new List<Candidate>();
        var rows = new List<(bool Suggested, Control Row)>();
        var usedLabels = new HashSet<string>();
        foreach (var o in objects)
        {
            for (int ei = 0; ei < o.Effects.Count; ei++)
            {
                var e = o.Effects[ei];
                if (!EffectRegistry.Exists(e.DefinitionId)) continue;
                foreach (var p in e.Definition.Params)
                {
                    if (p.Key == "scene") continue;
                    // 一覧が長くなりすぎないよう、トラックバーはオブジェクト本体 (先頭のエフェクト) のものだけを出す
                    if (p.Kind == ParamKind.Track && ei != 0) continue;
                    // ベクター画像の「色 n」は、Lottie にその番号の色があるものだけを出す
                    if (e.DefinitionId == EffectRegistry.Vector && VectorColors.SlotOf(p.Key) is var cs and >= 0
                        && cs >= VectorColors.PaletteOfFile(e.GetString("file")).Count) continue;
                    var existing = single?.Exposed?.FirstOrDefault(x => x.ObjectId == o.Id && x.Effect == ei && x.Key == p.Key);
                    bool suggested = existing != null || (single == null && ei == 0 &&
                        ((o.Main.DefinitionId == EffectRegistry.Text && p.Key == "text") || (p.Kind == ParamKind.File && o.Main.DefinitionId != EffectRegistry.Audio)));
                    // 表示名は利用者が付ける名前として保存するので、表示している言語の名前を最初に入れておく
                    string baseLabel = L.T(p.Name), label = existing?.Label ?? baseLabel;
                    if (existing == null) for (int n = 2; usedLabels.Contains(label); n++) label = $"{baseLabel} {n}";
                    if (suggested) usedLabels.Add(label);
                    var check = new CheckBox { IsChecked = suggested, VerticalAlignment = VerticalAlignment.Center };
                    var labelBox = new TextBox { Text = label, Width = 160, IsEnabled = suggested };
                    check.IsCheckedChanged += (_, _) => labelBox.IsEnabled = check.IsChecked == true;
                    // オブジェクトの表示名 (テキストの本文など) を含むので、組み立てた文字はもう訳さない
                    var where = L.Verbatim($"{TimelineCanvas.ObjectLabel(o)} › {L.T(e.Definition.Name)} › {L.T(p.Name)}");
                    where.VerticalAlignment = VerticalAlignment.Center; where.TextTrimming = TextTrimming.CharacterEllipsis; where.Margin = new Thickness(8, 0, 0, 0);
                    where.Classes.Add("secondary");
                    var row = new DockPanel { Children = { check, labelBox, where } };
                    DockPanel.SetDock(check, Dock.Left); DockPanel.SetDock(labelBox, Dock.Left);
                    rows.Add((suggested, row));
                    candidates.Add(new Candidate(check, labelBox, o.Id, ei, p.Key));
                }
            }
        }
        // 最初から選んだ項目を上に出す (一覧が長いと埋もれるため)
        foreach (var (_, row) in rows.Where(r => r.Suggested).Concat(rows.Where(r => !r.Suggested))) list.Children.Add(row);
        Body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var exposeNote = new TextBlock { Text = "まとめて置いたときに、プロパティの「テンプレートの項目」に出す項目です。あとから、シーンの中でパラメータ名を右クリックして変えることもできます。", TextWrapping = TextWrapping.Wrap };
        exposeNote.Classes.Add("secondary");
        Body.Children.Add(exposeNote);

        AddButton("キャンセル", Close);
        AddButton("保存", () =>
        {
            var n = name.Text?.Trim() ?? "";
            if (n.Length == 0) { name.Focus(); return; }
            Result = new TemplateOptions
            {
                Name = n, Category = category.Text?.Trim() ?? "", Description = description.Text?.Trim() ?? "",
                InsertAs = asObjects.IsChecked == true ? TemplateInsertMode.Objects : TemplateInsertMode.Scene,
                IntroSeconds = intro.Value, OutroSeconds = outro.Value,
                Exposed = candidates.Where(c => c.Check.IsChecked == true).Select(c => new ExposedParam
                {
                    ObjectId = c.ObjectId, Effect = c.Effect, Key = c.Key, Label = c.Label.Text?.Trim() ?? "",
                }).ToList(),
            };
            Close();
        }, accent: true);
        Opened += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}

/// <summary>テンプレートの一覧 (サムネイル・カテゴリ・検索)。挿入・削除・読み込み・書き出しができる。</summary>
public sealed class TemplateLibraryDialog : DialogBase
{
    private readonly TextBox _search = new() { Watermark = "名前で検索" };
    private readonly ComboBox _category = new() { MinWidth = 160 };
    private readonly ListBox _list = new() { Height = 360, SelectionMode = SelectionMode.Single };
    // 説明 (利用者が書いたもの) とアプリの文言を混ぜて出すので、アプリの文言はそれぞれ L.T で訳してから入れる
    private readonly TextBlock _detail = L.Verbatim("");
    private readonly List<Button> _needsSelection = new();
    private List<TemplateEntry> _shown = new();
    private readonly Dictionary<string, Bitmap?> _thumbs = new();

    public TemplateLibraryDialog() : base("テンプレートライブラリ", 760)
    {
        _list.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel());
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _detail.TextWrapping = TextWrapping.Wrap; _detail.MinHeight = 36;
        _detail.Classes.Add("secondary");
        _category.ItemTemplate = Menus.VerbatimItems(AllCategories, NoCategory, TemplateStore.LegacyCategory);
        var searchIcon = new Icon("Search") { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var top = new DockPanel();
        DockPanel.SetDock(searchIcon, Dock.Left);
        DockPanel.SetDock(_category, Dock.Right);
        _category.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(searchIcon);
        top.Children.Add(_category);
        top.Children.Add(_search);
        Body.Children.Add(top);
        Body.Children.Add(_list);
        Body.Children.Add(_detail);

        _search.TextChanged += (_, _) => Fill();
        _category.SelectionChanged += (_, _) => Fill();
        _list.SelectionChanged += (_, _) => ShowDetail();
        _list.DoubleTapped += (_, _) => InsertSelected(null);

        AddButton("読み込み...", () => _ = ImportAsync(), icon: "Upload");
        _needsSelection.Add(AddButton("書き出し...", () => _ = ExportAsync(), icon: "Download"));
        _needsSelection.Add(AddButton("削除", () => _ = DeleteAsync(), icon: "Delete"));
        AddButton("閉じる", Close);
        _needsSelection.Add(AddButton("展開して挿入", () => InsertSelected(TemplateInsertMode.Objects)));
        _needsSelection.Add(AddButton("挿入", () => InsertSelected(null), accent: true));

        Reload();
        Opened += (_, _) => _search.Focus();
    }

    private const string AllCategories = "すべてのカテゴリ";
    private const string NoCategory = "(カテゴリなし)";

    private void Reload()
    {
        var cats = Svc.Templates.Categories().ToList();
        if (Svc.Templates.List().Any(e => e.Category.Length == 0)) cats.Add(NoCategory);
        var selected = _category.SelectedItem as string;
        _category.ItemsSource = new[] { AllCategories }.Concat(cats).ToList();
        _category.SelectedItem = selected != null && cats.Contains(selected) ? selected : AllCategories;
        Fill();
    }

    private void Fill()
    {
        var query = _search.Text?.Trim() ?? "";
        var cat = _category.SelectedItem as string ?? AllCategories;
        _shown = Svc.Templates.List()
            .Where(e => cat == AllCategories || (cat == NoCategory ? e.Category.Length == 0 : e.Category == cat))
            .Where(e => query.Length == 0 || e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || e.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        _list.ItemsSource = _shown.Select(Tile).ToList();
        if (_shown.Count > 0) _list.SelectedIndex = 0;
        ShowDetail();
    }

    private Control Tile(TemplateEntry e)
    {
        if (!_thumbs.TryGetValue(e.Path, out var bmp))
        {
            var png = Svc.Templates.Thumbnail(e);
            try { bmp = png == null ? null : new Bitmap(new MemoryStream(png)); } catch { bmp = null; }
            _thumbs[e.Path] = bmp;
        }
        Control picture = bmp != null
            ? new Image { Source = bmp, Stretch = Stretch.Uniform, Width = 160, Height = 90 }
            : new Border { Width = 160, Height = 90, Child = new Icon(e.Legacy ? "Layers" : "Template", 32) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var frame = new Border { Child = picture, BorderThickness = new Thickness(1) };
        frame[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Border.Default");
        frame[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Bg.Base");
        // 名前は利用者が付けたものなので訳さない (旧形式のプリセットの名前も同じ)
        var label = L.Verbatim(e.Name);
        label.Width = 160; label.TextTrimming = TextTrimming.CharacterEllipsis; label.Margin = new Thickness(0, 4, 0, 0);
        var panel = new StackPanel { Margin = new Thickness(4), Children = { frame, label } };
        ToolTip.SetTip(panel, L.Verbatim(e.Description.Length > 0 ? $"{e.Name}\n{e.Description}" : e.Name));
        return panel;
    }

    private TemplateEntry? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count ? _shown[_list.SelectedIndex] : null;

    private void ShowDetail()
    {
        var e = Selected;
        foreach (var b in _needsSelection) b.IsEnabled = e != null;
        if (e == null) { _detail.Text = _shown.Count == 0 ? L.T("テンプレートがありません。タイムラインでオブジェクトを選び、右クリックの「テンプレートとして保存...」で作れます。") : ""; return; }
        if (e.Legacy) { _detail.Text = L.T("旧形式のプリセットです (展開して挿入します)。"); return; }
        var meta = TemplatePackage.ReadMeta(e.Path);
        if (meta == null) { _detail.Text = ""; return; }
        var lines = new List<string>();
        if (meta.Description.Length > 0) lines.Add(meta.Description);
        double seconds = Rational.Parse(meta.FrameRate).Value is var fps and > 0 ? meta.Length / fps : 0;
        lines.Add(L.T($"長さ {seconds:0.##} 秒 ・ {meta.Width} x {meta.Height} で作成") +
                  (meta.InsertAs == TemplateInsertMode.Scene ? L.T(" ・ まとめて置く") : L.T(" ・ 展開して置く")));
        var missing = TemplateService.MissingFonts(meta.Fonts);
        if (missing.Count > 0) lines.Add(L.T($"見つからないフォント: {string.Join(", ", missing)}"));
        var plugins = meta.Plugins.Where(p => !EffectRegistry.Exists(p)).ToList();
        if (plugins.Count > 0) lines.Add(L.T($"見つからないプラグイン: {string.Join(", ", plugins)}"));
        _detail.Text = string.Join("\n", lines);
    }

    private void InsertSelected(TemplateInsertMode? mode)
    {
        var e = Selected;
        if (e == null) return;
        var session = Svc.Session;
        int layer = session.Primary?.Layer ?? 0;
        var (inserted, message) = TemplateService.Insert(e, layer, session.CurrentFrame, mode);
        if (message != null) _ = MessageDialog.Show(this, "テンプレートライブラリ", message);
        else if (inserted) Close();
    }

    private static readonly FilePickerFileType TemplateType = new("video-builder テンプレート") { Patterns = new[] { "*" + TemplatePackage.Extension } };

    private async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "テンプレートを読み込む", AllowMultiple = true, FileTypeFilter = new[] { TemplateType, FilePickerFileTypes.All },
        });
        var errors = new List<string>();
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (path == null) continue;
            try { Svc.Templates.Import(path); }
            catch (Exception ex) { errors.Add($"{L.Name(Path.GetFileName(path))}: {ex.Message}"); }
        }
        Reload();
        if (errors.Count > 0) await MessageDialog.Show(this, "テンプレートを読み込む", string.Join("\n", errors));
    }

    private async Task ExportAsync()
    {
        var e = Selected;
        if (e == null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "テンプレートを書き出す", DefaultExtension = TemplatePackage.Extension.TrimStart('.'),
            SuggestedFileName = AliasStore.SafeName(e.Name) + TemplatePackage.Extension, FileTypeChoices = new[] { TemplateType },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            if (e.Legacy) Svc.Templates.Load(e).Save(path);
            else File.Copy(e.Path, path, overwrite: true);
        }
        catch (Exception ex) { await MessageDialog.Show(this, "テンプレートを書き出す", ex.Message); }
    }

    private async Task DeleteAsync()
    {
        var e = Selected;
        if (e == null) return;
        if (await MessageDialog.Show(this, "テンプレートの削除", $"「{L.Name(e.Name)}」を削除しますか?", ("キャンセル", false), ("削除", true)) != "削除") return;
        try { Svc.Templates.Delete(e); }
        catch (Exception ex) { await MessageDialog.Show(this, "テンプレートの削除", ex.Message); }
        _thumbs.Remove(e.Path);
        Reload();
    }
}

/// <summary>シーンの公開項目の編集: 表示名・グループ・並び順・削除。</summary>
public sealed class ExposedItemsDialog : DialogBase
{
    /// <summary>OK を押したときの並び (同じグループの項目をまとめる前)。キャンセルなら null。</summary>
    public List<ExposedParam>? Result { get; private set; }

    private readonly Scene _scene;
    private readonly List<ExposedParam> _items;
    private readonly StackPanel _rows = new() { Spacing = 4 };

    public ExposedItemsDialog(Scene scene) : base("テンプレートの項目を編集", 640)
    {
        _scene = scene;
        _items = (scene.Exposed ?? new()).Select(x => x.Clone()).ToList();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("160,140,*,Auto") };
        void Col(string text, int col)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(2, 0) };
            t.Classes.Add("secondary");
            Grid.SetColumn(t, col);
            header.Children.Add(t);
        }
        Col("表示名", 0); Col("グループ", 1); Col("パラメータ", 2);
        Body.Children.Add(header);
        Body.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 360, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var note = new TextBlock { Text = "同じグループの項目は、プロパティでまとめて小見出しの下に並びます (グループの順は、それぞれの最初の項目の順)。", TextWrapping = TextWrapping.Wrap };
        note.Classes.Add("secondary");
        Body.Children.Add(note);
        Fill();

        AddButton("キャンセル", Close);
        AddButton("OK", () => { Result = _items.Select(x => x.Clone()).ToList(); Close(); }, accent: true);
    }

    private void Fill()
    {
        _rows.Children.Clear();
        var groups = _items.Select(x => x.Group).Where(g => g.Length > 0).Distinct().ToList();
        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            var item = _items[i];
            var label = new TextBox { Text = item.Label, Margin = new Thickness(0, 0, 4, 0) };
            label.TextChanged += (_, _) => item.Label = label.Text?.Trim() ?? "";
            var group = new AutoCompleteBox { Text = item.Group, ItemsSource = groups, FilterMode = AutoCompleteFilterMode.StartsWith, MinimumPrefixLength = 0, Watermark = "(なし)", Margin = new Thickness(0, 0, 4, 0), ItemTemplate = Menus.VerbatimItems() };
            group.TextChanged += (_, _) => item.Group = group.Text?.Trim() ?? "";
            var where = L.Verbatim(Describe(item));
            where.VerticalAlignment = VerticalAlignment.Center; where.TextTrimming = TextTrimming.CharacterEllipsis; where.Margin = new Thickness(4, 0);
            where.Classes.Add("secondary");
            ToolTip.SetTip(where, L.Verbatim(where.Text ?? ""));
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new IconButton("ArrowUp", "上へ", () => Move(index, -1)) { IsEnabled = index > 0 },
                    new IconButton("ArrowDown", "下へ", () => Move(index, 1)) { IsEnabled = index < _items.Count - 1 },
                    new IconButton("Delete", "公開をやめる", () => { _items.RemoveAt(index); Fill(); }),
                },
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("160,140,*,Auto") };
            Grid.SetColumn(group, 1); Grid.SetColumn(where, 2); Grid.SetColumn(buttons, 3);
            row.Children.Add(label); row.Children.Add(group); row.Children.Add(where); row.Children.Add(buttons);
            _rows.Children.Add(row);
        }
        if (_items.Count == 0)
        {
            var empty = new TextBlock { Text = "公開している項目はありません。" };
            empty.Classes.Add("secondary");
            _rows.Children.Add(empty);
        }
    }

    private void Move(int index, int direction)
    {
        int j = index + direction;
        if (j < 0 || j >= _items.Count) return;
        (_items[index], _items[j]) = (_items[j], _items[index]);
        Fill();
    }

    private string Describe(ExposedParam x) =>
        x.Resolve(_scene) is { } r
            ? $"{TimelineCanvas.ObjectLabel(r.Object)} › {L.T(r.Effect.Definition.Name)} › {L.T(r.Param.Name)}"
            : L.T("(見つかりません)");
}
