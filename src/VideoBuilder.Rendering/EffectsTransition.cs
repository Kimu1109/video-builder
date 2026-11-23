using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// トランジションのうち、28 番以降 (入りと出で別の動きをするもの)。
/// <para>
/// 進み具合 u は「切り替えの始めが 0、終わりが 1」で、入ってくる側と出ていく側で同じ値になる
/// (入りは経過 / イン秒、出は 1 − 残り / アウト秒)。ストーリーボードは、重なっている 2 つのクリップの
/// 両方に同じ種類をかけるので、両方が同じ u で動き、合わせて 1 つの切り替えに見える。
/// </para>
/// <para>
/// どちらのクリップが手前のレイヤーにあっても同じ見え方になるようにしている:
/// 形で切り替えるもの (ワイプ類) は、入る側が形の内側、出る側が外側だけを描く (ちょうど補い合う)。
/// 前半・後半で切り替えるもの (クロスズームなど) は、出る側を前半だけ、入る側を後半だけ描く。
/// 「画面全体を基準にする」がオンのときは、形を絵ではなく画面の大きさ・位置で決めるので、大きさの違う 2 つの絵でも形がそろう。
/// </para>
/// </summary>
public static partial class Effects
{
    /// <summary>トランジションの動き方 (0〜1 → 0〜1)。kind は EffectRegistry.TransitionEasings の番号。</summary>
    internal static double TransitionEase(double t, int kind)
    {
        t = Math.Clamp(t, 0, 1);
        switch (kind)
        {
            case 1: return t;                                   // 直線
            case 2: return t * t * t;                           // 加速
            case 3: return 1 - (1 - t) * (1 - t) * (1 - t);     // 減速
            case 4:                                             // 行き過ぎて戻る
            {
                const double c1 = 1.70158, c3 = c1 + 1;
                double q = t - 1;
                return 1 + c3 * q * q * q + c1 * q * q;
            }
            case 5: return BounceOut((float)t);                 // 弾む
            case 6:                                             // ばね
                if (t <= 0 || t >= 1) return t;
                return Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * (2 * Math.PI / 3)) + 1;
            default: return t * t * (3 - 2 * t);                // 加減速
        }
    }

    /// <summary>
    /// 形の基準にする四角形 (絵の座標) と、画面の 1 ピクセルが絵の何ピクセルにあたるか。
    /// screen なら画面全体 (オブジェクトの位置・拡大率から逆算)、そうでなければ絵の大きさ。
    /// </summary>
    private static (SKRect G, float K) TransitionBase(Sprite s, EffectArgs a, bool screen)
    {
        var p = a.Context.Project;
        int W = p.Settings.Width, H = p.Settings.Height;
        float x = 0, y = 0, scale = 1;
        if (a.Object?.Draw is { } draw && a.Bounds != null)
        {
            x = (float)draw.Eval("x", a.Bounds, a.Frame);
            y = (float)draw.Eval("y", a.Bounds, a.Frame);
            scale = (float)draw.Eval("scale", a.Bounds, a.Frame) / 100f;
        }
        float total = s.Scale * scale;
        float k = total > 1e-4f ? 1 / total : 1;
        if (screen && total > 1e-4f)
            return (SKRect.Create(s.Cx + (-W / 2f - x) * k, s.Cy + (-H / 2f - y) * k, W * k, H * k), k);
        return (SKRect.Create(0, 0, s.Width, s.Height), k);
    }

    private static (float X, float Y) FromDir(int i) => i switch { 0 => (1, 0), 1 => (-1, 0), 2 => (0, 1), _ => (0, -1) };   // 左から・右から・上から・下から (動く向き)
    private static (float X, float Y) ToDir(int i) => i switch { 0 => (-1, 0), 1 => (1, 0), 2 => (0, -1), _ => (0, 1) };     // 左へ・右へ・上へ・下へ

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    internal static Sprite PairedTransition(Sprite s, EffectArgs a, int type, double progress, bool leaving)
    {
        var p = a.Context.Project;
        int W = p.Settings.Width, H = p.Settings.Height;
        int easeKind = a.Effect.GetInt("ease");
        float u = (float)Math.Clamp(leaving ? 1 - progress : progress, 0, 1);
        float eu = (float)TransitionEase(u, easeKind);
        var (G, K) = TransitionBase(s, a, a.Check("screen"));
        float feather = Math.Max(0, a.Track("feather")) * K;
        ref var m = ref a.Context.Modifiers;
        // 奥のクリップで、形で切り抜くだけの役なら何もしない (手前のクリップが、補い合う形で切り抜かれる)
        if (a.Check("under") && MaskOnly(type, leaving)) return s;
        switch (type)
        {
            case >= 28 and <= 31:   // プッシュ: 入る側が押し出すように、両方が同じ向きに動く
            {
                var (dx, dy) = FromDir(type - 28);
                float D = dx != 0 ? W : H, k = leaving ? eu : -(1 - eu);
                m.Ox += dx * D * k; m.Oy += dy * D * k;
                return s;
            }
            case >= 32 and <= 35:   // カバー: 入る側が上に重なるように入る。出る側は動かず、覆われた所を隠す
            {
                var (dx, dy) = FromDir(type - 32);
                if (!leaving)
                {
                    float D = dx != 0 ? W : H;
                    m.Ox -= dx * D * (1 - eu); m.Oy -= dy * D * (1 - eu);
                    return s;
                }
                var cover = G; cover.Offset(-dx * G.Width * (1 - eu), -dy * G.Height * (1 - eu));
                using var path = new SKPath();
                path.AddRect(cover);
                return Masked(s, a, path, 0, true);
            }
            case >= 36 and <= 39:   // アンカバー: 出る側がどいて、下にあった入る側が見えてくる
            {
                var (dx, dy) = ToDir(type - 36);
                if (leaving)
                {
                    float D = dx != 0 ? W : H;
                    m.Ox += dx * D * eu; m.Oy += dy * D * eu;
                    return s;
                }
                var cover = G; cover.Offset(dx * G.Width * eu, dy * G.Height * eu);
                using var path = new SKPath();
                path.AddRect(cover);
                return Masked(s, a, path, 0, true);
            }
            case >= 40 and <= 43: return WhipPan(s, a, type - 40, u, easeKind, leaving, W, H, K, ref m);
            case 44:   // クロスズーム: 出る側が拡大しながらぶれて、入る側が拡大された所から戻る
            {
                if (!Half(u, leaving, ref m, out float w)) return s;
                w *= w;
                m.Zoom *= 1 + 1.5f * w;
                float range = 0.7f * w;
                if (range < 0.01f) return s;
                float cx = s.Cx, cy = s.Cy;
                return Resample(s, a, 0, 0, (src, tmp, w2, h2, rb) => PixelFx.RadialBlur(src, tmp, w2, h2, rb, cx, cy, range, true, a.IsDraft ? 7 : 9)) ?? s;
            }
            case 45 or 46:   // 白・黒で切り替え: 出る側が白 (黒) になり、入る側が白 (黒) から戻る
            {
                if (!Half(u, leaving, ref m, out float w)) return s;
                w = Smooth(w);
                if (w <= 0) return s;
                float c = type == 45 ? w : 0, keep = 1 - w;
                return ApplyMatrix(s, a, new float[] { keep, 0, 0, 0, c, 0, keep, 0, 0, c, 0, 0, keep, 0, c, 0, 0, 0, 1, 0 });
            }
            case >= 47 and <= 64: return ShapeWipe(s, a, type, eu, leaving, G, K, feather);
            case 65: return Dissolve(s, a, eu, leaving, G, K);
            case 66: return RuleWipe(s, a, eu, leaving, G);
            case 67 or 68: return Doors(s, a, type == 67, eu, leaving, G);
            case >= 69 and <= 72:   // キューブ回転: 2 つの絵を、画面の大きさの立方体の隣り合う面として回す
            {
                bool horizontal = type <= 70;
                float sign = type is 69 or 71 ? -1 : 1;    // 右から・上から は回る向きが逆
                float S = horizontal ? W : H;
                float deg = leaving ? -sign * 90 * eu : sign * 90 * (1 - eu);
                float rad = deg * MathF.PI / 180;
                if (horizontal)
                {
                    m.Ry += deg;
                    m.Ox += -S / 2 * MathF.Sin(rad);
                }
                else
                {
                    m.Rx += deg;
                    m.Oy += S / 2 * MathF.Sin(rad);
                }
                // 回っている途中は、少し奥へ下げる (立方体の角が手前に出すぎないように)
                m.Oz += S / 2 * (1 - MathF.Cos(rad)) + S * 0.25f * MathF.Sin(MathF.PI * eu);
                return Shade(s, a, 1 - 0.35f * MathF.Abs(MathF.Sin(rad)));
            }
            case 73 or 74:   // カードを裏返す: 前半で出る側が真横まで回り、後半で入る側が裏から回ってくる
            {
                if (!Half(u, leaving, ref m, out _)) return s;
                float deg = leaving ? 180 * eu : -180 * (1 - eu);
                deg = Math.Clamp(deg, -90, 90);
                if (type == 73) m.Ry += deg; else m.Rx += deg;
                m.Oz += (type == 73 ? W : H) * 0.25f * MathF.Sin(MathF.PI * Math.Clamp(eu, 0, 1));
                return Shade(s, a, 1 - 0.4f * MathF.Abs(MathF.Sin(deg * MathF.PI / 180)));
            }
            case 75: return PageTurn(s, a, eu, leaving, G);
            case 76: return Glitch(s, a, u, leaving, G, ref m);
            case 77:   // 色収差: 色がずれながら切り替わる
            {
                if (!Half(u, leaving, ref m, out float w)) return s;
                w *= w;
                float amount = w * 0.06f * G.Width;
                m.Zoom *= 1 + 0.04f * w;
                if (amount < 0.5f) return s;
                // 全画面の絵は、はみ出さないよう端の色を使う。そうでなければ、ずれるぶんの余白を付ける
                bool clamp = a.Check("screen");
                int pad = clamp ? 0 : (int)MathF.Ceiling(amount) + 1;
                float cx = G.MidX + pad, cy = G.MidY + pad;
                return Resample(s, a, pad, pad, (src, dst, w2, h2, rb) =>
                {
                    PixelFx.ChromaticAberration(src, dst, w2, h2, rb, amount, 0, true, cx, cy, amount, clamp);
                    return dst;
                }) ?? s;
            }
            case 78: return LightLeak(s, a, u, leaving, G, ref m);
            case 79:   // ピクセル化: モザイクが粗くなっていき、粗いまま切り替わってから細かく戻る
            {
                if (!Half(u, leaving, ref m, out float w)) return s;
                int size = (int)MathF.Round(1 + w * w * G.Width / 14);
                if (size < 2) return s;
                return WithPixels(s, a, (px, w2, h2, rb) => PixelOps.Mosaic(px, w2, h2, rb, size), alsoOnGpu: true) ?? s;
            }
            case 80:   // 波紋: 波紋が強くなって切り替わり、収まる
            {
                if (!Half(u, leaving, ref m, out float w)) return s;
                float amp = w * 0.035f * G.Width;
                if (amp < 0.3f) return s;
                float cx = G.MidX, cy = G.MidY, wl = Math.Max(4, G.Width * 0.07f), phase = u * 24;
                return Resample(s, a, 0, 0, (src, dst, w2, h2, rb) =>
                {
                    PixelFx.Ripple(src, dst, w2, h2, rb, cx, cy, amp, wl, phase, 0, true);
                    return dst;
                }) ?? s;
            }
            case 81: return Swirl(s, a, u, leaving, G, ref m);
            case 82: return Burn(s, a, eu, leaving, G);
            case 83: return FilmBurn(s, a, u, leaving, G, ref m);
            case 84:   // 重力で落下: 出る側が下へ落ち、入る側が上から続いて落ちてくる (同じ速さなので隙間はできない)
            {
                float fall = H * u * u;
                m.Oy += leaving ? fall : fall - H;
                // 落ちている間だけ、少し傾く
                m.Rz += (Rand01(0, StableSeed(a.Object), 81) < 0.5f ? -1 : 1) * 4 * MathF.Sin(MathF.PI * u);
                return s;
            }
            case 85:   // 飛び去る: 左上から飛んできて、右上へ飛んでいく
            {
                float k = leaving ? eu * eu : (1 - eu) * (1 - eu);
                m.Ox += (leaving ? 1 : -1) * W * 0.7f * k;
                m.Oy -= H * 0.9f * k;
                m.Rz += (leaving ? -20 : 20) * k;
                m.Zoom *= 1 - 0.5f * k;
                m.Alpha *= 1 - k * k;
                return s;
            }
            default: return Melt(s, a, eu, leaving, G);
        }
    }

    /// <summary>その役 (入り・出) が、絵を形で切り抜くだけか (自分の絵を動かしたり描き足したりしない)。</summary>
    private static bool MaskOnly(int type, bool leaving) => type switch
    {
        >= 32 and <= 35 => leaving,                             // カバー: 覆われる側
        >= 36 and <= 39 => !leaving,                            // アンカバー: 現れる側
        >= 47 and <= 66 => true,                                // ワイプ類・ディゾルブ・ルールワイプ
        67 or 68 or 75 or 82 or 86 => !leaving,                 // 扉・ドア・ページめくり・焼けて消える・溶ける: 現れる側
        _ => false,
    };

    /// <summary>
    /// 前半は出ていく側、後半は入ってくる側だけを見せる。見せる方なら true と、強さ w (切り替わる瞬間 u = 0.5 で 1、両端で 0)。
    /// </summary>
    private static bool Half(float u, bool leaving, ref DrawModifiers m, out float w)
    {
        if (leaving ? u >= 0.5f : u < 0.5f) { m.Alpha = 0; w = 0; return false; }
        w = Math.Clamp(leaving ? u * 2 : (1 - u) * 2, 0, 1);
        return true;
    }

    /// <summary>明るさを掛ける (1 ならそのまま)。</summary>
    private static Sprite Shade(Sprite s, EffectArgs a, float k)
    {
        if (k >= 0.999f) return s;
        return ApplyMatrix(s, a, new float[] { k, 0, 0, 0, 0, 0, k, 0, 0, 0, 0, 0, k, 0, 0, 0, 0, 0, 1, 0 });
    }

    // ---------------- ホイップパン ----------------

    private static Sprite WhipPan(Sprite s, EffectArgs a, int dir, float u, int easeKind, bool leaving, int W, int H, float K, ref DrawModifiers m)
    {
        var (dx, dy) = FromDir(dir);
        float D = dx != 0 ? W : H;
        // 既定の動き方では、真ん中ですばやく動く (加減速を強めたもの)
        static float Whip(float t) => t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2;
        float At(float t) => easeKind == 0 ? Whip(Math.Clamp(t, 0, 1)) : (float)TransitionEase(t, easeKind);
        float w = At(u);
        float k = leaving ? w : -(1 - w);
        m.Ox += dx * D * k; m.Oy += dy * D * k;
        // 動く速さに合わせて、動く向きにぶらす
        const float du = 0.02f;
        float speed = MathF.Abs(At(u + du) - At(u - du)) / (2 * du);
        float len = MathF.Min(D * 0.12f * speed, D * 0.4f) * K;
        if (len < 1.5f) return s;
        int padX = (int)MathF.Ceiling(MathF.Abs(dx) * len / 2) + 1, padY = (int)MathF.Ceiling(MathF.Abs(dy) * len / 2) + 1;
        return Resample(s, a, padX, padY, (src, tmp, w2, h2, rb) => PixelFx.DirectionalBlur(src, tmp, w2, h2, rb, dx, dy, len, false, a.IsDraft ? 8 : 10)) ?? s;
    }

    // ---------------- 形で切り替えるもの ----------------

    /// <summary>入る側が見える範囲 (絵の座標)。出る側はこの外側を描く。</summary>
    private static SKPath WipePath(int type, SKRect G, float eu, float f)
    {
        var path = new SKPath();
        float cx = G.MidX, cy = G.MidY, half = MathF.Sqrt(G.Width * G.Width + G.Height * G.Height) / 2;
        var big = SKRect.Inflate(G, G.Width + f * 4 + 4, G.Height + f * 4 + 4);
        switch (type)
        {
            case >= 47 and <= 50:   // 斜めのワイプ: 角から対角の角へ、境界の直線を動かす
                LinearWipe(path, G, type is 47 or 49 ? G.Width : -G.Width, type is 47 or 48 ? G.Height : -G.Height, eu, f);
                break;
            case 52 or 53:   // 中央から開く
            {
                bool horizontal = type == 52;
                float hw = (G.Width / 2 + f) * eu, hh = (G.Height / 2 + f) * eu;
                path.AddRect(horizontal ? new SKRect(cx - hw, big.Top, cx + hw, big.Bottom) : new SKRect(big.Left, cy - hh, big.Right, cy + hh));
                break;
            }
            case 54 or 55:   // 中央へ閉じる: 両端から帯がのびる
            {
                if (type == 54)
                {
                    float w = -f + (G.Width / 2 + f * 2) * eu;
                    path.AddRect(new SKRect(big.Left, big.Top, G.Left + w, big.Bottom));
                    path.AddRect(new SKRect(G.Right - w, big.Top, big.Right, big.Bottom));
                }
                else
                {
                    float h = -f + (G.Height / 2 + f * 2) * eu;
                    path.AddRect(new SKRect(big.Left, big.Top, big.Right, G.Top + h));
                    path.AddRect(new SKRect(big.Left, G.Bottom - h, big.Right, big.Bottom));
                }
                break;
            }
            case 56:   // 四角形ワイプ (中心から)
            {
                float hw = (G.Width / 2 + f) * eu, hh = (G.Height / 2 + f) * eu;
                path.AddRect(new SKRect(cx - hw, cy - hh, cx + hw, cy + hh));
                break;
            }
            case 57:   // 四角形ワイプ (外から): 縮んでいく四角形の外側
            {
                float hw = (G.Width / 2 + f) * (1 - eu), hh = (G.Height / 2 + f) * (1 - eu);
                path.FillType = SKPathFillType.EvenOdd;
                path.AddRect(big);
                path.AddRect(new SKRect(cx - hw, cy - hh, cx + hw, cy + hh));
                break;
            }
            case 58:   // 円形ワイプ (外から)
            {
                float r = (half + f) * (1 - eu);
                path.FillType = SKPathFillType.EvenOdd;
                path.AddRect(big);
                path.AddCircle(cx, cy, Math.Max(0, r));
                break;
            }
            case 59:   // 星型ワイプ: 回りながら広がる (へこんだ角が画面の角に届けば全部見える)
            {
                float r = (half + f) * 2.05f * eu;
                Shapes.Polygon(path, new SKRect(cx - r, cy - r, cx + r, cy + r), 10, 0.5f);
                path.Transform(SKMatrix.CreateRotationDegrees(-72 * (1 - eu), cx, cy));
                break;
            }
            case 60:   // ハート型ワイプ
            {
                float size = (half + f) * 3.8f * eu;
                Shapes.Heart(path, new SKRect(cx - size / 2, cy - size * 0.55f, cx + size / 2, cy + size * 0.45f));
                break;
            }
            case 61 or 62:   // 反時計回り・扇形 (真上から両側へ開く)
            {
                float r = half * 2 + f * 2;
                var oval = new SKRect(cx - r, cy - r, cx + r, cy + r);
                path.MoveTo(cx, cy);
                if (type == 61) path.ArcTo(oval, -90, -359.99f * eu, false);
                else path.ArcTo(oval, -90 - 179.99f * eu, 359.98f * eu, false);
                path.Close();
                break;
            }
            case 63:   // チェッカーボード: 市松模様の片方が先に、もう片方が後から、マスの左から開く
            {
                int cols = 8, rows = Math.Max(1, (int)MathF.Round(cols * G.Height / Math.Max(1, G.Width)));
                float cw = G.Width / cols, ch = G.Height / rows;
                for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    float k = Math.Clamp(eu * 2 - ((i + j) & 1), 0, 1);
                    if (k <= 0) continue;
                    float x0 = G.Left + i * cw, y0 = G.Top + j * ch;
                    // 端のマスは外まで広げる (画面の外に余った絵を残さない)
                    float l = i == 0 ? big.Left : x0, t = j == 0 ? big.Top : y0, b = j == rows - 1 ? big.Bottom : y0 + ch + 0.5f;
                    float r = k >= 1 ? (i == cols - 1 ? big.Right : x0 + cw + 0.5f) : x0 + cw * k;
                    path.AddRect(new SKRect(l, t, r, b));
                }
                break;
            }
            default:   // ランダムブロック: マスがばらばらの順に、1 つずつ入れ替わる
            {
                int cols = 16, rows = Math.Max(1, (int)MathF.Round(cols * G.Height / Math.Max(1, G.Width)));
                float cw = G.Width / cols, ch = G.Height / rows;
                for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    if (eu <= Rand01((uint)(j * cols + i), 0x5EED, 61)) continue;
                    float x0 = G.Left + i * cw, y0 = G.Top + j * ch;
                    path.AddRect(new SKRect(i == 0 ? big.Left : x0, j == 0 ? big.Top : y0,
                        i == cols - 1 ? big.Right : x0 + cw + 0.5f, j == rows - 1 ? big.Bottom : y0 + ch + 0.5f));
                }
                break;
            }
        }
        return path;
    }

    /// <summary>(dx, dy) の向きに進む直線の境界。始め (eu = 0) は何も入らず、終わり (eu = 1) は G を全部含む。</summary>
    private static void LinearWipe(SKPath path, SKRect G, float dx, float dy, float eu, float f)
    {
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 0) { dx = 1; dy = 0; len = 1; }
        dx /= len; dy /= len;
        float nx = -dy, ny = dx;
        float amin = float.MaxValue, amax = float.MinValue, bmin = float.MaxValue, bmax = float.MinValue;
        foreach (var (x, y) in new[] { (G.Left, G.Top), (G.Right, G.Top), (G.Right, G.Bottom), (G.Left, G.Bottom) })
        {
            float pa = x * dx + y * dy, pb = x * nx + y * ny;
            amin = MathF.Min(amin, pa); amax = MathF.Max(amax, pa);
            bmin = MathF.Min(bmin, pb); bmax = MathF.Max(bmax, pb);
        }
        float pad = f * 2 + 2;
        float edge = amin - f + (amax - amin + f * 2) * eu;
        float a0 = amin - pad - (amax - amin), b0 = bmin - pad - (bmax - bmin), b1 = bmax + pad + (bmax - bmin);
        SKPoint P(float pa, float pb) => new(pa * dx + pb * nx, pa * dy + pb * ny);
        path.MoveTo(P(a0, b0)); path.LineTo(P(edge, b0)); path.LineTo(P(edge, b1)); path.LineTo(P(a0, b1));
        path.Close();
    }

    private static Sprite ShapeWipe(Sprite s, EffectArgs a, int type, float eu, bool leaving, SKRect G, float K, float feather)
    {
        // マスが多いものは、ぼかしを大きくすると隙間が目立つので小さくする
        // マスに分かれるものは、境目をぼかすと 2 つの絵の両方が薄くなって線が見えるので、ぼかさずくっきり分ける
        bool cells = type is 63 or 64;
        float f = cells ? 0 : feather;
        if (type == 51)
        {
            // 角度指定: 0 度で左から右へ、90 度で上から下へ進む
            float rad = a.Track("angle") * MathF.PI / 180;
            using var angled = new SKPath();
            LinearWipe(angled, G, MathF.Cos(rad), MathF.Sin(rad), eu, f);
            return WipeWithEdge(s, a, angled, f, leaving, K);
        }
        using var path = WipePath(type, G, eu, f);
        return WipeWithEdge(s, a, path, f, leaving, K, !cells);
    }

    /// <summary>入る側は path の内側、出る側は外側を描く。入る側には境界線 (path の内側半分) を描く。</summary>
    private static Sprite WipeWithEdge(Sprite s, EffectArgs a, SKPath path, float feather, bool leaving, float K, bool antialias = true)
    {
        float edge = leaving ? 0 : a.Track("edge") * K;
        if (antialias && edge <= 0) return Masked(s, a, path, feather, leaving);
        var color = a.Color("edgeColor");
        return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
        {
            c.DrawImage(s.Image, x, y);
            using (var layerPaint = new SKPaint { BlendMode = leaving ? SKBlendMode.DstOut : SKBlendMode.DstIn })
            {
                c.SaveLayer(layerPaint);
                using var shapePaint = new SKPaint { IsAntialias = antialias, Color = SKColors.White };
                if (feather > 0) shapePaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, feather / 2);
                c.Translate(x, y);
                c.DrawPath(path, shapePaint);
                c.Restore();
            }
            if (edge <= 0) return;
            // 境界線: 形の内側だけ (出る側の絵に隠れない所) に、絵の見えている所だけ描く
            c.Save();
            c.Translate(x, y);
            c.ClipPath(path, SKClipOperation.Intersect, true);
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = edge * 2, Color = color, BlendMode = SKBlendMode.SrcATop };
            c.DrawPath(path, stroke);
            c.Restore();
        });
    }

    // ---------------- ピクセルごとに決めるもの ----------------

    /// <summary>ディゾルブ (粒): 画面の 2 ピクセル四方の粒が、ばらばらの順に入れ替わる。</summary>
    private static unsafe Sprite Dissolve(Sprite s, EffectArgs a, float eu, bool leaving, SKRect G, float K)
    {
        float grain = MathF.Max(1, 2 * K), gl = G.Left, gt = G.Top;
        const float Soft = 0.06f;
        float th = eu * (1 + Soft);
        return WithPixels(s, a, (px, w, h, rb) =>
        {
            uint* basePtr = (uint*)px;
            int stride = rb / 4;
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* row = basePtr + (long)y * stride;
                    uint gy = (uint)(int)MathF.Floor((y + 0.5f - gt) / grain);
                    for (int x = 0; x < w; x++)
                    {
                        uint gx = (uint)(int)MathF.Floor((x + 0.5f - gl) / grain);
                        float n = PixelFx.Hash(gx, gy, 0xD155) / (float)uint.MaxValue;
                        float k = Math.Clamp((th - n) / Soft, 0, 1);
                        if (leaving) k = 1 - k;
                        row[x] = ScalePixel(row[x], k);
                    }
                }
            });
        }, alsoOnGpu: true) ?? s;
    }

    /// <summary>乗算済みのピクセル全体 (色と不透明度) に k (0〜1) を掛ける。</summary>
    private static uint ScalePixel(uint c, float k)
    {
        if (k >= 1) return c;
        if (k <= 0) return 0;
        uint t = (uint)(k * 256);
        uint rb = ((c & 0x00FF00FFu) * t >> 8) & 0x00FF00FFu;
        uint ag = (((c >> 8) & 0x00FF00FFu) * t) & 0xFF00FF00u;
        return rb | ag;
    }

    private sealed class RuleMap
    {
        public required byte[] Luma;
        public required int Width, Height;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKImage, RuleMap> RuleMaps = new();

    /// <summary>ルール画像の明るさ (0〜255)。読めなければ null。</summary>
    private static RuleMap? RuleOf(SKImage img)
    {
        if (RuleMaps.TryGetValue(img, out var cached)) return cached;
        int w = Math.Max(1, img.Width), h = Math.Max(1, img.Height);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bytes = new byte[w * h * 4];
        unsafe
        {
            fixed (byte* b = bytes)
                if (!img.ReadPixels(info, (IntPtr)b, w * 4, 0, 0)) return null;
        }
        var luma = new byte[w * h];
        for (int i = 0; i < luma.Length; i++)
            luma[i] = (byte)((bytes[i * 4 + 2] * 54 + bytes[i * 4 + 1] * 183 + bytes[i * 4] * 19) >> 8);
        var map = new RuleMap { Luma = luma, Width = w, Height = h };
        RuleMaps.AddOrUpdate(img, map);
        return map;
    }

    /// <summary>ルールワイプ: グレースケールの画像の暗い所 (反転なら明るい所) から順に切り替わる。画像がなければ左からのワイプ。</summary>
    private static unsafe Sprite RuleWipe(Sprite s, EffectArgs a, float eu, bool leaving, SKRect G)
    {
        string path = a.Effect.GetString("rule");
        RuleMap? rule = null;
        if (!string.IsNullOrEmpty(path) && a.Context.LoadImage?.Invoke(path) is { } img) rule = RuleOf(img);
        bool invert = a.Check("ruleInvert");
        float soft = Math.Clamp(a.Track("feather") / 100f, 0.01f, 1);
        float th = eu * (1 + soft);
        float gl = G.Left, gt = G.Top, gw = Math.Max(1, G.Width), gh = Math.Max(1, G.Height);
        return WithPixels(s, a, (px, w, h, rb) =>
        {
            uint* basePtr = (uint*)px;
            int stride = rb / 4;
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* row = basePtr + (long)y * stride;
                    float vy = (y + 0.5f - gt) / gh;
                    for (int x = 0; x < w; x++)
                    {
                        float vx = (x + 0.5f - gl) / gw, n;
                        if (rule != null)
                        {
                            int rx = Math.Clamp((int)(vx * rule.Width), 0, rule.Width - 1), ry = Math.Clamp((int)(vy * rule.Height), 0, rule.Height - 1);
                            n = rule.Luma[ry * rule.Width + rx] / 255f;
                        }
                        else n = Math.Clamp(vx, 0, 1);
                        if (invert) n = 1 - n;
                        // 0〜1 の明るさを、しきい値の幅 soft のぶんだけ縮めて、始めと終わりに全部消える / 見えるようにする
                        float k = Math.Clamp((th - n) / soft, 0, 1);
                        if (leaving) k = 1 - k;
                        row[x] = ScalePixel(row[x], k);
                    }
                }
            });
        }, alsoOnGpu: true) ?? s;
    }

    // ---------------- 立体的に動かすもの (絵の中で遠近をつけて描く) ----------------

    /// <summary>四角形 src を四角形 (4 点: 左上・右上・右下・左下) へ写す射影変換。</summary>
    internal static SKMatrix RectToQuad(SKRect src, SKPoint[] q)
    {
        float x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        float dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
        float dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
        float a, b, c = x0, d, e, f = y0, g = 0, h = 0;
        if (MathF.Abs(dx3) < 1e-6f && MathF.Abs(dy3) < 1e-6f)
        {
            a = x1 - x0; b = x3 - x0; d = y1 - y0; e = y3 - y0;
        }
        else
        {
            float det = dx1 * dy2 - dx2 * dy1;
            if (MathF.Abs(det) < 1e-9f) det = 1e-9f;
            g = (dx3 * dy2 - dx2 * dy3) / det;
            h = (dx1 * dy3 - dx3 * dy1) / det;
            a = x1 - x0 + g * x1; b = x3 - x0 + h * x3; d = y1 - y0 + g * y1; e = y3 - y0 + h * y3;
        }
        var unit = new SKMatrix { ScaleX = a, SkewX = b, TransX = c, SkewY = d, ScaleY = e, TransY = f, Persp0 = g, Persp1 = h, Persp2 = 1 };
        var toUnit = SKMatrix.CreateScale(1 / Math.Max(1e-6f, src.Width), 1 / Math.Max(1e-6f, src.Height)).PreConcat(SKMatrix.CreateTranslation(-src.Left, -src.Top));
        return unit.PreConcat(toUnit);
    }

    /// <summary>need (絵の座標) を含むように余白を付けて描き直す。draw は絵の座標で描く。</summary>
    private static Sprite RedrawCovering(Sprite s, EffectArgs a, SKRect need, Action<SKCanvas> draw)
    {
        const int Limit = 4096;
        int padL = Math.Clamp((int)MathF.Ceiling(-need.Left), 0, Limit), padT = Math.Clamp((int)MathF.Ceiling(-need.Top), 0, Limit);
        int padR = Math.Clamp((int)MathF.Ceiling(need.Right - s.Width), 0, Limit), padB = Math.Clamp((int)MathF.Ceiling(need.Bottom - s.Height), 0, Limit);
        var r = Redraw(s, a, padL, padT, padR, padB, (c, x, y) =>
        {
            c.Save();
            c.Translate(x, y);
            draw(c);
            c.Restore();
        });
        return r with { Scale = s.Scale };
    }

    /// <summary>
    /// 扉 (観音開き)・ドア (片開き): 出る側が、画面の端を軸に手前へ開いていく。入る側は扉の外側に見える。
    /// 手前へ開くので、開ききる (90 度) ころには扉が画面の外へ出ていき、自然に見えなくなる
    /// (奥へ開くと、遠近のせいで 90 度でも扉の内側が見えたまま残る)。
    /// </summary>
    private static Sprite Doors(Sprite s, EffectArgs a, bool two, float eu, bool leaving, SKRect G)
    {
        float theta = Math.Clamp(eu, 0, 1) * 90 * MathF.PI / 180;
        // 視点までの距離は扉の幅より十分長くする (扉の端がカメラの後ろへ回り込まず、開ききる直前まで画面に残るように)
        float focal = 2.5f * (two ? G.Width / 2 : G.Width), cx = G.MidX, cy = G.MidY;
        SKPoint Project(float x, float y, float z) => new(cx + (x - cx) * focal / (focal + z), cy + (y - cy) * focal / (focal + z));
        var panels = new List<(SKRect Src, SKPoint[] Quad)>();
        void Panel(SKRect src, bool hingeLeft)
        {
            float width = src.Width, hinge = hingeLeft ? src.Left : src.Right, dir = hingeLeft ? 1 : -1;
            float fx = hinge + dir * width * MathF.Cos(theta), fz = -width * MathF.Sin(theta);   // 手前 (z が負) へ開く
            var near1 = Project(hinge, src.Top, 0); var near2 = Project(hinge, src.Bottom, 0);
            var far1 = Project(fx, src.Top, fz); var far2 = Project(fx, src.Bottom, fz);
            panels.Add((src, hingeLeft ? new[] { near1, far1, far2, near2 } : new[] { far1, near1, near2, far2 }));
        }
        if (two)
        {
            Panel(new SKRect(G.Left, G.Top, cx, G.Bottom), true);
            Panel(new SKRect(cx, G.Top, G.Right, G.Bottom), false);
        }
        else Panel(G, true);

        if (!leaving)
        {
            using var covered = new SKPath();
            foreach (var (_, q) in panels) covered.AddPoly(q, true);
            return Masked(s, a, covered, 0, true);
        }
        byte k = (byte)Math.Clamp(255 * (1 - 0.55f * MathF.Sin(theta)), 0, 255);
        // 扉は画面の外まで大きくなるが、描くのは画面 (G) の少し外までで足りる
        var need = SKRect.Union(SKRect.Create(0, 0, s.Width, s.Height), SKRect.Inflate(G, G.Width * 0.25f, G.Height * 0.25f));
        return RedrawCovering(s, a, need, c =>
        {
            using var cf = SKColorFilter.CreateLighting(new SKColor(k, k, k), SKColors.Black);
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium, ColorFilter = cf };
            foreach (var (src, q) in panels)
            {
                c.Save();
                var mtx = RectToQuad(src, q);
                c.Concat(ref mtx);
                c.ClipRect(src, SKClipOperation.Intersect, true);
                c.DrawImage(s.Image, 0, 0, paint);
                c.Restore();
            }
        });
    }

    /// <summary>
    /// ページめくり: 出る側の右下の角が、紙を折り返すようにめくれて左上へ抜けていく。
    /// 折り目は対角線に垂直な直線。折り返した部分 (裏) は白っぽく、折り目の近くを暗くする。
    /// </summary>
    private static Sprite PageTurn(Sprite s, EffectArgs a, float eu, bool leaving, SKRect G)
    {
        float diag = MathF.Sqrt(G.Width * G.Width + G.Height * G.Height);
        float dx = -G.Width / diag, dy = -G.Height / diag;             // 右下から左上へ
        float c0 = dx * G.Right + dy * G.Bottom + Math.Clamp(eu, 0, 1) * diag;   // 折り目: dx·x + dy·y = c0
        // 折り目の手前 (右下側) と奥 (左上側) の半平面を、G を覆う大きさの四角形で作る
        float big = diag * 4;
        SKPath HalfPlane(bool nearCorner)
        {
            float nx = -dy, ny = dx;
            float a0 = nearCorner ? c0 - big : c0, a1 = nearCorner ? c0 : c0 + big;
            SKPoint P(float pa, float pb) => new(pa * dx + pb * nx, pa * dy + pb * ny);
            float mb = nx * G.MidX + ny * G.MidY;
            var hp = new SKPath();
            hp.MoveTo(P(a0, mb - big)); hp.LineTo(P(a1, mb - big)); hp.LineTo(P(a1, mb + big)); hp.LineTo(P(a0, mb + big));
            hp.Close();
            return hp;
        }
        using var page = new SKPath();
        page.AddRect(G);
        using var nearHalf = HalfPlane(true);
        using var farHalf = HalfPlane(false);
        using var flap = page.Op(nearHalf, SKPathOp.Intersect) ?? new SKPath();
        using var remain = page.Op(farHalf, SKPathOp.Intersect) ?? new SKPath();
        // 折り目の直線で鏡に写す
        var reflect = new SKMatrix
        {
            ScaleX = 1 - 2 * dx * dx, SkewX = -2 * dx * dy, TransX = 2 * c0 * dx,
            SkewY = -2 * dx * dy, ScaleY = 1 - 2 * dy * dy, TransY = 2 * c0 * dy, Persp2 = 1,
        };
        using var flapOnTop = new SKPath(flap);
        flapOnTop.Transform(reflect);

        if (!leaving)
        {
            using var covered = remain.Op(flapOnTop, SKPathOp.Union) ?? new SKPath();
            return Masked(s, a, covered, 0, true);
        }
        var need = SKRect.Union(SKRect.Union(SKRect.Create(0, 0, s.Width, s.Height), G), flapOnTop.Bounds);
        return RedrawCovering(s, a, need, c =>
        {
            c.Save();
            c.ClipPath(remain, SKClipOperation.Intersect, true);
            c.DrawImage(s.Image, 0, 0);
            c.Restore();
            if (flap.IsEmpty) return;
            // 折り返した紙の影 (めくれた部分のすぐ外側)
            using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 70), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1, diag * 0.01f)) })
            {
                c.Save();
                c.ClipPath(remain, SKClipOperation.Intersect, true);
                c.DrawPath(flapOnTop, shadow);
                c.Restore();
            }
            c.Save();
            c.Concat(ref reflect);
            c.ClipPath(flap, SKClipOperation.Intersect, true);
            // 裏面: 絵が透けて見える白っぽい紙
            using (var cf = SKColorFilter.CreateLighting(new SKColor(90, 90, 90), new SKColor(170, 170, 170)))
            using (var back = new SKPaint { ColorFilter = cf, FilterQuality = SKFilterQuality.Medium })
                c.DrawImage(s.Image, 0, 0, back);
            // 折り目の近くを暗くする (紙の丸み)
            var p0 = new SKPoint(c0 * dx, c0 * dy);
            var p1 = new SKPoint((c0 - diag * 0.25f) * dx, (c0 - diag * 0.25f) * dy);
            using var grad = SKShader.CreateLinearGradient(p0, p1, new[] { new SKColor(0, 0, 0, 90), new SKColor(0, 0, 0, 0) }, SKShaderTileMode.Clamp);
            using var gp = new SKPaint { Shader = grad, BlendMode = SKBlendMode.SrcATop };
            c.DrawRect(flap.Bounds, gp);
            c.Restore();
        });
    }

    // ---------------- 前半・後半で切り替える質感のもの ----------------

    /// <summary>グリッチ: 横の帯がばらばらにずれ、色が分かれる (毎フレーム変わる)。</summary>
    private static unsafe Sprite Glitch(Sprite s, EffectArgs a, float u, bool leaving, SKRect G, ref DrawModifiers m)
    {
        if (!Half(u, leaving, ref m, out float w)) return s;
        float g = w * MathF.Sqrt(w);
        if (g < 0.02f) return s;
        uint seed = (uint)a.Frame * 2654435761u;
        float bandUnit = MathF.Max(2, G.Height * 0.025f), maxShift = g * 0.12f * G.Width, split = g * 0.015f * G.Width;
        float gt = G.Top;
        // 画面全体を基準にするとき (全画面の絵) は、ずれて空いた所を端の色で埋める
        bool clamp = a.Check("screen");
        return Resample(s, a, 0, 0, (src, dst, w2, h2, rb) =>
        {
            uint* sp = (uint*)src, dp = (uint*)dst;
            int stride = rb / 4;
            PixelOps.ForBands(w2, h2, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    // 帯の高さもばらばらにするため、2 段階の帯で決める
                    int band = (int)MathF.Floor((y - gt) / bandUnit), coarse = band >> 2;
                    float pick = Rand01((uint)band, seed, 71);
                    float off = 0;
                    if (pick < 0.25f + 0.45f * g) off = (Rand01((uint)band, seed, 72) * 2 - 1) * maxShift;
                    else if (Rand01((uint)coarse, seed, 73) < 0.3f * g) off = (Rand01((uint)coarse, seed, 74) * 2 - 1) * maxShift * 0.5f;
                    uint* row = dp + (long)y * stride;
                    for (int x = 0; x < w2; x++)
                    {
                        uint r = PixelFx.Sample(sp, w2, h2, stride, x - off - split, y, clamp);
                        uint c = PixelFx.Sample(sp, w2, h2, stride, x - off, y, clamp);
                        uint b = PixelFx.Sample(sp, w2, h2, stride, x - off + split, y, clamp);
                        uint al = Math.Max(r >> 24, Math.Max(c >> 24, b >> 24));
                        row[x] = (r & 0xFF0000u) | (c & 0xFF00u) | (b & 0xFFu) | al << 24;
                    }
                }
            });
            return dst;
        }) ?? s;
    }

    /// <summary>光漏れ: 暖かい色の光が横切りながら強くなって切り替わり、引いていく。</summary>
    private static Sprite LightLeak(Sprite s, EffectArgs a, float u, bool leaving, SKRect G, ref DrawModifiers m)
    {
        if (!Half(u, leaving, ref m, out float w)) return s;
        if (w < 0.01f) return s;
        float k = w * MathF.Sqrt(w);
        float lx = G.Left + G.Width * (-0.2f + 1.4f * u), ly = G.Top + G.Height * 0.35f;
        float radius = MathF.Max(G.Width, G.Height) * (0.35f + 0.7f * k);
        byte A(float v) => (byte)Math.Clamp(v * 255, 0, 255);
        return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
        {
            c.DrawImage(s.Image, x, y);
            using var layerPaint = new SKPaint { BlendMode = SKBlendMode.Screen };
            c.SaveLayer(layerPaint);
            c.Translate(x, y);
            using (var g1 = SKShader.CreateRadialGradient(new SKPoint(lx, ly), radius,
                       new[] { new SKColor(255, 214, 150, A(k)), new SKColor(255, 110, 40, A(k * 0.75f)), new SKColor(255, 60, 90, 0) }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp))
            using (var p1 = new SKPaint { Shader = g1 })
                c.DrawRect(SKRect.Create(0, 0, s.Width, s.Height), p1);
            float lx2 = G.Right - G.Width * (-0.1f + 1.2f * u), ly2 = G.Bottom - G.Height * 0.2f;
            using (var g2 = SKShader.CreateRadialGradient(new SKPoint(lx2, ly2), radius * 0.7f,
                       new[] { new SKColor(255, 120, 170, A(k * 0.6f)), new SKColor(255, 120, 170, 0) }, SKShaderTileMode.Clamp))
            using (var p2 = new SKPaint { Shader = g2 })
                c.DrawRect(SKRect.Create(0, 0, s.Width, s.Height), p2);
            // 切り替わる瞬間は、ほぼ白に飛ばす
            float white = Math.Clamp((k - 0.7f) / 0.3f, 0, 1);
            if (white > 0)
                using (var p3 = new SKPaint { Color = new SKColor(255, 244, 228, A(white)) })
                    c.DrawRect(SKRect.Create(0, 0, s.Width, s.Height), p3);
            // 光は絵の見えている所だけに乗せる
            using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
                c.DrawImage(s.Image, 0, 0, keep);
            c.Restore();
        });
    }

    /// <summary>渦巻き: 中心ほど強くねじれて切り替わる。</summary>
    private static unsafe Sprite Swirl(Sprite s, EffectArgs a, float u, bool leaving, SKRect G, ref DrawModifiers m)
    {
        if (!Half(u, leaving, ref m, out float w)) return s;
        float maxAngle = 4 * MathF.PI * w * w;
        if (maxAngle < 0.01f) return s;
        float cx = G.MidX, cy = G.MidY, R = MathF.Max(1, MathF.Sqrt(G.Width * G.Width + G.Height * G.Height) / 2);
        return Resample(s, a, 0, 0, (src, dst, w2, h2, rb) =>
        {
            uint* sp = (uint*)src, dp = (uint*)dst;
            int stride = rb / 4;
            PixelOps.ForBands(w2, h2, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* row = dp + (long)y * stride;
                    for (int x = 0; x < w2; x++)
                    {
                        float ox = x - cx, oy = y - cy, r = MathF.Sqrt(ox * ox + oy * oy);
                        if (r >= R) { row[x] = sp[(long)y * stride + x]; continue; }
                        float t = 1 - r / R, ang = maxAngle * t * t;
                        float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
                        row[x] = PixelFx.Sample(sp, w2, h2, stride, cx + ox * cs - oy * sn, cy + ox * sn + oy * cs, true);
                    }
                }
            });
            return dst;
        }) ?? s;
    }

    /// <summary>なめらかな値の乱数 (格子の乱数を補間したもの、0〜1)。</summary>
    private static float ValueNoise(float x, float y, uint seed)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int ix = (int)fx, iy = (int)fy;
        float tx = x - fx, ty = y - fy;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        float N(int i, int j) => PixelFx.Hash((uint)i, (uint)j, seed) / (float)uint.MaxValue;
        float a = N(ix, iy), b = N(ix + 1, iy), c = N(ix, iy + 1), d = N(ix + 1, iy + 1);
        return a + (b - a) * tx + (c - a + (d - c - b + a) * tx) * ty;
    }

    /// <summary>焼けて消える: 出る側がまだらに焼けて (縁が燃える色に光って) 消え、その跡に入る側が見える。</summary>
    private static unsafe Sprite Burn(Sprite s, EffectArgs a, float eu, bool leaving, SKRect G)
    {
        const float Band = 0.08f;
        float th = eu * (1 + Band * 2) - Band;
        float gl = G.Left, gt = G.Top, scale = 5 / Math.Max(1, G.Width);
        return WithPixels(s, a, (px, w, h, rb) =>
        {
            uint* basePtr = (uint*)px;
            int stride = rb / 4;
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* row = basePtr + (long)y * stride;
                    float ny = (y + 0.5f - gt) * scale;
                    for (int x = 0; x < w; x++)
                    {
                        float nx = (x + 0.5f - gl) * scale;
                        float n = ValueNoise(nx, ny, 0xB0) * 0.55f + ValueNoise(nx * 2.3f, ny * 2.3f, 0xB1) * 0.3f + ValueNoise(nx * 5.1f, ny * 5.1f, 0xB2) * 0.15f;
                        // 焼け跡 (入る側が見える所) は n < th
                        float burned = Math.Clamp((th - n) / 0.01f + 0.5f, 0, 1);
                        if (!leaving) { row[x] = ScalePixel(row[x], burned); continue; }
                        uint c = ScalePixel(row[x], 1 - burned);
                        float d = (n - th) / Band;
                        if (d >= 0 && d < 1 && c >> 24 != 0)
                        {
                            // 燃えている縁: 焼け跡に近いほど明るい黄色、離れると暗い茶色
                            float heat = 1 - d;
                            float fr = 255, fg = 60 + 190 * heat * heat, fb = 20 + 150 * heat * heat * heat;
                            if (heat < 0.35f) { float k2 = heat / 0.35f; fr = 90 + 165 * k2; fg *= k2; fb *= k2; }
                            float mix = MathF.Pow(heat, 0.6f);
                            uint al = c >> 24;
                            float pa = al / 255f;
                            uint cr = (uint)(((c >> 16) & 0xFF) * (1 - mix) + fr * pa * mix), cg = (uint)(((c >> 8) & 0xFF) * (1 - mix) + fg * pa * mix), cb = (uint)((c & 0xFF) * (1 - mix) + fb * pa * mix);
                            c = al << 24 | Math.Min(al, cr) << 16 | Math.Min(al, cg) << 8 | Math.Min(al, cb);
                        }
                        row[x] = c;
                    }
                }
            });
        }, alsoOnGpu: true) ?? s;
    }

    /// <summary>フィルム焼け: フィルムが焼けたような、まだらなオレンジの光が広がって白く飛び、引いていく。</summary>
    private static unsafe Sprite FilmBurn(Sprite s, EffectArgs a, float u, bool leaving, SKRect G, ref DrawModifiers m)
    {
        if (!Half(u, leaving, ref m, out float w)) return s;
        if (w < 0.02f) return s;
        float gl = G.Left, gt = G.Top, scale = 3 / Math.Max(1, G.Width), drift = u * 3;
        float level = w * 1.35f;
        return WithPixels(s, a, (px, wd, h, rb) =>
        {
            uint* basePtr = (uint*)px;
            int stride = rb / 4;
            PixelOps.ForBands(wd, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* row = basePtr + (long)y * stride;
                    float ny = (y + 0.5f - gt) * scale;
                    for (int x = 0; x < wd; x++)
                    {
                        uint c = row[x];
                        uint al = c >> 24;
                        if (al == 0) continue;
                        float nx = (x + 0.5f - gl) * scale;
                        // 左から右へ流れながら、まだらに焼ける
                        float n = ValueNoise(nx - drift, ny, 0xF1) * 0.65f + ValueNoise(nx * 2.7f - drift * 1.5f, ny * 2.7f, 0xF2) * 0.35f;
                        n = n * 0.8f + (1 - Math.Clamp((nx - drift * 0.3f) / 3, 0, 1)) * 0.2f;
                        float b = Math.Clamp((level - n) * 2.5f, 0, 1);
                        if (b <= 0) continue;
                        // 0 → 赤橙 → 橙 → 黄 → 白
                        float fr = 255, fg = 40 + 215 * MathF.Min(1, b * 1.3f), fb = 10 + 245 * MathF.Max(0, b * 2 - 1);
                        float pa = al / 255f;
                        // スクリーン合成 (明るくなるだけ)
                        uint Screen(uint ch, float f) => (uint)Math.Min(al, ch + (f * pa - ch * f / 255f) * MathF.Min(1, b * 1.5f));
                        row[x] = al << 24 | Screen((c >> 16) & 0xFF, fr) << 16 | Screen((c >> 8) & 0xFF, fg) << 8 | Screen(c & 0xFF, fb);
                    }
                }
            });
        }, alsoOnGpu: true) ?? s;
    }

    /// <summary>溶ける: 出る側が細い縦の帯ごとに、ばらばらの速さで下へ溶け落ちていく。入る側は上から見えてくる。</summary>
    private static Sprite Melt(Sprite s, EffectArgs a, float eu, bool leaving, SKRect G)
    {
        const int Columns = 48;
        float cw = Math.Max(1, G.Width / Columns);
        int first = (int)MathF.Floor((Math.Min(0, G.Left) - G.Left) / cw) - 1, last = (int)MathF.Ceiling((Math.Max(s.Width, G.Right) - G.Left) / cw) + 1;
        float Delay(int i)
        {
            float R(int j) => Rand01((uint)j, 0x3E17, 91);
            return (R(i) * 2 + R(i - 1) + R(i + 1)) / 4;
        }
        // 整数ピクセルにそろえる (絵をずらして描く所と、入る側を切り抜く縁がぴったり合うように)
        float Drop(int i) => MathF.Round(G.Height * 1.05f * MathF.Pow(Math.Clamp((eu - 0.4f * Delay(i)) / 0.6f, 0, 1), 2));
        if (!leaving)
        {
            using var path = new SKPath();
            for (int i = first; i <= last; i++)
            {
                float x0 = G.Left + i * cw, d = Drop(i);
                if (d <= 0) continue;
                // 1 ピクセルずつ広げて重ねる (絵の位置と切り抜く縁の、ピクセルの端数で隙間ができないように)
                path.AddRect(new SKRect(x0 - 1, G.Top - G.Height * 2, x0 + cw + 1, G.Top + d + 1));
            }
            return Masked(s, a, path, 0, false);
        }
        var drops = new float[last - first + 1];
        for (int i = first; i <= last; i++) drops[i - first] = Drop(i);
        return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
        {
            for (int i = first; i <= last; i++)
            {
                float x0 = G.Left + i * cw;
                c.Save();
                c.ClipRect(new SKRect(x + x0, y - 1, x + x0 + cw + (i == last ? 0 : 0.5f), y + s.Height + 1));
                c.DrawImage(s.Image, x, y + drops[i - first]);
                c.Restore();
            }
        });
    }
}
