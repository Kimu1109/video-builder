using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Plugins;
using VideoBuilder.Rendering;

namespace SamplePlugin;

/// <summary>プラグインの書き方の見本: フィルタ効果・メディアオブジェクト・移動方法を 1 つずつ追加する。</summary>
public sealed class Plugin : IPlugin
{
    public void Initialize(IPluginHost host)
    {
        host.RegisterFilter(new EffectDefinition
        {
            Id = "sample.vignette", Name = "ビネット", Category = EffectCategory.Filter, Icon = "Icon.BlurOn",
            Params = new[] { new ParamDefinition { Key = "strength", Name = "強さ", Min = 0, Max = 100, Default = 60 } },
        }, new Vignette());

        host.RegisterMediaObject(new EffectDefinition
        {
            Id = "sample.checker", Name = "市松模様", Category = EffectCategory.VideoSource, Icon = "Icon.GridOn", Color = "#5A7F3A",
            Params = new[]
            {
                new ParamDefinition { Key = "size", Name = "マスの大きさ", Min = 2, Max = 500, Default = 40, Decimals = 0 },
                new ParamDefinition { Key = "color", Name = "色", Kind = ParamKind.Color, DefaultText = "#FFFFFF" },
            },
        }, new Checker());

        host.RegisterInterpolator(new Bounce());
    }
}

/// <summary>周辺を暗くする。</summary>
public sealed class Vignette : IVideoEffect
{
    public string Id => "sample.vignette";

    public Sprite Apply(Sprite input, EffectArgs args)
    {
        float strength = Math.Clamp(args.Track("strength") / 100f, 0, 1);
        if (strength <= 0) return input;
        using var surface = args.Context.CreateSurface(input.Width, input.Height);
        if (surface == null) return input;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(input.Image, 0, 0);
        var center = new SKPoint(input.Width / 2f, input.Height / 2f);
        float radius = MathF.Sqrt(center.X * center.X + center.Y * center.Y);
        using var shader = SKShader.CreateRadialGradient(center, radius,
            new[] { SKColors.Transparent, SKColors.Black.WithAlpha((byte)(strength * 255)) }, new[] { 0.4f, 1f }, SKShaderTileMode.Clamp);
        // SrcATop: 元の絵がある場所だけ暗くする (透明部分は透明のまま)
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.SrcATop };
        canvas.DrawRect(0, 0, input.Width, input.Height, paint);
        return new Sprite(args.Context.Own(surface.Snapshot()), input.Cx, input.Cy);
    }
}

/// <summary>画面いっぱいの市松模様。</summary>
public sealed class Checker : IVideoSource
{
    public SKImage? Render(EffectArgs args)
    {
        var settings = args.Context.Project.Settings;
        int size = Math.Max(2, (int)args.Track("size"));
        using var surface = args.Context.CreateSurface(settings.Width, settings.Height);
        if (surface == null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = args.Color("color") };
        for (int y = 0; y * size < settings.Height; y++)
        for (int x = y % 2; x * size < settings.Width; x += 2)
            surface.Canvas.DrawRect(x * size, y * size, size, size, paint);
        return args.Context.Own(surface.Snapshot());
    }
}

/// <summary>終点で跳ねる移動。</summary>
public sealed class Bounce : IInterpolator
{
    public string Id => "sample.bounce";
    public string DisplayName => "バウンド";

    public double Evaluate(double start, double end, double t)
    {
        const double n = 7.5625, d = 2.75;
        double e;
        if (t < 1 / d) e = n * t * t;
        else if (t < 2 / d) { t -= 1.5 / d; e = n * t * t + 0.75; }
        else if (t < 2.5 / d) { t -= 2.25 / d; e = n * t * t + 0.9375; }
        else { t -= 2.625 / d; e = n * t * t + 0.984375; }
        return start + (end - start) * e;
    }
}
