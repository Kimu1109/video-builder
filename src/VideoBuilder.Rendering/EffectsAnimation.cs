using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// エフェクト「アニメーション効果」のうち、14 番以降の種類と、どの種類にも効く設定 (回転・拡大の中心)。
/// 時間 t は、開始の遅延・繰り返す回数・位相をかけたあとの値 (秒) が渡される。
/// </summary>
public static partial class Effects
{
    /// <summary>
    /// 「速さ (回/秒)」で繰り返す種類か。繰り返す回数・位相は、繰り返す種類にだけ効く
    /// (振動・リール回転・手ぶれ・ゆっくりズーム・パンには効かない)。
    /// </summary>
    internal static bool IsPeriodicAnimation(int type) => type is not (0 or 13 or 26 or 27);

    /// <summary>オブジェクトの標準描画の拡大率 (1 = 等倍)。</summary>
    private static float DrawScaleOf(EffectArgs a)
    {
        if (a.Object?.Draw is { } draw && a.Bounds != null) return (float)draw.Eval("scale", a.Bounds, a.Frame) / 100f;
        return 1;
    }

    /// <summary>
    /// 回転・拡大の中心 (中心X・中心Y) を、オブジェクトの中心からずらす。
    /// この効果が足した回転 (Z 軸) と拡大で中心の点が動いたぶんを、位置で打ち消す。
    /// </summary>
    private static void ApplyAnimationAnchor(Sprite s, EffectArgs a, DrawModifiers before)
    {
        float ax = a.Track("ax"), ay = a.Track("ay");
        if (ax == 0 && ay == 0) return;
        ref var m = ref a.Context.Modifiers;
        float dz = before.Zoom != 0 ? m.Zoom / before.Zoom : 1;
        float rad = (m.Rz - before.Rz) * MathF.PI / 180;
        if (MathF.Abs(dz - 1) < 1e-6f && MathF.Abs(rad) < 1e-6f) return;
        float k = DrawScaleOf(a) * before.Zoom;
        float px = ax * k, py = ay * k, cs = MathF.Cos(rad), sn = MathF.Sin(rad);
        m.Ox += px - (px * cs - py * sn) * dz;
        m.Oy += py - (px * sn + py * cs) * dz;
    }

    /// <summary>明るさを変える (b が正なら白へ近づけ、負なら暗くする。-1〜1)。</summary>
    private static Sprite Brighten(Sprite s, EffectArgs a, float b)
    {
        b = Math.Clamp(b, -1, 1);
        if (MathF.Abs(b) < 0.002f) return s;
        float keep = b > 0 ? 1 - b : 1 + b, add = b > 0 ? b : 0;
        return ApplyMatrix(s, a, new float[] { keep, 0, 0, 0, add, 0, keep, 0, 0, add, 0, 0, keep, 0, add, 0, 0, 0, 1, 0 });
    }

    private static Sprite MoreAnimation(Sprite s, EffectArgs a, int type, double t, float speed)
    {
        float strength = a.Track("strength");
        double cycles = speed * t, phase = 2 * Math.PI * cycles;
        float sin = (float)Math.Sin(phase), cos = (float)Math.Cos(phase);
        float wave = (float)(0.5 - 0.5 * Math.Cos(phase));   // 0 → 1 → 0
        ref var m = ref a.Context.Modifiers;
        // 楕円軌道・8 の字: 移動量Y が 0 なら、移動量X の半分にする (既定のままでも形になるように)
        float RadiusY(float rx) { float ry = a.Track("dy"); return ry == 0 ? rx / 2 : ry; }
        switch (type)
        {
            case 14:   // 円運動: 元の位置を中心に、半径 (強さ) の円を回る
                m.Ox += strength * cos; m.Oy += strength * sin;
                return s;
            case 15:   // 楕円軌道
            {
                float rx = a.Track("dx");
                m.Ox += rx * cos; m.Oy += RadiusY(rx) * sin;
                return s;
            }
            case 16:   // 8 の字: 横に 1 往復するあいだに、縦に 2 往復する
            {
                float rx = a.Track("dx");
                m.Ox += rx * sin; m.Oy += RadiusY(rx) * (float)Math.Sin(2 * phase);
                return s;
            }
            case 17: return Jelly(s, a, strength / 100f * sin);
            case 18:   // 呼吸: ゆっくり大きくなりながら明るくなり、戻る
                m.Zoom *= 1 + strength / 100f * wave;
                return Brighten(s, a, a.Track("glow") / 100f * wave);
            case 19: return ApplyMatrix(s, a, HueRotate((float)(360 * (cycles % 1))));   // 色相が回る
            case 20: return Rainbow(s, a, (float)(cycles % 1));
            case 21: return Brighten(s, a, strength / 100f * wave);                      // 明るさの脈動
            case 22:   // 心臓の鼓動: 1 回のうちに、強い拍と弱い拍の 2 回ふくらむ
            {
                double c = cycles - Math.Floor(cycles);
                static double Bump(double c, double at, double width)
                {
                    double d = Math.Abs(c - at);
                    d = Math.Min(d, 1 - d);
                    return Math.Exp(-(d / width) * (d / width));
                }
                m.Zoom *= 1 + strength / 100f * (float)(Bump(c, 0.08, 0.06) + 0.6 * Bump(c, 0.3, 0.06));
                return s;
            }
            case 23: m.Ry += strength * sin; return s;   // 首振り (Y 軸で左右に向く)
            case 24: return Flag(s, a, strength, (float)cycles);
            case 25: return HopBackAndForth(s, a, strength, cycles, ref m);
            case 26:   // 手ぶれ: ゆっくりした、不規則な揺れ (周波数の違う正弦波を重ねる)
            {
                uint seed = (uint)(int)a.Track("seed");
                float Noise(uint axis)
                {
                    float v = 0;
                    (double F, float A)[] layers = { (1, 0.6f), (2.17, 0.3f), (4.31, 0.15f) };
                    for (uint i = 0; i < layers.Length; i++)
                    {
                        double ph0 = PixelFx.Hash(i, axis, seed) / (double)uint.MaxValue * 2 * Math.PI;
                        v += layers[i].A * (float)Math.Sin(2 * Math.PI * layers[i].F * speed * t + ph0);
                    }
                    return v;
                }
                m.Ox += strength * Noise(1); m.Oy += strength * Noise(2);
                float rot = a.Track("rotation");
                if (rot != 0) m.Rz += rot * Noise(3);
                return s;
            }
            case 27:   // ゆっくりズーム・パン (Ken Burns): オブジェクトの長さ全体で、少しずつ寄り (強さが負なら引き)、移動量だけ動く
            {
                var p = a.Context.Project;
                double total = p.FrameToSeconds(a.Object?.Length ?? 1) - a.Track("delay");
                float k = (float)Math.Clamp(total > 0 ? t / total : 1, 0, 1);
                float z = strength / 100f;
                m.Zoom *= z >= 0 ? 1 + z * k : 1 - z * (1 - k);
                m.Ox += a.Track("dx") * k; m.Oy += a.Track("dy") * k;
                return s;
            }
            default: return BlinkingCursor(s, a, cycles);
        }
    }

    /// <summary>ぷるぷる (ゼリー): 横に伸びると縦に縮む (面積はそのまま)。中心X・中心Y の点を動かさずに伸び縮みする。</summary>
    private static Sprite Jelly(Sprite s, EffectArgs a, float k)
    {
        k = Math.Clamp(k, -0.9f, 4);
        if (MathF.Abs(k) < 0.002f) return s;
        int w = Math.Max(1, (int)MathF.Round(s.Width * (1 + k))), h = Math.Max(1, (int)MathF.Round(s.Height / (1 + k)));
        if (w > 16384 || h > 16384) return s;
        float sx = w / (float)s.Width, sy = h / (float)s.Height;
        // 中心の点 (絵の座標)。拡大した絵でも、この点と中心の位置関係が同じになるように中心を決める
        float ox = a.Track("ax") / s.Scale, oy = a.Track("ay") / s.Scale;
        float px = s.Cx + ox, py = s.Cy + oy;
        return new Sprite(Resized(s, a, w, h), px * sx - ox, py * sy - oy, s.Scale);
    }

    /// <summary>虹色が流れる: 虹色の帯を斜めに流しながら、絵に乗算で重ねる (白い文字はそのまま虹色になる)。</summary>
    private static Sprite Rainbow(Sprite s, EffectArgs a, float shift)
    {
        float period = Math.Max(s.Width, s.Height);
        if (period < 1) return s;
        var colors = new[]
        {
            new SKColor(255, 64, 64), new SKColor(255, 200, 40), new SKColor(80, 230, 80), new SKColor(40, 210, 255),
            new SKColor(90, 100, 255), new SKColor(230, 70, 230), new SKColor(255, 64, 64),
        };
        return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
        {
            c.DrawImage(s.Image, x, y);
            // 乗算のレイヤー (CPU 描画では自前で合成する。PixelBlend)
            using var layer = PixelBlend.BeginLayer(c, SKRect.Create(x, y, s.Width, s.Height), SKBlendMode.Multiply);
            var lc = layer.Canvas;
            var local = SKMatrix.CreateRotationDegrees(-25).PostConcat(SKMatrix.CreateTranslation(x + shift * period, y));
            using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(period, 0), colors, null, SKShaderTileMode.Repeat, local))
            using (var paint = new SKPaint { Shader = shader })
                lc.DrawRect(SKRect.Create(x, y, s.Width, s.Height), paint);
            using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
                PixelBlend.DrawImage(lc, s.Image, x, y, keep);
        });
    }

    /// <summary>
    /// 旗のようになびく: 左の端 (旗ざお) を止めたまま、右へ行くほど大きく縦に波打つ。波の斜面に陰を付ける。
    /// </summary>
    private static unsafe Sprite Flag(Sprite s, EffectArgs a, float amplitude, float cycles)
    {
        float amp = amplitude / s.Scale, wl = Math.Max(1, a.Track("wavelength") / s.Scale);
        if (MathF.Abs(amp) < 0.3f) return s;
        int pad = (int)MathF.Ceiling(MathF.Abs(amp)) + 1;
        float k = 2 * MathF.PI / wl, shift = 2 * MathF.PI * cycles;
        // GPU 描画では SkSL で (読み戻さない。設計書 22.54)
        if (FlagOnGpu(s, a, pad, amp, k, shift) is { } onGpu) return onGpu;
        return Resample(s, a, 0, pad, (src, dst, w, h, rb) =>
        {
            uint* sp = (uint*)src, dp = (uint*)dst;
            int stride = rb / 4;
            PixelOps.ForBands(w, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* row = dp + (long)y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        float reach = x / (float)Math.Max(1, w - 1);
                        float arg = k * x - shift;
                        float dy = amp * reach * MathF.Sin(arg);
                        uint c = PixelFx.Sample(sp, w, h, stride, x, y - dy, false);
                        // 波の斜面の向きで明るさを変える (なびいている布に見えるように)
                        float light = 1 + 0.22f * reach * MathF.Cos(arg) * MathF.Min(1, MathF.Abs(amp) / 20);
                        if (light < 1) c = ScalePixelColor(c, light);
                        else if (light > 1) c = LightenPixel(c, light - 1);
                        row[x] = c;
                    }
                }
            });
            return dst;
        }) ?? s;
    }

    /// <summary>乗算済みのピクセルの色だけに k (0〜1) を掛ける (不透明度はそのまま)。</summary>
    private static uint ScalePixelColor(uint c, float k)
    {
        uint t = (uint)(Math.Clamp(k, 0, 1) * 256);
        uint rb = ((c & 0x00FF00FFu) * t >> 8) & 0x00FF00FFu;
        uint g = (((c >> 8) & 0xFFu) * t >> 8) & 0xFFu;
        return (c & 0xFF000000u) | rb | g << 8;
    }

    /// <summary>乗算済みのピクセルを、不透明度を超えない範囲で白へ k (0〜1) だけ近づける。</summary>
    private static uint LightenPixel(uint c, float k)
    {
        uint al = c >> 24;
        uint L(uint ch) => (uint)Math.Min(al, ch + (al - Math.Min(al, ch)) * k);
        return al << 24 | L((c >> 16) & 0xFF) << 16 | L((c >> 8) & 0xFF) << 8 | L(c & 0xFF);
    }

    /// <summary>
    /// 跳ねながら往復: 移動量 (X・Y) のところまでぴょんぴょん跳ねて行き、跳ねながら戻ってくる。
    /// 1 往復が「速さ」の 1 回。片道で 3 回跳ねる。戻るときに向きを変える (左右反転) こともできる。
    /// </summary>
    private static Sprite HopBackAndForth(Sprite s, EffectArgs a, float height, double cycles, ref DrawModifiers m)
    {
        double c = cycles - Math.Floor(cycles);
        float k = (float)(c < 0.5 ? c * 2 : 2 - c * 2);
        m.Ox += a.Track("dx") * k; m.Oy += a.Track("dy") * k;
        m.Oy -= height * (float)Math.Abs(Math.Sin(Math.PI * 6 * c));
        if (c < 0.5 || !a.Check("turn")) return s;
        var r = Redraw(s, a, 0, 0, 0, 0, (cv, x, y) =>
        {
            cv.Scale(-1, 1, x + s.Width / 2f, 0);
            cv.DrawImage(s.Image, x, y);
        });
        return new Sprite(r.Image, s.Width - s.Cx, s.Cy, s.Scale);
    }

    /// <summary>
    /// カーソル点滅: 文字のいちばん下の行の右端 (1 文字ずつ表示しているときは、表示された最後の文字の後ろ) に、
    /// 点滅する縦の棒を描く。テキストなら棒の高さは文字の大きさ、それ以外は最後の行の高さ。
    /// </summary>
    private static unsafe Sprite BlinkingCursor(Sprite s, EffectArgs a, double cycles)
    {
        double c = cycles - Math.Floor(cycles);
        float visible = c < 0.5 ? 1 : 1 - Math.Clamp(a.Track("depth") / 100f, 0, 1);
        if (visible <= 0) return s;
        float textH = a.Object?.Main is { } tm && tm.DefinitionId == EffectRegistry.Text && a.Bounds != null ? (float)tm.Eval("size", a.Bounds, a.Frame) / s.Scale : 0;
        var barColor = a.Color("color");
        barColor = barColor.WithAlpha((byte)(barColor.Alpha * visible));
        // GPU 描画では、最後の行と右端を GPU で求めて棒も SkSL で描く (読み戻さない。設計書 22.54)
        if (CursorOnGpu(s, a, textH, a.Track("width"), barColor) is { } onGpu) return onGpu;
        // いちばん下の、何か描かれている行のまとまり (最後の行) と、その右端を探す
        int bottom = -1, top = -1, right = -1;
        if (CopyPixels(s) is { } copy)
        {
            try
            {
                uint* px = (uint*)copy.Buffer;
                int stride = copy.RowBytes / 4;
                bool RowHas(int y) { for (int x = 0; x < s.Width; x++) if (px[(long)y * stride + x] >> 24 > 16) return true; return false; }
                for (int y = s.Height - 1; y >= 0 && bottom < 0; y--) if (RowHas(y)) bottom = y;
                if (bottom >= 0)
                {
                    top = bottom;
                    while (top > 0 && RowHas(top - 1)) top--;
                    for (int y = top; y <= bottom; y++)
                        for (int x = s.Width - 1; x > right; x--)
                            if (px[(long)y * stride + x] >> 24 > 16) { right = x; break; }
                }
            }
            finally { PixelPool.Return(copy.Buffer, (long)copy.RowBytes * s.Height); }
        }
        // 棒の高さ: テキストなら文字の大きさ (絵の座標)、そうでなければ最後の行の高さ
        float lineH = bottom >= 0 ? bottom - top + 1 : s.Height;
        if (a.Object?.Main is { } main && main.DefinitionId == EffectRegistry.Text && a.Bounds != null)
            lineH = (float)main.Eval("size", a.Bounds, a.Frame) / s.Scale;
        lineH = Math.Max(2, lineH);
        float barW = Math.Max(1, lineH * a.Track("width") / 100f), gap = lineH * 0.08f;
        float x0 = right >= 0 ? right + 1 + gap : 0;
        float y1 = bottom >= 0 ? bottom + 1 : s.Cy + lineH / 2, y0 = y1 - lineH;
        var bar = new SKRect(x0, y0, x0 + barW, y1);
        int padR = Math.Max(0, (int)MathF.Ceiling(bar.Right - s.Width)), padT = Math.Max(0, (int)MathF.Ceiling(-bar.Top));
        var color = a.Color("color");
        color = color.WithAlpha((byte)(color.Alpha * visible));
        return Redraw(s, a, 0, padT, padR, 0, (cv, x, y) =>
        {
            cv.DrawImage(s.Image, x, y);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            var r = bar; r.Offset(x, y);
            cv.DrawRect(r, paint);
        }) with { Scale = s.Scale };
    }
}
