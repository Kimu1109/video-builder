using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
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
using VoicevoxPlugin;

namespace VideoBuilder.App.Tests;

/// <summary>VOICEVOX 連携プラグイン。エンジンの代わりに、同じ API を返す小さな HTTP サーバーを立てて確かめる。</summary>
public class VoicevoxPluginTests
{
    /// <summary>16bit・モノラルの無音の WAV。extra なら、fmt と data の間に別のチャンク (奇数の長さ) をはさむ。</summary>
    public static byte[] Wav(double seconds, int rate = 24000, bool extra = false)
    {
        int data = (int)Math.Round(seconds * rate) * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        byte[] list = extra ? Encoding.ASCII.GetBytes("abc") : Array.Empty<byte>();
        int listChunk = extra ? 8 + list.Length + 1 : 0;
        w.Write("RIFF"u8); w.Write(4 + 24 + listChunk + 8 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        if (extra) { w.Write("LIST"u8); w.Write(list.Length); w.Write(list); w.Write((byte)0); }
        w.Write("data"u8); w.Write(data); w.Write(new byte[data]);
        return ms.ToArray();
    }

    /// <summary>VOICEVOX のエンジンの代わり。受けた要求を覚えておく。</summary>
    private sealed class FakeEngine : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public double Seconds { get; set; } = 1.25;
        public List<(string Method, string PathAndQuery, string Body)> Requests { get; } = new();

        public FakeEngine()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); } catch { return; }
                string body;
                using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
                var path = c.Request.Url!.PathAndQuery;
                lock (Requests) Requests.Add((c.Request.HttpMethod, path, body));
                byte[] reply; string type = "application/json";
                if (path == "/version") reply = Encoding.UTF8.GetBytes("\"0.21.1\"");
                else if (path == "/speakers")
                    reply = Encoding.UTF8.GetBytes("""
                        [{"name":"四国めたん","speaker_uuid":"a","styles":[{"name":"ノーマル","id":2,"type":"talk"}]},
                         {"name":"ずんだもん","speaker_uuid":"b","styles":[{"name":"ノーマル","id":3,"type":"talk"},{"name":"あまあま","id":1},{"name":"ハミング","id":3000,"type":"sing"},{"name":"ささやき","id":22,"type":"streaming_talk"}]},
                         {"name":"波音リツ","speaker_uuid":"c","styles":[{"name":"ノーマル","id":6000,"type":"singing_teacher"},{"name":"クイーン","id":6001,"type":"frame_decode"}]}]
                        """);
                else if (path.StartsWith("/audio_query"))
                    reply = Encoding.UTF8.GetBytes("""{"accent_phrases":[],"speedScale":1.0,"pitchScale":0.0,"intonationScale":1.0,"volumeScale":1.0,"prePhonemeLength":0.1,"postPhonemeLength":0.1,"outputSamplingRate":24000,"outputStereo":false,"kana":""}""");
                else if (path.StartsWith("/synthesis")) { reply = Wav(Seconds); type = "audio/wav"; }
                else { c.Response.StatusCode = 422; reply = Encoding.UTF8.GetBytes("{\"detail\":\"unknown\"}"); }
                c.Response.ContentType = type;
                c.Response.ContentLength64 = reply.Length;
                await c.Response.OutputStream.WriteAsync(reply);
                c.Response.Close();
            }
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    private static string ClosedPortUrl()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return $"http://127.0.0.1:{port}";
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"vb-voicevox-{Guid.NewGuid():N}");

    private static async Task Until(Func<bool> done, int timeoutMs = 15000)
    {
        var limit = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > limit) throw new TimeoutException();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window w, string text) =>
        w.GetLogicalDescendants().OfType<Button>().First(b => L.StripNames(b.Content as string ?? (b as IconButton)?.Text ?? "") == text)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Texts(Window w) => string.Join("\n", w.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    // ---------------- 部品 ----------------

    [Fact]
    public void Wav_duration_is_read_from_the_header()
    {
        Assert.True(WavInfo.TryGetDuration(Wav(1.5), out var a));
        Assert.Equal(1.5, a, 3);
        Assert.True(WavInfo.TryGetDuration(Wav(0.25, 48000, extra: true), out var b));
        Assert.Equal(0.25, b, 3);
        Assert.False(WavInfo.TryGetDuration("not a wav"u8.ToArray(), out _));
        Assert.False(WavInfo.TryGetDuration(Wav(1)[..30], out _));   // data がない
    }

    [Theory]
    [InlineData("http://127.0.0.1:50021", "http://127.0.0.1:50021/")]
    [InlineData(" http://localhost:10101/ ", "http://localhost:10101/")]
    [InlineData("https://tts.example.com/voicevox", "https://tts.example.com/voicevox/")]
    [InlineData("ftp://127.0.0.1", null)]
    [InlineData("127.0.0.1:50021", null)]
    [InlineData("http://127.0.0.1:50021/?a=1", null)]
    [InlineData("", null)]
    public void Engine_url_is_validated(string input, string? expected) =>
        Assert.Equal(expected, VoicevoxClient.ParseUrl(input)?.ToString());

    [Fact]
    public void Voice_params_are_clamped_and_written_into_the_query()
    {
        var json = VoicevoxClient.ApplyParams("""{"accent_phrases":[],"speedScale":1,"kana":"ア"}""",
            new VoiceParams { Speed = 5, Pitch = -1, Intonation = 1.2, Volume = 0.8, PreSilence = 0, PostSilence = 3 });
        var q = JsonNode.Parse(json)!;
        Assert.Equal(2, q["speedScale"]!.GetValue<double>());
        Assert.Equal(-0.15, q["pitchScale"]!.GetValue<double>());
        Assert.Equal(1.2, q["intonationScale"]!.GetValue<double>());
        Assert.Equal(0.8, q["volumeScale"]!.GetValue<double>());
        Assert.Equal(0, q["prePhonemeLength"]!.GetValue<double>());
        Assert.Equal(1.5, q["postPhonemeLength"]!.GetValue<double>());
        Assert.Equal("ア", q["kana"]!.GetValue<string>());   // ほかの値はそのまま
        Assert.Throws<VoicevoxException>(() => VoicevoxClient.ApplyParams("not json", new VoiceParams()));
    }

    [Fact]
    public void Settings_round_trip_and_fall_back_to_defaults()
    {
        var dir = TempDir();
        try
        {
            Assert.Equal(VoicevoxClient.DefaultUrl, VoiceSettings.Load(dir).EngineUrl);
            new VoiceSettings { EngineUrl = "http://127.0.0.1:10101", StyleId = 3, AddSubtitle = false, Voice = new VoiceParams { Speed = 1.5 } }.Save(dir);
            var s = VoiceSettings.Load(dir);
            Assert.Equal(("http://127.0.0.1:10101", 3, false, 1.5), (s.EngineUrl, s.StyleId, s.AddSubtitle, s.Voice.Speed));

            File.WriteAllText(Path.Combine(dir, VoiceSettings.FileName), "{ broken");
            Assert.Equal(VoicevoxClient.DefaultUrl, VoiceSettings.Load(dir).EngineUrl);
            File.WriteAllText(Path.Combine(dir, VoiceSettings.FileName), """{"engineUrl":"file:///etc"}""");
            Assert.Equal(VoicevoxClient.DefaultUrl, VoiceSettings.Load(dir).EngineUrl);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void File_names_are_safe_and_unique()
    {
        Assert.Equal("こんにちは_世界_a_b", VoiceFiles.Safe("  こんにちは 世界/a:b  ", 20));
        Assert.Equal("あいう", VoiceFiles.Safe("あいうえお", 3));
        Assert.Equal("😀", VoiceFiles.Safe("😀😀", 3));   // サロゲートの途中で切らない
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var now = new DateTime(2026, 11, 22, 10, 30, 0);
            var first = VoiceFiles.NewPath(dir, "ずんだもん", "こんにちは\nまた明日", now);
            Assert.Equal("20261122-103000_ずんだもん_こんにちは_また明日.wav", Path.GetFileName(first));
            File.WriteAllBytes(first, Wav(0.1));
            Assert.EndsWith("_2.wav", VoiceFiles.NewPath(dir, "ずんだもん", "こんにちは\nまた明日", now));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Client_talks_to_the_engine_and_reports_errors_plainly()
    {
        using var engine = new FakeEngine();
        var client = new VoicevoxClient(engine.Url);
        Assert.Equal("0.21.1", await client.GetVersionAsync(default));

        // 歌う用のスタイル (sing・singing_teacher・frame_decode) は除く。streaming_talk (エンジン 0.26 から) はしゃべる用なので残す
        var styles = await client.GetStylesAsync(default);
        Assert.Equal(new[] { 2, 3, 1, 22 }, styles.Select(s => s.Id));
        Assert.Equal("ずんだもん (あまあま)", styles[2].Label);

        var wav = await client.SynthesizeAsync("こんにちは & さようなら", 1, new VoiceParams { Speed = 1.3 }, default);
        Assert.True(WavInfo.TryGetDuration(wav, out var sec));
        Assert.Equal(1.25, sec, 3);
        var query = engine.Requests.Single(r => r.PathAndQuery.StartsWith("/audio_query"));
        Assert.Equal("POST", query.Method);
        Assert.Contains("speaker=1", query.PathAndQuery);
        Assert.Equal("こんにちは & さようなら", Uri.UnescapeDataString(query.PathAndQuery.Split("text=")[1].Split('&')[0]));
        var synth = engine.Requests.Single(r => r.PathAndQuery.StartsWith("/synthesis"));
        Assert.Contains("speaker=1", synth.PathAndQuery);
        Assert.Equal(1.3, JsonNode.Parse(synth.Body)!["speedScale"]!.GetValue<double>(), 3);

        // つながらない・エラーが返る
        var down = await Assert.ThrowsAsync<VoicevoxException>(() => new VoicevoxClient(ClosedPortUrl()).GetVersionAsync(default));
        Assert.Contains("つながりません", down.Message);
        var bad = await Assert.ThrowsAsync<VoicevoxException>(() => new VoicevoxClient(engine.Url + "nothing/").GetVersionAsync(default));
        Assert.Contains("422", bad.Message);
    }

    // ---------------- タイムラインに置く ----------------

    [AvaloniaFact]
    public void Voice_and_subtitle_are_placed_together_on_free_layers()
    {
        var session = new EditorSession();
        session.CurrentFrame = 10;
        // レイヤー 1 の 10 フレーム目はふさがっている → 字幕と音声が並んで入るのはレイヤー 2・3
        session.Edit("準備", () => session.Scene.Objects.Add(ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 30)));
        var ctx = new PluginCommandContext(session, null, "voicevox", TempDir());
        var style = new VoiceStyle(3, "ずんだもん", "ノーマル");
        double fps = session.Project.Settings.Fps.Value;

        var placed = VoicePlacer.Place(ctx, "/tmp/voice.wav", 1.01, "こんにちは", style, new VoiceParams { Speed = 1.2 }, subtitle: true);
        Assert.NotNull(placed);
        var (audio, caption) = (placed!.Audio, placed.Subtitle!);
        int expected = (int)Math.Ceiling(1.01 * fps);
        Assert.Equal((2, 10, expected), (caption.Layer, caption.Start, caption.Length));
        Assert.Equal((3, 10, expected), (audio.Layer, audio.Start, audio.Length));
        Assert.Equal("こんにちは", caption.Main.Values["text"]);
        Assert.Equal("/tmp/voice.wav", audio.Main.Values["file"]);
        Assert.Equal(caption.GroupId, audio.GroupId);
        Assert.Equal("こんにちは", audio.PluginData![VoicePlacer.KeyText]);
        Assert.Equal("3", audio.PluginData[VoicePlacer.KeyStyleId]);
        Assert.Equal("ずんだもん (ノーマル)", audio.PluginData[VoicePlacer.KeySpeaker]);
        Assert.Contains("1.2", audio.PluginData[VoicePlacer.KeyParams]);

        // 字幕なし: 音声だけ
        var only = VoicePlacer.Place(ctx, "/tmp/voice2.wav", 0.5, "やあ", style, new VoiceParams(), subtitle: false);
        Assert.Null(only!.Subtitle);
        Assert.Null(only.Audio.GroupId);

        // 1 回で取り消せる
        Assert.Equal("VOICEVOX の音声を置く", session.History.UndoDescription);
        session.Undo();
        Assert.Contains(session.Scene.Objects, o => o.Id == caption.Id);
        session.Undo();
        Assert.DoesNotContain(session.Scene.Objects, o => o.Id == caption.Id || o.Id == audio.Id);
    }

    [AvaloniaFact]
    public void Nothing_changes_when_there_is_no_room()
    {
        var session = new EditorSession();
        // 下の 2 つのレイヤーだけが空いている状態から、字幕と音声 (2 つ) を最後のレイヤーに置こうとする
        session.Edit("準備", () => session.Scene.Objects.Add(ObjectFactory.Create(EffectRegistry.Shape, Scene.LayerCount - 2, 0, 30)));
        session.Select(session.Scene.Objects[0].Id);
        var ctx = new PluginCommandContext(session, null, "voicevox", TempDir());
        string? before = session.History.UndoDescription;
        Assert.Null(VoicePlacer.Place(ctx, "/tmp/v.wav", 1, "a", new VoiceStyle(3, "ずんだもん", "ノーマル"), new VoiceParams(), subtitle: true));
        Assert.Single(session.Scene.Objects);
        Assert.Equal("準備", before);
        Assert.Equal(before, session.History.UndoDescription);   // 空の取り消しを残さない
    }

    [AvaloniaFact]
    public void Form_is_prefilled_from_the_selected_text_and_last_settings()
    {
        var session = new EditorSession();
        var text = ObjectFactory.Create(EffectRegistry.Text, 0, 0, 30);
        text.Main.Values["text"] = "選んだテキスト";
        session.Edit("準備", () => session.Scene.Objects.Add(text));
        session.Select(text.Id);
        var ctx = new PluginCommandContext(session, null, "voicevox", TempDir());
        var styles = new List<VoiceStyle> { new(2, "四国めたん", "ノーマル"), new(3, "ずんだもん", "ノーマル") };

        var form = SynthesizeCommand.BuildForm(ctx, new VoiceSettings { StyleId = 3, Voice = new VoiceParams { Speed = 1.4 } }, styles);
        Assert.Equal("選んだテキスト", form.Get<PluginTextField>("text").Value);
        Assert.Equal("3", form.Get<PluginChoiceField>("style").Value);
        Assert.Equal(1.4, form.Get<PluginNumberField>("speed").Value);
        Assert.NotNull(form.Message);   // 保存していないので、置き場所の説明が出る

        // 前回のスタイルがもうなければ先頭
        Assert.Equal("2", SynthesizeCommand.BuildForm(ctx, new VoiceSettings { StyleId = 99 }, styles).Get<PluginChoiceField>("style").Value);

        form.Get<PluginTextField>("text").Value = "  こんにちは  ";
        var req = SynthesizeCommand.ReadForm(form, styles);
        Assert.Equal(("こんにちは", 3, 1.4), (req.Text, req.Style.Id, req.Voice.Speed));
        form.Get<PluginTextField>("text").Value = " ";
        Assert.Throws<VoicevoxException>(() => SynthesizeCommand.ReadForm(form, styles));
    }

    // ---------------- コマンドを通しで ----------------

    [AvaloniaFact]
    public async Task Synthesize_command_runs_end_to_end_against_the_engine()
    {
        using var engine = new FakeEngine();
        var owner = new Window();
        owner.Show();
        var session = new EditorSession();
        session.CurrentFrame = 60;
        var dataRoot = TempDir();
        var ctx = new PluginCommandContext(session, owner, "voicevox", dataRoot);
        new VoiceSettings { EngineUrl = engine.Url, StyleId = 1, Voice = new VoiceParams { Speed = 1.5 } }.Save(ctx.DataDirectory);
        try
        {
            var run = new SynthesizeCommand().RunAsync(ctx);
            await Until(() => owner.OwnedWindows.OfType<PluginFormDialog>().Any() || run.IsCompleted);
            var form = Assert.Single(owner.OwnedWindows.OfType<PluginFormDialog>());
            form.GetLogicalDescendants().OfType<TextBox>().First(t => t is not NumberBox).Text = "ずんだもんなのだ";
            Click(form, "作って置く");
            await Until(() => run.IsCompleted);
            await run;
            Assert.Empty(owner.OwnedWindows);   // エラーのメッセージは出ていない

            // 字幕 (レイヤー 0) と音声 (レイヤー 1) が再生位置に並ぶ
            var audio = Assert.Single(session.Scene.Objects, o => o.Main.DefinitionId == EffectRegistry.Audio);
            var caption = Assert.Single(session.Scene.Objects, o => o.Main.DefinitionId == EffectRegistry.Text);
            Assert.Equal((0, 60), (caption.Layer, caption.Start));
            Assert.Equal((1, 60), (audio.Layer, audio.Start));
            Assert.Equal("ずんだもんなのだ", caption.Main.Values["text"]);
            Assert.Equal((int)Math.Ceiling(1.25 * session.Project.Settings.Fps.Value), audio.Length);

            // 音声はプラグインのデータフォルダの voices に保存される (プロジェクトを保存していないため)
            var file = audio.Main.Values["file"];
            Assert.StartsWith(Path.Combine(ctx.DataDirectory, VoiceFiles.FolderName), file);
            Assert.True(File.Exists(file));
            Assert.Contains("ずんだもん", Path.GetFileName(file));

            // 前回の話者 (あまあま = 1) と話速がエンジンに渡り、設定に残る
            Assert.Contains(engine.Requests, r => r.PathAndQuery.StartsWith("/synthesis") && r.PathAndQuery.Contains("speaker=1") && r.Body.Contains("\"speedScale\":1.5"));
            Assert.Equal("1", audio.PluginData![VoicePlacer.KeyStyleId]);
            Assert.Equal(1, VoiceSettings.Load(ctx.DataDirectory).StyleId);
        }
        finally
        {
            owner.Close();
            try { Directory.Delete(dataRoot, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task Unreachable_engine_shows_a_message_and_changes_nothing()
    {
        var owner = new Window();
        owner.Show();
        var session = new EditorSession();
        var dataRoot = TempDir();
        var ctx = new PluginCommandContext(session, owner, "voicevox", dataRoot);
        new VoiceSettings { EngineUrl = ClosedPortUrl() }.Save(ctx.DataDirectory);
        try
        {
            var run = new SynthesizeCommand().RunAsync(ctx);
            await Until(() => owner.OwnedWindows.OfType<MessageDialog>().Any() || run.IsCompleted);
            var message = Assert.Single(owner.OwnedWindows.OfType<MessageDialog>());
            Assert.Contains("つながりません", Texts(message));
            message.Close();
            await Until(() => run.IsCompleted);
            await run;
            Assert.Empty(session.Scene.Objects);
            Assert.False(session.History.CanUndo);
        }
        finally
        {
            owner.Close();
            try { Directory.Delete(dataRoot, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task Settings_command_saves_the_url_and_checks_the_connection()
    {
        using var engine = new FakeEngine();
        var owner = new Window();
        owner.Show();
        var dataRoot = TempDir();
        var ctx = new PluginCommandContext(new EditorSession(), owner, "voicevox", dataRoot);
        try
        {
            var run = new SettingsCommand().RunAsync(ctx);
            await Until(() => owner.OwnedWindows.OfType<PluginFormDialog>().Any());
            var form = owner.OwnedWindows.OfType<PluginFormDialog>().Single();
            var box = form.GetLogicalDescendants().OfType<TextBox>().Single();
            Assert.Equal(VoicevoxClient.DefaultUrl, box.Text);
            box.Text = engine.Url;
            Click(form, "保存してつながるか確かめる");
            await Until(() => owner.OwnedWindows.OfType<MessageDialog>().Any());
            var message = owner.OwnedWindows.OfType<MessageDialog>().Single();
            Assert.Contains("0.21.1", Texts(message));
            message.Close();
            await Until(() => run.IsCompleted);
            Assert.Equal(engine.Url, VoiceSettings.Load(ctx.DataDirectory).EngineUrl);
        }
        finally
        {
            owner.Close();
            try { Directory.Delete(dataRoot, true); } catch { }
        }
    }
}
