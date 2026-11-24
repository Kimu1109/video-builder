using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Core;

namespace VideoBuilder.App.Views;

/// <summary>
/// 動き方 (イージング) の編集。種類・向き・強さ・減衰を選び、グラフで形を確かめる。
/// ベジェ曲線は、グラフの 2 つの制御点をドラッグして形を決める。
/// </summary>
public sealed class EasingDialog : DialogBase
{
    public static readonly string[] KindNames =
    {
        "なめらか (Sine)", "2 次 (Quad)", "3 次 (Cubic)", "4 次 (Quart)", "5 次 (Quint)", "指数 (Expo)", "円 (Circ)",
        "行き過ぎて戻る (Back)", "ゴム (Elastic)", "弾む (Bounce)", "ばね (Spring)", "ベジェ曲線",
    };
    public static readonly string[] DirectionNames = { "加速 (In)", "減速 (Out)", "加速して減速 (In-Out)" };

    private static readonly (string Name, double X1, double Y1, double X2, double Y2)[] BezierPresets =
    {
        ("ease", 0.25, 0.1, 0.25, 1), ("ease-in", 0.42, 0, 1, 1), ("ease-out", 0, 0, 0.58, 1), ("ease-in-out", 0.42, 0, 0.58, 1),
        ("行き過ぎて止まる", 0.34, 1.56, 0.64, 1), ("溜めてから動く", 0.6, -0.28, 0.74, 0.05),
    };

    /// <summary>決めた動き方。null のままならキャンセル。</summary>
    public Easing? Result { get; private set; }
    /// <summary>「設定を消す」を押した (区間ごとの設定を消す / 標準の動きに戻す)。</summary>
    public bool Cleared { get; private set; }

    private readonly Easing _ease;
    private readonly EasingGraph _graph;

    public EasingDialog(string title, Easing? initial, string clearLabel) : base(title, 460)
    {
        _ease = initial?.Clone() ?? new Easing();
        _graph = new EasingGraph(_ease) { Width = 420, Height = 240, HorizontalAlignment = HorizontalAlignment.Center };

        var kind = new ComboBox { ItemsSource = KindNames, SelectedIndex = (int)_ease.Kind, HorizontalAlignment = HorizontalAlignment.Stretch };
        var dir = new ComboBox { ItemsSource = DirectionNames, SelectedIndex = (int)_ease.Direction, HorizontalAlignment = HorizontalAlignment.Stretch };
        var strength = new NumberBox { Minimum = 0, Maximum = 20, Decimals = 2, Value = _ease.Strength, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        var damping = new NumberBox { Minimum = 0, Maximum = 1, Decimals = 2, Value = _ease.Damping, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        var preset = new ComboBox { ItemsSource = BezierPresets.Select(p => p.Name).ToList(), PlaceholderText = "プリセットから選ぶ", HorizontalAlignment = HorizontalAlignment.Stretch };
        var dirRow = Row("向き", dir);
        var strengthRow = Row("強さ (1 = 標準)", strength);
        var dampingRow = Row("減衰 (0〜1)", damping);
        var presetRow = Row("ベジェのプリセット", preset);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap };
        hint.Classes.Add("secondary");

        void Sync()
        {
            bool bezier = _ease.Kind == EasingKind.Bezier;
            dirRow.IsVisible = !bezier;
            strengthRow.IsVisible = _ease.Kind is EasingKind.Back or EasingKind.Elastic or EasingKind.Spring;
            dampingRow.IsVisible = _ease.Kind is EasingKind.Elastic or EasingKind.Spring;
            presetRow.IsVisible = bezier;
            hint.Text = bezier
                ? "グラフの 2 つの丸をドラッグして形を決めます。横が時間、縦が進み具合です (枠の外へ出すと行き過ぎます)。"
                : "横が時間、縦が進み具合です。点線の枠の外へ出るところは、行き過ぎて戻ります。";
            _graph.InvalidateVisual();
        }
        kind.SelectionChanged += (_, _) => { if (kind.SelectedIndex >= 0) { _ease.Kind = (EasingKind)kind.SelectedIndex; Sync(); } };
        dir.SelectionChanged += (_, _) => { if (dir.SelectedIndex >= 0) { _ease.Direction = (EasingDirection)dir.SelectedIndex; Sync(); } };
        strength.Committed += v => { _ease.Strength = v; Sync(); };
        damping.Committed += v => { _ease.Damping = v; Sync(); };
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedIndex < 0) return;
            var p = BezierPresets[preset.SelectedIndex];
            (_ease.X1, _ease.Y1, _ease.X2, _ease.Y2) = (p.X1, p.Y1, p.X2, p.Y2);
            Sync();
        };

        Body.Children.Add(Row("種類", kind));
        Body.Children.Add(dirRow);
        Body.Children.Add(strengthRow);
        Body.Children.Add(dampingRow);
        Body.Children.Add(presetRow);
        Body.Children.Add(new Border { Child = _graph, Margin = new Thickness(0, 6) });
        Body.Children.Add(hint);
        Sync();

        AddButton(clearLabel, () => { Cleared = true; Close(); });
        AddButton("キャンセル", Close);
        AddButton("OK", () =>
        {
            // 入力途中の数値も反映する
            _ease.Strength = strength.Value; _ease.Damping = damping.Value;
            Result = _ease;
            Close();
        }, accent: true);
    }

    /// <summary>動き方の形を描くグラフ。ベジェ曲線なら制御点をドラッグできる。</summary>
    private sealed class EasingGraph(Easing ease) : Control
    {
        // 縦は -0.5〜1.5 を表示する (行き過ぎも見えるように)
        private const double Low = -0.5, High = 1.5;
        private int _dragging = -1;

        private Rect Plot => new(28, 8, Math.Max(10, Bounds.Width - 40), Math.Max(10, Bounds.Height - 16));
        private Point ToView(double t, double v)
        {
            var r = Plot;
            return new Point(r.X + t * r.Width, r.Bottom - (v - Low) / (High - Low) * r.Height);
        }
        private (double T, double V) FromView(Point p)
        {
            var r = Plot;
            return ((p.X - r.X) / r.Width, Low + (r.Bottom - p.Y) / r.Height * (High - Low));
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Svc.Brush("Bg.Base"), new Rect(Bounds.Size));
            var grid = new Pen(Svc.Brush("Border.Default"), 1);
            var dashed = new Pen(Svc.Brush("Border.Default"), 1, new DashStyle(new double[] { 3, 3 }, 0));
            // 0〜1 の枠
            context.DrawRectangle(null, dashed, new Rect(ToView(0, 1), ToView(1, 0)));
            context.DrawLine(grid, ToView(0, 0), ToView(1, 1));
            var label = Svc.Brush("Fg.Secondary");
            void Text(string s, Point at) =>
                context.DrawText(new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, label), at);
            Text("1", ToView(0, 1) - new Point(14, 7));
            Text("0", ToView(0, 0) - new Point(14, 7));

            var accent = new Pen(Svc.Brush("Accent"), 2);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(ToView(0, 0), false);
                for (int i = 1; i <= 200; i++)
                {
                    double t = i / 200.0;
                    g.LineTo(ToView(t, Math.Clamp(ease.Apply(t), Low - 0.2, High + 0.2)));
                }
                g.EndFigure(false);
            }
            context.DrawGeometry(null, accent, geo);

            if (ease.Kind != EasingKind.Bezier) return;
            var handlePen = new Pen(Svc.Brush("Fg.Secondary"), 1);
            var p1 = ToView(ease.X1, ease.Y1);
            var p2 = ToView(ease.X2, ease.Y2);
            context.DrawLine(handlePen, ToView(0, 0), p1);
            context.DrawLine(handlePen, ToView(1, 1), p2);
            foreach (var p in new[] { p1, p2 })
                context.DrawEllipse(Brushes.White, new Pen(Svc.Brush("Accent"), 2), p, 6, 6);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (ease.Kind != EasingKind.Bezier) return;
            var pos = e.GetPosition(this);
            double D(Point a) => Math.Abs(a.X - pos.X) + Math.Abs(a.Y - pos.Y);
            double d1 = D(ToView(ease.X1, ease.Y1)), d2 = D(ToView(ease.X2, ease.Y2));
            _dragging = Math.Min(d1, d2) > 16 ? -1 : d1 <= d2 ? 0 : 1;
            if (_dragging >= 0) e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_dragging < 0) return;
            var (t, v) = FromView(e.GetPosition(this));
            t = Math.Round(Math.Clamp(t, 0, 1), 2);
            v = Math.Round(Math.Clamp(v, Low, High), 2);
            if (_dragging == 0) { ease.X1 = t; ease.Y1 = v; } else { ease.X2 = t; ease.Y2 = v; }
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _dragging = -1;
            e.Pointer.Capture(null);
        }
    }
}
