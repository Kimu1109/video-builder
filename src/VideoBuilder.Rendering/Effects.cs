using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>組み込みのフィルタ効果。</summary>
public static class Effects
{
    private static readonly Dictionary<string, IVideoEffect> Map = new();

    static Effects()
    {
        // プラグインの PixelBuffer.ParallelRows を、エンジンの並列化の仕組みにつなぐ
        PixelRows.ForBands = PixelOps.ForBands;
        Register(new ColorCorrect()); Register(new Blur()); Register(new Border()); Register(new Shadow());
        Register(new Clip()); Register(new Mosaic()); Register(new ChromaKey()); Register(new Mono());
        Register(new Flip()); Register(new Fade()); Register(new ScriptEffect());
    }

    private static readonly Dictionary<string, VideoBuilder.Plugins.IVideoSource> Sources = new();
    public static void RegisterSource(string definitionId, VideoBuilder.Plugins.IVideoSource source) { lock (Sources) Sources[definitionId] = source; }
    public static VideoBuilder.Plugins.IVideoSource? FindSource(string id) { lock (Sources) return Sources.GetValueOrDefault(id); }

    public static void Register(IVideoEffect effect) { lock (Map) Map[effect.Id] = effect; }
    public static IVideoEffect? Find(string id) { lock (Map) return Map.TryGetValue(id, out var e) ? e : null; }

    /// <summary>ピクセルを直接書き換える効果を登録する (IVideoEffect としても使えるように包む)。</summary>
    public static void RegisterPixel(IPixelEffect effect) => Register(new PixelAdapter(effect));

    /// <summary>色の変換だけを行う効果を登録する。</summary>
    public static void RegisterMatrix(IColorMatrixEffect effect) => Register(new MatrixAdapter(effect));

    // 組み込みの効果のうち、結果が入力とパラメータだけで決まるもの (結果を使い回してよい)
    private static readonly HashSet<string> BuiltInDeterministic = new()
    {
        "filter.color", "filter.blur", "filter.border", "filter.shadow", "filter.clip", "filter.mosaic", "filter.chromakey", "filter.mono", "filter.flip",
    };

    /// <summary>結果を使い回してよい効果か (EffectFlags.Deterministic)。</summary>
    public static bool IsDeterministic(string id, IVideoEffect? fx) =>
        BuiltInDeterministic.Contains(id) || (fx != null && (fx.Flags & EffectFlags.Deterministic) != 0);

    /// <summary>IPixelEffect を、絵を受け取って絵を返す形 (IVideoEffect) でも使えるようにする。</summary>
    private sealed class PixelAdapter(IPixelEffect inner) : IVideoEffect, IPixelEffect
    {
        public string Id => inner.Id;
        public EffectFlags Flags => inner.Flags;
        public void Process(PixelBuffer pixels, EffectArgs args) => inner.Process(pixels, args);
        public Sprite Apply(Sprite s, EffectArgs a) =>
            WithPixels(s, a, (px, w, h, rb) => inner.Process(new PixelBuffer(px, w, h, rb), a), alsoOnGpu: true) ?? s;
    }

    private sealed class MatrixAdapter(IColorMatrixEffect inner) : IVideoEffect, IColorMatrixEffect
    {
        public string Id => inner.Id;
        public EffectFlags Flags => inner.Flags;
        public float[]? GetMatrix(EffectArgs args) => inner.GetMatrix(args);
        public Sprite Apply(Sprite s, EffectArgs a) => inner.GetMatrix(a) is { } m ? ApplyMatrix(s, a, m) : s;
    }

    /// <summary>
    /// effects[from..to) を順にかける (Filter 以外・無効なものは飛ばす)。
    /// 続けて並んだ色の変換 (IColorMatrixEffect) は、行列を掛け合わせて 1 回で処理する。
    /// </summary>
    public static Sprite ApplyRange(Sprite sprite, TimelineObject obj, int[] bounds, RenderContext ctx, int from, int to)
    {
        float[]? pending = null;
        EffectArgs pendingArgs = default;
        for (int i = from; i < to && i < obj.Effects.Count; i++)
        {
            var e = obj.Effects[i];
            if (!e.Enabled || e.Definition.Category != EffectCategory.Filter) continue;
            var fx = Find(e.DefinitionId);
            if (fx == null) continue;
            var args = new EffectArgs { Effect = e, Object = obj, Bounds = bounds, Context = ctx };
            if (fx is IColorMatrixEffect cm)
            {
                var m = cm.GetMatrix(args);
                if (m == null) continue;
                pending = pending == null ? m : Concat(m, pending);
                pendingArgs = args;
                continue;
            }
            if (pending != null) { sprite = ApplyMatrix(sprite, pendingArgs, pending); pending = null; }
            sprite = fx.Apply(sprite, args);
        }
        if (pending != null) sprite = ApplyMatrix(sprite, pendingArgs, pending);
        return sprite;
    }


    internal static SKImageInfo Info(int w, int h) => new(Math.Max(1, w), Math.Max(1, h), SKColorType.Bgra8888, SKAlphaType.Premul);

    /// <summary>余白を足した新しい画像に描き直す。draw には元画像を置く位置 (左上) が渡される。</summary>
    internal static Sprite Redraw(Sprite s, EffectArgs a, int padL, int padT, int padR, int padB, Action<SKCanvas, float, float> draw)
    {
        // 極端なサイズでメモリを使い切らないようにする
        int w = Math.Min(s.Width + padL + padR, 16384), h = Math.Min(s.Height + padT + padB, 16384);
        if (a.Context.Gpu == null)
        {
            // CPU 描画: 横帯に分けて複数スレッドで描く
            var parallel = a.Context.Own(PixelOps.DrawParallel(w, h, c => draw(c, padL, padT)));
            return new Sprite(parallel, s.Cx + padL, s.Cy + padT);
        }
        using var surface = a.Context.CreateSurface(w, h);
        if (surface == null) return s;
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas, padL, padT);
        var img = a.Context.Own(surface.Snapshot());
        return new Sprite(img, s.Cx + padL, s.Cy + padT);
    }

    internal static Sprite WithPaint(Sprite s, EffectArgs a, SKPaint paint, int pad = 0) =>
        Redraw(s, a, pad, pad, pad, pad, (c, x, y) => c.DrawImage(s.Image, x, y, paint));

    /// <summary>
    /// CPU 描画のとき: 画像を複製して、ピクセルを直接書き換える (PixelOps は複数スレッドで動く)。
    /// GPU 描画中や形式が合わないときは null を返すので、呼び出し側は Skia の描画で処理する。
    /// </summary>
    /// <param name="alsoOnGpu">GPU 描画中でも、画像を読み戻してこの処理を使う (CPU と同じ絵にしたい効果用)。</param>
    internal static Sprite? WithPixels(Sprite s, EffectArgs a, Action<IntPtr, int, int, int> op, bool alsoOnGpu = false)
    {
        if (a.Context.Gpu != null && !alsoOnGpu) return null;
        if (CopyPixels(s) is not { } copy) return null;
        op(copy.Buffer, s.Width, s.Height, copy.RowBytes);
        var img = a.Context.Own(PixelPool.ToImage(copy.Buffer, Info(s.Width, s.Height), copy.RowBytes));
        return new Sprite(img, s.Cx, s.Cy);
    }

    /// <summary>画像の中身を、使い回しのバッファ (BGRA) に写す。失敗したら null。</summary>
    internal static (IntPtr Buffer, int RowBytes)? CopyPixels(Sprite s)
    {
        var info = Info(s.Width, s.Height);
        int rowBytes = info.Width * 4;
        long bytes = (long)rowBytes * info.Height;
        var buffer = PixelPool.Rent(bytes);
        if (s.Image.ReadPixels(info, buffer, rowBytes, 0, 0)) return (buffer, rowBytes);
        PixelPool.Return(buffer, bytes);
        return null;
    }

    /// <summary>カラーマトリクスをかける。CPU 描画では PixelOps、GPU 描画では Skia のカラーフィルタを使う。</summary>
    internal static Sprite ApplyMatrix(Sprite s, EffectArgs a, float[] m)
    {
        if (PixelOps.CanApply(m) && WithPixels(s, a, (px, w, h, rb) => PixelOps.ColorMatrix(px, w, h, rb, m)) is { } fast) return fast;
        using var cf = SKColorFilter.CreateColorMatrix(m);
        using var paint = new SKPaint { ColorFilter = cf };
        return WithPaint(s, a, paint);
    }

    /// <summary>ぼかしを何分の 1 の大きさで計算するか。大きくぼかすときは縮小してから計算しても見分けがつかない。</summary>
    internal static int BlurDownscale(float sigma)
    {
        int k = 1;
        // 半分への縮小は、ぼかしの強さ (sigma) が 3 以上あれば見分けがつかない。
        // それ以上の縮小は、縮小後の強さ (sigma / k) が 3 以上残る範囲に限る (粗くしすぎると差が見えてくる)
        if (sigma >= 3f) k = 2;
        while (k < 8 && sigma / (k * 2) >= 3f) k *= 2;
        return k;
    }

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

    private sealed class ColorCorrect : IVideoEffect, IColorMatrixEffect
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
            return ApplyMatrix(s, a, m);
        }

        public float[]? GetMatrix(EffectArgs a)
        {
            float brightness = (a.Track("brightness") - 100) / 100f, contrast = a.Track("contrast") / 100f;
            float gain = a.Track("luminance") / 100f, sat = a.Track("saturation") / 100f, hue = a.Track("hue");
            var m = Identity();
            float off = 0.5f * (1 - contrast) + brightness;
            m = Concat(new float[] { contrast, 0, 0, 0, off, 0, contrast, 0, 0, off, 0, 0, contrast, 0, off, 0, 0, 0, 1, 0 }, m);
            m = Concat(new float[] { gain, 0, 0, 0, 0, 0, gain, 0, 0, 0, 0, 0, gain, 0, 0, 0, 0, 0, 1, 0 }, m);
            if (Math.Abs(sat - 1) > 1e-4) m = Concat(Saturation(sat), m);
            if (Math.Abs(hue) > 1e-4) m = Concat(HueRotate(hue), m);
            return m;
        }
    }

    private sealed class Blur : IVideoEffect
    {
        public string Id => "filter.blur";
        public Sprite Apply(Sprite s, EffectArgs a) => Gaussian(s, a, a.Track("radius"), a.Check("fixed") || a.IsFrameBuffer);
    }

    /// <summary>ぼかす (半径はピクセル)。keepSize なら大きさを変えない (はみ出すぶんは捨てる)。</summary>
    internal static Sprite GaussianBlur(Sprite s, RenderContext ctx, float radius, bool keepSize = false) =>
        Gaussian(s, new EffectArgs { Context = ctx }, radius, keepSize);

    private static Sprite Gaussian(Sprite s, EffectArgs a, float radius, bool keepSize)
    {
        {
            if (radius <= 0) return s;
            float sigma = radius / 2f;
            int pad = keepSize ? 0 : (int)MathF.Ceiling(radius * 1.5f);
            int k = BlurDownscale(sigma);
            if (k > 1)
            {
                // 縮小 → ぼかし → 拡大。計算するピクセル数が 1/k² になる
                int pw = s.Width + pad * 2, ph = s.Height + pad * 2;
                int sw = Math.Max(1, (pw + k - 1) / k), sh = Math.Max(1, (ph + k - 1) / k);
                float ss = sigma / k;
                using var sf = a.IsFrameBuffer ? SKImageFilter.CreateBlur(ss, ss, SKShaderTileMode.Clamp) : SKImageFilter.CreateBlur(ss, ss);
                using var down = new SKPaint { FilterQuality = SKFilterQuality.Medium };
                using var bp = new SKPaint { ImageFilter = sf };
                if (a.Context.Gpu == null)
                {
                    // 1. 縮小  2. 縮小した絵をぼかす  3. 元の大きさに戻す (どれも帯に分けて並列に描く)
                    // 半分ずつ縮める (ミップマップを作らせない)。余白は最初の 1 回で付ける
                    var smallCpu = PixelOps.Halve(s.Image, pw, ph, pad, pad);
                    for (int step = 2, cw = (pw + 1) / 2, ch = (ph + 1) / 2; step < k; step *= 2, cw = (cw + 1) / 2, ch = (ch + 1) / 2)
                    {
                        var next = PixelOps.Halve(smallCpu, cw, ch);
                        smallCpu.Dispose();
                        smallCpu = next;
                    }
                    using var smallOwner = smallCpu;
                    sw = smallCpu.Width; sh = smallCpu.Height;
                    using var blurredCpu = PixelOps.DrawParallel(sw, sh, c => c.DrawImage(smallCpu, 0, 0, bp));
                    using var upCpu = new SKPaint { FilterQuality = SKFilterQuality.Low, BlendMode = SKBlendMode.Src };
                    return Redraw(s, a, pad, pad, pad, pad, (c, _, _) => c.DrawImage(blurredCpu, SKRect.Create(sw, sh), SKRect.Create(0, 0, sw * k, sh * k), upCpu));
                }
                using var small = a.Context.CreateSurface(sw, sh);
                using var blurred = a.Context.CreateSurface(sw, sh);
                if (small != null && blurred != null)
                {
                    var sc = small.Canvas;
                    sc.Clear(SKColors.Transparent);
                    sc.Scale(1f / k);
                    sc.DrawImage(s.Image, pad, pad, down);
                    using var smallImage = small.Snapshot();
                    blurred.Canvas.Clear(SKColors.Transparent);
                    blurred.Canvas.DrawImage(smallImage, 0, 0, bp);
                    using var blurredImage = blurred.Snapshot();
                    // 3. 元の大きさに戻す
                    using var up = new SKPaint { FilterQuality = SKFilterQuality.Low, BlendMode = SKBlendMode.Src };
                    return Redraw(s, a, pad, pad, pad, pad, (c, _, _) => c.DrawImage(blurredImage, SKRect.Create(sw, sh), SKRect.Create(0, 0, sw * k, sh * k), up));
                }
            }
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

    private sealed class Mosaic : IVideoEffect, IPixelEffect
    {
        public string Id => "filter.mosaic";

        public void Process(PixelBuffer b, EffectArgs a) =>
            PixelOps.Mosaic(b.Pointer, b.Width, b.Height, b.RowBytes, Math.Max(1, (int)a.Track("size")));

        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int size = Math.Max(1, (int)a.Track("size"));
            if (size <= 1) return s;
            // ピクセルを直接平均する。CPU 描画では縮小と拡大を 2 回描くより速く、マスの色も正確になる。
            // GPU 描画でも同じ絵になるよう、読み戻してこの処理を使う
            if (WithPixels(s, a, (px, w, h, rb) => PixelOps.Mosaic(px, w, h, rb, size), alsoOnGpu: true) is { } fast) return fast;
            int sw = Math.Max(1, (int)MathF.Ceiling(s.Width / (float)size)), sh = Math.Max(1, (int)MathF.Ceiling(s.Height / (float)size));
            using var small = a.Context.CreateSurface(sw, sh);
            if (small == null) return s;
            small.Canvas.Clear(SKColors.Transparent);
            using (var p = new SKPaint { FilterQuality = SKFilterQuality.Medium })
                small.Canvas.DrawImage(s.Image, SKRect.Create(s.Width, s.Height), SKRect.Create(sw * size / (float)size, sh), p);
            using var smallImg = small.Snapshot();
            using var nearest = new SKPaint { FilterQuality = SKFilterQuality.None };
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
                c.DrawImage(smallImg, SKRect.Create(sw, sh), SKRect.Create(x, y, sw * size, sh * size), nearest));
        }
    }

    private sealed class ChromaKey : IVideoEffect, IPixelEffect
    {
        public string Id => "filter.chromakey";

        public void Process(PixelBuffer b, EffectArgs a)
        {
            var key = a.Color("color");
            key.ToHsv(out float kh, out float ks, out _);
            float hueRange = a.Track("hue") / 256f * 180f, satRange = a.Track("saturation") / 256f * 100f, edge = Math.Max(0.001f, a.Track("edge") / 100f);
            if (a.IsDraft) PixelOps.ChromaKeyFast(b.Pointer, b.Width, b.Height, b.RowBytes, kh, ks, hueRange, satRange, edge);
            else PixelOps.ChromaKey(b.Pointer, b.Width, b.Height, b.RowBytes, kh, ks, hueRange, satRange, edge);
        }

        // SkSL シェーダ (SKRuntimeEffect) での実装は、SkiaSharp 2.88 では描画時にネイティブ側で
        // クラッシュするため使わない。GPU 描画中でも、この効果だけは画像を読み戻して CPU で処理する。
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            var key = a.Color("color");
            key.ToHsv(out float kh, out float ks, out _);
            float hueRange = a.Track("hue") / 256f * 180f;     // 度
            float satRange = a.Track("saturation") / 256f * 100f;
            float edge = Math.Max(0.001f, a.Track("edge") / 100f);

            if (CopyPixels(s) is not { } copy) return s;
            if (a.Context.Draft) PixelOps.ChromaKeyFast(copy.Buffer, s.Width, s.Height, copy.RowBytes, kh, ks, hueRange, satRange, edge);
            else PixelOps.ChromaKey(copy.Buffer, s.Width, s.Height, copy.RowBytes, kh, ks, hueRange, satRange, edge);
            var img = a.Context.Own(PixelPool.ToImage(copy.Buffer, Info(s.Width, s.Height), copy.RowBytes));
            return new Sprite(img, s.Cx, s.Cy);
        }
    }

    private sealed class Mono : IVideoEffect, IColorMatrixEffect
    {
        public string Id => "filter.mono";
        public float[]? GetMatrix(EffectArgs a)
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
            return m;
        }

        public Sprite Apply(Sprite s, EffectArgs a) => ApplyMatrix(s, a, GetMatrix(a)!);
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
