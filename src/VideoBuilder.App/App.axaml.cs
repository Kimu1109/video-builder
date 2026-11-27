using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VideoBuilder.App.Services;
using VideoBuilder.App.Shell;

namespace VideoBuilder.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            int lang = Array.IndexOf(args, "--lang");
            if (lang >= 0 && lang + 1 < args.Length)
                Svc.LanguageOverride = args[lang + 1].ToLowerInvariant() switch { "en" => Language.English, "ja" => Language.Japanese, _ => null };
            var phase = System.Diagnostics.Stopwatch.StartNew();
            double sinceStart = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            Svc.Initialize(this);
            double initMs = phase.Elapsed.TotalMilliseconds; phase.Restart();
            ApplyOverrides(args);
            var main = new MainWindow();
            double windowMs = phase.Elapsed.TotalMilliseconds;
            desktop.MainWindow = main;
            EditorCommands.StartAutoSave();

            string? project = args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a) && a.EndsWith(Core.ProjectSerializer.Extension, StringComparison.OrdinalIgnoreCase));
            if (project != null) main.Opened += async (_, _) => await EditorCommands.OpenPathAsync(project);
            else if (args.Contains("--demo")) DemoProject.Load(Svc.Session);

            if (args.Contains("--startup-time"))
            {
                // 起動にかかった時間 (プロセス開始 → ウィンドウ表示) を出力して終了する (計測用)
                main.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    var elapsed = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
                    Console.WriteLine($"startup: {elapsed.TotalMilliseconds:0} ms (ランタイムと Avalonia の起動 {sinceStart:0} / 初期化 {initMs:0} / ウィンドウ作成 {windowMs:0} / 表示まで {elapsed.TotalMilliseconds - sinceStart - initMs - windowMs:0})");
                    Svc.Session.History.MarkSaved();
                    desktop.Shutdown();
                }, DispatcherPriority.Background);
            }
            // 初めて起動したとき: このパソコンでいちばん速い構成を調べて出す (設計書 22.63。一度出したら出さない)
            if (Views.SetupDialog.ShouldSuggestOnStartup(Svc.Settings, args))
                main.Opened += (_, _) => DispatcherTimer.RunOnce(async () =>
                {
                    var dialog = new Views.SetupDialog(firstRun: true);
                    await dialog.ShowDialog(main);
                    Svc.Settings.SetupSuggested = true;
                    Svc.Settings.Save();
                }, TimeSpan.FromSeconds(0.5));
            if (args.Contains("--report-setup"))
            {
                // 動作確認用: おすすめの構成を調べて出力して終了する (設定は変えない。設計書 22.63)
                main.Opened += (_, _) => DispatcherTimer.RunOnce(async () =>
                {
                    var options = new VideoBuilder.Rendering.SetupBenchmark.Options { ExportOnGpu = Svc.Settings.RenderBackend != RenderBackend.Cpu };
                    var result = await Task.Run(() => VideoBuilder.Rendering.SetupBenchmark.Run(options));
                    var env = await Task.Run(Views.SetupDialog.CurrentEnvironment);
                    Console.WriteLine($"setup: {result.Elapsed.TotalSeconds:0.0} s, screen={env.ScreenRenderer}");
                    foreach (var t in result.Render) Console.WriteLine($"setup: render {t.Renderer}: readback={t.WithReadbackMs:0.0} ms renderOnly={t.RenderOnlyMs:0.0} ms {t.Error}");
                    if (result.Decode is { } d) Console.WriteLine($"setup: decode {d.HardwareName}={d.HardwareMs:0.0} ms software={d.SoftwareMs:0.0} ms {d.HardwareError}");
                    foreach (var e in result.Encoders) Console.WriteLine($"setup: export {e.Label}: {e.Fps:0.0} fps {e.Error}");
                    foreach (var i in SetupAdvisor.Recommend(result, Svc.Settings, env))
                        Console.WriteLine($"setup: {(i.Changes ? "*" : " ")} {i.Name}: {i.Current} -> {i.Recommended} | {i.Reason.Replace('\n', ' ')}");
                    Svc.Session.History.MarkSaved();
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(0.5));
            }
            int report = Array.IndexOf(args, "--report-preview");
            if (report >= 0 && report + 1 < args.Length)
            {
                // 動作確認用: 動画を読み込んで 3 秒再生し、描画方式と GPU 上のまま変換したフレームの数を出力して終了する (設計書 22.62)
                string media = Path.GetFullPath(args[report + 1]);
                main.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    var error = MediaImport.AddFile(media, 0, 0);
                    if (error != null) Console.WriteLine("report: " + error);
                    Svc.Preview.Play();
                    DispatcherTimer.RunOnce(() =>
                    {
                        Svc.Preview.Stop();
                        var (frames, hwError, decoders) = Svc.Preview.HardwareFrameStatus;
                        Console.WriteLine($"report: backend={Svc.Preview.BackendDescription}");
                        Console.WriteLine($"report: hardwareFrames={frames} hardwareError={hwError} decoders={decoders}");
                        Svc.Session.History.MarkSaved();
                        desktop.Shutdown();
                    }, TimeSpan.FromSeconds(3));
                }, TimeSpan.FromSeconds(1));
            }
            int shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                // 共有のテクスチャの絵は、ウィンドウを画像に描く方法では写らないので、読み戻す方法で描く
                Svc.Preview.AllowSharedTarget = false;
                main.Opened += (_, _) => ScreenshotAndExit(desktop, main, args[shot + 1]);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>動作確認用のコマンドライン指定 (設定ファイルには保存しない)。</summary>
    private static void ApplyOverrides(string[] args)
    {
        var s = Svc.Settings;
        if (args.Contains("--docked")) s.WindowMode = WindowMode.Docked;
        if (args.Contains("--separate")) s.WindowMode = WindowMode.Separate;
        if (args.Contains("--gpu")) s.RenderBackend = RenderBackend.Gpu;
        if (args.Contains("--cpu")) s.RenderBackend = RenderBackend.Cpu;
        if (args.Contains("--gpu") || args.Contains("--cpu")) Svc.Preview.ReloadBackend();
        int t = Array.IndexOf(args, "--theme");
        if (t >= 0 && t + 1 < args.Length && Enum.TryParse<ThemeMode>(args[t + 1], true, out var mode)) { s.Theme = mode; Svc.Theme.Apply(); }
    }

    /// <summary>開いているウィンドウを PNG に保存して終了する (見た目の確認用)。</summary>
    private static void ScreenshotAndExit(IClassicDesktopStyleApplicationLifetime desktop, MainWindow main, string path)
    {
        DispatcherTimer.RunOnce(() =>
        {
            void Save(Window w, string file)
            {
                var size = new PixelSize((int)w.ClientSize.Width, (int)w.ClientSize.Height);
                if (size.Width <= 0 || size.Height <= 0) return;
                using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
                bmp.Render(w);
                bmp.Save(file);
            }
            Save(main, path);
            int i = 1;
            foreach (var w in main.ToolWindows) Save(w, Path.ChangeExtension(path, null) + $"-{i++}.png");
            Svc.Session.History.MarkSaved();
            desktop.Shutdown();
        }, TimeSpan.FromSeconds(2));
    }
}
