using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Media;
using VideoBuilder.Plugins;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>プラグイン API v2 (速く動かすための形) が、エンジンの中で正しく扱われることを確かめる。</summary>
public class PluginApiTests
{
    private static Project Small()
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 240;
        return p;
    }

    private static TimelineObject Square(Project p, string color)
    {
        var o = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        o.Main.Values["color"] = color;
        o.Main.Values["type"] = "0";
        p.Root.Objects.Add(o);
        return o;
    }

    private static SKColor Pixel(RenderEngine engine, Project p, int frame, int x, int y)
    {
        using var img = engine.RenderImage(p, p.Root, frame);
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    private static SKColor Pixel(Project p, int frame, int x, int y)
    {
        using var engine = new RenderEngine();
        return Pixel(engine, p, frame, x, y);
    }

    private static void Define(string id, EffectCategory category = EffectCategory.Filter)
    {
        if (!EffectRegistry.Exists(id))
            EffectRegistry.Register(new EffectDefinition { Id = id, Name = id, Category = category, Icon = "Icon.Tune", Params = Array.Empty<ParamDefinition>() });
    }

    /// <summary>RGB を半分にする色の変換。呼ばれた回数を数える。</summary>
    private sealed class Half(string id) : IColorMatrixEffect
    {
        public int Calls;
        public string Id => id;
        public float[]? GetMatrix(EffectArgs args)
        {
            Calls++;
            return new float[] { 0.5f, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0, 1, 0 };
        }
    }

    /// <summary>RGB を反転する (乗算済みなので a - c)。呼ばれた回数を数える。</summary>
    private sealed class Invert(string id, EffectFlags flags) : IPixelEffect
    {
        public int Calls;
        public string Id => id;
        public EffectFlags Flags => flags;
        public void Process(PixelBuffer pixels, EffectArgs args)
        {
            Interlocked.Increment(ref Calls);
            pixels.ParallelRows((y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    var row = pixels.Row(y);
                    for (int x = 0; x < row.Length; x += 4)
                        for (int c = 0; c < 3; c++) row[x + c] = (byte)(row[x + 3] - row[x + c]);
                }
            });
        }
    }

    [Fact]
    public void Consecutive_color_matrices_are_merged_into_one_pass()
    {
        var a = new Half("test.half.a");
        var b = new Half("test.half.b");
        Define(a.Id); Define(b.Id);
        Effects.RegisterMatrix(a); Effects.RegisterMatrix(b);

        // オブジェクトにかける場合: 2 つ続けて 1/4 になる
        var p = Small();
        var sq = Square(p, "#FFFFFF");
        ObjectFactory.AddFilter(sq, a.Id);
        ObjectFactory.AddFilter(sq, b.Id);
        var px = Pixel(p, 0, 160, 120);
        Assert.InRange(px.Red, 62, 66);
        Assert.Equal(px.Red, px.Blue);

        // 画面全体にかける (調整レイヤー): 同じ結果
        var q = Small();
        Square(q, "#FFFFFF");
        var adj = ObjectFactory.Create(a.Id, 1, 0, 60);
        ObjectFactory.AddFilter(adj, b.Id);
        q.Root.Objects.Add(adj);
        Assert.Equal(px, Pixel(q, 0, 160, 120));

        // 間に別の効果がはさまっても、順番どおりに処理される (反転 → 半分: (255-255)*... ではなく、半分 → 反転 → 半分)
        var inv = new Invert("test.invert.mid", EffectFlags.Deterministic);
        Define(inv.Id);
        Effects.RegisterPixel(inv);
        var r = Small();
        var sq2 = Square(r, "#FFFFFF");
        ObjectFactory.AddFilter(sq2, a.Id);
        ObjectFactory.AddFilter(sq2, inv.Id);
        ObjectFactory.AddFilter(sq2, b.Id);
        Assert.InRange(Pixel(r, 0, 160, 120).Red, 62, 66);   // 255 → 128 → 127 → 64
    }

    [Fact]
    public void Pixel_effects_work_on_objects_and_adjustment_layers()
    {
        var inv = new Invert("test.invert", EffectFlags.Deterministic);
        Define(inv.Id);
        Effects.RegisterPixel(inv);

        var p = Small();
        var sq = Square(p, "#FF0000");
        ObjectFactory.AddFilter(sq, inv.Id);
        Assert.Equal(new SKColor(0, 255, 255), Pixel(p, 0, 160, 120));

        var q = Small();
        Square(q, "#FF0000");
        q.Root.Objects.Add(ObjectFactory.Create(inv.Id, 1, 0, 60));
        Assert.Equal(new SKColor(0, 255, 255), Pixel(q, 0, 160, 120));
    }

    [Fact]
    public void Deterministic_flag_lets_the_engine_reuse_filter_results()
    {
        var cached = new Invert("test.invert.cached", EffectFlags.Deterministic);
        var live = new Invert("test.invert.live", EffectFlags.None);
        Define(cached.Id); Define(live.Id);
        Effects.RegisterPixel(cached); Effects.RegisterPixel(live);

        using var engine = new RenderEngine();
        var p = Small();
        ObjectFactory.AddFilter(Square(p, "#FF0000"), cached.Id);
        for (int f = 0; f < 3; f++) Pixel(engine, p, f, 160, 120);
        Assert.Equal(1, cached.Calls);   // 同じ入力・同じパラメータなので、1 回だけ計算する

        var q = Small();
        ObjectFactory.AddFilter(Square(q, "#FF0000"), live.Id);
        for (int f = 0; f < 3; f++) Pixel(engine, q, f, 160, 120);
        Assert.Equal(3, live.Calls);     // Deterministic でなければ毎フレーム計算する
    }

    private sealed class CountingSource(Func<EffectArgs, string?> key) : IVideoSource
    {
        public int Renders;
        public SKImage? Render(EffectArgs args)
        {
            Renders++;
            var info = new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(SKColors.Lime);
            return args.Context.Own(surface.Snapshot());
        }
        public string? CacheKey(EffectArgs args) => key(args);
    }

    [Fact]
    public void Source_cache_key_skips_render_while_it_is_unchanged()
    {
        string tag = "a";
        var source = new CountingSource(_ => tag);
        Define("test.counting", EffectCategory.VideoSource);
        Effects.RegisterSource("test.counting", source);

        using var engine = new RenderEngine();
        var p = Small();
        p.Root.Objects.Add(ObjectFactory.Create("test.counting", 0, 0, 60));
        for (int f = 0; f < 4; f++) Assert.Equal(new SKColor(0, 255, 0), Pixel(engine, p, f, 160, 120));
        Assert.Equal(1, source.Renders);
        tag = "b";
        Pixel(engine, p, 4, 160, 120);
        Pixel(engine, p, 5, 160, 120);
        Assert.Equal(2, source.Renders);

        // 鍵がなければ毎フレーム描く (今までどおり)
        var plain = new CountingSource(_ => null);
        Define("test.counting.plain", EffectCategory.VideoSource);
        Effects.RegisterSource("test.counting.plain", plain);
        var q = Small();
        q.Root.Objects.Add(ObjectFactory.Create("test.counting.plain", 0, 0, 60));
        for (int f = 0; f < 3; f++) Pixel(engine, q, f, 160, 120);
        Assert.Equal(3, plain.Renders);
    }

    // ---------------- 音声 ----------------

    /// <summary>一定の値 (直流) の WAV を書く。</summary>
    private static string ConstantWav(string dir, float value, double seconds, int rate = 48000)
    {
        var path = Path.Combine(dir, "dc.wav");
        int frames = (int)(seconds * rate);
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = frames * 4;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        short s = (short)(value * 32767);
        for (int i = 0; i < frames * 2; i++) w.Write(s);
        return path;
    }

    private sealed class Recorder : IAudioEffect
    {
        public readonly List<(bool Continuous, long ObjectSample, int StateSerial)> Blocks = new();
        private int _serial;
        public string Id => "test.audio.recorder";
        public void Process(AudioBlock block, ref object? state)
        {
            if (state is not int || !block.Continuous) state = ++_serial;
            Blocks.Add((block.Continuous, block.ObjectSample, (int)state));
            foreach (ref var v in block.Samples) v *= 0.5f;
        }
    }

    [Fact]
    public void Audio_effects_process_blocks_with_state_and_continuity()
    {
        if (!FFmpegLoader.EnsureLoaded())
        {
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG"));
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), $"vb-audiofx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var wav = ConstantWav(dir, 0.5f, 3);
            var p = Small();
            var a = ObjectFactory.Create(EffectRegistry.Audio, 0, 0, 60);   // 2 秒 (30fps)
            a.Main.Values["file"] = wav;
            p.Root.Objects.Add(a);
            var rec = new Recorder();
            Define(rec.Id, EffectCategory.AudioFilter);
            AudioEffects.Register(rec);
            ObjectFactory.AddFilter(a, rec.Id);
            Assert.Contains(EffectRegistry.FiltersFor(a), d => d.Id == rec.Id);
            Assert.DoesNotContain(EffectRegistry.FiltersFor(a), d => d.Id == "filter.blur");

            using var mixer = new AudioMixer();
            var buf = new float[1024 * 2];
            mixer.Mix(p, p.Root, 4800, buf);
            Assert.InRange(buf[100], 0.24f, 0.26f);    // 0.5 x 0.5
            mixer.Mix(p, p.Root, 4800 + 1024, buf);    // 続き
            mixer.Mix(p, p.Root, 30000, buf);          // シーク
            Assert.Equal(3, rec.Blocks.Count);
            Assert.False(rec.Blocks[0].Continuous);
            Assert.True(rec.Blocks[1].Continuous);
            Assert.Equal(rec.Blocks[0].StateSerial, rec.Blocks[1].StateSerial);   // 状態が引き継がれる
            Assert.False(rec.Blocks[2].Continuous);
            Assert.Equal(4800 + 1024, rec.Blocks[1].ObjectSample);

            // 組み込みのフェード: 先頭は無音、0.5 秒で半分、終わり際で小さくなる
            a.Effects.RemoveAll(e => e.DefinitionId == rec.Id);
            var fade = ObjectFactory.AddFilter(a, "audio.fade");
            fade.Tracks["in"] = new AnimatedValue(1);
            fade.Tracks["out"] = new AnimatedValue(1);
            mixer.Mix(p, p.Root, 0, buf);
            Assert.InRange(buf[0], -0.01f, 0.01f);
            mixer.Mix(p, p.Root, 24000, buf);
            Assert.InRange(buf[0], 0.24f, 0.26f);
            mixer.Mix(p, p.Root, 96000 - 2400, buf);   // 終わりの 0.05 秒前
            Assert.InRange(buf[0], 0.0f, 0.04f);

            // エコー: 遅延のあとに音が足される
            a.Effects.Remove(fade);
            var echo = ObjectFactory.AddFilter(a, "audio.echo");
            echo.Tracks["delay"] = new AnimatedValue(10);   // 480 サンプル
            echo.Tracks["mix"] = new AnimatedValue(100);
            echo.Tracks["feedback"] = new AnimatedValue(0);
            mixer.Mix(p, p.Root, 10000, buf);
            Assert.InRange(buf[2 * 100], 0.49f, 0.51f);   // まだ響きは来ていない
            Assert.InRange(buf[2 * 600], 0.99f, 1.0f);    // 0.5 + 0.5
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Old_api_versions_in_range_are_accepted_and_others_rejected()
    {
        Assert.True(PluginApi.MinimumVersion <= 1 && PluginApi.Version >= 2);
        var root = Path.Combine(Path.GetTempPath(), $"vb-plugins-{Guid.NewGuid():N}");
        try
        {
            foreach (var (name, ver) in new[] { ("v0", 0), ("v1", 1), ("v2", 2), ("v3", PluginApi.Version + 1) })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                File.WriteAllText(Path.Combine(root, name, "plugin.json"), $"{{\"id\":\"x.{name}\",\"apiVersion\":{ver},\"assembly\":\"missing.dll\"}}");
            }
            var result = PluginLoader.LoadAll(root);
            Assert.Contains("API", result[0].Error);
            Assert.Contains("見つかりません", result[1].Error);   // バージョンは通り、アセンブリがないだけ
            Assert.Contains("見つかりません", result[2].Error);
            Assert.Contains("API", result[3].Error);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---------------- API バージョン 3 ----------------

    [Fact]
    public void Plugin_data_is_saved_copied_and_undoable()
    {
        var p = Small();
        var o = Square(p, "#FFFFFF");
        o.PluginData = new Dictionary<string, string> { ["my.plugin.text"] = "こんにちは" };

        // 保存して読み直しても残る。使っていないオブジェクトには書かれない
        var q = ProjectSerializer.FromJson(ProjectSerializer.ToJson(p));
        Assert.Equal("こんにちは", q.Root.Objects[0].PluginData!["my.plugin.text"]);
        var plain = Square(p, "#000000");
        Assert.DoesNotContain("pluginData", ProjectSerializer.ObjectsToJson(new[] { plain }));

        // 複製は別の入れ物を持ち、内容の比較にも使われる
        var copy = o.Clone();
        Assert.True(TimelineObject.SameContent(o, copy));
        copy.PluginData!["my.plugin.text"] = "さようなら";
        Assert.Equal("こんにちは", o.PluginData["my.plugin.text"]);
        Assert.False(TimelineObject.SameContent(o, copy));

        // PluginData だけを書き換えても、取り消せる
        var session = new EditorSession();
        session.Edit("追加", () => session.Scene.Objects.Add(o.Clone(newId: true)));
        var target = session.Scene.Objects[0];
        session.Edit("書き換え", () => target.PluginData!["my.plugin.text"] = "変更");
        Assert.Equal("変更", session.Scene.Objects[0].PluginData!["my.plugin.text"]);
        session.Undo();
        Assert.Equal("こんにちは", session.Scene.Objects[0].PluginData!["my.plugin.text"]);
    }

    [Fact]
    public void Placing_objects_for_plugins_keeps_the_length_and_finds_a_free_layer()
    {
        var session = new EditorSession();
        // レイヤー 0 の 30 フレーム目から先はふさがっている
        session.Edit("準備", () => session.Scene.Objects.Add(ObjectFactory.Create(EffectRegistry.Shape, 0, 30, 30)));
        TimelineObject? placed = null;
        session.Edit("置く", () => placed = session.PlaceObject(EffectRegistry.Shape, 0, 10, 40));
        // 削らずに、空いている下のレイヤーへ
        Assert.NotNull(placed);
        Assert.Equal(1, placed!.Layer);
        Assert.Equal(40, placed.Length);
        Assert.Equal(0, session.FindFreeLayer(0, 0, 29));
        Assert.Equal(2, session.FindFreeLayer(1, 10, 49));

        var text = session.CreateSubtitleText("字幕", 3, 5, 14);
        Assert.Equal(EffectRegistry.Text, text.Main.DefinitionId);
        Assert.Equal("字幕", text.Main.Values["text"]);
        Assert.Equal((3, 5, 14), (text.Layer, text.Start, text.End));
        Assert.DoesNotContain(text, session.Scene.Objects);   // シーンにはまだ入れない
    }

    private sealed class NamedCommand(string id, string name) : IPluginCommand
    {
        public string Id => id;
        public string Name => name;
        public Task RunAsync(IPluginCommandContext context) => Task.CompletedTask;
    }

    [Fact]
    public void Commands_need_an_id_and_a_name_and_ids_must_be_unique()
    {
        try
        {
            PluginCommands.Register("test.plugin", new NamedCommand("test.cmd.unique", "コマンド"));
            Assert.Contains(PluginCommands.All, e => e.Command.Id == "test.cmd.unique" && e.PluginId == "test.plugin");
            Assert.Throws<ArgumentException>(() => PluginCommands.Register("other.plugin", new NamedCommand("test.cmd.unique", "別")));
            Assert.Throws<ArgumentException>(() => PluginCommands.Register("test.plugin", new NamedCommand("", "名前")));
            Assert.Throws<ArgumentException>(() => PluginCommands.Register("test.plugin", new NamedCommand("test.cmd.noname", " ")));
        }
        finally { PluginCommands.Unregister("test.cmd.unique"); }
        Assert.DoesNotContain(PluginCommands.All, e => e.Command.Id == "test.cmd.unique");
    }
}
