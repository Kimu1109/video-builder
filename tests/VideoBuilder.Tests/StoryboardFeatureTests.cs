using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.Tests;

/// <summary>シンプル編集の追加機能: 終わりのフェード・音楽の繰り返し / ダッキング・キャプションの設定・余白の色・複数選択・端のドラッグなど。</summary>
public class StoryboardFeatureTests
{
    private static Project Small() => new() { Settings = new ProjectSettings { Width = 320, Height = 240, FrameRate = "30" } };

    private static StoryClip Photo(double seconds = 2) =>
        new() { Kind = StoryClipKind.Photo, File = "/p.png", SourceWidth = 640, SourceHeight = 240, Duration = seconds };

    private static StoryClip Video(double seconds = 10, bool audio = true) =>
        new() { Kind = StoryClipKind.Video, File = "/v.mp4", SourceSeconds = seconds, HasAudio = audio };

    private static (EditorSession, StoryboardEditor) NewEditor()
    {
        var s = new EditorSession();
        s.NewProject(Small().Settings);
        return (s, new StoryboardEditor(s));
    }

    // ---------------- 変換 ----------------

    [Fact]
    public void Default_settings_add_nothing_new()
    {
        var p = Small();
        var board = new Storyboard { Clips = { Photo(), Video() } };
        board.Clips[0].Caption = "hi";
        board.Music.Add(new MusicClip { File = "/m.wav", SourceSeconds = 3 });
        var objects = StoryboardCompiler.Compile(board, p);
        Assert.DoesNotContain(objects, o => o.Id == StoryboardCompiler.IdPrefix + "bg");
        Assert.All(objects, o => Assert.Null(o.FindEffect("audio.duck")));
        Assert.All(objects, o => Assert.Null(o.FindEffect("audio.pitch")));
        Assert.All(objects, o => Assert.Null(o.FindEffect("filter.backplate")));
        Assert.Equal(1, objects.Count(o => o.Main.DefinitionId == EffectRegistry.Audio && o.Id.EndsWith("m")));
        Assert.Equal(StoryboardCompiler.VisualLayer, objects[0].Layer);
    }

    [Fact]
    public void End_fade_fades_the_last_clip_to_black()
    {
        var p = Small();
        var board = new Storyboard { Clips = { Photo(), Video() }, EndFadeSeconds = 1.5 };
        var objects = StoryboardCompiler.Compile(board, p);
        var last = objects.Single(o => o.Main.DefinitionId == EffectRegistry.Video);
        var fade = last.FindEffect("filter.fade");
        Assert.NotNull(fade);
        Assert.Equal(1.5, fade!.Tracks["out"].Values[0]);
        Assert.Equal(1.5, objects.Single(o => o.Main.DefinitionId == EffectRegistry.Audio).FindEffect("audio.fade")!.Tracks["out"].Values[0]);
        Assert.Null(objects.Single(o => o.Main.DefinitionId == EffectRegistry.Image).FindEffect("filter.fade"));
    }

    [Fact]
    public void Looping_music_repeats_its_range_until_the_video_ends()
    {
        var p = Small();
        var board = new Storyboard { Clips = { Photo(10) } };
        board.Music.Add(new MusicClip { File = "/m.wav", SourceSeconds = 5, TrimStart = 1, TrimEnd = 4, Loop = true });   // 3 秒の繰り返し
        var layout = StoryboardCompiler.Layout(board, p);
        Assert.Equal((0, 299), (layout.Music[0].Start, layout.Music[0].End));

        var music = StoryboardCompiler.Compile(board, p, layout).Where(o => o.Main.DefinitionId == EffectRegistry.Audio).OrderBy(o => o.Start).ToList();
        Assert.Equal(4, music.Count);   // 3 + 3 + 3 + 1 秒
        Assert.Equal(new[] { 0, 90, 180, 270 }, music.Select(o => o.Start));
        Assert.Equal(30, music[^1].Length);
        Assert.All(music, o => Assert.Equal(1, o.Main.Tracks["position"].Values[0]));
        Assert.Equal(0, music[1].FindEffect("audio.fade")?.Tracks["out"].Values[0] ?? 0);
        Assert.True(music[0].FindEffect("audio.fade")!.Tracks["in"].Values[0] > 0);
        Assert.True(music[^1].FindEffect("audio.fade")!.Tracks["out"].Values[0] > 0);
        Assert.Equal(music.Count, music.Select(o => o.Id).Distinct().Count());
    }

    [Fact]
    public void Ducked_music_gets_the_duck_effect()
    {
        var p = Small();
        var board = new Storyboard { Clips = { Video(4) } };
        board.Music.Add(new MusicClip { File = "/m.wav", SourceSeconds = 3, Duck = true });
        var objects = StoryboardCompiler.Compile(board, p);
        Assert.NotNull(objects.Single(o => o.Id.EndsWith("m")).FindEffect("audio.duck"));
        Assert.Null(objects.Single(o => o.Id.EndsWith("a")).FindEffect("audio.duck"));   // 動画の音声はキーのまま
    }

    [Fact]
    public void Keep_pitch_compensates_the_speed()
    {
        var p = Small();
        var clip = Video();
        clip.Speed = 2;
        clip.KeepPitch = true;
        var a = StoryboardCompiler.Compile(new Storyboard { Clips = { clip } }, p).Single(o => o.Main.DefinitionId == EffectRegistry.Audio);
        Assert.Equal(-12, a.FindEffect("audio.pitch")!.Tracks["semitones"].Values[0]);
        Assert.Equal(24, StoryboardCompiler.PitchCompensation(0.1));   // 範囲に収める
    }

    [Fact]
    public void Caption_can_be_shown_for_part_of_the_clip_with_a_band_and_size()
    {
        var p = Small();
        var clip = Photo(4);
        clip.Caption = "hello";
        clip.CaptionStart = 1;
        clip.CaptionSeconds = 2;
        clip.CaptionBackground = true;
        clip.TextScale = 200;
        clip.Font = "Some Font";
        var t = StoryboardCompiler.Compile(new Storyboard { Clips = { clip } }, p).Single(o => o.Main.DefinitionId == EffectRegistry.Text);
        Assert.Equal((30, 60), (t.Start, t.Length));
        Assert.NotNull(t.FindEffect("filter.backplate"));
        Assert.Null(t.FindEffect("filter.border"));
        Assert.Equal(Math.Round(240 * 0.055 * 2), t.Main.Tracks["size"].Values[0]);
        Assert.Equal("Some Font", t.Main.Values["font"]);
        var fade = t.FindEffect("filter.fade")!;
        Assert.True(fade.Tracks["in"].Values[0] > 0 && fade.Tracks["out"].Values[0] > 0);

        // 終わりがクリップより後ろなら、クリップの終わりまで
        clip.CaptionSeconds = 100;
        t = StoryboardCompiler.Compile(new Storyboard { Clips = { clip } }, p).Single(o => o.Main.DefinitionId == EffectRegistry.Text);
        Assert.Equal(119, t.End);
    }

    [Fact]
    public void End_credits_scroll_the_title_up()
    {
        var p = Small();
        var title = StoryClip.Title("A\nB");
        title.Motion = ClipMotion.ScrollUp;
        var t = StoryboardCompiler.Compile(new Storyboard { Clips = { title } }, p).Single(o => o.Main.DefinitionId == EffectRegistry.Text);
        var y = t.Draw!.Tracks["y"];
        Assert.True(y.Values[0] > 120 && y.Values[1] < -120);
    }

    [Fact]
    public void Fill_covers_the_screen()
    {
        var p = Small();
        var clip = Photo();   // 640x240 → 収めると 50%、いっぱいなら 100%
        clip.Fill = true;
        var v = StoryboardCompiler.Compile(new Storyboard { Clips = { clip } }, p)[0];
        Assert.Equal(100, v.Draw!.Tracks["scale"].Values[0]);
    }

    [Fact]
    public void Background_color_adds_a_layer_below_everything()
    {
        var p = Small();
        p.Storyboard = new Storyboard { Clips = { Photo() }, Background = "#FFFFFF" };
        p.Storyboard.Music.Add(new MusicClip { File = "/m.wav", SourceSeconds = 1 });
        StoryboardCompiler.Apply(p);
        var bg = p.Root.Objects.Single(o => o.Id == StoryboardCompiler.IdPrefix + "bg");
        Assert.Equal((0, 0, 60), (bg.Layer, bg.Start, bg.Length));
        Assert.Equal(1, p.Root.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Image).Layer);
        Assert.Equal(StoryboardCompiler.MusicLayer + 1, p.Root.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Audio).Layer);
        Assert.True(StoryboardCompiler.IsInSync(p));
    }

    [Fact]
    public void New_settings_survive_saving()
    {
        var p = Small();
        p.Storyboard = new Storyboard { Clips = { Photo() }, EndFadeSeconds = 2, Background = "#112233" };
        p.Storyboard.Clips[0].Fill = true;
        p.Storyboard.Music.Add(new MusicClip { File = "/m.wav", SourceSeconds = 1, Loop = true, Duck = true });
        StoryboardCompiler.Apply(p);
        var loaded = ProjectSerializer.FromJson(ProjectSerializer.ToJson(p));
        Assert.Equal((2.0, "#112233"), (loaded.Storyboard!.EndFadeSeconds, loaded.Storyboard.Background));
        Assert.True(loaded.Storyboard.Clips[0].Fill);
        Assert.True(loaded.Storyboard.Music[0].Loop && loaded.Storyboard.Music[0].Duck);
        Assert.True(StoryboardCompiler.IsInSync(loaded));
    }

    // ---------------- 編集 ----------------

    [Fact]
    public void Multiple_clips_can_be_selected_deleted_and_moved_together()
    {
        var (s, ed) = NewEditor();
        var clips = Enumerable.Range(0, 5).Select(_ => Photo()).ToArray();
        ed.AddClips(clips);
        var id = clips.Select(c => c.Id).ToArray();

        ed.Select(id[1]);
        ed.ToggleSelect(id[3]);
        Assert.Equal(new[] { id[1], id[3] }, ed.SelectedClipIds);
        Assert.True(ed.HasMultipleSelection);

        ed.MoveSelected(-1);
        Assert.Equal(new[] { id[1], id[0], id[3], id[2], id[4] }, ed.Board!.Clips.Select(c => c.Id));
        s.Undo();

        ed.SelectRange(id[2]);   // 主に選んでいる 3 から 2 まで
        Assert.Equal(new[] { id[2], id[3] }, ed.SelectedClipIds);

        ed.UpdateSelectedClips("見た目の変更", c => c.Look = ClipLook.Mono);
        Assert.Equal(new[] { ClipLook.None, ClipLook.None, ClipLook.Mono, ClipLook.Mono, ClipLook.None }, ed.Board!.Clips.Select(c => c.Look));

        ed.CopySelected();
        ed.RemoveSelected();
        Assert.Equal(new[] { id[0], id[1], id[4] }, ed.Board!.Clips.Select(c => c.Id));
        Assert.False(ed.HasMultipleSelection);
        ed.Paste();
        Assert.Equal(5, ed.Board!.Clips.Count);

        ed.SelectAllClips();
        Assert.Equal(5, ed.SelectedClipIds.Count);
        ed.ToggleSelect(ed.SelectedId!);   // 主を外すと、残りの 1 つが主になる
        Assert.Equal(4, ed.SelectedClipIds.Count);
        Assert.NotNull(ed.SelectedId);
        ed.Select(id[0]);
        Assert.False(ed.HasMultipleSelection);
    }

    [Fact]
    public void Music_can_be_split_copied_and_pasted()
    {
        var (s, ed) = NewEditor();
        ed.AddClips(new[] { Photo(20) });
        var music = new MusicClip { File = "/m.wav", SourceSeconds = 10, StartSeconds = 1, Loop = true };
        ed.AddMusic(music);
        Assert.True(ed.SplitAt(90));   // 鳴らし始めから 2 秒
        var (a, b) = (ed.Board!.Music[0], ed.Board.Music[1]);
        Assert.Equal((0.0, 2.0, false), (a.TrimStart, a.TrimEnd, a.Loop));
        Assert.Equal((2.0, 3.0, true), (b.TrimStart, b.StartSeconds, b.Loop));
        Assert.Equal((0.0, 0.0), (a.FadeOut, b.FadeIn));
        Assert.Equal(b.Id, ed.SelectedId);

        s.CurrentFrame = 300;
        ed.CopySelected();
        ed.Paste();
        Assert.Equal(3, ed.Board!.Music.Count);
        Assert.Equal(10, ed.SelectedMusic!.StartSeconds);
        Assert.Equal(2, ed.SelectedMusic.TrimStart);
    }

    [Fact]
    public void Dragging_clip_edges_changes_the_length_from_the_original()
    {
        var (s, ed) = NewEditor();
        var video = Video(10);
        video.Speed = 2;
        var photo = Photo(4);
        ed.AddClips(new[] { video, photo });
        var original = ed.Board!.FindClip(video.Id)!.Clone();

        // 何度呼んでも、いつも元の設定から計算する (1 回のドラッグで何度も呼ばれる)
        ed.ResizeClip(video.Id, original, 0, -1, "drag1");
        ed.ResizeClip(video.Id, original, 0, -2, "drag1");
        Assert.Equal((0.0, 6.0), (ed.Board!.FindClip(video.Id)!.TrimStart, ed.Board.FindClip(video.Id)!.TrimEnd));   // 画面の 2 秒 = 素材の 4 秒
        ed.ResizeClip(video.Id, original, 1, 0, "drag2");
        Assert.Equal((2.0, 10.0), (ed.Board!.FindClip(video.Id)!.TrimStart, ed.Board.FindClip(video.Id)!.TrimEnd));
        ed.ResizeClip(video.Id, original, 0, 100, "drag3");   // 素材の長さより伸ばせない
        Assert.Equal(10, ed.Board!.FindClip(video.Id)!.TrimEnd);

        var p0 = ed.Board.FindClip(photo.Id)!.Clone();
        ed.ResizeClip(photo.Id, p0, 1, 0.5);
        Assert.Equal(3.5, ed.Board!.FindClip(photo.Id)!.Duration);
        ed.ResizeClip(photo.Id, p0, 10, 0);
        Assert.Equal(StoryClip.MinSeconds, ed.Board!.FindClip(photo.Id)!.Duration);

        // 同じドラッグの変更は 1 回の取り消しにまとまる (写真 2 回・drag3・drag2・drag1 の 5 回で元どおり)
        for (int i = 0; i < 5; i++) s.Undo();
        Assert.Equal((0.0, 0.0), (ed.Board!.FindClip(video.Id)!.TrimStart, ed.Board.FindClip(video.Id)!.TrimEnd));
    }

    [Fact]
    public void Dragging_music_start_keeps_the_rest_in_place()
    {
        var (_, ed) = NewEditor();
        ed.AddClips(new[] { Photo(20) });
        var music = new MusicClip { File = "/m.wav", SourceSeconds = 10, StartSeconds = 2, TrimStart = 1 };
        ed.AddMusic(music);
        var original = music.Clone();
        ed.ResizeMusic(music.Id, original, 1.5, 0);
        var m = ed.Board!.FindMusic(music.Id)!;
        Assert.Equal((2.5, 3.5, 10.0), (m.TrimStart, m.StartSeconds, m.TrimEnd));
        ed.ResizeMusic(music.Id, original, -5, 0);   // 素材の先頭より前には出せない
        m = ed.Board!.FindMusic(music.Id)!;
        Assert.Equal((0.0, 1.0), (m.TrimStart, m.StartSeconds));
        ed.ResizeMusic(music.Id, original, 0, -4);
        Assert.Equal(6, ed.Board!.FindMusic(music.Id)!.TrimEnd);
    }

    [Fact]
    public void End_title_goes_last()
    {
        var (_, ed) = NewEditor();
        ed.AddClips(new[] { Photo(), Photo() });
        ed.Select(ed.Board!.Clips[0].Id);
        var title = ed.AddTitle("end", atEnd: true);
        Assert.Equal(title.Id, ed.Board!.Clips[^1].Id);
    }
}
