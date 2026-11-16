using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.Tests;

public class AnimatedValueTests
{
    [Fact]
    public void Linear_interpolates_between_section_ends()
    {
        var v = new AnimatedValue { Values = { [0] = 0, [1] = 100 }, Mode = MovementMode.Linear };
        var bounds = new[] { 0, 100 };
        Assert.Equal(0, v.Evaluate(bounds, 0));
        Assert.Equal(50, v.Evaluate(bounds, 50), 6);
        Assert.Equal(100, v.Evaluate(bounds, 100), 6);
    }

    [Fact]
    public void None_keeps_the_left_value_of_each_section()
    {
        var v = new AnimatedValue { Values = new() { 10, 20, 30 } };
        var bounds = new[] { 0, 50, 100 };
        Assert.Equal(10, v.Evaluate(bounds, 49));
        Assert.Equal(20, v.Evaluate(bounds, 50));
        Assert.Equal(20, v.Evaluate(bounds, 100));
    }

    [Fact]
    public void IgnoreMid_uses_first_and_last_values()
    {
        var v = new AnimatedValue { Values = new() { 0, 999, 100 }, Mode = MovementMode.IgnoreMid };
        Assert.Equal(50, v.Evaluate(new[] { 0, 10, 100 }, 50), 6);
    }

    [Fact]
    public void Accel_starts_slow_and_decel_ends_slow()
    {
        var bounds = new[] { 0, 100 };
        var accel = new AnimatedValue { Values = new() { 0, 100 }, Mode = MovementMode.Linear, Accel = true };
        var decel = new AnimatedValue { Values = new() { 0, 100 }, Mode = MovementMode.Linear, Decel = true };
        Assert.True(accel.Evaluate(bounds, 50) < 50);
        Assert.True(decel.Evaluate(bounds, 50) > 50);
    }

    [Fact]
    public void Repeat_goes_back_and_forth()
    {
        var v = new AnimatedValue { Values = new() { 0, 10 }, Mode = MovementMode.Repeat, Interval = 10 };
        var bounds = new[] { 0, 100 };
        Assert.Equal(10, v.Evaluate(bounds, 10), 6);
        Assert.Equal(0, v.Evaluate(bounds, 20), 6);
        Assert.Equal(5, v.Evaluate(bounds, 25), 6);
    }
}

public class TimelineObjectTests
{
    private static TimelineObject Shape(int start = 0, int length = 100)
        => ObjectFactory.Create(EffectRegistry.Shape, 0, start, length);

    [Fact]
    public void Adding_a_keyframe_keeps_the_motion_unchanged()
    {
        var o = Shape();
        var x = o.Draw!.Tracks["x"];
        x.Mode = MovementMode.Linear; x.Values[0] = 0; x.Values[1] = 99;
        double before = x.Evaluate(o.Bounds, 70);
        Assert.True(o.AddKeyFrame(33));
        Assert.Equal(3, x.Values.Count);
        Assert.Equal(33, x.Values[1], 6);
        Assert.Equal(before, x.Evaluate(o.Bounds, 70), 6);
    }

    [Fact]
    public void Removing_a_keyframe_restores_value_count()
    {
        var o = Shape();
        o.AddKeyFrame(50);
        o.RemoveKeyFrameAt(0);
        Assert.Empty(o.KeyFrames);
        Assert.All(o.Effects.SelectMany(e => e.Tracks.Values), v => Assert.Equal(2, v.Values.Count));
    }

    [Fact]
    public void Split_makes_two_adjacent_objects_with_continuous_values()
    {
        var o = Shape(10, 100);
        var x = o.Draw!.Tracks["x"];
        x.Mode = MovementMode.Linear; x.Values[0] = 0; x.Values[1] = 99;
        var second = o.SplitAt(60)!;
        Assert.Equal(59, o.End);
        Assert.Equal(60, second.Start);
        Assert.Equal(109, second.End);
        Assert.Equal(50, second.SourceOffsetFrames);
        Assert.Equal(o.Draw!.Tracks["x"].Values[^1], second.Draw!.Tracks["x"].Values[0], 6);
        Assert.Equal(99, second.Draw!.Tracks["x"].Values[^1], 6);
    }

    [Fact]
    public void Moving_shifts_keyframes_and_shrinking_drops_outside_ones()
    {
        var o = Shape(0, 100);
        o.AddKeyFrame(20); o.AddKeyFrame(80);
        o.SetRange(10, 109);
        Assert.Equal(new[] { 30, 90 }, o.KeyFrames);
        o.SetRange(10, 50);
        Assert.Equal(new[] { 30 }, o.KeyFrames);
        Assert.All(o.Effects.SelectMany(e => e.Tracks.Values), v => Assert.Equal(3, v.Values.Count));
    }
}

public class SerializationTests
{
    [Fact]
    public void Project_round_trips_through_file()
    {
        var p = new Project();
        p.Settings.FrameRate = "30000/1001";
        var o = ObjectFactory.Create(EffectRegistry.Text, 3, 30, 90);
        o.Main.Values["text"] = "こんにちは";
        o.AddKeyFrame(60);
        o.Draw!.Tracks["x"].Mode = MovementMode.Curve;
        ObjectFactory.AddFilter(o, "filter.blur");
        p.Root.Objects.Add(o);
        p.Root.EditLayer(2).Visible = false;

        var path = Path.Combine(Path.GetTempPath(), $"vb-test-{Guid.NewGuid():N}.vbproj");
        try
        {
            ProjectSerializer.Save(p, path);
            var q = ProjectSerializer.Load(path);
            Assert.Equal("30000/1001", q.Settings.FrameRate);
            var r = Assert.Single(q.Root.Objects);
            Assert.Equal("こんにちは", r.Main.GetString("text"));
            Assert.Equal(new[] { 60 }, r.KeyFrames);
            Assert.Equal(MovementMode.Curve, r.Draw!.Tracks["x"].Mode);
            Assert.Equal(3, r.Effects.Count);
            Assert.False(q.Root.GetLayer(2).Visible);
            Assert.Equal(ProjectSerializer.ToJson(p), ProjectSerializer.ToJson(q));
        }
        finally { File.Delete(path); }
    }
}

public class EditorSessionTests
{
    [Fact]
    public void Undo_and_redo_restore_objects()
    {
        var s = new EditorSession();
        s.AddObject(EffectRegistry.Shape, 0, 0, 30);
        s.AddObject(EffectRegistry.Text, 0, 40, 30);
        Assert.Equal(2, s.Scene.Objects.Count);
        s.Undo();
        Assert.Single(s.Scene.Objects);
        s.Redo();
        Assert.Equal(2, s.Scene.Objects.Count);
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void New_object_goes_to_a_lower_layer_when_the_spot_is_taken()
    {
        var s = new EditorSession();
        s.AddObject(EffectRegistry.Shape, 0, 0, 100);
        var second = s.AddObject(EffectRegistry.Shape, 0, 50, 100)!;
        Assert.Equal(1, second.Layer);
    }

    [Fact]
    public void New_object_is_shortened_to_fit_before_the_next_one()
    {
        var s = new EditorSession();
        s.AddObject(EffectRegistry.Shape, 0, 50, 100);
        var o = s.AddObject(EffectRegistry.Shape, 0, 0, 100)!;
        Assert.Equal(0, o.Layer);
        Assert.Equal(49, o.End);
    }

    [Fact]
    public void Video_with_audio_creates_a_linked_pair_that_is_deleted_together()
    {
        var s = new EditorSession();
        var v = s.AddMediaFile("/tmp/a.mp4", 0, 0, 90, hasAudio: true)!;
        Assert.Equal(2, s.Scene.Objects.Count);
        Assert.NotNull(v.GroupId);
        s.DeleteSelected();
        Assert.Empty(s.Scene.Objects);
        s.Undo();
        Assert.Equal(2, s.Scene.Objects.Count);
    }

    [Fact]
    public void Drag_edit_is_a_single_undo_step_and_cancel_restores()
    {
        var s = new EditorSession();
        var o = s.AddObject(EffectRegistry.Shape, 0, 0, 30)!;
        s.BeginEdit("移動");
        for (int i = 1; i <= 10; i++) { s.Scene.Find(o.Id)!.SetRange(i, i + 29); s.NotifyChanged(); }
        s.CommitEdit();
        Assert.Equal(10, s.Scene.Find(o.Id)!.Start);
        s.Undo();
        Assert.Equal(0, s.Scene.Find(o.Id)!.Start);

        s.BeginEdit("移動");
        s.Scene.Find(o.Id)!.SetRange(5, 34);
        s.CancelEdit();
        Assert.Equal(0, s.Scene.Find(o.Id)!.Start);
    }

    [Fact]
    public void Copy_paste_creates_new_ids()
    {
        var s = new EditorSession();
        var o = s.AddObject(EffectRegistry.Shape, 0, 0, 30)!;
        s.CopySelected();
        s.Paste(0, 100);
        Assert.Equal(2, s.Scene.Objects.Count);
        Assert.Equal(2, s.Scene.Objects.Select(x => x.Id).Distinct().Count());
        Assert.Contains(s.Scene.Objects, x => x.Start == 100 && x.Layer == 0);
    }

    [Fact]
    public void Saved_marker_tracks_dirty_state()
    {
        var s = new EditorSession();
        Assert.False(s.IsDirty);
        s.AddObject(EffectRegistry.Shape, 0, 0, 30);
        Assert.True(s.IsDirty);
        s.History.MarkSaved();
        Assert.False(s.IsDirty);
        s.Undo();
        Assert.True(s.IsDirty);
    }
}

public class ProjectMediaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vb-pm-{Guid.NewGuid():N}");

    public ProjectMediaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Touch(string relative, string content = "x")
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Project WithImages(params string[] files)
    {
        var p = new Project();
        int layer = 0;
        foreach (var f in files)
        {
            var o = ObjectFactory.Create(EffectRegistry.Image, layer++, 0, 30);
            o.Main.Values["file"] = f;
            p.Root.Objects.Add(o);
        }
        return p;
    }

    [Fact]
    public void Missing_files_are_found_and_relinked_from_a_folder()
    {
        var real = Touch("new/deep/a.png");
        var p = WithImages(Path.Combine(_dir, "old", "a.png"), Path.Combine(_dir, "old", "gone.png"));
        Assert.Equal(2, ProjectMedia.FindMissing(p).Count);
        Assert.Equal(1, ProjectMedia.RelinkFromFolder(p, Path.Combine(_dir, "new")));
        Assert.Equal(real, p.Root.Objects[0].Main.GetString("file"));
        Assert.Single(ProjectMedia.FindMissing(p));
    }

    [Fact]
    public void A_project_moved_together_with_its_media_still_opens()
    {
        var img = Touch("work/media/a.png");
        var proj = Path.Combine(_dir, "work", "p.vbproj");
        ProjectSerializer.Save(WithImages(img), proj);
        Directory.Move(Path.Combine(_dir, "work"), Path.Combine(_dir, "moved"));
        var q = ProjectSerializer.Load(Path.Combine(_dir, "moved", "p.vbproj"));
        Assert.Equal(Path.Combine(_dir, "moved", "media", "a.png"), q.Root.Objects[0].Main.GetString("file"));
        Assert.Empty(ProjectMedia.FindMissing(q));
    }

    [Fact]
    public void Collect_copies_media_and_keeps_same_named_files_apart()
    {
        var a1 = Touch("one/a.png", "first");
        var a2 = Touch("two/a.png", "second!");
        var p = WithImages(a1, a2, a1);
        var target = Path.Combine(_dir, "collected");
        Assert.Equal(2, ProjectMedia.Collect(p, target));
        var files = p.Root.Objects.Select(o => o.Main.GetString("file")).ToList();
        Assert.All(files, f => Assert.StartsWith(target, f));
        Assert.Equal(files[0], files[2]);
        Assert.NotEqual(files[0], files[1]);
        Assert.Equal("first", File.ReadAllText(files[0]));
        Assert.Equal("second!", File.ReadAllText(files[1]));
        Assert.Equal(0, ProjectMedia.Collect(p, target));     // 2 回目は何もコピーしない
    }
}

public class AliasAndRangeTests
{
    [Fact]
    public void Alias_round_trips_and_is_inserted_at_the_requested_place()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vb-alias-{Guid.NewGuid():N}");
        try
        {
            var s = new EditorSession();
            var t = s.AddObject(EffectRegistry.Text, 3, 100, 60)!;
            t.Main.Values["text"] = "見出し";
            ObjectFactory.AddFilter(t, "filter.border");
            t.AddKeyFrame(130);
            var store = new AliasStore(dir);
            store.Save("見出し/テロップ", new[] { t });
            Assert.Equal(new[] { "見出し_テロップ" }, store.List());

            var loaded = store.Load("見出し/テロップ");
            Assert.Equal(0, loaded[0].Start);
            Assert.Equal(new[] { 30 }, loaded[0].KeyFrames);
            Assert.Equal(1, s.Insert(loaded, 0, 500, "エイリアスの追加"));
            var added = s.Primary!;
            Assert.Equal((0, 500, 559), (added.Layer, added.Start, added.End));
            Assert.Equal("見出し", added.Main.GetString("text"));
            Assert.Equal(3, added.Effects.Count);
            Assert.NotEqual(t.Id, added.Id);
            s.Undo();
            Assert.Single(s.Scene.Objects);
            store.Delete("見出し/テロップ");
            Assert.Empty(store.List());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Export_range_defaults_to_the_whole_scene_and_is_undoable()
    {
        var s = new EditorSession();
        s.AddObject(EffectRegistry.Shape, 0, 0, 100);
        Assert.Equal((0, 99), s.EffectiveRange);
        s.SetRange(20, null);
        s.SetRange(s.Scene.RangeStart, 59);
        Assert.Equal((20, 59), s.EffectiveRange);
        s.SetRange(80, 30);                                  // 逆順は入れ替える
        Assert.Equal((30, 80), s.EffectiveRange);
        s.Undo();
        Assert.Equal((20, 59), s.EffectiveRange);
        var q = ProjectSerializer.FromJson(ProjectSerializer.ToJson(s.Project));
        Assert.Equal(20, q.Root.RangeStart);
    }
}

