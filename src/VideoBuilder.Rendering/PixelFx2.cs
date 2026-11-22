using System.Runtime.CompilerServices;

namespace VideoBuilder.Rendering;

/// <summary>
/// PixelFx の続き: 波紋・極座標変換・万華鏡・球体・部分変形 (ディスプレイスメント)・レンズブラー・境界ぼかし・
/// ライト・凸エッジ・エッジ抽出・カラーキー・ルミナンスキー・特定色域変換・インターレース解除。
/// どれも 8bit BGRA (乗算済みアルファ) を、行ごとに複数スレッドで処理する。
/// </summary>
public static unsafe partial class PixelFx
{
    // ---------------- 座標を変えて読む処理 ----------------

    /// <summary>横方向は繰り返し (端と端がつながる)、縦方向は端のピクセルとみなして読む。極座標変換・球体用。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SampleWrapX(uint* src, int w, int h, int stride, float x, float y)
    {
        if (!(x > -1e7f && x < 1e7f)) x = 0;
        x -= MathF.Floor(x / w) * w;
        y = Math.Clamp(y, 0, h - 1);
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int x0 = (int)fx, y0 = (int)fy;
        if (x0 >= w) x0 = 0;
        int x1 = x0 + 1 >= w ? 0 : x0 + 1, y1 = Math.Min(h - 1, y0 + 1);
        uint tx = (uint)((x - fx) * 256 + 0.5f), ty = (uint)((y - fy) * 256 + 0.5f);
        uint* r0 = src + (long)y0 * stride, r1 = src + (long)y1 * stride;
        return Lerp(Lerp(r0[x0], r0[x1], tx), Lerp(r1[x0], r1[x1], tx), ty);
    }

    /// <summary>
    /// 波紋: (cx, cy) から広がる波で、中心からの距離をずらして読む。ずれ = amp × sin(2π r / wavelength − phase)。
    /// decay が 0 より大きければ、中心から離れるほど弱くなる (距離 decay で約 37%)。
    /// </summary>
    public static void Ripple(IntPtr src, IntPtr dst, int w, int h, int rowBytes, float cx, float cy, float amp, float wavelength, float phase, float decay, bool clamp)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        int stride = rowBytes / 4;
        float k = 2 * MathF.PI / MathF.Max(1, wavelength);
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                float dy = y - cy;
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx;
                    float r = MathF.Sqrt(dx * dx + dy * dy);
                    if (r < 1e-3f) { row[x] = s[(long)y * stride + x]; continue; }
                    float d = amp * MathF.Sin(r * k - phase);
                    if (decay > 0) d *= MathF.Exp(-r / decay);
                    float f = (r + d) / r;
                    row[x] = Sample(s, w, h, stride, cx + dx * f, cy + dy * f, clamp);
                }
            }
        });
    }

    /// <summary>
    /// 極座標変換: 絵の横方向を円周に、縦方向を半径に巻き付ける (絵の上端が外側)。出力は入力と同じ大きさ。
    /// radius は円の半径、hole は中心の穴の半径 (px)。rotation (度) で回し、swirl で外側ほどねじる (1 = 外周で 1 周)。
    /// </summary>
    public static void Polar(IntPtr src, IntPtr dst, int w, int h, int rowBytes, float radius, float hole, float rotation, float swirl)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        int stride = rowBytes / 4;
        float cx = (w - 1) / 2f, cy = (h - 1) / 2f;
        radius = MathF.Max(1, radius);
        hole = Math.Clamp(hole, 0, radius - 1);
        float span = radius - hole, rot = rotation / 360f;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                float dy = y - cy;
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx;
                    float r = MathF.Sqrt(dx * dx + dy * dy);
                    // 円の縁と穴の縁は 1 ピクセルでなめらかにする
                    float cover = Math.Clamp(radius - r + 0.5f, 0, 1) * Math.Clamp(r - hole + 0.5f, 0, 1);
                    if (cover <= 0) { row[x] = 0; continue; }
                    float rr = Math.Clamp((r - hole) / span, 0, 1);
                    float u = MathF.Atan2(dx, -dy) / (2 * MathF.PI) + rot + swirl * rr;   // 0 = 真上、時計回り
                    uint c = SampleWrapX(s, w, h, stride, u * w - 0.5f, (1 - rr) * (h - 1));
                    row[x] = cover >= 1 ? c : Lerp(0, c, (uint)(cover * 256));
                }
            }
        });
    }

    /// <summary>万華鏡: (cx, cy) の周りを segments 個の扇形に分け、1 つの扇形の絵を鏡のように折り返して並べる。</summary>
    public static void Kaleidoscope(IntPtr src, IntPtr dst, int w, int h, int rowBytes, float cx, float cy, int segments, float rotation, bool clamp)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        int stride = rowBytes / 4;
        segments = Math.Clamp(segments, 1, 360);
        float seg = 2 * MathF.PI / segments, rot = rotation * MathF.PI / 180f;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                float dy = y - cy;
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx;
                    float r = MathF.Sqrt(dx * dx + dy * dy);
                    float t = MathF.Atan2(dy, dx) - rot;
                    float n = MathF.Floor(t / seg);
                    float a = t - n * seg;
                    if (((int)n & 1) != 0) a = seg - a;   // 1 つおきに折り返す
                    a += rot;
                    row[x] = Sample(s, w, h, stride, cx + r * MathF.Cos(a), cy + r * MathF.Sin(a), clamp);
                }
            }
        });
    }

    /// <summary>
    /// 球体: 絵を地図のように (横 = 経度・縦 = 緯度) 球に貼って、size × size の絵に描く。
    /// m は回転行列 (3x3, 行優先。画面の向き → 球の向き)。shade (0〜1) で縁を暗くして立体感を出す。
    /// </summary>
    public static void Sphere(IntPtr src, int sw, int sh, int srcRowBytes, IntPtr dst, int size, int dstRowBytes, float[] m, float shade)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        int sstride = srcRowBytes / 4, dstride = dstRowBytes / 4;
        float R = size / 2f, c = (size - 1) / 2f;
        PixelOps.ForBands(size, size, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * dstride;
                float ny = (y - c) / R;
                for (int x = 0; x < size; x++)
                {
                    float nx = (x - c) / R;
                    float d2 = nx * nx + ny * ny;
                    float edge = Math.Clamp((1 - MathF.Sqrt(d2)) * R + 0.5f, 0, 1);
                    if (edge <= 0) { row[x] = 0; continue; }
                    float nz = MathF.Sqrt(MathF.Max(0, 1 - d2));   // 手前向き
                    float px = m[0] * nx + m[1] * ny + m[2] * nz;
                    float py = m[3] * nx + m[4] * ny + m[5] * nz;
                    float pz = m[6] * nx + m[7] * ny + m[8] * nz;
                    float u = MathF.Atan2(px, pz) / (2 * MathF.PI) + 0.5f;
                    float v = MathF.Asin(Math.Clamp(py, -1, 1)) / MathF.PI + 0.5f;
                    uint col = SampleWrapX(s, sw, sh, sstride, u * sw - 0.5f, v * (sh - 1));
                    float light = 1 - shade * (1 - nz);
                    uint a = (uint)(edge * 256);
                    if (light < 1)
                    {
                        // 色だけを暗くする (乗算済みなので、色をアルファ以下に保ったまま)
                        uint t = (uint)(Math.Clamp(light, 0, 1) * 256);
                        col = (Lerp(0, col, t) & 0x00FFFFFF) | (col & 0xFF000000);
                    }
                    row[x] = a >= 256 ? col : Lerp(0, col, a);
                }
            }
        });
    }

    /// <summary>
    /// 部分変形: (cx, cy) を中心とした円 (rect なら四角形) の中だけを変形する。境目は feather (0〜1) の割合でなめらかに弱める。
    /// mode: 0 = 移動 (p1, p2 ピクセル)、1 = 拡大 (p1 %)、2 = 回転 (p1 度)。
    /// </summary>
    public static void Displace(IntPtr src, IntPtr dst, int w, int h, int rowBytes, int mode, bool rect, float cx, float cy, float rx, float ry, float feather, float p1, float p2, bool clamp)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        int stride = rowBytes / 4;
        rx = MathF.Max(0.5f, rx); ry = MathF.Max(0.5f, ry);
        feather = Math.Clamp(feather, 0.001f, 1);
        float zoom = p1 / 100f, ang = p1 * MathF.PI / 180f;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                float dy = y - cy, qy = dy / ry;
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx, qx = dx / rx;
                    float d = rect ? MathF.Max(MathF.Abs(qx), MathF.Abs(qy)) : MathF.Sqrt(qx * qx + qy * qy);
                    float t = Math.Clamp((1 - d) / feather, 0, 1);
                    if (t <= 0) { row[x] = s[(long)y * stride + x]; continue; }
                    t = t * t * (3 - 2 * t);
                    float sx, sy;
                    switch (mode)
                    {
                        case 0: sx = x - p1 * t; sy = y - p2 * t; break;
                        case 1: { float f = 1 / MathF.Max(0.01f, 1 + zoom * t); sx = cx + dx * f; sy = cy + dy * f; break; }
                        default: { float a = -ang * t, cs = MathF.Cos(a), sn = MathF.Sin(a); sx = cx + dx * cs - dy * sn; sy = cy + dx * sn + dy * cs; break; }
                    }
                    row[x] = Sample(s, w, h, stride, sx, sy, clamp);
                }
            }
        });
    }

    /// <summary>
    /// 画像を使った変形 (ディスプレイスメントマップ): map (同じ大きさ) の赤で横、緑で縦にずらす (128 = ずらさない)。
    /// ずれの最大は mx, my ピクセル。
    /// </summary>
    public static void DisplaceByMap(IntPtr src, IntPtr dst, int w, int h, int rowBytes, IntPtr map, int mapRowBytes, float mx, float my, bool clamp)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        byte* mp = (byte*)map;
        int stride = rowBytes / 4;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                byte* mrow = mp + (long)y * mapRowBytes;
                for (int x = 0; x < w; x++)
                {
                    int a = mrow[x * 4 + 3];
                    if (a == 0) { row[x] = s[(long)y * stride + x]; continue; }
                    // 乗算済みを戻して、128 からの差をずれにする (地図が半透明なら、そのぶん弱める)
                    float r = Math.Min(255, mrow[x * 4 + 2] * 255f / a), g = Math.Min(255, mrow[x * 4 + 1] * 255f / a);
                    float k = a / 255f;
                    row[x] = Sample(s, w, h, stride, x - (r - 128) / 128f * mx * k, y - (g - 128) / 128f * my * k, clamp);
                }
            }
        });
    }

    // ---------------- ぼかし ----------------

    /// <summary>
    /// レンズブラー: 半径 radius の円の中を平均する (ピントが外れたような、縁のはっきりしたぼけ)。
    /// boost (0〜1) で明るい所の重みを上げ、光の玉のように見せる。点の並びはひまわりの種の形 (どの向きにも偏らない)。
    /// </summary>
    public static void LensBlur(IntPtr src, IntPtr dst, int w, int h, int rowBytes, float radius, float boost, bool clamp)
    {
        if (radius < 0.5f) { Buffer.MemoryCopy((void*)src, (void*)dst, (long)rowBytes * h, (long)rowBytes * h); return; }
        uint* s = (uint*)src, o = (uint*)dst;
        int stride = rowBytes / 4;
        int n = Math.Clamp((int)MathF.Round(radius * radius * 1.2f), 8, 128);
        var ox = new long[n]; var oy = new long[n];
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float r = radius * MathF.Sqrt((i + 0.5f) / n), t = i * golden;
            ox[i] = Fixed(r * MathF.Cos(t)); oy[i] = Fixed(r * MathF.Sin(t));
        }
        // 明るさ (0〜255) → 重み。1 + boost × 明るさ⁴ × 8
        var weight = new int[256];
        for (int i = 0; i < 256; i++) { float l = i / 255f; weight[i] = (int)(64 * (1 + boost * 8 * l * l * l * l)); }
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                long yf = (long)y << 16;
                for (int x = 0; x < w; x++)
                {
                    long xf = (long)x << 16;
                    long sb = 0, sg = 0, sr = 0, sa = 0, sw = 0;
                    for (int i = 0; i < n; i++)
                    {
                        uint c = SampleFixed(s, w, h, stride, xf + ox[i], yf + oy[i], clamp);
                        int wt = weight[((c >> 16 & 0xFF) * 54 + (c >> 8 & 0xFF) * 183 + (c & 0xFF) * 19) >> 8];
                        sb += (c & 0xFF) * wt; sg += (c >> 8 & 0xFF) * wt; sr += (c >> 16 & 0xFF) * wt; sa += (c >> 24) * wt; sw += wt;
                    }
                    long half = sw / 2;
                    uint al = (uint)Math.Min(255, (sa + half) / sw);
                    uint bb = (uint)Math.Min(al, (sb + half) / sw), gg = (uint)Math.Min(al, (sg + half) / sw), rr = (uint)Math.Min(al, (sr + half) / sw);
                    row[x] = bb | gg << 8 | rr << 16 | al << 24;
                }
            }
        });
    }

    /// <summary>アルファだけを面 (1 ピクセル 1 バイト) に写す。</summary>
    private static void ExtractAlpha(byte* pixels, int w, int h, int rowBytes, byte* plane)
    {
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                byte* p = pixels + (long)y * rowBytes + 3;
                byte* q = plane + (long)y * w;
                for (int x = 0; x < w; x++) q[x] = p[x * 4];
            }
        });
    }

    /// <summary>
    /// 境界ぼかし: 形の縁を内側に向かって透明にする (範囲 sigma ≒ 幅の半分)。
    /// アルファをぼかした値が半分より小さい所 (縁の近く) ほど透明にする。
    /// </summary>
    public static void EdgeFeather(IntPtr pixels, int w, int h, int rowBytes, float sigma)
    {
        if (sigma < 0.2f) return;
        long bytes = (long)w * h;
        var plane = PixelPool.Rent(bytes);
        try
        {
            byte* px = (byte*)pixels, a = (byte*)plane;
            ExtractAlpha(px, w, h, rowBytes, a);
            PixelBlur.GaussianAlpha(a, w, h, w, a, w, sigma);
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* p = (uint*)(px + (long)y * rowBytes);
                    byte* b = a + (long)y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int f = Math.Clamp((b[x] - 128) * 2 + 1, 0, 256);
                        if (f < 256) p[x] = Lerp(0, p[x], (uint)f);
                    }
                }
            });
        }
        finally { PixelPool.Return(plane, bytes); }
    }

    // ---------------- 光・縁 ----------------

    /// <summary>
    /// ライト (縁の光): 形の内側の縁を、光の色で明るくする。光の向き (lx, ly) から当たる側の縁が強く光る。
    /// sigma は光が入り込む幅、shift は向きの強さ (ピクセル)。
    /// </summary>
    public static void RimLight(IntPtr pixels, int w, int h, int rowBytes, float sigma, float lx, float ly, float shift, byte cr, byte cg, byte cb, float strength)
    {
        long bytes = (long)w * h;
        var plane = PixelPool.Rent(bytes);
        var blurred = PixelPool.Rent(bytes);
        try
        {
            byte* px = (byte*)pixels, a = (byte*)plane, b = (byte*)blurred;
            ExtractAlpha(px, w, h, rowBytes, a);
            PixelBlur.GaussianAlpha(a, w, h, w, b, w, MathF.Max(0.3f, sigma));
            int sx = (int)MathF.Round(lx * shift), sy = (int)MathF.Round(ly * shift);
            int k = (int)(Math.Clamp(strength, 0, 8) * 512);
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* p = px + (long)y * rowBytes;
                    int yy = y + sy;
                    for (int x = 0; x < w; x++)
                    {
                        int al = p[x * 4 + 3];
                        if (al == 0) continue;
                        int xx = x + sx;
                        int around = (uint)xx < (uint)w && (uint)yy < (uint)h ? b[(long)yy * w + xx] : 0;
                        int rim = al - around;   // 縁の近く (周りが透明) ほど大きい
                        if (rim <= 0) continue;
                        int add = rim * k >> 8;  // 0〜255 × 強さ × 2
                        int amt = add * al / 255;
                        p[x * 4] = (byte)Math.Min(al, p[x * 4] + (cb * amt >> 8));
                        p[x * 4 + 1] = (byte)Math.Min(al, p[x * 4 + 1] + (cg * amt >> 8));
                        p[x * 4 + 2] = (byte)Math.Min(al, p[x * 4 + 2] + (cr * amt >> 8));
                    }
                }
            });
        }
        finally { PixelPool.Return(plane, bytes); PixelPool.Return(blurred, bytes); }
    }

    /// <summary>
    /// 凸エッジ: 形の縁を、光の向き (lx, ly。光のある方) に向いた側は明るく、反対側は暗くして、盛り上がって見せる。
    /// sigma は盛り上がりの幅、height は明暗の強さ。
    /// </summary>
    public static void Bevel(IntPtr pixels, int w, int h, int rowBytes, float sigma, float height, float lx, float ly)
    {
        long bytes = (long)w * h;
        var plane = PixelPool.Rent(bytes);
        try
        {
            byte* px = (byte*)pixels, b = (byte*)plane;
            ExtractAlpha(px, w, h, rowBytes, b);
            PixelBlur.GaussianAlpha(b, w, h, w, b, w, MathF.Max(0.3f, sigma));
            // 傾き (0〜255 / ピクセル) × 幅 ≒ 255 になるので、幅をかけて強さをそろえる
            float k = height * MathF.Max(1, sigma) / 255f;
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* p = px + (long)y * rowBytes;
                    byte* row = b + (long)y * w;
                    byte* up = y > 0 ? row - w : row, down = y < h - 1 ? row + w : row;
                    for (int x = 0; x < w; x++)
                    {
                        int al = p[x * 4 + 3];
                        if (al == 0) continue;
                        float gx = (x < w - 1 ? row[x + 1] : row[x]) - (x > 0 ? row[x - 1] : row[x]);
                        float gy = down[x] - up[x];
                        // 高さが内側に向かって上がるので、面の向きは (−gx, −gy)
                        float shade = (-gx * lx - gy * ly) * 0.5f * k;
                        if (MathF.Abs(shade) < 0.004f) continue;
                        int target = shade > 0 ? al : 0;
                        float t = MathF.Min(1, MathF.Abs(shade));
                        for (int c = 0; c < 3; c++) p[x * 4 + c] = (byte)(p[x * 4 + c] + (target - p[x * 4 + c]) * t);
                    }
                }
            });
        }
        finally { PixelPool.Return(plane, bytes); }
    }

    /// <summary>
    /// エッジ抽出: 明るさ (alphaEdge なら不透明度) の変わり目を、color の線にする (ソーベル)。
    /// gain は強さ、threshold (0〜1) より弱い変わり目は消す。overlay なら元の絵の上に重ねる。
    /// </summary>
    public static void EdgeDetect(IntPtr src, IntPtr dst, int w, int h, int rowBytes, float gain, float threshold, bool alphaEdge, uint premulColor, bool overlay)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        int stride = rowBytes / 4;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                int ya = Math.Max(0, y - 1), yb = Math.Min(h - 1, y + 1);
                uint* ra = s + (long)ya * stride, rm = s + (long)y * stride, rb = s + (long)yb * stride;
                for (int x = 0; x < w; x++)
                {
                    int xa = Math.Max(0, x - 1), xb = Math.Min(w - 1, x + 1);
                    int V(uint c) => alphaEdge ? (int)(c >> 24) : (int)(((c >> 16 & 0xFF) * 54 + (c >> 8 & 0xFF) * 183 + (c & 0xFF) * 19) >> 8);
                    int gx = V(ra[xb]) + 2 * V(rm[xb]) + V(rb[xb]) - V(ra[xa]) - 2 * V(rm[xa]) - V(rb[xa]);
                    int gy = V(rb[xa]) + 2 * V(rb[x]) + V(rb[xb]) - V(ra[xa]) - 2 * V(ra[x]) - V(ra[xb]);
                    float g = MathF.Sqrt(gx * gx + gy * gy) / 1020f * gain;
                    float e = Math.Clamp((g - threshold) / MathF.Max(0.001f, 1 - threshold), 0, 1);
                    uint edge = e <= 0 ? 0 : Lerp(0, premulColor, (uint)(e * 256));
                    if (!overlay) { row[x] = edge; continue; }
                    // 線を元の絵の上に重ねる (乗算済みの「上に重ねる」)
                    row[x] = Over(edge, rm[x]);
                }
            }
        });
    }

    /// <summary>乗算済みアルファの「上に重ねる」(top を bottom の上に)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Over(uint top, uint bottom)
    {
        uint ta = top >> 24;
        if (ta == 0) return bottom;
        if (ta == 255) return top;
        uint inv = 255 - ta, r = 0;
        for (int sh = 0; sh < 32; sh += 8)
        {
            uint v = (top >> sh & 0xFF) + (((bottom >> sh & 0xFF) * inv + 127) / 255);
            r |= Math.Min(255u, v) << sh;
        }
        return r;
    }

    // ---------------- キー・色 ----------------

    /// <summary>
    /// カラーキー: キー色に近い色を透明にする。明るさの差と色の差 (YCbCr) をそれぞれの範囲で比べる。edge は境目のなめらかさ。
    /// </summary>
    public static void ColorKey(IntPtr pixels, int w, int h, int rowBytes, byte kr, byte kg, byte kb, float lumaRange, float chromaRange, float edge)
    {
        float ky = 0.299f * kr + 0.587f * kg + 0.114f * kb, kcb = kb - ky, kcr = kr - ky;
        edge = MathF.Max(0.001f, edge);
        byte* basePtr = (byte*)pixels;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                byte* p = basePtr + (long)y * rowBytes;
                for (int x = 0; x < w; x++, p += 4)
                {
                    int a = p[3];
                    if (a == 0) continue;
                    float inv = 255f / a;
                    float b = MathF.Min(255, p[0] * inv), g = MathF.Min(255, p[1] * inv), r = MathF.Min(255, p[2] * inv);
                    float yy = 0.299f * r + 0.587f * g + 0.114f * b, cb = b - yy - kcb, cr = r - yy - kcr;
                    float dy = MathF.Abs(yy - ky), dc = MathF.Sqrt(cb * cb + cr * cr);
                    float dist = MathF.Max(lumaRange <= 0 ? (dy > 0.5f ? 2 : 0) : dy / lumaRange, chromaRange <= 0 ? (dc > 0.5f ? 2 : 0) : dc / chromaRange);
                    float keep = Math.Clamp((dist - 1) / edge + 1, 0, 1);
                    if (keep >= 1) continue;
                    p[0] = (byte)(p[0] * keep); p[1] = (byte)(p[1] * keep); p[2] = (byte)(p[2] * keep); p[3] = (byte)(a * keep);
                }
            }
        });
    }

    /// <summary>ルミナンスキー: 明るさが基準 (0〜255) より暗い所 (bright なら明るい所) を透明にする。soft は境目の幅。</summary>
    public static void LumaKey(IntPtr pixels, int w, int h, int rowBytes, float level, float soft, bool bright)
    {
        var keep = new ushort[256];
        soft = MathF.Max(0.5f, soft);
        for (int i = 0; i < 256; i++)
        {
            float t = Math.Clamp((i - (level - soft / 2)) / soft, 0, 1);
            t = t * t * (3 - 2 * t);
            keep[i] = (ushort)MathF.Round((bright ? 1 - t : t) * 256);
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
                        uint c = p[x], a = c >> 24;
                        if (a == 0) continue;
                        uint lum = ((c >> 16 & 0xFF) * 54 + (c >> 8 & 0xFF) * 183 + (c & 0xFF) * 19) >> 8;
                        if (a != 255) lum = Math.Min(255, lum * 255 / a);
                        uint t = lut[lum];
                        if (t != 256) p[x] = Lerp(0, c, t);
                    }
                }
            }
        });
    }

    /// <summary>
    /// 特定色域変換: 色相が keyHue (度) から range 以内の色を、色相を shift 度回した色にする。範囲の外 edge 度でなめらかに弱める。
    /// 彩度・明るさも satScale・valScale 倍にする (範囲の中だけ)。灰色に近い色は変えない。
    /// </summary>
    public static void HueConvert(IntPtr pixels, int w, int h, int rowBytes, float keyHue, float range, float edge, float shift, float satScale, float valScale)
    {
        byte* basePtr = (byte*)pixels;
        edge = MathF.Max(0.001f, edge);
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                byte* p = basePtr + (long)y * rowBytes;
                for (int x = 0; x < w; x++, p += 4)
                {
                    int a = p[3];
                    if (a == 0) continue;
                    float inv = 1f / a;
                    float b = MathF.Min(1, p[0] * inv), g = MathF.Min(1, p[1] * inv), r = MathF.Min(1, p[2] * inv);
                    float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b)), d = max - min;
                    if (d <= 1e-4f) continue;
                    float hue;
                    if (max == r) hue = 60 * (((g - b) / d) % 6);
                    else if (max == g) hue = 60 * ((b - r) / d + 2);
                    else hue = 60 * ((r - g) / d + 4);
                    if (hue < 0) hue += 360;
                    float dh = MathF.Abs(hue - keyHue); if (dh > 180) dh = 360 - dh;
                    float wgt = dh <= range ? 1 : MathF.Max(0, 1 - (dh - range) / edge);
                    float sat = d / max;
                    wgt *= Math.Clamp(sat * 5, 0, 1);
                    if (wgt <= 0) continue;
                    hue = (hue + shift * wgt) % 360; if (hue < 0) hue += 360;
                    sat = Math.Clamp(sat * (1 + (satScale - 1) * wgt), 0, 1);
                    float val = Math.Clamp(max * (1 + (valScale - 1) * wgt), 0, 1);
                    // HSV → RGB
                    float cc = val * sat, hh = hue / 60, xx = cc * (1 - MathF.Abs(hh % 2 - 1)), mm = val - cc;
                    float nr, ng, nb;
                    switch ((int)hh)
                    {
                        case 0: nr = cc; ng = xx; nb = 0; break;
                        case 1: nr = xx; ng = cc; nb = 0; break;
                        case 2: nr = 0; ng = cc; nb = xx; break;
                        case 3: nr = 0; ng = xx; nb = cc; break;
                        case 4: nr = xx; ng = 0; nb = cc; break;
                        default: nr = cc; ng = 0; nb = xx; break;
                    }
                    p[0] = (byte)MathF.Min(a, MathF.Round((nb + mm) * a));
                    p[1] = (byte)MathF.Min(a, MathF.Round((ng + mm) * a));
                    p[2] = (byte)MathF.Min(a, MathF.Round((nr + mm) * a));
                }
            }
        });
    }

    /// <summary>
    /// インターレース解除。mode: 0 = 奇数の行を残す (偶数の行は上下から補う)、1 = 偶数の行を残す、
    /// 2 = 二重化 (偶数の行を 2 回ずつ使う)、3 = 平均 (上下の行と混ぜる)。
    /// </summary>
    public static void Deinterlace(IntPtr src, IntPtr dst, int w, int h, int rowBytes, int mode)
    {
        uint* s = (uint*)src, o = (uint*)dst;
        int stride = rowBytes / 4;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                uint* row = o + (long)y * stride;
                uint* cur = s + (long)y * stride;
                uint* above = s + (long)Math.Max(0, y - 1) * stride, below = s + (long)Math.Min(h - 1, y + 1) * stride;
                switch (mode)
                {
                    case 0 or 1:
                        if ((y & 1) == (mode == 0 ? 1 : 0) || h == 1) Buffer.MemoryCopy(cur, row, w * 4L, w * 4L);
                        else
                        {
                            // 補う行が絵の外なら、もう片方の行をそのまま使う
                            uint* a2 = y - 1 >= 0 ? above : below, b2 = y + 1 < h ? below : above;
                            for (int x = 0; x < w; x++) row[x] = Average(a2[x], b2[x], (x & 1) == 0);
                        }
                        break;
                    case 2:
                        Buffer.MemoryCopy((y & 1) == 0 ? cur : above, row, w * 4L, w * 4L);
                        break;
                    default:
                        for (int x = 0; x < w; x++) row[x] = Average(Average(above[x], below[x], true), cur[x], false);
                        break;
                }
            }
        });
    }
}
