using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace VideoBuilder.Rendering;

/// <summary>
/// CPU のガウスぼかし (乗算済みアルファの BGRA)。横 → 縦の 2 回に分けて、行ごとに複数スレッドで処理する。
///
/// 重みは合計が 256 になる 8 ビットの整数にする。すると「重み × 0〜255 の値」の合計が 16 ビットに収まるので、
/// 16 バイト (4 ピクセル) ずつまとめて SIMD で計算できる。縦は上下の行の同じ位置のバイトを、
/// 横は 4 バイト (1 ピクセル) ずつずらしたバイトを重み付きで足すだけなので、色の成分を分けずに済む。
/// 乗算済みアルファの値をそのまま混ぜても、色がアルファを超えることはない (どちらも同じ重みで混ぜるため)。
/// </summary>
public static unsafe class PixelBlur
{
    /// <summary>ガウスの重み (中心から片側 radius 個ずつ、合計 256)。</summary>
    internal static ushort[] Kernel(float sigma, out int radius)
    {
        radius = Math.Max(1, (int)MathF.Ceiling(sigma * 3));
        var f = new double[radius * 2 + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++) sum += f[i + radius] = Math.Exp(-(double)i * i / (2.0 * sigma * sigma));
        // 合計がちょうど 256 になるよう、切り捨てた端数の大きいものから 1 ずつ足す (明るさも形も変わらないように。
        // 端数をまとめて中心に足すと、重みの小さい広いぼかしでは中心だけが重くなり、ぼかしが弱くなる)
        var w = new ushort[f.Length];
        var rest = new double[f.Length];
        int total = 0, center = radius;
        for (int i = 0; i < f.Length; i++)
        {
            double v = f[i] / sum * 256;
            w[i] = (ushort)Math.Floor(v);
            total += w[i];
            rest[i] = v - w[i];
        }
        // 端数が同じなら中心に近い方を先にする
        var order = Enumerable.Range(0, f.Length).OrderByDescending(i => rest[i]).ThenBy(i => Math.Abs(i - center)).Take(256 - total);
        foreach (int i in order) w[i]++;
        // 重みが 0 の端は計算しない
        int trim = 0;
        while (trim < radius && w[trim] == 0 && w[^(trim + 1)] == 0) trim++;
        if (trim > 0)
        {
            radius -= trim;
            w = w[trim..^trim];
        }
        return w;
    }

    /// <summary>
    /// src (width x height) をぼかして dst に書く。dst は (width + pad*2) x (height + pad*2) で、元の絵は (pad, pad) に置かれる。
    /// clamp なら、絵の外は端のピクセルが続いているものとして扱う (画面全体のぼかし)。そうでなければ透明として扱う。
    /// </summary>
    public static void Gaussian(IntPtr src, int width, int height, int srcRowBytes, IntPtr dst, int dstRowBytes, float sigma, int pad, bool clamp)
    {
        if (clamp) pad = 0;
        int outW = width + pad * 2, outH = height + pad * 2;
        if (sigma < 0.2f || width <= 0 || height <= 0)
        {
            Copy(src, width, height, srcRowBytes, dst, dstRowBytes, pad);
            return;
        }
        var kernel = Kernel(sigma, out int r);
        // 横にぼかした結果 (元の行の数 x 出力の幅)
        int tmpRow = outW * 4;
        // 作業用のバッファは使い回す (4K で 33 MB。毎回新しく確保すると、OS がページを割り当てる時間がかかる)
        long tmpBytes = (long)tmpRow * height;
        var tmp = (byte*)PixelPool.Rent(tmpBytes);
        try
        {
            fixed (ushort* k = kernel)
            {
                ushort* kp = k;
                Horizontal((byte*)src, width, height, srcRowBytes, tmp, tmpRow, kp, r, pad, clamp);
                Vertical(tmp, outW, height, tmpRow, (byte*)dst, dstRowBytes, outH, kp, r, pad, clamp);
            }
        }
        finally { PixelPool.Return((IntPtr)tmp, tmpBytes); }
    }

    private static void Copy(IntPtr src, int width, int height, int srcRowBytes, IntPtr dst, int dstRowBytes, int pad)
    {
        byte* s = (byte*)src, d = (byte*)dst;
        int outH = height + pad * 2, outBytes = (width + pad * 2) * 4;
        PixelOps.ForBands(width, outH, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                var row = d + (long)y * dstRowBytes;
                new Span<byte>(row, outBytes).Clear();
                int sy = y - pad;
                if (sy >= 0 && sy < height) Buffer.MemoryCopy(s + (long)sy * srcRowBytes, row + pad * 4, width * 4, width * 4);
            }
        });
    }

    /// <summary>行ごとに横へぼかす。行の左右に余白 (透明か、端のピクセル) を付けた作業用の行を作ってから、重みを掛けて足す。</summary>
    private static void Horizontal(byte* src, int width, int height, int srcRowBytes, byte* tmp, int tmpRow, ushort* k, int r, int pad, bool clamp)
    {
        int outW = width + pad * 2;
        int lineBytes = (outW + r * 2) * 4;
        PixelOps.ForBands(outW, height, (y0, y1) =>
        {
            var line = (byte*)NativeMemory.Alloc((nuint)lineBytes + 32);
            try
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* s = src + (long)y * srcRowBytes;
                    // 作業用の行: [余白 r + pad][元の行 width][余白 pad + r]
                    int left = (r + pad) * 4;
                    if (clamp)
                    {
                        uint first = *(uint*)s, last = *(uint*)(s + (width - 1) * 4);
                        for (int i = 0; i < r + pad; i++) ((uint*)line)[i] = first;
                        for (int i = r + pad + width; i < outW + r * 2; i++) ((uint*)line)[i] = last;
                    }
                    else
                    {
                        new Span<byte>(line, left).Clear();
                        new Span<byte>(line + left + width * 4, (pad + r) * 4).Clear();
                    }
                    Buffer.MemoryCopy(s, line + left, width * 4, width * 4);
                    Convolve(line, 4, tmp + (long)y * tmpRow, outW * 4, k, r);
                }
            }
            finally { NativeMemory.Free(line); }
        });
    }

    /// <summary>
    /// 縦へぼかす。出力の行 y は、作業用の行 (y - pad - r) 〜 (y - pad + r) を重み付きで足したもの。
    /// 範囲の外の行は、透明なら足さない (0)、clamp なら端の行を使う。
    /// </summary>
    private static void Vertical(byte* tmp, int outW, int height, int tmpRow, byte* dst, int dstRowBytes, int outH, ushort* k, int r, int pad, bool clamp)
    {
        int bytes = outW * 4;
        PixelOps.ForBands(outW, outH, (y0, y1) =>
        {
            var rows = stackalloc byte*[r * 2 + 1];
            var weights = stackalloc ushort[r * 2 + 1];
            for (int y = y0; y < y1; y++)
            {
                int n = 0;
                for (int t = -r; t <= r; t++)
                {
                    int sy = y - pad + t;
                    if (sy < 0 || sy >= height)
                    {
                        if (!clamp) continue;
                        sy = Math.Clamp(sy, 0, height - 1);
                    }
                    rows[n] = tmp + (long)sy * tmpRow;
                    weights[n] = k[t + r];
                    n++;
                }
                SumRows(rows, weights, n, dst + (long)y * dstRowBytes, bytes);
            }
        });
    }

    /// <summary>out[i] = Σ k[t] * line[i + t * step] / 256 (t = 0 〜 2r)。16 バイトずつ SIMD で計算する。</summary>
    private static void Convolve(byte* line, int step, byte* output, int bytes, ushort* k, int r)
    {
        int taps = r * 2 + 1;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var half = Vector256.Create((ushort)128);
            for (; i + 32 <= bytes; i += 32)
            {
                Vector256<ushort> lo = half, hi = half;
                for (int t = 0; t < taps; t++)
                {
                    var (vl, vh) = Vector256.Widen(Vector256.Load(line + i + t * step));
                    var w = Vector256.Create(k[t]);
                    lo += vl * w;
                    hi += vh * w;
                }
                Vector256.Narrow(Vector256.ShiftRightLogical(lo, 8), Vector256.ShiftRightLogical(hi, 8)).Store(output + i);
            }
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var half = Vector128.Create((ushort)128);
            for (; i + 16 <= bytes; i += 16)
            {
                Vector128<ushort> lo = half, hi = half;
                for (int t = 0; t < taps; t++)
                {
                    var v = Vector128.Load(line + i + t * step);
                    var w = Vector128.Create(k[t]);
                    var (vl, vh) = Vector128.Widen(v);
                    lo += vl * w;
                    hi += vh * w;
                }
                Vector128.Narrow(Vector128.ShiftRightLogical(lo, 8), Vector128.ShiftRightLogical(hi, 8)).Store(output + i);
            }
        }
        for (; i < bytes; i++)
        {
            int acc = 128;
            for (int t = 0; t < taps; t++) acc += k[t] * line[i + t * step];
            output[i] = (byte)(acc >> 8);
        }
    }

    /// <summary>out[i] = Σ weights[t] * rows[t][i] / 256。16 バイトずつ SIMD で計算する。</summary>
    private static void SumRows(byte** rows, ushort* weights, int n, byte* output, int bytes)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var half = Vector256.Create((ushort)128);
            for (; i + 32 <= bytes; i += 32)
            {
                Vector256<ushort> lo = half, hi = half;
                for (int t = 0; t < n; t++)
                {
                    var (vl, vh) = Vector256.Widen(Vector256.Load(rows[t] + i));
                    var w = Vector256.Create(weights[t]);
                    lo += vl * w;
                    hi += vh * w;
                }
                Vector256.Narrow(Vector256.ShiftRightLogical(lo, 8), Vector256.ShiftRightLogical(hi, 8)).Store(output + i);
            }
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var half = Vector128.Create((ushort)128);
            for (; i + 16 <= bytes; i += 16)
            {
                Vector128<ushort> lo = half, hi = half;
                for (int t = 0; t < n; t++)
                {
                    var v = Vector128.Load(rows[t] + i);
                    var w = Vector128.Create(weights[t]);
                    var (vl, vh) = Vector128.Widen(v);
                    lo += vl * w;
                    hi += vh * w;
                }
                Vector128.Narrow(Vector128.ShiftRightLogical(lo, 8), Vector128.ShiftRightLogical(hi, 8)).Store(output + i);
            }
        }
        for (; i < bytes; i++)
        {
            int acc = 128;
            for (int t = 0; t < n; t++) acc += weights[t] * rows[t][i];
            output[i] = (byte)(acc >> 8);
        }
    }
}
