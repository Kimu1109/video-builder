using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>組み込みのフィルタ効果。</summary>
public static partial class Effects
{
    private static readonly Dictionary<string, IVideoEffect> Map = new();

    static Effects()
    {
        // プラグインの PixelBuffer.ParallelRows を、エンジンの並列化の仕組みにつなぐ
        PixelRows.ForBands = PixelOps.ForBands;
        Register(new ColorCorrect()); Register(new Blur()); Register(new Border()); Register(new Shadow());
        Register(new Clip()); Register(new Mosaic()); Register(new ChromaKey()); Register(new Mono());
        Register(new Flip()); Register(new Fade()); Register(new ScriptEffect());
        Register(new Mask()); Register(new Transition());
        RegisterMore();
        RegisterExtra();
        RegisterText();
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
        "filter.color", "filter.blur", "filter.border", "filter.shadow", "filter.clip", "filter.mosaic", "filter.chromakey", "filter.mono", "filter.flip", "filter.mask",
        "filter.glow", "filter.dirblur", "filter.radialblur", "filter.sharpen", "filter.chromatic", "filter.vignette", "filter.gradient", "filter.diagclip",
        "filter.levels", "filter.posterize",
        "filter.emit", "filter.diffuse", "filter.flash", "filter.edge", "filter.polar", "filter.mirror", "filter.kaleido", "filter.freeform",
        "filter.lensblur", "filter.deinterlace", "filter.random",
        "filter.backplate", "filter.innershadow", "filter.extrude", "filter.overlapinvert",
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
            if (pending != null) { sprite = KeepScale(ApplyMatrix(sprite, pendingArgs, pending), sprite); pending = null; }
            sprite = KeepScale(fx.Apply(sprite, args), sprite);
        }
        if (pending != null) sprite = KeepScale(ApplyMatrix(sprite, pendingArgs, pending), sprite);
        return sprite;
    }

    /// <summary>
    /// 小さく作った絵 (Scale が 1 でない) にかけた効果が倍率 1 の絵を返したら、元の倍率に戻す。
    /// 小さいままかけるのは、大きさによらない効果 (色の変換など) だけなので、倍率はそのまま引き継いでよい。
    /// </summary>
    private static Sprite KeepScale(Sprite result, Sprite input) =>
        input.Scale != 1 && result.Scale == 1 ? result with { Scale = input.Scale } : result;


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
                    using var blurredCpu = BlurOnCpu(smallCpu, ss, 0, a.IsFrameBuffer) ?? PixelOps.DrawParallel(sw, sh, c => c.DrawImage(smallCpu, 0, 0, bp));
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
            if (a.Context.Gpu == null && BlurOnCpu(s.Image, sigma, pad, a.IsFrameBuffer) is { } own)
                return new Sprite(a.Context.Own(own), s.Cx + pad, s.Cy + pad);
            using var f = a.IsFrameBuffer
                ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp)
                : SKImageFilter.CreateBlur(sigma, sigma);
            using var paint = new SKPaint { ImageFilter = f };
            return WithPaint(s, a, paint, pad);
        }
    }

    /// <summary>
    /// CPU でぼかした絵を作る (PixelBlur、複数スレッド)。pad だけ周りを広げる。clamp なら絵の外を端のピクセルとみなす。
    /// ピクセルを読めない絵 (GPU 上の絵など) なら null (呼び出し側は Skia でぼかす)。
    /// </summary>
    public static SKImage? BlurOnCpu(SKImage image, float sigma, int pad, bool clamp)
    {
        if (clamp) pad = 0;
        int w = image.Width, h = image.Height, ow = w + pad * 2, oh = h + pad * 2;
        if (ow > 16384 || oh > 16384) return null;
        using var pixmap = image.PeekPixels();
        bool direct = pixmap != null && pixmap.ColorType == SKColorType.Bgra8888 && pixmap.AlphaType is SKAlphaType.Premul or SKAlphaType.Opaque;
        (IntPtr Buffer, int RowBytes)? copy = null;
        if (!direct)
        {
            copy = CopyPixels(new Sprite(image, 0, 0));
            if (copy == null) return null;
        }
        try
        {
            var src = direct ? pixmap!.GetPixels() : copy!.Value.Buffer;
            int srcRow = direct ? pixmap!.RowBytes : copy!.Value.RowBytes;
            var info = Info(ow, oh);
            int rowBytes = ow * 4;
            var dst = PixelPool.Rent((long)rowBytes * oh);
            PixelBlur.Gaussian(src, w, h, srcRow, dst, rowBytes, sigma, pad, clamp);
            return PixelPool.ToImage(dst, info, rowBytes);
        }
        finally
        {
            if (copy is { } c) PixelPool.Return(c.Buffer, (long)c.RowBytes * h);
        }
    }

    /// <summary>
    /// CPU で縁取りを作る: 元の絵のアルファを太らせ (DilateAlpha)、ぼかし (GaussianAlpha)、縁取りの色で塗った上に元の絵を重ねる。
    /// Skia の画像フィルタ (色の付いた絵全体を太らせてぼかす) と同じ結果を、アルファの 1 チャンネルだけで計算する。
    /// ピクセルを読めない絵なら null (呼び出し側は Skia で描く)。
    /// </summary>
    public static unsafe SKImage? BorderOnCpu(SKImage image, int size, float sigma, int pad, SKColor color)
    {
        int w = image.Width, h = image.Height, ow = w + pad * 2, oh = h + pad * 2;
        if (ow > 16384 || oh > 16384) return null;
        using var pixmap = image.PeekPixels();
        bool direct = pixmap != null && pixmap.ColorType == SKColorType.Bgra8888 && pixmap.AlphaType is SKAlphaType.Premul or SKAlphaType.Opaque;
        (IntPtr Buffer, int RowBytes)? copy = null;
        if (!direct)
        {
            copy = CopyPixels(new Sprite(image, 0, 0));
            if (copy == null) return null;
        }
        long planeBytes = (long)ow * oh;
        var plane = PixelPool.Rent(planeBytes);
        var shape = PixelPool.Rent(planeBytes);
        try
        {
            byte* src = (byte*)(direct ? pixmap!.GetPixels() : copy!.Value.Buffer);
            int srcRow = direct ? pixmap!.RowBytes : copy!.Value.RowBytes;
            byte* a0 = (byte*)plane, a1 = (byte*)shape;
            // 1. 元の絵のアルファを、余白を付けた面に写す
            PixelOps.ForBands(ow, oh, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* row = a0 + (long)y * ow;
                    new Span<byte>(row, ow).Clear();
                    int sy = y - pad;
                    if (sy < 0 || sy >= h) continue;
                    byte* s = src + (long)sy * srcRow + 3;
                    for (int x = 0; x < w; x++) row[pad + x] = s[x * 4];
                }
            });
            // 2. 太らせる → 3. ぼかす
            PixelBlur.DilateAlpha(a0, ow, oh, ow, a1, ow, size);
            if (sigma > 0) PixelBlur.GaussianAlpha(a1, ow, oh, ow, a1, ow, sigma);
            // 4. 縁取りの色 (乗算済み) × 形 の上に、元の絵を重ねる
            var info = Info(ow, oh);
            int rowBytes = ow * 4;
            var outBuf = PixelPool.Rent((long)rowBytes * oh);
            byte* o = (byte*)outBuf;
            // 乗算済みの縁取りの色
            int ca = color.Alpha, pb = Div255(color.Blue * ca), pg = Div255(color.Green * ca), pr = Div255(color.Red * ca);
            PixelOps.ForBands(ow, oh, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* m = a1 + (long)y * ow;
                    uint* d = (uint*)(o + (long)y * rowBytes);
                    int sy = y - pad;
                    uint* srow = sy >= 0 && sy < h ? (uint*)(src + (long)sy * srcRow) : null;
                    for (int x = 0; x < ow; x++)
                    {
                        int mv = m[x];
                        int sx = x - pad;
                        uint sp = srow != null && sx >= 0 && sx < w ? srow[sx] : 0;
                        int sa = (int)(sp >> 24);
                        // よくある場合を先に: 元の絵が不透明 (縁取りは見えない)・縁取りも元の絵もない
                        if (sa == 255) { d[x] = sp; continue; }
                        if (mv == 0) { d[x] = sp; continue; }
                        // 縁取り (色 × 形) の上に、元の絵を重ねる
                        int inv = 255 - sa;
                        int bb = Div255(Div255(pb * mv) * inv) + (int)(sp & 0xFF);
                        int bg = Div255(Div255(pg * mv) * inv) + (int)(sp >> 8 & 0xFF);
                        int br = Div255(Div255(pr * mv) * inv) + (int)(sp >> 16 & 0xFF);
                        int ba = Div255(Div255(ca * mv) * inv) + sa;
                        d[x] = (uint)(bb | bg << 8 | br << 16 | ba << 24);
                    }
                }
            });
            return PixelPool.ToImage(outBuf, info, rowBytes);
        }
        finally
        {
            PixelPool.Return(plane, planeBytes);
            PixelPool.Return(shape, planeBytes);
            if (copy is { } c) PixelPool.Return(c.Buffer, (long)c.RowBytes * h);
        }
    }

    /// <summary>v / 255 を四捨五入した値 (0 ≤ v ≤ 65535)。割り算を使わない。</summary>
    private static int Div255(int v) { v += 128; return (v + (v >> 8)) >> 8; }

    private sealed class Border : IVideoEffect
    {
        public string Id => "filter.border";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int size = (int)MathF.Round(a.Track("size"));
            float blur = a.Track("blur");
            if (size <= 0) return s;
            int pad = a.IsFrameBuffer ? 0 : size + (int)MathF.Ceiling(blur * 1.5f);
            bool only = a.Check("only");
            if (!only && a.Context.Gpu == null && BorderOnCpu(s.Image, size, blur / 2, pad, a.Color("color")) is { } own)
                return new Sprite(a.Context.Own(own), s.Cx + pad, s.Cy + pad);
            using var dilate = SKImageFilter.CreateDilate(size, size);
            using var cf = SKColorFilter.CreateBlendMode(a.Color("color"), SKBlendMode.SrcIn);
            using var colored = SKImageFilter.CreateColorFilter(cf, dilate);
            using var final = blur > 0 ? SKImageFilter.CreateBlur(blur / 2, blur / 2, colored) : null;
            using var paint = new SKPaint { ImageFilter = final ?? colored };
            // 縁だけにするときは、元の絵の形を抜く (袋文字)
            using var cut = only ? new SKPaint { BlendMode = SKBlendMode.DstOut } : null;
            return Redraw(s, a, pad, pad, pad, pad, (c, x, y) =>
            {
                c.DrawImage(s.Image, x, y, paint);
                c.DrawImage(s.Image, x, y, cut);
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

    /// <summary>
    /// マスクの形 (絵の座標)。shape: 0 = 四角形, 1 = 円 (楕円), 2 = 角丸四角形。中心 (cx, cy)・幅・高さ・回転 (度)。
    /// </summary>
    internal static SKPath MaskPath(int shape, float cx, float cy, float width, float height, float rotation, float radius)
    {
        var path = new SKPath();
        var rect = SKRect.Create(cx - width / 2, cy - height / 2, Math.Max(0, width), Math.Max(0, height));
        switch (shape)
        {
            case 1: path.AddOval(rect); break;
            case 2:
                float r = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
                path.AddRoundRect(rect, r, r);
                break;
            default: path.AddRect(rect); break;
        }
        if (rotation != 0) path.Transform(SKMatrix.CreateRotationDegrees(rotation, cx, cy));
        return path;
    }

    /// <summary>
    /// 絵を path で切り抜く (invert なら path の外側を残す)。feather は境界のぼかしの幅 (px)。絵の大きさは変えない。
    /// </summary>
    internal static Sprite Masked(Sprite s, EffectArgs a, SKPath path, float feather, bool invert)
    {
        return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
        {
            c.DrawImage(s.Image, x, y);
            // マスクの層を DstIn (反転なら DstOut) で重ねる。層の透明な所も合成されるので、図形の外は消える
            using var layerPaint = new SKPaint { BlendMode = invert ? SKBlendMode.DstOut : SKBlendMode.DstIn };
            c.SaveLayer(layerPaint);
            using var shapePaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
            if (feather > 0) shapePaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, feather / 2);
            c.Translate(x, y);
            c.DrawPath(path, shapePaint);
            c.Restore();
        });
    }

    /// <summary>エフェクト「マスク」: オブジェクトの絵を図形で切り抜く。</summary>
    private sealed class Mask : IVideoEffect
    {
        public string Id => "filter.mask";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float w = a.Track("width"), h = a.Track("height");
            bool invert = a.Check("invert");
            using var path = MaskPath(a.Effect.GetInt("shape"), s.Cx + a.Track("x"), s.Cy + a.Track("y"), w, h, a.Track("rotation"), a.Track("radius"));
            return Masked(s, a, path, a.Track("feather"), invert);
        }
    }

    /// <summary>
    /// エフェクト「トランジション」: クリップの入り (イン) と出 (アウト) で、フェード・ワイプ・円形ワイプ・スライド・ズームをかける。
    /// フェード・スライド・ズームは描く位置 (DrawModifiers) で、ワイプはマスクで描く。動きは加減速 (smoothstep) を付ける。
    /// </summary>
    private sealed class Transition : IVideoEffect
    {
        public string Id => "filter.transition";

        public Sprite Apply(Sprite s, EffectArgs a)
        {
            var p = a.Context.Project;
            var (progress, leaving) = Phase(a);
            if (progress >= 1) return s;
            int type = a.Effect.GetInt("type");
            // アウトだけ別の種類にできる (0 = インと同じ)
            int outType = a.Effect.GetInt("outType");
            if (leaving && outType > 0) type = outType - 1;
            if (type >= EffectRegistry.PairedTransitionStart) return PairedTransition(s, a, type, progress, leaving);
            float e = (float)TransitionEase(progress, a.Effect.GetInt("ease"));
            float feather = a.Track("feather");
            int W = p.Settings.Width, H = p.Settings.Height;
            switch (type)
            {
                case 0:   // フェード
                    a.Context.Modifiers.Alpha *= e;
                    return s;
                case >= 1 and <= 4 and var wipe:
                {
                    // ワイプ: 見えている範囲の端を、決めた辺から反対の辺へ動かす。
                    // ぼかしの幅ぶん外から始めて外で終わるので、始め (e = 0) は何も見えず、終わり (e = 1) は全部見える
                    float m = feather * 2, wx = -feather + (s.Width + feather * 2) * e, hy = -feather + (s.Height + feather * 2) * e;
                    SKRect r = wipe switch
                    {
                        1 => new SKRect(-m, -m, wx, s.Height + m),                          // 左から
                        2 => new SKRect(s.Width - wx, -m, s.Width + m, s.Height + m),       // 右から
                        3 => new SKRect(-m, -m, s.Width + m, hy),                           // 上から
                        _ => new SKRect(-m, s.Height - hy, s.Width + m, s.Height + m),      // 下から
                    };
                    using var path = new SKPath();
                    path.AddRect(r);
                    return Masked(s, a, path, feather, false);
                }
                case 5:   // 円形ワイプ: 中心から円を広げる
                {
                    float radius = (MathF.Sqrt(s.Width * s.Width + s.Height * s.Height) / 2 + feather) * e;
                    using var path = MaskPath(1, s.Cx, s.Cy, radius * 2, radius * 2, 0, 0);
                    return Masked(s, a, path, feather, false);
                }
                case 6: a.Context.Modifiers.Ox -= W * (1 - e); return s;   // スライド (左から)
                case 7: a.Context.Modifiers.Ox += W * (1 - e); return s;   // スライド (右から)
                case 8: a.Context.Modifiers.Oy -= H * (1 - e); return s;   // スライド (上から)
                case 9: a.Context.Modifiers.Oy += H * (1 - e); return s;   // スライド (下から)
                case 10:  // ズーム
                    a.Context.Modifiers.Zoom *= e;
                    return s;
                case 11:  // ぼかし: ぼかしながら現れる (ぼかしの幅の 2 倍から 0 へ)
                {
                    a.Context.Modifiers.Alpha *= e;
                    float radius = Math.Max(1, feather) * 2 * (1 - e);
                    return radius < 0.5f ? s : Gaussian(s, a, radius, a.IsFrameBuffer);
                }
                case 12:  // 回転ズーム
                    a.Context.Modifiers.Zoom *= e;
                    a.Context.Modifiers.Rz -= 360 * (1 - e);
                    return s;
                case 13:  // ポップ: 少し大きくなってから戻る (加減速ではなく、行き過ぎて戻る動き)
                {
                    const double c1 = 1.70158, c3 = c1 + 1;
                    double q = progress - 1;
                    a.Context.Modifiers.Zoom *= (float)Math.Max(0, 1 + c3 * q * q * q + c1 * q * q);
                    a.Context.Modifiers.Alpha *= (float)Math.Min(1, progress * 3);
                    return s;
                }
                case 14:  // 時計回りワイプ: 真上から時計回りに扇形を広げる
                {
                    float r = MathF.Sqrt(s.Width * s.Width + s.Height * s.Height) + feather * 2;
                    using var path = new SKPath();
                    path.MoveTo(s.Cx, s.Cy);
                    path.ArcTo(SKRect.Create(s.Cx - r, s.Cy - r, r * 2, r * 2), -90, 360 * e, false);
                    path.Close();
                    return Masked(s, a, path, feather, false);
                }
                case 15 or 16:  // ブラインド (横・縦): 帯ごとに少しずつ開く
                {
                    const int Slats = 8;
                    bool horizontal = type == 15;
                    float m = feather * 2 + 1;
                    using var path = new SKPath();
                    if (horizontal)
                    {
                        float hs = s.Height / (float)Slats;
                        for (int i = 0; i < Slats; i++) path.AddRect(new SKRect(-m, i * hs, s.Width + m, i * hs + hs * e));
                    }
                    else
                    {
                        float ws = s.Width / (float)Slats;
                        for (int i = 0; i < Slats; i++) path.AddRect(new SKRect(i * ws, -m, i * ws + ws * e, s.Height + m));
                    }
                    return Masked(s, a, path, Math.Min(feather, 4), false);
                }
                case 18:  // ランダム方向から: オブジェクトごとに決まった向きの、画面の外から入る
                {
                    float ang = Rand01(0, StableSeed(a.Object), 21) * 2 * MathF.PI, dist = MathF.Sqrt(W * W + H * H);
                    a.Context.Modifiers.Ox += MathF.Cos(ang) * dist * (1 - e);
                    a.Context.Modifiers.Oy += MathF.Sin(ang) * dist * (1 - e);
                    return s;
                }
                case 19:  // 縮小して登場: 大きい所から縮んで現れる
                    a.Context.Modifiers.Zoom *= 1 + 3 * (1 - e);
                    a.Context.Modifiers.Alpha *= e;
                    return s;
                case 20:  // 落ちて登場: オブジェクトごとにランダムに遅れて、上から落ちてくる
                {
                    float delay = Rand01(0, StableSeed(a.Object), 22) * 0.6f;
                    float q = (float)Math.Clamp((progress - delay) / (1 - delay), 0, 1);
                    float k = 1 - (1 - q) * (1 - q);   // 落ちるにつれて減速
                    a.Context.Modifiers.Oy -= H * (1 - k);
                    a.Context.Modifiers.Alpha *= MathF.Min(1, q * 4);
                    return s;
                }
                case 21:  // 落ちて弾む (以前の「弾んで登場」): 上から落ちて、何度か弾んで止まる
                    a.Context.Modifiers.Oy -= H * 0.6f * (1 - BounceOut((float)progress));
                    return s;
                case 22:  // 広がって登場: 横に広がって現れる
                {
                    float sx = MathF.Max(0.001f, e);
                    int w = Math.Max(1, (int)MathF.Round(s.Width * sx));
                    return new Sprite(Resized(s, a, w, s.Height), s.Cx * w / s.Width, s.Cy, s.Scale);
                }
                case 23:  // 起き上がって登場: 下の端を支点に、奥に倒れた状態から起き上がる
                {
                    float deg = -90 * (1 - e), rad = deg * MathF.PI / 180f, half = s.Height * s.Scale / 2;
                    a.Context.Modifiers.Rx += deg;
                    a.Context.Modifiers.Oy += half * (1 - MathF.Cos(rad));
                    a.Context.Modifiers.Oz += -half * MathF.Sin(rad);
                    return s;
                }
                case 24:  // 集まって登場 (以前の「何処からともなく登場」): ランダムな位置・回転から、薄く現れながら集まる
                {
                    uint seed = StableSeed(a.Object);
                    a.Context.Modifiers.Ox += (Rand01(0, seed, 23) - 0.5f) * W * (1 - e);
                    a.Context.Modifiers.Oy += (Rand01(0, seed, 24) - 0.5f) * H * (1 - e);
                    a.Context.Modifiers.Rz += (Rand01(0, seed, 25) * 2 - 1) * 360 * (1 - e);
                    a.Context.Modifiers.Zoom *= 0.3f + 0.7f * e;
                    a.Context.Modifiers.Alpha *= e;
                    return s;
                }
                case 25:  // 点滅して登場: 点滅しながら現れる
                    if (((int)(progress * 8) & 1) == 0) a.Context.Modifiers.Alpha = 0;
                    return s;
                case 26:  // 弾んで登場: 右から、ぴょんぴょん跳ねながら入ってくる (アウトでは左へ跳ねながら出ていく)
                {
                    var q = progress;
                    float height = a.Track("height"), bounces = Math.Max(1, a.Track("bounces"));
                    float dist = W / 2f + s.Width * s.Scale / 2f;
                    a.Context.Modifiers.Ox += (leaving ? -dist : dist) * (float)(1 - q);
                    // 跳ねる高さは、着くにつれて低くなる
                    a.Context.Modifiers.Oy -= height * (float)(Math.Abs(Math.Sin(Math.PI * bounces * q)) * (1 - q * 0.6));
                    return s;
                }
                case 27:  // 何処からともなく登場: 忍者のように、あちこちへ高速で移動してから止まる (移動中はぼかす)
                {
                    var (q, _) = Phase(a);
                    const int Jumps = 4;
                    uint seed = StableSeed(a.Object);
                    float range = a.Track("range") * Math.Max(s.Width, s.Height) * s.Scale;
                    (float X, float Y) P(int k) => k >= Jumps ? (0, 0) : ((Rand01((uint)k, seed, 41) * 2 - 1) * range, (Rand01((uint)k, seed, 42) * 2 - 1) * range);
                    double f = q * Jumps;
                    int k = Math.Min(Jumps - 1, (int)f);
                    // 1 回の移動は、区間の始めの 35% ですばやく動き、残りは止まって見せる
                    float u = (float)Math.Clamp((f - k) / 0.35, 0, 1);
                    float ease = 1 - (1 - u) * (1 - u);
                    var (ax, ay) = P(k); var (bx, by) = P(k + 1);
                    a.Context.Modifiers.Ox += ax + (bx - ax) * ease;
                    a.Context.Modifiers.Oy += ay + (by - ay) * ease;
                    a.Context.Modifiers.Alpha *= (float)Math.Min(1, f * 4);   // 最初に現れる所だけ、すっと出す
                    float blur = a.Track("jumpBlur");
                    if (u >= 1 || blur <= 0) return s;
                    // 動いている向きにぼかす (速いほど長く)
                    float mx = (bx - ax) / Math.Max(0.01f, s.Scale), my = (by - ay) / Math.Max(0.01f, s.Scale);
                    float len = MathF.Min(MathF.Sqrt(mx * mx + my * my) * 0.35f * blur * (1 - u), 2000);
                    if (len < 1.5f) return s;
                    int padX = (int)MathF.Ceiling(MathF.Abs(mx) / MathF.Max(1, MathF.Sqrt(mx * mx + my * my)) * len / 2) + 1;
                    int padY = (int)MathF.Ceiling(MathF.Abs(my) / MathF.Max(1, MathF.Sqrt(mx * mx + my * my)) * len / 2) + 1;
                    return Resample(s, a, padX, padY, (src, tmp, w, h, rb) => PixelFx.DirectionalBlur(src, tmp, w, h, rb, mx, my, len, false, a.IsDraft ? 8 : 10)) ?? s;
                }
                default:  // ひし形ワイプ: 中心からひし形を広げる
                {
                    float r = (s.Width + s.Height) / 2f * 1.05f * e + feather * 2 * e;
                    using var path = new SKPath();
                    path.MoveTo(s.Cx, s.Cy - r); path.LineTo(s.Cx + r, s.Cy); path.LineTo(s.Cx, s.Cy + r); path.LineTo(s.Cx - r, s.Cy);
                    path.Close();
                    return Masked(s, a, path, feather, false);
                }
            }
        }
    }

    /// <summary>
    /// トランジションの進み具合 (0〜1、1 で効果なし) と、出ていく (アウト) 側か。入りと出が重なるときは、進みの小さい方。
    /// </summary>
    private static (double Progress, bool Leaving) Phase(EffectArgs a)
    {
        var p = a.Context.Project;
        double t = p.FrameToSeconds(a.Context.Frame - a.Object.Start);
        double rest = p.FrameToSeconds(a.Object.End + 1 - a.Context.Frame);
        double fin = a.Track("in"), fout = a.Track("out");
        double qi = fin > 0 ? Math.Clamp(t / fin, 0, 1) : 1, qo = fout > 0 ? Math.Clamp(rest / fout, 0, 1) : 1;
        return qo < qi ? (qo, true) : (qi, false);
    }

    /// <summary>弾む動き (0〜1 → 0〜1。最後に 3 回弾んで止まる)。</summary>
    internal static float BounceOut(float t)
    {
        const float n1 = 7.5625f, d1 = 2.75f;
        if (t < 1 / d1) return n1 * t * t;
        if (t < 2 / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
        if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
        t -= 2.625f / d1;
        return n1 * t * t + 0.984375f;
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
