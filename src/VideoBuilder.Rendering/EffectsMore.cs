using System.Runtime.InteropServices;
using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>
/// 組み込みのフィルタ効果 (追加分): グロー・方向ブラー・放射ブラー・シャープ・色収差・ラスター・ノイズ・
/// 周辺減光・グラデーション・斜めクリッピング・レベル補正・ポスタリゼーション・アニメーション効果。
/// ピクセルを直接扱うものは PixelFx (複数スレッド) で処理し、GPU 描画中も読み戻して同じ処理を使う (CPU と同じ絵になる)。
/// </summary>
public static partial class Effects
{
    private static void RegisterMore()
    {
        Register(new Glow("filter.glow")); Register(new Glow("filter.emit")); Register(new DirectionalBlur()); Register(new RadialBlur()); Register(new Sharpen());
        Register(new ChromaticAberration()); Register(new Raster()); Register(new Noise()); Register(new Vignette());
        Register(new GradientOverlay()); Register(new DiagonalClip()); Register(new Levels()); Register(new Posterize());
        Register(new Animation());
    }

    /// <summary>
    /// 周りに余白 (padX, padY) を付けて写したバッファと、同じ大きさの作業用バッファを op に渡す。
    /// op は結果が入っている方のバッファを返す。絵を読めなければ null。
    /// </summary>
    internal static unsafe Sprite? Resample(Sprite s, EffectArgs a, int padX, int padY, Func<IntPtr, IntPtr, int, int, int, IntPtr> op)
    {
        padX = Math.Max(0, padX); padY = Math.Max(0, padY);
        int w = s.Width + padX * 2, h = s.Height + padY * 2;
        if (w > 16384 || h > 16384) { padX = padY = 0; w = s.Width; h = s.Height; }
        int rowBytes = w * 4;
        long bytes = (long)rowBytes * h;
        var src = PixelPool.Rent(bytes);
        var tmp = PixelPool.Rent(bytes);
        if (padX > 0 || padY > 0) NativeMemory.Clear((void*)src, (nuint)bytes);
        if (!s.Image.ReadPixels(Info(s.Width, s.Height), src + padY * rowBytes + padX * 4, rowBytes, 0, 0))
        {
            PixelPool.Return(src, bytes); PixelPool.Return(tmp, bytes);
            return null;
        }
        IntPtr result;
        try { result = op(src, tmp, w, h, rowBytes); }
        catch { PixelPool.Return(src, bytes); PixelPool.Return(tmp, bytes); throw; }
        PixelPool.Return(result == src ? tmp : src, bytes);
        var img = a.Context.Own(PixelPool.ToImage(result, Info(w, h), rowBytes));
        return new Sprite(img, s.Cx + padX, s.Cy + padY);
    }

    /// <summary>
    /// プレビュー (CPU 描画) で、大きくぼかすときは縦横半分の絵で計算して、元の大きさに戻す。計算するピクセルが 1/4 になる。
    /// 大きくぼかした絵は細部が残らないので、見た目はほとんど変わらない。出力では使わない (null を返す)。
    /// run には半分の絵と、大きさの倍率 (0.5) が渡される。
    /// </summary>
    private static Sprite? AtHalfForPreview(Sprite s, EffectArgs a, bool large, Func<Sprite, float, Sprite?> run)
    {
        if (!a.IsDraft || a.Context.Gpu != null || !large || s.Width < 64 || s.Height < 64) return null;
        using var small = PixelOps.Halve(s.Image, s.Width, s.Height);
        if (run(new Sprite(small, s.Cx / 2, s.Cy / 2), 0.5f) is not { } r) return null;
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, BlendMode = SKBlendMode.Src };
        var up = a.Context.Own(PixelOps.DrawParallel(r.Width * 2, r.Height * 2, c => { c.Scale(2); c.DrawImage(r.Image, 0, 0, paint); }));
        return new Sprite(up, r.Cx * 2, r.Cy * 2);
    }

    /// <summary>オブジェクトの先頭 (イン) と末尾 (アウト) からの進み具合 (0〜1)。1 なら効果はかかっていない。</summary>
    private static double InOutProgress(EffectArgs a)
    {
        var p = a.Context.Project;
        double t = p.FrameToSeconds(a.Context.Frame - a.Object.Start);
        double rest = p.FrameToSeconds(a.Object.End + 1 - a.Context.Frame);
        double fin = a.Track("in"), fout = a.Track("out");
        double progress = 1;
        if (fin > 0) progress = Math.Min(progress, t / fin);
        if (fout > 0) progress = Math.Min(progress, rest / fout);
        return Math.Clamp(progress, 0, 1);
    }

    /// <summary>
    /// グロー: 明るい部分 (しきい値より明るいところ) をぼかして、元の絵に足す (加算)。
    /// ぼかしは「ぼかし」と同じく、大きいときは縮小してから計算する。
    /// </summary>
    private sealed class Glow(string id) : IVideoEffect
    {
        public string Id => id;
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float strength = Math.Clamp(a.Track("strength") / 100f, 0, 4);
            float radius = a.Track("radius");
            float threshold = Math.Clamp(a.Track("threshold"), 0, 100) / 100f * 255f;
            bool only = a.Check("only");
            if (strength <= 0) return only ? Redraw(s, a, 0, 0, 0, 0, (_, _, _) => { }) : s;

            var bright = threshold > 0 ? WithPixels(s, a, (px, w, h, rb) => PixelFx.BrightPass(px, w, h, rb, threshold), alsoOnGpu: true) ?? s : s;
            // 形: 0 = 通常 (まわりに広がる)、1 = 十字、2 = 横、3 = 縦 (光の筋を方向ブラーで作る)
            int shape = a.Effect.GetInt("shape");
            var glows = new List<Sprite>();
            if (shape == 0) glows.Add(radius > 0 ? Gaussian(bright, a, radius, a.IsFrameBuffer) : bright);
            else
            {
                float len = MathF.Max(2, radius * 6);
                foreach (float angle in shape == 1 ? new[] { 0f, 90f } : shape == 2 ? new[] { 0f } : new[] { 90f })
                {
                    bool vertical = angle != 0;
                    int pp = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(len / 2) + 1;
                    // 同じぼかしを 2 回かけて、筋の先を細く消えるようにする (箱形のままだと端がはっきり切れる)
                    var streak = Resample(bright, a, vertical ? 0 : pp, vertical ? pp : 0, (src, tmp, w, h, rb) =>
                    {
                        var r1 = PixelFx.DirectionalBlur(src, tmp, w, h, rb, vertical ? 0 : 1, vertical ? 1 : 0, len / 2, a.IsFrameBuffer);
                        return PixelFx.DirectionalBlur(r1, r1 == src ? tmp : src, w, h, rb, vertical ? 0 : 1, vertical ? 1 : 0, len / 2, a.IsFrameBuffer);
                    });
                    if (streak is { } st) glows.Add(st);
                }
                // 筋は細く伸ばすぶん暗くなるので、明るさを足す
                strength = Math.Min(8, strength * 2);
            }
            float gx = glows.Count == 0 ? 0 : glows.Max(g => g.Cx - s.Cx), gy = glows.Count == 0 ? 0 : glows.Max(g => g.Cy - s.Cy);
            int pad = a.IsFrameBuffer ? 0 : Math.Max(0, (int)MathF.Ceiling(Math.Max(gx, gy)));

            SKColorFilter? tint = null;
            var colorText = a.Effect.GetString("color");
            if (!string.IsNullOrWhiteSpace(colorText) && SKColor.TryParse(colorText, out var c))
            {
                // 光の色を変える: 明るさ × 指定の色
                float cr = c.Red / 255f, cg = c.Green / 255f, cb = c.Blue / 255f;
                const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
                tint = SKColorFilter.CreateColorMatrix(new float[]
                {
                    lr * cr, lg * cr, lb * cr, 0, 0,
                    lr * cg, lg * cg, lb * cg, 0, 0,
                    lr * cb, lg * cb, lb * cb, 0, 0,
                    0, 0, 0, 1, 0,
                });
            }
            using (tint)
            {
                // 100% を超えるぶんは、重ねて足す
                int times = Math.Max(1, (int)MathF.Ceiling(strength));
                using var paint = new SKPaint
                {
                    BlendMode = SKBlendMode.Plus, ColorFilter = tint, FilterQuality = SKFilterQuality.Low,
                    Color = SKColors.White.WithAlpha((byte)Math.Clamp(MathF.Round(strength / times * 255), 0, 255)),
                };
                return Redraw(s, a, pad, pad, pad, pad, (cv, x, y) =>
                {
                    if (!only) cv.DrawImage(s.Image, x, y);
                    foreach (var g in glows)
                        for (int i = 0; i < times; i++) cv.DrawImage(g.Image, x - (g.Cx - s.Cx), y - (g.Cy - s.Cy), paint);
                });
            }
        }
    }

    /// <summary>方向ブラー: 決めた向きに流れるようにぼかす (動きの速さを表す)。</summary>
    private sealed class DirectionalBlur : IVideoEffect
    {
        public string Id => "filter.dirblur";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float length = a.Track("length");
            if (length < 1) return s;
            float rad = a.Track("angle") * MathF.PI / 180f;
            float dx = MathF.Cos(rad), dy = MathF.Sin(rad);
            bool clamp = a.IsFrameBuffer || a.Check("fixed");
            int padX = clamp ? 0 : (int)MathF.Ceiling(MathF.Abs(dx) * length / 2) + 1;
            int padY = clamp ? 0 : (int)MathF.Ceiling(MathF.Abs(dy) * length / 2) + 1;
            int passes = a.IsDraft ? 8 : 10;
            return AtHalfForPreview(s, a, length >= 24, (half, k) =>
                       Resample(half, a, (int)MathF.Ceiling(padX * k), (int)MathF.Ceiling(padY * k), (src, tmp, w, h, rb) => PixelFx.DirectionalBlur(src, tmp, w, h, rb, dx, dy, length * k, clamp, passes)))
                ?? Resample(s, a, padX, padY, (src, tmp, w, h, rb) => PixelFx.DirectionalBlur(src, tmp, w, h, rb, dx, dy, length, clamp, passes)) ?? s;
        }
    }

    /// <summary>放射ブラー: 中心から外へ向かってぼかす (ズームしているような絵になる)。</summary>
    private sealed class RadialBlur : IVideoEffect
    {
        public string Id => "filter.radialblur";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float range = Math.Max(0, a.Track("amount") / 100f);
            if (range <= 0) return s;
            float ox = a.Track("x"), oy = a.Track("y");
            bool clamp = a.IsFrameBuffer;
            int passes = a.IsDraft ? 8 : 10;
            // いちばん遠い所が動く距離 (ピクセル) が大きければ、プレビューは半分の大きさで計算する
            bool large = MathF.Sqrt(s.Width * s.Width + s.Height * s.Height) / 2 * MathF.Log(1 + range) >= 24;
            return AtHalfForPreview(s, a, large, (half, k) =>
                       Resample(half, a, 0, 0, (src, tmp, w, h, rb) => PixelFx.RadialBlur(src, tmp, w, h, rb, half.Cx + ox * k, half.Cy + oy * k, range, clamp, passes)))
                ?? Resample(s, a, 0, 0, (src, tmp, w, h, rb) => PixelFx.RadialBlur(src, tmp, w, h, rb, s.Cx + ox, s.Cy + oy, range, clamp, passes)) ?? s;
        }
    }

    /// <summary>シャープ (アンシャープマスク): ぼかした絵との差を強めて、輪郭をくっきりさせる。</summary>
    private sealed class Sharpen : IVideoEffect
    {
        public string Id => "filter.sharpen";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            float amount = a.Track("amount") / 100f, radius = a.Track("radius");
            if (amount <= 0 || radius <= 0) return s;
            using var blurred = BlurOnCpu(s.Image, radius / 2, 0, true);
            if (blurred == null) return s;
            using var bp = blurred.PeekPixels();
            if (bp == null) return s;
            return WithPixels(s, a, (px, w, h, rb) => PixelFx.Unsharp(px, rb, bp.GetPixels(), bp.RowBytes, w, h, amount), alsoOnGpu: true) ?? s;
        }
    }

    /// <summary>色収差: 赤と青をずらして、レンズの色ずれのような縁を付ける。</summary>
    private sealed class ChromaticAberration : IVideoEffect
    {
        public string Id => "filter.chromatic";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float amount = a.Track("amount");
            if (MathF.Abs(amount) < 0.05f) return s;
            bool radial = a.Check("radial"), clamp = a.IsFrameBuffer;
            float rad = a.Track("angle") * MathF.PI / 180f;
            float dx = MathF.Cos(rad) * amount, dy = MathF.Sin(rad) * amount;
            int pad = clamp ? 0 : (int)MathF.Ceiling(MathF.Abs(amount)) + 1;
            return Resample(s, a, pad, pad, (src, tmp, w, h, rb) =>
            {
                PixelFx.ChromaticAberration(src, tmp, w, h, rb, dx, dy, radial, s.Cx + pad, s.Cy + pad, amount, clamp, a.Effect.GetInt("type"));
                return tmp;
            }) ?? s;
        }
    }

    /// <summary>ラスター: 行ごとに波のようにずらす。速さを付けると波が流れる。</summary>
    private sealed class Raster : IVideoEffect
    {
        public string Id => "filter.raster";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float amp = a.Track("amplitude"), wave = a.Track("wavelength"), speed = a.Track("speed");
            if (MathF.Abs(amp) < 0.05f) return s;
            bool vertical = a.Check("vertical"), clamp = a.IsFrameBuffer;
            float phase = (float)(2 * Math.PI * speed * a.Time % (2 * Math.PI));
            int pad = clamp ? 0 : (int)MathF.Ceiling(MathF.Abs(amp)) + 1;
            return Resample(s, a, vertical ? 0 : pad, vertical ? pad : 0, (src, tmp, w, h, rb) =>
            {
                PixelFx.Raster(src, tmp, w, h, rb, amp, wave, phase, vertical, clamp);
                return tmp;
            }) ?? s;
        }
    }

    /// <summary>ノイズ: 粒状のざらつきを足す。「動かす」なら毎フレーム模様が変わる (フィルムの粒子のように)。</summary>
    private sealed class Noise : IVideoEffect, IPixelEffect
    {
        public string Id => "filter.noise";
        public EffectFlags Flags => EffectFlags.None;

        private static (float Amount, int Cell, bool Color, uint Seed) Read(EffectArgs a)
        {
            uint seed = (uint)(int)a.Track("seed");
            if (a.Check("animate")) seed = unchecked(seed * 31 + (uint)a.Frame * 0x9E3779B9u);
            return (Math.Clamp(a.Track("amount"), 0, 100) * 1.28f, Math.Max(1, (int)MathF.Round(a.Track("size"))), a.Check("color"), seed);
        }

        public void Process(PixelBuffer b, EffectArgs a)
        {
            var (amount, cell, color, seed) = Read(a);
            PixelFx.Noise(b.Pointer, b.Width, b.Height, b.RowBytes, amount, cell, color, seed);
        }

        public Sprite Apply(Sprite s, EffectArgs a)
        {
            var (amount, cell, color, seed) = Read(a);
            if (amount <= 0) return s;
            return WithPixels(s, a, (px, w, h, rb) => PixelFx.Noise(px, w, h, rb, amount, cell, color, seed), alsoOnGpu: true) ?? s;
        }
    }

    /// <summary>周辺減光 (ビネット): 絵の周りを暗く (指定の色に) する。大きさは絵の大きさに対する割合。</summary>
    private sealed class Vignette : IVideoEffect
    {
        public string Id => "filter.vignette";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float strength = Math.Clamp(a.Track("strength") / 100f, 0, 1);
            if (strength <= 0) return s;
            float inner = Math.Max(0, a.Track("size") / 100f), soft = Math.Max(0.001f, a.Track("softness") / 100f);
            float outer = inner + soft;
            var color = a.Color("color");
            var edge = color.WithAlpha((byte)MathF.Round(color.Alpha * strength));
            // 半径 1 = 絵の中心から角までの距離 (縦横で伸ばした楕円)
            float hw = s.Width / 2f, hh = s.Height / 2f;
            var local = SKMatrix.CreateScale(hw * MathF.Sqrt(2) * outer, hh * MathF.Sqrt(2) * outer).PostConcat(SKMatrix.CreateTranslation(hw, hh));
            using var shader = SKShader.CreateRadialGradient(new SKPoint(0, 0), 1, new[] { color.WithAlpha(0), edge }, new[] { inner / outer, 1f }, SKShaderTileMode.Clamp, local);
            using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.SrcATop };
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                c.DrawImage(s.Image, x, y);
                c.Translate(x, y);
                c.DrawRect(SKRect.Create(s.Width, s.Height), paint);
            });
        }
    }

    /// <summary>グラデーション: 2 つの色のグラデーションを、絵の見えている部分に重ねる。</summary>
    private sealed class GradientOverlay : IVideoEffect
    {
        public string Id => "filter.gradient";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float strength = Math.Clamp(a.Track("strength") / 100f, 0, 1);
            if (strength <= 0) return s;
            float rad = a.Track("angle") * MathF.PI / 180f, width = Math.Max(1, a.Track("width"));
            // 角度 0 で、色 1 が上・色 2 が下
            float dx = -MathF.Sin(rad), dy = MathF.Cos(rad);
            float cx = s.Cx + a.Track("x"), cy = s.Cy + a.Track("y");
            var p0 = new SKPoint(cx - dx * width / 2, cy - dy * width / 2);
            var p1 = new SKPoint(cx + dx * width / 2, cy + dy * width / 2);
            using var shader = SKShader.CreateLinearGradient(p0, p1, new[] { a.Color("color1"), a.Color("color2") }, SKShaderTileMode.Clamp);
            using var paint = new SKPaint
            {
                Shader = shader, BlendMode = SKBlendMode.SrcATop,
                Color = SKColors.White.WithAlpha((byte)MathF.Round(strength * 255)),
            };
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                c.DrawImage(s.Image, x, y);
                c.Translate(x, y);
                c.DrawRect(SKRect.Create(s.Width, s.Height), paint);
            });
        }
    }

    /// <summary>斜めクリッピング: 中心を通る直線で切り、片側だけを残す。角度 0 で上半分を消す。</summary>
    private sealed class DiagonalClip : IVideoEffect
    {
        public string Id => "filter.diagclip";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float cx = s.Cx + a.Track("x"), cy = s.Cy + a.Track("y"), feather = a.Track("feather");
            float big = (s.Width + s.Height) * 2 + feather * 4 + 16;
            using var path = new SKPath();
            path.AddRect(new SKRect(cx - big, cy, cx + big, cy + big));
            path.Transform(SKMatrix.CreateRotationDegrees(a.Track("angle"), cx, cy));
            return Masked(s, a, path, feather, false);
        }
    }

    /// <summary>レベル補正: 入力の黒・白とガンマ、出力の黒・白で明るさの分布を整える。</summary>
    private sealed class Levels : IVideoEffect, IPixelEffect
    {
        public string Id => "filter.levels";
        private static byte[] Lut(EffectArgs a) =>
            PixelFx.LevelsLut(a.Track("inBlack"), a.Track("inWhite"), a.Track("gamma"), a.Track("outBlack"), a.Track("outWhite"));

        public void Process(PixelBuffer b, EffectArgs a) => PixelFx.ApplyLut(b.Pointer, b.Width, b.Height, b.RowBytes, Lut(a));

        public Sprite Apply(Sprite s, EffectArgs a)
        {
            var lut = Lut(a);
            bool identity = true;
            for (int i = 0; i < 256 && identity; i++) identity = lut[i] == i;
            if (identity) return s;
            return ApplyLut(s, a, lut);
        }
    }

    /// <summary>ポスタリゼーション: 色の段階を減らして、イラストやポスターのような絵にする。</summary>
    private sealed class Posterize : IVideoEffect, IPixelEffect
    {
        public string Id => "filter.posterize";
        private static int Steps(EffectArgs a) => Math.Clamp((int)MathF.Round(a.Track("levels")), 2, 256);

        public void Process(PixelBuffer b, EffectArgs a) => PixelFx.ApplyLut(b.Pointer, b.Width, b.Height, b.RowBytes, PixelFx.PosterizeLut(Steps(a)));

        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int n = Steps(a);
            if (n >= 256) return s;
            var lut = PixelFx.PosterizeLut(n);
            return ApplyLut(s, a, lut);
        }
    }

    /// <summary>
    /// リール回転: スロットマシンのリールのように、オブジェクトの長さの間に「回転数」だけ回って、元の向きで止まる
    /// (ゆっくり回り始めて速くなり、また遅くなって止まる)。速く回っている間は、回る向きにぼかす (ブラー)。
    /// 横回転なら Y 軸 (左右) で回す。
    /// </summary>
    private static Sprite Reel(Sprite s, EffectArgs a, double t)
    {
        double total = a.Object == null ? 0 : a.Context.Project.FrameToSeconds(a.Object.Length);
        float turns = a.Track("turns");
        if (total <= 0 || turns == 0) return s;
        double q = Math.Clamp(t / total, 0, 1);
        // 加速して減速する動き (3 次のイーズ) と、その速さ
        double ease = q < 0.5 ? 4 * q * q * q : 1 - Math.Pow(-2 * q + 2, 3) / 2;
        double rate = q < 0.5 ? 12 * q * q : 3 * Math.Pow(-2 * q + 2, 2);   // d(ease)/dq
        bool horizontal = a.Check("reelHorizontal");
        float angle = (float)(360 * turns * ease);
        ref var m = ref a.Context.Modifiers;
        if (horizontal) m.Ry += angle; else m.Rx += angle;
        // 1 フレームで回る角度 × 半径 = 端が動く距離。これにブラーの割合をかけた長さで、回る向きにぼかす
        double perFrame = 2 * Math.PI * Math.Abs(turns) * rate / Math.Max(1, a.Object!.Length);
        float half = (horizontal ? s.Width : s.Height) / 2f;
        float length = (float)(perFrame * half * a.Track("reelBlur") / 100);
        if (length < 1.5f) return s;
        length = MathF.Min(length, half * 2);
        float dx = horizontal ? 1 : 0, dy = horizontal ? 0 : 1;
        int pad = (int)MathF.Ceiling(length / 2) + 1;
        int passes = a.IsDraft ? 8 : 10;
        return Resample(s, a, horizontal ? pad : 0, horizontal ? 0 : pad, (src, tmp, w, h, rb) => PixelFx.DirectionalBlur(src, tmp, w, h, rb, dx, dy, length, false, passes)) ?? s;
    }

    /// <summary>
    /// アニメーション効果: 振動・点滅・回転・鼓動・揺れ・浮遊・弾む・横揺れを、キーフレームなしで付ける。
    /// 絵は変えず、描く位置 (DrawModifiers) だけを変えるので軽い。時間はオブジェクトの先頭から数える。
    /// </summary>
    private sealed class Animation : IVideoEffect
    {
        public string Id => "filter.anim";

        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int type = a.Effect.GetInt("type");
            float speed = a.Track("speed");
            // 開始の遅延・繰り返す回数・位相 (どの種類にも効く)
            double t = a.Time - a.Track("delay");
            if (t < 0) return s;
            if (IsPeriodicAnimation(type) && speed != 0)
            {
                int count = (int)a.Track("count");
                if (count > 0) t = Math.Min(t, count / Math.Abs(speed));
                t += a.Track("phase") / 360.0 / speed;
            }
            var before = a.Context.Modifiers;
            var result = type >= 14 ? MoreAnimation(s, a, type, t, speed) : Core(s, a, type, t, speed);
            ApplyAnimationAnchor(s, a, before);
            return result;
        }

        private static Sprite Core(Sprite s, EffectArgs a, int type, double t, float speed)
        {
            float strength = a.Track("strength");
            double phase = 2 * Math.PI * speed * t;
            ref var m = ref a.Context.Modifiers;
            switch (type)
            {
                case 0:   // 振動
                {
                    float rate = Math.Max(0.01f, a.Track("rate"));
                    uint seed = (uint)(int)a.Track("seed");
                    double pos = t * rate;
                    long step = (long)Math.Floor(pos);
                    float Rand(long i, uint axis) => PixelFx.Hash((uint)i, axis, seed) / (float)uint.MaxValue * 2 - 1;
                    float Value(uint axis)
                    {
                        if (!a.Check("smooth")) return Rand(step, axis);
                        float f = (float)(pos - step);
                        f = f * f * (3 - 2 * f);
                        return Rand(step, axis) + (Rand(step + 1, axis) - Rand(step, axis)) * f;
                    }
                    m.Ox += strength * Value(1);
                    m.Oy += strength * Value(2);
                    float rot = a.Track("rotation");
                    if (rot != 0) m.Rz += rot * Value(3);
                    break;
                }
                case 1:   // 点滅: 1 回の点滅のうち、前半は見えて後半は消える
                {
                    double cycle = speed * t;
                    if (cycle - Math.Floor(cycle) >= 0.5) m.Alpha *= 1 - Math.Clamp(a.Track("depth") / 100f, 0, 1);
                    break;
                }
                case 2: m.Rz += (float)(360 * speed * t); break;                                    // 回転
                case 3: m.Zoom *= 1 + strength / 100f * (float)(0.5 - 0.5 * Math.Cos(phase)); break; // 鼓動
                case 4: m.Rz += strength * (float)Math.Sin(phase); break;                           // 揺れ (傾く)
                case 5: m.Oy += strength * (float)Math.Sin(phase); break;                           // 浮遊 (上下)
                case 6: m.Oy -= strength * (float)Math.Abs(Math.Sin(phase / 2)); break;             // 弾む
                case 7: m.Ox += strength * (float)Math.Sin(phase); break;                           // 横揺れ
                case 8:   // 振り子: 上の端を支点にして左右に振れる
                {
                    float deg = strength * (float)Math.Sin(phase), rad = deg * MathF.PI / 180f;
                    float half = s.Height * s.Scale / 2;
                    m.Rz += deg;
                    m.Ox += -half * MathF.Sin(rad);
                    m.Oy += -half + half * MathF.Cos(rad);
                    break;
                }
                case 9:   // 反復移動: (移動量X, 移動量Y) まで行って戻る (端で加減速)
                {
                    double tri = speed * t % 1;
                    float k = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * tri));
                    m.Ox += a.Track("dx") * k; m.Oy += a.Track("dy") * k;
                    break;
                }
                case 10: m.Rx += (float)(360 * speed * t); break;   // 回転 (X 軸)
                case 11: m.Ry += (float)(360 * speed * t); break;   // 回転 (Y 軸)
                case 13: return Reel(s, a, t);
                default:  // 立方体の面: 6 つのオブジェクトに 1〜6 の面を割り当てると立方体になる (カメラ制御と使う)
                {
                    float size = a.Track("cube");
                    if (size <= 0) size = s.Width * s.Scale;
                    float half = size / 2, spin = (float)(360 * speed * t), sr = spin * MathF.PI / 180f;
                    // 面の中心 (立方体の中心から) と向き。Z は奥が +
                    (float X, float Y, float Z, float Rx, float Ry) f = a.Effect.GetInt("face") switch
                    {
                        0 => (0, 0, -half, 0, 0),        // 前
                        1 => (0, 0, half, 0, 180),       // 後ろ
                        2 => (half, 0, 0, 0, -90),       // 右
                        3 => (-half, 0, 0, 0, 90),       // 左
                        4 => (0, -half, 0, -90, 0),      // 上
                        _ => (0, half, 0, 90, 0),        // 下
                    };
                    // 立方体ごと Y 軸で回す
                    float cs = MathF.Cos(sr), sn = MathF.Sin(sr);
                    m.Ox += f.X * cs + f.Z * sn; m.Oy += f.Y; m.Oz += -f.X * sn + f.Z * cs;
                    m.Rx += f.Rx; m.Ry += f.Ry + spin;
                    break;
                }
            }
            return s;
        }
    }
}
