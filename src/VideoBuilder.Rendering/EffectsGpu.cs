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
            float k = floor(t * t * (3 - 2 * t) * 256 + 0.5);
            if (k >= 256) return c;
            // PixelFx.Lerp(0, c, k) と同じ丸め
            return half4(floor((float4(c) * 255 * k + 128) / 256) / 255);
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
        // PixelFx.Sample と同じ (重みを四捨五入で求める。絵の外は子のタイルで決まる)。sample8 は SampleFixed と同じく切り捨て
        float4 sample8r(float2 q) {
            float2 f = floor(q);
            float2 t = floor((q - f) * 256 + 0.5);
            float4 p00 = fetch(f), p10 = fetch(f + float2(1, 0)), p01 = fetch(f + float2(0, 1)), p11 = fetch(f + float2(1, 1));
            float4 top = t.x == 0 ? p00 : lerp8(p00, p10, t.x);
            if (t.y == 0) return top;
            return lerp8(top, t.x == 0 ? p01 : lerp8(p01, p11, t.x), t.y);
        }
        half4 out8(float4 v) { return half4(v / 255); }
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
    private static Sprite? MultiPassOnGpu(Sprite s, EffectArgs a, int padX, int padY, Sksl sksl, bool clamp, int passes, Action<SKRuntimeEffectUniforms, int> uniforms) =>
        MultiPassOnGpu(s, a, padX, padY, sksl, clamp ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal, clamp ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal,
            passes, (u, i, _, _) => uniforms(u, i));

    /// <summary>MultiPassOnGpu の、タイル (絵の外の読み方) を縦横で指定する版。uniforms には (回, 幅, 高さ) を渡す。</summary>
    private static Sprite? MultiPassOnGpu(Sprite s, EffectArgs a, int padX, int padY, Sksl sksl, SKShaderTileMode tileX, SKShaderTileMode tileY,
        int passes, Action<SKRuntimeEffectUniforms, int, int, int> uniforms)
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
        try
        {
            for (int i = 0; i < passes; i++)
            {
                using var surface = a.Context.CreateSurface(w, h);
                if (surface == null) return null;
                using var image = current.ToShader(tileX, tileY);
                var kids = new SKRuntimeEffectChildren(effect) { ["image"] = image };
                var u = new SKRuntimeEffectUniforms(effect);
                uniforms(u, i, w, h);
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

    // ---------------- 画素を動かす効果 (1 回で済むもの) ----------------

    private static readonly Sksl ChromaticShader = new(SampleSksl + """
        uniform float2 off;     // ずらす量 (radial でないとき)
        uniform float radial;
        uniform float2 center;
        uniform float k;        // radial のとき: 中心からの距離あたりのずれ
        uniform float pair;     // 0 = 赤と青, 1 = 赤と緑, 2 = 緑と青 (前にずらす色・後ろにずらす色)
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float2 o = radial > 0 ? (q - center) * k : off;
            float4 r = sample8r(q - o), g = fetch(q), b = sample8r(q + o);
            float al = max(r.a, max(g.a, b.a));
            if (pair == 1) return out8(float4(r.r, b.g, g.b, al));
            if (pair == 2) return out8(float4(g.r, r.g, b.b, al));
            return out8(float4(r.r, g.g, b.b, al));
        }
        """);

    /// <summary>色収差 (PixelFx.ChromaticAberration と同じ)。中心は余白を付けた絵の座標。</summary>
    internal static Sprite? ChromaticSprite(Sprite s, EffectArgs a, int padX, int padY, float dx, float dy, bool radial, float cx, float cy, float amount, bool clamp, int pair = 0) =>
        MultiPassOnGpu(s, a, padX, padY, ChromaticShader, Tile(clamp), Tile(clamp), 1, (u, _, w, h) =>
        {
            float far = MathF.Max(1, MathF.Sqrt(MathF.Max(cx * cx, (w - cx) * (w - cx)) + MathF.Max(cy * cy, (h - cy) * (h - cy))));
            u["off"] = new[] { dx, dy }; u["radial"] = radial ? 1f : 0f; u["center"] = new[] { cx, cy };
            u["k"] = amount / far; u["pair"] = (float)pair;
        })
        ?? Resample(s, a, padX, padY, (src, tmp, w, h, rb) => { PixelFx.ChromaticAberration(src, tmp, w, h, rb, dx, dy, radial, cx, cy, amount, clamp, pair); return tmp; });

    private static readonly Sksl RasterShader = new(SampleSksl + """
        uniform float amp;
        uniform float k;        // 2π / 波長
        uniform float phase;
        uniform float vertical;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            if (vertical > 0) return out8(sample8r(float2(q.x, q.y - amp * sin(q.x * k + phase))));
            return out8(sample8r(float2(q.x - amp * sin(q.y * k + phase), q.y)));
        }
        """);

    /// <summary>ラスター (PixelFx.Raster と同じ)。</summary>
    internal static Sprite? RasterSprite(Sprite s, EffectArgs a, int padX, int padY, float amp, float wavelength, float phase, bool vertical, bool clamp) =>
        MultiPassOnGpu(s, a, padX, padY, RasterShader, Tile(clamp), Tile(clamp), 1, (u, _, _, _) =>
        {
            u["amp"] = amp; u["k"] = 2 * MathF.PI / MathF.Max(1, wavelength); u["phase"] = phase; u["vertical"] = vertical ? 1f : 0f;
        })
        ?? Resample(s, a, padX, padY, (src, tmp, w, h, rb) => { PixelFx.Raster(src, tmp, w, h, rb, amp, wavelength, phase, vertical, clamp); return tmp; });

    private static readonly Sksl RippleShader = new(SampleSksl + """
        uniform float2 center;
        uniform float amp;
        uniform float k;
        uniform float phase;
        uniform float decay;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float2 d = q - center;
            float r = length(d);
            if (r < 1e-3) return out8(fetch(q));
            float m = amp * sin(r * k - phase);
            if (decay > 0) m *= exp(-r / decay);
            return out8(sample8r(center + d * ((r + m) / r)));
        }
        """);

    /// <summary>波紋 (PixelFx.Ripple と同じ)。</summary>
    internal static Sprite? RippleSprite(Sprite s, EffectArgs a, int padX, int padY, float cx, float cy, float amp, float wavelength, float phase, float decay, bool clamp) =>
        MultiPassOnGpu(s, a, padX, padY, RippleShader, Tile(clamp), Tile(clamp), 1, (u, _, _, _) =>
        {
            u["center"] = new[] { cx, cy }; u["amp"] = amp; u["k"] = 2 * MathF.PI / MathF.Max(1, wavelength); u["phase"] = phase; u["decay"] = decay;
        })
        ?? Resample(s, a, padX, padY, (src, tmp, w, h, rb) => { PixelFx.Ripple(src, tmp, w, h, rb, cx, cy, amp, wavelength, phase, decay, clamp); return tmp; });

    private static readonly Sksl KaleidoShader = new(SampleSksl + """
        uniform float2 center;
        uniform float seg;
        uniform float rot;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float2 d = q - center;
            float r = length(d);
            float t = atan(d.y, d.x) - rot;
            float n = floor(t / seg);
            float an = t - n * seg;
            if (mod(n, 2) == 1) an = seg - an;   // 1 つおきに折り返す
            an += rot;
            return out8(sample8r(center + r * float2(cos(an), sin(an))));
        }
        """);

    /// <summary>万華鏡 (PixelFx.Kaleidoscope と同じ)。</summary>
    internal static Sprite? KaleidoSprite(Sprite s, EffectArgs a, int padX, int padY, float cx, float cy, int segments, float rotation, bool clamp)
    {
        segments = Math.Clamp(segments, 1, 360);
        return MultiPassOnGpu(s, a, padX, padY, KaleidoShader, Tile(clamp), Tile(clamp), 1, (u, _, _, _) =>
            {
                u["center"] = new[] { cx, cy }; u["seg"] = 2 * MathF.PI / segments; u["rot"] = rotation * MathF.PI / 180f;
            })
            ?? Resample(s, a, padX, padY, (src, tmp, w, h, rb) => { PixelFx.Kaleidoscope(src, tmp, w, h, rb, cx, cy, segments, rotation, clamp); return tmp; });
    }

    private static readonly Sksl DisplaceShader = new(SampleSksl + """
        uniform float mode;      // 0 = ずらす, 1 = 拡大, 2 = 回転
        uniform float rectShape;
        uniform float2 center;
        uniform float2 radius;
        uniform float feather;
        uniform float2 param;    // (p1, p2)
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float2 d = q - center, n = d / radius;
            float dist = rectShape > 0 ? max(abs(n.x), abs(n.y)) : length(n);
            float t = clamp((1 - dist) / feather, 0, 1);
            if (t <= 0) return out8(fetch(q));
            t = t * t * (3 - 2 * t);
            float2 src;
            if (mode == 0) src = q - param * t;
            else if (mode == 1) src = center + d / max(0.01, 1 + param.x / 100 * t);
            else {
                float an = -param.x * 3.14159265358979 / 180 * t, cs = cos(an), sn = sin(an);
                src = center + float2(d.x * cs - d.y * sn, d.x * sn + d.y * cs);
            }
            return out8(sample8r(src));
        }
        """);

    /// <summary>ずらし (PixelFx.Displace と同じ)。</summary>
    internal static Sprite? DisplaceSprite(Sprite s, EffectArgs a, int padX, int padY, int mode, bool rect, float cx, float cy, float rx, float ry, float feather, float p1, float p2, bool clamp)
    {
        float frx = MathF.Max(0.5f, rx), fry = MathF.Max(0.5f, ry), ff = Math.Clamp(feather, 0.001f, 1);
        return MultiPassOnGpu(s, a, padX, padY, DisplaceShader, Tile(clamp), Tile(clamp), 1, (u, _, _, _) =>
            {
                u["mode"] = (float)mode; u["rectShape"] = rect ? 1f : 0f; u["center"] = new[] { cx, cy };
                u["radius"] = new[] { frx, fry }; u["feather"] = ff; u["param"] = new[] { p1, p2 };
            })
            ?? Resample(s, a, padX, padY, (src, tmp, w, h, rb) => { PixelFx.Displace(src, tmp, w, h, rb, mode, rect, cx, cy, rx, ry, feather, p1, p2, clamp); return tmp; });
    }

    private static readonly Sksl PolarShader = new(SampleSksl + """
        uniform float2 size;
        uniform float radius;
        uniform float hole;
        uniform float rot;      // 回転 (1 = 1 周)
        uniform float swirl;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float2 c = (size - 1) / 2;
            float2 d = q - c;
            float r = length(d);
            // 円の縁と穴の縁は 1 ピクセルでなめらかにする
            float cover = clamp(radius - r + 0.5, 0, 1) * clamp(r - hole + 0.5, 0, 1);
            if (cover <= 0) return half4(0);
            float rr = clamp((r - hole) / (radius - hole), 0, 1);
            float u = atan(d.x, -d.y) / (2 * 3.14159265358979) + rot + swirl * rr;   // 0 = 真上、時計回り
            // 横は繰り返し、縦は端 (子のタイル)。PixelFx.SampleWrapX と同じく、縦は絵の中に収める
            float4 v = sample8r(float2(u * size.x - 0.5, clamp((1 - rr) * (size.y - 1), 0, size.y - 1)));
            if (cover >= 1) return out8(v);
            return out8(lerp8(float4(0), v, floor(cover * 256)));
        }
        """);

    /// <summary>極座標変換 (PixelFx.Polar と同じ)。</summary>
    internal static Sprite? PolarSprite(Sprite s, EffectArgs a, float radius, float hole, float rotation, float swirl)
    {
        float r = MathF.Max(1, radius), hl = Math.Clamp(hole, 0, r - 1);
        return MultiPassOnGpu(s, a, 0, 0, PolarShader, SKShaderTileMode.Repeat, SKShaderTileMode.Clamp, 1, (u, _, w, h) =>
            {
                u["size"] = new[] { (float)w, h }; u["radius"] = r; u["hole"] = hl; u["rot"] = rotation / 360f; u["swirl"] = swirl;
            })
            ?? Resample(s, a, 0, 0, (src, tmp, w, h, rb) => { PixelFx.Polar(src, tmp, w, h, rb, radius, hole, rotation, swirl); return tmp; });
    }

    private static SKShaderTileMode Tile(bool clamp) => clamp ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;

    // ---------------- 1 画素ずつの色の処理 ----------------

    /// <summary>
    /// MapColors (EffectsFilters) と同じ流れの SkSL: アルファを戻した色 c (0〜1) を body で変え、strength の割合で元の色と混ぜ、
    /// アルファを掛けて 8 ビットに丸める。decls には uniform や関数を書く。
    /// </summary>
    private static Sksl MapColorsSksl(string decls, string body) => new($$"""
        uniform shader image;
        uniform float strength;
        {{decls}}
        float luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
        float3 saturate3(float3 c, float k) { float l = luma(c); return l + (c - l) * k; }
        float smooth01(float t) { t = clamp(t, 0, 1); return t * t * (3 - 2 * t); }
        half4 main(float2 p) {
            half4 px = image.eval(p);
            float a = floor(float(px.a) * 255 + 0.5);
            if (a <= 0) return px;
            float3 c0 = floor(float3(px.rgb) * 255 + 0.5) / a;
            float3 c = c0;
            {{body}}
            if (strength < 1) c = c0 + (c - c0) * strength;
            return half4(half3(floor(clamp(c, 0, 1) * a + 0.5) / 255), half(a / 255));
        }
        """);

    private static Sprite? MapColorsOnGpu(Sprite s, EffectArgs a, Sksl sksl, float strength, Action<SKRuntimeEffectUniforms>? uniforms = null,
        Action<SKRuntimeEffectChildren>? children = null) =>
        strength <= 0 ? s : ApplyGpuShader(s, a, sksl, u => { u["strength"] = strength; uniforms?.Invoke(u); }, children == null ? null : (k, _, _) => children(k));

    private static readonly Sksl ShadowHighlightShader = MapColorsSksl("uniform float sh; uniform float hi;", """
        float l = luma(c);
        float ws = (1 - l) * (1 - l) * 0.7, wh = l * l * 0.6;
        c += sh * ws * (1 - c) - hi * wh * c;
        """);

    private static readonly Sksl GradientMapShader = MapColorsSksl("uniform float3 dark; uniform float3 mid; uniform float3 light; uniform float useMid;", """
        float l = luma(c);
        float3 g0 = dark, g1 = light;
        float t = l;
        if (useMid > 0) { if (l < 0.5) { g1 = mid; t = l * 2; } else { g0 = mid; t = l * 2 - 1; } }
        c = (g0 + (g1 - g0) * t) / 255;
        """);

    private static readonly Sksl LookShader = MapColorsSksl("uniform float preset;", """
        if (preset == 0) { float l = luma(c); c = float3(l * 1.07 + 0.05, l * 0.88 + 0.02, l * 0.66); }
        else if (preset == 1) { float t = smooth01(luma(c)); c += float3(-0.08 + 0.2 * t, 0.02, 0.1 - 0.24 * t); c = saturate3(c, 1.1); }
        else if (preset == 2) { c = saturate3(c, 0.7); c = float3(0.08 + c.r * 0.86 + 0.02, 0.08 + c.g * 0.84, 0.07 + c.b * 0.8); }
        else if (preset == 3) { c = float3(c.r * 0.9, c.g * 0.98, c.b * 1.06 + 0.03); }
        else if (preset == 4) { c = float3(c.r * 1.06 + 0.03, c.g, c.b * 0.88); }
        else if (preset == 5) { float l = smooth01((luma(c) - 0.5) * 1.5 + 0.5); c = float3(l); }
        else if (preset == 6) { c = saturate3(c, 1.45); c = (c - 0.5) * 1.1 + 0.5; }
        else if (preset == 7) { c = saturate3(c, 0.45); c = float3(smooth01((c.r - 0.5) * 1.25 + 0.5), smooth01((c.g - 0.5) * 1.25 + 0.5), smooth01((c.b - 0.5) * 1.25 + 0.5)); }
        else if (preset == 8) { float t = luma(c); c = float3(c.r * 1.1 + 0.06 * t + 0.03, c.g * 0.92, c.b * 0.78 + 0.08 * (1 - t)); }
        else { c = saturate3(c, 0.45); c = float3(c.r * 0.75, c.g * 0.85, c.b * 1.05 + 0.04); }
        """);

    // 3D LUT: 横に b の段を並べた 2D の画像 (幅 n*n、高さ n)。SampleCube と同じ 8 点の補間
    private static readonly Sksl CubeShader = MapColorsSksl("""
        uniform shader cube;
        uniform float n;
        float3 cubeAt(float r, float g, float b) { return float3(cube.eval(float2(r + b * n + 0.5, g + 0.5)).rgb); }
        """, """
        float m = n - 1;
        float3 f = clamp(c, 0, 1) * m;
        float3 i0 = min(floor(f), m - 1);
        float3 t = f - i0;
        float3 acc = float3(0);
        for (int k = 0; k < 8; k++) {
            float dr = mod(float(k), 2), dg = mod(floor(float(k) / 2), 2), db = floor(float(k) / 4);
            float wgt = (dr == 1 ? t.r : 1 - t.r) * (dg == 1 ? t.g : 1 - t.g) * (db == 1 ? t.b : 1 - t.b);
            acc += cubeAt(i0.r + dr, i0.g + dg, i0.b + db) * wgt;
        }
        c = acc;
        """);

    // トーンカーブ (1 色だけ): 256 個の表 (横 256 の画像の赤)。Map と同じく四捨五入した位置を引く
    private static readonly Sksl CurveChannelShader = MapColorsSksl("""
        uniform shader table;
        uniform float channel;
        float mapv(float v) { return floor(float(table.eval(float2(clamp(floor(v * 255 + 0.5), 0, 255) + 0.5, 0.5)).r) * 255 + 0.5) / 255; }
        """, """
        if (channel == 1) c.r = mapv(c.r); else if (channel == 2) c.g = mapv(c.g); else c.b = mapv(c.b);
        """);

    /// <summary>3D LUT を GPU の画像 (半精度) にしたもの。LUT ごとに 1 つ作って使い回す。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CubeLut, SKImage> CubeImages = new();

    private static SKImage? CubeImage(CubeLut lut)
    {
        if (CubeImages.TryGetValue(lut, out var img)) return img;
        int n = lut.Size;
        var info = new SKImageInfo(n * n, n, SKColorType.RgbaF32, SKAlphaType.Unpremul);
        var data = new float[n * n * n * 4];
        for (int b = 0; b < n; b++)
            for (int g = 0; g < n; g++)
                for (int r = 0; r < n; r++)
                {
                    int src = ((b * n + g) * n + r) * 3, dst = (g * n * n + b * n + r) * 4;
                    data[dst] = lut.Data[src]; data[dst + 1] = lut.Data[src + 1]; data[dst + 2] = lut.Data[src + 2]; data[dst + 3] = 1;
                }
        unsafe
        {
            fixed (float* p = data) img = SKImage.FromPixelCopy(info, (IntPtr)p, n * n * 16);
        }
        if (img != null) CubeImages.AddOrUpdate(lut, img);
        return img;
    }

    private static readonly Sksl HueConvertShader = new("""
        uniform shader image;
        uniform float keyHue;
        uniform float range;
        uniform float edge;
        uniform float shift;
        uniform float satScale;
        uniform float valScale;
        half4 main(float2 p) {
            half4 px = image.eval(p);
            float a = floor(float(px.a) * 255 + 0.5);
            if (a <= 0) return px;
            float3 c = min(floor(float3(px.rgb) * 255 + 0.5) / a, 1.0);
            float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b)), d = mx - mn;
            if (d <= 1e-4) return px;
            float hue;
            if (mx == c.r) hue = 60 * ((c.g - c.b) / d);
            else if (mx == c.g) hue = 60 * ((c.b - c.r) / d + 2);
            else hue = 60 * ((c.r - c.g) / d + 4);
            if (hue < 0) hue += 360;
            float dh = abs(hue - keyHue);
            if (dh > 180) dh = 360 - dh;
            float wgt = dh <= range ? 1 : max(0, 1 - (dh - range) / edge);
            float sat = d / mx;
            wgt *= clamp(sat * 5, 0, 1);
            if (wgt <= 0) return px;
            hue = mod(hue + shift * wgt, 360);
            sat = clamp(sat * (1 + (satScale - 1) * wgt), 0, 1);
            float val = clamp(mx * (1 + (valScale - 1) * wgt), 0, 1);
            // HSV → RGB (PixelFx.HueConvert と同じ)
            float cc = val * sat, hh = hue / 60, xx = cc * (1 - abs(mod(hh, 2) - 1)), mm = val - cc;
            float3 n;
            float sector = floor(hh);
            if (sector == 0) n = float3(cc, xx, 0);
            else if (sector == 1) n = float3(xx, cc, 0);
            else if (sector == 2) n = float3(0, cc, xx);
            else if (sector == 3) n = float3(0, xx, cc);
            else if (sector == 4) n = float3(xx, 0, cc);
            else n = float3(cc, 0, xx);
            return half4(half3(min(float3(a), floor((n + mm) * a + 0.5)) / 255), px.a);
        }
        """);

    private static readonly Sksl ColorKeyShader = new("""
        uniform shader image;
        uniform float3 key;      // キー色 (0〜255)
        uniform float lumaRange;
        uniform float chromaRange;
        uniform float edge;
        half4 main(float2 p) {
            half4 px = image.eval(p);
            float a = floor(float(px.a) * 255 + 0.5);
            if (a <= 0) return px;
            float4 v = floor(float4(px) * 255 + 0.5);
            float3 c = min(v.rgb * (255 / a), 255);
            float ky = 0.299 * key.r + 0.587 * key.g + 0.114 * key.b, kcb = key.b - ky, kcr = key.r - ky;
            float yy = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b, cb = c.b - yy - kcb, cr = c.r - yy - kcr;
            float dy = abs(yy - ky), dc = sqrt(cb * cb + cr * cr);
            float dist = max(lumaRange <= 0 ? (dy > 0.5 ? 2 : 0) : dy / lumaRange, chromaRange <= 0 ? (dc > 0.5 ? 2 : 0) : dc / chromaRange);
            float keep = clamp((dist - 1) / edge + 1, 0, 1);
            if (keep >= 1) return px;
            return half4(floor(v * keep) / 255);
        }
        """);

    private static readonly Sksl LumaKeyShader = new("""
        uniform shader image;
        uniform float level;
        uniform float soft;
        uniform float bright;
        half4 main(float2 p) {
            half4 px = image.eval(p);
            float a = floor(float(px.a) * 255 + 0.5);
            if (a <= 0) return px;
            float4 v = floor(float4(px) * 255 + 0.5);
            float lum = floor((v.r * 54 + v.g * 183 + v.b * 19) / 256);
            if (a < 255) lum = min(255, floor(lum * 255 / a));
            float t = clamp((lum - (level - soft / 2)) / soft, 0, 1);
            t = t * t * (3 - 2 * t);
            float k = floor((bright > 0 ? 1 - t : t) * 256 + 0.5);
            if (k >= 256) return px;
            return half4(floor((v * k + 128) / 256) / 255);
        }
        """);

    // ---------------- 縁の処理 (ライト・ベベル・縁ぼかし) ----------------

    /// <summary>s を GPU でぼかした絵 (同じ大きさ。絵の外は透明)。アルファの面の代わりに使う。作れなければ null。</summary>
    private static SKImage? BlurredOnGpu(Sprite s, EffectArgs a, float sigma, SKShaderTileMode tile)
    {
        using var surface = a.Context.CreateSurface(s.Width, s.Height);
        if (surface == null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        using (var blur = SKImageFilter.CreateBlur(sigma, sigma, tile))
        using (var paint = new SKPaint { ImageFilter = blur })
            surface.Canvas.DrawImage(s.Image, 0, 0, paint);
        return surface.Snapshot();
    }

    /// <summary>ぼかしたアルファを子 "blurred" に渡して、shader で描き直す。GPU 描画でない・使えないときは null。</summary>
    private static Sprite? WithBlurredAlphaOnGpu(Sprite s, EffectArgs a, Sksl sksl, float sigma, SKShaderTileMode tile, Action<SKRuntimeEffectUniforms> uniforms)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || sksl.Effect == null) return null;
        using var blurred = BlurredOnGpu(s, a, sigma, tile);
        if (blurred == null) return null;
        using var blurredShader = blurred.ToShader(tile, tile);
        return ApplyGpuShader(s, a, sksl, uniforms, (kids, _, _) => kids["blurred"] = blurredShader);
    }

    private const string Fetch8Sksl = """
        float4 px8(float2 q) { return floor(float4(image.eval(q + 0.5)) * 255 + 0.5); }
        float ba8(float2 q) { return floor(float(blurred.eval(q + 0.5).a) * 255 + 0.5); }
        """;

    private static readonly Sksl RimLightShader = new("""
        uniform shader image;
        uniform shader blurred;
        uniform float2 shiftPx;
        uniform float k;
        uniform float3 color;
        """ + Fetch8Sksl + """
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 v = px8(q);
            float al = v.a;
            if (al == 0) return half4(0);
            float rim = al - ba8(q + shiftPx);   // 縁の近く (周りが透明) ほど大きい
            if (rim <= 0) return half4(v / 255);
            float add = floor(rim * k / 256);
            float amt = floor(add * al / 255);
            v.rgb = min(float3(al), v.rgb + floor(color * amt / 256));
            return half4(v / 255);
        }
        """);

    private static readonly Sksl BevelShader = new("""
        uniform shader image;
        uniform shader blurred;
        uniform float2 light;
        uniform float k;
        """ + Fetch8Sksl + """
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 v = px8(q);
            float al = v.a;
            if (al == 0) return half4(0);
            // 端は端の値 (子のタイルが Clamp)
            float gx = ba8(q + float2(1, 0)) - ba8(q - float2(1, 0));
            float gy = ba8(q + float2(0, 1)) - ba8(q - float2(0, 1));
            float shade = (-gx * light.x - gy * light.y) * 0.5 * k;
            if (abs(shade) < 0.004) return half4(v / 255);
            float target = shade > 0 ? al : 0;
            float t = min(1, abs(shade));
            v.rgb = floor(v.rgb + (target - v.rgb) * t);
            return half4(v / 255);
        }
        """);

    private static readonly Sksl EdgeFeatherShader = new("""
        uniform shader image;
        uniform shader blurred;
        """ + Fetch8Sksl + """
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 v = px8(q);
            float f = clamp((ba8(q) - 128) * 2 + 1, 0, 256);
            if (f >= 256) return half4(v / 255);
            return half4(floor((v * f + 128) / 256) / 255);
        }
        """);

    // ---------------- 輪郭抽出・ノイズ ----------------

    private static readonly Sksl EdgeDetectShader = new(SampleSksl + """
        uniform float gain;
        uniform float threshold;
        uniform float alphaEdge;
        uniform float4 color;     // 乗算済み (0〜255)
        uniform float overlay;
        float val(float2 q) {
            float4 c = fetch(q);
            return alphaEdge > 0 ? c.a : floor((c.r * 54 + c.g * 183 + c.b * 19) / 256);
        }
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float gx = val(q + float2(1, -1)) + 2 * val(q + float2(1, 0)) + val(q + float2(1, 1))
                     - val(q + float2(-1, -1)) - 2 * val(q + float2(-1, 0)) - val(q + float2(-1, 1));
            float gy = val(q + float2(-1, 1)) + 2 * val(q + float2(0, 1)) + val(q + float2(1, 1))
                     - val(q + float2(-1, -1)) - 2 * val(q + float2(0, -1)) - val(q + float2(1, -1));
            float g = sqrt(gx * gx + gy * gy) / 1020 * gain;
            float e = clamp((g - threshold) / max(0.001, 1 - threshold), 0, 1);
            float4 edge = e <= 0 ? float4(0) : floor((color * floor(e * 256) + 128) / 256);
            if (overlay <= 0) return out8(edge);
            // PixelFx.Over: 上に重ねる (乗算済み)
            float4 b = fetch(q);
            if (edge.a == 0) return out8(b);
            if (edge.a == 255) return out8(edge);
            return out8(min(float4(255), edge + floor((b * (255 - edge.a) + 127) / 255)));
        }
        """);

    /// <summary>輪郭抽出 (PixelFx.EdgeDetect と同じ。周りは端のピクセル)。</summary>
    internal static Sprite? EdgeDetectSprite(Sprite s, EffectArgs a, int pad, float gain, float threshold, bool alphaEdge, uint premul, bool overlay) =>
        MultiPassOnGpu(s, a, pad, pad, EdgeDetectShader, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, 1, (u, _, _, _) =>
        {
            u["gain"] = gain; u["threshold"] = threshold; u["alphaEdge"] = alphaEdge ? 1f : 0f; u["overlay"] = overlay ? 1f : 0f;
            u["color"] = new float[] { premul >> 16 & 0xFF, premul >> 8 & 0xFF, premul & 0xFF, premul >> 24 };
        })
        ?? Resample(s, a, pad, pad, (src, tmp, w, h, rb) => { PixelFx.EdgeDetect(src, tmp, w, h, rb, gain, threshold, alphaEdge, premul, overlay); return tmp; });

    private static readonly Sksl NoiseShader = new("""
        uniform shader image;
        uniform shader cells;     // マスごとの乱数 (PixelFx.Hash の下 3 バイト)
        uniform float cell;
        uniform float amp;
        uniform float colored;
        // C# の整数の割り算 (0 に向かって切り捨て)
        float idiv(float x, float d) { return x < 0 ? -floor(-x / d) : floor(x / d); }
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 v = floor(float4(image.eval(p)) * 255 + 0.5);
            float a = v.a;
            if (a == 0) return half4(0);
            // マスは画素の中心 (p) で決める。GPU は割り算を逆数の掛け算にすることがあり、q / cell がマスの境目ちょうどだと 1 つ前のマスになる
            float3 r = floor(float3(cells.eval(floor(p / cell) + 0.5).rgb) * 255 + 0.5);
            // -128〜127 → ×amp/256 (算術シフトなので切り下げ)
            float n0 = floor((r.r - 128) * amp / 256);
            float n1 = colored > 0 ? floor((r.g - 128) * amp / 256) : n0;
            float n2 = colored > 0 ? floor((r.b - 128) * amp / 256) : n0;
            if (a != 255) { n0 = idiv(n0 * a, 255); n1 = idiv(n1 * a, 255); n2 = idiv(n2 * a, 255); }
            // 画素は B・G・R の順に n0・n1・n2
            float3 o = clamp(float3(v.r + n2, v.g + n1, v.b + n0), 0, a);
            return half4(half3(o / 255), half(a / 255));
        }
        """);

    /// <summary>ノイズを GPU で (乱数は CPU で作って送る。送るのは待たない)。GPU 描画でなければ null。</summary>
    private static Sprite? NoiseOnGpu(Sprite s, EffectArgs a, float amount, int cell, bool color, uint seed)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || NoiseShader.Effect == null) return null;
        int amp = (int)MathF.Round(Math.Clamp(amount, 0, 255) * 2);
        if (amp == 0) return s;
        cell = Math.Max(1, cell);
        int cw = (s.Width + cell - 1) / cell, ch = (s.Height + cell - 1) / cell;
        var px = new byte[cw * ch * 4];
        PixelOps.ForBands(cw, ch, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < cw; x++)
                {
                    uint r = PixelFx.Hash((uint)x, (uint)y, seed);
                    int i = (y * cw + x) * 4;
                    px[i] = (byte)r; px[i + 1] = (byte)(r >> 8); px[i + 2] = (byte)(r >> 16); px[i + 3] = 255;
                }
        });
        using var cells = SKImage.FromPixelCopy(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Unpremul), px);
        if (cells == null) return null;
        using var cellShader = cells.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        return ApplyGpuShader(s, a, NoiseShader, u => { u["cell"] = (float)cell; u["amp"] = (float)amp; u["colored"] = color ? 1f : 0f; },
            (kids, _, _) => kids["cells"] = cellShader);
    }
}
