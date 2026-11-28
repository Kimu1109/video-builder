using SkiaSharp;

namespace VideoBuilder.Rendering;

/// <summary>
/// ピクセルを直接書き換える処理。行ごとに複数スレッドへ分けて実行する (CPU 描画での高速化)。
/// 対象は 8bit BGRA (乗算済みアルファ)。
/// </summary>
public static unsafe class PixelOps
{
    /// <summary>これより小さい画像は、スレッドに分ける手間の方が大きいので 1 スレッドで処理する。</summary>
    private const int ParallelThreshold = 64 * 1024;

    /// <summary>1 つのコアあたりの帯の数。</summary>
    private const int BandsPerCore = 8;

    /// <summary>1 つの帯の最小の行数 (帯が細かすぎると、分ける手間の方が大きくなる)。</summary>
    private const int MinRowsPerBand = 8;

    /// <summary>
    /// 行の範囲 [y0, y1) ごとに body を呼ぶ。大きな画像では複数スレッドに分ける。
    /// 帯はコア数より多めに作る (コア数の 8 倍。1 つの帯は 8 行以上)。Parallel.For は空いたスレッドに次の帯を渡すので、
    /// 速いコアと遅いコアが混ざった CPU (RK3588 の A76 + A55 などの big.LITTLE、Intel の P コア + E コア) でも、
    /// 遅いコアに当たった大きな帯を最後まで待たずに済む。以前はコア数の 2 倍だった。
    /// </summary>
    internal static void ForBands(int width, int height, Action<int, int> body)
    {
        int cores = Environment.ProcessorCount;
        if ((long)width * height < ParallelThreshold || cores == 1) { body(0, height); return; }
        int bands = BandCount(height, cores);
        Parallel.For(0, bands, b => body((int)((long)height * b / bands), (int)((long)height * (b + 1) / bands)));
    }

    /// <summary>
    /// 1〜255 の a について、2^32 / a を切り上げた値。n / a (n は 0〜65535) を、割り算でなく掛け算とシフトで求めるのに使う。
    /// 割る数が画素ごとに変わる割り算は、定数の割り算と違って JIT が掛け算に直せず、ARM64 でも x64 でも遅い
    /// (乗算済みアルファを戻すところで、半透明の画素ごとに 3 回ずつあった)。
    /// n &lt; 2^16・a &lt; 2^8 なら、切り上げの誤差が n / 2^32 &lt; 1 / a に収まるので、結果は n / a と同じになる。
    /// </summary>
    private static readonly ulong[] Reciprocals = CreateReciprocals();

    private static ulong[] CreateReciprocals()
    {
        var t = new ulong[256];
        for (int a = 1; a < 256; a++) t[a] = ((1UL << 32) + (ulong)a - 1) / (ulong)a;
        return t;
    }

    /// <summary>n / a (切り捨て)。n は 0〜65535、a は 1〜255。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int DivideByAlpha(int n, int a) => (int)(((ulong)(uint)n * Reciprocals[a]) >> 32);

    /// <summary>乗算済みの値 c (0〜255) をアルファ a (1〜255) で割って戻す (四捨五入)。(c * 255 + a / 2) / a と同じ。255 を超えることがある。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Unpremultiply(int c, int a) => DivideByAlpha(c * 255 + (a >> 1), a);

    /// <summary>ForBands で分ける帯の数。</summary>
    internal static int BandCount(int height, int cores) =>
        Math.Max(1, Math.Min(height, Math.Min(cores * BandsPerCore, Math.Max(cores, height / MinRowsPerBand))));

    /// <summary>カラーマトリクスをアルファ行が「そのまま」の場合に限って適用できるか。</summary>
    public static bool CanApply(float[] m) =>
        m[15] == 0 && m[16] == 0 && m[17] == 0 && Math.Abs(m[18] - 1) < 1e-6f && m[19] == 0;

    /// <summary>
    /// 4x5 のカラーマトリクス (RGBA の順, 平行移動は 0..1) を、その場で適用する。
    /// 計算は Skia の SKColorFilter.CreateColorMatrix と同じく、乗算済みアルファを戻してから行う。
    /// </summary>
    public static void ColorMatrix(IntPtr pixels, int width, int height, int rowBytes, float[] m)
    {
        bool diagonal = m[1] == 0 && m[2] == 0 && m[5] == 0 && m[7] == 0 && m[10] == 0 && m[11] == 0;
        byte* basePtr = (byte*)pixels;
        if (diagonal)
        {
            // チャンネルどうしが混ざらない (明るさ・コントラストなど) なら、256 通りの表を引くだけで済む
            // 1 つの配列に B, G, R の順で 256 個ずつ並べる
            var lut = new byte[768];
            for (int i = 0; i < 256; i++)
            {
                lut[i] = Clamp8(m[12] * i + m[14] * 255f);
                lut[256 + i] = Clamp8(m[6] * i + m[9] * 255f);
                lut[512 + i] = Clamp8(m[0] * i + m[4] * 255f);
            }
            ForBands(width, height, (y0, y1) =>
            {
                fixed (byte* table = lut)
                {
                    byte* lb = table, lg = table + 256, lr = table + 512;
                    for (int y = y0; y < y1; y++)
                    {
                        byte* p = basePtr + (long)y * rowBytes;
                        byte* end = p + (long)width * 4;
                        for (; p < end; p += 4)
                        {
                            int a = p[3];
                            if (a == 255) { p[0] = lb[p[0]]; p[1] = lg[p[1]]; p[2] = lr[p[2]]; }
                            else if (a != 0)
                            {
                                int b = Unpremultiply(p[0], a), g = Unpremultiply(p[1], a), r = Unpremultiply(p[2], a);
                                if (b > 255) b = 255; if (g > 255) g = 255; if (r > 255) r = 255;
                                p[0] = (byte)((lb[b] * a + 127) / 255); p[1] = (byte)((lg[g] * a + 127) / 255); p[2] = (byte)((lr[r] * a + 127) / 255);
                            }
                        }
                    }
                }
            });
            return;
        }

        // 一般の場合: 固定小数点 (1 = 4096) で計算する
        const int Shift = 12, One = 1 << Shift, Half = One / 2;
        int c0 = F(m[0]), c1 = F(m[1]), c2 = F(m[2]), c3 = F(m[3]), c4 = F(m[4] * 255f);
        int c5 = F(m[5]), c6 = F(m[6]), c7 = F(m[7]), c8 = F(m[8]), c9 = F(m[9] * 255f);
        int c10 = F(m[10]), c11 = F(m[11]), c12 = F(m[12]), c13 = F(m[13]), c14 = F(m[14] * 255f);
        static int F(float v) => (int)MathF.Round(v * One);
        ForBands(width, height, (y0, y1) =>
        {
            // アルファが 255 のときの定数項をあらかじめ足しておく
            int k0 = c3 * 255 + c4 + Half, k1 = c8 * 255 + c9 + Half, k2 = c13 * 255 + c14 + Half;
            for (int y = y0; y < y1; y++)
            {
                byte* p = basePtr + (long)y * rowBytes;
                byte* end = p + (long)width * 4;
                for (; p < end; p += 4)
                {
                    int a = p[3];
                    if (a == 255)
                    {
                        int b = p[0], g = p[1], r = p[2];
                        int nr = (c0 * r + c1 * g + c2 * b + k0) >> Shift;
                        int ng = (c5 * r + c6 * g + c7 * b + k1) >> Shift;
                        int nb = (c10 * r + c11 * g + c12 * b + k2) >> Shift;
                        p[2] = (byte)(nr < 0 ? 0 : nr > 255 ? 255 : nr); p[1] = (byte)(ng < 0 ? 0 : ng > 255 ? 255 : ng); p[0] = (byte)(nb < 0 ? 0 : nb > 255 ? 255 : nb);
                    }
                    else if (a != 0)
                    {
                        int b = Unpremultiply(p[0], a), g = Unpremultiply(p[1], a), r = Unpremultiply(p[2], a);
                        if (b > 255) b = 255; if (g > 255) g = 255; if (r > 255) r = 255;
                        int nr = (c0 * r + c1 * g + c2 * b + c3 * a + c4 + Half) >> Shift;
                        int ng = (c5 * r + c6 * g + c7 * b + c8 * a + c9 + Half) >> Shift;
                        int nb = (c10 * r + c11 * g + c12 * b + c13 * a + c14 + Half) >> Shift;
                        nr = nr < 0 ? 0 : nr > 255 ? 255 : nr; ng = ng < 0 ? 0 : ng > 255 ? 255 : ng; nb = nb < 0 ? 0 : nb > 255 ? 255 : nb;
                        p[2] = (byte)((nr * a + 127) / 255); p[1] = (byte)((ng * a + 127) / 255); p[0] = (byte)((nb * a + 127) / 255);
                    }
                }
            }
        });
    }

    private static byte Clamp8(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    private static readonly object ChromaLock = new();
    private static (float, float, float, float, float) _chromaKeyOfTable;
    private static byte[]? _chromaTable;

    /// <summary>
    /// クロマキーの近似版 (プレビュー用)。色を各 6bit に丸めた表 (64³) を引くだけなので速い。
    /// 境界のなめらかさがわずかに粗くなるため、出力では使わない。
    /// </summary>
    public static void ChromaKeyFast(IntPtr pixels, int width, int height, int rowBytes, float keyHue, float keySat, float hueRange, float satRange, float edge)
    {
        byte[] table;
        var key = (keyHue, keySat, hueRange, satRange, edge);
        lock (ChromaLock)
        {
            if (_chromaTable == null || _chromaKeyOfTable != key)
            {
                // 表の各マスの中心の色を、正確な計算に通して「残す割合」を求める
                var t = new byte[64 * 64 * 64];
                var one = new byte[4];
                fixed (byte* px = one)
                {
                    for (int r = 0; r < 64; r++)
                    for (int g = 0; g < 64; g++)
                    for (int b = 0; b < 64; b++)
                    {
                        px[0] = (byte)(b * 4 + 2); px[1] = (byte)(g * 4 + 2); px[2] = (byte)(r * 4 + 2); px[3] = 255;
                        ChromaKey((IntPtr)px, 1, 1, 4, keyHue, keySat, hueRange, satRange, edge);
                        t[(r << 12) | (g << 6) | b] = px[3];
                    }
                }
                _chromaTable = t; _chromaKeyOfTable = key;
            }
            table = _chromaTable;
        }
        byte* basePtr = (byte*)pixels;
        ForBands(width, height, (y0, y1) =>
        {
            fixed (byte* lut = table)
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* p = basePtr + (long)y * rowBytes;
                    byte* end = p + (long)width * 4;
                    for (; p < end; p += 4)
                    {
                        int a = p[3];
                        if (a == 0) continue;
                        int b = p[0], g = p[1], r = p[2];
                        if (a != 255) { b = Math.Min(255, DivideByAlpha(b * 255, a)); g = Math.Min(255, DivideByAlpha(g * 255, a)); r = Math.Min(255, DivideByAlpha(r * 255, a)); }
                        int keep = lut[((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2)];
                        if (keep == 255) continue;
                        p[0] = (byte)(p[0] * keep / 255); p[1] = (byte)(p[1] * keep / 255); p[2] = (byte)(p[2] * keep / 255); p[3] = (byte)(a * keep / 255);
                    }
                }
            }
        });
    }

    /// <summary>キー色に近いピクセルを透明にする (クロマキー)。</summary>
    /// <param name="hueRange">色相の許容範囲 (度)。</param>
    /// <param name="satRange">彩度の許容範囲 (0〜100)。</param>
    public static void ChromaKey(IntPtr pixels, int width, int height, int rowBytes, float keyHue, float keySat, float hueRange, float satRange, float edge)
    {
        byte* basePtr = (byte*)pixels;
        ForBands(width, height, (y0, y1) =>
        {
          for (int y = y0; y < y1; y++)
          {
            byte* p = basePtr + (long)y * rowBytes;
            for (int x = 0; x < width; x++, p += 4)
            {
                byte al = p[3];
                if (al == 0) continue;
                // 乗算済みアルファを戻してから色を調べる
                float inv = 255f / al;
                float b = Math.Min(255, p[0] * inv), g = Math.Min(255, p[1] * inv), r = Math.Min(255, p[2] * inv);
                float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                float sat = max <= 0 ? 0 : d / max * 100f;
                // 彩度だけで範囲外と分かるピクセルは、ここで終わり (結果は変わらず、色相の計算を省ける)
                float dsEarly = Math.Abs(sat - keySat);
                if (satRange > 0 ? dsEarly >= satRange : dsEarly > 0) continue;
                float hue;
                if (d <= 0) hue = 0;
                else if (max == r) hue = 60 * (((g - b) / d) % 6);
                else if (max == g) hue = 60 * ((b - r) / d + 2);
                else hue = 60 * ((r - g) / d + 4);
                if (hue < 0) hue += 360;
                float dh = Math.Abs(hue - keyHue); if (dh > 180) dh = 360 - dh;
                float ds = Math.Abs(sat - keySat);
                // 0 = キー色そのもの, 1 以上 = 範囲外
                float dist = Math.Max(hueRange <= 0 ? (dh > 0 ? 2 : 0) : dh / hueRange, satRange <= 0 ? (ds > 0 ? 2 : 0) : ds / satRange);
                float keep = Math.Clamp((dist - 1) / edge + 1, 0, 1);
                if (keep >= 1) continue;
                p[0] = (byte)(p[0] * keep); p[1] = (byte)(p[1] * keep); p[2] = (byte)(p[2] * keep); p[3] = (byte)(al * keep);
            }
          }
        });
    }

    /// <summary>
    /// モザイク: size ピクセル四方のマスごとに平均の色で塗りつぶす (その場で書き換える)。
    /// マスの行ごとに複数スレッドへ分ける。
    /// </summary>
    public static void Mosaic(IntPtr pixels, int width, int height, int rowBytes, int size)
    {
        if (size <= 1) return;
        byte* basePtr = (byte*)pixels;
        int cellRows = (height + size - 1) / size;
        void Row(int cy)
        {
            int y0 = cy * size, y1 = Math.Min(height, y0 + size);
            for (int x0 = 0; x0 < width; x0 += size)
            {
                int x1 = Math.Min(width, x0 + size);
                // 乗算済みアルファのまま平均すると、半透明の縁も正しい色になる
                uint b = 0, g = 0, r = 0, a = 0;
                for (int y = y0; y < y1; y++)
                {
                    byte* p = basePtr + (long)y * rowBytes + x0 * 4;
                    for (int x = x0; x < x1; x++, p += 4) { b += p[0]; g += p[1]; r += p[2]; a += p[3]; }
                }
                uint n = (uint)((x1 - x0) * (y1 - y0)), half = n / 2;
                uint value = ((b + half) / n) | (((g + half) / n) << 8) | (((r + half) / n) << 16) | (((a + half) / n) << 24);
                for (int y = y0; y < y1; y++)
                {
                    uint* q = (uint*)(basePtr + (long)y * rowBytes + x0 * 4);
                    for (int x = x0; x < x1; x++) *q++ = value;
                }
            }
        }
        if ((long)width * height < ParallelThreshold || Environment.ProcessorCount == 1)
            for (int cy = 0; cy < cellRows; cy++) Row(cy);
        else Parallel.For(0, cellRows, Row);
    }

    /// <summary>
    /// 画像を縦横半分にする (2x2 の平均)。複数スレッドで描く。
    /// 毎フレーム変わる画像を大きく縮小するとき、Skia に縮小用の中間画像 (ミップマップ) を
    /// 1 スレッドで作らせるより、これを繰り返す方がずっと速い。
    /// </summary>
    /// <param name="offsetX">元の画像を置く位置 (縮小前の座標)。余白を付けたいときに使う。</param>
    public static SKImage Halve(SKImage image, int width, int height, float offsetX = 0, float offsetY = 0)
    {
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low };
        return DrawParallel((width + 1) / 2, (height + 1) / 2, c =>
        {
            c.Scale(0.5f);
            c.DrawImage(image, offsetX, offsetY, paint);
        });
    }

    /// <summary>
    /// Skia の描画を横帯に分けて、複数スレッドで同時に行う (Skia の CPU 描画は 1 回の描画が 1 スレッドでしか動かないため)。
    /// draw は帯ごとに別々の SKCanvas で呼ばれるので、共有するのは読み取り専用のもの (SKImage・SKPaint・フィルタ) だけにする。
    /// </summary>
    public static SKImage DrawParallel(int width, int height, Action<SKCanvas> draw)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int rowBytes = width * 4;
        IntPtr pixels = PixelPool.Rent((long)rowBytes * height);   // 使い回しのバッファ。帯ごとに最初に透明で塗る
        int cores = Environment.ProcessorCount;
        int bands = (long)width * height < 256 * 1024 || cores == 1 ? 1 : Math.Min(cores, Math.Max(1, height / 64));
        void Band(int b)
        {
            int y0 = (int)((long)height * b / bands), y1 = (int)((long)height * (b + 1) / bands);
            if (y1 <= y0) return;
            using var surface = SKSurface.Create(new SKImageInfo(width, y1 - y0, SKColorType.Bgra8888, SKAlphaType.Premul), pixels + y0 * rowBytes, rowBytes);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            // 効果の中の合成モードの描画を、自前で合成できるようにする (PixelBlend)
            PixelBlend.Register(canvas, pixels + y0 * rowBytes, rowBytes, width, y1 - y0);
            canvas.Translate(0, -y0);
            draw(canvas);
            canvas.Flush();
        }
        try
        {
            if (bands == 1) Band(0); else Parallel.For(0, bands, Band);
        }
        catch
        {
            PixelPool.Return(pixels, (long)rowBytes * height);
            throw;
        }
        return PixelPool.ToImage(pixels, info, rowBytes);
    }
}
