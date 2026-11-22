using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// オブジェクト 1 つ分の絵。(Cx, Cy) は画像の中でオブジェクトの中心にあたる位置。
/// Scale は「画像の 1 ピクセルが何ピクセルぶんにあたるか」(プロキシなど、小さく作った絵を元の大きさで描くときに 1 より大きくなる)。
/// </summary>
public readonly record struct Sprite(SKImage Image, float Cx, float Cy, float Scale)
{
    /// <summary>倍率 1 の絵。(以前からあるコンストラクタ。作成済みのプラグインがそのまま動くよう残している)</summary>
    public Sprite(SKImage image, float cx, float cy) : this(image, cx, cy, 1) { }

    public static Sprite Centered(SKImage image) => new(image, image.Width / 2f, image.Height / 2f);
    public int Width => Image.Width;
    public int Height => Image.Height;
}

/// <summary>1 フレームを描いている間の状態。</summary>
public sealed class RenderContext
{
    public required Project Project { get; init; }
    public required int Frame { get; init; }
    /// <summary>プレビュー用の描画か。true のとき、効果は見た目がほぼ変わらない範囲で速い近似を使ってよい。</summary>
    public bool Draft { get; init; }
    /// <summary>GPU で描くときの Skia コンテキスト。null なら CPU で描く。</summary>
    public GRContext? Gpu { get; init; }
    /// <summary>画像ファイルを読む (キャッシュ付き。返した画像は破棄しない)。効果が別のファイルを使うとき (画像ファイル合成など) に使う。</summary>
    public Func<string, SKImage?>? LoadImage { get; init; }
    /// <summary>
    /// 動画ファイルのフレームを読む (キー, パス, 秒, ループ)。キーはデコーダを使い分けるための文字列。返した画像は破棄しない。
    /// </summary>
    public Func<string, string, double, bool, SKImage?>? LoadVideoFrame { get; init; }

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

    /// <summary>描いているフレーム (タイムライン上の番号)。</summary>
    public int Frame => Context.Frame;
    /// <summary>オブジェクトの先頭からの時間 (秒)。</summary>
    public double Time => Context.Project.FrameToSeconds(Context.Frame - Object.Start);
    /// <summary>
    /// プレビュー用の描画か。true のときは、見た目がほぼ変わらない範囲で速い近似を使ってよい (出力では false)。
    /// </summary>
    public bool IsDraft => Context.Draft;

    public float Track(string key) => (float)Effect.Eval(key, Bounds, Context.Frame);
    public bool Check(string key) => Effect.GetBool(key);
    public SKColor Color(string key) => SKColor.TryParse(Effect.GetString(key), out var c) ? c : SKColors.White;
}

/// <summary>効果の性質。エンジンはこれを見て、速い経路を選ぶ。</summary>
[Flags]
public enum EffectFlags
{
    None = 0,
    /// <summary>
    /// 結果が「入力の絵」と「パラメータの今の値」だけで決まる (時刻・乱数・外部の状態を使わない)。
    /// エンジンは、入力とパラメータが前のフレームと同じなら、前回の結果を使い回す (Apply を呼ばない)。
    /// </summary>
    Deterministic = 1,
}

/// <summary>
/// 絵を受け取って、新しい絵を返す効果。いちばん自由だが、いちばん遅い経路。
/// 色を変えるだけなら IColorMatrixEffect、ピクセルを書き換えるだけなら IPixelEffect を使うと速い。
/// </summary>
public interface IVideoEffect
{
    string Id { get; }
    /// <summary>
    /// 新しく作った画像は args.Context.Own() に渡す (フレームを描き終えたら破棄される)。
    /// 作業用の描画先は args.Context.CreateSurface (GPU 描画中は GPU 上に作られる) か
    /// PixelOps.DrawParallel (CPU で横帯に分けて並列に描く) を使う。
    /// </summary>
    Sprite Apply(Sprite input, EffectArgs args);
    /// <summary>効果の性質。既定は None (毎フレーム Apply を呼ぶ)。</summary>
    EffectFlags Flags => EffectFlags.None;
}

/// <summary>
/// ピクセルを直接書き換える効果 (絵の大きさは変えない)。
/// エンジンが BGRA (乗算済みアルファ) のバッファを用意して渡す:
/// 画面全体 (調整レイヤー) にかけるときは、画面のバッファをそのまま (複製なしで) 渡す。
/// オブジェクトにかけるときは、使い回しのバッファに 1 回写して渡す。GPU 描画中は読み戻して渡す。
/// バッファの処理を並列にするには PixelBuffer.ParallelRows を使う。
/// </summary>
public interface IPixelEffect
{
    string Id { get; }
    void Process(PixelBuffer pixels, EffectArgs args);
    /// <summary>既定は Deterministic (時刻や乱数を使うなら None にする)。</summary>
    EffectFlags Flags => EffectFlags.Deterministic;
}

/// <summary>
/// 色の変換だけを行う効果。4x5 のカラーマトリクスを返すだけでよい (ピクセルの処理はエンジンが行う)。
/// CPU 描画では並列の表引き / 固定小数点、GPU 描画ではカラーフィルタで処理する。
/// 続けて並んだ色の変換 (組み込みの色調補正・単色化を含む) は、行列を掛け合わせて 1 回で処理する。
/// </summary>
public interface IColorMatrixEffect
{
    string Id { get; }
    /// <summary>
    /// 今のフレームでの行列 (RGBA の順に 4 行 x 5 列。5 列目は 0〜1 の平行移動)。変換しないときは null。
    /// アルファの行 (4 行目) は 0, 0, 0, 1, 0 にすること (それ以外は遅い経路になる)。
    /// </summary>
    float[]? GetMatrix(EffectArgs args);
    /// <summary>既定は Deterministic (行列が時刻や乱数で変わるなら None にする)。</summary>
    EffectFlags Flags => EffectFlags.Deterministic;
}

/// <summary>ピクセルのバッファ (BGRA・乗算済みアルファ)。IPixelEffect に渡される。Process の中でだけ使う。</summary>
public readonly unsafe struct PixelBuffer
{
    public IntPtr Pointer { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>1 行のバイト数 (Width * 4 以上)。</summary>
    public int RowBytes { get; }

    public PixelBuffer(IntPtr pointer, int width, int height, int rowBytes)
    {
        Pointer = pointer; Width = width; Height = height; RowBytes = rowBytes;
    }

    /// <summary>y 行目のピクセル (1 ピクセル = 4 バイト: B, G, R, A)。</summary>
    public Span<byte> Row(int y) => new((byte*)Pointer + (long)y * RowBytes, Width * 4);

    /// <summary>y 行目のピクセルを 32bit 単位で (下位から B, G, R, A)。</summary>
    public Span<uint> RowPixels(int y) => new((byte*)Pointer + (long)y * RowBytes, Width);

    /// <summary>
    /// 行の範囲 [y0, y1) ごとに body を呼ぶ。大きなバッファでは複数のスレッドに分ける (行どうしが独立した処理に使う)。
    /// </summary>
    public void ParallelRows(Action<int, int> body) => PixelRows.ForBands(Width, Height, body);
}

/// <summary>PixelBuffer.ParallelRows の実体 (エンジンが差し替える)。</summary>
public static class PixelRows
{
    /// <summary>エンジンの並列化の仕組み。既定は 1 スレッドで呼ぶだけ。</summary>
    public static Action<int, int, Action<int, int>> ForBands { get; set; } = (_, h, body) => body(0, h);
}
