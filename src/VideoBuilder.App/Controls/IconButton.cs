using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace VideoBuilder.App.Controls;

internal static class IconContent
{
    public static Control Build(Geometry? icon, string? text, double size)
    {
        var ic = new Icon { Data = icon, Size = size };
        if (string.IsNullOrEmpty(text)) return ic;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { ic, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
        };
    }
}

/// <summary>アイコン (と任意のラベル) を表示するボタン。</summary>
public class IconButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<IconButton, Geometry?>(nameof(Icon));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<IconButton, string?>(nameof(Text));
    public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<IconButton, double>(nameof(IconSize), 16);

    protected override Type StyleKeyOverride => typeof(Button);

    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    public IconButton() { }

    public IconButton(string iconKey, string? tooltip = null, Action? click = null, bool flat = true)
    {
        Icon = Icons.Get(iconKey);
        if (tooltip != null) ToolTip.SetTip(this, tooltip);
        if (click != null) Click += (_, _) => click();
        if (flat) Classes.Add("flat");
        Focusable = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == TextProperty || change.Property == IconSizeProperty)
            Content = IconContent.Build(Icon, Text, IconSize);
    }
}

/// <summary>ON / OFF を持つアイコンボタン。</summary>
public class IconToggle : ToggleButton
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<IconToggle, Geometry?>(nameof(Icon));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<IconToggle, string?>(nameof(Text));

    protected override Type StyleKeyOverride => typeof(ToggleButton);

    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public IconToggle() { }

    public IconToggle(string iconKey, string? tooltip = null)
    {
        Icon = Icons.Get(iconKey);
        if (tooltip != null) ToolTip.SetTip(this, tooltip);
        Classes.Add("flat");
        Focusable = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == TextProperty)
            Content = IconContent.Build(Icon, Text, 16);
    }
}
