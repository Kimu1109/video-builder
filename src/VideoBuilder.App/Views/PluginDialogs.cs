using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Plugins;

namespace VideoBuilder.App.Views;

/// <summary>
/// プラグインが PluginForm で指定した入力フォーム。文字はプラグインが付けたものなので訳さない。
/// OK を押すと、入力された値を各項目の Value に書き戻す。
/// </summary>
public sealed class PluginFormDialog : DialogBase
{
    private readonly List<Action> _apply = new();
    private readonly List<(PluginTextField Field, TextBox Box)> _required = new();

    public bool Accepted { get; private set; }

    public PluginFormDialog(PluginForm form) : base(L.Name(form.Title), Math.Max(280, form.Width))
    {
        if (!string.IsNullOrEmpty(form.Message))
            Body.Children.Add(new TextBlock { Text = L.Name(form.Message), TextWrapping = TextWrapping.Wrap });
        foreach (var field in form.Fields)
        {
            var control = Build(field);
            Body.Children.Add(field is PluginCheckField || field is PluginTextField { Multiline: true } ? control : Row(L.Name(field.Label), control));
            if (!string.IsNullOrEmpty(field.Note))
            {
                var note = new TextBlock { Text = L.Name(field.Note), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(field is PluginCheckField ? 0 : 120, -4, 0, 0) };
                note.Classes.Add("secondary");
                Body.Children.Add(note);
            }
        }
        AddButton("キャンセル", Close);
        AddButton(string.IsNullOrEmpty(form.OkText) ? "OK" : L.Name(form.OkText), Ok, accent: true);
    }

    private Control Build(PluginField field)
    {
        switch (field)
        {
            case PluginTextField t:
            {
                var box = new TextBox { Text = t.Value, Watermark = t.Placeholder != null ? L.Name(t.Placeholder) : null };
                if (t.Multiline)
                {
                    box.AcceptsReturn = true;
                    box.TextWrapping = TextWrapping.Wrap;
                    box.Height = 140;
                    box.VerticalContentAlignment = VerticalAlignment.Top;
                }
                _apply.Add(() => t.Value = box.Text ?? "");
                if (t.Required) _required.Add((t, box));
                if (!t.Multiline) return box;
                // 複数行のときは、見出しを上に置いて幅いっぱいに使う
                var panel = new StackPanel { Spacing = 4 };
                panel.Children.Add(new TextBlock { Text = L.Name(t.Label) });
                panel.Children.Add(box);
                return panel;
            }
            case PluginNumberField n:
            {
                var box = new NumberBox { Minimum = n.Minimum, Maximum = n.Maximum, Decimals = Math.Clamp(n.Decimals, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, Width = 120 };
                box.Value = Math.Clamp(n.Value, n.Minimum, n.Maximum);
                _apply.Add(() => { box.Commit(); n.Value = box.Value; });
                return box;
            }
            case PluginChoiceField c:
            {
                var labels = c.Options.Select(o => o.Label).ToList();
                var combo = new ComboBox
                {
                    ItemsSource = labels,
                    ItemTemplate = Menus.VerbatimItems(),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    SelectedIndex = Math.Max(0, c.Options.FindIndex(o => o.Value == c.Value)),
                };
                if (c.Options.Count == 0) combo.IsEnabled = false;
                _apply.Add(() => { if (combo.SelectedIndex >= 0 && combo.SelectedIndex < c.Options.Count) c.Value = c.Options[combo.SelectedIndex].Value; });
                return combo;
            }
            case PluginCheckField k:
            {
                var check = new CheckBox { Content = L.Name(k.Label), IsChecked = k.Value };
                _apply.Add(() => k.Value = check.IsChecked == true);
                return check;
            }
            default:
                return new TextBlock { Text = L.Name(field.Label) };
        }
    }

    private void Ok()
    {
        foreach (var (_, box) in _required)
        {
            if (!string.IsNullOrWhiteSpace(box.Text)) continue;
            box.Focus();
            return;
        }
        foreach (var a in _apply) a();
        Accepted = true;
        Close();
    }
}

/// <summary>プラグインの時間のかかる処理の進み具合。中止のボタン (と閉じるボタン) で取り消す。</summary>
public sealed class PluginProgressDialog : DialogBase
{
    private readonly CancellationTokenSource _cts;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Text = "処理しています..." };
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Minimum = 0, Maximum = 1, Height = 6 };
    private readonly Button _cancel;
    private bool _finished;

    public PluginProgressDialog(string title, CancellationTokenSource cts) : base(L.Name(title), 380)
    {
        _cts = cts;
        Body.Children.Add(_message);
        Body.Children.Add(_bar);
        _cancel = AddButton("中止", Cancel);
        Closing += (_, _) => { if (!_finished) Cancel(); };
        // ダイアログが開く前に終わっていたら、開いてすぐ閉じる
        Opened += (_, _) => { if (_finished) Close(); };
    }

    public void Report(PluginProgress p)
    {
        if (p.Message != null) _message.Text = L.Name(p.Message);
        if (p.Fraction is { } f)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(f, 0, 1);
        }
        else _bar.IsIndeterminate = true;
    }

    private void Cancel()
    {
        if (_cts.IsCancellationRequested) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _message.Text = "中止しています...";
        _cancel.IsEnabled = false;
    }

    /// <summary>処理が終わった (UI のスレッドで呼ぶ)。</summary>
    public void Finish()
    {
        _finished = true;
        if (IsVisible) Close();
    }
}
