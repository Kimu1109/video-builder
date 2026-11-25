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

    /// <summary>自前で合成できる合成モード (RenderEngine の合成モードの番号)。0 (通常) は含まない。</summary>
    public static bool Supports(int blend) => blend is >= 1 and <= 12;

    /// <summary>
    /// canvas の今の変換で sprite を (-Cx, -Cy) に描くのと同じ位置に、合成モード blend で重ねる。
    /// target は canvas の描画先のピクセル (BGRA)。描けたら true、条件に合わなければ false (呼び出し元が Skia で描く)。
    /// </summary>
    public static bool Draw(SKCanvas canvas, SKPixmap? target, Sprite sprite, SKPaint paint, int blend)
    {
        if (Disabled || target == null || target.ColorType != SKColorType.Bgra8888 || target.AlphaType != SKAlphaType.Premul || !Supports(blend)) return false;
        // 矩形でないクリップ (マスクなど) は、作業用の画像に写せないので Skia に任せる
        if (!canvas.IsClipRect) return false;
        var m = canvas.TotalMatrix;
        var corners = new[]
        {
            new SKPoint(-sprite.Cx, -sprite.Cy), new SKPoint(sprite.Width - sprite.Cx, -sprite.Cy),
            new SKPoint(sprite.Width - sprite.Cx, sprite.Height - sprite.Cy), new SKPoint(-sprite.Cx, sprite.Height - sprite.Cy),
        };
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in corners)
        {
            // 遠近のある変換でカメラの後ろに回る角があると範囲を求められない
            float w = m.Persp0 * c.X + m.Persp1 * c.Y + m.Persp2;
            if (w <= 1e-3f) return false;
            var q = m.MapPoint(c);
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }
        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY)) return false;
        var region = SKRectI.Intersect(
            new SKRectI((int)MathF.Floor(minX) - 1, (int)MathF.Floor(minY) - 1, (int)MathF.Ceiling(maxX) + 1, (int)MathF.Ceiling(maxY) + 1),
            SKRectI.Intersect(canvas.DeviceClipBounds, new SKRectI(0, 0, target.Width, target.Height)));
        if (region.Width <= 0 || region.Height <= 0) return true;   // 画面の外
        if ((long)region.Width * region.Height > 64L * 1024 * 1024) return false;

        var info = new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int rowBytes = info.Width * 4;
        long bytes = (long)rowBytes * info.Height;
        var layer = PixelPool.Rent(bytes);
        try
        {
            // 変換した絵を、同じ範囲の作業用の画像に SrcOver で描く (不透明度・補間・アンチエイリアスは paint のまま)
            using (var layerSurface = SKSurface.Create(info, layer, rowBytes))
            {
                if (layerSurface == null) return false;
                var lc = layerSurface.Canvas;
                lc.Clear(SKColors.Transparent);
                lc.Translate(-region.Left, -region.Top);
                lc.Concat(ref m);
                using var copy = paint.Clone();
                copy.BlendMode = SKBlendMode.SrcOver;
                lc.DrawImage(sprite.Image, -sprite.Cx, -sprite.Cy, copy);
                lc.Flush();
            }
            var dst = (byte*)target.GetPixels() + (long)region.Top * target.RowBytes + region.Left * 4;
            Composite((byte*)layer, rowBytes, dst, target.RowBytes, region.Width, region.Height, blend);
            return true;
        }
        finally { PixelPool.Return(layer, bytes); }
    }

    /// <summary>src (作業用の画像) を dst に合成モード blend で重ねる。どちらも BGRA (乗算済みアルファ)。</summary>
    public static void Composite(byte* src, int srcRowBytes, byte* dst, int dstRowBytes, int width, int height, int blend)
    {
        PixelOps.ForBands(width, height, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                byte* s = src + (long)y * srcRowBytes, d = dst + (long)y * dstRowBytes;
                switch (blend)
                {
                    case 1: RowPlus(s, d, width); break;
                    case 2: RowSubtract(s, d, width); break;
                    case 3 or 4 or 6 or 7 or 12: RowSeparable(s, d, width, blend); break;
                    default: RowFloat(s, d, width, blend); break;
                }
            }
        });
    }

    // ---------------- 整数で計算するもの (8 画素ずつ) ----------------

    /// <summary>加算 (Plus): min(s + d, 1)。アルファも同じ。</summary>
    private static void RowPlus(byte* s, byte* d, int width)
    {
        int n = width * 4, i = 0;
        for (; i + 32 <= n; i += 32) Vector256.Store(Vector256.AddSaturate(Vector256.Load(s + i), Vector256.Load(d + i)), d + i);
        for (; i < n; i++) d[i] = (byte)Math.Min(255, s[i] + d[i]);
    }

    /// <summary>
    /// 減算: 以前の描き方 (背景を Difference で反転 → 加算 → もう一度反転) と同じ結果。
    /// 色は max(d - s, 0)、アルファは 1 (反転で不透明になる)。
    /// </summary>
    private static void RowSubtract(byte* s, byte* d, int width)
    {
        var alpha = Vector256.Create(0xFF000000u).AsByte();
        int n = width * 4, i = 0;
        for (; i + 32 <= n; i += 32)
            Vector256.Store(Vector256.SubtractSaturate(Vector256.Load(d + i), Vector256.Load(s + i)) | alpha, d + i);
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

    /// <summary>乗算・スクリーン・比較 (明)・比較 (暗)・差の絶対値。どれも成分ごとに独立して計算できる。</summary>
    private static void RowSeparable(byte* s, byte* d, int width, int blend)
    {
        int n = width * 4, i = 0;
        for (; i + 32 <= n; i += 32)
        {
            var (sl, sh) = Vector256.Widen(Vector256.Load(s + i));
            var (dl, dh) = Vector256.Widen(Vector256.Load(d + i));
            Vector256.Store(Vector256.Narrow(Separable(sl, dl, blend), Separable(sh, dh, blend)), d + i);
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
            _ => Vector256.ConditionalSelect(AlphaLanes, s + d - Mul(s, d), s + d - (Vector256.Min(Mul(s, da), Mul(d, sa)) << 1)),
        };
        return Vector256.Min(r, one);
    }

    private static int Separable(int s, int d, int sa, int da, int blend, bool alpha)
    {
        int r = blend switch
        {
            3 => Mul(s, 255 - da) + Mul(d, 255 - sa) + Mul(s, d),
            4 => s + d - Mul(s, d),
            6 => s + d - Math.Min(Mul(s, da), Mul(d, sa)),
            7 => s + d - Math.Max(Mul(s, da), Mul(d, sa)),
            _ => alpha ? s + d - Mul(s, d) : s + d - 2 * Math.Min(Mul(s, da), Mul(d, sa)),
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
