using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SkiaSharp;

namespace VideoBuilder.Rendering;

/// <summary>
/// CPU の高品質な拡大 (バイキュービック、Mitchell: B = C = 1/3。Skia の高品質と同じ種類)。
/// 出力では、小さい動画を大きなプロジェクトに拡大して映すことがあり、Skia の拡大は重い (4K で 1 フレーム 90 ms ほど)。
/// 回転も遠近もない拡大だけを、横 → 縦の 2 回に分けて、1 ピクセルの 4 成分をまとめて SIMD で、行ごとに複数スレッドで計算する。
/// </summary>
public static unsafe class PixelResample
{
    private const int Shift = 14;            // 重みの固定小数点の桁 (1.0 = 16384)
    private const int One = 1 << Shift;

    /// <summary>Mitchell のフィルタ (B = C = 1/3)。|x| &lt; 2 の範囲で 0 以外。</summary>
    private static double Mitchell(double x)
    {
        const double B = 1.0 / 3, C = 1.0 / 3;
        x = Math.Abs(x);
        if (x < 1) return ((12 - 9 * B - 6 * C) * x * x * x + (-18 + 12 * B + 6 * C) * x * x + (6 - 2 * B)) / 6;
        if (x < 2) return ((-B - 6 * C) * x * x * x + (6 * B + 30 * C) * x * x + (-12 * B - 48 * C) * x + (8 * B + 24 * C)) / 6;
        return 0;
    }

    /// <summary>出力の i 番目 (0 〜 count-1) の、元の絵の 4 つの位置と重み (合計 One)。端は元の絵の端で止める。</summary>
    private static (int[] Index, int[] Weight) Taps(int count, double first, double step, int size)
    {
        var index = new int[count * 4];
        var weight = new int[count * 4];
        for (int i = 0; i < count; i++)
        {
            double u = first + i * step;          // 元の絵での位置 (ピクセルの中心が整数)
            int u0 = (int)Math.Floor(u);
            double f = u - u0;
            int sum = 0;
            for (int t = 0; t < 4; t++)
            {
                int w = (int)Math.Round(Mitchell(f - (t - 1)) * One);
                index[i * 4 + t] = Math.Clamp(u0 + t - 1, 0, size - 1);
                weight[i * 4 + t] = w;
                sum += w;
            }
            weight[i * 4 + 1] += One - sum;       // 丸めのずれを近い方の重みで吸収する (明るさを変えない)
        }
        return (index, weight);
    }

    /// <summary>
    /// 絵を拡大して canvas に描く (CPU 描画・回転なしの拡大のときだけ)。描けなければ false (呼び出し側が Skia で描く)。
    /// paint の不透明度・合成方法はそのまま使う (拡大した絵を、等倍で Skia に描かせる)。
    /// </summary>
    internal static bool DrawEnlarged(SKCanvas canvas, SKPixmap target, SKImage image, float left, float top, SKPaint paint)
    {
        var m = canvas.TotalMatrix;
        if (m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0 || m.Persp2 != 1) return false;
        float sx = m.ScaleX, sy = m.ScaleY;
        if (sx < 1 || sy < 1 || (sx == 1 && sy == 1)) return false;   // 拡大だけ (縮小・反転は Skia に任せる)
        using var src = image.PeekPixels();
        if (src == null || src.ColorType != SKColorType.Bgra8888 || src.AlphaType is not (SKAlphaType.Premul or SKAlphaType.Opaque)) return false;
        int w = image.Width, h = image.Height;
        // 画面上の絵の範囲
        double x0 = m.TransX + sx * left, y0 = m.TransY + sy * top, x1 = x0 + sx * w, y1 = y0 + sy * h;
        var clip = canvas.DeviceClipBounds;
        int ix0 = (int)Math.Max(Math.Floor(x0), Math.Max(0, clip.Left)), iy0 = (int)Math.Max(Math.Floor(y0), Math.Max(0, clip.Top));
        int ix1 = (int)Math.Min(Math.Ceiling(x1), Math.Min(target.Width, clip.Right)), iy1 = (int)Math.Min(Math.Ceiling(y1), Math.Min(target.Height, clip.Bottom));
        int ow = ix1 - ix0, oh = iy1 - iy0;
        if (ow <= 0 || oh <= 0) return true;                          // 画面の外: 何も描かない
        if ((long)ow * oh < 100_000) return false;                    // 小さいものは Skia で十分速い

        // 出力のピクセルの中心 (ix + 0.5) が、元の絵のどこにあたるか
        var (hx, hw) = Taps(ow, (ix0 + 0.5 - x0) / sx - 0.5, 1 / sx, w);
        var (vy, vw) = Taps(oh, (iy0 + 0.5 - y0) / sy - 0.5, 1 / sy, h);
        // 端のピクセルは、絵がかかっている割合だけ薄くする (Skia のアンチエイリアスと同じ)
        int Coverage(int i, double a, double b) => (int)Math.Round(Math.Clamp(Math.Min(i + 1, b) - Math.Max(i, a), 0, 1) * 256);
        var cx = new int[ow];
        for (int i = 0; i < ow; i++) cx[i] = Coverage(ix0 + i, x0, x1);
        var cy = new int[oh];
        for (int i = 0; i < oh; i++) cy[i] = Coverage(iy0 + i, y0, y1);
        // paint の不透明度もここで掛ける (Skia に重ねさせるときに掛けると、丸めが 2 回になって少し暗くなる)
        int opacity = paint.Color.Alpha;

        // 縦に使う元の行の範囲だけを横に拡大する (int の 4 成分 = 1 ピクセル 16 バイト)
        int rowMin = int.MaxValue, rowMax = -1;
        foreach (int r in vy) { rowMin = Math.Min(rowMin, r); rowMax = Math.Max(rowMax, r); }
        int rows = rowMax - rowMin + 1;
        long midBytes = (long)rows * ow * 16;
        var mid = (int*)PixelPool.Rent(midBytes);
        int rowBytes = ow * 4;
        var outBuf = PixelPool.Rent((long)rowBytes * oh);
        bool handedOver = false;
        try
        {
            byte* s = (byte*)src.GetPixels();
            int srcRow = src.RowBytes;
            fixed (int* hxp = hx, hwp = hw, vyp = vy, vwp = vw, cxp = cx, cyp = cy)
            {
                int* hX = hxp, hW = hwp, vY = vyp, vW = vwp, cX = cxp, cY = cyp;
                // 1. 横: 元の行 → 出力の幅 (値は 0〜255 に重みを掛けたまま。負になることもある)
                PixelOps.ForBands(ow, rows, (r0, r1) =>
                {
                    for (int r = r0; r < r1; r++)
                    {
                        uint* line = (uint*)(s + (long)(rowMin + r) * srcRow);
                        var dst = (Vector128<int>*)(mid + (long)r * ow * 4);
                        for (int i = 0; i < ow; i++)
                        {
                            int* ti = hX + i * 4, tw = hW + i * 4;
                            var acc = Pixel(line[ti[0]]) * tw[0] + Pixel(line[ti[1]]) * tw[1] + Pixel(line[ti[2]]) * tw[2] + Pixel(line[ti[3]]) * tw[3];
                            dst[i] = Vector128.ShiftRightArithmetic(acc + Vector128.Create(1 << 5), 6);   // 桁を 8 だけ残して、縦で溢れないように
                        }
                    }
                });
                // 2. 縦: 4 つの行を重み付きで足して、0〜255 (乗算済みアルファの範囲) に収める
                byte* o = (byte*)outBuf;
                PixelOps.ForBands(ow, oh, (y0b, y1b) =>
                {
                    for (int y = y0b; y < y1b; y++)
                    {
                        int* ty = vY + y * 4, tw = vW + y * 4;
                        var r0 = (Vector128<int>*)(mid + (long)(ty[0] - rowMin) * ow * 4);
                        var r1 = (Vector128<int>*)(mid + (long)(ty[1] - rowMin) * ow * 4);
                        var r2 = (Vector128<int>*)(mid + (long)(ty[2] - rowMin) * ow * 4);
                        var r3 = (Vector128<int>*)(mid + (long)(ty[3] - rowMin) * ow * 4);
                        int w0 = tw[0], w1 = tw[1], w2 = tw[2], w3 = tw[3], covY = cY[y];
                        uint* d = (uint*)(o + (long)y * rowBytes);
                        var zero = Vector128<int>.Zero;
                        var max = Vector128.Create(255);
                        for (int i = 0; i < ow; i++)
                        {
                            // 横で 2^(14-6) = 256 倍、縦で 2^14 倍になっている → 2^22 で割る
                            var acc = r0[i] * w0 + r1[i] * w1 + r2[i] * w2 + r3[i] * w3;
                            var v = Vector128.ShiftRightArithmetic(acc + Vector128.Create(1 << 21), 22);
                            v = Vector128.Min(Vector128.Max(v, zero), max);
                            int a = v.GetElement(3);
                            int cov = cX[i] * covY;   // 0〜65536
                            if (opacity != 255) cov = (cov * opacity + 127) / 255;
                            if (cov != 65536)
                            {
                                v = Vector128.ShiftRightArithmetic(v * cov + Vector128.Create(1 << 15), 16);
                                a = v.GetElement(3);
                            }
                            // 拡大で色がアルファを超えることがあるので (負の重み)、乗算済みとして正しい値に収める
                            int b = Math.Min(v.GetElement(0), a), g = Math.Min(v.GetElement(1), a), rr = Math.Min(v.GetElement(2), a);
                            d[i] = (uint)(b | g << 8 | rr << 16 | a << 24);
                        }
                    }
                });
            }
            using var enlarged = PixelPool.ToImage(outBuf, new SKImageInfo(ow, oh, SKColorType.Bgra8888, SKAlphaType.Premul), rowBytes);
            handedOver = true;
            // 拡大した絵を等倍で重ねる (不透明度・合成方法は paint のまま)
            var quality = paint.FilterQuality;
            bool aa = paint.IsAntialias;
            var color = paint.Color;
            paint.FilterQuality = SKFilterQuality.None;
            paint.IsAntialias = false;
            paint.Color = color.WithAlpha(255);   // 不透明度は掛け終えている
            canvas.Save();
            canvas.ResetMatrix();
            if (!DrawSpriteParallelAt(canvas, target, enlarged, ix0, iy0, paint))
                canvas.DrawImage(enlarged, ix0, iy0, paint);
            canvas.Restore();
            paint.FilterQuality = quality;
            paint.IsAntialias = aa;
            paint.Color = color;
            return true;
        }
        finally
        {
            PixelPool.Return((IntPtr)mid, midBytes);
            if (!handedOver) PixelPool.Return(outBuf, (long)rowBytes * oh);
        }
    }

    /// <summary>BGRA の 1 ピクセルを、4 つの int (B, G, R, A) にする。</summary>
    private static Vector128<int> Pixel(uint p) =>
        Vector128.Create((int)(p & 0xFF), (int)(p >> 8 & 0xFF), (int)(p >> 16 & 0xFF), (int)(p >> 24));

    /// <summary>等倍の絵を、描画先を横帯に分けて複数スレッドで重ねる。</summary>
    private static bool DrawSpriteParallelAt(SKCanvas canvas, SKPixmap target, SKImage image, int x, int y, SKPaint paint)
    {
        if (target.ColorType != SKColorType.Bgra8888 || Environment.ProcessorCount < 2 || image.Height < 64) return false;
        int H = target.Height, W = target.Width;
        int ya = Math.Max(0, y), yb = Math.Min(H, y + image.Height);
        if (yb - ya < 64) return false;
        int bands = Math.Min(Environment.ProcessorCount, (yb - ya) / 32);
        IntPtr pixels = target.GetPixels();
        int rowBytes = target.RowBytes;
        Parallel.For(0, bands, b =>
        {
            int b0 = ya + (int)((long)(yb - ya) * b / bands), b1 = ya + (int)((long)(yb - ya) * (b + 1) / bands);
            if (b1 <= b0) return;
            using var band = SKSurface.Create(new SKImageInfo(W, b1 - b0, SKColorType.Bgra8888, SKAlphaType.Premul), pixels + b0 * rowBytes, rowBytes);
            band.Canvas.DrawImage(image, x, y - b0, paint);
            band.Canvas.Flush();
        });
        return true;
    }
}
