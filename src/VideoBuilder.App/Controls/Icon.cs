using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VideoBuilder.App.Controls;

/// <summary>
/// Material Symbols のパス (viewBox 0 -960 960 960) を指定サイズで描く。
/// PathIcon と違い、アイコンごとの余白を保ったまま拡大縮小する。
/// </summary>
public class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, SizeProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public Icon() { }
    public Icon(string key, double size = 16) { Data = Icons.Get(key); Size = size; }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data == null || Foreground == null) return;
        Draw(context, Data, Foreground, new Point((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2), Size);
    }

    /// <summary>他のコントロールの Render からアイコンを描くときに使う。</summary>
    public static void Draw(DrawingContext context, Geometry data, IBrush brush, Point topLeft, double size)
    {
        double k = size / 960.0;
        var m = Matrix.CreateTranslation(0, 960) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation(topLeft.X, topLeft.Y);
        using (context.PushTransform(m)) context.DrawGeometry(brush, null, data);
    }
}

public static class Icons
{
    private static readonly Dictionary<string, Geometry?> Cache = new();

    /// <summary>"Icon.Play" や "Play" でアイコンを取り出す。</summary>
    public static Geometry? Get(string key)
    {
        if (!key.StartsWith("Icon.", StringComparison.Ordinal)) key = "Icon." + key;
        if (Cache.TryGetValue(key, out var g)) return g;
        g = Application.Current != null && Application.Current.TryGetResource(key, null, out var res) ? res as Geometry : null;
        Cache[key] = g;
        return g;
    }
}
