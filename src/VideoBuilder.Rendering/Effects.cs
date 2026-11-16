using SkiaSharp;

namespace VideoBuilder.Rendering;

/// <summary>組み込みのフィルタ効果。</summary>
public static class Effects
{
    private static readonly Dictionary<string, IVideoEffect> Map = new();

    static Effects()
    {
        Register(new ColorCorrect()); Register(new Blur()); Register(new Border()); Register(new Shadow());
        Register(new Clip()); Register(new Mosaic()); Register(new ChromaKey()); Register(new Mono());
        Register(new Flip()); Register(new Fade());
    }

    public static void Register(IVideoEffect effect) => Map[effect.Id] = effect;
    public static IVideoEffect? Find(string id) => Map.TryGetValue(id, out var e) ? e : null;

    internal static SKImageInfo Info(int w, int h) => new(Math.Max(1, w), Math.Max(1, h), SKColorType.Bgra8888, SKAlphaType.Premul);

    /// <summary>余白を足した新しい画像に描き直す。draw には元画像を置く位置 (左上) が渡される。</summary>
    internal static Sprite Redraw(Sprite s, EffectArgs a, int padL, int padT, int padR, int padB, Action<SKCanvas, float, float> draw)
    {
        // 極端なサイズでメモリを使い切らないようにする
        int w = Math.Min(s.Width + padL + padR, 16384), h = Math.Min(s.Height + padT + padB, 16384);
        using var surface = SKSurface.Create(Info(w, h));
        if (surface == null) return s;
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas, padL, padT);
        var img = a.Context.Own(surface.Snapshot());
        return new Sprite(img, s.Cx + padL, s.Cy + padT);
    }

    internal static Sprite WithPaint(Sprite s, EffectArgs a, SKPaint paint, int pad = 0) =>
        Redraw(s, a, pad, pad, pad, pad, (c, x, y) => c.DrawImage(s.Image, x, y, paint));

    // 4x5 のカラーマトリクス (平行移動は 0..1)
    internal static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0 };

    /// <summary>a を適用したあとに b を適用する行列。</summary>
    internal static float[] Concat(float[] b, float[] a)
    {
        var r = new float[20];
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 5; col++)
            {
                float v = 0;
                for (int k = 0; k < 4; k++) v += b[row * 5 + k] * a[k * 5 + col];
                if (col == 4) v += b[row * 5 + 4];
                r[row * 5 + col] = v;
            }
        }
        return r;
    }

    internal static float[] Saturation(float s)
    {
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        return new float[]
        {
            lr * (1 - s) + s, lg * (1 - s), lb * (1 - s), 0, 0,
            lr * (1 - s), lg * (1 - s) + s, lb * (1 - s), 0, 0,
            lr * (1 - s), lg * (1 - s), lb * (1 - s) + s, 0, 0,
            0, 0, 0, 1, 0,
        };
    }

    internal static float[] HueRotate(float degrees)
    {
        float rad = degrees * MathF.PI / 180f, c = MathF.Cos(rad), s = MathF.Sin(rad);
        const float lr = 0.213f, lg = 0.715f, lb = 0.072f;
        return new float[]
        {
            lr + c * (1 - lr) + s * -lr, lg + c * -lg + s * -lg, lb + c * -lb + s * (1 - lb), 0, 0,
            lr + c * -lr + s * 0.143f, lg + c * (1 - lg) + s * 0.140f, lb + c * -lb + s * -0.283f, 0, 0,
            lr + c * -lr + s * -(1 - lr), lg + c * -lg + s * lg, lb + c * (1 - lb) + s * lb, 0, 0,
            0, 0, 0, 1, 0,
        };
    }

    private sealed class ColorCorrect : IVideoEffect
    {
        public string Id => "filter.color";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float brightness = (a.Track("brightness") - 100) / 100f;   // 加算
            float contrast = a.Track("contrast") / 100f;
            float gain = a.Track("luminance") / 100f;                  // 乗算
            float sat = a.Track("saturation") / 100f;
            float hue = a.Track("hue");
            var m = Identity();
            float off = 0.5f * (1 - contrast) + brightness;
            m = Concat(new float[] { contrast, 0, 0, 0, off, 0, contrast, 0, 0, off, 0, 0, contrast, 0, off, 0, 0, 0, 1, 0 }, m);
            m = Concat(new float[] { gain, 0, 0, 0, 0, 0, gain, 0, 0, 0, 0, 0, gain, 0, 0, 0, 0, 0, 1, 0 }, m);
            if (Math.Abs(sat - 1) > 1e-4) m = Concat(Saturation(sat), m);
            if (Math.Abs(hue) > 1e-4) m = Concat(HueRotate(hue), m);
            using var cf = SKColorFilter.CreateColorMatrix(m);
            using var paint = new SKPaint { ColorFilter = cf };
            return WithPaint(s, a, paint);
        }
    }

    private sealed class Blur : IVideoEffect
    {
        public string Id => "filter.blur";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float radius = a.Track("radius");
            if (radius <= 0) return s;
            float sigma = radius / 2f;
            int pad = a.Check("fixed") || a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(radius * 1.5f);
            using var f = a.IsFrameBuffer
                ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp)
                : SKImageFilter.CreateBlur(sigma, sigma);
            using var paint = new SKPaint { ImageFilter = f };
            return WithPaint(s, a, paint, pad);
        }
    }

    private sealed class Border : IVideoEffect
    {
        public string Id => "filter.border";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int size = (int)MathF.Round(a.Track("size"));
            float blur = a.Track("blur");
            if (size <= 0) return s;
            int pad = a.IsFrameBuffer ? 0 : size + (int)MathF.Ceiling(blur * 1.5f);
            using var dilate = SKImageFilter.CreateDilate(size, size);
            using var cf = SKColorFilter.CreateBlendMode(a.Color("color"), SKBlendMode.SrcIn);
            using var colored = SKImageFilter.CreateColorFilter(cf, dilate);
            using var final = blur > 0 ? SKImageFilter.CreateBlur(blur / 2, blur / 2, colored) : null;
            using var paint = new SKPaint { ImageFilter = final ?? colored };
            return Redraw(s, a, pad, pad, pad, pad, (c, x, y) =>
            {
                c.DrawImage(s.Image, x, y, paint);
                c.DrawImage(s.Image, x, y);
            });
        }
    }

    private sealed class Shadow : IVideoEffect
    {
        public string Id => "filter.shadow";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float dx = a.Track("x"), dy = a.Track("y"), spread = a.Track("spread");
            float density = Math.Clamp(a.Track("density") / 100f, 0, 1);
            int blurPad = (int)MathF.Ceiling(spread * 1.5f);
            int padL = a.IsFrameBuffer ? 0 : Math.Max(0, (int)MathF.Ceiling(-dx)) + blurPad;
            int padR = a.IsFrameBuffer ? 0 : Math.Max(0, (int)MathF.Ceiling(dx)) + blurPad;
            int padT = a.IsFrameBuffer ? 0 : Math.Max(0, (int)MathF.Ceiling(-dy)) + blurPad;
            int padB = a.IsFrameBuffer ? 0 : Math.Max(0, (int)MathF.Ceiling(dy)) + blurPad;
            var color = a.Color("color").WithAlpha((byte)(density * 255));
            using var cf = SKColorFilter.CreateBlendMode(color, SKBlendMode.SrcIn);
            using var colored = SKImageFilter.CreateColorFilter(cf);
            using var final = spread > 0 ? SKImageFilter.CreateBlur(spread / 2, spread / 2, colored) : null;
            using var paint = new SKPaint { ImageFilter = final ?? colored };
            return Redraw(s, a, padL, padT, padR, padB, (c, x, y) =>
            {
                c.DrawImage(s.Image, x + dx, y + dy, paint);
                c.DrawImage(s.Image, x, y);
            });
        }
    }

    private sealed class Clip : IVideoEffect
    {
        public string Id => "filter.clip";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int top = (int)a.Track("top"), bottom = (int)a.Track("bottom"), left = (int)a.Track("left"), right = (int)a.Track("right");
            if (top <= 0 && bottom <= 0 && left <= 0 && right <= 0) return s;
            var rect = SKRect.Create(left, top, s.Width - left - right, s.Height - top - bottom);
            // サイズは変えず、切り取った外側を透明にする
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                if (rect.Width <= 0 || rect.Height <= 0) return;
                c.ClipRect(rect);
                c.DrawImage(s.Image, x, y);
            });
        }
    }

    private sealed class Mosaic : IVideoEffect
    {
        public string Id => "filter.mosaic";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int size = Math.Max(1, (int)a.Track("size"));
            if (size <= 1) return s;
            int sw = Math.Max(1, (int)MathF.Ceiling(s.Width / (float)size)), sh = Math.Max(1, (int)MathF.Ceiling(s.Height / (float)size));
            using var small = SKSurface.Create(Info(sw, sh));
            small.Canvas.Clear(SKColors.Transparent);
            using (var p = new SKPaint { FilterQuality = SKFilterQuality.Medium })
                small.Canvas.DrawImage(s.Image, SKRect.Create(s.Width, s.Height), SKRect.Create(sw * size / (float)size, sh), p);
            using var smallImg = small.Snapshot();
            using var nearest = new SKPaint { FilterQuality = SKFilterQuality.None };
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
                c.DrawImage(smallImg, SKRect.Create(sw, sh), SKRect.Create(x, y, sw * size, sh * size), nearest));
        }
    }

    private sealed class ChromaKey : IVideoEffect
    {
        public string Id => "filter.chromakey";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            var key = a.Color("color");
            key.ToHsv(out float kh, out float ks, out _);
            float hueRange = a.Track("hue") / 256f * 180f;     // 度
            float satRange = a.Track("saturation") / 256f * 100f;
            float edge = Math.Max(0.001f, a.Track("edge") / 100f);

            var bmp = new SKBitmap(Info(s.Width, s.Height));
            if (!s.Image.ReadPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes, 0, 0)) { bmp.Dispose(); return s; }
            byte* p = (byte*)bmp.GetPixels();
            int count = s.Width * s.Height;
            for (int i = 0; i < count; i++, p += 4)
            {
                byte al = p[3];
                if (al == 0) continue;
                // 乗算済みアルファを戻してから色を調べる
                float inv = 255f / al;
                float b = Math.Min(255, p[0] * inv), g = Math.Min(255, p[1] * inv), r = Math.Min(255, p[2] * inv);
                float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                float sat = max <= 0 ? 0 : d / max * 100f;
                float hue;
                if (d <= 0) hue = 0;
                else if (max == r) hue = 60 * (((g - b) / d) % 6);
                else if (max == g) hue = 60 * ((b - r) / d + 2);
                else hue = 60 * ((r - g) / d + 4);
                if (hue < 0) hue += 360;
                float dh = Math.Abs(hue - kh); if (dh > 180) dh = 360 - dh;
                float ds = Math.Abs(sat - ks);
                // 0 = キー色そのもの, 1 以上 = 範囲外
                float dist = Math.Max(hueRange <= 0 ? (dh > 0 ? 2 : 0) : dh / hueRange, satRange <= 0 ? (ds > 0 ? 2 : 0) : ds / satRange);
                float keep = Math.Clamp((dist - 1) / edge + 1, 0, 1);
                if (keep >= 1) continue;
                p[0] = (byte)(p[0] * keep); p[1] = (byte)(p[1] * keep); p[2] = (byte)(p[2] * keep); p[3] = (byte)(al * keep);
            }
            bmp.SetImmutable();
            var img = a.Context.Own(SKImage.FromBitmap(bmp));
            a.Context.Own(bmp);
            return new Sprite(img, s.Cx, s.Cy);
        }
    }

    private sealed class Mono : IVideoEffect
    {
        public string Id => "filter.mono";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float t = Math.Clamp(a.Track("strength") / 100f, 0, 1);
            var c = a.Color("color");
            float cr = c.Red / 255f, cg = c.Green / 255f, cb = c.Blue / 255f;
            const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            float[] target = a.Check("keep")
                ? new float[] { lr * cr, lg * cr, lb * cr, 0, 0, lr * cg, lg * cg, lb * cg, 0, 0, lr * cb, lg * cb, lb * cb, 0, 0, 0, 0, 0, 1, 0 }
                : new float[] { 0, 0, 0, 0, cr, 0, 0, 0, 0, cg, 0, 0, 0, 0, cb, 0, 0, 0, 1, 0 };
            var id = Identity();
            var m = new float[20];
            for (int i = 0; i < 20; i++) m[i] = id[i] + (target[i] - id[i]) * t;
            using var cf = SKColorFilter.CreateColorMatrix(m);
            using var paint = new SKPaint { ColorFilter = cf };
            return WithPaint(s, a, paint);
        }
    }

    private sealed class Flip : IVideoEffect
    {
        public string Id => "filter.flip";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            bool v = a.Check("vertical"), h = a.Check("horizontal"), luma = a.Check("luma");
            if (!v && !h && !luma) return s;
            using var cf = luma ? SKColorFilter.CreateColorMatrix(new float[] { -1, 0, 0, 0, 1, 0, -1, 0, 0, 1, 0, 0, -1, 0, 1, 0, 0, 0, 1, 0 }) : null;
            using var paint = new SKPaint { ColorFilter = cf };
            var r = Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                c.Scale(h ? -1 : 1, v ? -1 : 1, s.Width / 2f, s.Height / 2f);
                c.DrawImage(s.Image, x, y, paint);
            });
            return new Sprite(r.Image, h ? s.Width - s.Cx : s.Cx, v ? s.Height - s.Cy : s.Cy);
        }
    }

    private sealed class Fade : IVideoEffect
    {
        public string Id => "filter.fade";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            var p = a.Context.Project;
            double t = p.FrameToSeconds(a.Context.Frame - a.Object.Start);
            double rest = p.FrameToSeconds(a.Object.End + 1 - a.Context.Frame);
            double fin = a.Track("in"), fout = a.Track("out");
            double alpha = 1;
            if (fin > 0) alpha = Math.Min(alpha, t / fin);
            if (fout > 0) alpha = Math.Min(alpha, rest / fout);
            alpha = Math.Clamp(alpha, 0, 1);
            if (alpha >= 1) return s;
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(alpha * 255)) };
            return WithPaint(s, a, paint);
        }
    }
}
