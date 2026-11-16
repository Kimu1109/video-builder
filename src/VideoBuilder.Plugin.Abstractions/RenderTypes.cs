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
    /// <summary>GPU で描くときの Skia コンテキスト。null なら CPU で描く。</summary>
    public GRContext? Gpu { get; init; }

    /// <summary>作業用の描画先を作る。GPU 描画中は GPU 上に作るので、フィルタも GPU で処理される。</summary>
    public SKSurface? CreateSurface(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (Gpu != null)
        {
            var gpu = SKSurface.Create(Gpu, false, new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (gpu != null) return gpu;
        }
        return SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
    }
    /// <summary>
    /// 今描いているオブジェクトの描画に足す補正 (スクリプト制御などの効果が書き込む)。オブジェクトごとに初期化される。
    /// </summary>
    public DrawModifiers Modifiers = DrawModifiers.None;

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

/// <summary>標準描画の値に足す (Zoom と Alpha は掛ける) 補正。</summary>
public struct DrawModifiers
{
    public float Ox, Oy, Oz, Rx, Ry, Rz;
    public float Zoom, Alpha;
    public static DrawModifiers None => new() { Zoom = 1, Alpha = 1 };
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
