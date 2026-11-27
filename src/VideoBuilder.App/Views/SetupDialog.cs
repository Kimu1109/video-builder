using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VideoBuilder.App.Services;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Views;

/// <summary>
/// おすすめの構成 (設計書 22.63)。開くとすぐに短い計測を裏で始め (数秒)、このパソコンでいちばん速い構成を、
/// 今の設定・理由 (測った値) と並べて出す。「この構成を使う」で、そのまま設定に書き込んで使う。
/// 初めて起動したときと、環境設定 > 描画 の「おすすめの構成を調べる」から開く。
/// </summary>
public sealed class SetupDialog : DialogBase
{
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6 };
    // 状態の文は、ここで訳してから入れる (GPU の名前などを含むので、TextBlock では訳さない)
    private readonly TextBlock _status = L.Verbatim("");
    private readonly StackPanel _results = new() { Spacing = 10 };
    private readonly Button _apply, _close;
    private readonly TextBlock _intro;
    private readonly CancellationTokenSource _cancel = new();
    private List<SetupItem> _items = new();
    private bool _running;

    /// <summary>おすすめを使った (設定を書き換えた)。</summary>
    public bool Applied { get; private set; }
    /// <summary>計測が終わって、おすすめを出した (テスト用)。</summary>
    internal bool Finished { get; private set; }
    /// <summary>出したおすすめ (テスト用)。</summary>
    internal IReadOnlyList<SetupItem> Items => _items;
    /// <summary>「この構成を使う」を押す (テスト用)。</summary>
    internal void ClickApply() { if (_apply.IsEnabled) Apply(); }

    /// <summary>計測の内容 (テストで短くするため)。</summary>
    internal SetupBenchmark.Options Options { get; init; } = new() { ExportOnGpu = Svc.Settings.RenderBackend != RenderBackend.Cpu };

    public SetupDialog(bool firstRun = false) : base("おすすめの構成", 640)
    {
        var intro = _intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = firstRun
                ? "このパソコンでいちばん速く動く設定を調べています (数秒かかります)。結果を見て、そのまま使うこともできます。あとから 環境設定 > 描画 の「おすすめの構成を調べる」でも調べられます。"
                : "このパソコンでいちばん速く動く設定を調べています (数秒かかります)。",
        };
        Body.Children.Add(intro);
        Body.Children.Add(_progress);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Classes.Add("secondary");
        Body.Children.Add(_status);
        Body.Children.Add(new ScrollViewer { Content = _results, MaxHeight = 420, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        _close = AddButton(firstRun ? "今は使わない" : "閉じる", Close);
        _apply = AddButton("この構成を使う", Apply, accent: true);
        _apply.IsEnabled = false;
        Opened += async (_, _) => await MeasureAsync();
        Closing += (_, _) => _cancel.Cancel();
    }

    /// <summary>計測して、おすすめを出す。</summary>
    internal async Task MeasureAsync()
    {
        if (_running) return;
        _running = true;
        _apply.IsEnabled = false;
        _progress.IsVisible = true;
        _results.Children.Clear();
        var progress = new Progress<(string Step, string? Name, double Fraction)>(p =>
        {
            _progress.Value = p.Fraction;
            _status.Text = (p.Name != null ? L.Format(p.Step, p.Name) : L.T(p.Step)) + "...";
        });
        try
        {
            var options = Options;
            var result = await Task.Run(() => SetupBenchmark.Run(options, progress, _cancel.Token));
            var env = await Task.Run(CurrentEnvironment);
            _items = SetupAdvisor.Recommend(result, Svc.Settings, env);
            _progress.Value = 1;
            int changes = _items.Count(i => i.Changes);
            _status.Text = changes == 0
                ? L.Format("今の設定が、いちばん速い構成です (調べた時間 {0} 秒)。", $"{result.Elapsed.TotalSeconds:0.0}")
                : L.Format("{0} 個の項目を変えると速くなります (調べた時間 {1} 秒)。", changes.ToString(), $"{result.Elapsed.TotalSeconds:0.0}");
            ShowItems();
            _apply.IsEnabled = changes > 0;
            _progress.IsVisible = false;
            _intro.Text = changes == 0
                ? L.T("このパソコンで調べた結果です。今の設定のままで、いちばん速く動きます。")
                : L.T("このパソコンで調べた結果です。「この構成を使う」で、変わる項目 (色の付いたもの) をまとめて設定します。");
            Finished = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = L.T("調べられませんでした: ") + ex.Message; }
        finally { _running = false; }
    }

    /// <summary>この環境のこと (画面の GPU・GPU の一覧など)。</summary>
    internal static SetupEnvironment CurrentEnvironment()
    {
        IReadOnlyList<GpuAdapter> all;
        try { all = GpuContext.Adapters(); } catch { all = Array.Empty<GpuAdapter>(); }
        var shared = Svc.Preview.SharedTarget;
        string? screen = shared is { IsD3D: false } && shared.Renderer.Length > 0 ? shared.Renderer : null;
        return new SetupEnvironment(OperatingSystem.IsLinux(), OperatingSystem.IsWindows(), screen, all,
            GpuFixedByEnvironment: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VIDEO_BUILDER_GPU")),
            ScreenGlFixedByEnvironment: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VIDEO_BUILDER_SCREEN_GL")));
    }

    private void ShowItems()
    {
        _results.Children.Clear();
        foreach (var item in _items)
        {
            var name = new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            // GPU の名前などはドライバが返すものなので、項目の値は訳さない (アプリの言葉は SetupAdvisor で訳してある)
            var value = L.Verbatim(item.Changes ? $"{item.Current}  →  {item.Recommended}" : item.Recommended + "  " + L.T("(今のまま)"));
            value.TextWrapping = TextWrapping.Wrap;
            if (item.Changes) value.Classes.Add("accent-text");
            var reason = L.Verbatim(item.Reason + (item.NeedsRestart ? "\n" + L.T("アプリを起動し直すと反映されます。") : ""));
            reason.TextWrapping = TextWrapping.Wrap;
            reason.Classes.Add("secondary");
            _results.Children.Add(new StackPanel { Spacing = 2, Children = { name, value, reason } });
        }
    }

    private void Apply()
    {
        Applied = ApplyItems(_items);
        int restart = _items.Count(i => i.Changes && i.NeedsRestart);
        _status.Text = restart > 0 ? L.T("おすすめの構成にしました。一部は、アプリを起動し直すと反映されます。") : L.T("おすすめの構成にしました。");
        _apply.IsEnabled = false;
        _close.Content = L.T("閉じる");
    }

    /// <summary>おすすめを設定に書き込み、描画・デコードを新しい設定で作り直す。何か変えたら true。</summary>
    internal static bool ApplyItems(IEnumerable<SetupItem> items)
    {
        var s = Svc.Settings;
        if (!SetupAdvisor.ApplyAll(items, s)) return false;
        s.SetupSuggested = true;
        s.Save();
        Svc.ApplyGpuAdapter();
        VideoBuilder.Media.HardwareDecoding.Mode = s.HardwareDecode;
        VideoBuilder.Media.HardwareDecoding.ResetDevices();
        Svc.Preview.ReloadBackend();
        return true;
    }

    /// <summary>初めて起動したときに出すか (一度出したら出さない。動作確認用の起動では出さない)。</summary>
    internal static bool ShouldSuggestOnStartup(AppSettings s, string[] args) =>
        !s.SetupSuggested && Environment.GetEnvironmentVariable("VIDEO_BUILDER_SETUP_SUGGEST") != "0"
        && !args.Any(a => a is "--screenshot" or "--startup-time" or "--report-preview" or "--report-setup" or "--no-setup");
}
