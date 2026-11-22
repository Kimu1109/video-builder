using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Plugins;
using VideoBuilder.Rendering;

namespace SamplePlugin;

/// <summary>
/// プラグインの書き方の見本。速い順に:
/// 色の変換 (IColorMatrixEffect) → ピクセルの書き換え (IPixelEffect) → 自由な描画 (IVideoEffect)。
/// ほかに、メディアオブジェクト・音声のエフェクト・移動方法・メニューのコマンドを 1 つずつ追加する。
/// </summary>
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

        host.RegisterColorMatrixEffect(new EffectDefinition
        {
            Id = "sample.sepia", Name = "セピア", Category = EffectCategory.Filter, Icon = "Icon.Tune",
            Params = new[] { new ParamDefinition { Key = "strength", Name = "強さ", Min = 0, Max = 100, Default = 100 } },
        }, new Sepia());

        host.RegisterPixelEffect(new EffectDefinition
        {
            Id = "sample.posterize", Name = "ポスタリゼーション", Category = EffectCategory.Filter, Icon = "Icon.GridOn",
            Params = new[] { new ParamDefinition { Key = "levels", Name = "階調", Min = 2, Max = 32, Default = 4, Decimals = 0 } },
        }, new Posterize());

        host.RegisterAudioEffect(new EffectDefinition
        {
            Id = "sample.lowpass", Name = "こもった音", Category = EffectCategory.AudioFilter, Icon = "Icon.VolumeUp",
            Params = new[] { new ParamDefinition { Key = "cutoff", Name = "カットオフ (Hz)", Min = 20, Max = 20000, Default = 800, Decimals = 0 } },
        }, new LowPass());

        host.RegisterInterpolator(new Bounce());
        // メニューのコマンドは API バージョン 3 から
        if (host.ApiVersion >= 3) host.RegisterCommand(new InsertCaption());
        if (host.ApiVersion >= 2) host.Log("読み込みました");
    }
}

/// <summary>周辺を暗くする。</summary>
public sealed class Vignette : IVideoEffect
{
    public string Id => "sample.vignette";
    // 結果が入力とパラメータだけで決まるので、エンジンが前のフレームの結果を使い回してよい
    public EffectFlags Flags => EffectFlags.Deterministic;

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
    // 絵は「マスの大きさ・色・画面の大きさ」だけで決まる。同じ間はエンジンが前の絵を使い、Render を呼ばない
    public string? CacheKey(EffectArgs args)
    {
        var s = args.Context.Project.Settings;
        return $"{(int)args.Track("size")}|{args.Effect.GetString("color")}|{s.Width}x{s.Height}";
    }

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

/// <summary>セピア調にする。色の変換だけなので、エンジンが前後の色の変換とまとめて 1 回で処理する。</summary>
public sealed class Sepia : IColorMatrixEffect
{
    public string Id => "sample.sepia";

    public float[]? GetMatrix(EffectArgs args)
    {
        float t = Math.Clamp(args.Track("strength") / 100f, 0, 1);
        if (t <= 0) return null;   // 何もしないときは null (処理を飛ばす)
        float[] sepia =
        {
            0.393f, 0.769f, 0.189f, 0, 0,
            0.349f, 0.686f, 0.168f, 0, 0,
            0.272f, 0.534f, 0.131f, 0, 0,
            0, 0, 0, 1, 0,
        };
        var m = new float[20];
        for (int i = 0; i < 20; i++)
        {
            float identity = i % 6 == 0 ? 1 : 0;
            m[i] = identity + (sepia[i] - identity) * t;
        }
        return m;
    }
}

/// <summary>色の階調を減らす。ピクセルを直接書き換える (複数スレッドで処理する)。</summary>
public sealed class Posterize : IPixelEffect
{
    public string Id => "sample.posterize";

    public void Process(PixelBuffer pixels, EffectArgs args)
    {
        int levels = Math.Clamp((int)args.Track("levels"), 2, 256);
        // 0..255 の値ごとの答えを先に作っておく (ピクセルごとに割り算しない)
        var table = new byte[256];
        for (int v = 0; v < 256; v++) table[v] = (byte)Math.Round(Math.Round(v / 255.0 * (levels - 1)) / (levels - 1) * 255);
        // 行を横帯に分けて、複数スレッドで処理する (BGRA・乗算済みアルファ)
        pixels.ParallelRows((y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                var row = pixels.Row(y);
                for (int x = 0; x + 3 < row.Length; x += 4)
                {
                    byte a = row[x + 3];
                    if (a == 0) continue;
                    if (a == 255)
                    {
                        row[x] = table[row[x]]; row[x + 1] = table[row[x + 1]]; row[x + 2] = table[row[x + 2]];
                        continue;
                    }
                    // 半透明: いったんアルファを外してから階調を減らし、また掛ける
                    for (int c = 0; c < 3; c++) row[x + c] = (byte)(table[Math.Min(255, row[x + c] * 255 / a)] * a / 255);
                }
            }
        });
    }
}

/// <summary>高い音を弱める (1 次のローパスフィルタ)。前のブロックの続きの値を state に持つ。</summary>
public sealed class LowPass : IAudioEffect
{
    public string Id => "sample.lowpass";

    private sealed class State { public float L, R; }

    public void Process(AudioBlock block, ref object? state)
    {
        if (state is not State st || !block.Continuous) state = st = new State();
        float cutoff = Math.Clamp(block.Track("cutoff"), 20, block.SampleRate / 2f);
        float k = 1 - MathF.Exp(-2 * MathF.PI * cutoff / block.SampleRate);
        var s = block.Samples;
        float l = st.L, r = st.R;
        for (int i = 0; i + 1 < s.Length; i += 2)
        {
            l += (s[i] - l) * k; s[i] = l;
            r += (s[i + 1] - r) * k; s[i + 1] = r;
        }
        st.L = l; st.R = r;
    }
}

/// <summary>
/// 「プラグイン」メニューのコマンドの見本: フォームで文字と長さを聞き、再生位置にテキストを置く。
/// </summary>
public sealed class InsertCaption : IPluginCommand
{
    public string Id => "sample.insertCaption";
    public string Name => "再生位置にテキストを置く...";
    public string? Group => "サンプル";

    public async Task RunAsync(IPluginCommandContext context)
    {
        var form = new PluginForm
        {
            Title = "テキストを置く",
            OkText = "置く",
            Fields =
            {
                new PluginTextField { Key = "text", Label = "文字", Value = "こんにちは", Required = true },
                new PluginNumberField { Key = "seconds", Label = "長さ (秒)", Value = 3, Minimum = 0.1, Maximum = 600, Decimals = 1 },
            },
        };
        if (!await context.ShowFormAsync(form)) return;
        string text = form.Get<PluginTextField>("text").Value;
        int length = Math.Max(1, context.Project.SecondsToFrames(form.Get<PluginNumberField>("seconds").Value));

        TimelineObject? placed = null;
        context.Edit("テキストを置く", editor =>
        {
            placed = editor.AddText(text, context.TargetLayer, context.CurrentFrame, length);
            if (placed != null) editor.Select(new[] { placed });
        });
        if (placed == null) await context.ShowMessageAsync("テキストを置く", "空いているレイヤーがありません。");
    }
}
