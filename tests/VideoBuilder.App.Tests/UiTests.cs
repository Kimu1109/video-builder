using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VideoBuilder.App;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.App.Shell;
using VideoBuilder.App.Views;
using VideoBuilder.App.Workspaces.Advanced;
using VideoBuilder.App.Workspaces.Simple;
using VideoBuilder.Core;

[assembly: AvaloniaTestApplication(typeof(VideoBuilder.App.Tests.TestApp))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace VideoBuilder.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // 利用者の設定ファイルを読み書きしないよう、設定の置き場所を一時フォルダにする
        var dir = Path.Combine(Path.GetTempPath(), "vb-ui-tests-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(dir, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(dir, "data"));
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
    }
}

public class UiTests
{
    /// <summary>画面のキャプチャの、ウィンドウ座標 p のピクセル (BGRA)。</summary>
    private static unsafe Color ScreenPixel(Avalonia.Media.Imaging.WriteableBitmap shot, Point p)
    {
        using var fb = shot.Lock();
        int x = Math.Clamp((int)p.X, 0, fb.Size.Width - 1), y = Math.Clamp((int)p.Y, 0, fb.Size.Height - 1);
        byte* px = (byte*)fb.Address + (long)y * fb.RowBytes + x * 4;
        return fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromArgb(px[3], px[0], px[1], px[2]) : Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    private static MainWindow? _window;

    /// <summary>テスト間でウィンドウを使い回し、プロジェクトだけ新しくする。</summary>
    private static MainWindow Open()
    {
        if (_window == null)
        {
            Svc.Initialize(Application.Current!);
            Svc.Settings.WindowMode = WindowMode.Docked;
            Svc.Settings.Theme = ThemeMode.Dark;
            Svc.Theme.Apply();
            _window = new MainWindow();
            _window.Show();
        }
        // シンプル編集のテストのあとでも、詳細編集から始める
        if (_window.Workspace.Mode != EditorMode.Advanced) EditorCommands.SetEditorMode(EditorMode.Advanced);
        Svc.Session.NewProject(new ProjectSettings());
        Svc.Settings.Snap = false;
        Pump();
        return _window;
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static Point CanvasPoint(MainWindow w, int frame, int layer)
    {
        var c = w.Timeline.Canvas;
        var local = new Point(c.FrameToX(frame) + 1, c.LayerToY(layer) + c.LayerHeight / 2);
        return c.TranslatePoint(local, w)!.Value;
    }

    [AvaloniaFact]
    public void Dragging_an_object_moves_it_and_undo_restores_it()
    {
        var w = Open();
        var s = Svc.Session;
        var o = s.AddObject(EffectRegistry.Shape, 0, 10, 30)!;
        Pump();
        var c = w.Timeline.Canvas;
        double ppf = c.PixelsPerFrame;

        var from = CanvasPoint(w, 20, 0);
        var to = new Point(from.X + 15 * ppf, from.Y + c.LayerHeight * 2);
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point(from.X + 6, from.Y));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
        Pump();

        var moved = s.Scene.Find(o.Id)!;
        Assert.Equal(25, moved.Start);
        Assert.Equal(54, moved.End);
        Assert.Equal(2, moved.Layer);

        s.Undo();
        var back = s.Scene.Find(o.Id)!;
        Assert.Equal((10, 39, 0), (back.Start, back.End, back.Layer));
    }

    [AvaloniaFact]
    public void Dragging_the_right_edge_changes_the_length_but_not_past_the_next_object()
    {
        var w = Open();
        var s = Svc.Session;
        var a = s.AddObject(EffectRegistry.Shape, 0, 0, 30)!;
        s.AddObject(EffectRegistry.Shape, 0, 50, 30);
        Pump();
        var c = w.Timeline.Canvas;
        var edge = c.TranslatePoint(new Point(c.FrameToX(30) - 2, c.LayerToY(0) + 10), w)!.Value;
        w.MouseDown(edge, MouseButton.Left);
        w.MouseMove(new Point(edge.X + 5, edge.Y));
        w.MouseMove(new Point(edge.X + 10 * c.PixelsPerFrame, edge.Y));
        Pump();
        Assert.Equal(39, s.Scene.Find(a.Id)!.End);
        w.MouseMove(new Point(edge.X + 100 * c.PixelsPerFrame, edge.Y));   // 隣のオブジェクトを越えようとする
        w.MouseUp(new Point(edge.X + 100 * c.PixelsPerFrame, edge.Y), MouseButton.Left);
        Pump();
        Assert.Equal(49, s.Scene.Find(a.Id)!.End);
        Assert.Equal(0, s.Scene.Find(a.Id)!.Start);
    }

    [AvaloniaFact]
    public void Objects_do_not_overlap_when_dropped_onto_another()
    {
        var w = Open();
        var s = Svc.Session;
        var a = s.AddObject(EffectRegistry.Shape, 0, 0, 30)!;
        var b = s.AddObject(EffectRegistry.Shape, 0, 60, 30)!;
        Pump();
        var c = w.Timeline.Canvas;
        var from = CanvasPoint(w, 70, 0);
        var to = new Point(from.X - 45 * c.PixelsPerFrame, from.Y);   // a の上に重ねようとする
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point(from.X - 6, from.Y));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
        Pump();
        var a2 = s.Scene.Find(a.Id)!; var b2 = s.Scene.Find(b.Id)!;
        Assert.True(b2.Start > a2.End || b2.End < a2.Start, $"重なっている: a={a2.Start}-{a2.End} b={b2.Start}-{b2.End}");
    }

    [AvaloniaFact]
    public void Clicking_the_ruler_seeks_and_keyboard_shortcuts_edit()
    {
        var w = Open();
        var s = Svc.Session;
        var o = s.AddObject(EffectRegistry.Shape, 0, 0, 60)!;
        Pump();
        var c = w.Timeline.Canvas;
        var ruler = c.TranslatePoint(new Point(c.FrameToX(30) + 1, 8), w)!.Value;
        w.MouseDown(ruler, MouseButton.Left);
        w.MouseUp(ruler, MouseButton.Left);
        Assert.Equal(30, s.CurrentFrame);

        s.Select(o.Id);
        w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(31, s.CurrentFrame);
        w.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.None);            // 中間点
        Assert.Equal(new[] { 31 }, s.Scene.Find(o.Id)!.KeyFrames);
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);         // 元に戻す
        Assert.Empty(s.Scene.Find(o.Id)!.KeyFrames);
        w.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);            // 分割
        Assert.Equal(2, s.Scene.Objects.Count);
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Single(s.Scene.Objects);
        s.Select(o.Id);
        w.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Empty(s.Scene.Objects);
        w.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);         // やり直しは何もない → そのまま
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Single(s.Scene.Objects);
    }

    [AvaloniaFact]
    public void Layer_header_toggles_visibility_and_lock()
    {
        var w = Open();
        var s = Svc.Session;
        var c = w.Timeline.Canvas;
        // レイヤー名をクリックしても表示は変わらない (そのレイヤーのオブジェクトを選ぶだけ)
        var name = c.TranslatePoint(new Point(20, c.LayerToY(1) + 10), w)!.Value;
        w.MouseDown(name, MouseButton.Left); w.MouseUp(name, MouseButton.Left);
        Assert.True(s.Scene.GetLayer(1).Visible);
        var eye = c.TranslatePoint(new Point(TimelineCanvas.HeaderWidth - 33, c.LayerToY(1) + 10), w)!.Value;
        w.MouseDown(eye, MouseButton.Left); w.MouseUp(eye, MouseButton.Left);
        Assert.False(s.Scene.GetLayer(1).Visible);
        var lockPos = c.TranslatePoint(new Point(TimelineCanvas.HeaderWidth - 10, c.LayerToY(1) + 10), w)!.Value;
        w.MouseDown(lockPos, MouseButton.Left); w.MouseUp(lockPos, MouseButton.Left);
        Assert.True(s.Scene.GetLayer(1).Locked);

        // ロックしたレイヤーのオブジェクトは選択できない
        s.Edit("test", () => s.Scene.Objects.Add(ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 30)));
        s.Select(null);
        Pump();
        var p = CanvasPoint(w, 10, 1);
        w.MouseDown(p, MouseButton.Left); w.MouseUp(p, MouseButton.Left);
        Assert.Empty(s.SelectionIds);
    }

    [AvaloniaFact]
    public void Settings_panel_edits_the_value_of_the_current_section()
    {
        var w = Open();
        var s = Svc.Session;
        var o = s.AddObject(EffectRegistry.Shape, 0, 0, 100)!;
        s.AddKeyFrameToSelected(50);
        s.CurrentFrame = 70;      // 2 つ目の区間
        Pump();

        var boxes = w.GetVisualDescendants().OfType<NumberBox>().ToList();
        Assert.True(boxes.Count >= 12, $"NumberBox が {boxes.Count} 個しかない");
        // 標準描画の X の左側の入力欄を探す: 図形 (サイズ・縦横比・ライン幅) の 3 行 × 2 個のあと
        var xLeft = boxes[6];
        xLeft.Focus();
        xLeft.Text = "100+23";
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Pump();
        var x = s.Scene.Find(o.Id)!.Draw!.Tracks["x"];
        Assert.Equal(new[] { 0.0, 123.0, 0.0 }, x.Values);
        Assert.False(boxes[7].IsEnabled);    // 移動方法なしのとき、右側は使えない

        s.Undo();
        Pump();
        Assert.Equal(0, s.Scene.Find(o.Id)!.Draw!.Tracks["x"].Values[1]);
        Assert.Equal("0.0", xLeft.Text);
    }

    [AvaloniaFact]
    public void Theme_switches_at_runtime_and_accent_keeps_contrast()
    {
        var w = Open();
        EditorCommands.SetTheme(ThemeMode.Light);
        Pump();
        Assert.Equal(ThemeVariant.Light, Application.Current!.ActualThemeVariant);
        Assert.Equal(Color.Parse("#F3F3F3"), ((ISolidColorBrush)Svc.Brush("Bg.Base")).Color);
        Assert.Equal(Color.Parse("#F3F3F3"), ((ISolidColorBrush)w.Background!).Color);

        EditorCommands.SetTheme(ThemeMode.Dark);
        Pump();
        Assert.Equal(Color.Parse("#1E1E1E"), ((ISolidColorBrush)Svc.Brush("Bg.Base")).Color);

        // 背景に埋もれる色を指定しても、コントラスト比 3:1 以上に調整される
        Svc.Settings.Accent = AccentMode.Custom;
        Svc.Settings.CustomAccent = "#262626";
        Svc.Theme.Apply();
        var accent = ((ISolidColorBrush)Svc.Brush("Accent")).Color;
        Assert.True(AccentPalette.Contrast(accent, Color.Parse("#252526")) >= 3.0, accent.ToString());
        Svc.Settings.Accent = AccentMode.AppDefault;
        Svc.Theme.Apply();
        Assert.Equal(Color.Parse("#2F80ED"), ((ISolidColorBrush)Svc.Brush("Accent")).Color);
    }

    [AvaloniaFact]
    public async Task Preview_renders_the_current_frame_and_dragging_moves_the_object()
    {
        var w = Open();
        var s = Svc.Session;
        var o = s.AddObject(EffectRegistry.Shape, 0, 0, 60, x => x.Main.Values["color"] = "#FF0000")!;
        uint center = 0;
        for (int i = 0; i < 100 && center != 0xFFFF0000; i++)
        {
            await Task.Delay(20);
            Pump();
            Svc.Preview.WithFrame(b => center = (uint)b.GetPixel(b.Width / 2, b.Height / 2));
        }
        Assert.Equal(0xFFFF0000u, center);
        Assert.Contains(Svc.Preview.Quads, q => q.Id == o.Id);

        var preview = w.GetVisualDescendants().OfType<PreviewView>().Single();
        preview.InvalidateVisual();
        w.CaptureRenderedFrame()?.Dispose();    // 描画して、表示位置を確定させる
        var c = preview.TranslatePoint(new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2), w)!.Value;
        // 画面に出たプレビュー (描画のスレッドのフレームを、複製せずにそのまま描いたもの) も赤い
        preview.InvalidateVisual();
        using (var shot = w.CaptureRenderedFrame())
        {
            Assert.NotNull(shot);
            var px = ScreenPixel(shot!, c);
            Assert.True(px.R > 200 && px.G < 60 && px.B < 60, $"画面のプレビューが赤くない: {px}");
        }
        w.MouseDown(c, MouseButton.Left);
        w.MouseMove(new Point(c.X + 5, c.Y));
        w.MouseMove(new Point(c.X + 50, c.Y + 20));
        w.MouseUp(new Point(c.X + 50, c.Y + 20), MouseButton.Left);
        Pump();
        var draw = s.Scene.Find(o.Id)!.Draw!;
        Assert.True(draw.Tracks["x"].Values[0] > 20, "X が右に動いていない");
        Assert.True(draw.Tracks["y"].Values[0] > 5, "Y が下に動いていない");
        s.Undo();
        Assert.Equal(0, s.Scene.Find(o.Id)!.Draw!.Tracks["x"].Values[0]);
    }

    [AvaloniaFact]
    public void Window_mode_can_be_switched_between_docked_and_separate()
    {
        var w = Open();
        Svc.Settings.WindowMode = WindowMode.Separate;
        w.ApplyWindowMode();
        Pump();
        Assert.Equal(2, w.ToolWindows.Count());
        Assert.DoesNotContain(w.Timeline, w.GetVisualDescendants());
        Svc.Settings.WindowMode = WindowMode.Docked;
        w.ApplyWindowMode();
        Pump();
        Assert.Empty(w.ToolWindows);
        Assert.Contains(w.Timeline, w.GetVisualDescendants());
    }

    [AvaloniaFact]
    public async Task Playback_advances_the_current_frame_and_stops_at_the_end()
    {
        Open();
        var s = Svc.Session;
        s.AddObject(EffectRegistry.Shape, 0, 0, 12);    // 30fps で 0.4 秒
        Svc.Preview.Play();
        Assert.True(Svc.Preview.IsPlaying);
        int seen = 0;
        for (int i = 0; i < 150 && Svc.Preview.IsPlaying; i++)
        {
            await Task.Delay(10);
            Pump();
            seen = Math.Max(seen, s.CurrentFrame);
        }
        Assert.False(Svc.Preview.IsPlaying);     // 末尾で自動的に止まる
        Assert.Equal(11, s.CurrentFrame);
        Assert.True(seen >= 5);
    }

    [AvaloniaFact]
    public async Task Demo_project_renders_fast_enough_to_report()
    {
        Open();
        DemoProject.Load(Svc.Session);
        double worst = 0;
        for (int f = 0; f < 240; f += 20)
        {
            Svc.Session.CurrentFrame = f;
            await Task.Delay(120);
            Pump();
            worst = Math.Max(worst, Svc.Preview.LastRenderMs);
        }
        Console.WriteLine($"BENCH demo 1080p worst frame: {worst:0.0} ms");
        Assert.True(worst > 0);
    }

    [AvaloniaFact]
    public async Task Render_backend_can_be_switched_while_running()
    {
        Open();
        var s = Svc.Session;
        s.AddObject(EffectRegistry.Shape, 0, 0, 60, x => x.Main.Values["color"] = "#00FF00");

        async Task<uint> CenterAfterSwitch(RenderBackend backend)
        {
            Svc.Settings.RenderBackend = backend;
            bool changed = false;
            void OnChanged() => changed = true;
            Svc.Preview.BackendChanged += OnChanged;
            Svc.Preview.ReloadBackend();
            for (int i = 0; i < 200 && !changed; i++) { await Task.Delay(20); Pump(); }
            Svc.Preview.BackendChanged -= OnChanged;
            Assert.True(changed, "描画方式が切り替わらない");
            uint center = 0;
            for (int i = 0; i < 100 && center != 0xFF00FF00; i++)
            {
                s.NotifyChanged();
                await Task.Delay(20);
                Pump();
                Svc.Preview.WithFrame(b => center = (uint)b.GetPixel(b.Width / 2, b.Height / 2));
            }
            return center;
        }

        Assert.Equal(0xFF00FF00u, await CenterAfterSwitch(RenderBackend.Gpu));
        Console.WriteLine("BENCH app backend (Gpu requested): " + Svc.Preview.BackendDescription);
        // GPU コンテキストを作れる環境なら GPU、作れなければ CPU に落ちて描画は続く
        Assert.True(Svc.Preview.BackendDescription.StartsWith("GPU") == Svc.Preview.IsGpuActive);

        Assert.Equal(0xFF00FF00u, await CenterAfterSwitch(RenderBackend.Cpu));
        Assert.False(Svc.Preview.IsGpuActive);
        Assert.Equal("CPU", Svc.Preview.BackendDescription);

        Assert.Equal(0xFF00FF00u, await CenterAfterSwitch(RenderBackend.Auto));
        Console.WriteLine("BENCH app backend (Auto): " + Svc.Preview.BackendDescription);
    }

    [AvaloniaFact]
    public void Bracket_keys_set_the_export_range_and_shortcuts_can_be_reassigned()
    {
        var w = Open();
        var s = Svc.Session;
        s.AddObject(EffectRegistry.Shape, 0, 0, 100);
        s.CurrentFrame = 20;
        w.KeyPressQwerty(PhysicalKey.BracketLeft, RawInputModifiers.None);
        s.CurrentFrame = 70;
        w.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Assert.Equal((20, 70), s.EffectiveRange);

        // 「分割」を S から K に変える
        try
        {
            Assert.Null(Shortcuts.Assign("edit.split", new KeyGesture(Key.K)));
            s.CurrentFrame = 50;
            w.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
            Assert.Single(s.Scene.Objects);
            w.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.None);
            Assert.Equal(2, s.Scene.Objects.Count);
            // すでに使われているキーを割り当てると、元の操作から外れる
            Assert.Equal("キーフレームを追加", Shortcuts.Assign("edit.split", new KeyGesture(Key.P)));
            Assert.Equal("", Shortcuts.GesturesOf("edit.addKey"));
        }
        finally { Shortcuts.Reset(); }
        Assert.Equal("S", Shortcuts.GesturesOf("edit.split"));
        Assert.Equal("P", Shortcuts.GesturesOf("edit.addKey"));
    }

    [AvaloniaFact]
    public async Task Waveform_thumbnail_and_bpm_grid_are_shown_on_the_timeline()
    {
        var w = Open();
        var s = Svc.Session;
        if (!VideoBuilder.Media.FFmpegLoader.EnsureLoaded()) return;
        var dir = Path.Combine(Path.GetTempPath(), "vb-ui-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var clip = Path.Combine(dir, "clip.mp4");
            using (var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                $"-y -loglevel error -f lavfi -i testsrc=size=320x180:rate=30:duration=4 -f lavfi -i \"sine=frequency=3:beep_factor=200:duration=4\" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{clip}\"")))
            {
                proc!.WaitForExit(30000);
                if (proc.ExitCode != 0) return;
            }
            Assert.Null(MediaImport.AddFile(clip, 0, 0));
            Assert.Equal(2, s.Scene.Objects.Count);        // 映像 + 音声
            Assert.Equal(320, s.Project.Settings.Width);   // 最初の動画にプロジェクトを合わせる
            s.Edit("grid", () => s.Project.Grid = new GridSettings { Mode = GridMode.Bpm, Bpm = 120 });

            WaveformCache.Peaks? peaks = null; Avalonia.Media.Imaging.Bitmap? thumb = null;
            for (int i = 0; i < 300 && (peaks is not { IsComplete: true } || thumb == null); i++)
            {
                await Task.Delay(20);
                Pump();
                if (peaks is not { IsComplete: true }) peaks = Svc.Waveforms.Get(clip);   // 途中までのものが先に返ることがある
                thumb ??= Svc.Thumbnails.Get(clip, 0, true);
            }
            Assert.NotNull(peaks);
            Assert.InRange(peaks!.Values.Length, 180, 220);            // 4 秒 × 50
            Assert.Contains(peaks.Values, v => v > 20);
            Assert.NotNull(thumb);
            Assert.Equal(ThumbnailCache.Height, (int)thumb!.Size.Height);

            w.Timeline.Canvas.InvalidateVisual();
            Pump();
            var shot = Environment.GetEnvironmentVariable("VB_UI_SHOT");
            if (!string.IsNullOrEmpty(shot)) w.CaptureRenderedFrame()?.Save(shot);
        }
        finally
        {
            s.NewProject(new ProjectSettings());
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public void Alias_can_be_created_from_the_store_and_inserted()
    {
        Open();
        var s = Svc.Session;
        var t = s.AddObject(EffectRegistry.Text, 0, 0, 30)!;
        Svc.Aliases.Save("テスト用", new[] { t });
        try
        {
            EditorCommands.InsertAlias("テスト用", 4, 200);
            Assert.Equal(2, s.Scene.Objects.Count);
            Assert.Equal((4, 200), (s.Primary!.Layer, s.Primary.Start));
        }
        finally { Svc.Aliases.Delete("テスト用"); }
    }

    private static void Shot(Window w, string name)
    {
        // VB_UI_SHOT_DIR を指定すると、画面の確認用にスクリーンショットを保存する
        var dir = Environment.GetEnvironmentVariable("VB_UI_SHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
    }

    [AvaloniaFact]
    public void Template_is_inserted_as_one_object_and_its_exposed_items_are_edited_in_the_properties()
    {
        var w = Open();
        var s = Svc.Session;
        var t = s.AddObject(EffectRegistry.Text, 0, 0, 60)!;
        t.Main.Values["text"] = "元の名前";
        var shape = s.AddObject(EffectRegistry.Shape, 1, 0, 60)!;
        var pkg = TemplatePackage.Create(s.Project, new[] { t, shape }, new TemplateOptions
        {
            Name = "UI テスト用テンプレート", Category = "テスト", IntroSeconds = 0.5,
            Exposed = new() { new ExposedParam { ObjectId = t.Id, Effect = 0, Key = "text", Label = "名前" },
                              new ExposedParam { ObjectId = shape.Id, Effect = 0, Key = "color", Label = "帯の色" } },
        });
        pkg.Thumbnail = TemplateService.RenderThumbnail(pkg, s.Project.Settings);
        Assert.NotNull(pkg.Thumbnail);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, pkg.Thumbnail![..4]);
        Svc.Templates.Save(pkg);
        var entry = Svc.Templates.List().Single(e => e.Name == "UI テスト用テンプレート");
        try
        {
            EditorCommands.InsertTemplate(entry, 4, 200);
            var placed = s.Primary!;
            Assert.Equal(EffectRegistry.SceneRef, placed.Main.DefinitionId);
            Assert.Equal((4, 200), (placed.Layer, placed.Start));
            s.CurrentFrame = 210;
            Pump();

            // プロパティの先頭に「テンプレートの項目」が出て、中のテキストを編集できる
            var settings = w.GetVisualDescendants().OfType<ObjectSettingsView>().Single();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == L.T("テンプレートの項目") && b.IsEffectivelyVisible);
            var box = settings.GetVisualDescendants().OfType<TextBox>().First(b => b.Text == "元の名前" && b.IsEffectivelyVisible);
            box.Text = "新しい名前";
            Pump();
            var scene = s.Project.FindScene(s.Primary!.Main.GetString("scene"))!;
            Assert.Equal("新しい名前", scene.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Text).Main.GetString("text"));
            Shot(w, "template-properties");

            s.Undo();
            Pump();
            scene = s.Project.FindScene(s.Primary!.Main.GetString("scene"))!;
            Assert.Equal("元の名前", scene.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Text).Main.GetString("text"));
            Assert.Equal("元の名前", box.Text);

            // シーンの中で、パラメータの公開をやめられる
            s.SwitchScene(scene.Id);
            var inner = s.Scene.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Text);
            s.SetExposed(inner, 0, "text", null);
            Assert.Single(s.Scene.Exposed!);
        }
        finally { Svc.Templates.Delete(entry); }
    }

    [AvaloniaFact]
    public void Template_dialogs_show_candidates_and_the_library()
    {
        var w = Open();
        var s = Svc.Session;
        var t = s.AddObject(EffectRegistry.Text, 0, 0, 60)!;
        var img = s.AddObject(EffectRegistry.Image, 1, 0, 60)!;
        var save = new TemplateSaveDialog(s.Project, new[] { t, img }, "見出し", new[] { "テロップ" });
        save.Show(w);
        Pump();
        // テキストの本文と画像のファイルは、最初から公開する項目に選ばれている
        var checkedRows = save.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsChecked == true).ToList();
        Assert.Equal(2, checkedRows.Count);
        Shot(save, "template-save-dialog");
        save.Close();

        var pkg = TemplatePackage.Create(s.Project, new[] { t }, new TemplateOptions { Name = "ライブラリ表示用", Category = "テロップ" });
        pkg.Thumbnail = TemplateService.RenderThumbnail(pkg, s.Project.Settings);
        pkg.Preview = TemplateService.RenderPreview(pkg, s.Project.Settings);
        using (var strip = SkiaSharp.SKBitmap.Decode(pkg.Preview!))
            Assert.Equal((160 * TemplatePackage.PreviewFrames, 90), (strip.Width, strip.Height));   // 長辺 160px を 12 枚
        Svc.Templates.Save(pkg);
        var entry = Svc.Templates.List().Single(e => e.Name == "ライブラリ表示用");
        try
        {
            var lib = new TemplateLibraryDialog();
            lib.Show(w);
            Pump();
            var list = lib.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.True(list.ItemCount >= 1);
            Assert.Contains(lib.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "ライブラリ表示用");
            Shot(lib, "template-library");
            // マウスを乗せるとプレビューを再生し、離すとサムネイルに戻る
            int index = Enumerable.Range(0, list.ItemCount).First(i => ((Control)((IList<Control>)list.ItemsSource!)[i]).GetVisualDescendants().OfType<TextBlock>().Any(b => b.Text == "ライブラリ表示用"));
            var image = ((Control)((IList<Control>)list.ItemsSource!)[index]).GetVisualDescendants().OfType<Image>().First();
            var thumb = image.Source;
            lib.HoverForTest(index, true);
            Assert.Equal(0, lib.PreviewFrame);
            Assert.NotSame(thumb, image.Source);
            lib.HoverForTest(index, false);
            Assert.Equal(-1, lib.PreviewFrame);
            Assert.Same(thumb, image.Source);
            var insert = lib.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "挿入");
            int before = s.Scene.Objects.Count;
            insert.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.Equal(before + 1, s.Scene.Objects.Count);
        }
        finally { Svc.Templates.Delete(entry); }
    }

    [AvaloniaFact]
    public void Vector_files_and_templates_can_be_dropped_on_the_timeline()
    {
        Open();
        var s = Svc.Session;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-ui-vector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lottie = Path.Combine(dir, "a.json");
            File.WriteAllText(lottie, """{"v":"5.7.4","fr":30,"ip":0,"op":45,"w":100,"h":100,"layers":[]}""");
            Assert.Null(MediaImport.AddFile(lottie, 0, 0));
            Assert.Equal(EffectRegistry.Vector, s.Primary!.Main.DefinitionId);
            Assert.Equal(45, s.Primary.Length);   // Lottie の長さ (1.5 秒)

            var pkg = TemplatePackage.Create(s.Project, new[] { s.Primary }, new TemplateOptions { Name = "ドロップ用" });
            var file = Path.Combine(dir, "drop" + TemplatePackage.Extension);
            pkg.Save(file);
            Assert.Null(MediaImport.AddFile(file, 3, 10));
            Assert.Equal(EffectRegistry.SceneRef, s.Primary!.Main.DefinitionId);
            Assert.Equal((3, 10), (s.Primary.Layer, s.Primary.Start));
            Assert.Contains(Svc.Templates.List(), e => e.Name == "ドロップ用");   // ライブラリにも取り込まれる
        }
        finally
        {
            foreach (var e in Svc.Templates.List().Where(e => e.Name == "ドロップ用")) Svc.Templates.Delete(e);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public void Lottie_colors_appear_in_the_properties_only_as_many_as_the_file_has()
    {
        var w = Open();
        var s = Svc.Session;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-ui-lottie-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "two.json");
            // 赤い塗りと緑の線 (2 色)
            File.WriteAllText(path, """{"v":"5.7.4","fr":30,"ip":0,"op":30,"w":100,"h":100,"layers":[{"ty":4,"ip":0,"op":30,"st":0,"ks":{},"shapes":[{"ty":"fl","c":{"a":0,"k":[1,0,0,1]}},{"ty":"st","c":{"a":0,"k":[0,1,0,1]}}]}]}""");
            var o = s.AddObject(EffectRegistry.Vector, 0, 0, 30, x => x.Main.Values["file"] = path)!;
            Pump();
            var settings = w.GetVisualDescendants().OfType<ObjectSettingsView>().Single();
            var labels = settings.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(L.T("色 1"), labels);
            Assert.Contains(L.T("色 2"), labels);
            Assert.DoesNotContain(L.T("色 3"), labels);
            // 空の色は、ファイルの色を表示する
            Assert.Contains(labels, t => t != null && t.StartsWith("#FF0000"));
            Assert.Contains(labels, t => t != null && t.StartsWith("#00FF00"));

            s.Edit("色", () => s.Scene.Find(o.Id)!.Main.Values["color1"] = "#0000FF");
            Pump();
            labels = settings.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("#0000FF", labels);
            Shot(w, "lottie-colors");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [AvaloniaFact]
    public void Template_items_are_grouped_reordered_and_files_are_dropped_on_them()
    {
        var w = Open();
        var s = Svc.Session;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-ui-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string Png(string name)
            {
                var path = Path.Combine(dir, name);
                using var bmp = new SkiaSharp.SKBitmap(4, 4);
                using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, data.ToArray());
                return path;
            }
            var scene = s.AddScene();
            var text = s.AddObject(EffectRegistry.Text, 0, 0, 60)!;
            var image = s.AddObject(EffectRegistry.Image, 1, 0, 60, o => o.Main.Values["file"] = Png("logo.png"))!;
            s.SetExposed(text, 0, "text", "名前");
            s.SetExposed(image, 0, "file", "ロゴ");
            s.SetExposed(text, 0, "color", "文字色");
            s.SwitchScene("root");
            var so = s.AddObject(EffectRegistry.SceneRef, 0, 0, 60, o => o.Main.Values["scene"] = scene.Id)!;
            Pump();

            // 編集のダイアログ: 1 行目 (名前) を下へ、文字色をグループ「文字」に
            var d = new ExposedItemsDialog(s.Project.FindScene(scene.Id)!);
            d.Show(w);
            Pump();
            // 各行のボタンは 上へ・下へ・削除 の順。1 行目の「上へ」は使えないので、最初に使えるボタンが 1 行目の「下へ」
            d.GetVisualDescendants().OfType<IconButton>().First(b => b.IsEnabled)
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            var groups = d.GetVisualDescendants().OfType<AutoCompleteBox>().ToList();
            Assert.Equal(3, groups.Count);
            groups[0].Text = "文字";   // 並べ替えたあとの 1 行目 = ロゴ
            groups[2].Text = "文字";   // 3 行目 = 文字色
            Shot(d, "exposed-items-dialog");
            d.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == L.T("OK"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.NotNull(d.Result);
            s.ReplaceExposed(scene.Id, d.Result!);
            Assert.Equal(new[] { "文字/ロゴ", "文字/文字色", "/名前" }, s.Project.FindScene(scene.Id)!.Exposed!.Select(x => x.Group + "/" + x.Label).ToArray());

            // プロパティにはグループの小見出しが出る
            s.Select(so.Id);
            Pump();
            var settings = w.GetVisualDescendants().OfType<ObjectSettingsView>().Single();
            var texts = settings.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("文字", texts);

            // ロゴの欄にファイルをドロップすると差し替わる (X11 の受け取り口と同じ道筋)
            var fileBox = settings.GetVisualDescendants().OfType<TextBox>().First(b => b.Text == "logo.png" && b.IsEffectivelyVisible);
            var point = fileBox.TranslatePoint(new Point(fileBox.Bounds.Width / 2, fileBox.Bounds.Height / 2), w)!.Value;
            // 当たり判定は描いた画面で行うので、いまの配置を 1 回描いておく (実際のアプリでは常に描かれている)
            w.CaptureRenderedFrame()?.Dispose();
            Assert.True(FileDropZone.HandleExternal(w, point, null));        // ドラッグ中: 欄の上
            var other = Png("other.png");
            Assert.True(FileDropZone.HandleExternal(w, point, new[] { Path.Combine(dir, "memo.txt"), other }));
            Pump();
            var inner = s.Project.FindScene(scene.Id)!.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Image);
            Assert.Equal(other, inner.Main.GetString("file"));
            // 合わないファイルだけなら何もしない (タイムラインにも置かない)
            int count = s.Scene.Objects.Count;
            Assert.True(FileDropZone.HandleExternal(w, point, new[] { Path.Combine(dir, "memo.txt") }));
            Assert.Equal(other, s.Project.FindScene(scene.Id)!.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Image).Main.GetString("file"));
            Assert.Equal(count, s.Scene.Objects.Count);
            // 取り消せる
            s.Undo();
            Assert.EndsWith("logo.png", s.Project.FindScene(scene.Id)!.Objects.Single(o => o.Main.DefinitionId == EffectRegistry.Image).Main.GetString("file"));
            // ファイルの欄以外 (タイムライン) の上では受け取らない
            var canvasPoint = w.Timeline.Canvas.TranslatePoint(new Point(200, 40), w)!.Value;
            Assert.False(FileDropZone.HandleExternal(w, canvasPoint, null));
            Shot(w, "exposed-groups");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [AvaloniaFact]
    public void User_given_names_are_not_translated()
    {
        var w = Open();
        var s = Svc.Session;
        bool wasEnglish = L.English;
        // 対応表にある言葉 (「名前」「説明」「テキスト」) を、利用者が名前として付けた場合
        var pkg = (TemplatePackage?)null;
        try
        {
            L.Initialize(Language.English);
            Assert.Equal("Name", L.T("名前"));
            Assert.Equal("名前", L.Verbatim("名前").Text);

            var scene = s.AddScene();
            var text = s.AddObject(EffectRegistry.Text, 0, 0, 60)!;
            var image = s.AddObject(EffectRegistry.Image, 1, 0, 60)!;
            s.SetExposed(text, 0, "text", "名前");
            s.SetExposed(image, 0, "file", "テキスト");
            s.ReplaceExposed(scene.Id, s.Scene.Exposed!.Select(x => { var c = x.Clone(); c.Group = "説明"; return c; }).ToList());
            s.SwitchScene("root");
            var so = s.AddObject(EffectRegistry.SceneRef, 0, 0, 60, o => o.Main.Values["scene"] = scene.Id)!;
            Pump();
            var settings = w.GetVisualDescendants().OfType<ObjectSettingsView>().Single();
            var texts = settings.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("名前", texts);         // 表示名
            Assert.Contains("テキスト", texts);     // 表示名
            Assert.Contains("説明", texts);         // グループ名
            Assert.Contains("Template items", texts);   // アプリの文言は訳す
            Assert.Contains("Start offset", texts);     // 公開項目でないパラメータ名も訳す
            Assert.DoesNotContain("Description", texts);

            // テンプレートの名前・カテゴリ (ライブラリと右クリックのメニュー)
            pkg = TemplatePackage.Create(s.Project, new[] { so }, new TemplateOptions { Name = "テキスト", Category = "説明" });
            Svc.Templates.Save(pkg);
            var lib = new TemplateLibraryDialog();
            lib.Show(w);
            Pump();
            var libTexts = lib.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("テキスト", libTexts);
            Assert.Contains("All categories", libTexts);
            Shot(lib, "verbatim-library");
            lib.Close();
            Shot(w, "verbatim-properties");
        }
        finally
        {
            L.Initialize(wasEnglish ? Language.English : Language.Japanese);
            foreach (var e in Svc.Templates.List().Where(e => e.Name == "テキスト" && !e.Legacy)) Svc.Templates.Delete(e);
        }
    }

    [AvaloniaFact]
    public void Object_labels_and_scene_names_keep_what_the_user_typed()
    {
        var w = Open();
        var s = Svc.Session;
        bool wasEnglish = L.English;
        try
        {
            L.Initialize(Language.English);
            // 本文・シーン名が対応表の言葉 (「テキスト」「説明」) と同じでも、そのまま出す
            var text = s.AddObject(EffectRegistry.Text, 0, 0, 30)!;
            s.Edit("本文", () => s.Scene.Find(text.Id)!.Main.Values["text"] = "テキスト");
            var empty = s.AddObject(EffectRegistry.Text, 1, 0, 30, o => o.Main.Values["text"] = "")!;
            var shape = s.AddObject(EffectRegistry.Shape, 2, 0, 30)!;
            var scene = s.AddScene();
            s.Edit("名前", () => s.Project.FindScene(scene.Id)!.Name = "説明");
            s.SwitchScene("root");
            var so = s.AddObject(EffectRegistry.SceneRef, 3, 0, 30, o => o.Main.Values["scene"] = scene.Id)!;

            Assert.Equal("テキスト", Labels.Object(s.Scene.Find(text.Id)!));
            Assert.Equal("Text", Labels.Object(empty));                     // 本文がなければ種類の名前 (訳す)
            Assert.Equal("Shape [Rectangle]", Labels.Object(shape));       // アプリの名前は訳す
            Assert.Equal("Scene [説明]", Labels.Object(so));                // 型紙だけ訳す
            Assert.Equal("Main", Labels.Scene(s.Project.Root));             // アプリが付けた既定の名前は訳す
            Assert.Equal("説明", Labels.Scene(s.Project.FindScene(scene.Id)!));

            // タイムラインのシーンの一覧と、シーンオブジェクトのプロパティ
            Pump();
            var settings = w.GetVisualDescendants().OfType<ObjectSettingsView>().Single();
            var texts = settings.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("説明", texts);
            Assert.DoesNotContain("Description", texts);

            // テンプレートの保存のダイアログの「どこの項目か」も、本文をそのまま出す
            var d = new TemplateSaveDialog(s.Project, new[] { s.Scene.Find(text.Id)! }, "x", Array.Empty<string>());
            d.Show(w);
            Pump();
            Assert.Contains(d.GetVisualDescendants().OfType<TextBlock>(), t => t.Text != null && t.Text.StartsWith("テキスト › Text › "));
            d.Close();

            // タイムラインのシーンの一覧: 選んだシーンの名前がそのまま出る
            var sceneBox = w.Timeline.GetVisualDescendants().OfType<ComboBox>().First();
            Assert.Contains("説明", (IEnumerable<string>)sceneBox.ItemsSource!);
            s.SwitchScene(scene.Id);
            Pump();
            Assert.Contains("説明", sceneBox.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Shot(w, "verbatim-labels");
            s.SwitchScene("root");
        }
        finally { L.Initialize(wasEnglish ? Language.English : Language.Japanese); }
    }

    [AvaloniaFact]
    public void File_names_in_messages_and_the_title_are_not_translated()
    {
        var w = Open();
        bool wasEnglish = L.English;
        try
        {
            L.Initialize(Language.English);
            // 「テキスト」は対応表にある言葉。名前の目印を付けると、文の型紙だけが訳される
            Assert.Equal("Could not open ⁨テキスト⁩.\nboom", L.T($"{L.Name("テキスト")} を開けませんでした。\nboom"));
            Assert.Equal("Could not open テキスト.\nboom", L.Display($"{L.Name("テキスト")} を開けませんでした。\nboom"));
            Assert.Equal("Could not open Text.\nboom", L.T("テキスト を開けませんでした。\nboom"));   // 目印がなければ、これまでどおり中身も訳す
            Assert.Equal("Unsupported file format: 説明", L.Display($"対応していないファイル形式です: {L.Name("説明")}"));
            Assert.Equal("説明", L.Display(L.Name("説明")));

            // 画面に出るときは目印を取り除き、名前はそのまま
            var tb = new TextBlock { Text = $"{L.Name("テキスト.vbproj")} を開けませんでした。\nboom" };
            Assert.Equal("Could not open テキスト.vbproj.\nboom", tb.Text);
            Assert.DoesNotContain(L.NameStart, tb.Text!);
            var message = MediaImport.AddFile("/tmp/説明.xyz", 0, 0);
            var shown = new TextBlock { Text = message };
            Assert.Equal("Unsupported file format: 説明.xyz", shown.Text);

            // ウィンドウのタイトル (プロジェクトのファイル名)
            var path = Path.Combine(Path.GetTempPath(), $"vb-title-{Guid.NewGuid():N}", "テキスト.vbproj");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                Svc.Session.Save(path);
                Pump();
                Assert.StartsWith("テキスト.vbproj", w.Title);
                Assert.DoesNotContain(L.NameStart, w.Title!);
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }

            // 日本語のときも、目印は取り除く
            L.Initialize(Language.Japanese);
            var ja = new TextBlock { Text = $"{L.Name("a.vbproj")} を開けませんでした。" };
            Assert.Equal("a.vbproj を開けませんでした。", ja.Text);
        }
        finally { L.Initialize(wasEnglish ? Language.English : Language.Japanese); }
    }

    [AvaloniaFact]
    public void Newer_template_version_is_announced_and_applied_from_the_properties()
    {
        var w = Open();
        var s = Svc.Session;
        var t = s.AddObject(EffectRegistry.Text, 0, 0, 60)!;
        var options = new TemplateOptions { Name = "版のテスト", Exposed = new() { new ExposedParam { ObjectId = t.Id, Effect = 0, Key = "text", Label = "名前" } } };
        Svc.Templates.Save(TemplatePackage.Create(s.Project, new[] { t }, options));
        try
        {
            var entry = Svc.Templates.List().Single(e => e.Name == "版のテスト");
            EditorCommands.InsertTemplate(entry, 5, 100);
            var placed = s.Primary!;
            TemplateService.ForgetVersions();
            Pump();
            var settings = w.GetVisualDescendants().OfType<ObjectSettingsView>().Single();
            string notice = L.T("テンプレートに新しい版があります");
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == notice && b.IsEffectivelyVisible);

            // 新しい版を保存すると、知らせが出る
            s.Edit("色", () => s.Scene.Find(t.Id)!.Main.Values["color"] = "#00FF00");
            Svc.Templates.Save(TemplatePackage.Create(s.Project, new[] { s.Scene.Find(t.Id)! }, options));
            TemplateService.ForgetVersions();
            s.Select(placed.Id);
            s.NotifyChanged();
            Pump();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == notice && b.IsEffectivelyVisible);
            Shot(w, "template-update");

            var button = settings.GetVisualDescendants().OfType<IconButton>().First(b => b.Text == "更新" && b.IsEffectivelyVisible);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            var scene = s.Project.FindScene(s.Scene.Find(placed.Id)!.Main.GetString("scene"))!;
            Assert.Equal(2, scene.Template!.Version);
            Assert.Equal("#00FF00", scene.Objects.Single().Main.GetString("color"));
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == notice && b.IsEffectivelyVisible);
        }
        finally { foreach (var e in Svc.Templates.List().Where(e => e.Name == "版のテスト")) Svc.Templates.Delete(e); }
    }

    [AvaloniaFact]
    public void Style_is_saved_and_applied_to_the_selection()
    {
        Open();
        var s = Svc.Session;
        var a = s.AddObject(EffectRegistry.Text, 0, 0, 30)!;
        a.Main.Values["color"] = "#123456";
        Svc.Styles.Save(EffectStyle.From(a.Main, "UI テスト"));
        try
        {
            var b = s.AddObject(EffectRegistry.Text, 1, 0, 30)!;
            EditorCommands.ApplyStyle(EffectRegistry.Text, "UI テスト");
            Assert.Equal("#123456", s.Scene.Find(b.Id)!.Main.GetString("color"));
        }
        finally { Svc.Styles.Delete(EffectRegistry.Text, "UI テスト"); }
    }

    [AvaloniaFact]
    public void English_translation_covers_fixed_and_composed_strings()
    {
        var w = Open();
        bool wasEnglish = L.English;
        try
        {
            L.Initialize(Language.English);
            Assert.Equal("Undo", L.T("元に戻す"));
            Assert.Equal("Undo: Move object", L.T("元に戻す: オブジェクトの移動"));       // 型紙の中身も訳す
            Assert.Equal("Shape [Circle]", L.T("図形 [円]"));
            Assert.Equal("Could not open a.vbproj.\nboom", L.T("a.vbproj を開けませんでした。\nboom"));
            Assert.Equal("ユーザーが付けた名前", L.T("ユーザーが付けた名前"));             // 対応がなければそのまま
            Assert.Equal("clip.mp4", L.T("clip.mp4"));

            // 画面に出る文字は、入った時点で自動的に英語になる
            var text = new TextBlock { Text = "環境設定" };
            Assert.Equal("Preferences", text.Text);
            Svc.Session.AddObject(EffectRegistry.Shape, 0, 0, 30);
            Pump();
            Assert.Contains("video-builder", w.Title);
            Assert.DoesNotContain("無題", w.Title);
        }
        finally { L.Initialize(wasEnglish ? Language.English : Language.Japanese); }
    }

    /// <summary>再生中に画面側 (タイムライン・設定ダイアログ) の更新にかかる時間を測って表示する。</summary>
    [AvaloniaFact]
    public void Report_ui_cost_per_frame_during_playback()
    {
        var w = Open();
        var s = Svc.Session;
        s.Edit("bench", () =>
        {
            for (int i = 0; i < 40; i++)
            {
                var o = ObjectFactory.Create(i % 2 == 0 ? EffectRegistry.Text : EffectRegistry.Shape, i % 14, (i / 14) * 130 + i % 5 * 3, 110);
                if (i % 2 == 0) o.Main.Values["text"] = $"テロップ {i}";
                o.AddKeyFrame(o.Start + 40);
                s.Scene.Objects.Add(o);
            }
        });
        // 動きのあるパラメータを持つオブジェクトを選択しておく (設定ダイアログの値が毎フレーム変わる)
        var target = s.Scene.Objects[0];
        var x = target.Draw!.Tracks["x"]; x.Mode = MovementMode.Linear; x.Values[0] = 0; x.Values[1] = 500;
        ObjectFactory.AddFilter(target, "filter.border"); ObjectFactory.AddFilter(target, "filter.shadow");
        s.Select(target.Id);
        Pump();
        w.CaptureRenderedFrame()?.Dispose();

        const int Frames = 120;
        var canvas = w.Timeline.Canvas;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 1; f <= Frames; f++) { s.CurrentFrame = f % 100; Pump(); }
        double update = sw.Elapsed.TotalMilliseconds / Frames;
        int rendersBefore = canvas.RenderCount;
        sw.Restart();
        for (int f = 1; f <= Frames; f++) { s.CurrentFrame = f % 100; Pump(); w.CaptureRenderedFrame()?.Dispose(); }
        double total = sw.Elapsed.TotalMilliseconds / Frames;
        int rendersWhileSeeking = canvas.RenderCount - rendersBefore;
        // 現在位置が動くだけなら、タイムライン本体は描き直さない (赤い線だけが動く)
        Assert.Equal(0, rendersWhileSeeking);
        int count0 = canvas.RenderCount; double ms0 = canvas.RenderMsTotal;
        for (int f = 0; f < 50; f++) { canvas.InvalidateVisual(); w.CaptureRenderedFrame()?.Dispose(); }
        Console.WriteLine($"BENCH ui timeline: redraws while seeking {Frames} frames = {rendersWhileSeeking}, one full redraw (building draw commands) = {(canvas.RenderMsTotal - ms0) / Math.Max(1, canvas.RenderCount - count0):0.00} ms");
        sw.Restart();
        for (int f = 1; f <= Frames; f++) { w.Timeline.Canvas.InvalidateVisual(); w.CaptureRenderedFrame()?.Dispose(); }
        double timelineOnly = sw.Elapsed.TotalMilliseconds / Frames;
        sw.Restart();
        for (int f = 1; f <= Frames; f++) w.CaptureRenderedFrame()?.Dispose();
        double idle = sw.Elapsed.TotalMilliseconds / Frames;
        var preview = w.GetVisualDescendants().OfType<PreviewView>().Single();
        sw.Restart();
        for (int f = 1; f <= Frames; f++) { preview.InvalidateVisual(); w.CaptureRenderedFrame()?.Dispose(); }
        double previewOnly = sw.Elapsed.TotalMilliseconds / Frames;
        var settings = w.GetVisualDescendants().OfType<ObjectSettingsView>().Single();
        sw.Restart();
        for (int f = 1; f <= Frames; f++) { settings.InvalidateVisual(); foreach (var c in settings.GetVisualDescendants().OfType<Control>().Take(400)) c.InvalidateVisual(); w.CaptureRenderedFrame()?.Dispose(); }
        double settingsOnly = sw.Elapsed.TotalMilliseconds / Frames;
        Console.WriteLine($"BENCH ui parts: preview redraw={previewOnly:0.00} ms, settings redraw={settingsOnly:0.00} ms");
        Console.WriteLine($"BENCH ui per frame: update={update:0.00} ms, update+draw={total:0.00} ms, timeline redraw={timelineOnly:0.00} ms, capture only={idle:0.00} ms");
        Assert.True(total > 0);
    }

    [AvaloniaFact]
    public async Task Autosave_writes_only_when_something_changed_and_restores_the_same_content()
    {
        Open();
        var s = Svc.Session;
        Svc.Settings.AutoSave = true;
        var dir = AppPaths.AutoSaveDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        await EditorCommands.AutoSaveNow();
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);     // 変更がなければ書かない

        var o = s.AddObject(EffectRegistry.Text, 2, 30, 60)!;
        o.Main.Values["text"] = "自動保存";
        s.NotifyChanged();
        await EditorCommands.AutoSaveNow();
        var files = Directory.GetFiles(dir);
        Assert.Single(files);
        var saved = ProjectSerializer.Load(files[0]);
        Assert.Equal("自動保存", saved.Root.Objects.Single().Main.GetString("text"));
        Assert.Empty(s.Project.MediaPaths);                 // 編集中のプロジェクト側は書き換えない

        await EditorCommands.AutoSaveNow();                 // 変更なし → 増えない
        Assert.Single(Directory.GetFiles(dir));
    }

    [AvaloniaFact]
    public async Task Importing_a_heavy_video_creates_a_proxy_in_the_background()
    {
        var w = Open();
        var s = Svc.Session;
        if (!VideoBuilder.Media.FFmpegLoader.EnsureLoaded()) return;
        var dir = Path.Combine(Path.GetTempPath(), "vb-ui-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var clip = Path.Combine(dir, "big.mp4");
            using (var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                $"-y -loglevel error -f lavfi -i testsrc2=size=2560x1440:rate=10:duration=1 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{clip}\"")))
            {
                proc!.WaitForExit(60000);
                if (proc.ExitCode != 0) return;
            }
            Svc.Settings.AutoProxy = true;
            Assert.Null(MediaImport.AddFile(clip, 0, 0));       // 2560x1440 は「重い動画」→ 自動でプロキシを作る
            string? statusSeen = null;
            for (int i = 0; i < 500 && (!VideoBuilder.Media.ProxyStore.Exists(clip) || Svc.Proxies.Pending > 0); i++)
            {
                await Task.Delay(20);
                Pump();
                if (Svc.Proxies.Status.Length > 0) statusSeen = Svc.Proxies.Status;
            }
            Assert.True(VideoBuilder.Media.ProxyStore.Exists(clip), Svc.Proxies.LastError ?? "プロキシができない");
            Assert.Contains("big.mp4", statusSeen ?? "big.mp4");
            Assert.Equal(0, Svc.Proxies.Pending);
            var info = VideoBuilder.Media.MediaInfo.Probe(VideoBuilder.Media.ProxyStore.FindSegment(clip, 0)!)!;
            Assert.Equal(1280, info.Width);
            VideoBuilder.Media.ProxyStore.Delete(clip);
            Assert.False(VideoBuilder.Media.ProxyStore.Exists(clip));
        }
        finally
        {
            s.NewProject(new ProjectSettings());
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 終了の不具合の再現: 分離表示のとき、メインウィンドウを閉じようとすると、先に子ウィンドウ (タイムライン・設定) に
    /// 「閉じてよいか」が問われる。子ウィンドウが「隠すだけにして、閉じるのは取りやめる」と答えていたため、
    /// メインウィンドウの終了処理 (保存の確認) まで届かず、子ウィンドウが消えるだけで終了できなかった。
    /// </summary>
    [AvaloniaFact]
    public void Closing_the_owner_reaches_its_confirmation_and_tool_windows_close_with_it()
    {
        var owner = new Window { Width = 300, Height = 200 };
        int asked = 0;
        bool allow = false;
        owner.Closing += (_, e) => { asked++; if (!allow) e.Cancel = true; };   // 保存の確認で「キャンセル」した場合
        owner.Show();
        var tool = new ToolWindow("tool", 200, 100);
        bool toolClosed = false;
        tool.Closed += (_, _) => toolClosed = true;
        tool.Show(owner);
        Pump();

        owner.Close();
        Pump();
        Assert.Equal(1, asked);                 // メインウィンドウの終了処理 (確認) まで届く
        Assert.True(tool.IsVisible);            // 確認でやめたら、子ウィンドウもそのまま残る
        Assert.True(owner.IsVisible);

        allow = true;                           // 確認で「保存しない」を選んだ場合
        owner.Close();
        Pump();
        Assert.Equal(2, asked);
        Assert.False(owner.IsVisible);
        Assert.True(toolClosed);                // 子ウィンドウも一緒に閉じる

        // 子ウィンドウだけを閉じたときは、従来どおり隠すだけ
        var owner2 = new Window();
        owner2.Show();
        var tool2 = new ToolWindow("tool2", 200, 100);
        bool tool2Closed = false;
        tool2.Closed += (_, _) => tool2Closed = true;
        tool2.Show(owner2);
        tool2.Close();
        Pump();
        Assert.False(tool2.IsVisible);
        Assert.False(tool2Closed);
        owner2.Close();
    }

    [AvaloniaFact]
    public void Dragging_on_empty_space_selects_a_range_and_clicking_it_seeks()
    {
        var w = Open();
        var s = Svc.Session;
        var a = s.AddObject(EffectRegistry.Shape, 0, 10, 20)!;
        var b = s.AddObject(EffectRegistry.Shape, 1, 40, 20)!;
        var far = s.AddObject(EffectRegistry.Shape, 3, 200, 20)!;
        s.Select(null);
        Pump();
        // 何もない所 (レイヤー 3・フレーム 5) から、レイヤー 2・フレーム 70 までドラッグ → a と b が選ばれる
        var from = CanvasPoint(w, 5, 2);
        var to = CanvasPoint(w, 70, 0);
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point(from.X + 10, from.Y - 5));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
        Pump();
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), s.SelectionIds.OrderBy(x => x));
        Assert.DoesNotContain(far.Id, s.SelectionIds);

        // クリックだけなら、選択を外して現在位置を移す
        var click = CanvasPoint(w, 120, 2);
        w.MouseDown(click, MouseButton.Left); w.MouseUp(click, MouseButton.Left);
        Assert.Empty(s.SelectionIds);
        Assert.Equal(120, s.CurrentFrame);
    }

    [AvaloniaFact]
    public void Report_selection_change_cost()
    {
        var w = Open();
        var s = Svc.Session;
        var ids = new List<string>();
        s.Edit("bench", () =>
        {
            for (int i = 0; i < 20; i++)
            {
                var o = ObjectFactory.Create(i % 2 == 0 ? EffectRegistry.Text : EffectRegistry.Video, i, 0, 100);
                ObjectFactory.AddFilter(o, "filter.border"); ObjectFactory.AddFilter(o, "filter.color");
                s.Scene.Objects.Add(o); ids.Add(o.Id);
            }
        });
        Pump(); w.CaptureRenderedFrame()?.Dispose();
        foreach (var id in ids.Take(4)) { s.Select(id); Pump(); w.CaptureRenderedFrame()?.Dispose(); }   // 1 回目の組み立て・JIT を済ませる
        double selectMs = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 40; i++) { var t0 = sw.Elapsed.TotalMilliseconds; s.Select(ids[i % ids.Count]); selectMs += sw.Elapsed.TotalMilliseconds - t0; Pump(); }
        double update = sw.Elapsed.TotalMilliseconds / 40;
        Console.WriteLine($"BENCH detail: select-event {selectMs / 40:0.0} ms");
        sw.Restart();
        for (int i = 0; i < 40; i++) { s.Select(ids[(i + 1) % ids.Count]); Pump(); w.CaptureRenderedFrame()?.Dispose(); }
        double withDraw = sw.Elapsed.TotalMilliseconds / 40;
        Console.WriteLine($"BENCH selection change: build {update:0.0} ms, build+layout+draw {withDraw:0.0} ms");
    }

    // ---------------- シンプル編集 ----------------

    /// <summary>シンプル編集に切り替えたウィンドウ。</summary>
    private static MainWindow OpenSimple()
    {
        var w = Open();
        EditorCommands.SetEditorMode(EditorMode.Simple);
        Pump();
        return w;
    }

    private static StoryClip ColorTitle(string background, double seconds = 2) =>
        new() { Kind = StoryClipKind.Title, Text = "T", Background = background, Duration = seconds };

    [AvaloniaFact]
    public void Editor_mode_switches_the_workspace_and_keeps_the_shared_preview()
    {
        var w = Open();
        Assert.IsType<AdvancedWorkspace>(w.Workspace);
        Assert.Contains(w.Timeline, w.GetVisualDescendants());
        EditorCommands.SetEditorMode(EditorMode.Simple);
        Pump();
        Assert.IsType<SimpleWorkspace>(w.Workspace);
        Assert.Equal(EditorMode.Simple, Svc.Settings.EditorMode);
        Assert.DoesNotContain(w.Timeline, w.GetVisualDescendants());
        Assert.Contains(w.Simple.Strip, w.GetVisualDescendants());
        Assert.Contains(w.PreviewArea, w.GetVisualDescendants());
        // 詳細編集だけのメニューはない
        List<string?> FileMenu() => w.GetVisualDescendants().OfType<Menu>().First().Items.OfType<MenuItem>().First()
            .Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.DoesNotContain("字幕を読み込む...", FileMenu());
        Assert.Contains("メディアファイルを読み込む...", FileMenu());
        EditorCommands.SetEditorMode(EditorMode.Advanced);
        Pump();
        Assert.Contains(w.Timeline, w.GetVisualDescendants());
        Assert.Contains(w.PreviewArea, w.GetVisualDescendants());
        Assert.Contains("字幕を読み込む...", FileMenu());
    }

    [AvaloniaFact]
    public void Simple_mode_shortcuts_edit_the_storyboard_and_undo_restores_it()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { ColorTitle("#FF0000"), ColorTitle("#00FF00"), ColorTitle("#0000FF") });
        Pump();
        Assert.Equal(3, ed.Board!.Clips.Count);
        Assert.Equal(6, Svc.Session.Project.Root.Objects.Count);   // 背景 + 文字 が 3 つ

        ed.Select(ed.Board.Clips[1].Id);
        w.Simple.Strip.Focus();
        w.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Pump();
        Assert.Equal(new[] { "#FF0000", "#0000FF" }, ed.Board!.Clips.Select(c => c.Background));
        Assert.True(ed.CanEdit);

        // S: 現在位置 (1 つ目の真ん中) で分割
        Svc.Session.CurrentFrame = 30;
        w.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
        Pump();
        Assert.Equal(3, ed.Board!.Clips.Count);
        Assert.Equal(1, ed.Board.Clips[0].Duration);

        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Pump();
        Assert.Equal(new[] { "#FF0000", "#00FF00", "#0000FF" }, ed.Board!.Clips.Select(c => c.Background));
        // 詳細編集だけのショートカット (P: キーフレーム) は効かない
        w.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.None);
        Assert.True(ed.CanEdit);
    }

    [AvaloniaFact]
    public void Clicking_the_storyboard_selects_and_seeks_and_dragging_reorders()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { ColorTitle("#FF0000"), ColorTitle("#00FF00"), ColorTitle("#0000FF") });
        ed.Select(null);
        Pump();
        var strip = w.Simple.Strip;
        var layout = ed.Layout;
        Point At(int frame) => strip.TranslatePoint(new Point(strip.FrameToX(frame), StoryboardStrip.ClipTop + 40), w)!.Value;

        var p = At(layout.Clips[1].Start + 10);
        w.MouseDown(p, MouseButton.Left); w.MouseUp(p, MouseButton.Left);
        Pump();
        Assert.Equal(ed.Board!.Clips[1].Id, ed.SelectedId);
        Assert.Equal(layout.Clips[1].Start + 10, Svc.Session.CurrentFrame);
        // 設定の欄に、選んだクリップの設定が出る
        Assert.Contains(w.Simple.Panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == L.T("タイトル"));

        // 1 つ目を、最後へドラッグ
        string first = ed.Board.Clips[0].Id;
        var from = At(10);
        var to = At(layout.VideoLength - 2);
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point(from.X + 10, from.Y));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
        Pump();
        Assert.Equal(first, ed.Board!.Clips[^1].Id);
    }

    [AvaloniaFact]
    public void Speed_is_chosen_in_the_clip_panel_for_videos_only()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { new StoryClip { Kind = StoryClipKind.Video, File = "/v.mp4", SourceSeconds = 10 }, ColorTitle("#FF0000") });
        ed.Select(ed.Board!.Clips[0].Id);
        Pump();
        var combos = w.Simple.Panel.GetVisualDescendants().OfType<ComboBox>().ToList();
        var speed = combos.Single(c => c.ItemsSource is string[] items && items.Contains("2x"));
        speed.SelectedIndex = Array.IndexOf((string[])speed.ItemsSource!, "2x");
        Pump();
        Assert.Equal(2, ed.Board!.Clips[0].Speed);
        Assert.Equal(150, ed.Layout.Clips[0].Length);
        Svc.Session.Undo();
        Assert.Equal(1, ed.Board!.Clips[0].Speed);

        // タイトルには速度の欄がない
        ed.Select(ed.Board.Clips[1].Id);
        Pump();
        Assert.DoesNotContain(w.Simple.Panel.GetVisualDescendants().OfType<ComboBox>(), c => c.ItemsSource is string[] items && items.Contains("2x"));
    }

    [AvaloniaFact]
    public void Project_changed_in_advanced_mode_shows_a_notice_in_simple_mode()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { ColorTitle("#FF0000") });
        EditorCommands.SetEditorMode(EditorMode.Advanced);
        var s = Svc.Session;
        s.Edit("move", () => s.Project.Root.Objects[0].Layer = 20);
        EditorCommands.SetEditorMode(EditorMode.Simple);
        Pump();
        Assert.False(w.Simple.CanEdit);
        Assert.False(w.Simple.Strip.IsEffectivelyVisible);
        Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == L.T("このプロジェクトは、詳細編集で変更されています。"));

        var rebuild = w.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) is "シンプル編集の内容に戻す" or "Restore the simple editing content");
        rebuild.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.True(w.Simple.CanEdit);
        Assert.True(w.Simple.Strip.IsEffectivelyVisible);
        Assert.Equal(0, s.Project.Root.Objects[0].Layer);
    }

    [AvaloniaFact]
    public async Task Simple_mode_preview_renders_the_storyboard()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        Svc.Session.NewProject(new ProjectSettings { Width = 320, Height = 180 });
        ed.AddClips(new[] { ColorTitle("#FF0000") });
        Svc.Session.CurrentFrame = 5;
        Svc.Preview.Request();
        SkiaSharp.SKColor pixel = default;
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(20);
            Pump();
            Svc.Preview.WithFrame(b => pixel = b.GetPixel(b.Width / 2, b.Height / 4));
            if (pixel.Red > 200) break;
        }
        Assert.True(pixel.Red > 200 && pixel.Green < 50, $"pixel {pixel}");
    }

    // ---------------- パフォーマンスチェック ----------------

    [AvaloniaFact]
    public async Task Performance_check_shows_preview_numbers_in_both_editing_modes()
    {
        var w = Open();
        var perf = Svc.Performance;
        try
        {
            Svc.Session.AddObject(EffectRegistry.Shape, 0, 0, 60);
            Assert.False(w.PreviewArea.Overlay.IsVisible);
            // ショートカットでオンにすると、プレビューの上の表示と詳細の窓が出る
            w.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            Pump();
            Assert.True(perf.Enabled);
            Assert.True(Svc.Settings.PerformanceMode);
            Assert.True(w.PreviewArea.Overlay.IsEffectivelyVisible);
            Assert.NotNull(EditorCommands.PerformanceWindow);

            async Task WaitForRenders(int count)
            {
                for (int i = 0; i < 200 && perf.RenderMs.Summarize().Count < count; i++)
                {
                    Svc.Session.CurrentFrame = (Svc.Session.CurrentFrame + 1) % 60;
                    await Task.Delay(10);
                    Pump();
                }
            }
            await WaitForRenders(3);
            Assert.True(perf.RenderMs.Summarize().Count >= 3);
            Assert.True(perf.RenderSize.Width > 0);
            Assert.Contains("fps", perf.Report());

            // シンプル編集でも同じ表示が出る (プレビューは共通)
            Svc.Session.NewProject(new ProjectSettings());
            EditorCommands.SetEditorMode(EditorMode.Simple);
            Svc.Storyboard.AddClips(new[] { ColorTitle("#FF0000") });
            Pump();
            Assert.True(w.PreviewArea.Overlay.IsEffectivelyVisible);
            perf.Reset();
            await WaitForRenders(2);
            Assert.True(perf.RenderMs.Summarize().Count >= 2);

            // 出力の内訳も記録される (出力ダイアログから呼ばれるのと同じ)
            var stats = perf.BeginExport();
            perf.EndExport(stats, "MP4", "/tmp/x.mp4", 2, null);
            Assert.Single(perf.Exports);
            Pump();
        }
        finally
        {
            perf.Enabled = false;
            perf.Exports.Clear();
            Pump();
        }
        Assert.False(w.PreviewArea.Overlay.IsVisible);
        Assert.Null(EditorCommands.PerformanceWindow);
    }

    [AvaloniaFact]
    public void Shared_preview_texture_is_used_only_on_the_same_gpu_and_falls_back_without_gl()
    {
        Assert.True(PreviewService.SameGpu("NVIDIA GeForce RTX 3060/PCIe/SSE2", "NVIDIA GeForce RTX 3060"));
        Assert.True(PreviewService.SameGpu("Mesa Intel(R) UHD Graphics 620 (KBL GT2)", "Mesa Intel(R) UHD Graphics 620 (KBL GT2)"));
        Assert.False(PreviewService.SameGpu("Mesa Intel(R) UHD Graphics 620 (KBL GT2)", "NVIDIA GeForce RTX 3060/PCIe/SSE2"));
        Assert.False(PreviewService.SameGpu("", "NVIDIA"));
        // ヘッドレス (GPU で合成しない画面) では共有できないので、これまでどおり読み戻して描く
        Open();
        Pump();
        Assert.Null(Svc.Preview.SharedTarget);
        Assert.False(Svc.Preview.PresentsToShared);
    }


    [Fact]
    public void Waveform_is_reported_part_by_part_while_it_is_computed()
    {
        if (!VideoBuilder.Media.FFmpegLoader.EnsureLoaded()) return;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-wave-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // 前半 5 秒は無音、後半 5 秒は音がある
            var clip = Path.Combine(dir, "half.m4a");
            using (var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                $"-y -loglevel error -f lavfi -i \"aevalsrc='if(gte(t,5),sin(440*2*PI*t),0)':d=10\" -c:a aac \"{clip}\"")))
            {
                proc!.WaitForExit(30000);
                if (proc.ExitCode != 0) return;
            }
            var parts = new List<WaveformCache.Peaks>();
            var final = WaveformCache.LoadOrCompute(clip, p => parts.Add(p with { Values = p.Values.ToArray() }))!;
            Assert.True(final.IsComplete);
            Assert.InRange(final.Values.Length, 495, 505);
            Assert.True(parts.Count >= 5, $"{parts.Count} 回");
            for (int k = 1; k < parts.Count; k++) Assert.True(parts[k].Ready > parts[k - 1].Ready);
            foreach (var part in parts)
            {
                Assert.False(part.IsComplete);
                // 見せる範囲 (Ready より前) の値は、できあがりと同じ
                Assert.Equal(final.Values.Take(part.Ready), part.Values.Take(part.Ready));
            }
            Assert.All(final.Values.Take(240), v => Assert.True(v < 5));      // 無音
            Assert.Contains(final.Values.Skip(260), v => v > 100);            // 音がある
            // 2 回目は保存したものを読むので、途中経過はない
            var again = new List<WaveformCache.Peaks>();
            Assert.Equal(final.Values, WaveformCache.LoadOrCompute(clip, again.Add)!.Values);
            Assert.Empty(again);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

}
