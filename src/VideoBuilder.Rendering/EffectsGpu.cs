using SkiaSharp;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>
/// GPU 描画のときに、SkSL のシェーダー (SKRuntimeEffect) で処理する効果 (設計書 22.54)。
/// <para>
/// SkiaSharp 2.88 では SKRuntimeEffect が描画時に落ちたので、ピクセルを直接扱う効果は GPU 描画中も
/// 絵を読み戻して CPU で処理していた (GPU が描き終えるのを待ち、写して、また GPU に送る)。
/// SkiaSharp 3 では GPU で動くので、読み戻さずに GPU のまま処理する。
/// SkSL は CPU 描画では高速化されておらず遅い (設計書 22.53) ので、CPU 描画ではこれまでどおり C# の処理を使う。
/// </para>
/// 式は CPU の処理 (PixelOps・PixelFx) と同じにする (CPU 描画と同じ絵になるように)。
/// </summary>
public static partial class Effects
{
    /// <summary>テスト・計測用: SkSL を使わず、GPU 描画中も読み戻して CPU で処理する (以前の描き方)。</summary>
    internal static bool GpuShadersDisabled { get; set; }

    /// <summary>SkSL をコンパイルして使い回す。コンパイルできなければ null (そのときは CPU の処理を使う)。SKRuntimeEffect はスレッド間で共有してよい。</summary>
    private sealed class Sksl(string source)
    {
        private readonly Lazy<SKRuntimeEffect?> _effect = new(() =>
        {
            var e = SKRuntimeEffect.CreateShader(source, out var error);
            if (e == null) Console.Error.WriteLine("SkSL をコンパイルできません: " + error);
            return e;
        });
        public SKRuntimeEffect? Effect => _effect.Value;
    }

    /// <summary>
    /// GPU 描画のとき、s を effect で描き直した絵を返す。effect の子 "image" に s の絵を渡す (同じ座標で読める)。
    /// uniforms で数値を、children で "image" 以外の子を設定する。GPU 描画でない・使えないときは null。
    /// </summary>
    private static Sprite? ApplyGpuShader(Sprite s, EffectArgs a, Sksl sksl, Action<SKRuntimeEffectUniforms> uniforms,
        Action<SKRuntimeEffectChildren, float, float>? children = null)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || sksl.Effect is not { } effect) return null;
        return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
        {
            // 補間なし (最近傍) で、画素の中心をそのまま読む
            using var image = s.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateTranslation(x, y));
            var kids = new SKRuntimeEffectChildren(effect) { ["image"] = image };
            children?.Invoke(kids, x, y);
            var u = new SKRuntimeEffectUniforms(effect);
            uniforms(u);
            using var shader = effect.ToShader(u, kids);
            using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
            c.DrawRect(SKRect.Create(x, y, s.Width, s.Height), paint);
        });
    }

    // ---------------- クロマキー ----------------

    private static readonly Sksl ChromaKeyShader = new("""
        uniform shader image;
        uniform float keyHue;    // 度
        uniform float keySat;    // 0〜100
        uniform float hueRange;  // 度
        uniform float satRange;  // 0〜100
        uniform float edge;

        half4 main(float2 p) {
            half4 c = image.eval(p);
            if (c.a <= 0) return c;
            // 乗算済みアルファを戻してから色を調べる (0〜255。PixelOps.ChromaKey と同じ式)
            float3 rgb = min(float3(c.rgb) / float(c.a), 1.0) * 255.0;
            float r = rgb.r, g = rgb.g, b = rgb.b;
            float mx = max(r, max(g, b)), mn = min(r, min(g, b)), d = mx - mn;
            float sat = mx <= 0 ? 0 : d / mx * 100;
            float ds = abs(sat - keySat);
            if (satRange > 0 ? ds >= satRange : ds > 0) return c;
            float hue;
            if (d <= 0) hue = 0;
            else if (mx == r) hue = 60 * ((g - b) / d);
            else if (mx == g) hue = 60 * ((b - r) / d + 2);
            else hue = 60 * ((r - g) / d + 4);
            if (hue < 0) hue += 360;
            float dh = abs(hue - keyHue);
            if (dh > 180) dh = 360 - dh;
            // 0 = キー色そのもの, 1 以上 = 範囲外
            float dist = max(hueRange <= 0 ? (dh > 0 ? 2 : 0) : dh / hueRange, satRange <= 0 ? (ds > 0 ? 2 : 0) : ds / satRange);
            float keep = clamp((dist - 1) / edge + 1, 0, 1);
            return c * half(keep);
        }
        """);

    /// <summary>クロマキーを GPU で。GPU 描画でなければ null。</summary>
    private static Sprite? ChromaKeyOnGpu(Sprite s, EffectArgs a, float keyHue, float keySat, float hueRange, float satRange, float edge) =>
        ApplyGpuShader(s, a, ChromaKeyShader, u =>
        {
            u["keyHue"] = keyHue; u["keySat"] = keySat; u["hueRange"] = hueRange; u["satRange"] = satRange; u["edge"] = edge;
        });

    // ---------------- 明るい所だけを残す (光彩・発光・閃光) ----------------

    private static readonly Sksl BrightPassShader = new("""
        uniform shader image;
        uniform float threshold;   // 0〜255

        half4 main(float2 p) {
            half4 c = image.eval(p);
            if (c.a <= 0) return c;
            // PixelFx.BrightPass と同じ: 明るさ (0〜255 の整数) を求め、アルファで割って元の明るさにする
            float a = floor(float(c.a) * 255 + 0.5);
            float3 v = floor(float3(c.rgb) * 255 + 0.5);
            float lum = floor((v.r * 54 + v.g * 183 + v.b * 19) / 256);
            if (a < 255) lum = min(255, floor(lum * 255 / a));
            float t = clamp((lum - (threshold - 16)) / 32, 0, 1);
            float keep = floor(t * t * (3 - 2 * t) * 256 + 0.5) / 256;
            return c * half(keep);
        }
        """);

    /// <summary>明るい所だけを残す (しきい値 0〜255)。GPU 描画なら SkSL、CPU 描画なら PixelFx.BrightPass。</summary>
    internal static Sprite BrightPass(Sprite s, EffectArgs a, float threshold)
    {
        if (threshold <= 0) return s;
        return ApplyGpuShader(s, a, BrightPassShader, u => u["threshold"] = threshold)
               ?? WithPixels(s, a, (px, w, h, rb) => PixelFx.BrightPass(px, w, h, rb, threshold), alsoOnGpu: true)
               ?? s;
    }

    // ---------------- シャープ (アンシャープマスク) ----------------

    private static readonly Sksl UnsharpShader = new("""
        uniform shader image;
        uniform shader blurred;
        uniform float amount;   // PixelFx.Unsharp と同じく 1/256 刻み

        half4 main(float2 p) {
            half4 c = image.eval(p);
            if (c.a <= 0) return c;
            half4 q = blurred.eval(p);
            // ぼかした絵との差を強める (乗算済みのまま。0〜アルファに収める)
            half3 v = c.rgb + (c.rgb - q.rgb) * half(amount);
            return half4(clamp(v, 0, c.a), c.a);
        }
        """);

    /// <summary>シャープを GPU で (ぼかしは Skia の GPU のぼかし)。GPU 描画でなければ null。</summary>
    private static Sprite? SharpenOnGpu(Sprite s, EffectArgs a, float amount, float radius)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || UnsharpShader.Effect == null) return null;
        // ぼかした絵 (同じ大きさ。端は延ばす。CPU の BlurOnCpu(clamp: true) と同じ扱い)
        using var surface = a.Context.CreateSurface(s.Width, s.Height);
        if (surface == null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        using (var blur = SKImageFilter.CreateBlur(radius / 2, radius / 2, SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { ImageFilter = blur })
            surface.Canvas.DrawImage(s.Image, 0, 0, paint);
        using var blurred = surface.Snapshot();
        // ApplyGpuShader は余白なしで描くので、絵は (0, 0) にある
        using var blurredShader = blurred.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        float k = MathF.Round(amount * 256) / 256;
        return ApplyGpuShader(s, a, UnsharpShader, u => u["amount"] = k, (kids, _, _) => kids["blurred"] = blurredShader);
    }

    // ---------------- 画素を動かす効果 (方向ブラー・放射ブラー) ----------------

    /// <summary>
    /// PixelFx.Sample と同じ読み方 (双線形補間。重みは 8 ビット、色は 0〜255 の整数) の SkSL。座標はピクセルの中心が整数。
    /// 絵の外は、子のシェーダーのタイル (Decal なら透明、Clamp なら端のピクセル) で決まる。
    /// </summary>
    private const string SampleSksl = """
        uniform shader image;

        float4 fetch(float2 q) { return floor(float4(image.eval(q + 0.5)) * 255 + 0.5); }
        float4 lerp8(float4 a, float4 b, float t) { return floor((a * (256 - t) + b * t + 128) / 256); }
        float4 sample8(float2 q) {
            float2 f = floor(q);
            float2 t = floor((q - f) * 256);
            float4 p00 = fetch(f), p10 = fetch(f + float2(1, 0));
            float4 top = t.x == 0 ? p00 : lerp8(p00, p10, t.x);
            if (t.y == 0) return top;
            float4 p01 = fetch(f + float2(0, 1)), p11 = fetch(f + float2(1, 1));
            return lerp8(top, t.x == 0 ? p01 : lerp8(p01, p11, t.x), t.y);
        }
        // PixelFx.Average と同じ: 2 つの平均。up = 1 なら 0.5 を切り上げ、0 なら切り捨て
        half4 average(float4 a, float4 b, float up) { return half4(floor((a + b + up) / 2) / 255); }
        """;

    private static readonly Sksl DirectionalPassShader = new(SampleSksl + """
        uniform float2 off;   // ずらす量 (±)
        uniform float up;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            return average(sample8(q - off), sample8(q + off), up);
        }
        """);

    private static readonly Sksl RadialPassShader = new(SampleSksl + """
        uniform float2 center;
        uniform float k1;   // 中心からの倍率 (縮める側)
        uniform float k2;   // 中心からの倍率 (広げる側)
        uniform float up;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            return average(sample8(center + (q - center) * k1), sample8(center + (q - center) * k2), up);
        }
        """);

    /// <summary>
    /// GPU で、s に余白 (padX, padY) を付けた絵に passes を順にかける。各回は前の回の絵を子 "image" として読み、同じ大きさに描く。
    /// 結果は Resample と同じ位置 (余白の分だけ中心をずらす)。GPU 描画でない・使えないときは null。
    /// </summary>
    private static Sprite? MultiPassOnGpu(Sprite s, EffectArgs a, int padX, int padY, Sksl sksl, bool clamp, int passes, Action<SKRuntimeEffectUniforms, int> uniforms)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || sksl.Effect is not { } effect) return null;
        padX = Math.Max(0, padX); padY = Math.Max(0, padY);
        int w = s.Width + padX * 2, h = s.Height + padY * 2;
        if (w > 16384 || h > 16384) { padX = padY = 0; w = s.Width; h = s.Height; }
        // 余白を付けた絵 (Resample と同じ。余白は透明)
        SKImage? current;
        using (var first = a.Context.CreateSurface(w, h))
        {
            if (first == null) return null;
            first.Canvas.Clear(SKColors.Transparent);
            first.Canvas.DrawImage(s.Image, padX, padY);
            current = first.Snapshot();
        }
        var tile = clamp ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;
        try
        {
            for (int i = 0; i < passes; i++)
            {
                using var surface = a.Context.CreateSurface(w, h);
                if (surface == null) return null;
                using var image = current.ToShader(tile, tile);
                var kids = new SKRuntimeEffectChildren(effect) { ["image"] = image };
                var u = new SKRuntimeEffectUniforms(effect);
                uniforms(u, i);
                using var shader = effect.ToShader(u, kids);
                using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
                surface.Canvas.DrawRect(SKRect.Create(w, h), paint);
                var next = surface.Snapshot();
                current.Dispose();
                current = next;
            }
            var result = new Sprite(a.Context.Own(current), s.Cx + padX, s.Cy + padY);
            current = null;
            return result;
        }
        finally { current?.Dispose(); }
    }

    /// <summary>
    /// 方向ブラー (PixelFx.DirectionalBlur と同じ) をかけた絵。座標は余白 (padX, padY) を付けた絵のもの。
    /// GPU 描画なら SkSL (読み戻さない)、CPU 描画なら PixelFx。読めなければ null。
    /// </summary>
    internal static Sprite? DirectionalBlurSprite(Sprite s, EffectArgs a, int padX, int padY, float dx, float dy, float length, bool clamp, int maxPasses = 10)
    {
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (length >= 1 && len > 0)
        {
            float ux = dx / len, uy = dy / len;
            int passes = Math.Clamp((int)MathF.Ceiling(MathF.Log2(length)), 1, maxPasses);
            var gpu = MultiPassOnGpu(s, a, padX, padY, DirectionalPassShader, clamp, passes, (u, i) =>
            {
                float d = length / 2 / MathF.Pow(2, i);
                u["off"] = new[] { ux * d / 2, uy * d / 2 };
                u["up"] = (i & 1) == 0 ? 1f : 0f;
            });
            if (gpu != null) return gpu;
        }
        return Resample(s, a, padX, padY, (src, tmp, w, h, rb) => PixelFx.DirectionalBlur(src, tmp, w, h, rb, dx, dy, length, clamp, maxPasses));
    }

    /// <summary>
    /// 放射ブラー (PixelFx.RadialBlur と同じ) をかけた絵。中心 (cx, cy) は余白 (padX, padY) を付けた絵の座標。
    /// GPU 描画なら SkSL (読み戻さない)、CPU 描画なら PixelFx。読めなければ null。
    /// </summary>
    internal static Sprite? RadialBlurSprite(Sprite s, EffectArgs a, int padX, int padY, float cx, float cy, float range, bool clamp, int maxPasses = 10)
    {
        if (range > 0)
        {
            int w = s.Width + Math.Max(0, padX) * 2, h = s.Height + Math.Max(0, padY) * 2;
            float lg = MathF.Log(1 + range);
            float far = MathF.Sqrt(MathF.Max(cx * cx, (w - cx) * (w - cx)) + MathF.Max(cy * cy, (h - cy) * (h - cy)));
            float pixels = far * lg;
            if (pixels >= 1 && w <= 16384 && h <= 16384)
            {
                int passes = Math.Clamp((int)MathF.Ceiling(MathF.Log2(pixels)), 1, maxPasses);
                var gpu = MultiPassOnGpu(s, a, padX, padY, RadialPassShader, clamp, passes, (u, i) =>
                {
                    float d = lg / 2 / MathF.Pow(2, i);
                    u["center"] = new[] { cx, cy };
                    u["k1"] = MathF.Exp(-d / 2);
                    u["k2"] = MathF.Exp(d / 2);
                    u["up"] = (i & 1) == 0 ? 1f : 0f;
                });
                if (gpu != null) return gpu;
            }
        }
        return Resample(s, a, padX, padY, (src, tmp, w, h, rb) => PixelFx.RadialBlur(src, tmp, w, h, rb, cx, cy, range, clamp, maxPasses));
    }
}
