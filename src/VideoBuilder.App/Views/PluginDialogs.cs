using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Plugins;

namespace VideoBuilder.App.Views;

/// <summary>
/// プラグインが指定した項目 (PluginField) の入力欄。フォーム (モーダル) と入力パネル (開いたまま) で共通に使う。
/// 文字はプラグインが付けたものなので訳さない。Apply で入力された値を各項目の Value に書き戻す。
/// </summary>
public sealed class PluginFieldsView
{
    private readonly List<Action> _apply = new();
    private readonly List<TextBox> _required = new();
    private readonly Dictionary<string, Control> _inputs = new();
    private readonly Dictionary<string, Action<object>> _setters = new();

    /// <summary>入力欄を並べたもの。</summary>
    public StackPanel Panel { get; } = new() { Spacing = 8 };

    public PluginFieldsView(string? message, IEnumerable<PluginField> fields)
    {
        if (!string.IsNullOrEmpty(message))
            Panel.Children.Add(new TextBlock { Text = L.Name(message), TextWrapping = TextWrapping.Wrap });
        foreach (var field in fields)
        {
            var control = Build(field);
            Panel.Children.Add(field is PluginCheckField || field is PluginTextField { Multiline: true } ? control : Row(L.Name(field.Label), control));
            if (!string.IsNullOrEmpty(field.Note))
            {
                var note = new TextBlock { Text = L.Name(field.Note), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(field is PluginCheckField ? 0 : 120, -4, 0, 0) };
                note.Classes.Add("secondary");
                Panel.Children.Add(note);
            }
        }
    }

    private static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), MinHeight = 26 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
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
                _inputs[t.Key] = box;
                _setters[t.Key] = v => { t.Value = v as string ?? ""; box.Text = t.Value; };
                if (t.Required) _required.Add(box);
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
                _inputs[n.Key] = box;
                _setters[n.Key] = v => { if (v is double d) { n.Value = Math.Clamp(d, n.Minimum, n.Maximum); box.Value = n.Value; } };
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
                _inputs[c.Key] = combo;
                _setters[c.Key] = v =>
                {
                    int i = c.Options.FindIndex(o => o.Value == v as string);
                    if (i >= 0) { c.Value = c.Options[i].Value; combo.SelectedIndex = i; }
                };
                return combo;
            }
            case PluginCheckField k:
            {
                var check = new CheckBox { Content = L.Name(k.Label), IsChecked = k.Value };
                _apply.Add(() => k.Value = check.IsChecked == true);
                _inputs[k.Key] = check;
                _setters[k.Key] = v => { if (v is bool b) { k.Value = b; check.IsChecked = b; } };
                return check;
            }
            default:
                return new TextBlock { Text = L.Name(field.Label) };
        }
    }

    /// <summary>必須の項目がすべて埋まっているか。空のものがあれば、そこにフォーカスを移して false。</summary>
    public bool CheckRequired()
    {
        foreach (var box in _required)
        {
            if (!string.IsNullOrWhiteSpace(box.Text)) continue;
            box.Focus();
            return false;
        }
        return true;
    }

    /// <summary>入力された値を、各項目の Value に書き戻す。</summary>
    public void Apply()
    {
        foreach (var a in _apply) a();
    }

    /// <summary>項目の値と入力欄を変える (文字・選択は string、数値は double、チェックは bool)。</summary>
    public void SetValue(string key, object value)
    {
        if (_setters.TryGetValue(key, out var set)) set(value);
    }

    public void Focus(string key)
    {
        if (!_inputs.TryGetValue(key, out var c)) return;
        c.Focus();
        if (c is TextBox box && box is not NumberBox) box.CaretIndex = box.Text?.Length ?? 0;
    }
}

/// <summary>
/// プラグインが PluginForm で指定した入力フォーム (モーダル)。OK を押すと、入力された値を各項目の Value に書き戻す。
/// </summary>
public sealed class PluginFormDialog : DialogBase
{
    private readonly PluginFieldsView _fields;

    public bool Accepted { get; private set; }

    public PluginFormDialog(PluginForm form) : base(L.Name(form.Title), Math.Max(280, form.Width))
    {
        _fields = new PluginFieldsView(form.Message, form.Fields);
        Body.Children.Add(_fields.Panel);
        AddButton("キャンセル", Close);
        AddButton(string.IsNullOrEmpty(form.OkText) ? "OK" : L.Name(form.OkText), Ok, accent: true);
    }

    private void Ok()
    {
        if (!_fields.CheckRequired()) return;
        _fields.Apply();
        Accepted = true;
        Close();
    }
}

/// <summary>
/// プラグインの入力パネル (PluginPanel)。閉じずに開いたまま、ボタンを押すたびにプラグインの処理を呼ぶ。
/// 編集の画面は操作できる (モーダルでない)。下端に、プラグインが出す状態の文字を出す。
/// </summary>
public sealed class PluginPanelWindow : DialogBase, IPluginPanel
{
    private readonly PluginFieldsView _fields;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 8) };
    private readonly Action<PluginPanelWindow, PluginPanelButton> _click;
    private bool _closed;

    public PluginPanel Spec { get; }

    /// <param name="click">ボタンが押されたとき (値はもう Value に書き戻してある)。</param>
    public PluginPanelWindow(PluginPanel spec, Action<PluginPanelWindow, PluginPanelButton> click) : base(L.Name(spec.Title), Math.Max(280, spec.Width))
    {
        Spec = spec;
        _click = click;
        ShowInTaskbar = false;
        _fields = new PluginFieldsView(spec.Message, spec.Fields);
        Body.Children.Add(_fields.Panel);
        _status.Classes.Add("secondary");
        // 状態の文字はボタン列のすぐ上に置く
        DockPanel.SetDock(_status, Dock.Bottom);
        Root.Children.Insert(1, _status);
        foreach (var b in spec.Buttons)
            AddButton(L.Name(b.Text), () => Press(b), accent: b.IsDefault);
        AddButton("閉じる", Close);
        Closed += (_, _) => { _closed = true; PanelClosed?.Invoke(); };
        if (spec.FocusKey != null) Opened += (_, _) => _fields.Focus(spec.FocusKey);
    }

    private void Press(PluginPanelButton b)
    {
        if (b.CheckRequired && !_fields.CheckRequired()) return;
        _fields.Apply();
        _click(this, b);
    }

    /// <summary>ボタンを押したのと同じ (テスト用)。</summary>
    public void Click(string text) => Press(Spec.Buttons.First(b => b.Text == text));

    // ---------------- IPluginPanel ----------------

    public bool IsOpen => !_closed;
    public event Action? PanelClosed;
    event Action? IPluginPanel.Closed { add => PanelClosed += value; remove => PanelClosed -= value; }

    /// <summary>今出している状態の文字 (プラグインが渡したまま)。</summary>
    public string? Status { get; private set; }

    public void SetStatus(string? text)
    {
        Status = string.IsNullOrEmpty(text) ? null : text;
        _status.Text = Status == null ? null : L.Name(Status);
    }
    public void SetValue(string key, string value) => _fields.SetValue(key, value);
    public void SetValue(string key, double value) => _fields.SetValue(key, value);
    public void SetValue(string key, bool value) => _fields.SetValue(key, value);
    public void FocusField(string key) => _fields.Focus(key);
    void IPluginPanel.Focus(string key) => FocusField(key);
    void IPluginPanel.Close() { if (!_closed) Close(); }
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
