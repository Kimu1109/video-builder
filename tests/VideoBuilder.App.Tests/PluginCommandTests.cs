using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
}
