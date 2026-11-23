using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>シンプル編集 (ストーリーボード) のモデル・シーンへの変換・編集操作。</summary>
public class StoryboardTests
{
    private static Project Small() => new() { Settings = new ProjectSettings { Width = 320, Height = 240, FrameRate = "30" } };

    private static StoryClip Title(string background, double seconds = 2, int transition = 0, double transitionSeconds = 1) =>
        new() { Kind = StoryClipKind.Title, Background = background, Duration = seconds, Transition = transition, TransitionSeconds = transitionSeconds };

    private static SKColor Pixel(Project p, int frame, int x, int y)
    {
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, frame);
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void Clips_are_laid_out_in_order_and_transitions_overlap_neighbours()
    {
        var p = Small();
        var board = new Storyboard();
        board.Clips.Add(Title("#FF0000"));                                    // 0..59
        board.Clips.Add(Title("#00FF00", transition: 1, transitionSeconds: 0.5));   // 15 フレーム重なる
        board.Clips.Add(Title("#0000FF", seconds: 1));                         // 切り替えなし
        var layout = StoryboardCompiler.Layout(board, p);

        Assert.Equal((0, 59, 0), (layout.Clips[0].Start, layout.Clips[0].End, layout.Clips[0].Lane));
        Assert.Equal((45, 104, 1, 15), (layout.Clips[1].Start, layout.Clips[1].End, layout.Clips[1].Lane, layout.Clips[1].FadeIn));
        Assert.Equal(15, layout.Clips[0].FadeOut);
        Assert.Equal((105, 134, 0), (layout.Clips[2].Start, layout.Clips[2].End, layout.Clips[2].Lane));
        Assert.Equal(135, layout.VideoLength);
        Assert.Equal(board.Clips[1].Id, layout.ClipAt(50)!.Clip.Id);    // 重なっている所は、入ってくる方
    }

    [Fact]
    public void Transition_never_spans_three_clips()
    {
        var p = Small();
        var board = new Storyboard();
        board.Clips.Add(Title("#FF0000", seconds: 1));
        board.Clips.Add(Title("#00FF00", seconds: 1, transition: 1, transitionSeconds: 10));
        board.Clips.Add(Title("#0000FF", seconds: 1, transition: 1, transitionSeconds: 10));
        var layout = StoryboardCompiler.Layout(board, p);
        Assert.True(layout.Clips[1].FadeIn + layout.Clips[1].FadeOut < layout.Clips[1].Length);
        Assert.True(layout.Clips[2].Start > layout.Clips[0].End);
    }

    [Fact]
    public void Compiled_scene_is_deterministic_and_detects_advanced_edits()
    {
        var p = Small();
        p.Storyboard = new Storyboard();
        p.Storyboard.Clips.Add(Title("#FF0000"));
        p.Storyboard.Clips[0].Text = "Hello";
        StoryboardCompiler.Apply(p);
        Assert.Equal(2, p.Root.Objects.Count);   // 背景 + 文字
        Assert.True(StoryboardCompiler.IsInSync(p));

        // 保存して読み直しても同じ
        var loaded = ProjectSerializer.FromJson(ProjectSerializer.ToJson(p));
        Assert.NotNull(loaded.Storyboard);
        Assert.True(StoryboardCompiler.IsInSync(loaded));

        // 詳細編集でオブジェクトを動かすと、ずれたと分かる
        p.Root.Objects[0].Layer = 10;
        Assert.False(StoryboardCompiler.IsInSync(p));
        StoryboardCompiler.Apply(p);
        Assert.True(StoryboardCompiler.IsInSync(p));
    }

    [Fact]
    public void Photo_is_fitted_to_the_screen_and_rotation_swaps_the_fit()
    {
        var p = Small();
        var clip = new StoryClip { Kind = StoryClipKind.Photo, File = "/x.png", SourceWidth = 640, SourceHeight = 240 };
        var board = new Storyboard { Clips = { clip } };
        var o = StoryboardCompiler.Compile(board, p)[0];
        Assert.Equal(50, o.Draw!.Tracks["scale"].Values[0]);     // 横 640 → 320

        clip.Rotation = 90;
        o = StoryboardCompiler.Compile(board, p)[0];
        Assert.Equal(37.5, o.Draw!.Tracks["scale"].Values[0]);   // 縦になった 640 → 240
        Assert.Equal(90, o.Draw!.Tracks["rotation"].Values[0]);

        clip.Rotation = 0; clip.Motion = ClipMotion.ZoomIn;
        var scale = StoryboardCompiler.Compile(board, p)[0].Draw!.Tracks["scale"];
        Assert.Equal(MovementMode.Linear, scale.Mode);
        Assert.True(scale.Values[1] > scale.Values[0]);
    }

    [Fact]
    public void Video_clip_uses_its_trim_and_links_its_audio()
    {
        var p = Small();
        var clip = new StoryClip { Kind = StoryClipKind.Video, File = "/v.mp4", SourceSeconds = 10, TrimStart = 2, TrimEnd = 5, HasAudio = true, Volume = 80 };
        var objects = StoryboardCompiler.Compile(new Storyboard { Clips = { clip } }, p);
        var v = objects.Single(o => o.Main.DefinitionId == EffectRegistry.Video);
        var a = objects.Single(o => o.Main.DefinitionId == EffectRegistry.Audio);
        Assert.Equal(90, v.Length);
        Assert.Equal(2, v.Main.Tracks["position"].Values[0]);
        Assert.Equal((v.Start, v.End), (a.Start, a.End));
        Assert.Equal(v.GroupId, a.GroupId);
        Assert.Equal(80, a.Effects[^1].Tracks["volume"].Values[0]);

        clip.Muted = true;
        Assert.DoesNotContain(StoryboardCompiler.Compile(new Storyboard { Clips = { clip } }, p), o => o.Main.DefinitionId == EffectRegistry.Audio);
    }

    [Fact]
    public void Music_is_cut_at_the_end_of_the_video()
    {
        var p = Small();
        var board = new Storyboard { Clips = { Title("#FF0000", seconds: 2) } };
        board.Music.Add(new MusicClip { File = "/m.mp3", SourceSeconds = 30, StartSeconds = 1 });
        var music = StoryboardCompiler.Layout(board, p).Music.Single();
        Assert.Equal((30, 59), (music.Start, music.End));
        board.FitMusicToVideo = false;
        Assert.Equal(929, StoryboardCompiler.Layout(board, p).Music.Single().End);
    }

    [Fact]
    public void Crossfade_renders_the_same_whichever_lane_the_incoming_clip_is_on()
    {
        var p = Small();
        p.Storyboard = new Storyboard();
        p.Storyboard.Clips.Add(Title("#FF0000"));                                   // レーン 0
        p.Storyboard.Clips.Add(Title("#0000FF", transition: 1, transitionSeconds: 1));   // レーン 1 (手前から入る)
        p.Storyboard.Clips.Add(Title("#FF0000", transition: 1, transitionSeconds: 1));   // レーン 0 (奥から入る)
        StoryboardCompiler.Apply(p);
        var layout = StoryboardCompiler.Layout(p.Storyboard, p);

        // 切り替えの真ん中は、2 つの色が混ざっている (どちらのレーンでも)
        foreach (var span in layout.Clips.Skip(1))
        {
            var mid = Pixel(p, span.Start + span.FadeIn / 2, 160, 120);
            Assert.InRange(mid.Red, 60, 200);
            Assert.InRange(mid.Blue, 60, 200);
            var after = Pixel(p, span.Start + span.FadeIn + 1, 160, 120);
            var expected = SKColor.Parse(span.Clip.Background);
            Assert.Equal((expected.Red, expected.Blue), (after.Red, after.Blue));
        }
    }

    [Fact]
    public void Wipe_comes_from_the_same_side_whichever_lane_the_incoming_clip_is_on()
    {
        var p = Small();
        p.Storyboard = new Storyboard();
        p.Storyboard.Clips.Add(Title("#FF0000"));
        p.Storyboard.Clips.Add(Title("#0000FF", transition: 2, transitionSeconds: 1));   // ワイプ (左から)
        p.Storyboard.Clips.Add(Title("#FF0000", transition: 2, transitionSeconds: 1));
        foreach (var c in p.Storyboard.Clips) c.Duration = 3;
        StoryboardCompiler.Apply(p);
        var layout = StoryboardCompiler.Layout(p.Storyboard, p);
        foreach (var span in layout.Clips.Skip(1))
        {
            int frame = span.Start + span.FadeIn / 2;
            var incoming = SKColor.Parse(span.Clip.Background);
            Assert.Equal(incoming.Blue, Pixel(p, frame, 10, 120).Blue);     // 左は入ってくる色
            Assert.NotEqual(incoming.Blue, Pixel(p, frame, 310, 120).Blue); // 右はまだ前の色
        }
    }

    // ---------------- 編集操作 ----------------

    private static (EditorSession, StoryboardEditor) NewEditor()
    {
        var s = new EditorSession();
        s.NewProject(Small().Settings);
        return (s, new StoryboardEditor(s));
    }

    [Fact]
    public void Adding_moving_and_removing_clips_are_undoable()
    {
        var (s, ed) = NewEditor();
        Assert.True(ed.CanEdit);
        ed.AddClips(new[] { Title("#FF0000"), Title("#00FF00") });
        var red = ed.Board!.Clips[0].Id;
        Assert.Equal(2, ed.Board.Clips.Count);
        Assert.True(ed.CanEdit);
        Assert.Equal(2, s.Project.Root.Objects.Count);

        ed.Move(red, 1);
        Assert.Equal(red, ed.Board.Clips[1].Id);
        ed.Remove(red);
        Assert.Single(ed.Board.Clips);
        s.Undo();
        Assert.Equal(red, ed.Board!.Clips[1].Id);
        s.Undo();
        Assert.Equal(red, ed.Board!.Clips[0].Id);
        Assert.True(ed.CanEdit);
    }

    [Fact]
    public void Splitting_a_video_divides_its_trim()
    {
        var (s, ed) = NewEditor();
        ed.AddClips(new[] { new StoryClip { Kind = StoryClipKind.Video, File = "/v.mp4", SourceSeconds = 10, TrimStart = 1 } });
        Assert.True(ed.SplitAt(60));   // 2 秒目
        var (a, b) = (ed.Board!.Clips[0], ed.Board.Clips[1]);
        Assert.Equal((1, 3), (a.TrimStart, a.TrimEnd));
        Assert.Equal((3, 0), (b.TrimStart, b.TrimEnd));
        Assert.Equal(b.Id, ed.SelectedId);
        Assert.Equal(270, ed.Layout.VideoLength);   // 合わせて元の長さ (9 秒)
    }

    [Fact]
    public void Trim_start_and_end_follow_the_playhead()
    {
        var (s, ed) = NewEditor();
        ed.AddClips(new[] { Title("#FF0000", seconds: 5) });
        Assert.True(ed.TrimSelectedAt(90, start: false));
        Assert.Equal(3, ed.SelectedClip!.Duration);
        Assert.True(ed.TrimSelectedAt(30, start: true));
        Assert.Equal(2, ed.SelectedClip!.Duration);
    }

    [Fact]
    public void Copy_paste_inserts_after_the_selection_with_a_new_id()
    {
        var (s, ed) = NewEditor();
        ed.AddClips(new[] { Title("#FF0000"), Title("#00FF00") });
        ed.Select(ed.Board!.Clips[0].Id);
        ed.CopySelected();
        ed.Paste();
        Assert.Equal(3, ed.Board.Clips.Count);
        Assert.Equal("#FF0000", ed.Board.Clips[1].Background);
        Assert.NotEqual(ed.Board.Clips[0].Id, ed.Board.Clips[1].Id);
    }

    [Fact]
    public void Advanced_project_can_be_imported_into_the_storyboard()
    {
        var (s, ed) = NewEditor();
        s.AddObject(EffectRegistry.Image, 0, 0, 60, o => o.Main.Values["file"] = "/a.png");
        s.AddObject(EffectRegistry.Image, 0, 60, 30, o => o.Main.Values["file"] = "/b.png");
        s.AddObject(EffectRegistry.Audio, 3, 15, 90, o => o.Main.Values["file"] = "/m.mp3");
        Assert.False(ed.CanEdit);
        ed.ImportFromScene();
        Assert.True(ed.CanEdit);
        Assert.Equal(new[] { "/a.png", "/b.png" }, ed.Board!.Clips.Select(c => c.File));
        Assert.Equal(1, ed.Board.Clips[1].Duration);
        Assert.Equal(0.5, ed.Board.Music.Single().StartSeconds);
    }

    [Fact]
    public void Relinking_media_keeps_the_scene_in_sync()
    {
        var (s, ed) = NewEditor();
        ed.AddClips(new[] { new StoryClip { Kind = StoryClipKind.Photo, File = "/old/a.png" } });
        Assert.Equal(2, ProjectMedia.Relink(s.Project, "/old/a.png", "/new/a.png"));
        Assert.Equal("/new/a.png", ed.Board!.Clips[0].File);
        Assert.True(ed.CanEdit);
    }

    // ---------------- 再生速度 ----------------

    [Fact]
    public void Speed_shortens_the_clip_and_is_applied_to_video_and_audio()
    {
        var p = Small();
        var clip = new StoryClip { Kind = StoryClipKind.Video, File = "/v.mp4", SourceSeconds = 10, TrimStart = 2, TrimEnd = 8, HasAudio = true, Speed = 2 };
        var board = new Storyboard { Clips = { clip } };
        Assert.Equal(3, clip.Seconds);                          // 6 秒ぶんを 2 倍速
        Assert.Equal(90, StoryboardCompiler.Layout(board, p).VideoLength);
        var objects = StoryboardCompiler.Compile(board, p);
        var v = objects.Single(o => o.Main.DefinitionId == EffectRegistry.Video);
        var a = objects.Single(o => o.Main.DefinitionId == EffectRegistry.Audio);
        Assert.Equal(200, v.Main.Tracks["speed"].Values[0]);
        Assert.Equal(200, a.Main.Tracks["speed"].Values[0]);
        // 最後のフレームは、素材の使う範囲の終わりの手前
        Assert.InRange(RenderEngine.SourceTime(p, v, v.Bounds, v.End), 7.9, 8);

        // 写真・タイトルには効かない、範囲の外は収める
        Assert.Equal(1, new StoryClip { Kind = StoryClipKind.Photo, Speed = 4 }.EffectiveSpeed);
        clip.Speed = 100;
        Assert.Equal(StoryClip.MaxSpeed, clip.EffectiveSpeed);
    }

    [Fact]
    public void Splitting_and_trimming_a_fast_clip_cut_the_source_at_the_same_picture()
    {
        var (s, ed) = NewEditor();
        ed.AddClips(new[] { new StoryClip { Kind = StoryClipKind.Video, File = "/v.mp4", SourceSeconds = 10, Speed = 2 } });
        Assert.True(ed.SplitAt(60));   // 画面で 2 秒 = 素材で 4 秒
        Assert.Equal(4, ed.Board!.Clips[0].TrimEnd);
        Assert.Equal(4, ed.Board.Clips[1].TrimStart);
        Assert.Equal(2, ed.Board.Clips[1].Speed);
        Assert.Equal(150, ed.Layout.VideoLength);   // 合わせて 5 秒 (10 秒の 2 倍速) のまま

        ed.Select(ed.Board.Clips[1].Id);
        Assert.True(ed.TrimSelectedAt(60 + 30, start: false));   // 後半の 1 秒目 = 素材で 2 秒進んだ所
        Assert.Equal(6, ed.Board!.Clips[1].TrimEnd);
    }

    [Fact]
    public void Imported_video_keeps_its_speed()
    {
        var (s, ed) = NewEditor();
        s.AddObject(EffectRegistry.Video, 0, 0, 60, o => { o.Main.Values["file"] = "/v.mp4"; o.Main.Tracks["speed"] = new AnimatedValue(150); });
        ed.ImportFromScene();
        var clip = ed.Board!.Clips.Single();
        Assert.Equal(1.5, clip.Speed);
        Assert.Equal(3, clip.TrimEnd - clip.TrimStart);
        Assert.Equal(60, ed.Layout.VideoLength);
    }

    /// <summary>赤から緑へ、type (TransitionTypes の番号) で切り替わる 2 つのクリップ。切り替えは 30〜59 フレーム。</summary>
    private static Project Paired(int type)
    {
        var p = Small();
        p.Storyboard = new Storyboard();
        p.Storyboard.Clips.Add(Title("#FF0000"));
        p.Storyboard.Clips.Add(Title("#00FF00", transition: type + 1, transitionSeconds: 1));
        StoryboardCompiler.Apply(p);
        return p;
    }

    [Fact]
    public void Paired_transitions_go_on_both_clips_and_survive_a_reload()
    {
        var p = Paired(28);   // プッシュ (左から)
        var filters = p.Root.Objects.Select(o => o.FindEffect("filter.transition")).Where(e => e != null).ToList();
        Assert.Equal(2, filters.Count);
        Assert.All(filters, e => Assert.Equal("1", e!.Values["screen"]));
        // 奥のクリップ (レーン 0 = 先のクリップ) にだけ印が付く
        Assert.Equal(1, filters.Count(e => e!.GetBool("under")));
        Assert.True(StoryboardCompiler.IsInSync(ProjectSerializer.FromJson(ProjectSerializer.ToJson(p))));

        // プッシュの途中: 左に入ってくる緑、右に押し出される赤
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, 45);
        using var bmp = SKBitmap.FromImage(img);
        Assert.True(bmp.GetPixel(20, 120).Green > 200 && bmp.GetPixel(20, 120).Red < 50, "左が緑でない: " + bmp.GetPixel(20, 120));
        Assert.True(bmp.GetPixel(300, 120).Red > 200 && bmp.GetPixel(300, 120).Green < 50, "右が赤でない: " + bmp.GetPixel(300, 120));
    }

    [Fact]
    public void Shape_wipes_split_the_screen_between_both_clips_without_gaps()
    {
        // 形で切り替えるもの: 途中は、どのピクセルも赤か緑 (か、その混ざった色) で、黒い隙間がない (境目の 1 ピクセルの少しの暗さは許す)。両方の色が見えている
        var types = Enumerable.Range(32, 8).Concat(Enumerable.Range(47, 20)).Append(86);
        using var engine = new RenderEngine();
        foreach (int type in types)
        {
            var p = Paired(type);
            foreach (int frame in new[] { 38, 45, 52 })
            {
                using var img = engine.RenderImage(p, p.Root, frame);
                using var bmp = SKBitmap.FromImage(img);
                int red = 0, green = 0;
                for (int y = 2; y < 240; y += 6)
                for (int x = 2; x < 320; x += 6)
                {
                    var c = bmp.GetPixel(x, y);
                    Assert.True(c.Red + c.Green >= 150, $"{EffectRegistry.TransitionTypes[type]} の {frame} フレーム目 ({x}, {y}) に隙間: {c}");
                    if (c.Red > 200) red++;
                    if (c.Green > 200) green++;
                }
                if (frame == 45) Assert.True(red > 0 && green > 0, $"{EffectRegistry.TransitionTypes[type]}: 途中で両方が見えていない ({red}, {green})");
            }
        }
    }

    [Fact]
    public void Half_and_half_transitions_show_the_old_clip_first_and_the_new_clip_after()
    {
        using var engine = new RenderEngine();
        foreach (int type in new[] { 44, 45, 46, 73, 74, 76, 77, 78, 79, 80, 81, 83 })
        {
            var p = Paired(type);
            SKColor At(int frame)
            {
                using var img = engine.RenderImage(p, p.Root, frame);
                using var bmp = SKBitmap.FromImage(img);
                return bmp.GetPixel(160, 120);
            }
            string name = EffectRegistry.TransitionTypes[type];
            var early = At(31);
            Assert.True(early.Red > early.Green, $"{name}: 始めに赤 (前のクリップ) が見えていない: {early}");
            var late = At(58);
            Assert.True(late.Green > late.Red, $"{name}: 終わりに緑 (次のクリップ) が見えていない: {late}");
            Assert.Equal(new SKColor(0, 255, 0), At(62));
        }
    }

    [Fact]
    public void Out_type_replaces_the_movement_when_leaving()
    {
        var p = Small();
        var o = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        o.Main.Values["type"] = "0";
        o.Main.Values["color"] = "#FFFFFF";
        p.Root.Objects.Add(o);
        var e = ObjectFactory.AddFilter(o, "filter.transition");
        e.Values["type"] = "0";                       // イン: フェード
        e.Values["outType"] = (6 + 1).ToString();     // アウト: スライド (左から) の逆 = 左へ出ていく
        e.Tracks["in"] = new AnimatedValue(0);
        e.Tracks["out"] = new AnimatedValue(1);
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, 45);
        using var bmp = SKBitmap.FromImage(img);
        // フェードなら全体が半透明になるが、スライドなので右側が空いて、左側は真っ白のまま
        Assert.Equal(SKColors.White, bmp.GetPixel(10, 120));
        Assert.Equal(SKColors.Black, bmp.GetPixel(310, 120));
    }
}
