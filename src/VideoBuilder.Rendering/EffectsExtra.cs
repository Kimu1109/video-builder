using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>
/// 組み込みのフィルタ効果 (追加分その 2): 色・キー・光・変形・ぼかし・配置・動きのエフェクト。
/// ピクセルの処理は PixelFx (複数スレッド)、図形や画像の重ね合わせは Skia で描く。
/// </summary>
public static partial class Effects
{
    private static void RegisterExtra()
    {
        // 色・キー
        Register(new ExtColor()); Register(new HueConvert()); Register(new ColorKey()); Register(new LumaKey());
        // 光
        Register(new Diffuse()); Register(new Flash()); Register(new RimLight()); Register(new Bevel()); Register(new EdgeDetect());
        // 変形
        Register(new Ripple()); Register(new Polar()); Register(new Displace()); Register(new Mirror()); Register(new Kaleidoscope());
        Register(new SphereMap()); Register(new FreeTransform());
        // ぼかし
        Register(new LensBlur()); Register(new MotionBlur()); Register(new EdgeBlur());
        // その他
        Register(new BasicEffect()); Register(new Shake()); Register(new ImageLoop()); Register(new FileBlend("filter.imageblend"));
        Register(new FileBlend("filter.videoblend")); Register(new Deinterlace());
        // 配置・動き
        Register(new Shatter()); Register(new Scatter()); Register(new CircleArrange()); Register(new RandomArrange());
    }

    /// <summary>オブジェクトごとに決まる乱数の種 (同じオブジェクトなら、いつ描いても同じ)。</summary>
    internal static uint StableSeed(TimelineObject? o)
    {
        uint h = 2166136261;
        if (o?.Id is { } id) foreach (char c in id) h = (h ^ c) * 16777619;
        return h;
    }

    /// <summary>絵を w × h に引き伸ばした新しい絵 (GPU 描画中は GPU 上で描く)。</summary>
    internal static SKImage Resized(Sprite s, EffectArgs a, int w, int h)
    {
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
        if (a.Context.Gpu == null) return a.Context.Own(PixelOps.DrawParallel(w, h, c => c.DrawImage(s.Image, SKRect.Create(w, h), paint)));
        using var surface = a.Context.CreateSurface(w, h);
        if (surface == null) return s.Image;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(s.Image, SKRect.Create(w, h), paint);
        return a.Context.Own(surface.Snapshot());
    }

    /// <summary>0〜1 の乱数 (i 番目・種 seed・用途 salt)。</summary>
    internal static float Rand01(uint i, uint seed, uint salt) => PixelFx.Hash(i, salt, seed) / (float)uint.MaxValue;

    /// <summary>ピクセルを書き換える効果の共通部分 (CPU / GPU どちらでも同じ処理)。</summary>
    private abstract class PixelEffectBase : IVideoEffect, IPixelEffect
    {
        public abstract string Id { get; }
        public virtual EffectFlags Flags => EffectFlags.Deterministic;
        /// <summary>何もしないなら null を返す。</summary>
        protected abstract Action<IntPtr, int, int, int>? Op(EffectArgs a);
        public void Process(PixelBuffer b, EffectArgs a) => Op(a)?.Invoke(b.Pointer, b.Width, b.Height, b.RowBytes);
        public Sprite Apply(Sprite s, EffectArgs a) => Op(a) is { } op ? WithPixels(s, a, op, alsoOnGpu: true) ?? s : s;
    }

    // ================= 色・キー =================

    /// <summary>拡張色設定: 赤・緑・青をそれぞれ足す / 引く (−255〜255)。色の変換なので、ほかの色の変換とまとめて処理される。</summary>
    private sealed class ExtColor : IVideoEffect, IColorMatrixEffect
    {
        public string Id => "filter.extcolor";
        public float[]? GetMatrix(EffectArgs a)
        {
            float r = a.Track("r") / 255f, g = a.Track("g") / 255f, b = a.Track("b") / 255f;
            if (r == 0 && g == 0 && b == 0) return null;
            return new float[] { 1, 0, 0, 0, r, 0, 1, 0, 0, g, 0, 0, 1, 0, b, 0, 0, 0, 1, 0 };
        }
        public Sprite Apply(Sprite s, EffectArgs a) => GetMatrix(a) is { } m ? ApplyMatrix(s, a, m) : s;
    }

    /// <summary>特定色域変換: 指定の色に近い色相だけを、別の色相に変える (彩度・明るさも変えられる)。</summary>
    private sealed class HueConvert : PixelEffectBase
    {
        public override string Id => "filter.hueconvert";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            a.Color("from").ToHsv(out float fromHue, out _, out _);
            a.Color("to").ToHsv(out float toHue, out _, out _);
            float shift = toHue - fromHue + a.Track("hue");
            float range = a.Track("range"), edge = a.Track("edge");
            float sat = a.Track("saturation") / 100f, val = a.Track("value") / 100f;
            if (MathF.Abs(shift % 360) < 1e-3f && sat == 1 && val == 1) return null;
            return (px, w, h, rb) => PixelFx.HueConvert(px, w, h, rb, fromHue, range, edge, shift, sat, val);
        }
    }

    /// <summary>カラーキー: 指定の色に近い所 (明るさの差と色の差がそれぞれ範囲内) を透明にする。</summary>
    private sealed class ColorKey : PixelEffectBase
    {
        public override string Id => "filter.colorkey";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            var c = a.Color("color");
            float luma = a.Track("luma"), chroma = a.Track("chroma"), edge = Math.Max(0.001f, a.Track("edge") / 100f);
            return (px, w, h, rb) => PixelFx.ColorKey(px, w, h, rb, c.Red, c.Green, c.Blue, luma, chroma, edge);
        }
    }

    /// <summary>ルミナンスキー: 暗い所 (または明るい所) を透明にする。</summary>
    private sealed class LumaKey : PixelEffectBase
    {
        public override string Id => "filter.lumakey";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            float level = a.Track("level"), soft = a.Track("soft");
            bool bright = a.Effect.GetInt("type") == 1;
            return (px, w, h, rb) => PixelFx.LumaKey(px, w, h, rb, level, soft, bright);
        }
    }

    // ================= 光 =================

    /// <summary>拡散光: ぼかした絵をスクリーンで重ねて、やわらかく光らせる。</summary>
    private sealed class Diffuse : IVideoEffect
    {
        public string Id => "filter.diffuse";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float strength = Math.Clamp(a.Track("strength") / 100f, 0, 1), radius = a.Track("radius");
            if (strength <= 0 || radius <= 0) return s;
            var blurred = Gaussian(s, a, radius, true);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Screen, Color = SKColors.White.WithAlpha((byte)MathF.Round(strength * 255)) };
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                c.DrawImage(s.Image, x, y);
                PixelBlend.DrawImage(c, blurred.Image, x - (blurred.Cx - s.Cx), y - (blurred.Cy - s.Cy), paint);
            });
        }
    }

    /// <summary>閃光: 明るい部分から、中心に向かう光の筋 (放射状) を出して足す。</summary>
    private sealed class Flash : IVideoEffect
    {
        public string Id => "filter.flash";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float strength = Math.Clamp(a.Track("strength") / 100f, 0, 4), length = Math.Max(0, a.Track("length") / 100f);
            float threshold = Math.Clamp(a.Track("threshold"), 0, 100) / 100f * 255f;
            if (strength <= 0 || length <= 0) return s;
            float ox = a.Track("x"), oy = a.Track("y");
            var bright = BrightPass(s, a, threshold);
            int passes = a.IsDraft ? 8 : 10;
            // 光の筋は絵の外まで伸びるので、伸びるぶんの余白を付ける (いちばん遠い所が √(1 + 長さ) 倍まで伸びる)
            float far = MathF.Sqrt(s.Width * s.Width + s.Height * s.Height) / 2 + MathF.Sqrt(ox * ox + oy * oy);
            int pad = a.IsFrameBuffer ? 0 : (int)Math.Min(MathF.Ceiling(far * (MathF.Sqrt(1 + length) - 1)), 4096);
            bool clampEdge = a.IsFrameBuffer;
            var rays = AtHalfForPreview(bright, a, true, (half, k) =>
                           RadialBlurSprite(half, a, (int)(pad * k), (int)(pad * k), half.Cx + (int)(pad * k) + ox * k, half.Cy + (int)(pad * k) + oy * k, length, clampEdge, passes))
                       ?? RadialBlurSprite(bright, a, pad, pad, bright.Cx + pad + ox, bright.Cy + pad + oy, length, clampEdge, passes);
            if (rays is not { } r) return s;
            int times = Math.Max(1, (int)MathF.Ceiling(strength * 2));
            using var paint = new SKPaint { BlendMode = SKBlendMode.Plus, Color = SKColors.White.WithAlpha((byte)Math.Clamp(MathF.Round(strength * 2 / times * 255), 0, 255)) };
            bool only = a.Check("only");
            int rp = Math.Max(0, (int)MathF.Round(r.Cx - s.Cx));
            return Redraw(s, a, rp, rp, rp, rp, (c, x, y) =>
            {
                if (!only) c.DrawImage(s.Image, x, y);
                for (int i = 0; i < times; i++) PixelBlend.DrawImage(c, r.Image, x - (r.Cx - s.Cx), y - (r.Cy - s.Cy), paint);
            });
        }
    }

    /// <summary>ライト: 形の縁を、光の当たる向きから照らしたように明るくする (逆光のふち取りのような光)。</summary>
    private sealed class RimLight : PixelEffectBase
    {
        public override string Id => "filter.light";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            float strength = a.Track("strength") / 100f, width = a.Track("width"), dir = Math.Clamp(a.Track("direction") / 100f, 0, 1);
            if (strength <= 0 || width <= 0) return null;
            float rad = a.Track("angle") * MathF.PI / 180f;
            float lx = MathF.Sin(rad), ly = -MathF.Cos(rad);   // 0 度 = 上から
            var c = a.Color("color");
            return (px, w, h, rb) => PixelFx.RimLight(px, w, h, rb, width / 2, lx, ly, width * dir, c.Red, c.Green, c.Blue, strength);
        }
    }

    /// <summary>凸エッジ: 縁を盛り上がったように見せる (光の当たる側を明るく、反対側を暗く)。</summary>
    private sealed class Bevel : PixelEffectBase
    {
        public override string Id => "filter.bevel";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            float width = a.Track("width"), height = a.Track("height") / 100f;
            if (width <= 0 || height <= 0) return null;
            float rad = a.Track("angle") * MathF.PI / 180f;
            float lx = MathF.Sin(rad), ly = -MathF.Cos(rad);
            return (px, w, h, rb) => PixelFx.Bevel(px, w, h, rb, width / 2, height, lx, ly);
        }
    }

    /// <summary>エッジ抽出: 輪郭だけを線にする (元の絵に重ねることもできる)。</summary>
    private sealed class EdgeDetect : IVideoEffect
    {
        public string Id => "filter.edge";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float gain = a.Track("strength") / 100f * 2, threshold = Math.Clamp(a.Track("threshold") / 100f, 0, 0.999f);
            bool alphaEdge = a.Effect.GetInt("type") == 1, overlay = a.Check("overlay");
            var c = a.Color("color");
            uint al = c.Alpha;
            uint premul = (uint)(c.Blue * al / 255) | (uint)(c.Green * al / 255) << 8 | (uint)(c.Red * al / 255) << 16 | al << 24;
            // 絵の端に接する縁も線になるよう、周りに 1 ピクセルの透明な余白を付ける
            int pad = a.IsFrameBuffer ? 0 : 1;
            return Resample(s, a, pad, pad, (src, tmp, w, h, rb) =>
            {
                PixelFx.EdgeDetect(src, tmp, w, h, rb, gain, threshold, alphaEdge, premul, overlay);
                return tmp;
            }) ?? s;
        }
    }

    // ================= 変形 =================

    /// <summary>波紋: 中心から波が広がるようにゆがめる。速さを付けると波が外へ進む。</summary>
    private sealed class Ripple : IVideoEffect
    {
        public string Id => "filter.ripple";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float amp = a.Track("amplitude");
            if (MathF.Abs(amp) < 0.05f) return s;
            float wave = a.Track("wavelength"), speed = a.Track("speed"), decay = a.Track("decay");
            float cx = s.Cx + a.Track("x"), cy = s.Cy + a.Track("y");
            float phase = (float)(2 * Math.PI * speed * a.Time % (2 * Math.PI));
            bool clamp = a.IsFrameBuffer;
            return RippleSprite(s, a, 0, 0, cx, cy, amp, wave, phase, decay, clamp) ?? s;
        }
    }

    /// <summary>極座標変換: 絵を円形に巻き付ける (横 → 円周、縦 → 半径)。</summary>
    private sealed class Polar : IVideoEffect
    {
        public string Id => "filter.polar";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float zoom = Math.Max(0.01f, a.Track("zoom") / 100f), hole = a.Track("hole"), rot = a.Track("rotation"), swirl = a.Track("swirl") / 100f;
            float radius = Math.Min(s.Width, s.Height) / 2f * zoom;
            return PolarSprite(s, a, radius, hole, rot, swirl) ?? s;
        }
    }

    /// <summary>
    /// ディスプレイスメントマップ (部分変形): 円・四角形の範囲の中を、移動・拡大・回転でゆがめる。
    /// 形を「画像ファイル」にすると、画像の赤で横、緑で縦にずらす。
    /// </summary>
    private sealed class Displace : IVideoEffect
    {
        public string Id => "filter.displace";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            int mode = a.Effect.GetInt("mode"), shape = a.Effect.GetInt("shape");
            float p1 = a.Track("param1"), p2 = a.Track("param2");
            bool clamp = a.IsFrameBuffer;
            if (shape == 2)
            {
                // 画像ファイル: 絵と同じ大きさに引き伸ばして、色をずれにする
                var map = a.Context.LoadImage?.Invoke(a.Effect.GetString("file"));
                if (map == null || (p1 == 0 && p2 == 0)) return s;
                int w0 = s.Width, h0 = s.Height;
                using var mapPaint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
                using var stretched = PixelOps.DrawParallel(w0, h0, c => c.DrawImage(map, SKRect.Create(w0, h0), mapPaint));
                using var mp = stretched.PeekPixels();
                if (mp == null) return s;
                return Resample(s, a, 0, 0, (src, tmp, w, h, rb) =>
                {
                    PixelFx.DisplaceByMap(src, tmp, w, h, rb, mp.GetPixels(), mp.RowBytes, p1, p2, clamp);
                    return tmp;
                }) ?? s;
            }
            float size = Math.Max(1, a.Track("size")), aspect = Math.Clamp(a.Track("aspect") / 100f, -0.99f, 0.99f);
            float rx = size / 2 * (aspect < 0 ? 1 + aspect : 1), ry = size / 2 * (aspect > 0 ? 1 - aspect : 1);
            float cx = s.Cx + a.Track("x"), cy = s.Cy + a.Track("y"), feather = a.Track("feather") / 100f;
            if (mode == 0 ? p1 == 0 && p2 == 0 : p1 == 0) return s;
            return DisplaceSprite(s, a, 0, 0, mode, shape == 1, cx, cy, rx, ry, feather, p1, p2, clamp) ?? s;
        }
    }

    /// <summary>ミラー: 絵の下 (上・左・右) に、反転した絵を映り込みのように描く。離れるほど薄くなる。</summary>
    private sealed class Mirror : IVideoEffect
    {
        public string Id => "filter.mirror";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int dir = a.Effect.GetInt("direction");   // 0 = 下, 1 = 上, 2 = 右, 3 = 左
            float alpha = Math.Clamp(1 - a.Track("alpha") / 100f, 0, 1), fade = Math.Max(0.001f, a.Track("fade") / 100f), gap = a.Track("gap");
            if (alpha <= 0) return s;
            int w = s.Width, h = s.Height;
            bool vertical = dir <= 1;
            int extra = (int)MathF.Ceiling((vertical ? h : w) + Math.Max(0, gap));
            if (a.IsFrameBuffer) extra = 0;
            int padL = dir == 3 ? extra : 0, padR = dir == 2 ? extra : 0, padT = dir == 1 ? extra : 0, padB = dir == 0 ? extra : 0;
            var edge = SKColors.White.WithAlpha((byte)MathF.Round(alpha * 255));
            return Redraw(s, a, padL, padT, padR, padB, (c, x, y) =>
            {
                c.DrawImage(s.Image, x, y);
                // 映り込みの範囲と、元の絵に接する辺から遠い辺へのグラデーション
                SKRect area; SKPoint near, far;
                float len = (vertical ? h : w) * fade;
                switch (dir)
                {
                    case 0: area = SKRect.Create(x, y + h + gap, w, h); near = new(0, area.Top); far = new(0, area.Top + len); break;
                    case 1: area = SKRect.Create(x, y - gap - h, w, h); near = new(0, area.Bottom); far = new(0, area.Bottom - len); break;
                    case 2: area = SKRect.Create(x + w + gap, y, w, h); near = new(area.Left, 0); far = new(area.Left + len, 0); break;
                    default: area = SKRect.Create(x - gap - w, y, w, h); near = new(area.Right, 0); far = new(area.Right - len, 0); break;
                }
                // 反射した絵のレイヤー (CPU 描画では自前で合成する。PixelBlend)
                using var layer = PixelBlend.BeginLayer(c, area, SKBlendMode.SrcOver);
                var lc = layer.Canvas;
                lc.Save();
                if (vertical) { lc.Translate(0, area.Top + area.Bottom); lc.Scale(1, -1); lc.DrawImage(s.Image, x, area.Top); }
                else { lc.Translate(area.Left + area.Right, 0); lc.Scale(-1, 1); lc.DrawImage(s.Image, area.Left, y); }
                lc.Restore();
                using var shader = SKShader.CreateLinearGradient(near, far, new[] { edge, SKColors.Transparent }, SKShaderTileMode.Clamp);
                using var mask = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstIn };
                PixelBlend.Draw(lc, area, mask, (cv, p) => cv.DrawRect(area, p));
            });
        }
    }

    /// <summary>万華鏡: 中心の周りを扇形に分けて、鏡のように折り返して並べる。</summary>
    private sealed class Kaleidoscope : IVideoEffect
    {
        public string Id => "filter.kaleido";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int n = Math.Clamp((int)MathF.Round(a.Track("count")), 1, 360);
            float cx = s.Cx + a.Track("x"), cy = s.Cy + a.Track("y"), rot = a.Track("rotation");
            bool clamp = a.IsFrameBuffer || a.Check("clamp");
            return KaleidoSprite(s, a, 0, 0, cx, cy, n * 2, rot, clamp) ?? s;
        }
    }

    /// <summary>球体: 絵を地球儀のように球に貼る。X・Y・Z 軸で回せる (Y 軸で回すと自転する)。</summary>
    private sealed class SphereMap : IVideoEffect
    {
        public string Id => "filter.sphere";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            float size = a.Track("size");
            if (size <= 0) size = Math.Max(s.Width, s.Height) / 2f;   // 0 = 絵の大きい方の辺の半分
            int d = Math.Clamp((int)MathF.Round(size), 2, 8192);
            float D(string k) => a.Track(k) * MathF.PI / 180f;
            float ax = D("rx"), ay = D("ry") + (float)(2 * Math.PI * a.Track("spin") * a.Time), az = D("rz");
            // 画面の向き → 球の向き: 逆回転 (Z → X → Y の逆順)
            var m = System.Numerics.Matrix4x4.CreateRotationY(-ay) * System.Numerics.Matrix4x4.CreateRotationX(-ax) * System.Numerics.Matrix4x4.CreateRotationZ(-az);
            // 行ベクトルの行列なので、列を取り出して 3x3 (行優先) にする
            var r = new[] { m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, m.M13, m.M23, m.M33 };
            float shade = Math.Clamp(a.Track("shade") / 100f, 0, 1);
            if (CopyPixels(s) is not { } copy) return s;
            try
            {
                int rowBytes = d * 4;
                var dst = PixelPool.Rent((long)rowBytes * d);
                PixelFx.Sphere(copy.Buffer, s.Width, s.Height, copy.RowBytes, dst, d, rowBytes, r, shade);
                var img = a.Context.Own(PixelPool.ToImage(dst, Info(d, d), rowBytes));
                return new Sprite(img, d / 2f, d / 2f);
            }
            finally { PixelPool.Return(copy.Buffer, (long)copy.RowBytes * s.Height); }
        }
    }

    /// <summary>
    /// 単位正方形 (0,0)-(1,1) を、4 つの点 (左上・右上・右下・左下) の四角形に写す射影変換。
    /// </summary>
    internal static SKMatrix SquareToQuad(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3)
    {
        float dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dx3 = p0.X - p1.X + p2.X - p3.X;
        float dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, dy3 = p0.Y - p1.Y + p2.Y - p3.Y;
        float g = 0, hh = 0;
        float den = dx1 * dy2 - dx2 * dy1;
        if ((MathF.Abs(dx3) > 1e-4f || MathF.Abs(dy3) > 1e-4f) && MathF.Abs(den) > 1e-6f)
        {
            g = (dx3 * dy2 - dx2 * dy3) / den;
            hh = (dx1 * dy3 - dx3 * dy1) / den;
        }
        return new SKMatrix
        {
            ScaleX = p1.X - p0.X + g * p1.X, SkewX = p3.X - p0.X + hh * p3.X, TransX = p0.X,
            SkewY = p1.Y - p0.Y + g * p1.Y, ScaleY = p3.Y - p0.Y + hh * p3.Y, TransY = p0.Y,
            Persp0 = g, Persp1 = hh, Persp2 = 1,
        };
    }

    /// <summary>簡易変形: 4 つの角を動かして、絵を自由な四角形にゆがめる (遠近のある変形)。</summary>
    private sealed class FreeTransform : IVideoEffect
    {
        public string Id => "filter.freeform";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int w = s.Width, h = s.Height;
            var pts = new[]
            {
                new SKPoint(a.Track("x1"), a.Track("y1")), new SKPoint(w + a.Track("x2"), a.Track("y2")),
                new SKPoint(w + a.Track("x3"), h + a.Track("y3")), new SKPoint(a.Track("x4"), h + a.Track("y4")),
            };
            if (pts[0] == new SKPoint(0, 0) && pts[1] == new SKPoint(w, 0) && pts[2] == new SKPoint(w, h) && pts[3] == new SKPoint(0, h)) return s;
            float minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
            int padL = Math.Max(0, (int)MathF.Ceiling(-minX)), padT = Math.Max(0, (int)MathF.Ceiling(-minY));
            int padR = Math.Max(0, (int)MathF.Ceiling(maxX - w)), padB = Math.Max(0, (int)MathF.Ceiling(maxY - h));
            if (a.IsFrameBuffer) padL = padT = padR = padB = 0;
            var quad = SquareToQuad(pts[0], pts[1], pts[2], pts[3]).PreConcat(SKMatrix.CreateScale(1f / w, 1f / h));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
            return Redraw(s, a, padL, padT, padR, padB, (c, x, y) =>
            {
                c.Translate(x, y);
                c.Concat(ref quad);
                c.DrawImage(s.Image, 0, 0, paint);
            });
        }
    }

    // ================= ぼかし =================

    /// <summary>
    /// レンズブラー: 円の形にぼかす (ピントが外れたようなぼけ)。光の強さで、明るい所を光の玉のように広げる。
    /// 大きくぼかすときは、半径が 12 ピクセル以下になるまで縮小して計算する (ぼけた絵は細部がないので見分けがつかない)。
    /// </summary>
    private sealed class LensBlur : IVideoEffect
    {
        public string Id => "filter.lensblur";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float radius = a.Track("radius"), boost = Math.Clamp(a.Track("boost") / 100f, 0, 1);
            if (radius < 0.5f) return s;
            bool clamp = a.IsFrameBuffer || a.Check("fixed");
            int pad = clamp ? 0 : (int)MathF.Ceiling(radius) + 1;
            int k = 1;
            while (k < 8 && radius / k > 12) k *= 2;
            if (k == 1)
                return Resample(s, a, pad, pad, (src, tmp, w, h, rb) => { PixelFx.LensBlur(src, tmp, w, h, rb, radius, boost, clamp); return tmp; }) ?? s;
            // 縮小 (余白も付ける) → ぼかす → 元の大きさに戻す
            SKImage? raster = null;
            using var pm = s.Image.PeekPixels();
            if (pm == null && CopyPixels(s) is { } cp) raster = PixelPool.ToImage(cp.Buffer, Info(s.Width, s.Height), cp.RowBytes);
            var source = raster ?? s.Image;
            int pw = s.Width + pad * 2, ph = s.Height + pad * 2;
            var small = PixelOps.Halve(source, pw, ph, pad, pad);
            raster?.Dispose();
            for (int step = 2; step < k; step *= 2)
            {
                var next = PixelOps.Halve(small, small.Width, small.Height);
                small.Dispose();
                small = next;
            }
            using var smallOwner = small;
            var blurred = Resample(new Sprite(small, 0, 0), a, 0, 0, (src, tmp, w, h, rb) => { PixelFx.LensBlur(src, tmp, w, h, rb, radius / k, boost, clamp); return tmp; });
            if (blurred is not { } b) return s;
            using var up = new SKPaint { FilterQuality = SKFilterQuality.Low, BlendMode = SKBlendMode.Src };
            return Redraw(s, a, pad, pad, pad, pad, (c, _, _) => c.DrawImage(b.Image, SKRect.Create(b.Width, b.Height), SKRect.Create(0, 0, b.Width * k, b.Height * k), up));
        }
    }

    /// <summary>
    /// モーションブラー: オブジェクトの前のフレームからの動き (変形の X・Y) に合わせて、動いた向きに尾を引くようにぼかす。
    /// 間隔 (%) は、1 フレームで動いた距離の何倍ぶんぼかすか (シャッターの開いている長さ)。
    /// </summary>
    private sealed class MotionBlur : IVideoEffect
    {
        public string Id => "filter.motionblur";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            var draw = a.Object?.Draw;
            if (draw == null || a.IsFrameBuffer) return s;
            int f = a.Frame;
            float X(int fr) => (float)draw.Eval("x", a.Bounds, fr);
            float Y(int fr) => (float)draw.Eval("y", a.Bounds, fr);
            float shutter = a.Track("shutter") / 100f;
            float vx = (X(f) - X(f - 1)) * shutter, vy = (Y(f) - Y(f - 1)) * shutter;
            // 絵の向き (回転・拡大率) に合わせて、絵の中での向きに直す
            float rot = -(float)draw.Eval("rotation", a.Bounds, f) * MathF.PI / 180f;
            float scale = MathF.Max(0.01f, MathF.Abs((float)draw.Eval("scale", a.Bounds, f) / 100f) * s.Scale);
            float lx = (vx * MathF.Cos(rot) - vy * MathF.Sin(rot)) / scale, ly = (vx * MathF.Sin(rot) + vy * MathF.Cos(rot)) / scale;
            float length = MathF.Sqrt(lx * lx + ly * ly);
            if (length < 1) return s;
            int padX = (int)MathF.Ceiling(MathF.Abs(lx)) + 1, padY = (int)MathF.Ceiling(MathF.Abs(ly)) + 1;
            int passes = a.IsDraft ? 8 : 10;
            var r = DirectionalBlurSprite(s, a, padX, padY, lx, ly, length, false, passes);
            if (r is not { } blurred) return s;
            // ぼかしは今の位置を中心に前後へ広がるので、半分だけ後ろへずらして、通ってきた側に尾を引かせる
            return blurred with { Cx = blurred.Cx + lx / 2, Cy = blurred.Cy + ly / 2 };
        }
    }

    /// <summary>境界ぼかし: 形の縁を内側に向かってなめらかに透明にする。</summary>
    private sealed class EdgeBlur : PixelEffectBase
    {
        public override string Id => "filter.edgeblur";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            float r = a.Track("radius");
            if (r <= 0) return null;
            return (px, w, h, rb) => PixelFx.EdgeFeather(px, w, h, rb, r / 2);
        }
    }

    // ================= その他 =================

    /// <summary>基本効果: 位置・回転・拡大率・透明度をずらし、縦横比を変える (変形とは別に、エフェクトとして足す)。</summary>
    private sealed class BasicEffect : IVideoEffect
    {
        public string Id => "filter.basic";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            ref var m = ref a.Context.Modifiers;
            m.Ox += a.Track("x"); m.Oy += a.Track("y");
            m.Rz += a.Track("rotation");
            m.Zoom *= Math.Max(0, a.Track("scale") / 100f);
            m.Alpha *= Math.Clamp(1 - a.Track("alpha") / 100f, 0, 1);
            float aspect = Math.Clamp(a.Track("aspect") / 100f, -1, 1);
            if (MathF.Abs(aspect) < 1e-4f) return s;
            // 正なら横を縮め、負なら縦を縮める
            float sx = aspect > 0 ? 1 - aspect : 1, sy = aspect < 0 ? 1 + aspect : 1;
            int w = Math.Max(1, (int)MathF.Round(s.Width * sx)), h = Math.Max(1, (int)MathF.Round(s.Height * sy));
            return new Sprite(Resized(s, a, w, h), s.Cx * sx, s.Cy * sy, s.Scale);
        }
    }

    /// <summary>振動: X・Y・Z・回転を、決めた周期 (フレーム) ごとにランダムに揺らす。</summary>
    private sealed class Shake : IVideoEffect
    {
        public string Id => "filter.shake";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int period = Math.Max(1, (int)MathF.Round(a.Track("period")));
            uint seed = (uint)(int)a.Track("seed") ^ (a.Check("perObject") ? StableSeed(a.Object) : 0);
            double pos = (a.Frame - (a.Object?.Start ?? 0)) / (double)period;
            long step = (long)Math.Floor(pos);
            bool smooth = a.Check("smooth");
            float V(uint axis)
            {
                float R(long i) => Rand01((uint)i, seed, axis) * 2 - 1;
                if (!smooth) return R(step);
                float t = (float)(pos - step);
                t = t * t * (3 - 2 * t);
                return R(step) + (R(step + 1) - R(step)) * t;
            }
            ref var m = ref a.Context.Modifiers;
            m.Ox += a.Track("x") * V(1); m.Oy += a.Track("y") * V(2); m.Oz += a.Track("z") * V(3);
            m.Rz += a.Track("rotation") * V(4);
            return s;
        }
    }

    /// <summary>画像ループ: 絵を縦横に並べる。速さを付けると、並べた絵の中で流れるように動く。</summary>
    private sealed class ImageLoop : IVideoEffect
    {
        public string Id => "filter.loop";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int nx = Math.Clamp((int)MathF.Round(a.Track("countX")), 1, 100), ny = Math.Clamp((int)MathF.Round(a.Track("countY")), 1, 100);
            float vx = a.Track("speedX"), vy = a.Track("speedY");
            if (nx == 1 && ny == 1 && vx == 0 && vy == 0) return s;
            int w = s.Width, h = s.Height;
            double t = a.Time;
            float ox = (float)(vx * t % w), oy = (float)(vy * t % h);
            int ow = a.IsFrameBuffer ? w : Math.Min(16384, w * nx), oh = a.IsFrameBuffer ? h : Math.Min(16384, h * ny);
            var local = a.IsFrameBuffer
                ? SKMatrix.CreateScale(1f / nx, 1f / ny).PreConcat(SKMatrix.CreateTranslation(ox, oy))
                : SKMatrix.CreateTranslation(ox, oy);
            using var shader = SKShader.CreateImage(s.Image, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, local);
            using var paint = new SKPaint { Shader = shader, FilterQuality = SKFilterQuality.Low };
            var sprite = Redraw(s, a, 0, 0, ow - w, oh - h, (c, x, y) => c.DrawRect(SKRect.Create(ow, oh), paint));
            // 並べた全体の中心を、オブジェクトの中心にする
            return a.IsFrameBuffer ? sprite : sprite with { Cx = ow / 2f + (s.Cx - w / 2f), Cy = oh / 2f + (s.Cy - h / 2f) };
        }
    }

    internal static readonly SKBlendMode[] FileBlendModes =
    {
        SKBlendMode.SrcOver, SKBlendMode.Plus, SKBlendMode.Multiply, SKBlendMode.Screen, SKBlendMode.Overlay,
        SKBlendMode.Lighten, SKBlendMode.Darken, SKBlendMode.Luminosity, SKBlendMode.Color, SKBlendMode.HardLight, SKBlendMode.Difference,
    };

    /// <summary>
    /// 画像ファイル合成・動画ファイル合成: ファイルの絵を、オブジェクトの見えている部分にだけ合成する。
    /// ループ画像なら、ファイルの絵を敷き詰める。
    /// </summary>
    private sealed class FileBlend(string id) : IVideoEffect
    {
        public string Id => id;
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            string path = a.Effect.GetString("file");
            if (string.IsNullOrEmpty(path)) return s;
            SKImage? img;
            if (id == "filter.videoblend")
            {
                double at = a.Track("position") + a.Time * a.Track("speed") / 100.0;
                img = a.Context.LoadVideoFrame?.Invoke((a.Object?.Id ?? "") + "#" + id + a.Effect.GetHashCode(), path, Math.Max(0, at), a.Check("loopVideo"));
            }
            else img = a.Context.LoadImage?.Invoke(path);
            if (img == null) return s;
            float strength = Math.Clamp(1 - a.Track("alpha") / 100f, 0, 1);
            if (strength <= 0) return s;
            float zoom = Math.Max(0.001f, a.Track("scale") / 100f);
            int mode = Math.Clamp(a.Effect.GetInt("blend"), 0, FileBlendModes.Length - 1);
            bool tile = a.Check("tile");
            float cx = s.Cx + a.Track("x"), cy = s.Cy + a.Track("y");
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                // 元の絵の形で切り抜くためのレイヤー (CPU 描画では自前で合成する。PixelBlend)
                using var layer = PixelBlend.BeginLayer(c, SKRect.Create(x, y, s.Width, s.Height), SKBlendMode.SrcOver);
                var lc = layer.Canvas;
                lc.DrawImage(s.Image, x, y);
                using var paint = new SKPaint
                {
                    BlendMode = FileBlendModes[mode], FilterQuality = SKFilterQuality.Medium,
                    Color = SKColors.White.WithAlpha((byte)MathF.Round(strength * 255)),
                };
                float iw = img.Width * zoom, ih = img.Height * zoom;
                var dest = SKRect.Create(x + cx - iw / 2, y + cy - ih / 2, iw, ih);
                if (tile)
                {
                    var local = SKMatrix.CreateScale(zoom, zoom).PostConcat(SKMatrix.CreateTranslation(dest.Left, dest.Top));
                    using var shader = SKShader.CreateImage(img, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, local);
                    paint.Shader = shader;
                    PixelBlend.Draw(lc, SKRect.Create(x, y, s.Width, s.Height), paint, (cv, p) => cv.DrawRect(SKRect.Create(x, y, s.Width, s.Height), p));
                }
                else PixelBlend.Draw(lc, dest, paint, (cv, p) => cv.DrawImage(img, dest, p));
                // 元の絵の形で切り抜く
                using var clip = new SKPaint { BlendMode = SKBlendMode.DstIn };
                PixelBlend.DrawImage(lc, s.Image, x, y, clip);
            });
        }
    }

    /// <summary>インターレース解除: 縞模様 (インターレース) の動画を、1 枚の絵に直す。</summary>
    private sealed class Deinterlace : IVideoEffect
    {
        public string Id => "filter.deinterlace";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int mode = a.Effect.GetInt("mode");
            return Resample(s, a, 0, 0, (src, tmp, w, h, rb) => { PixelFx.Deinterlace(src, tmp, w, h, rb, mode); return tmp; }) ?? s;
        }
    }

    // ================= 配置・動き =================

    /// <summary>
    /// 飛び散る (以前の「砕け散る」): 開始時刻から、絵を四角い破片に分けて一度に飛び散らせる (中心から外へ・重力で落ちる・回る・消える)。
    /// </summary>
    private sealed class Scatter : IVideoEffect
    {
        public string Id => "filter.scatter";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float t = (float)(a.Time - a.Track("start"));
            if (t <= 0) return s;
            float speed = a.Track("speed"), gravity = a.Track("gravity"), spin = a.Track("spin"), fade = a.Track("fade");
            if (fade > 0 && t >= fade) return Redraw(s, a, 0, 0, 0, 0, (_, _, _) => { });
            int piece = Math.Max(2, (int)MathF.Round(a.Track("size")));
            float ecx = s.Cx + a.Track("x"), ecy = s.Cy + a.Track("y");
            uint seed = (uint)(int)a.Track("seed") ^ StableSeed(a.Object);
            int W = a.Context.Project.Settings.Width, H = a.Context.Project.Settings.Height;
            float reach = speed * 1.5f * t + 0.5f * MathF.Abs(gravity) * t * t + piece;
            int pad = a.IsFrameBuffer ? 0 : (int)Math.Min(MathF.Ceiling(reach), Math.Max(W, H));
            byte alpha = fade > 0 ? (byte)MathF.Round(255 * (1 - t / fade)) : (byte)255;
            int cols = (s.Width + piece - 1) / piece, rows = (s.Height + piece - 1) / piece;
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, Color = SKColors.White.WithAlpha(alpha) };
            return Redraw(s, a, pad, pad, pad, pad, (c, x, y) =>
            {
                for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    uint id = (uint)(j * cols + i);
                    var src = SKRect.Create(i * piece, j * piece, Math.Min(piece, s.Width - i * piece), Math.Min(piece, s.Height - j * piece));
                    float pcx = src.MidX, pcy = src.MidY;
                    float dx = pcx - ecx, dy = pcy - ecy, len = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
                    // 外向き + ばらつき。中心に近い破片ほど速く飛ぶ
                    float sp = speed * (0.5f + Rand01(id, seed, 1));
                    float ang = MathF.Atan2(dy, dx) + (Rand01(id, seed, 2) - 0.5f) * 0.8f;
                    float px = pcx + MathF.Cos(ang) * sp * t, py = pcy + MathF.Sin(ang) * sp * t + 0.5f * gravity * t * t;
                    float rot = spin * (Rand01(id, seed, 3) * 2 - 1) * t;
                    c.Save();
                    c.Translate(x + px, y + py);
                    if (rot != 0) c.RotateDegrees(rot);
                    c.DrawImage(s.Image, src, SKRect.Create(-src.Width / 2, -src.Height / 2, src.Width, src.Height), paint);
                    c.Restore();
                }
            });
        }
    }

    /// <summary>
    /// 砕け散る: 割れたガラスのように、ふぞろいな三角形の破片に割れて落ちる。
    /// 中心から割れ始めて外へ広がり (時間差)、破片は外へ飛びながら重力で落ち、ばらばらに回る (奥行きの回転は縦横の縮みで表す)。
    /// </summary>
    private sealed class Shatter : IVideoEffect
    {
        public string Id => "filter.shatter";

        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float t = (float)((a.Time - a.Track("start")) * a.Track("playback"));
            if (t <= 0) return s;
            int piece = Math.Max(10, (int)MathF.Round(a.Track("size")));
            float speed = a.Track("speed") / 100f, gravity = a.Track("gravity") / 100f * 980f;
            float delay = Math.Max(0, a.Track("delay") / 100f), distance = Math.Max(0, a.Track("distance") / 100f);
            float spin = Math.Max(0, a.Track("rotation") / 100f), randDir = Math.Max(0, a.Track("direction") / 100f);
            float ecx = s.Cx + a.Track("x"), ecy = s.Cy + a.Track("y");
            uint seed = (uint)(int)a.Track("seed") ^ StableSeed(a.Object);
            int w = s.Width, h = s.Height;
            int cols = Math.Max(1, (w + piece - 1) / piece), rows = Math.Max(1, (h + piece - 1) / piece);
            float cw = w / (float)cols, ch = h / (float)rows;
            // 格子の点 (内側の点だけをずらすので、破片どうしはすき間なく並ぶ)
            var vx = new float[(cols + 1) * (rows + 1)]; var vy = new float[vx.Length];
            for (int j = 0; j <= rows; j++)
            for (int i = 0; i <= cols; i++)
            {
                int k = j * (cols + 1) + i;
                bool inner = i > 0 && i < cols && j > 0 && j < rows;
                vx[k] = i * cw + (inner ? (Rand01((uint)k, seed, 31) - 0.5f) * cw * 0.7f : 0);
                vy[k] = j * ch + (inner ? (Rand01((uint)k, seed, 32) - 0.5f) * ch * 0.7f : 0);
            }
            float maxD = MathF.Max(1, MathF.Sqrt(MathF.Max(ecx * ecx, (w - ecx) * (w - ecx)) + MathF.Max(ecy * ecy, (h - ecy) * (h - ecy))));
            var still = new SKPath();
            var moving = new List<(SKPath Path, float Cx, float Cy, float Px, float Py, float Rz, float Sx, float Sy)>();
            float reach = 0;
            for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                int k00 = j * (cols + 1) + i, k10 = k00 + 1, k01 = k00 + cols + 1, k11 = k01 + 1;
                bool flip = Rand01((uint)k00, seed, 33) < 0.5f;
                var tris = flip
                    ? new[] { (k00, k10, k11), (k00, k11, k01) }
                    : new[] { (k00, k10, k01), (k10, k11, k01) };
                for (int n = 0; n < 2; n++)
                {
                    var (p0, p1, p2) = tris[n];
                    float gx = (vx[p0] + vx[p1] + vx[p2]) / 3, gy = (vy[p0] + vy[p1] + vy[p2]) / 3;
                    uint id = (uint)((j * cols + i) * 2 + n);
                    float dx = gx - ecx, dy = gy - ecy, d = MathF.Sqrt(dx * dx + dy * dy);
                    // 中心から遠い破片ほど遅れて割れる
                    float tt = t - d / maxD * delay * 0.6f;
                    var path = new SKPath();
                    path.MoveTo(vx[p0], vy[p0]); path.LineTo(vx[p1], vy[p1]); path.LineTo(vx[p2], vy[p2]); path.Close();
                    if (tt <= 0) { still.AddPath(path); path.Dispose(); continue; }
                    float ang = (d < 1e-3f ? Rand01(id, seed, 34) * 2 * MathF.PI : MathF.Atan2(dy, dx)) + (Rand01(id, seed, 35) - 0.5f) * MathF.PI * randDir;
                    // 中心に近い破片ほど強く飛ぶ (距離影響)
                    float v = speed * 120 * (0.5f + Rand01(id, seed, 36)) * (1 + distance * (1 - d / maxD));
                    float px = gx + MathF.Cos(ang) * v * tt, py = gy + MathF.Sin(ang) * v * tt + 0.5f * gravity * tt * tt;
                    float rz = (Rand01(id, seed, 37) * 2 - 1) * 540 * spin * tt;
                    float rx = (Rand01(id, seed, 38) * 2 - 1) * 9.4f * spin * tt, ry = (Rand01(id, seed, 39) * 2 - 1) * 9.4f * spin * tt;
                    moving.Add((path, gx, gy, px, py, rz, MathF.Cos(ry), MathF.Cos(rx)));
                    reach = MathF.Max(reach, MathF.Max(MathF.Abs(px - gx), MathF.Abs(py - gy)) + piece);
                }
            }
            int W = a.Context.Project.Settings.Width, H = a.Context.Project.Settings.Height;
            int pad = a.IsFrameBuffer ? 0 : (int)Math.Min(MathF.Ceiling(reach), Math.Max(W, H));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low };
            try
            {
                return Redraw(s, a, pad, pad, pad, pad, (c, x, y) =>
                {
                    // まだ割れていない部分は、まとめて 1 回で描く (破片の境目に線が出ない)
                    if (!still.IsEmpty)
                    {
                        c.Save(); c.Translate(x, y); c.ClipPath(still, SKClipOperation.Intersect, true); c.DrawImage(s.Image, 0, 0, paint); c.Restore();
                    }
                    foreach (var m in moving)
                    {
                        c.Save();
                        c.Translate(x + m.Px, y + m.Py);
                        if (m.Rz != 0) c.RotateDegrees(m.Rz);
                        c.Scale(m.Sx, m.Sy);
                        c.Translate(-m.Cx, -m.Cy);
                        c.ClipPath(m.Path, SKClipOperation.Intersect, true);
                        c.DrawImage(s.Image, 0, 0, paint);
                        c.Restore();
                    }
                });
            }
            finally
            {
                still.Dispose();
                foreach (var m in moving) m.Path.Dispose();
            }
        }
    }

    /// <summary>円形配置: 絵を円周上に並べる (個数・半径・回転。外向きにそろえることもできる)。</summary>
    private sealed class CircleArrange : IVideoEffect
    {
        public string Id => "filter.circle";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int n = Math.Clamp((int)MathF.Round(a.Track("count")), 1, 1000);
            float radius = a.Track("radius"), rot = a.Track("rotation") + a.Track("spin") * (float)a.Time * 360, scale = Math.Max(0.01f, a.Track("scale") / 100f);
            bool face = a.Check("face");
            float half = MathF.Sqrt(s.Width * s.Width + s.Height * s.Height) / 2 * scale;
            int pad = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(MathF.Max(0, MathF.Abs(radius) + half - Math.Min(s.Width, s.Height) / 2f));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
            var r = Redraw(s, a, pad, pad, pad, pad, (c, x, y) =>
            {
                for (int i = 0; i < n; i++)
                {
                    float deg = rot + 360f * i / n, rad = deg * MathF.PI / 180f;
                    c.Save();
                    c.Translate(x + s.Cx + radius * MathF.Sin(rad), y + s.Cy - radius * MathF.Cos(rad));
                    if (face) c.RotateDegrees(deg);
                    c.Scale(scale);
                    c.DrawImage(s.Image, -s.Cx, -s.Cy, paint);
                    c.Restore();
                }
            });
            return r;
        }
    }

    /// <summary>ランダム配置: 絵を決めた範囲にランダムに並べる (大きさ・回転もばらつかせられる)。</summary>
    private sealed class RandomArrange : IVideoEffect
    {
        public string Id => "filter.random";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int n = Math.Clamp((int)MathF.Round(a.Track("count")), 1, 2000);
            float rw = Math.Max(0, a.Track("width")), rh = Math.Max(0, a.Track("height"));
            float sizeVar = Math.Clamp(a.Track("sizeVar") / 100f, 0, 1), rotVar = a.Track("rotVar");
            uint seed = (uint)(int)a.Track("seed");
            float half = MathF.Sqrt(s.Width * s.Width + s.Height * s.Height) / 2;
            int padX = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(MathF.Max(0, rw / 2 + half - s.Width / 2f));
            int padY = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(MathF.Max(0, rh / 2 + half - s.Height / 2f));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
            return Redraw(s, a, padX, padY, padX, padY, (c, x, y) =>
            {
                for (int i = 0; i < n; i++)
                {
                    uint id = (uint)i;
                    float px = (Rand01(id, seed, 11) - 0.5f) * rw, py = (Rand01(id, seed, 12) - 0.5f) * rh;
                    float sc = 1 - sizeVar * Rand01(id, seed, 13), ro = (Rand01(id, seed, 14) * 2 - 1) * rotVar;
                    c.Save();
                    c.Translate(x + s.Cx + px, y + s.Cy + py);
                    if (ro != 0) c.RotateDegrees(ro);
                    c.Scale(sc);
                    c.DrawImage(s.Image, -s.Cx, -s.Cy, paint);
                    c.Restore();
                }
            });
        }
    }
}
