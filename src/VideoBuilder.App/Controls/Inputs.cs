using System.Data;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VideoBuilder.App.Services;
using VideoBuilder.Core;

namespace VideoBuilder.App.Controls;

/// <summary>数値入力欄。式 ("100*2") も受け付け、ホイールと上下キーで増減できる。</summary>
public sealed class NumberBox : TextBox
{
    private double _value;
    private bool _editing;

    protected override Type StyleKeyOverride => typeof(TextBox);

    public double Minimum { get; set; } = double.MinValue;
    public double Maximum { get; set; } = double.MaxValue;
    public int Decimals { get; set; } = 1;
    /// <summary>
    /// 秒を「分:秒」(1 時間以上なら「時:分:秒」) で表示する。入力は「1:23.5」「83.5」のどちらでもよい。
    /// </summary>
    public bool TimeFormat { get; set; }
    /// <summary>ユーザーが値を確定した。</summary>
    public event Action<double>? Committed;

    public NumberBox()
    {
        Classes.Add("num");
        MinWidth = 0;
    }

    public double Value
    {
        get => _value;
        set
        {
            // 入力途中の文字は消さない。表示が前の値のままなら (= まだ何も打っていなければ) 新しい値に更新する
            bool untouched = !_editing || Text == Format(_value);
            _value = value;
            if (untouched) Text = Format(value);
        }
    }

    private string Format(double v) => TimeFormat ? FormatTime(v, Decimals) : v.ToString("F" + Decimals, CultureInfo.InvariantCulture);

    /// <summary>秒を「分:秒」(1 時間以上なら「時:分:秒」) にする。例: 83.5 → "1:23.50"。</summary>
    public static string FormatTime(double seconds, int decimals)
    {
        string sign = seconds < 0 ? "-" : "";
        seconds = Math.Round(Math.Abs(seconds), decimals);
        long whole = (long)Math.Floor(seconds);
        string sec = (seconds - whole + whole % 60).ToString((decimals > 0 ? "00." + new string('0', decimals) : "00"), CultureInfo.InvariantCulture);
        long minutes = whole / 60;
        return minutes >= 60 ? $"{sign}{minutes / 60}:{minutes % 60:00}:{sec}" : $"{sign}{minutes}:{sec}";
    }

    /// <summary>「時:分:秒」「分:秒」「秒」を秒にする。読めなければ false。</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        bool negative = parts[0].TrimStart().StartsWith('-');
        if (negative) parts[0] = parts[0].TrimStart()[1..];
        for (int i = 0; i < parts.Length; i++)
        {
            // 最後 (秒) だけ小数を許す
            var style = i == parts.Length - 1 ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
            if (!double.TryParse(parts[i].Trim(), style, CultureInfo.InvariantCulture, out var v)) return false;
            seconds = seconds * 60 + v;
        }
        if (negative) seconds = -seconds;
        return true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _editing = true;
        SelectAll();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        Commit();
        _editing = false;
        Text = Format(_value);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { Commit(); Text = Format(_value); SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { Text = Format(_value); TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null); /* フォーカスを外す (Avalonia 12 で ClearFocus がなくなった) */ e.Handled = true; return; }
        if (e.Key is Key.Up or Key.Down)
        {
            Commit();
            Step(e.Key == Key.Up ? 1 : -1, e.KeyModifiers);
            Text = Format(_value);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!IsFocused) return;   // スクロール中に誤って値が変わらないよう、フォーカス中だけ反応する
        Step(e.Delta.Y > 0 ? 1 : -1, e.KeyModifiers);
        Text = Format(_value);
        e.Handled = true;
    }

    /// <summary>Shift で 10 倍、Ctrl で 1/10。</summary>
    public void Step(int direction, KeyModifiers modifiers)
    {
        double step = Math.Pow(10, -Decimals);
        if (Decimals == 0 || !modifiers.HasFlag(KeyModifiers.Control)) step *= Decimals == 0 ? 1 : 10;
        if (modifiers.HasFlag(KeyModifiers.Shift)) step *= 10;
        Apply(_value + direction * step);
    }

    /// <summary>入力途中の文字を値として確定する (ダイアログの OK を押したときなど)。</summary>
    public void Commit()
    {
        var text = (Text ?? "").Trim();
        if (text.Length == 0 || text == Format(_value)) return;
        if (text.Contains(':'))
        {
            if (!TryParseTime(text, out var t)) return;
            Apply(t);
            return;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            try { v = Convert.ToDouble(new DataTable().Compute(text, null), CultureInfo.InvariantCulture); }
            catch { return; }
        }
        if (double.IsNaN(v) || double.IsInfinity(v)) return;
        Apply(v);
    }

    private void Apply(double v)
    {
        v = Math.Round(Math.Clamp(v, Minimum, Maximum), Decimals);
        if (v == _value) return;
        _value = v;
        Committed?.Invoke(v);
    }
}

/// <summary>角ばった小さなスライダー。ドラッグの開始と終了を通知する (Undo を 1 回にまとめるため)。</summary>
public sealed class MiniSlider : Control
{
    private double _value;
    private bool _dragging;
    private double _dragStartX, _dragStartValue;

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public event Action? DragStarted;
    public event Action<double>? Changed;
    public event Action? DragCompleted;

    public MiniSlider()
    {
        Height = 20;
        MinWidth = 24;
        Cursor = new Cursor(StandardCursorType.Hand);
        Svc.Theme.Changed += InvalidateVisual;
    }

    public double Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; InvalidateVisual(); }
    }

    private const double Thumb = 7;

    private double Ratio => Maximum <= Minimum ? 0 : Math.Clamp((_value - Minimum) / (Maximum - Minimum), 0, 1);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, mid = Math.Round(h / 2);
        // 当たり判定のために全体を透明で塗る
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        bool on = IsEffectivelyEnabled;
        ctx.FillRectangle(Svc.Brush("Border.Strong"), new Rect(0, mid - 1, w, 2));
        double x = Math.Round(Ratio * (w - Thumb));
        if (on) ctx.FillRectangle(Svc.Brush("Accent"), new Rect(0, mid - 1, x, 2));
        ctx.FillRectangle(Svc.Brush(on ? "Accent" : "Fg.Disabled"), new Rect(x, mid - 7, Thumb, 14));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEffectivelyEnabledProperty) InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        DragStarted?.Invoke();
        double x = e.GetPosition(this).X;
        double thumbX = Ratio * (Bounds.Width - Thumb);
        // つまみの外を押したら、その位置へ値を飛ばす
        if (x < thumbX || x > thumbX + Thumb) SetFromX(x);
        _dragStartX = x; _dragStartValue = _value;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_dragging) return;
        double x = e.GetPosition(this).X;
        double range = Maximum - Minimum, track = Math.Max(1, Bounds.Width - Thumb);
        // Shift: 微調整 (1/10 の速さ)
        double factor = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 1;
        Set(_dragStartValue + (x - _dragStartX) / track * range * factor);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        DragCompleted?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DragCompleted?.Invoke();
    }

    private void SetFromX(double x)
    {
        double track = Math.Max(1, Bounds.Width - Thumb);
        Set(Minimum + Math.Clamp((x - Thumb / 2) / track, 0, 1) * (Maximum - Minimum));
    }

    private void Set(double v)
    {
        v = Math.Clamp(v, Minimum, Maximum);
        if (v == _value) return;
        _value = v;
        InvalidateVisual();
        Changed?.Invoke(v);
    }
}

/// <summary>メニュー項目を短く書くためのヘルパー。</summary>
public static class Menus
{
    /// <param name="verbatim">true なら header を訳さない (利用者が付けた名前)。</param>
    public static MenuItem Item(string header, string? icon, Action? action, string? gesture = null, bool enabled = true, bool? isChecked = null, bool verbatim = false)
    {
        var item = new MenuItem { Header = verbatim ? L.Verbatim(header) : header, IsEnabled = enabled };
        if (icon != null) item.Icon = new Icon(icon);
        if (isChecked != null)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = isChecked.Value;
        }
        if (gesture != null) item.InputGesture = KeyGesture.Parse(gesture);
        if (action != null) item.Click += (_, _) => action();
        return item;
    }

    public static MenuItem Sub(string header, string? icon, IEnumerable<object> items, bool verbatim = false)
    {
        var item = new MenuItem { Header = verbatim ? L.Verbatim(header) : header };
        if (icon != null) item.Icon = new Icon(icon);
        foreach (var i in items) item.Items.Add(i);
        return item;
    }

    public static Separator Sep() => new();

    /// <summary>
    /// エフェクトを追加するメニューの項目。分類 (EffectDefinition.Group) ごとのサブメニューにまとめる
    /// (分類のないプラグインの効果は「プラグイン」に入れる)。どれも分類がなければ (音声のエフェクトなど) 並べるだけ。
    /// </summary>
    public static IEnumerable<object> Effects(IEnumerable<EffectDefinition> defs, Action<EffectDefinition> add)
    {
        var list = defs.ToList();
        object Leaf(EffectDefinition d) => Item(d.Name, d.Icon, () => add(d));
        if (list.All(d => d.Group == null && EffectRegistry.IsBuiltin(d.Id))) return list.Select(Leaf).ToList();
        string GroupOf(EffectDefinition d) => d.Group ?? (EffectRegistry.IsBuiltin(d.Id) ? "切り替え・その他" : "プラグイン");
        var order = EffectRegistry.FilterGroups.ToList();
        return list.GroupBy(GroupOf)
            .OrderBy(g => order.IndexOf(g.Key) is var i && i >= 0 ? i : order.Count)
            .Select(g => (object)Sub(g.Key, null, g.Select(Leaf)))
            .ToList();
    }

    /// <summary>
    /// 一覧 (ComboBox・AutoCompleteBox) の項目を訳さずに表示する。利用者が付けた名前 (カテゴリ・グループ) の一覧に使う。
    /// except に入っている項目 (アプリが用意した「すべてのカテゴリ」など) だけは訳す。
    /// </summary>
    public static Avalonia.Controls.Templates.IDataTemplate VerbatimItems(params string[] except) =>
        new Avalonia.Controls.Templates.FuncDataTemplate<string>((s, _) => s != null && except.Contains(s) ? new TextBlock { Text = s } : L.Verbatim(s ?? ""));
}
