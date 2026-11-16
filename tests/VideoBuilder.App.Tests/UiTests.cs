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
        var eye = c.TranslatePoint(new Point(20, c.LayerToY(1) + 10), w)!.Value;
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
}
