using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

public class TemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-template-{Guid.NewGuid():N}");

    public TemplateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Png(string name, SKColor color)
    {
        var path = Path.Combine(_dir, name);
        using var bmp = new SKBitmap(8, 8);
        bmp.Erase(color);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>テキスト + 画像 + 図形 (連結) の、よくある「名前テロップ」。</summary>
    private (EditorSession Session, TimelineObject Text, TimelineObject Image, TimelineObject Shape) LowerThird()
    {
        var s = new EditorSession();
        var text = s.AddObject(EffectRegistry.Text, 5, 100, 60)!;
        text.Main.Values["text"] = "山田 太郎";
        text.Main.Values["font"] = "存在しないフォント";
        var image = s.AddObject(EffectRegistry.Image, 6, 100, 60)!;
        image.Main.Values["file"] = Png("logo.png", SKColors.Red);
        var shape = s.AddObject(EffectRegistry.Shape, 7, 110, 50)!;
        shape.GroupId = image.GroupId = "g1";
        return (s, text, image, shape);
    }

    [Fact]
    public void Scene_time_keeps_intro_and_outro_and_stretches_the_middle()
    {
        // 長さ 100 のシーンを 200 に伸ばす。先頭 10・末尾 20 は固定
        Assert.Equal(0, SceneTime.Map(0, 200, 100, true, 10, 20));
        Assert.Equal(9, SceneTime.Map(9, 200, 100, true, 10, 20));
        Assert.Equal(10, SceneTime.Map(10, 200, 100, true, 10, 20));
        Assert.Equal(80, SceneTime.Map(180, 200, 100, true, 10, 20));   // 末尾の最初
        Assert.Equal(99, SceneTime.Map(199, 200, 100, true, 10, 20));
        int mid = SceneTime.Map(95, 200, 100, true, 10, 20);
        Assert.InRange(mid, 44, 46);                                      // 中間は 70/170 倍
        // 縮める
        Assert.Equal(99, SceneTime.Map(49, 50, 100, true, 10, 20));
        Assert.Equal(5, SceneTime.Map(5, 50, 100, true, 10, 20));
        // 固定部分がオブジェクトより長いときは縮めて扱う
        Assert.InRange(SceneTime.Map(29, 30, 100, true, 40, 40), 0, 99);
        // 伸縮しなければそのまま
        Assert.Equal(150, SceneTime.Map(150, 200, 100, false, 10, 20));
    }

    [Fact]
    public void Template_round_trips_with_assets_and_is_inserted_as_one_scene_object()
    {
        var (s, text, image, shape) = LowerThird();
        var options = new TemplateOptions
        {
            Name = "名前テロップ", Category = "テロップ", IntroSeconds = 0.5, OutroSeconds = 0.5,
            Exposed = new() { new ExposedParam { ObjectId = text.Id, Effect = 0, Key = "text", Label = "名前" } },
        };
        var pkg = TemplatePackage.Create(s.Project, new[] { text, image, shape }, options);
        Assert.Equal(new[] { "存在しないフォント" }, pkg.Meta.Fonts);
        Assert.Single(pkg.AssetNames);
        Assert.StartsWith(TemplatePackage.AssetPrefix, pkg.Body.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Image).Main.GetString("file"));
        Assert.Equal(60, pkg.Meta.Length);
        Assert.Equal((0, 0), (pkg.Body.Objects.Min(o => o.Layer), pkg.Body.Objects.Min(o => o.Start)));
        pkg.Thumbnail = new byte[] { 1, 2, 3 };

        var store = new TemplateStore(Path.Combine(_dir, "templates"), Path.Combine(_dir, "assets"));
        store.Save(pkg);
        var entry = Assert.Single(store.List());
        Assert.Equal(("名前テロップ", "テロップ", TemplateInsertMode.Scene), (entry.Name, entry.Category, entry.InsertAs));
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Thumbnail(entry));

        // 素材を消しても、同梱したものから使える
        File.Delete(image.Main.GetString("file"));
        int scenesBefore = s.Project.Scenes.Count;
        var inst = store.Instantiate(entry, s.Project, TemplateInsertMode.Scene);
        Assert.Equal(1, s.InsertTemplate(inst, 0, 300));
        Assert.Equal(scenesBefore + 1, s.Project.Scenes.Count);
        var placed = s.Primary!;
        Assert.Equal(EffectRegistry.SceneRef, placed.Main.DefinitionId);
        Assert.Equal((0, 300, 359), (placed.Layer, placed.Start, placed.End));
        Assert.True(placed.Main.GetBool("stretch"));
        Assert.Equal(0.5, placed.Main.Tracks["intro"].Values[0]);

        var scene = s.Project.FindScene(placed.Main.GetString("scene"))!;
        Assert.Equal("名前テロップ", scene.Name);
        Assert.Equal(3, scene.Objects.Count);
        var img = scene.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Image);
        Assert.True(File.Exists(img.Main.GetString("file")));
        Assert.StartsWith(Path.Combine(_dir, "assets"), img.Main.GetString("file"));
        // ID は振り直し、連結と公開項目は新しい ID に付け替わっている
        Assert.DoesNotContain(scene.Objects, o => o.Id == text.Id || o.Id == image.Id);
        var grouped = scene.Objects.Where(o => o.GroupId != null).ToList();
        Assert.Equal(2, grouped.Count);
        Assert.Single(grouped.Select(o => o.GroupId).Distinct());
        Assert.NotEqual("g1", grouped[0].GroupId);
        var exposed = Assert.Single(scene.Exposed!);
        var resolved = exposed.Resolve(scene)!.Value;
        Assert.Equal("山田 太郎", resolved.Effect.GetString("text"));

        // 2 回目は別のシーン・別の名前になり、1 回目と独立して編集できる
        var second = store.Instantiate(entry, s.Project, TemplateInsertMode.Scene);
        Assert.Equal(1, s.InsertTemplate(second, 0, 300));   // 重なるので下のレイヤーに置かれる
        var scene2 = s.Project.FindScene(s.Primary!.Main.GetString("scene"))!;
        Assert.Equal("名前テロップ (2)", scene2.Name);
        Assert.Equal(1, s.Primary!.Layer);
        scene2.Exposed![0].Resolve(scene2)!.Value.Effect.Values["text"] = "佐藤";
        Assert.Equal("山田 太郎", resolved.Effect.GetString("text"));

        // シーンの追加とオブジェクトの追加は 1 回で取り消せる
        s.Undo();
        Assert.Equal(scenesBefore + 1, s.Project.Scenes.Count);
        s.Undo();
        Assert.Equal(scenesBefore, s.Project.Scenes.Count);
        Assert.DoesNotContain(s.Scene.Objects, o => o.Main.DefinitionId == EffectRegistry.SceneRef);
    }

    [Fact]
    public void Template_can_be_expanded_and_nested_scenes_come_along()
    {
        var s = new EditorSession();
        var inner = new Scene { Id = "inner", Name = "中身" };
        inner.Objects.Add(ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 30));
        s.Project.Scenes.Add(inner);
        var sceneObj = s.AddObject(EffectRegistry.SceneRef, 2, 40, 30, o => o.Main.Values["scene"] = "inner")!;
        var text = s.AddObject(EffectRegistry.Text, 3, 40, 30)!;

        var pkg = TemplatePackage.Create(s.Project, new[] { sceneObj, text }, new TemplateOptions { Name = "組み合わせ", InsertAs = TemplateInsertMode.Objects });
        Assert.Equal(2, pkg.Scenes.Count);
        var path = Path.Combine(_dir, "a" + TemplatePackage.Extension);
        pkg.Save(path);
        var loaded = TemplatePackage.Load(path);

        var target = new EditorSession();
        var inst = loaded.Instantiate(target.Project, Path.Combine(_dir, "assets"), TemplateInsertMode.Objects);
        Assert.Equal(2, target.InsertTemplate(inst, 1, 10));
        Assert.Equal(2, target.Project.Scenes.Count);                  // 入れ子のシーンだけが足される
        var placedRef = target.Scene.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.SceneRef);
        Assert.Equal(target.Project.Scenes[1].Id, placedRef.Main.GetString("scene"));
        Assert.Equal("中身", target.Project.Scenes[1].Name);
        Assert.Equal((1, 10), (placedRef.Layer, placedRef.Start));

        // まとめて置けば、本体のシーンも入る (中で入れ子のシーンを参照している)
        var inst2 = loaded.Instantiate(target.Project, Path.Combine(_dir, "assets"), TemplateInsertMode.Scene);
        Assert.Equal(1, target.InsertTemplate(inst2, 0, 100));
        Assert.Equal(4, target.Project.Scenes.Count);
        var body = target.Project.FindScene(target.Primary!.Main.GetString("scene"))!;
        var nestedRef = body.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.SceneRef);
        Assert.NotNull(target.Project.FindScene(nestedRef.Main.GetString("scene")));
        Assert.Equal("中身 (2)", target.Project.FindScene(nestedRef.Main.GetString("scene"))!.Name);
    }

    [Fact]
    public void Template_from_a_single_scene_object_uses_that_scene_and_keeps_its_exposed_params()
    {
        var s = new EditorSession();
        var inner = new Scene { Id = "eyecatch", Name = "アイキャッチ" };
        var t = ObjectFactory.Create(EffectRegistry.Text, 0, 0, 90);
        inner.Objects.Add(t);
        inner.Exposed = new() { new ExposedParam { ObjectId = t.Id, Effect = 0, Key = "color", Label = "文字の色" } };
        s.Project.Scenes.Add(inner);
        var so = s.AddObject(EffectRegistry.SceneRef, 0, 0, 90, o => o.Main.Values["scene"] = "eyecatch")!;
        var pkg = TemplatePackage.Create(s.Project, new[] { so }, new TemplateOptions { Name = "アイキャッチ" });
        Assert.Single(pkg.Scenes);
        Assert.Equal("文字の色", Assert.Single(pkg.Body.Exposed!).Label);
        Assert.Equal(90, pkg.Meta.Length);
    }

    [Fact]
    public void Exposed_params_are_toggled_with_undo()
    {
        var s = new EditorSession();
        var scene = s.AddScene();
        var t = s.AddObject(EffectRegistry.Text, 0, 0, 30)!;
        s.SetExposed(t, 0, "text", "見出し");
        Assert.Equal("見出し", s.FindExposed(t, 0, "text")!.Label);
        s.SetExposed(t, 0, "text", "見出し 2");
        Assert.Single(scene.Exposed!);
        s.SetExposed(t, 0, "text", null);
        Assert.Null(s.Scene.Exposed);
        s.Undo();
        Assert.Equal("見出し 2", s.FindExposed(t, 0, "text")!.Label);
        // 複製 (取り消し履歴・プレビュー用) にも引き継がれる
        Assert.Single(s.SnapshotForRead().FindScene(scene.Id)!.Exposed!);
    }

    [Fact]
    public void Exposed_items_are_reordered_grouped_and_kept_in_templates()
    {
        var s = new EditorSession();
        var scene = s.AddScene();
        var a = s.AddObject(EffectRegistry.Text, 0, 0, 30)!;
        var b = s.AddObject(EffectRegistry.Shape, 1, 0, 30)!;
        s.SetExposed(a, 0, "text", "本文");
        s.SetExposed(a, 0, "color", "文字の色");
        s.SetExposed(b, 0, "color", "帯の色");
        var items = scene.Exposed!.Select(x => x.Clone()).ToList();
        // 「色」グループを作り、本文を最後にする。グループの項目は最初に出てくる位置にまとまる
        items[1].Group = "色"; items[2].Group = "色";
        var reordered = new List<ExposedParam> { items[1], items[0], items[2] };
        s.ReplaceExposed(scene.Id, reordered);
        Assert.Equal(new[] { "文字の色", "帯の色", "本文" }, s.Scene.Exposed!.Select(x => x.Label).ToArray());
        Assert.Equal(new[] { "色", "色", "" }, s.Scene.Exposed!.Select(x => x.Group).ToArray());

        // 1 回で取り消せる
        s.Undo();
        Assert.Equal(new[] { "本文", "文字の色", "帯の色" }, s.Scene.Exposed!.Select(x => x.Label).ToArray());
        s.Redo();

        // テンプレートにしても並びとグループが残る
        s.SwitchScene("root");
        var so = s.AddObject(EffectRegistry.SceneRef, 0, 0, 30, o => o.Main.Values["scene"] = scene.Id)!;
        var pkg = TemplatePackage.Create(s.Project, new[] { so }, new TemplateOptions { Name = "並び" });
        var path = Path.Combine(_dir, "order" + TemplatePackage.Extension);
        pkg.Save(path);
        var inst = TemplatePackage.Load(path).Instantiate(new Project(), Path.Combine(_dir, "assets"), TemplateInsertMode.Scene);
        Assert.Equal(new[] { "色/文字の色", "色/帯の色", "/本文" }, inst.Scenes[0].Exposed!.Select(x => x.Group + "/" + x.Label).ToArray());

        // 全部消すと null になる
        s.ReplaceExposed(scene.Id, Array.Empty<ExposedParam>());
        Assert.Null(s.Project.FindScene(scene.Id)!.Exposed);
        Assert.Equal(new[] { "b", "b", "a", "c" }, ExposedParam.GroupTogether(new[] { "b", "a", "b", "c" }.Select(g => new ExposedParam { Group = g })).Select(x => x.Group).ToArray());
    }

    [Fact]
    public void Shared_folder_templates_are_listed_read_only_and_can_receive_copies()
    {
        var (s, text, _, _) = LowerThird();
        var shared = Path.Combine(_dir, "shared");
        var store = new TemplateStore(Path.Combine(_dir, "templates"), Path.Combine(_dir, "assets")) { SharedDirectory = shared };
        Assert.Empty(store.List());                      // 共有フォルダがまだなくても動く
        store.Save(TemplatePackage.Create(s.Project, new[] { text }, new TemplateOptions { Name = "自分の" }));
        var mine = Assert.Single(store.List());
        Assert.False(mine.Shared);

        store.CopyToShared(mine);
        var all = store.List();
        Assert.Equal(2, all.Count);
        var sharedEntry = all.Single(e => e.Shared);
        Assert.Equal("自分の", sharedEntry.Name);
        Assert.Throws<InvalidOperationException>(() => store.Delete(sharedEntry));
        Assert.Throws<InvalidOperationException>(() => store.CopyToShared(sharedEntry));
        // 共有のものも挿入できる
        Assert.Equal(1, s.InsertTemplate(store.Instantiate(sharedEntry, s.Project, TemplateInsertMode.Scene), 0, 500));

        // 共有フォルダが読めなくても、自分のものは並ぶ
        store.SharedDirectory = Path.Combine(_dir, "does-not-exist");
        Assert.Single(store.List());
        // 共有フォルダが自分のフォルダと同じなら、重ねて並べない
        store.SharedDirectory = Path.Combine(_dir, "templates");
        Assert.Single(store.List());
    }

    [Fact]
    public void Templates_are_scaled_to_the_target_resolution()
    {
        var s = new EditorSession();
        s.Project.Settings.Width = 1920; s.Project.Settings.Height = 1080;
        var t = s.AddObject(EffectRegistry.Text, 0, 0, 60)!;
        t.Draw!.Tracks["x"] = new AnimatedValue(400);
        t.Draw!.Tracks["y"] = new AnimatedValue(-200);
        t.AddKeyFrame(30);
        t.Draw!.Tracks["x"].Values[1] = 800;
        t.Draw!.Tracks["scale"] = new AnimatedValue(100, 2);
        var bg = s.AddObject(EffectRegistry.Shape, 1, 0, 60, o => o.Main.Values["type"] = "0")!;
        var pkg = TemplatePackage.Create(s.Project, new[] { t, bg }, new TemplateOptions { Name = "縮小", InsertAs = TemplateInsertMode.Objects });

        // 縦長 (1080x1920): 幅の比 0.5625 で縮める
        var portrait = new Project { Settings = { Width = 1080, Height = 1920 } };
        Assert.Equal(0.5625, pkg.ResolutionScale(portrait.Settings), 6);
        var inst = pkg.Instantiate(portrait, Path.Combine(_dir, "a"), TemplateInsertMode.Objects, fitResolution: true);
        var text = inst.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Text);
        Assert.Equal(new[] { 225.0, 450.0 }, text.Draw!.Tracks["x"].Values.Take(2).ToArray());
        Assert.Equal(-112.5, text.Draw!.Tracks["y"].Values[0]);
        Assert.Equal(56.25, text.Draw!.Tracks["scale"].Values[0]);
        // 4K: 2 倍
        var uhd = new Project { Settings = { Width = 3840, Height = 2160 } };
        var big = pkg.Instantiate(uhd, Path.Combine(_dir, "a"), TemplateInsertMode.Scene, fitResolution: true);
        var inner = big.Scenes[0].Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Text);
        Assert.Equal(800, inner.Draw!.Tracks["x"].Values[0]);
        // 合わせない・同じ解像度なら変えない
        Assert.Equal(400, pkg.Instantiate(uhd, Path.Combine(_dir, "a"), TemplateInsertMode.Objects).Objects
            .Single(o => o.Main.DefinitionId == EffectRegistry.Text).Draw!.Tracks["x"].Values[0]);
        Assert.Equal(1, pkg.ResolutionScale(new ProjectSettings { Width = 1920, Height = 1080 }));
        Assert.Equal(1, new TemplatePackage().ResolutionScale(uhd.Settings));   // 解像度の分からない古いテンプレート
    }

    [Fact]
    public void Legacy_presets_are_listed_and_expanded()
    {
        var legacyDir = Path.Combine(_dir, "aliases");
        var s = new EditorSession();
        var t = s.AddObject(EffectRegistry.Text, 3, 100, 60)!;
        new AliasStore(legacyDir).Save("古い", new[] { t });
        var store = new TemplateStore(Path.Combine(_dir, "templates"), Path.Combine(_dir, "assets"), legacyDir);
        var entry = Assert.Single(store.List());
        Assert.True(entry.Legacy);
        Assert.Equal(TemplateStore.LegacyCategory, entry.Category);
        var inst = store.Instantiate(entry, s.Project, entry.InsertAs);
        Assert.Equal(1, s.InsertTemplate(inst, 0, 0));
        Assert.Single(s.Project.Scenes);
        store.Delete(entry);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Import_renames_when_the_name_is_taken()
    {
        var (s, text, _, _) = LowerThird();
        var pkg = TemplatePackage.Create(s.Project, new[] { text }, new TemplateOptions { Name = "見出し" });
        var outside = Path.Combine(_dir, "outside" + TemplatePackage.Extension);
        pkg.Save(outside);
        var store = new TemplateStore(Path.Combine(_dir, "templates"), Path.Combine(_dir, "assets"));
        store.Import(outside);
        store.Import(outside);
        Assert.Equal(new[] { "見出し", "見出し (2)" }, store.List().Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Missing_plugins_are_reported_on_insert()
    {
        var s = new EditorSession();
        var t = s.AddObject(EffectRegistry.Text, 0, 0, 30)!;
        t.Effects.Insert(1, new Effect { DefinitionId = "plugin.nothing" });
        var pkg = TemplatePackage.Create(s.Project, new[] { t }, new TemplateOptions { Name = "x" });
        Assert.Equal(new[] { "plugin.nothing" }, pkg.Meta.Plugins);
        var inst = pkg.Instantiate(s.Project, Path.Combine(_dir, "assets"), TemplateInsertMode.Scene);
        Assert.Equal(new[] { "plugin.nothing" }, inst.MissingPlugins);
    }

    [Fact]
    public void Style_copies_the_look_but_not_the_content()
    {
        var s = new EditorSession();
        var a = s.AddObject(EffectRegistry.Text, 0, 0, 30)!;
        a.Main.Values["text"] = "元の文";
        a.Main.Values["color"] = "#FF0000";
        a.Main.Tracks["size"] = new AnimatedValue(120);
        var border = ObjectFactory.AddFilter(a, "filter.border");
        border.Values["color"] = "#00FF00";

        var textStyle = EffectStyle.From(a.Main, "赤い大きな字");
        Assert.False(textStyle.Values.ContainsKey("text"));
        var store = new StyleStore(Path.Combine(_dir, "styles"));
        store.Save(textStyle);
        store.Save(EffectStyle.From(border, "緑の縁"));
        Assert.Equal(new[] { "赤い大きな字" }, store.List(EffectRegistry.Text));

        var b = s.AddObject(EffectRegistry.Text, 1, 0, 30)!;
        b.Main.Values["text"] = "別の文";
        b.AddKeyFrame(10);
        s.Select(b.Id);
        Assert.Equal(1, s.ApplyStyle(store.Load(EffectRegistry.Text, "赤い大きな字")));
        Assert.Equal("#FF0000", b.Main.GetString("color"));
        Assert.Equal("別の文", b.Main.GetString("text"));
        Assert.Equal(new double[] { 120, 120, 120 }, b.Main.Tracks["size"].Values);

        // 持っていないエフェクトのスタイルは、エフェクトを足して当てる
        Assert.Equal(1, s.ApplyStyle(store.Load("filter.border", "緑の縁")));
        Assert.Equal("#00FF00", b.FindEffect("filter.border")!.GetString("color"));
        Assert.Equal(EffectRegistry.StandardDraw, b.Effects[^1].DefinitionId);
        s.Undo();
        Assert.Null(s.Scene.Find(b.Id)!.FindEffect("filter.border"));
    }
}

public class VectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-vector-{Guid.NewGuid():N}");

    public VectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Project Small()
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 240;
        return p;
    }

    private static SKColor Pixel(Project p, int frame, int x, int y)
    {
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, frame);
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void Svg_is_drawn_sharp_at_the_requested_size()
    {
        var svg = Path.Combine(_dir, "box.svg");
        File.WriteAllText(svg, "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='10'><rect width='20' height='10' fill='#0000ff'/></svg>");
        var p = Small();
        var o = ObjectFactory.Create(EffectRegistry.Vector, 0, 0, 30);
        o.Main.Values["file"] = svg;
        p.Root.Objects.Add(o);
        // 元の大きさ (20x10) なら、中心から 30px 離れた所には届かない
        Assert.Equal(new SKColor(0, 0, 255), Pixel(p, 0, 160, 120));
        Assert.NotEqual(new SKColor(0, 0, 255), Pixel(p, 0, 190, 120));
        // 長辺 200px で描く
        o.Main.Tracks["size"] = new AnimatedValue(200);
        Assert.Equal(new SKColor(0, 0, 255), Pixel(p, 0, 250, 120));
        Assert.Equal(EffectRegistry.Vector, ObjectFactory.DefinitionForFile(svg));
    }

    [Fact]
    public void Lottie_animation_moves_over_time_and_loops()
    {
        // 1 秒で、赤い丸が左 (x=20) から右 (x=80) に動く。大きさ 100x100
        const string json = """{"v":"5.7.4","fr":30,"ip":0,"op":30,"w":100,"h":100,"layers":[{"ty":4,"ip":0,"op":30,"st":0,"ks":{"o":{"a":0,"k":100},"r":{"a":0,"k":0},"p":{"a":1,"k":[{"t":0,"s":[20,50,0]},{"t":29,"s":[80,50,0]}]},"a":{"a":0,"k":[0,0,0]},"s":{"a":0,"k":[100,100,100]}},"shapes":[{"ty":"el","p":{"a":0,"k":[0,0]},"s":{"a":0,"k":[20,20]}},{"ty":"fl","c":{"a":0,"k":[1,0,0,1]},"o":{"a":0,"k":100}}]}]}""";
        var path = Path.Combine(_dir, "move.json");
        File.WriteAllText(path, json);
        var p = Small();
        var o = ObjectFactory.Create(EffectRegistry.Vector, 0, 0, 90);
        o.Main.Values["file"] = path;
        p.Root.Objects.Add(o);
        // オブジェクトの左上は (110, 70)
        var red = new SKColor(255, 0, 0);
        Assert.Equal(red, Pixel(p, 0, 130, 120));
        Assert.NotEqual(red, Pixel(p, 0, 190, 120));
        Assert.Equal(red, Pixel(p, 29, 190, 120));
        Assert.Equal(red, Pixel(p, 30, 130, 120));       // ループして先頭に戻る
        o.Main.Values["loop"] = "0";
        Assert.Equal(red, Pixel(p, 60, 190, 120));       // ループしなければ最後で止まる
    }

    [Fact]
    public void Broken_vector_file_draws_nothing()
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{ not lottie");
        var p = Small();
        var o = ObjectFactory.Create(EffectRegistry.Vector, 0, 0, 30);
        o.Main.Values["file"] = path;
        var empty = Pixel(p, 0, 160, 120);
        p.Root.Objects.Add(o);
        Assert.Equal(empty, Pixel(p, 0, 160, 120));
    }

    [Fact]
    public void Stretched_scene_object_keeps_the_end_of_the_scene_at_the_end()
    {
        var p = Small();
        var sub = new Scene { Id = "s1", Name = "Sub" };
        // シーンの最後の 10 フレームだけ緑の四角が出る
        var last = ObjectFactory.Create(EffectRegistry.Shape, 0, 50, 10);
        last.Main.Values["color"] = "#00FF00";
        sub.Objects.Add(last);
        p.Scenes.Add(sub);
        var so = ObjectFactory.Create(EffectRegistry.SceneRef, 0, 0, 120);
        so.Main.Values["scene"] = "s1";
        p.Root.Objects.Add(so);
        var green = new SKColor(0, 255, 0);
        Assert.NotEqual(green, Pixel(p, 115, 160, 120));   // 伸縮しなければ、シーンの終わりを過ぎている
        so.Main.Values["stretch"] = "1";
        so.Main.Tracks["outro"] = new AnimatedValue(10 / 30.0);
        Assert.Equal(green, Pixel(p, 115, 160, 120));
        Assert.NotEqual(green, Pixel(p, 100, 160, 120));
    }
}

public class LottieColorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-lottie-{Guid.NewGuid():N}");

    public LottieColorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>
    /// 赤い丸 (塗り) と、緑から赤に変わる線、平面レイヤー (#0000ff)、文字色 (白) を持つ Lottie。
    /// パレットは出てくる順に 赤・緑・青・白。
    /// </summary>
    internal const string Json = """
    {"v":"5.7.4","fr":30,"ip":0,"op":30,"w":100,"h":100,"layers":[
      {"ty":4,"ip":0,"op":30,"st":0,"ks":{"o":{"a":0,"k":100},"r":{"a":0,"k":0},"p":{"a":0,"k":[50,50,0]},"a":{"a":0,"k":[0,0,0]},"s":{"a":0,"k":[100,100,100]}},
       "shapes":[{"ty":"el","p":{"a":0,"k":[0,0]},"s":{"a":0,"k":[60,60]}},
                 {"ty":"fl","c":{"a":0,"k":[1,0,0,1]},"o":{"a":0,"k":100}},
                 {"ty":"st","c":{"a":1,"k":[{"t":0,"s":[0,1,0,1]},{"t":29,"s":[1,0,0,1]}]},"o":{"a":0,"k":100},"w":{"a":0,"k":2}}]},
      {"ty":1,"sc":"#0000ff","sw":10,"sh":10,"ip":0,"op":30,"st":0,"ks":{}},
      {"ty":5,"t":{"d":{"k":[{"t":0,"s":{"t":"A","fc":[1,1,1],"s":10}}]}},"ip":0,"op":30,"st":0,"ks":{}}
    ]}
    """;

    [Fact]
    public void Palette_lists_colors_in_order_and_recolor_replaces_every_occurrence()
    {
        Assert.Equal(new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" }, VectorColors.LottiePalette(Json));
        var recolored = VectorColors.RecolorLottie(Json, new Dictionary<string, string> { ["#FF0000"] = "#336699", ["#0000FF"] = "#ABCDEF" });
        // 塗りと、線のキーフレームの終わりの両方が置き換わる。並びは変わらない
        Assert.Equal(new[] { "#336699", "#00FF00", "#ABCDEF", "#FFFFFF" }, VectorColors.LottiePalette(recolored));
        Assert.Contains("\"sc\":\"#abcdef\"", recolored);
        Assert.Empty(VectorColors.LottiePalette("{ not json"));
        Assert.Equal("#AABBCC", VectorColors.Normalize("#abc"));
        Assert.Equal("#112233", VectorColors.Normalize("#FF112233"));
        Assert.Null(VectorColors.Normalize("red"));
        Assert.Equal(2, VectorColors.SlotOf("color3"));
        Assert.Equal(-1, VectorColors.SlotOf("color9"));
    }

    [Fact]
    public void Map_uses_only_set_slots()
    {
        var e = new Effect(EffectRegistry.Get(EffectRegistry.Vector));
        var palette = VectorColors.LottiePalette(Json);
        Assert.Empty(VectorColors.MapFor(e, palette));
        e.Values["color2"] = "#123456";
        e.Values["color4"] = "#ffffff";    // 元と同じ色は置き換えない
        e.Values["color8"] = "#000000";    // パレットにない番号は無視
        Assert.Equal(new Dictionary<string, string> { ["#00FF00"] = "#123456" }, VectorColors.MapFor(e, palette));
    }

    private static SKColor Pixel(Project p, int frame, int x, int y)
    {
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, frame);
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    private static readonly string Circle = """{"v":"5.7.4","fr":30,"ip":0,"op":30,"w":100,"h":100,"layers":[{"ty":4,"ip":0,"op":30,"st":0,"ks":{"o":{"a":0,"k":100},"r":{"a":0,"k":0},"p":{"a":0,"k":[50,50,0]},"a":{"a":0,"k":[0,0,0]},"s":{"a":0,"k":[100,100,100]}},"shapes":[{"ty":"el","p":{"a":0,"k":[0,0]},"s":{"a":0,"k":[60,60]}},{"ty":"fl","c":{"a":0,"k":[1,0,0,1]},"o":{"a":0,"k":100}}]}]}""";

    [Fact]
    public void Lottie_color_is_replaced_when_rendering_and_through_an_exposed_template_item()
    {
        var path = Path.Combine(_dir, "circle.json");
        File.WriteAllText(path, Circle);
        var s = new EditorSession();
        s.Project.Settings.Width = 320; s.Project.Settings.Height = 240;
        var o = s.AddObject(EffectRegistry.Vector, 0, 0, 30, x => x.Main.Values["file"] = path)!;
        Assert.Equal(new SKColor(255, 0, 0), Pixel(s.Project, 0, 160, 120));
        o.Main.Values["color1"] = "#0000FF";
        Assert.Equal(new SKColor(0, 0, 255), Pixel(s.Project, 0, 160, 120));
        o.Main.Values["color1"] = "";
        Assert.Equal(new SKColor(255, 0, 0), Pixel(s.Project, 0, 160, 120));

        // テンプレートにして「色 1」を公開し、挿入したものの公開項目から色を変える
        var pkg = TemplatePackage.Create(s.Project, new[] { o }, new TemplateOptions
        {
            Name = "丸",
            Exposed = new() { new ExposedParam { ObjectId = o.Id, Effect = 0, Key = "color1", Label = "丸の色" } },
        });
        var target = new EditorSession();
        target.Project.Settings.Width = 320; target.Project.Settings.Height = 240;
        Assert.Equal(1, target.InsertTemplate(pkg.Instantiate(target.Project, Path.Combine(_dir, "assets"), TemplateInsertMode.Scene), 0, 0));
        var scene = target.Project.FindScene(target.Primary!.Main.GetString("scene"))!;
        var (_, effect, param) = scene.Exposed![0].Resolve(scene)!.Value;
        Assert.Equal(ParamKind.Color, param.Kind);
        target.Edit("色", () => effect.Values["color1"] = "#00FF00");
        Assert.Equal(new SKColor(0, 255, 0), Pixel(target.Project, 0, 160, 120));
    }
}

public class SvgColorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-svgcolor-{Guid.NewGuid():N}");

    public SvgColorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string Svg = """
    <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
      <style>.b { fill: rgb(0, 0, 255); }</style>
      <defs><linearGradient id="g"><stop offset="0" stop-color="#ff0"/><stop offset="1" style="stop-color: white"/></linearGradient></defs>
      <rect width="50" height="50" fill="#FF0000" stroke="none"/>
      <rect x="50" width="50" height="50" style="fill:red;stroke:#00ff0080"/>
      <circle class="b" r="5" fill="currentColor" color="url(#g)"/>
    </svg>
    """;

    [Fact]
    public void Svg_palette_reads_attributes_styles_and_css_in_order()
    {
        Assert.Equal(new[] { "#0000FF", "#FFFF00", "#FFFFFF", "#FF0000", "#00FF00" }, VectorColors.SvgPalette(Svg));
    }

    [Fact]
    public void Svg_recolor_replaces_every_form_and_keeps_alpha()
    {
        var map = new Dictionary<string, string> { ["#FF0000"] = "#123456", ["#00FF00"] = "#ABCDEF", ["#0000FF"] = "#010203" };
        var r = VectorColors.RecolorSvg(Svg, map);
        Assert.Contains("fill=\"#123456\"", r);
        Assert.Contains("fill:#123456;", r);            // 名前の色 (red) も置き換わる
        Assert.Contains("stroke:#abcdef80", r);          // 透明度は残る
        Assert.Contains("fill: #010203;", r);            // <style> の中
        Assert.Contains("stroke=\"none\"", r);
        Assert.Contains("fill=\"currentColor\"", r);
        Assert.Equal(new[] { "#010203", "#FFFF00", "#FFFFFF", "#123456", "#ABCDEF" }, VectorColors.SvgPalette(r));
        Assert.Contains("rgba(1,2,3,0.5)", VectorColors.RecolorSvg("<a fill='rgba(0,0,255,0.5)'/>", map));
    }

    private static SKColor Pixel(Project p, int x, int y)
    {
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, 0);
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void Svg_color_is_replaced_when_rendering_and_through_an_exposed_template_item()
    {
        var path = Path.Combine(_dir, "two.svg");
        // 左半分が赤、右半分が緑。大きさ 100x50 で、画面 (320x240) の中央に置かれる
        File.WriteAllText(path, "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect width='50' height='50' fill='red'/><rect x='50' width='50' height='50' fill='#00ff00'/></svg>");
        var s = new EditorSession();
        s.Project.Settings.Width = 320; s.Project.Settings.Height = 240;
        var o = s.AddObject(EffectRegistry.Vector, 0, 0, 30, x => x.Main.Values["file"] = path)!;
        Assert.Equal(new[] { "#FF0000", "#00FF00" }, VectorColors.PaletteOfFile(path));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(s.Project, 135, 120));
        o.Main.Values["color2"] = "#0000FF";
        Assert.Equal(new SKColor(255, 0, 0), Pixel(s.Project, 135, 120));
        Assert.Equal(new SKColor(0, 0, 255), Pixel(s.Project, 185, 120));

        var pkg = TemplatePackage.Create(s.Project, new[] { o }, new TemplateOptions
        {
            Name = "ロゴ",
            Exposed = new() { new ExposedParam { ObjectId = o.Id, Effect = 0, Key = "color1", Label = "左の色" } },
        });
        var target = new EditorSession();
        target.Project.Settings.Width = 320; target.Project.Settings.Height = 240;
        Assert.Equal(1, target.InsertTemplate(pkg.Instantiate(target.Project, Path.Combine(_dir, "assets"), TemplateInsertMode.Scene), 0, 0));
        var scene = target.Project.FindScene(target.Primary!.Main.GetString("scene"))!;
        var (_, effect, _) = scene.Exposed![0].Resolve(scene)!.Value;
        target.Edit("色", () => effect.Values["color1"] = "#FFFF00");
        Assert.Equal(new SKColor(255, 255, 0), Pixel(target.Project, 135, 120));
        Assert.Equal(new SKColor(0, 0, 255), Pixel(target.Project, 185, 120));   // 保存したときの「色 2」も引き継がれる
    }
}

public class SubtitleTests
{
    private const string Srt = "﻿1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>こんにちは</i>\r\n世界\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\n{\\an8}次 &amp; 最後\r\n\r\n";
    private const string Vtt = "WEBVTT\n\nNOTE comment\n\nintro\n00:01.000 --> 00:02.000 align:middle\n<v 話者>一行目\n\n00:00:05.250 --> 00:00:06.000\n二つ目\n";

    [Fact]
    public void Srt_and_vtt_are_parsed_and_tags_removed()
    {
        var srt = Subtitles.Parse(Srt);
        Assert.Equal(2, srt.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), srt[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2.5), srt[0].End);
        Assert.Equal("こんにちは\n世界", srt[0].Text);
        Assert.Equal("次 & 最後", srt[1].Text);
        var vtt = Subtitles.Parse(Vtt);
        Assert.Equal(new[] { "一行目", "二つ目" }, vtt.Select(c => c.Text).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(5.25), vtt[1].Start);
    }

    [Fact]
    public void Writers_round_trip()
    {
        var cues = Subtitles.Parse(Srt);
        var srt = Subtitles.ToSrt(cues);
        Assert.Contains("00:00:01,000 --> 00:00:02,500", srt);
        Assert.Equal(cues, Subtitles.Parse(srt));
        var vtt = Subtitles.Write("a.vtt", cues);
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:03.000 --> 00:00:04.000", vtt);
        Assert.Equal(cues, Subtitles.Parse(vtt));
    }

    [Fact]
    public void Subtitles_become_text_objects_on_one_free_layer_and_are_exported_again()
    {
        var s = new EditorSession();
        s.Project.Settings.FrameRate = "30";
        s.AddObject(EffectRegistry.Shape, 0, 40, 10);              // レイヤー 1 は 2 つ目の字幕と重なる
        var cues = Subtitles.Parse(Srt);
        Assert.Equal(1, s.ImportSubtitles(cues, 0));
        var texts = s.Scene.Objects.Where(o => o.Main.DefinitionId == EffectRegistry.Text).OrderBy(o => o.Start).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal((30, 74), (texts[0].Start, texts[0].End));    // 1.0〜2.5 秒 (終わりは含まない)
        Assert.Equal("こんにちは\n世界", texts[0].Main.GetString("text"));
        Assert.NotNull(texts[0].FindEffect("filter.border"));
        Assert.Equal(432, texts[0].Draw!.Tracks["y"].Values[0]);   // 下寄せ (高さ 1080 の 0.4)

        var back = s.ExportSubtitles(1);
        Assert.Equal(cues.Select(c => (c.Start, c.End, c.Text)), back.Select(c => (c.Start, c.End, c.Text)));
        s.Undo();
        Assert.DoesNotContain(s.Scene.Objects, o => o.Main.DefinitionId == EffectRegistry.Text);

        // テキストを選んでいれば、その見た目をひな形にする
        var style = s.AddObject(EffectRegistry.Text, 5, 0, 10)!;
        style.Main.Values["color"] = "#FFFF00";
        style.AddKeyFrame(5);
        Assert.Equal(5, s.ImportSubtitles(cues, 5, style));
        var styled = s.Scene.Objects.Where(o => o.Main.DefinitionId == EffectRegistry.Text && o.Id != style.Id).ToList();
        Assert.All(styled, o => Assert.Equal("#FFFF00", o.Main.GetString("color")));
        Assert.All(styled, o => Assert.Empty(o.KeyFrames));

        // 重なった字幕は、前のものを次の手前で終わらせる
        var t = new EditorSession();
        var overlapping = new[] { new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "a"), new SubtitleCue(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "b") };
        Assert.Equal(0, t.ImportSubtitles(overlapping, 0));
        Assert.Equal(new[] { (30, 59), (60, 119) }, t.Scene.Objects.OrderBy(o => o.Start).Select(o => (o.Start, o.End)).ToArray());
    }
}

public class VectorFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-vfile-{Guid.NewGuid():N}");
    public VectorFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static SKColor Pixel(Project p, int frame, int x, int y)
    {
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, frame);
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    private static Project Small() => new() { Settings = { Width = 320, Height = 240 } };

    [Fact]
    public void Svg_parts_are_hidden_by_id()
    {
        var svg = Path.Combine(_dir, "two.svg");
        File.WriteAllText(svg, "<?xml version='1.0'?><svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect id='left' width='50' height='50' fill='red'/><g id='right'><rect x='50' width='50' height='50' style='fill:#00ff00'/></g></svg>");
        Assert.Equal(new[] { "left", "right" }, VectorFiles.SvgIds(File.ReadAllText(svg)));
        Assert.Equal(new[] { "a", "b", "c" }, VectorFiles.ParseIds(" a, b c,").Order().ToArray());
        var p = Small();
        var o = ObjectFactory.Create(EffectRegistry.Vector, 0, 0, 30);
        o.Main.Values["file"] = svg;
        p.Root.Objects.Add(o);
        var bg = Pixel(new Project { Settings = { Width = 320, Height = 240 } }, 0, 0, 0);
        Assert.Equal(new SKColor(0, 255, 0), Pixel(p, 0, 185, 120));
        o.Main.Values["hide"] = "right";
        Assert.Equal(bg, Pixel(p, 0, 185, 120));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 135, 120));
        // 色の置き換えと一緒に使える
        o.Main.Values["color1"] = "#0000FF";
        Assert.Equal(new SKColor(0, 0, 255), Pixel(p, 0, 135, 120));
        Assert.Equal(bg, Pixel(p, 0, 185, 120));
        // 壊れた SVG はそのまま
        Assert.Equal("<svg", VectorFiles.HideSvgElements("<svg", new HashSet<string> { "x" }));
    }

    [Fact]
    public void DotLottie_is_played_with_embedded_images_and_recolored()
    {
        // 赤い丸の Lottie と、使われない画像 1 つを入れた dotLottie (版 1 の並び)
        const string anim = """{"v":"5.7.4","fr":30,"ip":0,"op":30,"w":100,"h":100,"assets":[{"id":"img_0","w":4,"h":4,"u":"/images/","p":"img_0.png","e":0}],"layers":[{"ty":4,"ip":0,"op":30,"st":0,"ks":{"o":{"a":0,"k":100},"r":{"a":0,"k":0},"p":{"a":0,"k":[50,50,0]},"a":{"a":0,"k":[0,0,0]},"s":{"a":0,"k":[100,100,100]}},"shapes":[{"ty":"el","p":{"a":0,"k":[0,0]},"s":{"a":0,"k":[60,60]}},{"ty":"fl","c":{"a":0,"k":[1,0,0,1]},"o":{"a":0,"k":100}}]}]}""";
        var path = Path.Combine(_dir, "circle.lottie");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write("""{"version":"1","animations":[{"id":"main"}]}""");
            using (var w = new StreamWriter(zip.CreateEntry("animations/main.json").Open())) w.Write(anim);
            using (var s = zip.CreateEntry("images/img_0.png").Open())
            {
                using var bmp = new SKBitmap(4, 4);
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                data.SaveTo(s);
            }
        }
        var json = VectorFiles.ReadText(path);
        Assert.Contains("data:image/png;base64,", json);
        Assert.Equal(new[] { "#FF0000" }, VectorColors.PaletteOfFile(path));
        Assert.Equal(EffectRegistry.Vector, ObjectFactory.DefinitionForFile(path));

        var p = Small();
        var o = ObjectFactory.Create(EffectRegistry.Vector, 0, 0, 30);
        o.Main.Values["file"] = path;
        p.Root.Objects.Add(o);
        Assert.Equal(new SKColor(255, 0, 0), Pixel(p, 0, 160, 120));
        o.Main.Values["color1"] = "#00FF00";
        Assert.Equal(new SKColor(0, 255, 0), Pixel(p, 0, 160, 120));
        o.Main.Values["hide"] = "x";   // Lottie では非表示の指定は使わない (描ける)
        Assert.Equal(new SKColor(0, 255, 0), Pixel(p, 0, 160, 120));
    }
}

