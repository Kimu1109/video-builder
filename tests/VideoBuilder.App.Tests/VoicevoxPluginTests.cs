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
                    reply = Encoding.UTF8.GetBytes("""{"accent_phrases":[{"moras":[{"text":"コ","consonant":"k","consonant_length":0.05,"vowel":"o","vowel_length":0.1,"pitch":5.5},{"text":"ン","consonant":null,"consonant_length":null,"vowel":"N","vowel_length":0.08,"pitch":5.4},{"text":"マ","consonant":"m","consonant_length":0.06,"vowel":"a","vowel_length":0.12,"pitch":5.6}],"accent":1,"pause_mora":null,"is_interrogative":false}],"speedScale":1.0,"pitchScale":0.0,"intonationScale":1.0,"volumeScale":1.0,"prePhonemeLength":0.1,"postPhonemeLength":0.1,"outputSamplingRate":24000,"outputStereo":false,"kana":""}""");
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

        var synthesis = await client.SynthesizeAsync("こんにちは & さようなら", 1, new VoiceParams { Speed = 1.3 }, default);
        Assert.True(WavInfo.TryGetDuration(synthesis.Wav, out var sec));
        Assert.Equal(1.3, JsonNode.Parse(synthesis.Query)!["speedScale"]!.GetValue<double>(), 3);   // 合成に使った audio_query も返る
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

        var placed = VoicePlacer.Place(ctx, new VoiceClip("/tmp/voice.wav", 1.01, "こんにちは", style, new VoiceParams { Speed = 1.2 }), subtitle: true);
        Assert.NotNull(placed);
        var (audio, caption) = (placed!.Audio, placed.Subtitle!);
        int expected = (int)Math.Ceiling(1.01 * fps);
        Assert.Equal((2, 10, expected), (caption.Layer, caption.Start, caption.Length));
        Assert.Equal((3, 10, expected), (audio.Layer, audio.Start, audio.Length));
        Assert.Equal("こんにちは", caption.Main.Values["text"]);
        Assert.Equal("/tmp/voice.wav", audio.Main.Values["file"]);
        Assert.Equal(caption.GroupId, audio.GroupId);
        Assert.Equal("こんにちは", audio.PluginData![VoiceInfo.KeyText]);
        Assert.Equal("3", audio.PluginData[VoiceInfo.KeyStyleId]);
        Assert.Equal("ずんだもん (ノーマル)", audio.PluginData[VoiceInfo.KeySpeaker]);
        Assert.Contains("1.2", audio.PluginData[VoiceInfo.KeyParams]);

        // 字幕なし: 音声だけ
        var only = VoicePlacer.Place(ctx, new VoiceClip("/tmp/voice2.wav", 0.5, "やあ", style, new VoiceParams()), subtitle: false);
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
        Assert.Null(VoicePlacer.Place(ctx, new VoiceClip("/tmp/v.wav", 1, "a", new VoiceStyle(3, "ずんだもん", "ノーマル"), new VoiceParams()), subtitle: true));
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
            Assert.Equal("1", audio.PluginData![VoiceInfo.KeyStyleId]);
            Assert.Equal(1, VoiceSettings.Load(ctx.DataDirectory).StyleId);
            // 口パクの時刻表とキャラクター名も残る (「コ・ン・マ」→ お・閉・あ)
            Assert.Equal("ずんだもん", audio.PluginData[VoiceInfo.KeyCharacter]);
            Assert.Equal(new[] { Mouth.Closed, Mouth.O, Mouth.Closed, Mouth.A, Mouth.Closed },
                LipSync.Decode(audio.PluginData[VoiceInfo.KeyLips]).Points.Select(x => x.Mouth));
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
            Assert.Contains("話者 2 人・スタイル 4 個", Texts(message));   // 話者の一覧も読めている
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

    // ---------------- 台本から一括で作る ----------------

    private static readonly List<VoiceStyle> Styles = new()
    {
        new(2, "四国めたん", "ノーマル"), new(3, "ずんだもん", "ノーマル"), new(1, "ずんだもん", "あまあま"), new(22, "ずんだもん", "ささやき"),
    };

    [Fact]
    public void Script_lines_pick_speakers_by_name()
    {
        var zundaSweet = Styles[2];
        var lines = ScriptParser.Parse("""
            # コメントの行は飛ばす
            ずんだもん: こんにちはなのだ
            四国めたん：こんにちは

            ずんだもん（ささやき）： ないしょなのだ
            ず ん だ も ん (ノーマル):ふつうなのだ
            時刻は 10:30 です
            知らない人: だれ？
            話者のない行
            ずんだもん:
            """, Styles, zundaSweet);
        Assert.Equal(new[]
        {
            (2, "こんにちはなのだ", 1),        // キャラクター名だけ: 既定と同じキャラクターなので既定のスタイル (あまあま)
            (3, "こんにちは", 2),              // 全角のコロン。ほかのキャラクターは最初のスタイル
            (5, "ないしょなのだ", 22),         // スタイル付き (全角の括弧)
            (6, "ふつうなのだ", 3),            // 空白は無視
            (7, "時刻は 10:30 です", 1),       // コロンの前が話者でなければ、行全体を既定の話者で
            (8, "知らない人: だれ？", 1),
            (9, "話者のない行", 1),
        }, lines.Select(l => (l.LineNumber, l.Text, l.Style.Id)));
    }

    private static VoiceClip Clip(string text, double seconds, int style = 3) =>
        new($"/tmp/{text}.wav", seconds, text, Styles.First(s => s.Id == style), new VoiceParams());

    [AvaloniaFact]
    public void Script_clips_are_laid_out_in_order_on_one_pair_of_layers()
    {
        var session = new EditorSession();
        session.CurrentFrame = 30;
        // レイヤー 1 の 100 フレーム目だけふさがっている → 3 つ目がぶつかるので、レイヤー 2・3 に並べる
        session.Edit("準備", () => session.Scene.Objects.Add(ObjectFactory.Create(EffectRegistry.Shape, 1, 100, 1)));
        var ctx = new PluginCommandContext(session, null, "voicevox", TempDir());
        int F(double sec) => (int)Math.Ceiling(sec * session.Project.Settings.Fps.Value - 1e-6);

        var placed = VoicePlacer.PlaceMany(ctx, new[] { Clip("一", 1.0), Clip("二", 0.5, 1), Clip("三", 0.8, 2) }, gapFrames: 6, subtitle: true, "台本");
        Assert.NotNull(placed);
        Assert.Equal(3, placed!.Count);
        int start = 30;
        foreach (var (p, sec) in placed.Zip(new[] { 1.0, 0.5, 0.8 }))
        {
            Assert.Equal((2, start, F(sec)), (p.Subtitle!.Layer, p.Subtitle.Start, p.Subtitle.Length));
            Assert.Equal((3, start, F(sec)), (p.Audio.Layer, p.Audio.Start, p.Audio.Length));
            Assert.Equal(p.Subtitle.GroupId, p.Audio.GroupId);
            start += F(sec) + 6;
        }
        Assert.Equal(3, placed.Select(p => p.Audio.GroupId).Distinct().Count());   // 1 組ずつ別のグループ
        Assert.Equal("1", placed[1].Audio.PluginData![VoiceInfo.KeyStyleId]);
        Assert.Equal(6, session.SelectionIds.Count);

        // まとめて 1 回で取り消せる
        Assert.Equal("台本", session.History.UndoDescription);
        session.Undo();
        Assert.Single(session.Scene.Objects);
    }

    // ---------------- 作り直す ----------------

    [AvaloniaFact]
    public void Targets_are_found_from_audio_or_its_subtitle_and_replaced_in_place()
    {
        var session = new EditorSession();
        var ctx = new PluginCommandContext(session, null, "voicevox", TempDir());
        int F(double sec) => (int)Math.Ceiling(sec * session.Project.Settings.Fps.Value - 1e-6);
        var placed = VoicePlacer.PlaceMany(ctx, new[] { Clip("一", 1.0), Clip("二", 1.0) }, gapFrames: 0, subtitle: true, "台本")!;
        var regen = new RegenerateCommand();

        // 何も選んでいない・VOICEVOX でないものだけを選んでいる → 対象なし
        session.Select(null);
        Assert.False(regen.CanRun(ctx));
        var shape = ObjectFactory.Create(EffectRegistry.Shape, 10, 0, 10);
        session.Edit("図形", () => session.Scene.Objects.Add(shape));
        session.Select(shape.Id);
        Assert.Empty(VoicePlacer.FindTargets(ctx));

        // 字幕を選んでも、同じグループの音声が対象になる。字幕を直していれば、直した文字が今のセリフ
        var first = placed[0];
        session.Edit("字幕を直す", () => session.Scene.Find(first.Subtitle!.Id)!.Main.Values["text"] = "直したセリフ");
        session.Select(first.Subtitle!.Id);
        Assert.True(regen.CanRun(ctx));
        var target = Assert.Single(VoicePlacer.FindTargets(ctx));
        Assert.Equal(first.Audio.Id, target.Audio.Id);
        Assert.Equal("直したセリフ", target.CurrentText);
        Assert.Equal("一", target.Info.Text);

        // 短くなるものは差し替える。長くなって次のセリフにぶつかるものは差し替えない
        session.SelectMany(new[] { first.Audio.Id, placed[1].Audio.Id });
        var targets = VoicePlacer.FindTargets(ctx);
        Assert.Equal(2, targets.Count);
        var blocked = VoicePlacer.Replace(ctx, new[]
        {
            (targets[0], Clip("直したセリフ", 0.5, 22) with { Path = "/tmp/new1.wav" }),
        });
        Assert.Empty(blocked);
        var audio = session.Scene.Find(first.Audio.Id)!;
        var caption = session.Scene.Find(first.Subtitle.Id)!;
        Assert.Equal("/tmp/new1.wav", audio.Main.Values["file"]);
        Assert.Equal(F(0.5), audio.Length);
        Assert.Equal(F(0.5), caption.Length);
        Assert.Equal("直したセリフ", caption.Main.Values["text"]);
        Assert.Equal("22", audio.PluginData![VoiceInfo.KeyStyleId]);
        Assert.Equal("直したセリフ", audio.PluginData[VoiceInfo.KeyText]);
        Assert.Equal("VOICEVOX の音声を作り直す", session.History.UndoDescription);

        // 1 つ目を元の長さより長くすると、すぐ後ろの 2 つ目にぶつかる
        var longer = VoicePlacer.Replace(ctx, new[] { (VoicePlacer.FindTargets(ctx)[0], Clip("長い", 1.5)) });
        Assert.Single(longer);
        Assert.Equal(F(0.5), session.Scene.Find(first.Audio.Id)!.Length);   // 変わらない
        Assert.Equal("VOICEVOX の音声を作り直す", session.History.UndoDescription);

        // 取り消すと、元のファイル・長さ・情報に戻る
        session.Undo();
        audio = session.Scene.Find(first.Audio.Id)!;
        Assert.Equal("/tmp/一.wav", audio.Main.Values["file"]);
        Assert.Equal(F(1.0), audio.Length);
        Assert.Equal("一", VoiceInfo.Read(audio)!.Text);
    }

    [AvaloniaFact]
    public void Regenerate_form_keeps_each_speaker_when_many_are_selected()
    {
        var session = new EditorSession();
        var ctx = new PluginCommandContext(session, null, "voicevox", TempDir());
        var placed = VoicePlacer.PlaceMany(ctx, new[] { Clip("一", 1.0, 3), Clip("二", 1.0, 2) }, 0, subtitle: false, "台本")!;
        session.SelectMany(placed.Select(p => p.Audio.Id));
        var targets = VoicePlacer.FindTargets(ctx);

        var form = RegenerateCommand.BuildForm(ctx, targets, Styles);
        Assert.Equal(RegenerateCommand.Keep, form.Get<PluginChoiceField>("style").Value);
        Assert.Equal(new[] { ("一", 3), ("二", 2) }, RegenerateCommand.ReadForm(form, targets, Styles).Select(r => (r.Text, r.Style.Id)));
        form.Get<PluginChoiceField>("style").Value = "22";
        Assert.All(RegenerateCommand.ReadForm(form, targets, Styles), r => Assert.Equal(22, r.Style.Id));

        // 1 つなら、セリフ・話者・調整の値を変えられる
        var single = RegenerateCommand.BuildForm(ctx, targets.Take(1).ToList(), Styles);
        Assert.Equal("一", single.Get<PluginTextField>("text").Value);
        Assert.Equal("3", single.Get<PluginChoiceField>("style").Value);

        // 作ったときの話者が今のエンジンにない
        var others = new List<VoiceStyle> { new(99, "だれか", "ノーマル") };
        form.Get<PluginChoiceField>("style").Value = RegenerateCommand.Keep;
        Assert.Throws<VoicevoxException>(() => RegenerateCommand.ReadForm(form, targets, others));
    }

    [AvaloniaFact]
    public async Task Script_and_regenerate_commands_run_end_to_end_against_the_engine()
    {
        using var engine = new FakeEngine { Seconds = 0.5 };
        var owner = new Window();
        owner.Show();
        var session = new EditorSession();
        var dataRoot = TempDir();
        var ctx = new PluginCommandContext(session, owner, "voicevox", dataRoot);
        new VoiceSettings { EngineUrl = engine.Url, StyleId = 3, ScriptGap = 0.2 }.Save(ctx.DataDirectory);
        int F(double sec) => (int)Math.Ceiling(sec * session.Project.Settings.Fps.Value - 1e-6);
        try
        {
            // 台本: 3 行 (話者付き 2 行)
            var run = new ScriptCommand().RunAsync(ctx);
            await Until(() => owner.OwnedWindows.OfType<PluginFormDialog>().Any() || run.IsCompleted);
            var form = Assert.Single(owner.OwnedWindows.OfType<PluginFormDialog>());
            form.GetLogicalDescendants().OfType<TextBox>().First(t => t is not NumberBox).Text = "ずんだもん: いち\n四国めたん: に\nさん";
            Click(form, "作って並べる");
            await Until(() => run.IsCompleted);
            await run;
            Assert.Empty(owner.OwnedWindows);
            var audios = session.Scene.Objects.Where(o => o.Main.DefinitionId == EffectRegistry.Audio).OrderBy(o => o.Start).ToList();
            Assert.Equal(3, audios.Count);
            int gap = (int)Math.Round(0.2 * session.Project.Settings.Fps.Value);   // セリフの間 0.2 秒
            Assert.Equal(new[] { 0, F(0.5) + gap, 2 * (F(0.5) + gap) }, audios.Select(a => a.Start));
            Assert.Equal(new[] { "3", "2", "3" }, audios.Select(a => a.PluginData![VoiceInfo.KeyStyleId]));
            Assert.Equal(new[] { "speaker=3", "speaker=2", "speaker=3" },
                engine.Requests.Where(r => r.PathAndQuery.StartsWith("/synthesis")).Select(r => r.PathAndQuery.Split('?')[1]));
            Assert.Equal(0.2, VoiceSettings.Load(ctx.DataDirectory).ScriptGap);

            // 2 つ目の字幕を直して、字幕を選んで作り直す (エンジンは今度は 0.4 秒の音声を返すので、短くなる)
            engine.Seconds = 0.4;
            var second = audios[1];
            var oldFile = second.Main.Values["file"];
            var caption = session.Scene.Objects.Single(o => o.GroupId == second.GroupId && o.Main.DefinitionId == EffectRegistry.Text);
            session.Edit("字幕を直す", () => caption.Main.Values["text"] = "にい");
            session.Select(caption.Id);
            var regen = new RegenerateCommand().RunAsync(ctx);
            await Until(() => owner.OwnedWindows.OfType<PluginFormDialog>().Any() || regen.IsCompleted);
            var regenForm = Assert.Single(owner.OwnedWindows.OfType<PluginFormDialog>());
            Assert.Equal("にい", regenForm.GetLogicalDescendants().OfType<TextBox>().First(t => t is not NumberBox).Text);
            Click(regenForm, "作り直す");
            await Until(() => regen.IsCompleted);
            await regen;
            Assert.Empty(owner.OwnedWindows);
            var after = session.Scene.Find(second.Id)!;
            Assert.Equal(F(0.4), after.Length);
            Assert.NotEqual(oldFile, after.Main.Values["file"]);   // 前のファイルは残す (取り消せるように)
            Assert.True(File.Exists(oldFile));
            Assert.True(File.Exists(after.Main.Values["file"]));
            Assert.Equal("にい", after.PluginData![VoiceInfo.KeyText]);
            Assert.Equal("2", after.PluginData[VoiceInfo.KeyStyleId]);   // 話者はそのまま
            Assert.Contains(engine.Requests, r => r.PathAndQuery.StartsWith("/audio_query") && Uri.UnescapeDataString(r.PathAndQuery).Contains("text=にい"));
        }
        finally
        {
            owner.Close();
            try { Directory.Delete(dataRoot, true); } catch { }
        }
    }

    // ---------------- 字幕のテンプレート ----------------

    [AvaloniaFact]
    public void Subtitle_plan_uses_speaker_templates_then_the_default()
    {
        using var lib = new TemplateFixture();
        var band = lib.Add("字幕の帯");
        var green = lib.Add("緑の帯", textLabel: "本文");
        var noText = lib.Add("文字なし", textLabel: null);
        var ctx = new PluginCommandContext(new EditorSession(), null, "voicevox", TempDir(), lib.Store);
        var settings = new VoiceSettings
        {
            SubtitleTemplate = new SubtitleTemplate { Id = band, Name = "字幕の帯" },
            SpeakerTemplates =
            {
                ["ずんだもん"] = new SubtitleTemplate { Id = green, Name = "緑の帯" },
                ["四国めたん"] = new SubtitleTemplate(),   // テンプレートを使わない
            },
        };
        var plan = SubtitlePlan.Resolve(ctx, settings, new[] { "ずんだもん", "四国めたん", "春日部つむぎ" });
        Assert.Equal(("緑の帯", "本文"), (plan.For(new VoiceStyle(3, "ずんだもん", "ノーマル"))!.Template.Name, plan.For(new VoiceStyle(3, "ずんだもん", "ノーマル"))!.Field));
        Assert.Null(plan.For(new VoiceStyle(2, "四国めたん", "ノーマル")));
        Assert.Equal(("字幕の帯", "セリフ"), (plan.For(new VoiceStyle(8, "春日部つむぎ", "ノーマル"))!.Template.Name, plan.For(new VoiceStyle(8, "春日部つむぎ", "ノーマル"))!.Field));
        Assert.Null(SubtitlePlan.Plain.For(new VoiceStyle(3, "ずんだもん", "ノーマル")));

        // 文字の項目がない・指定した項目がない・ライブラリにない → 先に知らせる
        settings.SubtitleTemplate = new SubtitleTemplate { Id = noText, Name = "文字なし" };
        Assert.Contains("文字の公開項目がありません", Assert.Throws<VoicevoxException>(() => SubtitlePlan.Resolve(ctx, settings, new[] { "春日部つむぎ" })).Message);
        settings.SubtitleTemplate = new SubtitleTemplate { Id = band, Name = "字幕の帯", Field = "名前" };
        var missingField = Assert.Throws<VoicevoxException>(() => SubtitlePlan.Resolve(ctx, settings, new[] { "春日部つむぎ" }));
        Assert.Contains("セリフ、帯の色", missingField.Message);
        settings.SubtitleTemplate = new SubtitleTemplate { Id = "gone", Name = "消したテンプレート" };
        Assert.Contains("消したテンプレート", Assert.Throws<VoicevoxException>(() => SubtitlePlan.Resolve(ctx, settings, new[] { "春日部つむぎ" })).Message);
        // 字幕を置かないなら、テンプレートは確かめない
        Assert.Same(SubtitlePlan.Plain, Plugin.PlanSubtitles(ctx, settings, false, Styles));
    }

    [AvaloniaFact]
    public void Template_subtitles_are_placed_and_regenerated()
    {
        using var lib = new TemplateFixture();
        var band = lib.Add("字幕の帯");
        var session = new EditorSession();
        var ctx = new PluginCommandContext(session, null, "voicevox", TempDir(), lib.Store);
        int F(double sec) => (int)Math.Ceiling(sec * session.Project.Settings.Fps.Value - 1e-6);
        var settings = new VoiceSettings { SubtitleTemplate = new SubtitleTemplate { Id = band, Name = "字幕の帯" }, SpeakerTemplates = { ["四国めたん"] = new SubtitleTemplate() } };
        var clips = new[] { Clip("いち", 1.0, 3), Clip("に", 0.5, 2) };
        var plan = SubtitlePlan.Resolve(ctx, settings, clips.Select(c => c.Style.Speaker));

        var placed = VoicePlacer.PlaceMany(ctx, clips, 0, subtitle: true, "台本", plan)!;
        // ずんだもん: テンプレート (シーンオブジェクト)、四国めたん: 文字だけ。どちらも字幕のレイヤーに並ぶ
        var first = placed[0];
        Assert.Equal(EffectRegistry.SceneRef, first.Subtitle!.Main.DefinitionId);
        Assert.Equal((0, F(1.0)), (first.Subtitle.Layer, first.Subtitle.Length));
        Assert.Equal("いち", ctx.TemplateFields(first.Subtitle).Single(f => f.Label == "セリフ").Value);
        Assert.Equal("セリフ", first.Audio.PluginData![VoiceInfo.KeySubtitleField]);
        Assert.Equal(first.Subtitle.GroupId, first.Audio.GroupId);
        Assert.Equal(EffectRegistry.Text, placed[1].Subtitle!.Main.DefinitionId);
        Assert.False(placed[1].Audio.PluginData!.ContainsKey(VoiceInfo.KeySubtitleField));

        // テンプレートの項目でセリフを直してから、字幕を選んで作り直す
        ctx.Edit("直す", e => e.SetTemplateValues(first.Subtitle, new Dictionary<string, string> { ["セリフ"] = "いちばん" }));
        session.Select(first.Subtitle.Id);
        var target = Assert.Single(VoicePlacer.FindTargets(ctx));
        Assert.Equal(("いちばん", "セリフ"), (target.CurrentText, target.SubtitleField));
        Assert.Empty(VoicePlacer.Replace(ctx, new[] { (target, Clip("いちばんなのだ", 0.6, 3)) }));
        var caption = session.Scene.Find(first.Subtitle.Id)!;
        Assert.Equal(F(0.6), caption.Length);
        Assert.Equal(F(0.6), session.Scene.Find(first.Audio.Id)!.Length);
        Assert.Equal("いちばんなのだ", ctx.TemplateFields(caption).Single(f => f.Label == "セリフ").Value);
        Assert.Equal("#203040", ctx.TemplateFields(caption).Single(f => f.Label == "帯の色").Value);   // ほかの項目はそのまま

        // 文字だけの字幕も、これまでどおり
        session.Select(placed[1].Audio.Id);
        Assert.Null(Assert.Single(VoicePlacer.FindTargets(ctx)).SubtitleField);
    }

    [AvaloniaFact]
    public void Template_command_forms_save_default_and_speaker_choices()
    {
        using var lib = new TemplateFixture();
        var band = lib.Add("字幕の帯");
        var ctx = new PluginCommandContext(new EditorSession(), null, "voicevox", TempDir(), lib.Store);
        var templates = ctx.ListTemplates();
        var settings = new VoiceSettings();

        // 既定: テンプレートを選ぶ
        var form = SubtitleTemplateCommand.BuildTemplateForm(settings, SubtitleTemplateCommand.DefaultTarget, templates);
        Assert.Equal(SubtitleTemplateCommand.PlainText, form.Get<PluginChoiceField>("template").Value);
        Assert.DoesNotContain(form.Get<PluginChoiceField>("template").Options, o => o.Value == SubtitleTemplateCommand.FollowDefault);
        form.Get<PluginChoiceField>("template").Value = band;
        SubtitleTemplateCommand.Apply(ctx, settings, SubtitleTemplateCommand.DefaultTarget, form, templates);
        Assert.Equal((band, "字幕の帯", ""), (settings.SubtitleTemplate!.Id, settings.SubtitleTemplate.Name, settings.SubtitleTemplate.Field));

        // キャラクター: 文字だけ → 既定に従う (指定を消す)
        var zunda = SubtitleTemplateCommand.BuildTemplateForm(settings, "ずんだもん", templates);
        Assert.Equal(SubtitleTemplateCommand.FollowDefault, zunda.Get<PluginChoiceField>("template").Value);
        zunda.Get<PluginChoiceField>("template").Value = SubtitleTemplateCommand.PlainText;
        SubtitleTemplateCommand.Apply(ctx, settings, "ずんだもん", zunda, templates);
        Assert.True(settings.SpeakerTemplates["ずんだもん"].IsPlain);
        zunda.Get<PluginChoiceField>("template").Value = SubtitleTemplateCommand.FollowDefault;
        SubtitleTemplateCommand.Apply(ctx, settings, "ずんだもん", zunda, templates);
        Assert.False(settings.SpeakerTemplates.ContainsKey("ずんだもん"));

        // ない項目を指定すると保存しない
        var bad = SubtitleTemplateCommand.BuildTemplateForm(settings, "四国めたん", templates);
        bad.Get<PluginChoiceField>("template").Value = band;
        bad.Get<PluginTextField>("field").Value = "名前";
        Assert.Throws<VoicevoxException>(() => SubtitleTemplateCommand.Apply(ctx, settings, "四国めたん", bad, templates));
        Assert.False(settings.SpeakerTemplates.ContainsKey("四国めたん"));

        // 今の設定が 1 つ目のフォームに出る。保存して読み直しても残る
        var target = SubtitleTemplateCommand.BuildTargetForm(settings, new[] { "ずんだもん" }, null);
        Assert.Contains("すべての話者の既定: 字幕の帯", target.Message);
        var dir = TempDir();
        settings.SpeakerTemplates["四国めたん"] = new SubtitleTemplate { Id = band, Name = "字幕の帯", Field = "セリフ" };
        settings.Save(dir);
        var loaded = VoiceSettings.Load(dir);
        Assert.Equal(band, loaded.SubtitleTemplate!.Id);
        Assert.Equal("セリフ", loaded.SpeakerTemplates["四国めたん"].Field);
    }

    [AvaloniaFact]
    public async Task Template_command_and_synthesis_use_the_chosen_template_end_to_end()
    {
        using var engine = new FakeEngine();
        using var lib = new TemplateFixture();
        var band = lib.Add("字幕の帯");
        var owner = new Window();
        owner.Show();
        var session = new EditorSession();
        var dataRoot = TempDir();
        var ctx = new PluginCommandContext(session, owner, "voicevox", dataRoot, lib.Store);
        new VoiceSettings { EngineUrl = engine.Url, StyleId = 3 }.Save(ctx.DataDirectory);
        try
        {
            // 字幕のテンプレート: ずんだもん → 字幕の帯
            var run = new SubtitleTemplateCommand().RunAsync(ctx);
            await Until(() => owner.OwnedWindows.OfType<PluginFormDialog>().Any());
            var first = owner.OwnedWindows.OfType<PluginFormDialog>().Single();
            var combo = first.GetLogicalDescendants().OfType<ComboBox>().Single();
            combo.SelectedIndex = combo.ItemsSource!.Cast<string>().ToList().FindIndex(x => x == "ずんだもん");
            Click(first, "次へ");
            await Until(() => owner.OwnedWindows.OfType<PluginFormDialog>().Any(d => d != first));
            var second = owner.OwnedWindows.OfType<PluginFormDialog>().Single();
            var pick = second.GetLogicalDescendants().OfType<ComboBox>().Single();
            pick.SelectedIndex = pick.ItemsSource!.Cast<string>().ToList().FindIndex(x => x.EndsWith("字幕の帯"));
            Click(second, "保存");
            await Until(() => run.IsCompleted);
            await run;
            Assert.Equal(band, VoiceSettings.Load(ctx.DataDirectory).SpeakerTemplates["ずんだもん"].Id);

            // セリフから音声を作ると、字幕はテンプレートで置かれる
            var synth = new SynthesizeCommand().RunAsync(ctx);
            await Until(() => owner.OwnedWindows.OfType<PluginFormDialog>().Any());
            var form = owner.OwnedWindows.OfType<PluginFormDialog>().Single();
            form.GetLogicalDescendants().OfType<TextBox>().First(t => t is not NumberBox).Text = "テンプレートなのだ";
            Click(form, "作って置く");
            await Until(() => synth.IsCompleted);
            await synth;
            Assert.Empty(owner.OwnedWindows);
            var caption = Assert.Single(session.Scene.Objects, o => o.Main.DefinitionId == EffectRegistry.SceneRef);
            Assert.Equal("テンプレートなのだ", ctx.TemplateFields(caption).Single(f => f.Label == "セリフ").Value);
            Assert.Equal("字幕の帯", session.TemplateOf(caption)!.Name);
        }
        finally
        {
            owner.Close();
            try { Directory.Delete(dataRoot, true); } catch { }
        }
    }

    // ---------------- 口パク ----------------

    private const string KonmaQuery = """
        {"accent_phrases":[
          {"moras":[{"text":"コ","consonant":"k","consonant_length":0.05,"vowel":"o","vowel_length":0.10},
                    {"text":"ン","consonant":null,"consonant_length":null,"vowel":"N","vowel_length":0.08},
                    {"text":"マ","consonant":"m","consonant_length":0.06,"vowel":"a","vowel_length":0.12}],
           "pause_mora":{"text":"、","consonant":null,"consonant_length":null,"vowel":"pau","vowel_length":0.2}},
          {"moras":[{"text":"イ","consonant":null,"consonant_length":null,"vowel":"i","vowel_length":0.01},
                    {"text":"ス","consonant":"s","consonant_length":0.07,"vowel":"U","vowel_length":0.09}],
           "pause_mora":null}],
         "speedScale":1.0,"prePhonemeLength":0.1,"postPhonemeLength":0.1}
        """;

    [Fact]
    public void Lip_sync_is_built_from_the_query_and_scaled_to_the_voice()
    {
        // 合計 0.1 + (0.05+0.1) + 0.08 + (0.06+0.12) + 0.2 + 0.01 + (0.07+0.09) + 0.1 = 0.98 秒
        var lips = LipSync.FromQuery(KonmaQuery, 0.98)!;
        Assert.Equal(new[]
        {
            (0.0, Mouth.Closed),
            (0.1, Mouth.O),        // k の子音から「お」の口
            (0.25, Mouth.Closed),  // ん → 閉じる。続く m (唇を閉じる子音) も閉じたまま
            (0.39, Mouth.A),
            (0.51, Mouth.Closed),  // 間 (、)
            (0.72, Mouth.U),       // 0.01 秒の「い」は短すぎるので飛ばす。無声の U も「う」の口
            (0.88, Mouth.Closed),
        }, lips.Points.Select(p => (Math.Round(p.Time, 2), p.Mouth)));

        // 話速などで実際の音声が倍の長さなら、時刻表も倍になる
        var slow = LipSync.FromQuery(KonmaQuery, 1.96)!;
        Assert.Equal(0.2, slow.Points[1].Time, 3);
        Assert.Equal(Mouth.A, slow.At(0.39 * 2 + 0.01));

        Assert.Equal(Mouth.Closed, lips.At(-1));
        Assert.Equal(Mouth.O, lips.At(0.2));
        Assert.Equal(Mouth.Closed, lips.At(5));
        Assert.Null(LipSync.FromQuery("not json", 1));

        // 文字列にして読み直す。壊れた部分は飛ばす
        var text = lips.Encode();
        Assert.StartsWith("0:-;0.1:o;0.25:-;0.39:a", text);
        Assert.Equal(lips.Points, LipSync.Decode(text).Points);
        Assert.Equal(new[] { (0.0, Mouth.Closed), (0.5, Mouth.A) }, LipSync.Decode("0:-;x:a;0.3:z;0.5:a;0.4:o").Points);
    }

    [Fact]
    public void Mouth_images_fall_back_to_a_then_closed()
    {
        var e = new Effect(MouthSourceDefinition());
        Assert.Equal("", MouthSource.ImageFor(e, Mouth.A));
        e.Values["closed"] = "/c.png";
        Assert.Equal("/c.png", MouthSource.ImageFor(e, Mouth.I));   // 閉じた絵しかない
        e.Values["a"] = "/a.png";
        Assert.Equal("/a.png", MouthSource.ImageFor(e, Mouth.I));   // 開け閉めだけ
        e.Values["i"] = "/i.png";
        Assert.Equal("/i.png", MouthSource.ImageFor(e, Mouth.I));
        Assert.Equal("/c.png", MouthSource.ImageFor(e, Mouth.Closed));
        e.Values["closed"] = "";
        Assert.Equal("/a.png", MouthSource.ImageFor(e, Mouth.Closed));
    }

    private static EffectDefinition MouthSourceDefinition()
    {
        if (!EffectRegistry.Exists(MouthSource.Id))
        {
            EffectRegistry.Register(MouthSource.Definition);
            VideoBuilder.Rendering.Effects.RegisterSource(MouthSource.Id, new MouthSource());
        }
        return EffectRegistry.Get(MouthSource.Id);
    }

    /// <summary>このプラグインで作った音声オブジェクト (口パクの時刻表付き)。</summary>
    private static TimelineObject Voice(int layer, int start, int length, string character, string lips, bool withCharacterKey = true)
    {
        var o = ObjectFactory.Create(EffectRegistry.Audio, layer, start, length);
        o.PluginData = new Dictionary<string, string> { [VoiceInfo.KeyText] = "x", [VoiceInfo.KeyStyleId] = "3", [VoiceInfo.KeySpeaker] = $"{character} (ノーマル)", [VoiceInfo.KeyLips] = lips };
        if (withCharacterKey) o.PluginData[VoiceInfo.KeyCharacter] = character;
        return o;
    }

    [Fact]
    public void Mouth_follows_the_characters_voice_in_the_same_scene()
    {
        var p = new Project();
        double fps = p.Settings.Fps.Value;
        int F(double sec) => (int)Math.Round(sec * fps);
        var owner = ObjectFactory.Create(MouthSourceDefinition().Id, 5, 0, F(10));
        p.Root.Objects.Add(owner);
        // ずんだもん: 1 秒目から 2 秒間。0.5 秒目から「あ」
        var zunda = Voice(1, F(1), F(2), "ずんだもん", "0:-;0.5:a;1.5:-");
        // 四国めたん: 同じ時間に「お」(キャラクターを指定すれば無視される)
        var metan = Voice(2, F(1), F(2), "四国めたん", "0:o", withCharacterKey: false);
        p.Root.Objects.Add(zunda);
        p.Root.Objects.Add(metan);

        Assert.Equal(Mouth.Closed, MouthSource.CurrentMouth(p, owner, "ずんだもん", F(1.2)));
        Assert.Equal(Mouth.A, MouthSource.CurrentMouth(p, owner, "ずんだもん", F(1.6)));
        Assert.Equal(Mouth.A, MouthSource.CurrentMouth(p, owner, " ずんだもん ", F(1.6)));   // 空白は無視
        Assert.Equal(Mouth.Closed, MouthSource.CurrentMouth(p, owner, "ずんだもん", F(2.6)));
        Assert.Equal(Mouth.Closed, MouthSource.CurrentMouth(p, owner, "ずんだもん", F(5)));      // 鳴っていない
        Assert.Equal(Mouth.O, MouthSource.CurrentMouth(p, owner, "四国めたん", F(1.2)));      // キャラクター名を残していない古い音声
        Assert.Equal(Mouth.O, MouthSource.CurrentMouth(p, owner, "", F(1.2)));                // 指定なし: 口を開けているもの

        // 開始位置・再生速度・分割のずれも見る
        zunda.Main.Tracks["position"] = new AnimatedValue(0.5);
        Assert.Equal(Mouth.A, MouthSource.CurrentMouth(p, owner, "ずんだもん", F(1.1)));
        zunda.Main.Tracks["position"] = new AnimatedValue(0);
        zunda.Main.Tracks["speed"] = new AnimatedValue(200);
        Assert.Equal(Mouth.A, MouthSource.CurrentMouth(p, owner, "ずんだもん", F(1.3)));    // 0.3 秒 x 2 = 0.6 秒目

        // 無効・非表示のレイヤーの音声は見ない。別のシーンの音声も見ない
        zunda.Main.Tracks["speed"] = new AnimatedValue(100);
        zunda.Enabled = false;
        Assert.Equal(Mouth.Closed, MouthSource.CurrentMouth(p, owner, "ずんだもん", F(1.6)));
        zunda.Enabled = true;
        p.Root.Layers[1] = new LayerInfo { Visible = false };
        Assert.Equal(Mouth.Closed, MouthSource.CurrentMouth(p, owner, "ずんだもん", F(1.6)));
        var other = new Scene { Id = "other", Name = "別" };
        p.Scenes.Add(other);
        var lonely = ObjectFactory.Create(MouthSource.Id, 0, 0, F(10));
        other.Objects.Add(lonely);
        Assert.Equal(Mouth.Closed, MouthSource.CurrentMouth(p, lonely, "四国めたん", F(1.2)));
    }

    private static string Png(string dir, string name, SkiaSharp.SKColor color)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var bmp = new SkiaSharp.SKBitmap(40, 40);
        bmp.Erase(color);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact]
    public void Mouth_object_draws_the_image_for_the_current_vowel()
    {
        var dir = TempDir();
        try
        {
            var p = new Project();
            p.Settings.Width = 160; p.Settings.Height = 120;
            double fps = p.Settings.Fps.Value;
            var owner = ObjectFactory.Create(MouthSourceDefinition().Id, 3, 0, (int)(fps * 5));
            owner.Main.Values["character"] = "ずんだもん";
            owner.Main.Values["closed"] = Png(dir, "closed.png", SkiaSharp.SKColors.Red);
            owner.Main.Values["a"] = Png(dir, "a.png", SkiaSharp.SKColors.Lime);
            p.Root.Objects.Add(owner);
            p.Root.Objects.Add(Voice(1, 0, (int)(fps * 2), "ずんだもん", "0:-;0.5:a;1.5:-"));

            using var engine = new VideoBuilder.Rendering.RenderEngine();
            SkiaSharp.SKColor At(double sec)
            {
                using var image = engine.RenderImage(p, p.Root, (int)Math.Round(sec * fps));
                using var bmp = SkiaSharp.SKBitmap.FromImage(image);
                return bmp.GetPixel(80, 60);
            }
            Assert.Equal(SkiaSharp.SKColors.Red, At(0.2));    // 閉じた口
            Assert.Equal(SkiaSharp.SKColors.Lime, At(1.0));   // 「あ」(「い」などの絵がなくても「あ」で開く)
            Assert.Equal(SkiaSharp.SKColors.Red, At(3.0));    // 声が終わったら閉じる
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
