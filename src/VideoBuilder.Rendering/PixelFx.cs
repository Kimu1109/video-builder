using System.Runtime.CompilerServices;

namespace VideoBuilder.Rendering;

/// <summary>
/// 追加のエフェクト (方向ブラー・放射ブラー・色収差・ラスター・グロー・シャープ・ノイズ・レベル補正など) の、
/// ピクセルを直接扱う処理。対象は 8bit BGRA (乗算済みアルファ)。どれも行ごとに複数スレッドへ分けて実行する。
/// ピクセルは 32bit (下位から B, G, R, A) として扱い、2 チャンネルずつまとめて計算する (0x00FF00FF の形)。
/// </summary>
public static unsafe class PixelFx
{
    private const uint LaneMask = 0x00FF00FF;

    /// <summary>a と b を t / 256 (0〜256) の割合で混ぜる (四捨五入)。乗算済みアルファのまま混ぜてよい。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Lerp(uint a, uint b, uint t)
    {
        uint s = 256 - t;
        uint rb = ((a & LaneMask) * s + (b & LaneMask) * t + 0x00800080) >> 8 & LaneMask;
        uint ag = (((a >> 8) & LaneMask) * s + ((b >> 8) & LaneMask) * t + 0x00800080) & 0xFF00FF00;
        return rb | ag;
    }

    /// <summary>2 つのピクセルの平均。up なら 0.5 を切り上げ、そうでなければ切り捨てる (交互に使うと偏らない)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average(uint a, uint b, bool up)
    {
        uint r = up ? 0x00010001u : 0;
        uint rb = ((a & LaneMask) + (b & LaneMask) + r) >> 1 & LaneMask;
        uint ag = ((((a >> 8) & LaneMask) + ((b >> 8) & LaneMask) + r) >> 1 & LaneMask) << 8;
        return rb | ag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Fetch(uint* src, int w, int h, int stride, int x, int y, bool clamp)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h)
        {
            if (!clamp) return 0;
            x = x < 0 ? 0 : x >= w ? w - 1 : x;
            y = y < 0 ? 0 : y >= h ? h - 1 : y;
        }
        return src[(long)y * stride + x];
    }

    /// <summary>
    /// (x, y) の色を、周りの 4 ピクセルから求める (双線形補間)。座標はピクセルの中心が整数。
    /// 絵の外は透明 (clamp なら端のピクセル)。stride はピクセル単位。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Sample(uint* src, int w, int h, int stride, float x, float y, bool clamp)
    {
        if (!(x > -2 && x < w + 1)) { if (!clamp) return 0; x = x > 0 ? w : -1; }
        if (!(y > -2 && y < h + 1)) { if (!clamp) return 0; y = y > 0 ? h : -1; }
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int x0 = (int)fx, y0 = (int)fy;
        uint tx = (uint)((x - fx) * 256 + 0.5f), ty = (uint)((y - fy) * 256 + 0.5f);
        uint p00, p10, p01, p11;
        if (x0 >= 0 && y0 >= 0 && x0 + 1 < w && y0 + 1 < h)
        {
            uint* p = src + (long)y0 * stride + x0;
            p00 = p[0]; p10 = p[1]; p01 = p[stride]; p11 = p[stride + 1];
        }
        else
        {
            p00 = Fetch(src, w, h, stride, x0, y0, clamp); p10 = Fetch(src, w, h, stride, x0 + 1, y0, clamp);
            p01 = Fetch(src, w, h, stride, x0, y0 + 1, clamp); p11 = Fetch(src, w, h, stride, x0 + 1, y0 + 1, clamp);
        }
        if (ty == 0) return tx == 0 ? p00 : Lerp(p00, p10, tx);
        return Lerp(Lerp(p00, p10, tx), Lerp(p01, p11, tx), ty);
    }

    /// <summary>
    /// Sample と同じ値を、固定小数点 (1 = 65536) の座標で求める。絵の内側なら浮動小数点の計算を使わないので速い。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SampleFixed(uint* src, int w, int h, int stride, long xf, long yf, bool clamp)
    {
        int x0 = (int)(xf >> 16), y0 = (int)(yf >> 16);
        if ((uint)x0 < (uint)(w - 1) && (uint)y0 < (uint)(h - 1))
        {
            uint tx = (uint)(xf >> 8) & 0xFF, ty = (uint)(yf >> 8) & 0xFF;
            uint* p = src + (long)y0 * stride + x0;
            uint top = tx == 0 ? p[0] : Lerp(p[0], p[1], tx);
            if (ty == 0) return top;
            return Lerp(top, tx == 0 ? p[stride] : Lerp(p[stride], p[stride + 1], tx), ty);
        }
        return Sample(src, w, h, stride, xf / 65536f, yf / 65536f, clamp);
    }

    /// <summary>座標を固定小数点 (1 = 65536) にする。極端な値は、絵から十分遠い所に丸める。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Fixed(float v) => (long)(Math.Clamp(v, -1e7f, 1e7f) * 65536f);

    /// <summary>
    /// 方向ブラー: (dx, dy) の向きに length ピクセルの範囲を平均する (中心は元の位置)。
    /// 「2 か所の平均」を、ずらす量を半分ずつにして繰り返す。n 回で 2ⁿ か所の平均になるので、長くしても重くなりにくい。
    /// src と tmp は同じ大きさで、src の中身は壊れる。結果が入っているバッファを返す。
    /// </summary>
    public static IntPtr DirectionalBlur(IntPtr src, IntPtr tmp, int w, int h, int rowBytes, float dx, float dy, float length, bool clamp, int maxPasses = 10)
    {
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1 || len <= 0) return src;
        dx /= len; dy /= len;
        int passes = Math.Clamp((int)MathF.Ceiling(MathF.Log2(length)), 1, maxPasses);
        float d = length / 2;
        IntPtr a = src, b = tmp;
        for (int i = 0; i < passes; i++, d /= 2)
        {
            float ox = dx * d / 2, oy = dy * d / 2;
            bool up = (i & 1) == 0;
            uint* s = (uint*)a, o = (uint*)b;
            int stride = rowBytes / 4;
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                // ずらす量はどのピクセルでも同じなので、x を 1 ずつ進めるだけでよい
                long ax0 = Fixed(-ox), bx0 = Fixed(ox);
                for (int y = y0; y < y1; y++)
                {
                    uint* row = o + (long)y * stride;
                    long ay = Fixed(y - oy), by = Fixed(y + oy);
                    long ax = ax0, bx = bx0;
                    for (int x = 0; x < w; x++, ax += 65536, bx += 65536)
                        row[x] = Average(SampleFixed(s, w, h, stride, ax, ay, clamp), SampleFixed(s, w, h, stride, bx, by, clamp), up);
                }
            });
            (a, b) = (b, a);
        }
        return a;
    }

    /// <summary>
    /// 放射ブラー: (cx, cy) から外へ向かう線に沿って平均する。range は倍率の幅 (0.5 なら ×0.5〜×1.5 の範囲に相当)。
    /// 方向ブラーと同じく、倍率を半分ずつにして 2 か所の平均を繰り返す。結果が入っているバッファを返す。
    /// </summary>
    public static IntPtr RadialBlur(IntPtr src, IntPtr tmp, int w, int h, int rowBytes, float cx, float cy, float range, bool clamp, int maxPasses = 10)
    {
        if (range <= 0) return src;
        // 対数で考えると、拡大縮小は「ずらす」ことになる。ln(倍率) の幅を lg とする
        float lg = MathF.Log(1 + range);
        float far = MathF.Sqrt(MathF.Max(cx * cx, (w - cx) * (w - cx)) + MathF.Max(cy * cy, (h - cy) * (h - cy)));
        float pixels = far * lg;   // いちばん遠い点が動く距離 (ピクセル)
        if (pixels < 1) return src;
        int passes = Math.Clamp((int)MathF.Ceiling(MathF.Log2(pixels)), 1, maxPasses);
        float d = lg / 2;
        IntPtr a = src, b = tmp;
        for (int i = 0; i < passes; i++, d /= 2)
        {
            float k1 = MathF.Exp(-d / 2), k2 = MathF.Exp(d / 2);
            bool up = (i & 1) == 0;
            uint* s = (uint*)a, o = (uint*)b;
            int stride = rowBytes / 4;
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                // 1 行の中では、読む位置の y は変わらず、x は一定の幅 (倍率) で進む
                long astep = Fixed(k1), bstep = Fixed(k2);
                long ax0 = Fixed(cx - cx * k1), bx0 = Fixed(cx - cx * k2);
                for (int y = y0; y < y1; y++)
                {
                    uint* row = o + (long)y * stride;
                    float ry = y - cy;
                    long ay = Fixed(cy + ry * k1), by = Fixed(cy + ry * k2);
                    long ax = ax0, bx = bx0;
                    for (int x = 0; x < w; x++, ax += astep, bx += bstep)
                        row[x] = Average(SampleFixed(s, w, h, stride, ax, ay, clamp), SampleFixed(s, w, h, stride, bx, by, clamp), up);
                }
            });
            (a, b) = (b, a);
        }
        return a;
    }

    /// <summary>
    /// 色収差: 赤を (dx, dy)、青を (-dx, -dy) だけずらす。radial なら (cx, cy) からの距離に比例してずらす
    /// (いちばん遠い点で amount ピクセル)。不透明度はずらした 3 色のうち大きいもの。
    /// </summary>
    public static void ChromaticAberration(IntPtr src, IntPtr dst, int w, int h, int rowBytes, float dx, float dy, bool radial, float cx, float cy, float amount, bool clamp)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        int stride = rowBytes / 4;
        float far = MathF.Max(1, MathF.Sqrt(MathF.Max(cx * cx, (w - cx) * (w - cx)) + MathF.Max(cy * cy, (h - cy) * (h - cy))));
        float k = amount / far;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                for (int x = 0; x < w; x++)
                {
                    float ox = dx, oy = dy;
                    if (radial) { ox = (x - cx) * k; oy = (y - cy) * k; }
                    uint r = Sample(s, w, h, stride, x - ox, y - oy, clamp);
                    uint g = s[(long)y * stride + x];
                    uint b = Sample(s, w, h, stride, x + ox, y + oy, clamp);
                    uint ra = r >> 24, ga = g >> 24, ba = b >> 24;
                    uint al = Math.Max(ra, Math.Max(ga, ba));
                    row[x] = (b & 0xFF) | (g & 0xFF00) | (r & 0xFF0000) | al << 24;
                }
            }
        });
    }

    /// <summary>
    /// ラスター: 行 (vertical なら列) ごとに、正弦波の形で横 (縦) にずらす。
    /// ずれ = amplitude × sin(2π × (位置 / wavelength) + phase)。
    /// </summary>
    public static void Raster(IntPtr src, IntPtr dst, int w, int h, int rowBytes, float amplitude, float wavelength, float phase, bool vertical, bool clamp)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        int stride = rowBytes / 4;
        float k = 2 * MathF.PI / MathF.Max(1, wavelength);
        if (!vertical)
        {
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    float shift = amplitude * MathF.Sin(y * k + phase);
                    uint* row = o + (long)y * stride;
                    for (int x = 0; x < w; x++) row[x] = Sample(s, w, h, stride, x - shift, y, clamp);
                }
            });
            return;
        }
        var shifts = new float[w];
        for (int x = 0; x < w; x++) shifts[x] = amplitude * MathF.Sin(x * k + phase);
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                for (int x = 0; x < w; x++) row[x] = Sample(s, w, h, stride, x, y - shifts[x], clamp);
            }
        });
    }

    /// <summary>
    /// グロー用: 明るさが threshold (0〜255) 以上の部分だけを残す (境目は 32 段階でなめらかに)。threshold が 0 なら何もしない。
    /// </summary>
    public static void BrightPass(IntPtr pixels, int w, int h, int rowBytes, float threshold)
    {
        if (threshold <= 0) return;
        // 明るさ (0〜255) → 残す割合 (0〜256)
        var keep = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            float t = Math.Clamp((i - (threshold - 16)) / 32f, 0, 1);
            keep[i] = (ushort)MathF.Round(t * t * (3 - 2 * t) * 256);
        }
        byte* basePtr = (byte*)pixels;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            fixed (ushort* lut = keep)
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* p = (uint*)(basePtr + (long)y * rowBytes);
                    for (int x = 0; x < w; x++)
                    {
                        uint c = p[x];
                        uint a = c >> 24;
                        if (a == 0) continue;
                        // 乗算済みの明るさを、アルファで割って元の明るさにする
                        uint lum = ((c >> 16 & 0xFF) * 54 + (c >> 8 & 0xFF) * 183 + (c & 0xFF) * 19) >> 8;
                        if (a != 255) lum = Math.Min(255, lum * 255 / a);
                        uint t = lut[lum];
                        if (t == 256) continue;
                        p[x] = t == 0 ? 0 : Lerp(0, c, t);
                    }
                }
            }
        });
    }

    /// <summary>
    /// アンシャープマスク: pixels = pixels + (pixels − blurred) × amount。アルファはそのまま。
    /// blurred は pixels と同じ大きさ。
    /// </summary>
    public static void Unsharp(IntPtr pixels, int rowBytes, IntPtr blurred, int blurredRowBytes, int w, int h, float amount)
    {
        int k = (int)MathF.Round(amount * 256);
        if (k == 0) return;
        byte* basePtr = (byte*)pixels, blurPtr = (byte*)blurred;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                byte* p = basePtr + (long)y * rowBytes;
                byte* q = blurPtr + (long)y * blurredRowBytes;
                for (int x = 0; x < w * 4; x += 4)
                {
                    int a = p[x + 3];
                    if (a == 0) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        int v = p[x + c];
                        v += ((v - q[x + c]) * k + 128) >> 8;
                        p[x + c] = (byte)(v < 0 ? 0 : v > a ? a : v);
                    }
                }
            }
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Hash(uint x, uint y, uint seed)
    {
        unchecked
        {
            uint h = x * 0x8DA6B343u ^ y * 0xD8163841u ^ seed * 0xCB1AB31Fu;
            h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
            return h;
        }
    }

    /// <summary>
    /// ノイズ: 明るさを ±amount (0〜255) の範囲でばらつかせる。cell ピクセル四方は同じ値 (粒の大きさ)。
    /// color なら赤・緑・青を別々にばらつかせる。乗算済みアルファなので、半透明の所は弱くかかる。
    /// </summary>
    public static void Noise(IntPtr pixels, int w, int h, int rowBytes, float amount, int cell, bool color, uint seed)
    {
        int amp = (int)MathF.Round(Math.Clamp(amount, 0, 255) * 2);   // 乱数 (0〜255) にかける倍率 ×256
        if (amp == 0) return;
        cell = Math.Max(1, cell);
        byte* basePtr = (byte*)pixels;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                byte* p = basePtr + (long)y * rowBytes;
                uint cy = (uint)(y / cell);
                for (int x = 0; x < w; x++, p += 4)
                {
                    int a = p[3];
                    if (a == 0) continue;
                    uint r = Hash((uint)(x / cell), cy, seed);
                    // -128〜127 → ×amp/256 → アルファを掛ける
                    int n0 = ((int)(r & 0xFF) - 128) * amp >> 8;
                    int n1 = color ? ((int)(r >> 8 & 0xFF) - 128) * amp >> 8 : n0;
                    int n2 = color ? ((int)(r >> 16 & 0xFF) - 128) * amp >> 8 : n0;
                    if (a != 255) { n0 = n0 * a / 255; n1 = n1 * a / 255; n2 = n2 * a / 255; }
                    int b = p[0] + n0, g = p[1] + n1, rr = p[2] + n2;
                    p[0] = (byte)(b < 0 ? 0 : b > a ? a : b);
                    p[1] = (byte)(g < 0 ? 0 : g > a ? a : g);
                    p[2] = (byte)(rr < 0 ? 0 : rr > a ? a : rr);
                }
            }
        });
    }

    /// <summary>
    /// 赤・緑・青に同じ変換表 (256 個) をかける。乗算済みアルファを戻してから表を引く。
    /// </summary>
    public static void ApplyLut(IntPtr pixels, int w, int h, int rowBytes, byte[] lut)
    {
        byte* basePtr = (byte*)pixels;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            fixed (byte* t = lut)
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* p = basePtr + (long)y * rowBytes;
                    byte* end = p + (long)w * 4;
                    for (; p < end; p += 4)
                    {
                        int a = p[3];
                        if (a == 255) { p[0] = t[p[0]]; p[1] = t[p[1]]; p[2] = t[p[2]]; }
                        else if (a != 0)
                        {
                            int b = Math.Min(255, (p[0] * 255 + a / 2) / a), g = Math.Min(255, (p[1] * 255 + a / 2) / a), r = Math.Min(255, (p[2] * 255 + a / 2) / a);
                            p[0] = (byte)((t[b] * a + 127) / 255); p[1] = (byte)((t[g] * a + 127) / 255); p[2] = (byte)((t[r] * a + 127) / 255);
                        }
                    }
                }
            }
        });
    }

    /// <summary>レベル補正の変換表。入力の黒・白 (0〜255)、ガンマ、出力の黒・白 (0〜255)。</summary>
    public static byte[] LevelsLut(float inBlack, float inWhite, float gamma, float outBlack, float outWhite)
    {
        var lut = new byte[256];
        float range = MathF.Max(1, inWhite - inBlack);
        float inv = 1 / MathF.Max(0.01f, gamma);
        for (int i = 0; i < 256; i++)
        {
            float v = Math.Clamp((i - inBlack) / range, 0, 1);
            v = MathF.Pow(v, inv);
            lut[i] = (byte)Math.Clamp((int)MathF.Round(outBlack + v * (outWhite - outBlack)), 0, 255);
        }
        return lut;
    }

    /// <summary>ポスタリゼーションの変換表 (levels 段階に減らす)。</summary>
    public static byte[] PosterizeLut(int levels)
    {
        levels = Math.Clamp(levels, 2, 256);
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int step = Math.Min(levels - 1, i * levels / 256);
            lut[i] = (byte)(step * 255 / (levels - 1));
        }
        return lut;
    }
}
