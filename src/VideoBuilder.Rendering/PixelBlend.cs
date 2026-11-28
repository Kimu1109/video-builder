using System.Runtime.Intrinsics;
using SkiaSharp;

namespace VideoBuilder.Rendering;

/// <summary>
/// CPU 描画で、SrcOver 以外の合成モードで絵を重ねる (設計書 22.53)。
/// <para>
/// SkiaSharp 3 以降の CPU 描画は、BGRA の描画先に SrcOver で描くとき以外 (合成モード・RGBA の描画先・単色の塗りも)
/// 汎用の描画経路 (ラスタパイプライン) を通り、1080p で 20 ms ほどかかる (2.88 の 10〜40 倍)。
/// SkSL (SKRuntimeEffect) も CPU では高速化されておらず、さらに遅い。
/// そこで、絵は作業用の BGRA の画像に SrcOver で描き (速い経路。拡大縮小・回転・不透明度もここで)、
/// 背景との合成は自前で行う。式は Skia の合成モードと同じ (乗算済みアルファの値で計算)。
/// </para>
/// 対象は 8 ビットの BGRA (乗算済みアルファ)。
/// </summary>
public static unsafe class PixelBlend
{
    /// <summary>テスト・計測用: 自前の合成を使わず、Skia で描く (以前の描き方と比べるため)。</summary>
    internal static bool Disabled { get; set; }

    // 合成の種類。1〜12 は RenderEngine の合成モードの番号と同じ (2 = 減算)。20 からはマスク・レイヤーを戻すときに使うもの
    internal const int OpDstIn = 20, OpDstOut = 21, OpSrcATop = 22, OpSrcOver = 23;

    /// <summary>自前で合成できる合成モード (RenderEngine の合成モードの番号)。0 (通常) は含まない。</summary>
    public static bool Supports(int blend) => blend is >= 1 and <= 12;

    /// <summary>Skia の合成モードを、自前の合成の種類にする。自前で合成できなければ -1。</summary>
    internal static int OpOf(SKBlendMode mode) => mode switch
    {
        SKBlendMode.Plus => 1, SKBlendMode.Multiply => 3, SKBlendMode.Screen => 4, SKBlendMode.Overlay => 5,
        SKBlendMode.Lighten => 6, SKBlendMode.Darken => 7, SKBlendMode.Luminosity => 8, SKBlendMode.Color => 9,
        SKBlendMode.ColorBurn => 10, SKBlendMode.HardLight => 11, SKBlendMode.Difference => 12,
        SKBlendMode.DstIn => OpDstIn, SKBlendMode.DstOut => OpDstOut, SKBlendMode.SrcATop => OpSrcATop, SKBlendMode.SrcOver => OpSrcOver,
        _ => -1,
    };

    // ---------------- CPU 描画の canvas と、その描画先のピクセル ----------------

    /// <summary>canvas の描画先 (BGRA・乗算済みアルファ) のピクセル。canvas の変換・クリップは、このピクセルの座標で表される。</summary>
    private sealed record Raster(IntPtr Pixels, int RowBytes, int Width, int Height);

    // SkiaSharp の SKCanvas からは描画先のピクセルを取り出せないので、CPU 描画で canvas を作るところで覚えておく
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKCanvas, Raster> Rasters = new();

    /// <summary>
    /// canvas が pixels (BGRA・乗算済みアルファ、width x height) に描く CPU 描画の canvas であることを覚えておく。
    /// 効果の中で合成モードを使う描画 (Draw・DrawImage・BeginLayer) が、自前で合成できるようになる。
    /// </summary>
    internal static void Register(SKCanvas canvas, IntPtr pixels, int rowBytes, int width, int height) =>
        Rasters.AddOrUpdate(canvas, new Raster(pixels, rowBytes, width, height));

    /// <summary>
    /// canvas の今の変換で sprite を (-Cx, -Cy) に描くのと同じ位置に、合成モード blend で重ねる。
    /// target は canvas の描画先のピクセル (BGRA)。描けたら true、条件に合わなければ false (呼び出し元が Skia で描く)。
    /// </summary>
    public static bool Draw(SKCanvas canvas, SKPixmap? target, Sprite sprite, SKPaint paint, int blend)
    {
        if (Disabled || target == null || target.ColorType != SKColorType.Bgra8888 || target.AlphaType != SKAlphaType.Premul || !Supports(blend)) return false;
        var raster = new Raster(target.GetPixels(), target.RowBytes, target.Width, target.Height);
        return DrawCore(canvas, raster, SKRect.Create(-sprite.Cx, -sprite.Cy, sprite.Width, sprite.Height), blend, lc =>
        {
            using var copy = paint.Clone();
            copy.BlendMode = SKBlendMode.SrcOver;
            lc.DrawImage(sprite.Image, -sprite.Cx, -sprite.Cy, copy);
        });
    }

    /// <summary>
    /// 効果の中で、paint の合成モードで描く。CPU 描画なら、draw が描くもの (localBounds の中) を作業用の画像に SrcOver で描いてから、
    /// 自前で合成する (draw には合成モードを SrcOver にした paint を渡す)。そうでなければ (GPU 描画など)、そのまま draw(c, paint) で描く。
    /// <para>
    /// DstIn は「描いていない所の背景を消す」ので、作業用の画像の範囲 (localBounds) は描くものとちょうど同じにすること
    /// (画像を整数の位置に描くなど)。ほかの合成モードは、範囲が広くても結果は変わらない。
    /// </para>
    /// </summary>
    public static void Draw(SKCanvas c, SKRect localBounds, SKPaint paint, Action<SKCanvas, SKPaint> draw)
    {
        int op = OpOf(paint.BlendMode);
        if (op >= 0 && op != OpSrcOver && !Disabled && Rasters.TryGetValue(c, out var raster))
        {
            using var copy = paint.Clone();
            copy.BlendMode = SKBlendMode.SrcOver;
            if (DrawCore(c, raster, localBounds, op, lc => draw(lc, copy))) return;
        }
        draw(c, paint);
    }

    /// <summary>画像を (x, y) に、paint の合成モードで描く (Draw の画像用)。paint の ImageFilter で絵が広がるときは Draw を使うこと。</summary>
    public static void DrawImage(SKCanvas c, SKImage image, float x, float y, SKPaint? paint)
    {
        if (paint == null) { c.DrawImage(image, x, y); return; }
        Draw(c, SKRect.Create(x, y, image.Width, image.Height), paint, (cv, p) => cv.DrawImage(image, x, y, p));
    }

    /// <summary>
    /// SaveLayer(合成モード) 〜 Restore の代わり。Canvas に描いたものを、Dispose で mode で重ねる。
    /// CPU 描画なら作業用の画像に描いて自前で合成し、そうでなければ Skia のレイヤーを使う (Canvas は c そのもの)。
    /// Canvas に加えた変換・クリップは、Dispose で元に戻る。
    /// </summary>
    public static Layer BeginLayer(SKCanvas c, SKRect? localBounds, SKBlendMode mode)
    {
        int op = OpOf(mode);
        if (op >= 0 && !Disabled && Rasters.TryGetValue(c, out var raster) && Region(c, raster, localBounds, 0) is { } region)
        {
            if (region.Width <= 0 || region.Height <= 0) return Layer.Empty();
            var info = new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            int rowBytes = info.Width * 4;
            long bytes = (long)rowBytes * info.Height;
            var buffer = PixelPool.Rent(bytes);
            var surface = SKSurface.Create(info, buffer, rowBytes);
            if (surface != null)
            {
                var lc = surface.Canvas;
                lc.Clear(SKColors.Transparent);
                lc.Translate(-region.Left, -region.Top);
                var m = c.TotalMatrix;
                lc.Concat(ref m);
                Register(lc, buffer, rowBytes, region.Width, region.Height);
                return new Layer(lc, () =>
                {
                    lc.Flush();
                    var dst = (byte*)raster.Pixels + (long)region.Top * raster.RowBytes + region.Left * 4;
                    Composite((byte*)buffer, rowBytes, dst, raster.RowBytes, region.Width, region.Height, op);
                    surface.Dispose();
                    PixelPool.Return(buffer, bytes);
                });
            }
            PixelPool.Return(buffer, bytes);
        }
        // Skia のレイヤー
        var paint = new SKPaint { BlendMode = mode };
        if (localBounds is { } b) c.SaveLayer(b, paint); else c.SaveLayer(paint);
        return new Layer(c, () => { c.Restore(); paint.Dispose(); });
    }

    /// <summary>BeginLayer の結果。Canvas に描き、Dispose で重ねる。</summary>
    public sealed class Layer : IDisposable
    {
        private Action? _end;
        private readonly SKSurface? _empty;
        public SKCanvas Canvas { get; }
        internal Layer(SKCanvas canvas, Action end) { Canvas = canvas; _end = end; }
        private Layer(SKSurface empty) { _empty = empty; Canvas = empty.Canvas; }

        /// <summary>画面の外: 描いても何も起きない canvas。</summary>
        internal static Layer Empty() => new(SKSurface.CreateNull(1, 1));

        public void Dispose()
        {
            var end = _end; _end = null;
            end?.Invoke();
            _empty?.Dispose();
        }
    }

    /// <summary>
    /// canvas の今の変換で localBounds (null なら今のクリップの範囲) を描くとき、描画先のどの範囲にかかるか。
    /// クリップが矩形でない・遠近の変換で範囲を求められない・広すぎるときは null (自前では合成できない)。pad は周りに足す画素数。
    /// </summary>
    private static SKRectI? Region(SKCanvas canvas, Raster target, SKRect? localBounds, int pad)
    {
        // 矩形でないクリップ (マスクなど) は、作業用の画像に写せないので Skia に任せる
        if (!canvas.IsClipRect) return null;
        var clip = SKRectI.Intersect(canvas.DeviceClipBounds, new SKRectI(0, 0, target.Width, target.Height));
        SKRectI region;
        if (localBounds is not { } local) region = clip;
        else
        {
            var m = canvas.TotalMatrix;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var c in new[] { new SKPoint(local.Left, local.Top), new SKPoint(local.Right, local.Top), new SKPoint(local.Right, local.Bottom), new SKPoint(local.Left, local.Bottom) })
            {
                // 遠近のある変換でカメラの後ろに回る角があると範囲を求められない
                float w = m.Persp0 * c.X + m.Persp1 * c.Y + m.Persp2;
                if (w <= 1e-3f) return null;
                var q = m.MapPoint(c);
                minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
            }
            if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY)) return null;
            // 整数の位置にちょうど描く絵は、その範囲だけにする (DstIn が、描いていない隣の画素を消さないように)
            static int Lo(float v) => (int)MathF.Floor(v + 1e-3f);
            static int Hi(float v) => (int)MathF.Ceiling(v - 1e-3f);
            region = SKRectI.Intersect(new SKRectI(Lo(minX) - pad, Lo(minY) - pad, Hi(maxX) + pad, Hi(maxY) + pad), clip);
        }
        if ((long)Math.Max(0, region.Width) * Math.Max(0, region.Height) > 64L * 1024 * 1024) return null;
        return region;
    }

    /// <summary>draw が描くもの (localBounds の中) を作業用の画像に描いて、target に op で合成する。自前で合成できなければ false。</summary>
    private static bool DrawCore(SKCanvas canvas, Raster target, SKRect localBounds, int op, Action<SKCanvas> draw)
    {
        // DstIn のときは範囲を広げない (広げた所の背景が消える)。ほかはアンチエイリアスの分だけ広げる
        if (Region(canvas, target, localBounds, op == OpDstIn ? 0 : 1) is not { } region) return false;
        if (region.Width <= 0 || region.Height <= 0) return true;   // 画面の外
        var info = new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int rowBytes = info.Width * 4;
        long bytes = (long)rowBytes * info.Height;
        var layer = PixelPool.Rent(bytes);
        try
        {
            // 同じ範囲の作業用の画像に SrcOver で描く (不透明度・補間・アンチエイリアスは paint のまま)
            using (var layerSurface = SKSurface.Create(info, layer, rowBytes))
            {
                if (layerSurface == null) return false;
                var lc = layerSurface.Canvas;
                lc.Clear(SKColors.Transparent);
                lc.Translate(-region.Left, -region.Top);
                var m = canvas.TotalMatrix;
                lc.Concat(ref m);
                draw(lc);
                lc.Flush();
            }
            var dst = (byte*)target.Pixels + (long)region.Top * target.RowBytes + region.Left * 4;
            Composite((byte*)layer, rowBytes, dst, target.RowBytes, region.Width, region.Height, op);
            return true;
        }
        finally { PixelPool.Return(layer, bytes); }
    }

    /// <summary>
    /// 整数の合成に使う SIMD の幅 (ビット)。256 (x64 の AVX2)・128 (ARM64 の NEON や AVX2 のない x64)・0 (SIMD なし)。
    /// ARM64 では Vector256 が高速化されない (IsHardwareAccelerated が false で、ソフトウェアの実装になる) ので、128 ビットで計算する。
    /// </summary>
    internal static readonly int VectorBits = Vector256.IsHardwareAccelerated ? 256 : Vector128.IsHardwareAccelerated ? 128 : 0;

    /// <summary>src (作業用の画像) を dst に合成の種類 op で重ねる。どちらも BGRA (乗算済みアルファ)。</summary>
    public static void Composite(byte* src, int srcRowBytes, byte* dst, int dstRowBytes, int width, int height, int blend) =>
        Composite(src, srcRowBytes, dst, dstRowBytes, width, height, blend, VectorBits);

    /// <summary>テスト用: SIMD の幅 (256・128・0) を指定して合成する (x64 の上でも ARM64 と同じ 128 ビットの計算を確かめるため)。</summary>
    internal static void Composite(byte* src, int srcRowBytes, byte* dst, int dstRowBytes, int width, int height, int blend, int vectorBits)
    {
        PixelOps.ForBands(width, height, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                byte* s = src + (long)y * srcRowBytes, d = dst + (long)y * dstRowBytes;
                switch (blend)
                {
                    case 1: RowPlus(s, d, width, vectorBits); break;
                    case 2: RowSubtract(s, d, width, vectorBits); break;
                    case 3 or 4 or 6 or 7 or 12 or OpDstIn or OpDstOut or OpSrcATop or OpSrcOver: RowSeparable(s, d, width, blend, vectorBits); break;
                    default: RowFloat(s, d, width, blend); break;
                }
            }
        });
    }

    // ---------------- 整数で計算するもの (256 ビットで 8 画素ずつ、128 ビットで 4 画素ずつ) ----------------

    /// <summary>加算 (Plus): min(s + d, 1)。アルファも同じ。</summary>
    private static void RowPlus(byte* s, byte* d, int width, int vectorBits)
    {
        int n = width * 4, i = 0;
        if (vectorBits == 256)
            for (; i + 32 <= n; i += 32) Vector256.Store(Vector256.AddSaturate(Vector256.Load(s + i), Vector256.Load(d + i)), d + i);
        else if (vectorBits == 128)
            for (; i + 16 <= n; i += 16) Vector128.Store(Vector128.AddSaturate(Vector128.Load(s + i), Vector128.Load(d + i)), d + i);
        for (; i < n; i++) d[i] = (byte)Math.Min(255, s[i] + d[i]);
    }

    /// <summary>
    /// 減算: 以前の描き方 (背景を Difference で反転 → 加算 → もう一度反転) と同じ結果。
    /// 色は max(d - s, 0)、アルファは 1 (反転で不透明になる)。
    /// </summary>
    private static void RowSubtract(byte* s, byte* d, int width, int vectorBits)
    {
        int n = width * 4, i = 0;
        if (vectorBits == 256)
        {
            var alpha = Vector256.Create(0xFF000000u).AsByte();
            for (; i + 32 <= n; i += 32)
                Vector256.Store(Vector256.SubtractSaturate(Vector256.Load(d + i), Vector256.Load(s + i)) | alpha, d + i);
        }
        else if (vectorBits == 128)
        {
            var alpha = Vector128.Create(0xFF000000u).AsByte();
            for (; i + 16 <= n; i += 16)
                Vector128.Store(Vector128.SubtractSaturate(Vector128.Load(d + i), Vector128.Load(s + i)) | alpha, d + i);
        }
        for (; i < n; i += 4)
        {
            d[i] = (byte)Math.Max(0, d[i] - s[i]); d[i + 1] = (byte)Math.Max(0, d[i + 1] - s[i + 1]); d[i + 2] = (byte)Math.Max(0, d[i + 2] - s[i + 2]);
            d[i + 3] = 255;
        }
    }

    /// <summary>x * y / 255 (四捨五入)。16 ビットで計算する。</summary>
    private static Vector256<ushort> Mul(Vector256<ushort> x, Vector256<ushort> y)
    {
        var t = x * y + Vector256.Create((ushort)128);
        return (t + (t >>> 8)) >>> 8;
    }

    private static int Mul(int x, int y) { int t = x * y + 128; return (t + (t >> 8)) >> 8; }

    private static readonly Vector256<ushort> AlphaLanes = Vector256.Create((ushort)0, 0, 0, 0xFFFF, 0, 0, 0, 0xFFFF, 0, 0, 0, 0xFFFF, 0, 0, 0, 0xFFFF);
    private static readonly Vector256<ushort> AlphaIndex = Vector256.Create((ushort)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);

    /// <summary>各画素のアルファを、その画素の 4 成分すべてに並べる。</summary>
    private static Vector256<ushort> Alpha(Vector256<ushort> v) => Vector256.Shuffle(v, AlphaIndex);

    /// <summary>乗算・スクリーン・比較 (明)・比較 (暗)・差の絶対値・マスク (DstIn・DstOut・SrcATop)・SrcOver。どれも成分ごとに独立して計算できる。</summary>
    private static void RowSeparable(byte* s, byte* d, int width, int blend, int vectorBits)
    {
        int n = width * 4, i = 0;
        if (vectorBits == 256)
            for (; i + 32 <= n; i += 32)
            {
                var (sl, sh) = Vector256.Widen(Vector256.Load(s + i));
                var (dl, dh) = Vector256.Widen(Vector256.Load(d + i));
                Vector256.Store(Vector256.Narrow(Separable(sl, dl, blend), Separable(sh, dh, blend)), d + i);
            }
        else if (vectorBits == 128)
            for (; i + 16 <= n; i += 16)
            {
                var (sl, sh) = Vector128.Widen(Vector128.Load(s + i));
                var (dl, dh) = Vector128.Widen(Vector128.Load(d + i));
                Vector128.Store(Vector128.Narrow(Separable(sl, dl, blend), Separable(sh, dh, blend)), d + i);
            }
        for (; i < n; i += 4)
        {
            int sa = s[i + 3], da = d[i + 3];
            for (int k = 0; k < 4; k++) d[i + k] = (byte)Separable(s[i + k], d[i + k], sa, da, blend, k == 3);
        }
    }

    private static Vector256<ushort> Separable(Vector256<ushort> s, Vector256<ushort> d, int blend)
    {
        var sa = Alpha(s); var da = Alpha(d);
        var one = Vector256.Create((ushort)255);
        Vector256<ushort> r = blend switch
        {
            // 乗算: s(1 - da) + d(1 - sa) + sd
            3 => Mul(s, one - da) + Mul(d, one - sa) + Mul(s, d),
            // スクリーン: s + d - sd
            4 => s + d - Mul(s, d),
            // 比較 (明): s + d - min(s·da, d·sa)
            6 => s + d - Vector256.Min(Mul(s, da), Mul(d, sa)),
            // 比較 (暗): s + d - max(s·da, d·sa)
            7 => s + d - Vector256.Max(Mul(s, da), Mul(d, sa)),
            // 差の絶対値: s + d - 2·min(s·da, d·sa)。アルファは sa + da - sa·da
            12 => Vector256.ConditionalSelect(AlphaLanes, s + d - Mul(s, d), s + d - (Vector256.Min(Mul(s, da), Mul(d, sa)) << 1)),
            // DstIn: d·sa (絵のある所だけ背景を残す)
            OpDstIn => Mul(d, sa),
            // DstOut: d(1 - sa) (絵のある所の背景を抜く)
            OpDstOut => Mul(d, one - sa),
            // SrcATop: s·da + d(1 - sa) (背景のある所にだけ絵を重ねる。アルファは背景のまま)
            OpSrcATop => Mul(s, da) + Mul(d, one - sa),
            // SrcOver: s + d(1 - sa)
            _ => s + Mul(d, one - sa),
        };
        return Vector256.Min(r, one);
    }

    // ---- 128 ビット版 (ARM64 の NEON・AVX2 のない x64)。式は 256 ビット版と同じ ----

    /// <summary>x * y / 255 (四捨五入)。16 ビットで計算する。</summary>
    private static Vector128<ushort> Mul(Vector128<ushort> x, Vector128<ushort> y)
    {
        var t = x * y + Vector128.Create((ushort)128);
        return (t + (t >>> 8)) >>> 8;
    }

    private static readonly Vector128<ushort> AlphaLanes128 = Vector128.Create((ushort)0, 0, 0, 0xFFFF, 0, 0, 0, 0xFFFF);
    private static readonly Vector128<ushort> AlphaIndex128 = Vector128.Create((ushort)3, 3, 3, 3, 7, 7, 7, 7);

    /// <summary>各画素のアルファを、その画素の 4 成分すべてに並べる。</summary>
    private static Vector128<ushort> Alpha(Vector128<ushort> v) => Vector128.Shuffle(v, AlphaIndex128);

    private static Vector128<ushort> Separable(Vector128<ushort> s, Vector128<ushort> d, int blend)
    {
        var sa = Alpha(s); var da = Alpha(d);
        var one = Vector128.Create((ushort)255);
        Vector128<ushort> r = blend switch
        {
            3 => Mul(s, one - da) + Mul(d, one - sa) + Mul(s, d),
            4 => s + d - Mul(s, d),
            6 => s + d - Vector128.Min(Mul(s, da), Mul(d, sa)),
            7 => s + d - Vector128.Max(Mul(s, da), Mul(d, sa)),
            12 => Vector128.ConditionalSelect(AlphaLanes128, s + d - Mul(s, d), s + d - (Vector128.Min(Mul(s, da), Mul(d, sa)) << 1)),
            OpDstIn => Mul(d, sa),
            OpDstOut => Mul(d, one - sa),
            OpSrcATop => Mul(s, da) + Mul(d, one - sa),
            _ => s + Mul(d, one - sa),
        };
        return Vector128.Min(r, one);
    }

    private static int Separable(int s, int d, int sa, int da, int blend, bool alpha)
    {
        int r = blend switch
        {
            3 => Mul(s, 255 - da) + Mul(d, 255 - sa) + Mul(s, d),
            4 => s + d - Mul(s, d),
            6 => s + d - Math.Min(Mul(s, da), Mul(d, sa)),
            7 => s + d - Math.Max(Mul(s, da), Mul(d, sa)),
            12 => alpha ? s + d - Mul(s, d) : s + d - 2 * Math.Min(Mul(s, da), Mul(d, sa)),
            OpDstIn => Mul(d, sa),
            OpDstOut => Mul(d, 255 - sa),
            OpSrcATop => Mul(s, da) + Mul(d, 255 - sa),
            _ => s + Mul(d, 255 - sa),
        };
        return Math.Clamp(r, 0, 255);
    }

    // ---------------- 浮動小数点で計算するもの (1 画素ずつ) ----------------

    /// <summary>オーバーレイ・ハードライト・焼き込みカラー・カラー・輝度。Skia のラスタパイプラインと同じ式。</summary>
    private static void RowFloat(byte* s, byte* d, int width, int blend)
    {
        const float k = 1f / 255;
        for (int x = 0; x < width; x++, s += 4, d += 4)
        {
            if (s[3] == 0) continue;   // 重ねる絵がない (どの合成モードでも背景のまま)
            float sb = s[0] * k, sg = s[1] * k, sr = s[2] * k, sa = s[3] * k;
            float db = d[0] * k, dg = d[1] * k, dr = d[2] * k, da = d[3] * k;
            float rr, rg, rb;
            switch (blend)
            {
                case 5:   // オーバーレイ (背景で場合分けするハードライト)
                    rr = Overlay(sr, dr, sa, da); rg = Overlay(sg, dg, sa, da); rb = Overlay(sb, db, sa, da);
                    break;
                case 11:  // ハードライト
                    rr = HardLight(sr, dr, sa, da); rg = HardLight(sg, dg, sa, da); rb = HardLight(sb, db, sa, da);
                    break;
                case 10:  // 焼き込みカラー
                    rr = ColorBurn(sr, dr, sa, da); rg = ColorBurn(sg, dg, sa, da); rb = ColorBurn(sb, db, sa, da);
                    break;
                case 9:   // カラー: 絵の色相と彩度、背景の輝度
                    NonSeparable(sr * da, sg * da, sb * da, Lum(dr, dg, db) * sa, sa * da, out rr, out rg, out rb);
                    rr += dr * (1 - sa) + sr * (1 - da); rg += dg * (1 - sa) + sg * (1 - da); rb += db * (1 - sa) + sb * (1 - da);
                    break;
                default:  // 8: 輝度: 背景の色相と彩度、絵の輝度
                    NonSeparable(dr * sa, dg * sa, db * sa, Lum(sr, sg, sb) * da, sa * da, out rr, out rg, out rb);
                    rr += dr * (1 - sa) + sr * (1 - da); rg += dg * (1 - sa) + sg * (1 - da); rb += db * (1 - sa) + sb * (1 - da);
                    break;
            }
            float ra = sa + da - sa * da;
            d[0] = ToByte(rb); d[1] = ToByte(rg); d[2] = ToByte(rr); d[3] = ToByte(ra);
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255 + 0.5f), 0, 255);

    private static float HardLight(float s, float d, float sa, float da) =>
        s * (1 - da) + d * (1 - sa) + (2 * s <= sa ? 2 * s * d : sa * da - 2 * (da - d) * (sa - s));

    private static float Overlay(float s, float d, float sa, float da) =>
        s * (1 - da) + d * (1 - sa) + (2 * d <= da ? 2 * s * d : sa * da - 2 * (da - d) * (sa - s));

    private static float ColorBurn(float s, float d, float sa, float da)
    {
        if (d == da) return d + s * (1 - da);
        if (s == 0) return d * (1 - sa);
        return sa * (da - MathF.Min(da, (da - d) * sa / s)) + s * (1 - da) + d * (1 - sa);
    }

    private static float Lum(float r, float g, float b) => 0.30f * r + 0.59f * g + 0.11f * b;

    /// <summary>(r, g, b) の輝度を lum にそろえ (set_lum)、0〜a に収める (clip_color)。</summary>
    private static void NonSeparable(float r, float g, float b, float lum, float a, out float rr, out float rg, out float rb)
    {
        float diff = lum - Lum(r, g, b);
        r += diff; g += diff; b += diff;
        float mn = MathF.Min(r, MathF.Min(g, b)), mx = MathF.Max(r, MathF.Max(g, b)), l = Lum(r, g, b);
        float Clip(float c)
        {
            if (mn < 0 && l - mn != 0) c = l + (c - l) * l / (l - mn);
            if (mx > a && mx - l != 0) c = l + (c - l) * (a - l) / (mx - l);
            return MathF.Max(c, 0);
        }
        rr = Clip(r); rg = Clip(g); rb = Clip(b);
    }
}
