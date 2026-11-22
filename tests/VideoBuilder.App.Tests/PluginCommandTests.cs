using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.App.Views;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Plugins;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Tests;

/// <summary>プラグイン API v3 (メニューのコマンド) の、画面側の実装を確かめる。</summary>
public class PluginCommandTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"vb-plugin-cmd-{Guid.NewGuid():N}");

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button ButtonOf(Window w, string text) =>
        w.GetLogicalDescendants().OfType<Button>().First(b => L.StripNames(b.Content as string ?? (b as IconButton)?.Text ?? "") == text);

    [AvaloniaFact]
    public void Edit_places_text_audio_and_groups_them_in_one_undo_step()
    {
        var session = new EditorSession();
        var ctx = new PluginCommandContext(session, null, "test.plugin", TempDir());
        // レイヤー 0 の 0〜29 はふさがっている
        session.Edit("準備", () => session.Scene.Objects.Add(ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 30)));
        int before = session.Scene.Objects.Count;

        TimelineObject? text = null, audio = null;
        ctx.Edit("声を置く", e =>
        {
            audio = e.AddAudioFile("/nonexistent/voice.wav", 0, 10, lengthFrames: 45);
            text = e.AddText("こんにちは", 0, 10, 45);
            audio!.PluginData = new() { ["test.plugin.text"] = "こんにちは" };
            e.Group(new[] { text!, audio });
            e.Select(new[] { text!, audio });
        });

        Assert.NotNull(text); Assert.NotNull(audio);
        Assert.Equal(1, audio!.Layer);   // 0 はふさがっているので下へ。長さは削らない
        Assert.Equal(45, audio.Length);
        Assert.Equal("/nonexistent/voice.wav", audio.Main.Values["file"]);
        Assert.Equal(EffectRegistry.Audio, audio.Main.DefinitionId);
        Assert.Equal(2, text!.Layer);
        Assert.Equal("こんにちは", text.Main.Values["text"]);
        Assert.NotNull(text.GroupId);
        Assert.Equal(text.GroupId, audio.GroupId);
        Assert.Equal(new[] { audio.Id, text.Id }.OrderBy(x => x), session.SelectionIds.OrderBy(x => x));
        Assert.Equal(1, ctx.TargetLayer);   // 最後に選んだもののレイヤー (選んだ順)

        // まとめて 1 回で取り消せる
        Assert.Equal("声を置く", session.History.UndoDescription);
        session.Undo();
        Assert.Equal(before, session.Scene.Objects.Count);
    }

    [AvaloniaFact]
    public void Edit_that_throws_changes_nothing()
    {
        var session = new EditorSession();
        var ctx = new PluginCommandContext(session, null, "test.plugin", TempDir());
        Assert.Throws<InvalidOperationException>(() => ctx.Edit("失敗", e =>
        {
            e.AddText("途中まで", 0, 0, 10);
            throw new InvalidOperationException("だめ");
        }));
        Assert.Empty(session.Scene.Objects);
        Assert.False(session.History.CanUndo);
        // 読めない音声ファイルで長さを省くと、理由付きの例外になる
        var ex = Assert.Throws<InvalidOperationException>(() => ctx.Edit("音声", e => e.AddAudioFile("/nonexistent/voice.wav", 0, 0)));
        Assert.Contains("音声ファイルを読めません", ex.Message);
        Assert.Throws<ArgumentException>(() => ctx.Edit("なし", e => e.AddObject("no.such.object", 0, 0, 10)));
        Assert.Empty(session.Scene.Objects);
    }

    [AvaloniaFact]
    public void Data_directory_is_created_per_plugin_and_stays_inside_the_root()
    {
        var root = TempDir();
        try
        {
            var dir = new PluginCommandContext(new EditorSession(), null, "my.plugin", root).DataDirectory;
            Assert.True(Directory.Exists(dir));
            Assert.Equal(Path.Combine(root, "my.plugin"), dir);
            var odd = new PluginCommandContext(new EditorSession(), null, "../../escape", root).DataDirectory;
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(odd));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [AvaloniaFact]
    public void Form_writes_values_back_only_when_accepted()
    {
        PluginForm Make() => new()
        {
            Title = "テスト",
            Fields =
            {
                new PluginTextField { Key = "text", Label = "セリフ", Multiline = true, Required = true },
                new PluginNumberField { Key = "speed", Label = "話速", Value = 1, Minimum = 0.5, Maximum = 2, Decimals = 2 },
                new PluginChoiceField { Key = "speaker", Label = "話者", Value = "b", Options = { new("a", "A さん"), new("b", "B さん"), new("c", "C さん") } },
                new PluginCheckField { Key = "subtitle", Label = "字幕も置く", Value = true },
            },
        };

        var form = Make();
        var d = new PluginFormDialog(form);
        d.Show();
        var box = d.GetLogicalDescendants().OfType<TextBox>().First(t => t is not NumberBox);
        var number = d.GetLogicalDescendants().OfType<NumberBox>().Single();
        var combo = d.GetLogicalDescendants().OfType<ComboBox>().Single();
        var check = d.GetLogicalDescendants().OfType<CheckBox>().Single();
        Assert.Equal(1, combo.SelectedIndex);   // Value に合わせて選ばれている

        // 必須の項目が空なら閉じない
        Click(ButtonOf(d, "OK"));
        Assert.False(d.Accepted);
        Assert.True(d.IsVisible);

        box.Text = "こんにちは\n2 行目";
        number.Text = "3";            // 入力途中の値も OK で確定する (範囲に収める)
        combo.SelectedIndex = 2;
        check.IsChecked = false;
        Click(ButtonOf(d, "OK"));
        Assert.True(d.Accepted);
        Assert.Equal("こんにちは\n2 行目", form.Get<PluginTextField>("text").Value);
        Assert.Equal(2, form.Get<PluginNumberField>("speed").Value);
        Assert.Equal("c", form.Get<PluginChoiceField>("speaker").Value);
        Assert.False(form.Get<PluginCheckField>("subtitle").Value);

        // キャンセルなら書き戻さない
        var form2 = Make();
        var d2 = new PluginFormDialog(form2);
        d2.Show();
        d2.GetLogicalDescendants().OfType<CheckBox>().Single().IsChecked = false;
        Click(ButtonOf(d2, "キャンセル"));
        Assert.False(d2.Accepted);
        Assert.True(form2.Get<PluginCheckField>("subtitle").Value);
    }

    [AvaloniaFact]
    public async Task Progress_returns_the_result_and_cancel_reaches_the_work()
    {
        var owner = new Window();
        owner.Show();
        var ctx = new PluginCommandContext(new EditorSession(), owner, "test.plugin", TempDir());

        // すぐ終わるもの・少し待つもの
        Assert.Equal(1, await ctx.RunWithProgressAsync("すぐ", (_, _) => Task.FromResult(1)));
        Assert.Equal(2, await ctx.RunWithProgressAsync("待つ", async (p, ct) =>
        {
            p.Report(new PluginProgress("半分", 0.5));
            await Task.Delay(20, ct);
            return 2;
        }));
        Assert.Empty(owner.OwnedWindows);

        // 中止を押すと、work のトークンが取り消される
        var running = ctx.RunWithProgressAsync("止める", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return 3; });
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.IsType<PluginProgressDialog>(Assert.Single(owner.OwnedWindows));
        Click(ButtonOf(dialog, "中止"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(owner.OwnedWindows);

        // work の例外はそのまま返る
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.RunWithProgressAsync<int>("失敗", (_, _) => throw new InvalidOperationException()));
        owner.Close();
    }

    private sealed class TestCommand(string id, string name, string? group, bool canRun, Func<IPluginCommandContext, Task> run) : IPluginCommand
    {
        public string Id => id;
        public string Name => name;
        public string? Group => group;
        public bool CanRun(IPluginCommandContext context) => canRun;
        public Task RunAsync(IPluginCommandContext context) => run(context);
    }

    [AvaloniaFact]
    public async Task Menu_groups_commands_and_errors_are_reported()
    {
        var ids = new[] { "test.menu.a", "test.menu.b", "test.menu.c", "test.menu.fail" };
        try
        {
            int ran = 0;
            PluginCommands.Register("test.plugin", new TestCommand(ids[0], "そのまま", null, true, _ => { ran++; return Task.CompletedTask; }));
            PluginCommands.Register("test.plugin", new TestCommand(ids[1], "まとめる 1", "グループ", true, _ => Task.CompletedTask));
            PluginCommands.Register("test.plugin", new TestCommand(ids[2], "選べない", "グループ", false, _ => Task.CompletedTask));
            PluginCommands.Register("test.plugin", new TestCommand(ids[3], "失敗する", null, true, _ => throw new InvalidOperationException("エンジンにつながりません")));

            var session = new EditorSession();
            PluginCommandContext Ctx(PluginCommandEntry e) => new(session, null, e.PluginId, TempDir());
            var items = PluginMenu.Items(Ctx, _ => Task.CompletedTask).OfType<MenuItem>().ToList();
            var group = items.Single(i => i.Items.Count > 0);
            Assert.Equal(2, group.Items.Count);
            Assert.False(((MenuItem)group.Items[1]!).IsEnabled);
            Assert.True(((MenuItem)group.Items[0]!).IsEnabled);

            var entries = PluginCommands.All.Where(e => ids.Contains(e.Command.Id)).ToList();
            await PluginMenu.RunAsync(entries[0], Ctx(entries[0]), null);
            Assert.Equal(1, ran);

            // 例外はメッセージで知らせ、ログに残す (呼び出し元には投げない)
            var owner = new Window();
            owner.Show();
            var failing = PluginMenu.RunAsync(entries[3], Ctx(entries[3]), owner);
            Dispatcher.UIThread.RunJobs();
            var message = Assert.IsType<MessageDialog>(Assert.Single(owner.OwnedWindows));
            Assert.Contains(message.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("エンジンにつながりません") == true);
            message.Close();
            await failing;
            Assert.Contains(PluginLoader.LogMessages, l => l.Contains(ids[3]) && l.Contains("エンジンにつながりません"));
            owner.Close();
        }
        finally { foreach (var id in ids) PluginCommands.Unregister(id); }
    }

    // ---------------- テンプレート (API バージョン 4) ----------------

    [AvaloniaFact]
    public void Templates_are_listed_loaded_and_placed_as_a_stretching_scene_object()
    {
        using var lib = new TemplateFixture();
        var id = lib.Add("字幕の帯");
        lib.Add("見出し", category: "見出し");
        lib.Add("字幕の帯");   // 同じ名前で保存し直す → 版が 2 になる (一覧には 1 つだけ)
        var session = new EditorSession();
        var ctx = new PluginCommandContext(session, null, "test.plugin", TempDir(), lib.Store);

        var list = ctx.ListTemplates();
        // 並び順 (分類・名前の順) は表示言語の並べ方によるので、ここでは中身だけを比べる
        Assert.Equal(new[] { ("字幕", "字幕の帯"), ("見出し", "見出し") }.OrderBy(x => x.Item2, StringComparer.Ordinal),
            list.Select(t => (t.Category, t.Name)).OrderBy(x => x.Name, StringComparer.Ordinal));
        Assert.Empty(new PluginCommandContext(session, null, "test.plugin", TempDir()).ListTemplates());   // ライブラリなし
        Assert.Null(ctx.LoadTemplate("no-such-id"));

        var template = ctx.LoadTemplate(id)!;
        Assert.Equal((id, "字幕の帯", 2), (template.Id, template.Name, template.Version));
        Assert.Equal(new[] { ("セリフ", true, "見本の文字"), ("帯の色", false, "#203040") }, template.Fields.Select(f => (f.Label, f.IsText, f.Value)));

        // 置く: 既定の置き方が「複数のオブジェクト」でも、シーンオブジェクト 1 つ。長さに合わせて伸縮する
        int scenes = session.Project.Scenes.Count;
        TimelineObject? placed = null;
        ctx.Edit("字幕", e => placed = e.AddTemplate(template, 2, 30, 100, new Dictionary<string, string> { ["セリフ"] = "こんにちは", ["帯の色"] = "#FF0000", ["ない項目"] = "x" }));
        Assert.NotNull(placed);
        Assert.Equal(EffectRegistry.SceneRef, placed!.Main.DefinitionId);
        Assert.Equal((2, 30, 100), (placed.Layer, placed.Start, placed.Length));
        Assert.True(placed.Main.GetBool("stretch"));
        Assert.Equal(scenes + 1, session.Project.Scenes.Count);
        Assert.Equal("字幕の帯", session.TemplateOf(placed)!.Name);   // 新しい版への更新もできる

        // テンプレートの長さ (60 フレーム) より長く伸ばしても、後半まで帯が描かれる (伸縮しないと 60 フレームで消える)
        using (var engine = new VideoBuilder.Rendering.RenderEngine())
        {
            var project = session.SnapshotForRead();
            foreach (int frame in new[] { 40, 120 })
            {
                using var image = engine.RenderImage(project, project.Root, frame);
                using var bmp = SkiaSharp.SKBitmap.FromImage(image);
                var px = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
                Assert.True(px.Red > 200 && px.Green < 60, $"{frame} フレーム目に帯がありません: {px}");
            }
        }

        // 置いたものの項目を読み書きする
        Assert.Equal(new[] { ("セリフ", "こんにちは"), ("帯の色", "#FF0000") }, ctx.TemplateFields(placed).Select(f => (f.Label, f.Value)));
        int n = 0;
        ctx.Edit("直す", e => n = e.SetTemplateValues(placed, new Dictionary<string, string> { ["セリフ"] = "さようなら" }));
        Assert.Equal(1, n);
        Assert.Equal("さようなら", ctx.TemplateFields(placed)[0].Value);
        Assert.Empty(ctx.TemplateFields(ObjectFactory.Create(EffectRegistry.Text, 0, 0, 10)));   // テンプレートでないもの

        // 取り消すと、足したシーンも消える
        session.Undo();
        Assert.Equal("こんにちは", ctx.TemplateFields(session.Scene.Find(placed.Id)!)[0].Value);
        session.Undo();
        Assert.Null(session.Scene.Find(placed.Id));
        Assert.Equal(scenes, session.Project.Scenes.Count);
    }

    [Fact]
    public void Exposed_values_are_read_and_written_as_text()
    {
        var o = ObjectFactory.Create(EffectRegistry.Text, 0, 0, 60);
        o.AddKeyFrame(30);
        var objects = new[] { o };
        var size = new ExposedParam { ObjectId = o.Id, Effect = 0, Key = "size", Label = "大きさ" };
        Assert.True(ExposedValues.Set(objects, size, "72.5"));
        Assert.Equal("72.5", ExposedValues.Get(objects, size));
        Assert.Equal(3, o.Main.Tracks["size"].Values.Count);   // 中間点の区間ぶん
        Assert.False(ExposedValues.Set(objects, size, "大きい"));
        var text = new ExposedParam { ObjectId = o.Id, Effect = 0, Key = "text", Label = "文字" };
        Assert.True(ExposedValues.Set(objects, text, "あ\nい"));
        Assert.Equal("あ\nい", ExposedValues.Get(objects, text));
        Assert.True(ExposedValues.IsText(ExposedValues.Resolve(objects, text)!.Value.Param));
        Assert.Null(ExposedValues.Get(objects, new ExposedParam { ObjectId = "nothing", Key = "text" }));
    }

    // ---------------- 入力パネル (API バージョン 5) ----------------

    [AvaloniaFact]
    public void Panel_stays_open_and_enter_presses_the_default_button()
    {
        var owner = new Window();
        owner.Show();
        var ctx = new PluginCommandContext(new EditorSession(), owner, "test.plugin", TempDir());
        var pressed = new List<(string Text, double Number, bool Check)>();
        var spec = new PluginPanel
        {
            Id = "test.panel", Title = "パネル", FocusKey = "text",
            Fields =
            {
                new PluginTextField { Key = "text", Label = "文字", Required = true },
                new PluginNumberField { Key = "n", Label = "数", Value = 1, Minimum = 0, Maximum = 10, Decimals = 1 },
                new PluginCheckField { Key = "c", Label = "チェック", Value = true },
                new PluginChoiceField { Key = "k", Label = "選ぶ", Value = "a", Options = { new("a", "A"), new("b", "B") } },
            },
        };
        spec.Buttons.Add(new PluginPanelButton
        {
            Text = "置く", IsDefault = true,
            OnClick = p =>
            {
                pressed.Add((spec.Get<PluginTextField>("text").Value, spec.Get<PluginNumberField>("n").Value, spec.Get<PluginCheckField>("c").Value));
                p.SetValue("text", "");
                p.SetStatus($"{pressed.Count} 回");
                return Task.CompletedTask;
            },
        });
        spec.Buttons.Add(new PluginPanelButton { Text = "失敗", CheckRequired = false, OnClick = _ => throw new InvalidOperationException("だめでした") });
        try
        {
            var panel = ctx.ShowPanel(spec);
            Dispatcher.UIThread.RunJobs();
            var window = Assert.IsType<PluginPanelWindow>(Assert.Single(owner.OwnedWindows));
            Assert.Same(window, panel);
            var box = window.GetLogicalDescendants().OfType<TextBox>().First(t => t is not NumberBox);
            Assert.True(box.IsFocused);   // FocusKey

            // 空のままでは押せない
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Empty(pressed);

            // 入れて Enter → 押される。パネルは開いたまま、入力欄は空になる
            box.Text = "いち";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { ("いち", 1.0, true) }, pressed);
            Assert.True(panel.IsOpen);
            Assert.Equal("", box.Text);
            Assert.Equal("1 回", window.Status);

            panel.SetValue("n", 2.5);
            panel.SetValue("c", false);
            panel.SetValue("k", "b");
            Assert.Equal(1, window.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedIndex);
            box.Text = "に";
            window.Click("置く");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(("に", 2.5, false), pressed[1]);

            // 例外は状態の欄に出す (パネルは閉じない)
            window.Click("失敗");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("だめでした", window.Status);
            Assert.True(panel.IsOpen);

            // 同じ Id なら、新しく開かずに同じものを返す
            Assert.Same(panel, ctx.ShowPanel(spec));
            Assert.Single(owner.OwnedWindows);

            // 閉じると知らせが来て、次はまた新しく開く
            bool closed = false;
            panel.Closed += () => closed = true;
            panel.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(closed);
            Assert.False(panel.IsOpen);
            var again = ctx.ShowPanel(spec);
            Assert.NotSame(panel, again);
            again.Close();

            // 画面がなければ、閉じたパネルが返る
            Assert.False(new PluginCommandContext(new EditorSession(), null, "test.plugin", TempDir()).ShowPanel(spec).IsOpen);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public void Seek_moves_the_playhead_and_edits_wait_for_the_users_drag()
    {
        var session = new EditorSession();
        var ctx = new PluginCommandContext(session, null, "test.plugin", TempDir());
        ctx.Seek(120);
        Assert.Equal(120, session.CurrentFrame);
        ctx.Seek(-5);
        Assert.Equal(0, session.CurrentFrame);

        Assert.True(ctx.CanEdit);
        session.BeginEdit("ドラッグ");
        Assert.False(ctx.CanEdit);
        Assert.Throws<InvalidOperationException>(() => ctx.Edit("割り込み", e => e.AddText("x", 0, 0, 10)));
        session.CommitEdit();
        Assert.True(ctx.CanEdit);
        Assert.Empty(session.Scene.Objects);
    }
}
