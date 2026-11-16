using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>オブジェクト 1 つ分の絵。(Cx, Cy) は画像の中でオブジェクトの中心にあたる位置。</summary>
public readonly record struct Sprite(SKImage Image, float Cx, float Cy)
{
    public static Sprite Centered(SKImage image) => new(image, image.Width / 2f, image.Height / 2f);
    public int Width => Image.Width;
    public int Height => Image.Height;
}

/// <summary>1 フレームを描いている間の状態。</summary>
public sealed class RenderContext
{
    public required Project Project { get; init; }
    public required int Frame { get; init; }
    /// <summary>フレームを描き終わったら捨てる画像。</summary>
    public List<IDisposable> Temporaries { get; } = new();

    public T Own<T>(T item) where T : IDisposable
    {
        Temporaries.Add(item);
        return item;
    }

    public void DisposeTemporaries()
    {
        foreach (var d in Temporaries) d.Dispose();
        Temporaries.Clear();
    }
}

/// <summary>効果に渡す引数。パラメータは現在フレームで評価済みの値を返す。</summary>
public readonly struct EffectArgs
{
    public Effect Effect { get; init; }
    public TimelineObject Object { get; init; }
    public int[] Bounds { get; init; }
    public RenderContext Context { get; init; }
    /// <summary>フレームバッファ全体にかけている (フィルタオブジェクト) か。</summary>
    public bool IsFrameBuffer { get; init; }

    public float Track(string key) => (float)Effect.Eval(key, Bounds, Context.Frame);
    public bool Check(string key) => Effect.GetBool(key);
    public SKColor Color(string key) => SKColor.TryParse(Effect.GetString(key), out var c) ? c : SKColors.White;
}

public interface IVideoEffect
{
    string Id { get; }
    Sprite Apply(Sprite input, EffectArgs args);
}
