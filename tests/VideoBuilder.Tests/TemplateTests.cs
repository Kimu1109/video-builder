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
