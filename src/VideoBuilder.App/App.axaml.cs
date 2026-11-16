using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VideoBuilder.App.Services;
using VideoBuilder.App.Views;

namespace VideoBuilder.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            Svc.Initialize(this);
            ApplyOverrides(args);
            var main = new MainWindow();
            desktop.MainWindow = main;
            EditorCommands.StartAutoSave();

            string? project = args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a) && a.EndsWith(Core.ProjectSerializer.Extension, StringComparison.OrdinalIgnoreCase));
            if (project != null) main.Opened += async (_, _) => await EditorCommands.OpenPathAsync(project);
            else if (args.Contains("--demo")) DemoProject.Load(Svc.Session);

            int shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length) main.Opened += (_, _) => ScreenshotAndExit(desktop, main, args[shot + 1]);
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
