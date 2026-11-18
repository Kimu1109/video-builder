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
using VideoBuilder.App.Views;
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
            for (int i = 0; i < 300 && (peaks == null || thumb == null); i++)
            {
                await Task.Delay(20);
                Pump();
                peaks ??= Svc.Waveforms.Get(clip);
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
            for (int i = 0; i < 500 && !VideoBuilder.Media.ProxyStore.Exists(clip); i++)
            {
                await Task.Delay(20);
                Pump();
                if (Svc.Proxies.Status.Length > 0) statusSeen = Svc.Proxies.Status;
            }
            Assert.True(VideoBuilder.Media.ProxyStore.Exists(clip), Svc.Proxies.LastError ?? "プロキシができない");
            Assert.Contains("big.mp4", statusSeen ?? "big.mp4");
            Assert.Equal(0, Svc.Proxies.Pending);
            var info = VideoBuilder.Media.MediaInfo.Probe(VideoBuilder.Media.ProxyStore.FindReady(clip)!)!;
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
}

