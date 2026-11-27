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
using VideoBuilder.Media;
using VideoBuilder.Rendering;

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

        // 今表示している中身から探す。前のテストで表示した種類の中身も、隠れたまま残っている
        // (Avalonia 12 は見えていない入力欄にはフォーカスを移さないので、そちらを選ぶと入力できない)
        var body = w.GetVisualDescendants().OfType<ObjectSettingsView>().Select(v => v.CurrentBody).Single(b => b != null && b.IsEffectivelyVisible)!;
        var boxes = body.GetVisualDescendants().OfType<NumberBox>().ToList();
        Assert.True(boxes.Count >= 12, $"NumberBox が {boxes.Count} 個しかない");
        // 標準描画の X の左側の入力欄を探す: 図形のトラックバー (隠れているものも含む) の行 × 2 個のあと
        int shapeTracks = EffectRegistry.Get(EffectRegistry.Shape).Params.Count(p => p.Kind == ParamKind.Track);
        var xLeft = boxes[shapeTracks * 2];
        xLeft.Focus();
        xLeft.Text = "100+23";
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Pump();
        var x = s.Scene.Find(o.Id)!.Draw!.Tracks["x"];
        Assert.Equal(new[] { 0.0, 123.0, 0.0 }, x.Values);
        Assert.False(boxes[shapeTracks * 2 + 1].IsEnabled);    // 移動方法なしのとき、右側は使えない

        s.Undo();
        Pump();
        Assert.Equal(0, s.Scene.Find(o.Id)!.Draw!.Tracks["x"].Values[1]);
        Assert.Equal("0.0", xLeft.Text);
    }

    [AvaloniaFact]
    public void Shape_settings_show_only_the_params_of_the_selected_type()
    {
        var w = Open();
        var s = Svc.Session;
        var o = s.AddObject(EffectRegistry.Shape, 0, 0, 100)!;
        Pump();
        bool Shown(string caption) => w.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == caption || t.Text == L.T(caption))   // 英語表示のときは訳した名前
            .Any(t => t.GetVisualAncestors().OfType<Control>().Prepend(t).TakeWhile(c => c is not ObjectSettingsView).All(c => c.IsVisible));
        Assert.True(Shown("角の丸み"));
        Assert.False(Shown("矢じりの長さ (%)"));

        s.Edit("test", () => s.Scene.Find(o.Id)!.Main.Values["type"] = ((int)ShapeType.Arrow).ToString());
        Pump();
        Assert.True(Shown("矢じりの長さ (%)"));
        Assert.False(Shown("中心角"));
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
    public async Task Preview_frame_is_sent_to_the_screen_gpu_only_when_it_changes()
    {
        Open();
        var s = Svc.Session;
        async Task WaitCenter(uint color)
        {
            uint center = 0;
            for (int i = 0; i < 150 && center != color; i++)
            {
                await Task.Delay(20);
                Pump();
                Svc.Preview.WithFrame(b => center = (uint)b.GetPixel(b.Width / 2, b.Height / 2));
            }
            Assert.Equal(color, center);
        }
        s.AddObject(EffectRegistry.Shape, 90, 0, 60, x => { x.Main.Values["color"] = "#FF0000"; x.Main.Tracks["size"] = new AnimatedValue(4000); });
        await WaitCenter(0xFFFF0000);

        // 画面の GPU の代わりに、ソフトウェアの GL で作った GPU のコンテキストに描く
        using var gpu = VideoBuilder.Rendering.GpuContext.TryCreate(out var gpuError);
        if (gpu == null)
        {
            Assert.False(Environment.GetEnvironmentVariable("VB_REQUIRE_GPU") == "1", "GPU を作れません: " + gpuError);
            return;
        }
        gpu.MakeCurrent();
        using var surface = SkiaSharp.SKSurface.Create(gpu.Context, false, new SkiaSharp.SKImageInfo(64, 64, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
        uint Draw(SkiaSharp.SKFilterQuality q)
        {
            surface.Canvas.Clear(SkiaSharp.SKColors.Black);
            Assert.True(Svc.Preview.DrawFrame(surface.Canvas, new SkiaSharp.SKRect(0, 0, 64, 64), q, gpu.Context));
            surface.Flush();
            using var bmp = new SkiaSharp.SKBitmap(64, 64);
            Assert.True(surface.ReadPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes, 0, 0));
            return (uint)bmp.GetPixel(32, 32);
        }
        long uploads = Svc.Preview.FrameUploads;
        Assert.Equal(0xFFFF0000u, Draw(SkiaSharp.SKFilterQuality.Low));
        Assert.Equal(uploads + 1, Svc.Preview.FrameUploads);
        // 同じフレームを描き直しても、送り直さない (ミップマップがなかったときに、ミップマップのある画質で描くときだけ送る)
        Assert.Equal(0xFFFF0000u, Draw(SkiaSharp.SKFilterQuality.Low));
        Assert.Equal(0xFFFF0000u, Draw(SkiaSharp.SKFilterQuality.Medium));
        Assert.Equal(0xFFFF0000u, Draw(SkiaSharp.SKFilterQuality.Medium));
        Assert.Equal(0xFFFF0000u, Draw(SkiaSharp.SKFilterQuality.Low));
        Assert.Equal(uploads + 2, Svc.Preview.FrameUploads);

        // フレームが変わったら送り直す
        s.AddObject(EffectRegistry.Shape, 91, 0, 60, x => { x.Main.Values["color"] = "#00FF00"; x.Main.Tracks["size"] = new AnimatedValue(4000); });
        await WaitCenter(0xFF00FF00);
        gpu.MakeCurrent();
        Assert.Equal(0xFF00FF00u, Draw(SkiaSharp.SKFilterQuality.Low));
        Assert.Equal(uploads + 3, Svc.Preview.FrameUploads);
    }

    [Fact]
    public void Playback_resolution_drops_when_frames_are_late_and_returns_when_they_are_fast()
    {
        const double budget = 1000.0 / 30;
        int slow = 0, fast = 0, div = 1;
        int Step(double ms) => div = PreviewService.NextPlaybackDivisor(div, ms, budget, ref slow, ref fast);
        // 1 回や 2 回遅れただけでは縮めない
        Step(50); Step(50); Step(10);
        Assert.Equal(1, div);
        // 続けて遅れたら 1/2、さらに遅れたら 1/4 (それより小さくはしない)
        for (int i = 0; i < PreviewService.SlowFramesToShrink; i++) Step(50);
        Assert.Equal(2, div);
        for (int i = 0; i < PreviewService.SlowFramesToShrink * 3; i++) Step(50);
        Assert.Equal(PreviewService.MaxPlaybackDivisor, div);
        // 間に合ってはいるが余裕がない (戻すと間に合わない) ときは、そのまま
        for (int i = 0; i < 100; i++) Step(budget * 0.5);
        Assert.Equal(4, div);
        // 十分に速く描けるのが続いたら 1 段ずつ戻す。途中で遅れたら数え直す
        for (int i = 0; i < PreviewService.FastFramesToRestore - 1; i++) Step(1);
        Step(budget * 0.5);
        for (int i = 0; i < PreviewService.FastFramesToRestore - 1; i++) Step(1);
        Assert.Equal(4, div);
        Step(1);
        Assert.Equal(2, div);
        for (int i = 0; i < PreviewService.FastFramesToRestore; i++) Step(1);
        Assert.Equal(1, div);
        for (int i = 0; i < 100; i++) Step(1);
        Assert.Equal(1, div);
    }

    [AvaloniaFact]
    public async Task Heavy_scene_is_played_at_a_lower_resolution_and_redrawn_in_full_when_stopped()
    {
        Open();
        var s = Svc.Session;
        var settings = s.Project.Settings;
        int ow = settings.Width, oh = settings.Height;
        var added = new List<string>();
        try
        {
            // CPU で 1 フレームに 30fps の間隔より長くかかる絵 (4K に大きなぼかし)
            settings.Width = 3840; settings.Height = 2160;
            added.Add(s.AddObject(EffectRegistry.Shape, 92, 0, 600, x => { x.Main.Values["color"] = "#FF8000"; x.Main.Tracks["size"] = new AnimatedValue(3000); })!.Id);
            added.Add(s.AddObject("filter.blur", 93, 0, 600, x => x.Main.Tracks["radius"] = new AnimatedValue(300))!.Id);
            added.Add(s.AddObject("filter.sketch", 94, 0, 600)!.Id);
            s.CurrentFrame = 0;
            Svc.Preview.Play();
            for (int i = 0; i < 400 && Svc.Preview.PlaybackDivisor == 1; i++) { await Task.Delay(20); Pump(); }
            Assert.True(Svc.Preview.PlaybackDivisor > 1, $"縮めていない (描画 {Svc.Preview.LastRenderMs:0} ms)");
            // 縮めた大きさで描いている
            var small = Svc.Preview.TargetSize(3840, 2160);
            for (int i = 0; i < 400 && Svc.Preview.LastRenderSize != small; i++) { await Task.Delay(20); Pump(); small = Svc.Preview.TargetSize(3840, 2160); }
            Assert.Equal(small, Svc.Preview.LastRenderSize);
            Svc.Preview.Stop();
            Assert.Equal(1, Svc.Preview.PlaybackDivisor);
            // 止めたら元の大きさで描き直す
            var expected = Svc.Preview.TargetSize(3840, 2160);
            for (int i = 0; i < 400 && Svc.Preview.LastRenderSize != expected; i++) { await Task.Delay(20); Pump(); }
            Assert.Equal(expected, Svc.Preview.LastRenderSize);
            Assert.True(expected.Width > small.Width);
        }
        finally
        {
            Svc.Preview.Stop();
            // ほかのテストが重くならないよう、置いたものを消して大きさを戻す
            s.SelectMany(added);
            s.DeleteSelected();
            settings.Width = ow; settings.Height = oh;
            s.NotifyChanged();
        }
    }

    [AvaloniaFact]
    public async Task Playback_is_driven_by_the_screen_updates_of_the_preview_window()
    {
        var w = Open();
        var s = Svc.Session;
        // プレビューを表示しているウィンドウが、再生の時計になる
        Assert.Same(TopLevel.GetTopLevel(w.PreviewArea.Preview), Svc.Preview.FrameClock);
        s.AddObject(EffectRegistry.Shape, 0, 0, 12);
        long ticks = Svc.Preview.AnimationFrameTicks;
        Svc.Preview.Play();
        for (int i = 0; i < 150 && Svc.Preview.IsPlaying; i++)
        {
            await Task.Delay(10);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Pump();
        }
        Assert.False(Svc.Preview.IsPlaying);
        Assert.Equal(11, s.CurrentFrame);
        Assert.True(Svc.Preview.AnimationFrameTicks > ticks, "画面の更新で進んでいない");
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
    public void Easing_dialog_switches_the_kind_and_drags_the_bezier_handles()
    {
        var w = Open();
        var d = new EasingDialog("X の動き方", new Easing { Kind = EasingKind.Back }, "標準に戻す");
        d.Show(w);
        Pump();
        var kind = d.GetVisualDescendants().OfType<ComboBox>().First();
        kind.SelectedIndex = (int)EasingKind.Bezier;
        Pump();
        d.CaptureRenderedFrame()?.Dispose();   // 描画して、位置を確定させる
        // グラフの 1 つ目の制御点 (既定の ease: 0.25, 0.1) をつかんで、右上へ動かす
        var graph = d.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "EasingGraph");
        double pw = graph.Bounds.Width - 40, ph = graph.Bounds.Height - 16;
        var handle = graph.TranslatePoint(new Point(28 + 0.25 * pw, 8 + ph - (0.1 + 0.5) / 2 * ph), d)!.Value;
        d.MouseDown(handle, MouseButton.Left);
        d.MouseMove(new Point(handle.X + 30, handle.Y - 70));
        d.MouseUp(new Point(handle.X + 30, handle.Y - 70), MouseButton.Left);
        Pump();
        Shot(d, "easing-dialog");
        var ok = d.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "OK");
        ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.NotNull(d.Result);
        Assert.Equal(EasingKind.Bezier, d.Result!.Kind);
        Assert.True(d.Result.X1 > 0.3 && d.Result.Y1 > 0.3, $"制御点が動いていない: {d.Result.X1}, {d.Result.Y1}");
    }

    [AvaloniaFact]
    public async Task Motion_path_is_drawn_for_the_selected_object_and_its_points_can_be_dragged()
    {
        var w = Open();
        var s = Svc.Session;
        var o = s.AddObject(EffectRegistry.Shape, 0, 0, 60, x =>
        {
            x.Main.Values["color"] = "#FF0000";
            x.Main.Tracks["size"] = new AnimatedValue(80);
            var tx = x.Draw!.Tracks["x"]; tx.Mode = MovementMode.Curve; tx.Values[0] = -600; tx.Values[1] = 600;
            var ty = x.Draw.Tracks["y"]; ty.Mode = MovementMode.Curve; ty.Values[0] = 200; ty.Values[1] = 200;
            x.AddKeyFrame(30);
            ty.Values[1] = -250;   // 真ん中で上へ山なりに
        })!;
        s.Select(o.Id);
        s.CurrentFrame = 15;
        for (int i = 0; i < 100 && !Svc.Preview.HasFrame; i++) { await Task.Delay(20); Pump(); }
        await Task.Delay(100); Pump();
        var preview = w.GetVisualDescendants().OfType<PreviewView>().Single();
        preview.InvalidateVisual(); preview.Selection.InvalidateVisual();
        w.CaptureRenderedFrame()?.Dispose();
        Shot(w, "motion-path");

        // 真ん中の中間点 (X はほぼ 0、Y は -250) をつかんで、右へ 40px 動かす
        double before = s.Scene.Find(o.Id)!.Draw!.Tracks["x"].Values[1];
        var settings = s.Project.Settings;
        double scale = Math.Min((preview.Bounds.Width - 16) / settings.Width, (preview.Bounds.Height - 16) / settings.Height);
        double dw = Math.Round(settings.Width * scale), dh = Math.Round(settings.Height * scale);
        double left = Math.Round((preview.Bounds.Width - dw) / 2), top = Math.Round((preview.Bounds.Height - dh) / 2);
        var mid = preview.TranslatePoint(new Point(left + (settings.Width / 2.0 + before) * scale, top + (settings.Height / 2.0 - 250) * scale), w)!.Value;
        w.MouseDown(mid, MouseButton.Left);
        w.MouseMove(new Point(mid.X + 5, mid.Y));
        w.MouseMove(new Point(mid.X + 40, mid.Y));
        w.MouseUp(new Point(mid.X + 40, mid.Y), MouseButton.Left);
        Pump();
        var x = s.Scene.Find(o.Id)!.Draw!.Tracks["x"];
        Assert.True(x.Values[1] > before + 20, $"中間点の X が動いていない: {x.Values[1]}");
        Assert.Equal(-600, x.Values[0]);                                     // ほかの点はそのまま
        Assert.Equal(-250, s.Scene.Find(o.Id)!.Draw!.Tracks["y"].Values[1]); // 横に動かしただけ
        s.Undo();
        Assert.Equal(before, s.Scene.Find(o.Id)!.Draw!.Tracks["x"].Values[1], 1);
    }


    [AvaloniaFact]
    public void Preferences_are_split_into_categories_that_fit_the_window()
    {
        var w = Open();
        var d = new PreferencesDialog();
        d.Show(w);
        try
        {
            Pump();
            var list = d.GetVisualDescendants().OfType<ListBox>().First();
            Assert.Equal(6, list.ItemCount);
            Assert.Equal(520, d.Height);        // 項目の数で縦に伸びない
            for (int i = 0; i < list.ItemCount; i++)
            {
                list.SelectedIndex = i;
                Pump();
                d.CaptureRenderedFrame()?.Dispose();    // 切り替えた項目を組み立てる
                Shot(d, "preferences-" + i);
                var title = (string)((IList<string>)list.ItemsSource!)[i];
                Assert.Contains(d.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == L.T(title) && b.FontSize == 16);
            }
            // 開き直すと、最後に開いた分類から
            d.Close();
            d = new PreferencesDialog();
            d.Show(w);
            Pump();
            Assert.Equal(5, d.GetVisualDescendants().OfType<ListBox>().First().SelectedIndex);
            d.GetVisualDescendants().OfType<ListBox>().First().SelectedIndex = 0;
        }
        finally { d.Close(); }
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
    public void Expanded_template_objects_show_the_template_items_in_the_properties()
    {
        var w = Open();
        var s = Svc.Session;
        var t = s.AddObject(EffectRegistry.Text, 0, 0, 60)!;
        t.Main.Values["text"] = "見出し";
        var shape = s.AddObject(EffectRegistry.Shape, 1, 0, 60)!;
        var pkg = TemplatePackage.Create(s.Project, new[] { t, shape }, new TemplateOptions
        {
            Name = "展開テスト用", InsertAs = TemplateInsertMode.Objects,
            Exposed = new() { new ExposedParam { ObjectId = t.Id, Effect = 0, Key = "text", Label = "タイトル" },
                              new ExposedParam { ObjectId = shape.Id, Effect = shape.Effects.Count - 1, Key = "x", Label = "帯の位置" } },
        });
        Svc.Templates.Save(pkg);
        var entry = Svc.Templates.List().Single(e => e.Name == "展開テスト用");
        try
        {
            EditorCommands.InsertTemplate(entry, 10, 300);
            var placedShape = s.Scene.Objects.Single(o => o.Layer == 11 && o.Start == 300);
            s.Select(placedShape.Id);
            s.CurrentFrame = 310;
            Pump();
            // 図形を選んでも、組の項目 (テキストの本文を含む) が出て、テキストを書き換えられる
            var settings = w.GetVisualDescendants().OfType<ObjectSettingsView>().Single();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == L.T("テンプレートの項目") && b.IsEffectivelyVisible);
            var box = settings.GetVisualDescendants().OfType<TextBox>().First(b => b.Text == "見出し" && b.IsEffectivelyVisible);
            box.Text = "新しい見出し";
            Pump();
            Assert.Equal("新しい見出し", s.Scene.Objects.Single(o => o.Layer == 10 && o.Start == 300).Main.GetString("text"));
            Shot(w, "template-expanded-properties");
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
        // テキストの本文・文字色と画像のファイルは、最初から公開する項目に選ばれている
        var checkedRows = save.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsChecked == true).ToList();
        Assert.Equal(3, checkedRows.Count);
        // 変形 (X・拡大率など) やフィルタのトラックバーも選べる。オブジェクトごとにまとめて並ぶ
        Assert.Equal(2, save.GetVisualDescendants().OfType<CollapsibleSection>().Count());
        int all = save.GetVisualDescendants().OfType<CollapsibleSection>().Sum(x => ((StackPanel)x.Body).Children.OfType<DockPanel>().Count());
        int expected = new[] { t, img }.Sum(o => o.Effects.Sum(e => e.Definition.Params.Count(p => p.Key != "scene")));
        Assert.Equal(expected, all);
        // 絞り込み: 「拡大率」で探すと、2 つのオブジェクトの「変形」の拡大率だけが残る
        var search = save.GetVisualDescendants().OfType<TextBox>().First(b => b.Watermark == "パラメータ名で絞り込む" || b.Watermark == L.T("パラメータ名で絞り込む"));
        search.Text = L.T("拡大率");
        Pump();
        int visible = save.GetVisualDescendants().OfType<CollapsibleSection>().Sum(x => ((StackPanel)x.Body).Children.OfType<DockPanel>().Count(r => r.IsVisible));
        Assert.Equal(2, visible);
        search.Text = "";
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

            // テンプレートの保存のダイアログの、オブジェクトごとの欄の見出しも、本文をそのまま出す (訳さない)
            var d = new TemplateSaveDialog(s.Project, new[] { s.Scene.Find(text.Id)! }, "x", Array.Empty<string>());
            d.Show(w);
            Pump();
            var section = d.GetVisualDescendants().OfType<CollapsibleSection>().Single();
            Assert.Contains(section.Children[0].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "テキスト");
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
    public void Dragging_a_clip_edge_changes_its_length_in_one_undo()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { ColorTitle("#FF0000", 4), ColorTitle("#00FF00", 4) });
        ed.Select(null);
        Pump();
        var strip = w.Simple.Strip;
        var span = ed.Layout.Clips[0];
        double y = StoryboardStrip.ClipTop + 40;
        var from = strip.TranslatePoint(new Point(strip.FrameToX(span.End + 1) - 2, y), w)!.Value;
        var to = new Point(from.X - strip.PixelsPerSecond, from.Y);   // 1 秒短く
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point(from.X - 10, from.Y));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
        Pump();
        Assert.Equal(3, ed.Board!.Clips[0].Duration, 1);
        Assert.Equal(ed.Board.Clips[0].Id, ed.SelectedId);
        Assert.Equal("#FF0000", ed.Board.Clips[0].Background);   // 並べ替えにはならない
        Svc.Session.Undo();
        Assert.Equal(4, ed.Board!.Clips[0].Duration);
    }

    [AvaloniaFact]
    public void Ctrl_and_shift_click_select_several_clips_and_the_panel_edits_them_all()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { ColorTitle("#FF0000"), ColorTitle("#00FF00"), ColorTitle("#0000FF"), ColorTitle("#FFFFFF") });
        ed.Select(null);
        Pump();
        var strip = w.Simple.Strip;
        var layout = ed.Layout;
        Point At(int i) => strip.TranslatePoint(new Point(strip.FrameToX((layout.Clips[i].Start + layout.Clips[i].End) / 2), StoryboardStrip.ClipTop + 40), w)!.Value;
        void Click(int i, RawInputModifiers mods)
        {
            w.MouseDown(At(i), MouseButton.Left, mods); w.MouseUp(At(i), MouseButton.Left, mods);
            Pump();
        }

        Click(0, RawInputModifiers.None);
        Click(2, RawInputModifiers.Control);
        Assert.Equal(new[] { ed.Board!.Clips[0].Id, ed.Board.Clips[2].Id }, ed.SelectedClipIds);
        Assert.Contains(w.Simple.Panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == L.Format("{0} 個のクリップを選んでいます", "2"));

        // 見た目を選ぶと、選んでいる 2 つに付く
        var look = w.Simple.Panel.GetVisualDescendants().OfType<ComboBox>().Single(c => c.ItemsSource is string[] items && items.Contains("セピア"));
        look.SelectedIndex = Array.IndexOf(ClipPanel.Looks, "セピア");
        Pump();
        Assert.Equal(new[] { ClipLook.Sepia, ClipLook.None, ClipLook.Sepia, ClipLook.None }, ed.Board!.Clips.Select(c => c.Look));

        Click(3, RawInputModifiers.Shift);   // 主に選んでいる 2 つ目 (番号 2) から 4 つ目まで
        Assert.Equal(new[] { ed.Board.Clips[2].Id, ed.Board.Clips[3].Id }, ed.SelectedClipIds);

        strip.Focus();
        w.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        Pump();
        Assert.Equal(4, ed.SelectedClipIds.Count);
        w.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Pump();
        Assert.Empty(ed.Board!.Clips);
        Svc.Session.Undo();
        Assert.Equal(4, ed.Board!.Clips.Count);
    }

    [AvaloniaFact]
    public void Clip_panel_shows_the_new_settings_for_each_kind()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        var video = new StoryClip { Kind = StoryClipKind.Video, File = "/v.mp4", SourceSeconds = 10, HasAudio = true, Speed = 2, Caption = "c" };
        var photo = new StoryClip { Kind = StoryClipKind.Photo, File = "/p.png", Duration = 3 };
        ed.AddClips(new[] { video, photo, ColorTitle("#000000") });
        ed.AddMusic(new MusicClip { File = "/m.wav", SourceSeconds = 3 });
        Pump();
        List<string?> Checks() => w.Simple.Panel.GetVisualDescendants().OfType<CheckBox>().Select(c => c.Content as string).ToList();

        ed.Select(video.Id); Pump();
        Assert.Contains("音の高さを変えない", Checks());
        Assert.Contains("後ろに帯を敷く (読みやすくする)", Checks());
        Assert.Contains("画面いっぱいに広げる (はみ出た所は切れる)", Checks());
        var keep = w.Simple.Panel.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "音の高さを変えない");
        keep.IsChecked = true;
        Pump();
        Assert.True(ed.Board!.FindClip(video.Id)!.KeepPitch);

        ed.Select(ed.Board.Clips[2].Id); Pump();
        Assert.Contains(w.Simple.Panel.GetVisualDescendants().OfType<ComboBox>(), c => c.ItemsSource is string[] items && items.Contains("下から上へ流す (エンドロール)"));

        ed.Select(ed.Board.Music[0].Id); Pump();
        Assert.Contains("繰り返して、映像の終わりまで鳴らす", Checks());
        Assert.Contains("動画の音声が鳴っている間は小さくする", Checks());

        ed.Select(null); Pump();
        var fade = w.Simple.Panel.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "最後に黒い画面へフェードアウト");
        fade.IsChecked = true;
        Pump();
        Assert.True(ed.Board!.EndFadeSeconds > 0);
        Assert.True(ed.CanEdit);
    }

    [Fact]
    public void Time_boxes_read_and_show_minutes_and_seconds()
    {
        Assert.Equal("1:23.50", NumberBox.FormatTime(83.5, 2));
        Assert.Equal("0:05.00", NumberBox.FormatTime(5, 2));
        Assert.Equal("1:00:01.0", NumberBox.FormatTime(3601, 1));
        Assert.True(NumberBox.TryParseTime("1:23.5", out var a)); Assert.Equal(83.5, a);
        Assert.True(NumberBox.TryParseTime("1:02:03", out var b)); Assert.Equal(3723, b);
        Assert.True(NumberBox.TryParseTime("90", out var c)); Assert.Equal(90, c);
        Assert.False(NumberBox.TryParseTime("1:x", out _));
        Assert.False(NumberBox.TryParseTime("1.5:00", out _));
    }

    [AvaloniaFact]
    public void Clip_panel_time_box_accepts_minutes_and_seconds()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { ColorTitle("#FF0000") });
        Pump();
        var box = w.Simple.Panel.GetVisualDescendants().OfType<NumberBox>().First(b => b.TimeFormat);
        Assert.Equal("0:02.00", box.Text);
        box.Text = "1:05";
        box.Commit();
        Pump();
        Assert.Equal(65, ed.Board!.Clips[0].Duration);
    }

    [AvaloniaFact]
    public void Dragged_music_snaps_to_clip_boundaries()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { ColorTitle("#FF0000", 4), ColorTitle("#00FF00", 4) });
        ed.AddMusic(new MusicClip { File = "/m.wav", SourceSeconds = 2, StartSeconds = 0 });
        ed.Select(null);
        Svc.Session.CurrentFrame = 0;
        Pump();
        var strip = w.Simple.Strip;
        double y = StoryboardStrip.ClipTop + StoryboardStrip.ClipHeight + StoryboardStrip.MusicGap + StoryboardStrip.MusicHeight / 2;
        var from = strip.TranslatePoint(new Point(strip.FrameToX(30), y), w)!.Value;
        // 4 秒 (2 つ目のクリップの始まり) の少し手前まで動かすと、ぴったり 4 秒に合う
        var to = new Point(from.X + strip.PixelsPerSecond * 3.9, from.Y);
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point(from.X + 10, from.Y));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
        Pump();
        Assert.Equal(4, ed.Board!.Music[0].StartSeconds, 3);

        // Alt を押していれば合わせない
        var from2 = strip.TranslatePoint(new Point(strip.FrameToX(150), y), w)!.Value;
        var to2 = new Point(from2.X - strip.PixelsPerSecond * 0.1, from2.Y);
        w.MouseDown(from2, MouseButton.Left);
        w.MouseMove(new Point(from2.X - 10, from2.Y), RawInputModifiers.Alt);
        w.MouseMove(to2, RawInputModifiers.Alt);
        w.MouseUp(to2, MouseButton.Left, RawInputModifiers.Alt);
        Pump();
        Assert.Equal(3.9, ed.Board!.Music[0].StartSeconds, 1);
    }

    [AvaloniaFact]
    public void Dropped_audio_starts_where_it_was_dropped()
    {
        // 音声を調べるには FFmpeg のライブラリが要る (ほかのメディアのテストと同じく、なければ飛ばす。CI では必須)
        if (!VideoBuilder.Media.FFmpegLoader.EnsureLoaded())
        {
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("VB_REQUIRE_FFMPEG"));
            return;
        }
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { ColorTitle("#FF0000", 6) });
        Svc.Session.CurrentFrame = 0;
        Pump();
        var dir = Path.Combine(Path.GetTempPath(), "vb-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 無音の 16 ビット WAV (1 秒)
            string path = Path.Combine(dir, "music.wav");
            const int rate = 48000;
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                int bytes = rate * 2;
                bw.Write("RIFF"u8); bw.Write(36 + bytes); bw.Write("WAVE"u8);
                bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
                bw.Write("data"u8); bw.Write(bytes); bw.Write(new byte[bytes]);
            }
            var strip = w.Simple.Strip;
            double y = StoryboardStrip.ClipTop + StoryboardStrip.ClipHeight + StoryboardStrip.MusicGap + StoryboardStrip.MusicHeight / 2;
            var at = strip.TranslatePoint(new Point(strip.FrameToX(90), y), w)!.Value;   // 3 秒の所
            w.Simple.ExternalDrag(w, at, new[] { path });
            Pump();
            var music = Assert.Single(ed.Board!.Music);
            Assert.Equal(3, music.StartSeconds, 1);
            // 追加したものを選び、現在位置をその始まり (落とした所) へ移す (クリップを追加したときと同じ)
            Assert.Equal(music.Id, ed.SelectedId);
            Assert.Equal(90, Svc.Session.CurrentFrame);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void Alt_arrows_select_the_previous_and_next_clip()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        ed.AddClips(new[] { ColorTitle("#FF0000"), ColorTitle("#00FF00"), ColorTitle("#0000FF") });
        ed.Select(null);
        Svc.Session.CurrentFrame = 0;
        Pump();
        w.Simple.Strip.Focus();
        w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        Pump();
        Assert.Equal(ed.Board!.Clips[0].Id, ed.SelectedId);
        w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        Pump();
        Assert.Equal(ed.Board.Clips[1].Id, ed.SelectedId);
        Assert.Equal(ed.Layout.Clips[1].Start, Svc.Session.CurrentFrame);
        w.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Pump();
        Assert.Equal(ed.Board.Clips[0].Id, ed.SelectedId);
        Assert.Equal(0, Svc.Session.CurrentFrame);
    }

    [AvaloniaFact]
    public void Clip_panel_warns_about_shortened_transitions_and_missing_files()
    {
        var w = OpenSimple();
        var ed = Svc.Storyboard;
        var shortClip = ColorTitle("#00FF00", 1);
        shortClip.Transition = 1;
        shortClip.TransitionSeconds = 3;
        var missing = new StoryClip { Kind = StoryClipKind.Photo, File = "/no/such/file.png", Duration = 2 };
        ed.AddClips(new[] { ColorTitle("#FF0000"), shortClip, missing });
        Pump();
        List<string?> Texts() => w.Simple.Panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        ed.Select(shortClip.Id); Pump();
        // 1 秒のクリップなので、切り替えは半分 (14 フレーム = 0.47 秒) までに縮む
        Assert.Contains(Texts(), t => t != null && t.Contains("0.47"));
        ed.Select(missing.Id); Pump();
        Assert.Contains(L.T("ファイルが見つかりません。移動したときは、付け替えてください。"), Texts());
        Assert.Contains(w.Simple.Panel.GetVisualDescendants().OfType<Button>(), b => (b.Content as string) == "素材の付け替え...");
        ed.Select(ed.Board!.Clips[0].Id); Pump();
        Assert.DoesNotContain(L.T("ファイルが見つかりません。移動したときは、付け替えてください。"), Texts());
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
    public void Preview_can_be_drawn_on_the_screen_gpu_when_rendering_uses_another_gpu()
    {
        // ハイブリッドの GPU のノート PC: 画面は CPU 内蔵の GPU (Intel)、描画には外付けの GPU (NVIDIA) を選んでいる
        const string screen = "Mesa Intel(R) UHD Graphics 620 (KBL GT2)";
        var nvidia = new VideoBuilder.Rendering.GpuAdapter(1, "NVIDIA GeForce RTX 3060/PCIe/SSE2", "NVIDIA Corporation", "/dev/dri/card1", "/dev/dri/renderD129", false);
        var intel = new VideoBuilder.Rendering.GpuAdapter(0, screen, "Intel", "/dev/dri/card0", "/dev/dri/renderD128", false);
        // 既定: 違う GPU の間では共有できないので、読み戻して表示する。理由に、設定で変えられることを書く
        Assert.False(PreviewService.GlSharingAllowed(screen, nvidia, RenderBackend.Auto, onScreenGpu: false, force: false, out var reason));
        Assert.Contains("プレビューを描く GPU", reason);
        // 「画面の表示と同じ GPU」にすると、画面の GPU で描いて共有する
        Assert.True(PreviewService.GlSharingAllowed(screen, nvidia, RenderBackend.Auto, onScreenGpu: true, force: false, out _));
        // 同じ GPU を選んでいれば、設定にかかわらず共有する
        Assert.True(PreviewService.GlSharingAllowed(screen, intel, RenderBackend.Auto, onScreenGpu: false, force: false, out _));
        // 画面の GPU がソフトウェアなら、自動のときは画面の GPU で描かない (CPU 描画の方が速い)
        Assert.False(PreviewService.GlSharingAllowed("llvmpipe (LLVM 20.1.2, 256 bits)", nvidia, RenderBackend.Auto, onScreenGpu: true, force: false, out _));
        Assert.True(PreviewService.GlSharingAllowed("llvmpipe (LLVM 20.1.2, 256 bits)", nvidia, RenderBackend.Auto, onScreenGpu: true, force: true, out _));
    }

    [Fact]
    public void Screen_gpu_on_windows_follows_the_chosen_gpu_by_luid_then_by_name()
    {
        // Avalonia が渡す一覧: 名前は小文字、LUID は 8 バイト
        static Avalonia.Platform.PlatformGraphicsDeviceAdapterDescription Desc(string name, long luid) =>
            new() { Description = name, DeviceLuid = BitConverter.GetBytes(luid) };
        var list = new[] { Desc("intel(r) uhd graphics", 0x1111), Desc("nvidia geforce rtx 4070 laptop gpu", 0x2222) };
        var nvidia = new VideoBuilder.Rendering.GpuAdapter(1, "NVIDIA GeForce RTX 4070 Laptop GPU", "NVIDIA", "", "", false, 0x2222, "10DE:2860:3FC817AA");
        Assert.Equal(1, VideoBuilder.App.Platform.ScreenGpu.IndexOf(list, nvidia));
        // LUID が合わなくても (起動し直した直後の一覧など)、名前で見つける
        Assert.Equal(1, VideoBuilder.App.Platform.ScreenGpu.IndexOf(list, nvidia with { Luid = 0x9999 }));
        // 選んでいない・ソフトウェア・見つからないときは、これまでどおり最初の GPU
        Assert.Equal(0, VideoBuilder.App.Platform.ScreenGpu.IndexOf(list, null));
        Assert.Equal(0, VideoBuilder.App.Platform.ScreenGpu.IndexOf(list, nvidia with { IsSoftware = true }));
        Assert.Equal(0, VideoBuilder.App.Platform.ScreenGpu.IndexOf(list, nvidia with { Name = "AMD Radeon RX 7600", Luid = 0x3333 }));
    }


    [Fact]
    public void Thumbnails_are_kept_on_disk()
    {
        if (!VideoBuilder.Media.FFmpegLoader.EnsureLoaded()) return;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-thumb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var clip = Path.Combine(dir, "clip.mp4");
            using (var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                $"-y -loglevel error -f lavfi -i testsrc=size=320x240:rate=30:duration=2 -c:v libx264 -pix_fmt yuv420p -g 15 \"{clip}\"")))
            {
                proc!.WaitForExit(30000);
                if (proc.ExitCode != 0) return;
            }
            var cacheRoot = Path.Combine(AppPaths.CacheDir, "thumbnail");
            int before = Directory.Exists(cacheRoot) ? Directory.GetFiles(cacheRoot, "*.bgra", SearchOption.AllDirectories).Length : 0;
            var first = ThumbnailCache.LoadOrCreate(clip, 1.0, true)!.Value;
            Assert.Equal(ThumbnailCache.Height, first.H);
            Assert.Equal(first.W * first.H * 4, first.Pixels.Length);
            Assert.Equal(before + 1, Directory.GetFiles(cacheRoot, "*.bgra", SearchOption.AllDirectories).Length);
            // 2 回目は保存したものを読む (同じ絵)。元のファイルが変わったら作り直す
            var again = ThumbnailCache.LoadOrCreate(clip, 1.0, true)!.Value;
            Assert.Equal(first.Pixels, again.Pixels);
            Assert.Equal(before + 1, Directory.GetFiles(cacheRoot, "*.bgra", SearchOption.AllDirectories).Length);
            File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(1));
            ThumbnailCache.LoadOrCreate(clip, 1.0, true);
            Assert.Equal(before + 2, Directory.GetFiles(cacheRoot, "*.bgra", SearchOption.AllDirectories).Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
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


    [AvaloniaFact]
    public async Task Opening_a_project_checks_videos_for_proxies_one_at_a_time_without_blocking_the_screen()
    {
        if (!VideoBuilder.Media.FFmpegLoader.EnsureLoaded()) return;
        var dir = Path.Combine(Path.GetTempPath(), $"vb-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var p = new Project();
            for (int i = 0; i < 4; i++)
            {
                // 重くない動画 (320x180 の H.264) なので、この PC でのデコードの速さを測ってから決める
                var clip = Path.Combine(dir, $"c{i}.mp4");
                using (var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                    $"-y -loglevel error -f lavfi -i testsrc2=size=320x180:rate=30:duration=3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{clip}\"")))
                {
                    proc!.WaitForExit(30000);
                    if (proc.ExitCode != 0) return;
                }
                var v = ObjectFactory.Create(EffectRegistry.Video, i, 0, 60);
                v.Main.Values["file"] = clip;
                p.Root.Objects.Add(v);
            }
            VideoBuilder.Media.ProxyStore.MaxConcurrentMeasurements = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Svc.Proxies.EnqueueProject(p, heavyOnly: true);
            double callMs = watch.Elapsed.TotalMilliseconds;
            Assert.True(Svc.Proxies.IsChecking);                        // 裏で調べている
            for (int i = 0; i < 500 && Svc.Proxies.IsChecking; i++) { await Task.Delay(10); Pump(); }
            Assert.False(Svc.Proxies.IsChecking);
            Assert.Equal(1, VideoBuilder.Media.ProxyStore.MaxConcurrentMeasurements);   // 1 本ずつ測った
            Assert.NotNull(Svc.Proxies.LastCheck);
            Assert.Equal(0, Svc.Proxies.Pending);                       // 小さい動画にはプロキシを作らない
            Assert.True(callMs < 200, $"呼び出しに {callMs:0} ms (画面を止めない)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }


    /// <summary>
    /// おすすめの構成 (設計書 22.63): ダイアログを開くと計測を始め、項目を出す。「この構成を使う」で設定に書き込み、描画を作り直す。
    /// 計測は短くする (CPU だけ・書き出しなし)。書き出しの画面は、おすすめのエンコーダを最初に選ぶ。
    /// </summary>
    [AvaloniaFact]
    public async Task Recommended_setup_is_measured_shown_and_applied()
    {
        var window = Open();
        var s = Svc.Settings;
        var saved = (s.RenderBackend, s.PreferredVideoEncoder, s.SetupSuggested);
        try
        {
            // CPU に決め打ち・x265 を勧める結果を作って、使えば書き込まれることを確かめる
            s.RenderBackend = RenderBackend.Gpu;
            // VB_UI_SHOTS (見た目の確認) のときは、ふだんと同じ計測にする
            bool full = Environment.GetEnvironmentVariable("VB_UI_SHOTS") is { Length: > 0 };
            var dialog = new SetupDialog
            {
                Options = full ? new SetupBenchmark.Options()
                    : new SetupBenchmark.Options { IncludeGpus = false, RenderFrames = 3, DecodeFrames = 3, ExportFrames = 0, ExportOnGpu = false },
            };
            dialog.Show(window);
            for (int i = 0; i < 1500 && !dialog.Finished; i++) { await Task.Delay(20); Pump(); }
            Assert.True(dialog.Finished, "計測が終わらない");
            // 見た目の確認用: VB_UI_SHOTS にフォルダを指定すると、ダイアログの絵を保存する
            if (Environment.GetEnvironmentVariable("VB_UI_SHOTS") is { Length: > 0 } shots)
            {
                Pump();
                dialog.CaptureRenderedFrame()?.Save(Path.Combine(shots, $"setup-{(L.English ? "en" : "ja")}.png"));
            }
            var backend = Assert.Single(dialog.Items, i => i.Name == "描画方式");
            if (!full)
            {
                // GPU を測らない計測なので、描画方式は自動 (GPU に決め打ちなら変える)
                Assert.True(backend.Changes);
                dialog.ClickApply();
                Assert.True(dialog.Applied);
                Assert.True(s.SetupSuggested);
                Assert.Equal(RenderBackend.Auto, s.RenderBackend);
            }
            dialog.Close();

            // 書き出しの画面: おすすめのエンコーダ (ここでは x265。ソフトウェアなので、はじめから選べる) を最初に選ぶ
            if (MediaWriter.IsEncoderAvailable("libx265") && MediaWriter.IsEncoderAvailable("aac"))
            {
                s.PreferredVideoEncoder = "libx265";
                var export = new ExportDialog();
                Assert.Contains("H.265", export.SelectedFormatName);
                export.Close();
            }
        }
        finally
        {
            (s.RenderBackend, s.PreferredVideoEncoder, s.SetupSuggested) = saved;
            Svc.Preview.ReloadBackend();
        }
    }
}

public class EffectMenuTests
{
    [AvaloniaFact]
    public void Filter_menu_is_grouped_by_category_and_lists_every_filter()
    {
        var items = Menus.Effects(EffectRegistry.Filters, _ => { }).Cast<MenuItem>().ToList();
        Assert.Equal(EffectRegistry.FilterGroups.Length, items.Count);
        int leaves = items.Sum(i => i.Items.Count);
        Assert.Equal(EffectRegistry.Filters.Count(), leaves);

        // 音声のエフェクトは分類がないので、サブメニューにせず並べる
        var audio = Menus.Effects(EffectRegistry.AudioFilters, _ => { }).Cast<MenuItem>().ToList();
        Assert.Equal(EffectRegistry.AudioFilters.Count(), audio.Count);
        Assert.All(audio, a => Assert.Empty(a.Items));
    }
}
