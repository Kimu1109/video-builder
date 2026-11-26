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
        int passes, Action<SKRuntimeEffectUniforms, int, int, int> uniforms, Action<SKRuntimeEffectChildren>? children = null)
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
                children?.Invoke(kids);
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

    // ---------------- モザイク ----------------

    /// <summary>
    /// 1 回の描画でマスの平均を求めるマスの大きさの上限。SkSL のループは回数が定数でなければならず、Skia はループを展開するので、
    /// ループの中身を含めて大きくなりすぎるとコンパイルできない (32 × 32 は通り、64 × 64 は通らない)。
    /// それより大きいマスは、横と縦に分けて合計する (BigCellSums。途中の合計は 24 ビットの値の画像に持つ)。
    /// </summary>
    private const int MaxMosaicCell = 32;

    private static readonly Sksl MosaicCellShader = new("""
        uniform shader image;
        uniform float cell;
        uniform float2 size;
        // 1 画素 = 1 マス。マスの中の平均 (乗算済みのまま)
        half4 main(float2 p) {
            float2 o = floor(p) * cell;
            float2 e = min(size, o + cell);
            float4 sum = float4(0);
            for (int j = 0; j < 32; j++) {
                float y = o.y + float(j);
                if (y >= e.y) break;
                for (int i = 0; i < 32; i++) {
                    float x = o.x + float(i);
                    if (x >= e.x) break;
                    sum += floor(float4(image.eval(float2(x, y) + 0.5)) * 255 + 0.5);
                }
            }
            float n = (e.x - o.x) * (e.y - o.y);
            // (sum + n/2) / n の切り捨て (PixelOps.Mosaic と同じ四捨五入)。+0.5 で、割り算の誤差で境目ちょうどの値が 1 つ下にならないようにする
            return half4(floor((sum + floor(n / 2) + 0.5) / n) / 255);
        }
        """);

    /// <summary>
    /// モザイクを GPU で (マスごとの平均を小さな画像に描いてから、補間なしで広げる)。
    /// GPU 描画でない・マスが大きすぎるときは null (読み戻して CPU で処理する)。
    /// </summary>
    internal static Sprite? MosaicOnGpu(Sprite s, EffectArgs a, int size) =>
        size <= 1 && a.Context.Gpu != null && !GpuShadersDisabled ? s : CellsOnGpu(s, a, size, MosaicCellShader, _ => { }, null, BigMosaicShader);

    /// <summary>
    /// マス (size 四方) ごとの色を GPU で求めて (sksl が 1 画素 = 1 マスの小さな画像を描く。子 "image" が元の絵、uniform の cell・size も渡す)、
    /// 補間なしで広げた絵。マスが MaxMosaicCell より大きいときは、マスの合計を横・縦の順に求めてから big で色にする (BigCellSums)。
    /// マスの大きさに上限はない。GPU 描画でないときは null。
    /// </summary>
    private static Sprite? CellsOnGpu(Sprite s, EffectArgs a, int size, Sksl sksl, Action<SKRuntimeEffectUniforms> uniforms,
        Action<SKRuntimeEffectChildren>? children = null, Sksl? big = null)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled) return null;
        size = Math.Max(1, size);
        bool useBig = size > MaxMosaicCell;
        if (useBig && big == null) return null;
        if ((useBig ? big! : sksl).Effect is not { } effect) return null;
        int sw = (s.Width + size - 1) / size, sh = (s.Height + size - 1) / size;
        SKImage? smallImg;
        var owned = new List<IDisposable>();
        try
        {
            var image = s.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            owned.Add(image);
            SKShader[]? sums = null;
            if (useBig && (sums = BigCellSums(s, a, size, image, owned)) == null) return null;
            smallImg = RunOnGpu(a, sw, sh, effect, u =>
            {
                u["cell"] = (float)size; u["size"] = new float[] { s.Width, s.Height };
                uniforms(u);
            }, k =>
            {
                if (sums != null) for (int c = 0; c < 4; c++) { k["hi" + c] = sums[c]; k["lo" + c] = sums[c + 4]; }
                else k["image"] = image;
                children?.Invoke(k);
            });
        }
        finally { foreach (var d in owned) d.Dispose(); }
        if (smallImg == null) return null;
        using var smallOwner = smallImg;
        return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
        {
            c.Save();
            c.ClipRect(SKRect.Create(x, y, s.Width, s.Height));
            c.DrawImage(smallImg, SKRect.Create(sw, sh), SKRect.Create(x, y, sw * size, sh * size), new SKSamplingOptions(SKFilterMode.Nearest), null);
            c.Restore();
        });
    }

    // 1 列の合計。入力の並び (axis の向き) を cell 個ずつの区画に分け、区画を 1024 個ずつの塊に分けて (区画ごとに chunks 個)、
    // 出力の 1 画素が 1 つの塊の合計 (SkSL のループは 1025 回まで通る)。出力の j 番目は、区画 j / chunks の j % chunks 番目の塊
    private const string SegmentSumSksl = """
        uniform float cell;
        uniform float chunks;
        uniform float n;        // 入力の並びの長さ
        // axis: (1, 0) なら横、(0, 1) なら縦 (連結するので、最後の行に // の注釈を書かない)
        uniform float2 axis;
        float2 range(float2 c) {
            float j = dot(c, axis), cc = floor((j + 0.5) / chunks), k = j - cc * chunks;
            float o = cc * cell + k * 1024;
            return float2(o, min(n, min(cc * cell + cell, o + 1024)));
        }
        """;

    // 絵を読むもの (mask の色、0〜255)
    private static readonly Sksl SegmentSumImageShader = new(Enc24Sksl + SegmentSumSksl + """
        uniform shader src;
        uniform float4 mask;
        half4 main(float2 p) {
            float2 c = floor(p), r = range(c);
            float other = dot(c, 1 - axis), sum = 0;
            for (int i = 0; i < 1025; i++) {
                float t = r.x + float(i);
                if (t >= r.y) break;
                sum += dot(floor(float4(src.eval(axis * t + (1 - axis) * other + 0.5)) * 255 + 0.5), mask);
            }
            return enc24(sum / 16777216);
        }
        """);

    // 前の回の値 (idx24) を読むもの。合計が 24 ビットを超えうるので、上位 (4096 単位) と下位 (4096 未満) に分けて足す
    // (下位は毎回 4096 未満に戻すので、1024 個足しても収まる)。ループの中身を小さくするため、足すものごとに分ける
    private static Sksl SegmentSumOf(string value) => new(Val24Sksl + Enc24Sksl + SegmentSumSksl + $$"""

        uniform shader hiSrc;
        uniform shader loSrc;
        half4 main(float2 p) {
            float2 c = floor(p), r = range(c);
            float other = dot(c, 1 - axis), sum = 0;
            for (int i = 0; i < 1025; i++) {
                float t = r.x + float(i);
                if (t >= r.y) break;
                float2 q = axis * t + (1 - axis) * other + 0.5;
                {{value}}
            }
            return enc24(sum / 16777216);
        }
        """);

    // 下位をそのまま合計
    private static readonly Sksl SegmentSumPlainShader = SegmentSumOf("sum += idx24(loSrc.eval(q));");
    // 上位 + 下位の 4096 単位の部分
    private static readonly Sksl SegmentSumHiShader = SegmentSumOf("sum += idx24(hiSrc.eval(q)) + floor(idx24(loSrc.eval(q)) / 4096);");
    // 下位の 4096 未満の部分
    private static readonly Sksl SegmentSumLoShader = SegmentSumOf("float lo = idx24(loSrc.eval(q)); sum += lo - floor(lo / 4096) * 4096;");

    /// <summary>
    /// マス (size 四方) ごとの R・G・B・A の合計を、上位 (4096 単位) と下位 (4096 未満) に分けた 8 枚の画像 (1 画素 = 1 マス) のシェーダー。
    /// 横の合計 (1 行ぶん。絵の幅は 16384 までなので 24 ビットに収まる) → 縦の合計 (上位と下位に分ける) の順。
    /// 1 回の描画で読むのは 1024 個までなので、それより大きいマスは 1024 個ずつの塊の合計を作ってから、塊を合計する。
    /// </summary>
    private static SKShader[]? BigCellSums(Sprite s, EffectArgs a, int size, SKShader image, List<IDisposable> owned)
    {
        if (SegmentSumImageShader.Effect is not { } imageFx || SegmentSumPlainShader.Effect is not { } plainFx
            || SegmentSumHiShader.Effect is not { } hiFx || SegmentSumLoShader.Effect is not { } loFx) return null;
        int w = s.Width, h = s.Height, sw = (w + size - 1) / size, sh = (h + size - 1) / size;
        using var zero = ValueImage(1, 1, (_, _) => 0);
        if (zero == null) return null;
        var zeroShader = zero.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        owned.Add(zeroShader);
        SKShader? Run(int ow, int oh, SKRuntimeEffect fx, Action<SKRuntimeEffectUniforms> uniforms, Action<SKRuntimeEffectChildren> children)
        {
            var img = RunOnGpu(a, ow, oh, fx, uniforms, children);
            if (img == null) return null;
            owned.Add(img);
            var sh2 = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            owned.Add(sh2);
            return sh2;
        }
        void Seg(SKRuntimeEffectUniforms u, int cell, int chunks, int n, bool vertical)
        {
            u["cell"] = (float)cell; u["chunks"] = (float)chunks; u["n"] = (float)n;
            u["axis"] = vertical ? new float[] { 0, 1 } : new float[] { 1, 0 };
        }
        int Chunks(int cell, int n) => (Math.Min(cell, n) + 1023) / 1024;
        var result = new SKShader[8];
        for (int c = 0; c < 4; c++)
        {
            // 横: 行ごとに、マスの幅の合計 (塊が 2 つ以上なら、塊の合計を作ってから合計する)
            int ch = c, k = Chunks(size, w);
            var rows = Run(sw * k, h, imageFx, u =>
            {
                Seg(u, size, k, w, false);
                u["mask"] = new float[] { ch == 0 ? 1 : 0, ch == 1 ? 1 : 0, ch == 2 ? 1 : 0, ch == 3 ? 1 : 0 };
            }, kids => kids["src"] = image);
            for (int len = k; rows != null && len > 1; )
            {
                int kk = Chunks(len, len), inLen = len, src = sw * len;
                var input = rows;
                rows = Run(sw * kk, h, plainFx, u => Seg(u, inLen, kk, src, false),
                    kids => { kids["hiSrc"] = zeroShader; kids["loSrc"] = input; });
                len = kk;
            }
            if (rows == null) return null;
            // 縦: 上位と下位を別々に合計する (1 回目は、横の合計を上位 0・下位 その値 として分ける)
            SKShader hi = zeroShader, lo = rows;
            int vcell = size, vk = Chunks(size, h), vn = h;
            while (true)
            {
                SKShader hiIn = hi, loIn = lo;
                int cell = vcell, chunks = vk, n = vn;
                var nhi = Run(sw, sh * chunks, hiFx, u => Seg(u, cell, chunks, n, true), kids => { kids["hiSrc"] = hiIn; kids["loSrc"] = loIn; });
                var nlo = Run(sw, sh * chunks, loFx, u => Seg(u, cell, chunks, n, true), kids => { kids["hiSrc"] = hiIn; kids["loSrc"] = loIn; });
                if (nhi == null || nlo == null) return null;
                hi = nhi; lo = nlo;
                if (chunks == 1) break;
                vcell = chunks; vn = sh * chunks; vk = Chunks(chunks, chunks);
            }
            result[c] = hi; result[c + 4] = lo;
        }
        return result;
    }

    /// <summary>
    /// 大きなマスの SkSL の共通部分: 合計 (上位 hi・下位 lo) の子と、マスの画素の数・合計 ÷ 数の正確な切り捨て。
    /// 数 (最大 16384²) も 24 ビットを超えうるので、上位 nh (4096 単位)・下位 nl に分けて持つ。
    /// </summary>
    private const string BigCellSksl = """
        uniform shader hi0; uniform shader hi1; uniform shader hi2; uniform shader hi3;
        uniform shader lo0; uniform shader lo1; uniform shader lo2; uniform shader lo3;
        uniform float cell;
        uniform float2 size;
        float4 his(float2 p) { return float4(idx24(hi0.eval(p)), idx24(hi1.eval(p)), idx24(hi2.eval(p)), idx24(hi3.eval(p))); }
        float4 los(float2 p) { return float4(idx24(lo0.eval(p)), idx24(lo1.eval(p)), idx24(lo2.eval(p)), idx24(lo3.eval(p))); }
        // マス p の画素の数 a × b (a, b ≤ 16384) を、上位 (4096 単位) と下位に分ける (どの途中の値も 2^24 未満の整数)
        float2 cellCount(float2 p) {
            float2 o = floor(p) * cell, e = min(size, o + cell);
            float a = e.x - o.x, b = e.y - o.y;
            float ah = floor(a / 64), al = a - ah * 64;
            float u = ah * b, t = al * b;               // a b = u × 64 + t
            float uh = floor(u / 64);
            float r = (u - uh * 64) * 64 + t;           // a b = uh × 4096 + r
            float rh = floor(r / 4096);
            return float2(uh + rh, r - rh * 4096);
        }
        // floor((hi × 4096 + lo) / (nh × 4096 + nl))。商は 0〜255 ほど。浮動小数点の見積もりを、正確な整数の差で直す
        // (差は 0 の近くでだけ正確であればよい。(hi - q nh) 4096 + (lo - q nl) の各項は 2^24 未満の整数)
        float divHL(float hi, float lo, float2 n) {
            float q = floor((hi * 4096 + lo) / (n.x * 4096 + n.y));
            for (int i = 0; i < 3; i++) {
                float d = (hi - q * n.x) * 4096 + (lo - q * n.y);
                float e = (hi - (q + 1) * n.x) * 4096 + (lo - (q + 1) * n.y);
                if (d < 0) q -= 1; else if (e >= 0) q += 1;
            }
            return q;
        }
        """;

    private static readonly Sksl BigMosaicShader = new(Val24Sksl + BigCellSksl + """
        half4 main(float2 p) {
            float2 n = cellCount(p);
            // (合計 + n/2) / n の切り捨て (PixelOps.Mosaic と同じ四捨五入)。n/2 も上位・下位に分けて足す
            float hh = floor(n.x / 2), hl = (n.x - hh * 2) * 2048 + floor(n.y / 2);
            float4 h = his(p) + hh, l = los(p) + hl;
            return half4(float4(divHL(h.r, l.r, n), divHL(h.g, l.g, n), divHL(h.b, l.b, n), divHL(h.a, l.a, n)) / 255);
        }
        """);

    // ---------------- 旗・渦巻き・インターレース解除 ----------------

    private static readonly Sksl FlagShader = new(SampleSksl + """
        uniform float amp;
        uniform float k;
        uniform float shift;
        uniform float width;
        uniform float lightAmp;   // min(1, |amp| / 20)
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float reach = q.x / max(1, width - 1);
            float arg = k * q.x - shift;
            float4 c = sample8r(float2(q.x, q.y - amp * reach * sin(arg)));
            // 波の斜面の向きで明るさを変える (ScalePixelColor・LightenPixel と同じ丸め)
            float light = 1 + 0.22 * reach * cos(arg) * lightAmp;
            if (light < 1) { float t = floor(clamp(light, 0, 1) * 256); c.rgb = floor(c.rgb * t / 256); }
            else if (light > 1) { float kk = light - 1; c.rgb = floor(min(float3(c.a), c.rgb + (c.a - min(float3(c.a), c.rgb)) * kk)); }
            return out8(c);
        }
        """);

    /// <summary>旗のようになびく (EffectsAnimation.Flag と同じ)。上下に pad の余白を付ける。</summary>
    internal static Sprite? FlagOnGpu(Sprite s, EffectArgs a, int pad, float amp, float k, float shift) =>
        MultiPassOnGpu(s, a, 0, pad, FlagShader, SKShaderTileMode.Decal, SKShaderTileMode.Decal, 1, (u, _, w, _) =>
        {
            u["amp"] = amp; u["k"] = k; u["shift"] = shift; u["width"] = (float)w; u["lightAmp"] = MathF.Min(1, MathF.Abs(amp) / 20);
        });

    private static readonly Sksl SwirlShader = new(SampleSksl + """
        uniform float2 center;
        uniform float radius;
        uniform float maxAngle;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float2 o = q - center;
            float r = length(o);
            if (r >= radius) return out8(fetch(q));
            float t = 1 - r / radius, an = maxAngle * t * t;
            float cs = cos(an), sn = sin(an);
            return out8(sample8r(center + float2(o.x * cs - o.y * sn, o.x * sn + o.y * cs)));
        }
        """);

    /// <summary>トランジションの渦巻き (EffectsTransition.Swirl と同じ。絵の外は端のピクセル)。</summary>
    internal static Sprite? SwirlOnGpu(Sprite s, EffectArgs a, float cx, float cy, float radius, float maxAngle) =>
        MultiPassOnGpu(s, a, 0, 0, SwirlShader, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, 1, (u, _, _, _) =>
        {
            u["center"] = new[] { cx, cy }; u["radius"] = radius; u["maxAngle"] = maxAngle;
        });

    private static readonly Sksl DeinterlaceShader = new(SampleSksl + """
        uniform float mode;
        uniform float height;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 cur = fetch(q);
            float4 above = fetch(float2(q.x, max(0, q.y - 1))), below = fetch(float2(q.x, min(height - 1, q.y + 1)));
            float odd = mod(q.y, 2);
            float up = mod(q.x, 2) == 0 ? 1 : 0;
            if (mode <= 1) {
                if (odd == (mode == 0 ? 1 : 0) || height == 1) return out8(cur);
                // 補う行が絵の外なら、もう片方の行をそのまま使う
                float4 a2 = q.y - 1 >= 0 ? above : below, b2 = q.y + 1 < height ? below : above;
                return average(a2, b2, up);
            }
            if (mode == 2) return out8(odd == 0 ? cur : above);
            return average(floor((above + below + 1) / 2), cur, 0);
        }
        """);

    /// <summary>インターレース解除 (PixelFx.Deinterlace と同じ)。</summary>
    internal static Sprite? DeinterlaceSprite(Sprite s, EffectArgs a, int mode) =>
        MultiPassOnGpu(s, a, 0, 0, DeinterlaceShader, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, 1, (u, _, _, h) =>
        {
            u["mode"] = (float)mode; u["height"] = (float)h;
        })
        ?? Resample(s, a, 0, 0, (src, tmp, w, h, rb) => { PixelFx.Deinterlace(src, tmp, w, h, rb, mode); return tmp; });

    // ---------------- 半径方向のゆがみ・ミラータイル・マップでずらす ----------------

    private static readonly Sksl RadialWarpShader = new(SampleSksl + """
        uniform float2 center;
        uniform float radius;
        uniform float mode;     // 0 = 渦巻き (ねじるだけ), 1 = 膨張・収縮 (d^power), 2 = 魚眼 (d (1 - k + k d²))
        uniform float param;    // 渦巻きの角度・膨張の power・魚眼の k
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float2 o = q - center;
            float r = length(o);
            if (r >= radius || r < 1e-4) return out8(fetch(q));
            float d = r / radius;
            float sd = mode == 1 ? pow(d, param) : mode == 2 ? d * (1 - param + param * d * d) : d;
            float2 so = o * (sd / d);
            if (mode == 0) {
                float an = param * (1 - d) * (1 - d), cs = cos(an), sn = sin(an);
                so = float2(so.x * cs - so.y * sn, so.x * sn + so.y * cs);
            }
            return out8(sample8r(center + so));
        }
        """);

    /// <summary>半径方向のゆがみ (EffectsFilters.RadialWarp と同じ。絵の外は透明)。GPU 描画でない・使えないときは null。</summary>
    internal static Sprite? RadialWarpOnGpu(Sprite s, EffectArgs a, float cx, float cy, float radius, int mode, float param) =>
        MultiPassOnGpu(s, a, 0, 0, RadialWarpShader, SKShaderTileMode.Decal, SKShaderTileMode.Decal, 1, (u, _, _, _) =>
        {
            u["center"] = new[] { cx, cy }; u["radius"] = radius; u["mode"] = (float)mode; u["param"] = param;
        });

    private static readonly Sksl MirrorTileShader = new(SampleSksl + """
        uniform float2 count;
        uniform float mirror;
        uniform float2 size;
        half4 main(float2 p) {
            float2 uv = p / size * count;
            float2 t = floor(uv);
            float2 f = uv - t;
            if (mirror > 0) {
                if (mod(t.x, 2) == 1) f.x = 1 - f.x;
                if (mod(t.y, 2) == 1) f.y = 1 - f.y;
            }
            return out8(sample8r(f * size - 0.5));
        }
        """);

    /// <summary>ミラータイル (EffectsFilters.MirrorTile と同じ。絵の外は端のピクセル)。</summary>
    internal static Sprite? MirrorTileOnGpu(Sprite s, EffectArgs a, int nx, int ny, bool mirror) =>
        MultiPassOnGpu(s, a, 0, 0, MirrorTileShader, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, 1, (u, _, w, h) =>
        {
            u["count"] = new float[] { nx, ny }; u["mirror"] = mirror ? 1f : 0f; u["size"] = new float[] { w, h };
        });

    private static readonly Sksl DisplaceMapShader = new(SampleSksl + """
        uniform shader map;
        uniform float2 amount;   // (mx, my)
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 m = floor(float4(map.eval(p)) * 255 + 0.5);
            if (m.a == 0) return out8(fetch(q));
            // 乗算済みを戻して、128 からの差をずれにする (地図が半透明なら、そのぶん弱める)
            float r = min(255, m.r * 255 / m.a), g = min(255, m.g * 255 / m.a);
            float k = m.a / 255;
            return out8(sample8r(float2(q.x - (r - 128) / 128 * amount.x * k, q.y - (g - 128) / 128 * amount.y * k)));
        }
        """);

    /// <summary>マップの絵の色でずらす (PixelFx.DisplaceByMap と同じ)。map は絵と同じ大きさ。</summary>
    internal static Sprite? DisplaceByMapOnGpu(Sprite s, EffectArgs a, SKImage map, float mx, float my, bool clamp)
    {
        using var mapShader = map.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        return MultiPassOnGpu(s, a, 0, 0, DisplaceMapShader, Tile(clamp), Tile(clamp), 1, (u, _, _, _) => u["amount"] = new[] { mx, my },
            kids => kids["map"] = mapShader);
    }
    // ---------------- CPU で作った値を送る画像 ----------------

    /// <summary>
    /// ValueImage の値を読む SkSL。値は 0〜1 未満を 24 ビットにしたもの (赤が上位)。
    /// SkSL には整数のビット演算がなく PixelFx.Hash を GPU で同じに計算できないので、乱数などは CPU で作って画像で送る。
    /// </summary>
    private const string Val24Sksl = """
        float val24(half4 c) { float3 b = floor(float3(c.rgb) * 255 + 0.5); return (b.r * 65536 + b.g * 256 + b.b) / 16777216; }
        float idx24(half4 c) { float3 b = floor(float3(c.rgb) * 255 + 0.5); return b.r * 65536 + b.g * 256 + b.b; }
        """;

    /// <summary>
    /// 整数 (0〜2^24 - 1) を持つ、横 1 列の画像。idx24 で読む。
    /// 「x / 幅 の切り捨て」のような区切りは、GPU では割り算が逆数の掛け算になり境目ちょうどで 1 つずれることがあるので、CPU で求めて送る。
    /// </summary>
    private static SKImage? IndexImage(int n, Func<int, int> index) => ValueImage(n, 1, (x, _) => index(x) / 16777216f);

    /// <summary>value(x, y) (0〜1) を 24 ビットにして持つ、幅 w・高さ h の画像 (不透明。GPU へ送るのは待たない)。</summary>
    private static SKImage? ValueImage(int w, int h, Func<int, int, float> value)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var px = new byte[w * h * 4];
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = value(x, y);
                    uint q = v >= 1 ? 16777215u : v <= 0 ? 0u : Math.Min(16777215u, (uint)(v * 16777216f));
                    int i = (y * w + x) * 4;
                    px[i] = (byte)(q >> 16); px[i + 1] = (byte)(q >> 8); px[i + 2] = (byte)q; px[i + 3] = 255;
                }
        });
        return SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), px);
    }

    // ---------------- VHS ----------------

    private static readonly Sksl VhsShader = new(SampleSksl + Val24Sksl + """
        uniform shader rows;    // x = 0: 行のずれ (range で戻す), x = 1: ノイズの帯の中なら 0.5 以上
        uniform shader rnd;     // 画素ごとの乱数 (Rand01 の salt 5)
        uniform shader band;    // 帯の乱数 (横 3 画素ごと。salt 6)
        uniform float range;
        uniform float shift;
        uniform float sat;
        uniform float noise;
        uniform float k;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float off = (val24(rows.eval(float2(0.5, p.y))) - 0.5) * range;
            float inBand = val24(rows.eval(float2(1.5, p.y)));
            float sx = q.x - off;
            float4 c = sample8r(float2(sx, q.y));
            float4 r1 = sample8r(float2(sx - shift, q.y)), r2 = sample8r(float2(sx - shift * 2.5, q.y));
            float4 b1 = sample8r(float2(sx + shift, q.y)), b2 = sample8r(float2(sx + shift * 2.5, q.y));
            float al = c.a;
            float r = (r1.r + r2.r) / 2, g = c.g, b = (b1.b + b2.b) / 2;
            float l = 0.3 * r + 0.59 * g + 0.11 * b;
            r = l + (r - l) * sat; g = l + (g - l) * sat; b = l + (b - l) * sat;
            float n = 0;
            if (noise > 0) {
                float rv = val24(rnd.eval(p));
                n = (rv - 0.5) * 40 * noise * k;
                if (inBand >= 0.5) n += 120 * noise * val24(band.eval(float2(floor(p.x / 3) + 0.5, p.y)));
                else if (rv > 1 - 0.0015 * noise) n += 200;
            }
            float3 o = floor(clamp(float3(r, g, b) + n, 0, al));
            return half4(half3(o / 255), half(al / 255));
        }
        """);

    /// <summary>
    /// VHS (EffectsFilters.Vhs と同じ)。行のずれ off(y)・帯 inBand(y) と乱数は CPU で作って画像で送り、読む・混ぜるのは GPU で。
    /// GPU 描画でない・使えないときは null。
    /// </summary>
    internal static Sprite? VhsOnGpu(Sprite s, EffectArgs a, Func<int, float> off, Func<int, bool> inBand, float shift, float sat, float noise, float k, uint seed)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || VhsShader.Effect == null) return null;
        int w = s.Width, h = s.Height;
        var offs = new float[h];
        var bands = new bool[h];
        float max = 0;
        for (int y = 0; y < h; y++) { offs[y] = off(y); bands[y] = inBand(y); max = Math.Max(max, MathF.Abs(offs[y])); }
        float range = max * 2 + 2;
        using var rows = ValueImage(2, h, (x, y) => x == 0 ? offs[y] / range + 0.5f : bands[y] ? 0.75f : 0);
        using var rnd = noise > 0 ? ValueImage(w, h, (x, y) => Rand01((uint)(y * 8191 + x), seed, 5)) : ValueImage(1, 1, (_, _) => 0);
        using var band = noise > 0 && bands.Any(b => b)
            ? ValueImage((w + 2) / 3, h, (x, y) => bands[y] ? Rand01((uint)(y * 131 + x), seed, 6) : 0)
            : ValueImage(1, 1, (_, _) => 0);
        if (rows == null || rnd == null || band == null) return null;
        using var rowsShader = rows.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var rndShader = rnd.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var bandShader = band.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        return MultiPassOnGpu(s, a, 0, 0, VhsShader, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, 1, (u, _, _, _) =>
        {
            u["range"] = range; u["shift"] = shift; u["sat"] = sat; u["noise"] = noise; u["k"] = k;
        }, kids => { kids["rows"] = rowsShader; kids["rnd"] = rndShader; kids["band"] = bandShader; });
    }
    // ---------------- ブラウン管 ----------------

    private static readonly Sksl CrtShader = new(SampleSksl + Val24Sksl + """
        uniform shader cols;    // 列ごとの RGB の格子の色 (0〜2)
        uniform float2 size;
        uniform float scan;
        uniform float mask;
        uniform float curve;
        uniform float vig;
        uniform float pitch;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float cx = size.x / 2, cy = size.y / 2;
            float nx = (q.x - cx) / cx, ny = (q.y - cy) / cy, r2 = nx * nx + ny * ny;
            float k = 1 + curve * 0.12 * r2;
            float sx = cx + nx * k * cx, sy = cy + ny * k * cy;
            if (sx < -0.5 || sy < -0.5 || sx > size.x - 0.5 || sy > size.y - 0.5) return half4(0, 0, 0, half(fetch(q).a / 255));
            float4 c = sample8r(float2(sx, sy));
            float al = c.a;
            float sl = abs(sin(3.14159265 * sy / pitch));
            float line = 1 - scan * 0.55 * sl * sl;
            float col = idx24(cols.eval(float2(p.x, 0.5)));
            float dim = 1 - mask * 0.55;
            float3 m = float3(col == 0 ? 1 : dim, col == 1 ? 1 : dim, col == 2 ? 1 : dim);
            float v = 1 - vig * 0.6 * pow(r2 / 2, 1.5);
            float boost = 1 + 0.25 * (scan + mask) / 2;
            float3 o = floor(clamp(c.rgb * line * m * v * boost, 0, al));
            return half4(half3(o / 255), half(al / 255));
        }
        """);

    /// <summary>ブラウン管 (EffectsFilters.Crt と同じ)。GPU 描画でない・使えないときは null。</summary>
    internal static Sprite? CrtOnGpu(Sprite s, EffectArgs a, float scan, float mask, float curve, float vig, float pitch)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || CrtShader.Effect == null) return null;
        using var cols = IndexImage(s.Width, x => (int)(x / (pitch / 3)) % 3);
        if (cols == null) return null;
        using var colShader = cols.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        return MultiPassOnGpu(s, a, 0, 0, CrtShader, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, 1, (u, _, w, h) =>
        {
            u["size"] = new float[] { w, h }; u["scan"] = scan; u["mask"] = mask; u["curve"] = curve; u["vig"] = vig; u["pitch"] = pitch;
        }, kids => kids["cols"] = colShader);
    }

    // ---------------- グリッチ ----------------

    private static readonly Sksl GlitchShader = new(SampleSksl + Val24Sksl + """
        uniform shader cols;    // 列ごとのブロックの番号 (横)
        uniform shader rows;    // 行ごとのブロックの番号 (縦)
        uniform shader offs;    // ブロックごとのずれ (range で戻す)
        uniform shader invs;    // ブロックごとの反転 (0.5 以上なら反転)
        uniform shader rowOffs; // ブロックの行ごとのずれ
        uniform float range;
        uniform float split;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float bx = idx24(cols.eval(float2(p.x, 0.5))), by = idx24(rows.eval(float2(0.5, p.y)));
            float2 b = float2(bx, by) + 0.5;
            float off = (val24(offs.eval(b)) - 0.5) * range;
            float rowOff = (val24(rowOffs.eval(float2(0.5, b.y))) - 0.5) * range;
            float sx = q.x - off - rowOff;
            float4 r = sample8r(float2(sx - split, q.y)), c = sample8r(float2(sx, q.y)), bl = sample8r(float2(sx + split, q.y));
            float al = max(c.a, max(r.a, bl.a));
            float3 o = float3(r.r, c.g, bl.b);
            // まれに色を反転したブロック (どの色もアルファ以下なので、桁は借りない)
            if (val24(invs.eval(b)) >= 0.5) o = al - o;
            return half4(half3(o / 255), half(al / 255));
        }
        """);

    /// <summary>
    /// グリッチ (EffectsFilters.Glitch と同じ)。ブロックごとの乱数は CPU で作って画像で送る。
    /// off(bx, by)・rowOff(by)・inverted(bx, by) はブロックの番号から。GPU 描画でない・使えないときは null。
    /// </summary>
    internal static Sprite? GlitchOnGpu(Sprite s, EffectArgs a, float block, float split, Func<int, int, float> off, Func<int, float> rowOff, Func<int, int, bool> inverted)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || GlitchShader.Effect == null) return null;
        int w = s.Width, h = s.Height;
        int nbx = (int)((w - 1) / (block * 3)) + 1, nby = (int)((h - 1) / block) + 1;
        var o = new float[nbx * nby];
        var ro = new float[nby];
        float max = 0;
        for (int by = 0; by < nby; by++)
        {
            ro[by] = rowOff(by); max = Math.Max(max, MathF.Abs(ro[by]));
            for (int bx = 0; bx < nbx; bx++) { o[by * nbx + bx] = off(bx, by); max = Math.Max(max, MathF.Abs(o[by * nbx + bx])); }
        }
        float range = max * 2 + 2;
        using var cols = IndexImage(w, x => (int)(x / (block * 3)));
        using var rows = ValueImage(1, h, (_, y) => (int)(y / block) / 16777216f);
        using var offs = ValueImage(nbx, nby, (x, y) => o[y * nbx + x] / range + 0.5f);
        using var invs = ValueImage(nbx, nby, (x, y) => inverted(x, y) ? 0.75f : 0);
        using var rowOffs = ValueImage(1, nby, (_, y) => ro[y] / range + 0.5f);
        if (cols == null || rows == null || offs == null || invs == null || rowOffs == null) return null;
        using var c1 = cols.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var c2 = rows.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var c3 = offs.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var c4 = invs.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var c5 = rowOffs.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        return MultiPassOnGpu(s, a, 0, 0, GlitchShader, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, 1, (u, _, _, _) =>
        {
            u["range"] = range; u["split"] = split;
        }, kids => { kids["cols"] = c1; kids["rows"] = c2; kids["offs"] = c3; kids["invs"] = c4; kids["rowOffs"] = c5; });
    }

    // ---------------- 網点 ----------------

    private static readonly Sksl HalftoneShader = new(SampleSksl + """
        uniform float size;
        uniform float mode;
        uniform float4 cs;   // 網点の角度の cos (mode 2 は C・M・Y・K の順。ほかは x だけ)
        uniform float4 sn;
        float3 un(float4 q) { return q.a == 0 ? float3(0) : q.rgb / q.a; }
        // kind: 0 = 明るさ, 1〜3 = C・M・Y, 4 = K
        float dotAt(float2 xy, float c, float s, float kind) {
            float u = xy.x * c + xy.y * s, v = -xy.x * s + xy.y * c;
            float cu = (floor(u / size) + 0.5) * size, cv = (floor(v / size) + 0.5) * size;
            float3 q = un(sample8r(float2(cu * c - cv * s, cu * s + cv * c)));
            float amount;
            if (kind == 0) amount = 1 - (0.2126 * q.r + 0.7152 * q.g + 0.0722 * q.b);
            else {
                float k0 = 1 - max(q.r, max(q.g, q.b));
                float ch = kind == 1 ? q.r : kind == 2 ? q.g : q.b;
                amount = kind == 4 ? k0 * 0.9 : k0 >= 1 ? 0 : (1 - ch - k0) / (1 - k0);
            }
            float radius = size * 0.71 * sqrt(clamp(amount, 0, 1));
            float d = sqrt((u - cu) * (u - cu) + (v - cv) * (v - cv));
            return clamp(radius - d + 0.5, 0, 1);
        }
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 orig = fetch(q);
            float al = orig.a;
            if (al == 0) return half4(0);
            float3 o;
            if (mode == 2) {
                float c = dotAt(q, cs.x, sn.x, 1), m = dotAt(q, cs.y, sn.y, 2), y = dotAt(q, cs.z, sn.z, 3), k = dotAt(q, cs.w, sn.w, 4);
                o = float3((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
            } else {
                float d = dotAt(q, cs.x, sn.x, 0);
                o = mode == 1 ? 1 - d + d * un(orig) : float3(1 - d);
            }
            return half4(half3(floor(clamp(o, 0, 1) * al) / 255), half(al / 255));
        }
        """);

    /// <summary>網点 (EffectsFilters.Halftone と同じ)。GPU 描画でない・使えないときは null。</summary>
    internal static Sprite? HalftoneOnGpu(Sprite s, EffectArgs a, float size, float angle, int mode)
    {
        float[] angles = mode == 2 ? [15 * MathF.PI / 180, 75 * MathF.PI / 180, 0, 45 * MathF.PI / 180] : [angle, 0, 0, 0];
        return MultiPassOnGpu(s, a, 0, 0, HalftoneShader, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, 1, (u, _, _, _) =>
        {
            u["size"] = size; u["mode"] = (float)mode;
            u["cs"] = angles.Select(MathF.Cos).ToArray(); u["sn"] = angles.Select(MathF.Sin).ToArray();
        });
    }
    // ---------------- ドット絵化 ----------------

    private static readonly Sksl PixelArtShader = new(Val24Sksl + """
        uniform shader image;
        uniform shader bayer;   // 4 × 4 のディザの表 (値 / 16)
        uniform float cell;
        uniform float2 size;
        uniform float levels;
        uniform float dither;
        // MathF.Round と同じ (ちょうど半分なら偶数へ)
        float roundHalfEven(float x) { float f = floor(x), d = x - f; return d > 0.5 ? f + 1 : d < 0.5 ? f : mod(f, 2) == 0 ? f : f + 1; }
        float quant(float v, float th) { return clamp(roundHalfEven(v * (levels - 1) + th), 0, levels - 1) / (levels - 1); }
        half4 main(float2 p) {
            float2 o = floor(p) * cell;
            float2 e = min(size, o + cell);
            float4 sum = float4(0);
            for (int j = 0; j < 32; j++) {
                float y = o.y + float(j);
                if (y >= e.y) break;
                for (int i = 0; i < 32; i++) {
                    float x = o.x + float(i);
                    if (x >= e.x) break;
                    sum += floor(float4(image.eval(float2(x, y) + 0.5)) * 255 + 0.5);
                }
            }
            float n = (e.x - o.x) * (e.y - o.y);
            // アルファの平均 (切り捨て)。半透明の縁は、不透明か透明かに分ける
            float al = floor((sum.a + 0.5) / n) >= 128 ? 255 : 0;
            float3 c = sum.a > 0 ? sum.rgb / sum.a : float3(0);
            float th = dither > 0 ? val24(bayer.eval(mod(floor(p), 4) + 0.5)) - 0.5 : 0;
            float3 qc = float3(quant(c.r, th), quant(c.g, th), quant(c.b, th));
            return half4(half3(floor(qc * al) / 255), half(al / 255));
        }
        """);

    // 大きなドット (BigCellSums の合計から)
    private static readonly Sksl BigPixelArtShader = new(Val24Sksl + BigCellSksl + """
        uniform shader bayer;
        uniform float levels;
        uniform float dither;
        float roundHalfEven(float x) { float f = floor(x), d = x - f; return d > 0.5 ? f + 1 : d < 0.5 ? f : mod(f, 2) == 0 ? f : f + 1; }
        float quant(float v, float th) { return clamp(roundHalfEven(v * (levels - 1) + th), 0, levels - 1) / (levels - 1); }
        half4 main(float2 p) {
            float4 h = his(p), l = los(p);
            float al = divHL(h.a, l.a, cellCount(p)) >= 128 ? 255 : 0;
            // CPU と同じく、合計を浮動小数点にしてから割る
            float4 sum = h * 4096 + l;
            float3 c = sum.a > 0 ? sum.rgb / sum.a : float3(0);
            float th = dither > 0 ? val24(bayer.eval(mod(floor(p), 4) + 0.5)) - 0.5 : 0;
            float3 qc = float3(quant(c.r, th), quant(c.g, th), quant(c.b, th));
            return half4(half3(floor(qc * al) / 255), half(al / 255));
        }
        """);

    private static SKImage? _bayer;

    /// <summary>ドット絵化 (EffectsFilters.PixelArt と同じ)。GPU 描画でない・ドットが大きすぎるときは null。</summary>
    internal static Sprite? PixelArtOnGpu(Sprite s, EffectArgs a, int size, int levels, bool dither, float[] bayer4)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled) return null;
        var bayer = _bayer ??= ValueImage(4, 4, (x, y) => bayer4[y * 4 + x] / 16f);
        if (bayer == null) return null;
        using var bayerShader = bayer.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        return CellsOnGpu(s, a, size, PixelArtShader, u => { u["levels"] = (float)levels; u["dither"] = dither ? 1f : 0f; }, kids => kids["bayer"] = bayerShader, BigPixelArtShader);
    }

    // ---------------- 24 ビットの値を描画先に持つ処理 ----------------

    /// <summary>
    /// 0〜1 の値を、描画先 (8 ビットの RGBA) の RGB に 24 ビットで持たせる SkSL (val24 で読む)。アルファは 1 なので乗算済みでも崩れない。
    /// Skia の GPU 描画 (GLES) では浮動小数点 32 ビットの描画先を作れず、16 ビットでは精度が足りないため。
    /// </summary>
    private const string Enc24Sksl = """
        half4 enc24(float v) {
            float q = min(16777215, floor(clamp(v, 0, 1) * 16777216));
            float r = floor(q / 65536), g = floor((q - r * 65536) / 256), b = q - r * 65536 - g * 256;
            return half4(half3(float3(r, g, b) / 255), 1);
        }
        """;

    /// <summary>effect を w × h の GPU の描画先に描いた画像。作れなければ null。</summary>
    private static SKImage? RunOnGpu(EffectArgs a, int w, int h, SKRuntimeEffect effect, Action<SKRuntimeEffectUniforms> uniforms, Action<SKRuntimeEffectChildren> children)
    {
        using var surface = a.Context.CreateSurface(w, h);
        if (surface == null) return null;
        var kids = new SKRuntimeEffectChildren(effect);
        children(kids);
        var u = new SKRuntimeEffectUniforms(effect);
        uniforms(u);
        using var shader = effect.ToShader(u, kids);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawRect(SKRect.Create(w, h), paint);
        return surface.Snapshot();
    }

    // ---------------- スケッチ ----------------

    private static readonly Sksl SketchLumShader = new(Enc24Sksl + """
        uniform shader image;
        half4 main(float2 p) {
            float4 c = floor(float4(image.eval(p)) * 255 + 0.5);
            float lum = c.a == 0 ? 1 : (0.299 * c.r + 0.587 * c.g + 0.114 * c.b) / c.a;
            return enc24(1 - lum);
        }
        """);

    /// <summary>EffectsFilters.BoxBlur と同じ箱型のぼかし (端は端の値を繰り返す)。半径 512 まで (それより大きいときは BoxBlurChunkShader)。</summary>
    private static readonly Sksl BoxBlur24Shader = new(Val24Sksl + Enc24Sksl + """
        uniform shader src;
        uniform float r;
        uniform float2 dir;
        uniform float k;
        half4 main(float2 p) {
            float sum = 0;
            for (int i = 0; i < 1025; i++) {
                float d = float(i) - r;
                if (d > r) break;
                sum += val24(src.eval(p + dir * d));
            }
            return enc24(sum * k);
        }
        """);

    /// <summary>
    /// 半径が 512 より大きい箱型のぼかしの 1 回ぶん (BoxBlur24Shader と同じ結果)。絵の中の範囲 [t - r, t + r] を 1024 個ずつの塊に分け、
    /// chunk 番目の塊の合計 × k を acc に足す。絵の外 (端の値を繰り返す所) は、chunk 0 で端の値 × 数を足す。
    /// </summary>
    private static readonly Sksl BoxBlurChunkShader = new(Val24Sksl + Enc24Sksl + """
        uniform shader src;
        uniform shader acc;
        uniform float r;
        uniform float k;
        uniform float n;        // 並びの長さ
        uniform float2 dir;
        uniform float chunk;
        half4 main(float2 p) {
            float2 q = floor(p);
            float t = dot(q, dir), other = dot(q, 1 - dir);
            float a = max(0, t - r), b = min(n - 1, t + r);
            float o = a + chunk * 1024, e = min(b, o + 1023);
            float sum = 0;
            for (int i = 0; i < 1024; i++) {
                float u = o + float(i);
                if (u > e) break;
                sum += val24(src.eval(dir * u + (1 - dir) * other + 0.5));
            }
            if (chunk == 0) {
                sum += max(0, r - t) * val24(src.eval((1 - dir) * other + 0.5));
                sum += max(0, t + r - (n - 1)) * val24(src.eval(dir * (n - 1) + (1 - dir) * other + 0.5));
            }
            return enc24(val24(acc.eval(p)) + sum * k);
        }
        """);

    private static readonly Sksl SketchShader = new(Val24Sksl + """
        uniform shader image;
        uniform shader inv;
        uniform float strength;
        uniform float colored;
        uniform float3 paper;
        half4 main(float2 p) {
            float4 c = floor(float4(image.eval(p)) * 255 + 0.5);
            float al = c.a;
            if (al == 0) return half4(0);
            float lum = (0.299 * c.r + 0.587 * c.g + 0.114 * c.b) / al;
            float v = min(1, lum / max(0.02, 1 - val24(inv.eval(p))));
            v = pow(v, 1 + strength * 2);
            float3 o = v * paper / 255;
            if (colored > 0) o *= 0.55 + 0.45 * c.rgb / al;
            return half4(half3(floor(clamp(o, 0, 1) * al) / 255), half(al / 255));
        }
        """);

    /// <summary>スケッチ (EffectsFilters.Sketch と同じ)。GPU 描画でない・使えないときは null。</summary>
    internal static Sprite? SketchOnGpu(Sprite s, EffectArgs a, float strength, bool color, SKColor paper, int radius)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled) return null;
        if (SketchLumShader.Effect is not { } lumFx || BoxBlur24Shader.Effect is not { } blurFx || BoxBlurChunkShader.Effect is not { } chunkFx
            || SketchShader.Effect == null) return null;
        int w = s.Width, h = s.Height;
        SKImage? cur;
        SKShader? invShader = null;
        using (var image = s.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp))
            cur = RunOnGpu(a, w, h, lumFx, _ => { }, kids => kids["image"] = image);
        try
        {
            for (int pass = 0; pass < 6 && cur != null; pass++)
            {
                using var src = cur.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                bool horizontal = pass % 2 == 0;
                var dir = horizontal ? new float[] { 1, 0 } : new float[] { 0, 1 };
                SKImage? next;
                if (radius <= 512)
                    next = RunOnGpu(a, w, h, blurFx, u => { u["r"] = (float)radius; u["dir"] = dir; u["k"] = 1f / (2 * radius + 1); }, kids => kids["src"] = src);
                else
                {
                    // 大きな半径: 絵の中の範囲を 1024 個ずつの塊に分けて、順に足す (ぼかしの半径に上限がない)
                    int n = horizontal ? w : h, chunks = (Math.Min(2 * radius + 1, n) + 1023) / 1024;
                    next = ValueImage(1, 1, (_, _) => 0);
                    for (int c = 0; c < chunks && next != null; c++)
                    {
                        using var accShader = next.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                        float chunk = c;
                        var sum = RunOnGpu(a, w, h, chunkFx, u =>
                        {
                            u["r"] = (float)radius; u["k"] = 1f / (2 * radius + 1); u["n"] = (float)n; u["dir"] = dir; u["chunk"] = chunk;
                        }, kids => { kids["src"] = src; kids["acc"] = accShader; });
                        next.Dispose();
                        next = sum;
                    }
                }
                cur.Dispose();
                cur = next;
            }
            if (cur == null) return null;
            return ApplyGpuShader(s, a, SketchShader, u =>
            {
                u["strength"] = strength; u["colored"] = color ? 1f : 0f; u["paper"] = new float[] { paper.Red, paper.Green, paper.Blue };
            }, (kids, x, y) =>
            {
                // inv は絵と同じ大きさ。描き直す先の (x, y) に合わせる
                invShader?.Dispose();
                kids["inv"] = invShader = cur.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateTranslation(x, y));
            });
        }
        finally { cur?.Dispose(); invShader?.Dispose(); }
    }

    // ---------------- 油絵・水彩 ----------------

    /// <summary>1 回の描画で扱う筆の大きさの上限 (SkSL のループの大きさの都合。ずれ 16 × 16 は通り、21 × 21 は通らない)。それより大きい筆は BigPaintOnGpu。</summary>
    private const int MaxPaintRadius = 15;

    private static readonly Sksl PaintShader = new(SampleSksl + Val24Sksl + """
        uniform float r;
        uniform float2 size;
        uniform float water;
        """ + VNoiseSksl(1) + """

        float lum(float3 c) { return 0.299 * c.r + 0.587 * c.g + 0.114 * c.b; }
        // 絵の外は数えない (区画は絵の中だけ)
        float4 val(float2 q, float lc) {
            if (q.x < 0 || q.y < 0 || q.x >= size.x || q.y >= size.y) return float4(0);
            float3 c = fetch(q).rgb;
            float dl = lum(c) - lc;
            return float4(c, dl * dl);
        }
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 orig = fetch(q);
            float al = orig.a;
            if (al == 0) return half4(0);
            // 4 つの区画 (左上・右上・左下・右下) の R・G・B と、明るさの 2 乗の合計。
            // 2 乗は真ん中の明るさを引いてから (大きな数どうしの引き算で精度を落とさないように。ばらつきは同じ)
            float lc = lum(orig.rgb);
            float4 s0 = float4(0), s1 = float4(0), s2 = float4(0), s3 = float4(0);
            // 中心からのずれ (dx, dy) ごとに 4 つの区画を 1 画素ずつ足す (窓全体を 1 つのループで回すより、展開したときに小さい)。
            // dx = 0 や dy = 0 の画素は、区画どうしの境目なので両方に入る (CPU と同じ)
            for (int j = 0; j < 16; j++) {
                float dy = float(j);
                if (dy > r) break;
                for (int i = 0; i < 16; i++) {
                    float dx = float(i);
                    if (dx > r) break;
                    s0 += val(q + float2(-dx, -dy), lc); s1 += val(q + float2(dx, -dy), lc);
                    s2 += val(q + float2(-dx, dy), lc); s3 += val(q + float2(dx, dy), lc);
                }
            }
            float wl = q.x - max(0, q.x - r) + 1, wr = min(size.x - 1, q.x + r) - q.x + 1;
            float ht = q.y - max(0, q.y - r) + 1, hb = min(size.y - 1, q.y + r) - q.y + 1;
            float best = 1e30;
            float3 bc = float3(0);
            for (int k = 0; k < 4; k++) {
                float4 sk = k == 0 ? s0 : k == 1 ? s1 : k == 2 ? s2 : s3;
                float n = (mod(float(k), 2) == 0 ? wl : wr) * (k < 2 ? ht : hb);
                float3 m = sk.rgb / n;
                float dm = lum(m) - lc;
                float variance = sk.a / n - dm * dm;
                // 一色の区画はばらつき 0 (CPU と同じく、0 どうしなら先の区画)。GPU によっては計算の順序の違いで ±ごくわずかずれるので、そろえる
                if (abs(variance) < 1.0 / 128) variance = 0;
                if (variance < best) { best = variance; bc = m; }
            }
            if (water > 0) {
                float edge = min(1, sqrt(max(0, best)) / 40);
                float paper = 0.94 + 0.06 * vn0(q * 0.08);
                bc = (bc + (al - bc) * 0.25 - edge * 35) * paper;
            }
            return half4(half3(floor(clamp(bc, 0, al)) / 255), half(al / 255));
        }
        """);

    /// <summary>油絵・水彩 (EffectsFilters.Paint と同じ)。GPU 描画でない・筆が大きすぎるときは null。</summary>
    internal static Sprite? PaintOnGpu(Sprite s, EffectArgs a, int radius, bool water, uint noiseSeed)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled) return null;
        if (radius > MaxPaintRadius) return BigPaintOnGpu(s, a, radius, water, noiseSeed);
        using var lats = new Lattices();
        lats.Add(water ? LatticeImage(0, (s.Width - 1) * 0.08f, 0, (s.Height - 1) * 0.08f, noiseSeed) : (ValueImage(1, 1, (_, _) => 0), 0, 0));
        if (lats.Failed) return null;
        return MultiPassOnGpu(s, a, 0, 0, PaintShader, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, 1, (u, _, w, h) =>
        {
            lats.Set(u);
            u["r"] = (float)radius; u["size"] = new float[] { w, h }; u["water"] = water ? 1f : 0f;
        }, lats.Set);
    }

    // ---------------- フィルム粒子 ----------------

    private static readonly Sksl GrainShader = new(Val24Sksl + """
        uniform shader image;
        uniform float2 off1;    // 2 つ目・3 つ目の ValueNoise の座標のずれ
        uniform float2 off2;
        uniform float size;
        uniform float amount;
        uniform float colored;
        """ + VNoiseSksl(3) + """
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 c = floor(float4(image.eval(p)) * 255 + 0.5);
            float al = c.a;
            if (al == 0) return half4(0);
            float2 v = q / size;
            float l = (c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722) / max(1, al);
            float k = amount * (0.35 + 2.6 * l * (1 - l)) * al;
            float3 o;
            // 色の粒: 青・緑・赤の順に 0・1・2 番の格子 (CPU の画素の並び B・G・R と同じ)
            if (colored > 0) o = float3(c.r + (vn2(v + off2) * 2 - 1) * k, c.g + (vn1(v + off1) * 2 - 1) * k, c.b + (vn0(v) * 2 - 1) * k);
            else o = c.rgb + (vn0(v) + vn1(v + off1) - 1) * k;
            return half4(half3(floor(clamp(o, 0, al)) / 255), half(al / 255));
        }
        """);

    /// <summary>ValueNoise(x / size + ox, y / size + oy, seed) を w × h の絵で使う範囲の格子の乱数の画像と、その左上の格子の番号。</summary>
    private static (SKImage? Image, float X0, float Y0) LatticeImage(int w, int h, float size, float ox, float oy, uint seed) =>
        LatticeImage(ox, (w - 1) / size + ox, oy, (h - 1) / size + oy, seed);

    /// <summary>ValueNoise(x, y, seed) を x ∈ [xmin, xmax]・y ∈ [ymin, ymax] で使うときの格子の乱数の画像と、その左上の格子の番号。</summary>
    private static (SKImage? Image, float X0, float Y0) LatticeImage(float xmin, float xmax, float ymin, float ymax, uint seed)
    {
        int x0 = (int)MathF.Floor(xmin) - 1, y0 = (int)MathF.Floor(ymin) - 1;
        int nx = Math.Clamp((int)MathF.Floor(xmax) - x0 + 3, 1, 8192), ny = Math.Clamp((int)MathF.Floor(ymax) - y0 + 3, 1, 8192);
        return (ValueImage(nx, ny, (i, j) => PixelFx.Hash((uint)(x0 + i), (uint)(y0 + j), seed) / (float)uint.MaxValue), x0, y0);
    }

    /// <summary>
    /// ValueNoise (EffectsTransition) と同じなめらかな乱数 vn0〜vn{count-1} の SkSL。子 lat{k} が格子の乱数 (LatticeImage)、uniform o{k} がその左上の格子の番号。
    /// </summary>
    private static string VNoiseSksl(int count) => string.Concat(Enumerable.Range(0, count).Select(k => $$"""

        uniform shader lat{{k}};
        uniform float2 o{{k}};
        float N{{k}}(float2 ij) { return val24(lat{{k}}.eval(ij - o{{k}} + 0.5)); }
        float vn{{k}}(float2 v) {
            float2 f = floor(v), t = v - f;
            t = t * t * (3 - 2 * t);
            float a = N{{k}}(f), b = N{{k}}(f + float2(1, 0)), c = N{{k}}(f + float2(0, 1)), d = N{{k}}(f + float2(1, 1));
            return a + (b - a) * t.x + (c - a + (d - c - b + a) * t.x) * t.y;
        }
        """));

    /// <summary>LatticeImage の画像を、VNoiseSksl の lat{k}・o{k} に渡すためのもの (使い終わったら Dispose)。</summary>
    private sealed class Lattices : IDisposable
    {
        private readonly List<(SKImage? Image, float X0, float Y0)> _items = new();
        private readonly List<SKShader> _shaders = new();
        public bool Failed { get; private set; }
        public void Add((SKImage? Image, float X0, float Y0) l)
        {
            _items.Add(l);
            if (l.Image == null) Failed = true; else _shaders.Add(l.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp));
        }
        public void Set(SKRuntimeEffectUniforms u) { for (int k = 0; k < _items.Count; k++) u["o" + k] = new[] { _items[k].X0, _items[k].Y0 }; }
        public void Set(SKRuntimeEffectChildren kids) { for (int k = 0; k < _shaders.Count; k++) kids["lat" + k] = _shaders[k]; }
        public void Dispose()
        {
            foreach (var sh in _shaders) sh.Dispose();
            foreach (var l in _items) l.Image?.Dispose();
        }
    }

    /// <summary>フィルム粒子 (EffectsFilters.Grain と同じ)。GPU 描画でない・使えないときは null。</summary>
    internal static Sprite? GrainOnGpu(Sprite s, EffectArgs a, float amount, float size, bool color, uint seed)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || GrainShader.Effect == null) return null;
        int w = s.Width, h = s.Height;
        // 3 つの ValueNoise (乱数の種と座標のずれ)。白黒の粒は 2 つだけ使う
        var specs = color
            ? new (uint Seed, float X, float Y)[] { (seed, 0, 0), (seed + 101, 17.1f, 0), (seed + 202, 2 * 17.1f, 0) }
            : new (uint Seed, float X, float Y)[] { (seed, 0, 0), (seed ^ 0x55u, 31.7f, 11.3f), (0, 0, 0) };
        using var lats = new Lattices();
        for (int k = 0; k < 3; k++)
            lats.Add(!color && k == 2 ? (ValueImage(1, 1, (_, _) => 0), 0, 0) : LatticeImage(w, h, size, specs[k].X, specs[k].Y, specs[k].Seed));
        if (lats.Failed) return null;
        return ApplyGpuShader(s, a, GrainShader, u =>
        {
            lats.Set(u);
            u["off1"] = new[] { specs[1].X, specs[1].Y }; u["off2"] = new[] { specs[2].X, specs[2].Y };
            u["size"] = size; u["amount"] = amount; u["colored"] = color ? 1f : 0f;
        }, (kids, _, _) => lats.Set(kids));
    }

    // ---------------- レンズブラー ----------------

    private static readonly Sksl LensBlurShader = new(SampleSksl + Val24Sksl + """
        uniform shader offs;     // 1 行目: x のずれ、2 行目: y のずれ (固定小数点 1 = 65536、+ 2^23)
        uniform shader weights;  // 明るさ (0〜255) → 重み
        uniform float n;
        uniform float2 size;
        // SampleFixed の補間 (位置の整数部 b と、8 ビットの重み t)
        float4 sampleW(float2 b, float2 t) {
            float4 p00 = fetch(b), p10 = fetch(b + float2(1, 0));
            float4 top = t.x == 0 ? p00 : lerp8(p00, p10, t.x);
            if (t.y == 0) return top;
            float4 p01 = fetch(b + float2(0, 1)), p11 = fetch(b + float2(1, 1));
            return lerp8(top, t.x == 0 ? p01 : lerp8(p01, p11, t.x), t.y);
        }
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float4 sum = float4(0);
            float sw = 0;
            for (int i = 0; i < 128; i++) {
                if (float(i) >= n) break;
                float2 v = float2(idx24(offs.eval(float2(float(i) + 0.5, 0.5))), idx24(offs.eval(float2(float(i) + 0.5, 1.5)))) - 8388608;
                // 算術シフトと同じ (負の数は切り下げ)。65536・256 で割るのは 2 の累乗なので誤差がない
                float2 oi = floor(v / 65536);
                float2 b = q + oi;
                float2 t = floor((v - oi * 65536) / 256);
                float4 c = b.x >= 0 && b.y >= 0 && b.x < size.x - 1 && b.y < size.y - 1 ? sampleW(b, t) : sample8r(q + v / 65536);
                float wt = idx24(weights.eval(float2(floor((c.r * 54 + c.g * 183 + c.b * 19) / 256) + 0.5, 0.5)));
                sum += c * wt;
                sw += wt;
            }
            float hw = floor(sw / 2);
            // (合計 + sw / 2) / sw の切り捨て。+0.5 で、割り算の誤差で境目ちょうどの値が 1 つ下にならないようにする
            float4 m = floor((sum + hw + 0.5) / sw);
            float al = min(255, m.a);
            return half4(half3(min(float3(al), m.rgb) / 255), half(al / 255));
        }
        """);

    /// <summary>
    /// レンズブラー (PixelFx.LensBlur と同じ) を、余白 (pad) を付けた s にかけた絵。半径は 12 以下のときだけ (サンプルが 128 個まで)。
    /// GPU 描画でない・使えないときは null。
    /// </summary>
    internal static Sprite? LensBlurOnGpu(Sprite s, EffectArgs a, int pad, float radius, float boost, bool clamp)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || LensBlurShader.Effect == null) return null;
        if (radius < 0.5f) return s;
        int n = Math.Clamp((int)MathF.Round(radius * radius * 1.2f), 8, 128);
        var ox = new long[n]; var oy = new long[n];
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float r = radius * MathF.Sqrt((i + 0.5f) / n), t = i * golden;
            ox[i] = (long)(Math.Clamp(r * MathF.Cos(t), -1e7f, 1e7f) * 65536f); oy[i] = (long)(Math.Clamp(r * MathF.Sin(t), -1e7f, 1e7f) * 65536f);
            if (Math.Abs(ox[i]) >= 1 << 23 || Math.Abs(oy[i]) >= 1 << 23) return null;
        }
        using var offs = ValueImage(n, 2, (x, y) => ((y == 0 ? ox[x] : oy[x]) + (1 << 23)) / 16777216f);
        using var weights = ValueImage(256, 1, (i, _) => { float l = i / 255f; return (int)(64 * (1 + boost * 8 * l * l * l * l)) / 16777216f; });
        if (offs == null || weights == null) return null;
        using var offShader = offs.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var weightShader = weights.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        return MultiPassOnGpu(s, a, pad, pad, LensBlurShader, Tile(clamp), Tile(clamp), 1, (u, _, w, h) =>
        {
            u["n"] = (float)n; u["size"] = new float[] { w, h };
        }, kids => { kids["offs"] = offShader; kids["weights"] = weightShader; });
    }

    /// <summary>
    /// 縮小してからかけるレンズブラー (EffectsExtra.LensBlur の大きな半径) を GPU で。縮小 (PixelOps.Halve と同じく Skia の Low で半分ずつ) も GPU で行う。
    /// 結果は余白 (pad) を付けて縮小した絵 (呼び出し側で k 倍に広げる)。GPU 描画でない・使えないときは null。
    /// </summary>
    internal static Sprite? SmallLensBlurOnGpu(Sprite s, EffectArgs a, int pad, int k, float radius, float boost, bool clamp)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || LensBlurShader.Effect == null) return null;
        int pw = s.Width + pad * 2, ph = s.Height + pad * 2;
        SKImage? small = null;
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low };
        for (int step = 1; step < k; step *= 2)
        {
            int sw = step == 1 ? (pw + 1) / 2 : (small!.Width + 1) / 2, sh = step == 1 ? (ph + 1) / 2 : (small!.Height + 1) / 2;
            using var surface = a.Context.CreateSurface(sw, sh);
            if (surface == null) { small?.Dispose(); return null; }
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(0.5f);
            if (step == 1) surface.Canvas.DrawImage(s.Image, pad, pad, paint);
            else surface.Canvas.DrawImage(small, 0, 0, paint);
            small?.Dispose();
            small = surface.Snapshot();
        }
        if (small == null) return null;
        var owned = a.Context.Own(small);
        return LensBlurOnGpu(new Sprite(owned, 0, 0), a, 0, radius / k, boost, clamp);
    }
    // ---------------- トランジション: 画素ごとに不透明度を決めるもの ----------------

    /// <summary>EffectsTransition.ScalePixel と同じ (乗算済みの画素に k を掛ける。重みは 8 ビットで切り捨て)。</summary>
    private const string ScalePixelSksl = """
        half4 scalePixel(float4 c, float k) {
            if (k >= 1) return half4(c / 255);
            if (k <= 0) return half4(0);
            return half4(floor(c * floor(k * 256) / 256) / 255);
        }
        """;

    private static readonly Sksl MaskScaleShader = new(Val24Sksl + ScalePixelSksl + """
        uniform shader image;
        uniform shader cols;    // 列ごとの表の横の番号
        uniform shader rows;    // 行ごとの表の縦の番号
        uniform shader table;   // 0〜1 の値 (この値の小さい所から切り替わる)
        uniform float th;
        uniform float soft;
        uniform float invert;
        uniform float leaving;
        half4 main(float2 p) {
            float4 c = floor(float4(image.eval(p)) * 255 + 0.5);
            float2 i = float2(idx24(cols.eval(float2(p.x, 0.5))), idx24(rows.eval(float2(0.5, p.y))));
            float n = val24(table.eval(i + 0.5));
            if (invert > 0) n = 1 - n;
            float k = clamp((th - n) / soft, 0, 1);
            if (leaving > 0) k = 1 - k;
            return scalePixel(c, k);
        }
        """);

    /// <summary>
    /// 画素 (x, y) の値 n = table[row(y)][col(x)] から k = clamp((th - n) / soft) を求めて画素に掛ける (ディゾルブ・ルールワイプ)。
    /// table は 0〜1 の値の ValueImage。GPU 描画でない・使えないときは null。
    /// </summary>
    internal static Sprite? MaskScaleOnGpu(Sprite s, EffectArgs a, Func<int, int> col, Func<int, int> row, SKImage? table, float th, float soft, bool invert, bool leaving)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || MaskScaleShader.Effect == null || table == null) return null;
        using var cols = IndexImage(s.Width, col);
        using var rows = ValueImage(1, s.Height, (_, y) => row(y) / 16777216f);
        if (cols == null || rows == null) return null;
        using var c1 = cols.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var c2 = rows.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var c3 = table.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        return ApplyGpuShader(s, a, MaskScaleShader, u =>
        {
            u["th"] = th; u["soft"] = soft; u["invert"] = invert ? 1f : 0f; u["leaving"] = leaving ? 1f : 0f;
        }, (kids, _, _) => { kids["cols"] = c1; kids["rows"] = c2; kids["table"] = c3; });
    }

    /// <summary>ルール画像の明るさ (0〜1) の ValueImage。画像ごとに 1 つ作って使い回す。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], SKImage> RuleImages = new();

    private static SKImage? RuleImage(byte[] luma, int w, int h)
    {
        if (RuleImages.TryGetValue(luma, out var img)) return img;
        img = ValueImage(w, h, (x, y) => luma[y * w + x] / 255f);
        if (img != null) RuleImages.AddOrUpdate(luma, img);
        return img;
    }

    // ---------------- トランジション: グリッチ ----------------

    private static readonly Sksl GlitchBandShader = new(SampleSksl + Val24Sksl + """
        uniform shader rows;    // 行ごとのずれ (range で戻す)
        uniform float range;
        uniform float split;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float off = (val24(rows.eval(float2(0.5, p.y))) - 0.5) * range;
            float4 r = sample8r(float2(q.x - off - split, q.y)), c = sample8r(float2(q.x - off, q.y)), b = sample8r(float2(q.x - off + split, q.y));
            return half4(half3(float3(r.r, c.g, b.b) / 255), half(max(c.a, max(r.a, b.a)) / 255));
        }
        """);

    /// <summary>グリッチのトランジション (EffectsTransition.Glitch と同じ)。行ごとのずれ off(y) は CPU で。GPU 描画でない・使えないときは null。</summary>
    internal static Sprite? GlitchBandsOnGpu(Sprite s, EffectArgs a, Func<int, float> off, float split, bool clamp)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || GlitchBandShader.Effect == null) return null;
        var offs = new float[s.Height];
        float max = 0;
        for (int y = 0; y < offs.Length; y++) { offs[y] = off(y); max = Math.Max(max, MathF.Abs(offs[y])); }
        float range = max * 2 + 2;
        using var rows = ValueImage(1, s.Height, (_, y) => offs[y] / range + 0.5f);
        if (rows == null) return null;
        using var rowShader = rows.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        return MultiPassOnGpu(s, a, 0, 0, GlitchBandShader, Tile(clamp), Tile(clamp), 1, (u, _, _, _) =>
        {
            u["range"] = range; u["split"] = split;
        }, kids => kids["rows"] = rowShader);
    }

    // ---------------- トランジション: 焼けて消える・フィルム焼け ----------------

    private static readonly Sksl BurnShader = new(Val24Sksl + ScalePixelSksl + """
        uniform shader image;
        uniform float2 origin;   // G の左上
        uniform float scale;
        uniform float th;
        uniform float leaving;
        """ + VNoiseSksl(3) + """
        half4 main(float2 p) {
            const float Band = 0.08;
            float4 c0 = floor(float4(image.eval(p)) * 255 + 0.5);
            float2 v = (p - origin) * scale;
            float n = vn0(v) * 0.55 + vn1(v * 2.3) * 0.3 + vn2(v * 5.1) * 0.15;
            // 焼け跡 (入る側が見える所) は n < th
            float burned = clamp((th - n) / 0.01 + 0.5, 0, 1);
            if (leaving == 0) return scalePixel(c0, burned);
            float4 c = floor(float4(scalePixel(c0, 1 - burned)) * 255 + 0.5);
            float d = (n - th) / Band;
            if (d >= 0 && d < 1 && c.a != 0) {
                // 燃えている縁: 焼け跡に近いほど明るい黄色、離れると暗い茶色
                float heat = 1 - d;
                float fr = 255, fg = 60 + 190 * heat * heat, fb = 20 + 150 * heat * heat * heat;
                if (heat < 0.35) { float k2 = heat / 0.35; fr = 90 + 165 * k2; fg *= k2; fb *= k2; }
                float mx = pow(heat, 0.6);
                float pa = c.a / 255;
                float3 o = floor(c.rgb * (1 - mx) + float3(fr, fg, fb) * pa * mx);
                c.rgb = min(float3(c.a), o);
            }
            return half4(c / 255);
        }
        """);

    /// <summary>焼けて消える (EffectsTransition.Burn と同じ)。GPU 描画でない・使えないときは null。</summary>
    internal static Sprite? BurnOnGpu(Sprite s, EffectArgs a, float gl, float gt, float scale, float th, bool leaving)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || BurnShader.Effect == null) return null;
        float x0 = (0.5f - gl) * scale, x1 = (s.Width - 0.5f - gl) * scale, y0 = (0.5f - gt) * scale, y1 = (s.Height - 0.5f - gt) * scale;
        using var lats = new Lattices();
        lats.Add(LatticeImage(x0, x1, y0, y1, 0xB0));
        lats.Add(LatticeImage(x0 * 2.3f, x1 * 2.3f, y0 * 2.3f, y1 * 2.3f, 0xB1));
        lats.Add(LatticeImage(x0 * 5.1f, x1 * 5.1f, y0 * 5.1f, y1 * 5.1f, 0xB2));
        if (lats.Failed) return null;
        return ApplyGpuShader(s, a, BurnShader, u =>
        {
            lats.Set(u);
            u["origin"] = new[] { gl, gt }; u["scale"] = scale; u["th"] = th; u["leaving"] = leaving ? 1f : 0f;
        }, (kids, _, _) => lats.Set(kids));
    }

    private static readonly Sksl FilmBurnShader = new(Val24Sksl + """
        uniform shader image;
        uniform float2 origin;
        uniform float scale;
        uniform float drift;
        uniform float level;
        """ + VNoiseSksl(2) + """
        half4 main(float2 p) {
            float4 c = floor(float4(image.eval(p)) * 255 + 0.5);
            float al = c.a;
            if (al == 0) return half4(0);
            float2 v = (p - origin) * scale;
            // 左から右へ流れながら、まだらに焼ける
            float n = vn0(float2(v.x - drift, v.y)) * 0.65 + vn1(float2(v.x * 2.7 - drift * 1.5, v.y * 2.7)) * 0.35;
            n = n * 0.8 + (1 - clamp((v.x - drift * 0.3) / 3, 0, 1)) * 0.2;
            float b = clamp((level - n) * 2.5, 0, 1);
            if (b <= 0) return half4(c / 255);
            float3 f = float3(255, 40 + 215 * min(1, b * 1.3), 10 + 245 * max(0, b * 2 - 1));
            float pa = al / 255;
            // スクリーン合成 (明るくなるだけ)
            float3 o = min(float3(al), floor(c.rgb + (f * pa - c.rgb * f / 255) * min(1, b * 1.5)));
            return half4(half3(o / 255), half(al / 255));
        }
        """);

    /// <summary>フィルム焼け (EffectsTransition.FilmBurn と同じ)。GPU 描画でない・使えないときは null。</summary>
    internal static Sprite? FilmBurnOnGpu(Sprite s, EffectArgs a, float gl, float gt, float scale, float drift, float level)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || FilmBurnShader.Effect == null) return null;
        float x0 = (0.5f - gl) * scale, x1 = (s.Width - 0.5f - gl) * scale, y0 = (0.5f - gt) * scale, y1 = (s.Height - 0.5f - gt) * scale;
        using var lats = new Lattices();
        lats.Add(LatticeImage(x0 - drift, x1 - drift, y0, y1, 0xF1));
        lats.Add(LatticeImage(x0 * 2.7f - drift * 1.5f, x1 * 2.7f - drift * 1.5f, y0 * 2.7f, y1 * 2.7f, 0xF2));
        if (lats.Failed) return null;
        return ApplyGpuShader(s, a, FilmBurnShader, u =>
        {
            lats.Set(u);
            u["origin"] = new[] { gl, gt }; u["scale"] = scale; u["drift"] = drift; u["level"] = level;
        }, (kids, _, _) => lats.Set(kids));
    }
    // ---------------- 球体 ----------------

    private static readonly Sksl SphereShader = new(SampleSksl + """
        uniform float2 src;      // 元の絵の大きさ
        uniform float size;      // 描く先 (正方形) の一辺
        uniform float3 m0;       // 画面の向き → 球の向きの回転 (行)
        uniform float3 m1;
        uniform float3 m2;
        uniform float shade;
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float R = size / 2, c = (size - 1) / 2;
            float nx = (q.x - c) / R, ny = (q.y - c) / R;
            float d2 = nx * nx + ny * ny;
            float edge = clamp((1 - sqrt(d2)) * R + 0.5, 0, 1);
            if (edge <= 0) return half4(0);
            float nz = sqrt(max(0, 1 - d2));   // 手前向き
            float px = m0.x * nx + m0.y * ny + m0.z * nz;
            float py = m1.x * nx + m1.y * ny + m1.z * nz;
            float pz = m2.x * nx + m2.y * ny + m2.z * nz;
            float u = atan(px, pz) / (2 * 3.14159265) + 0.5;
            float v = asin(clamp(py, -1, 1)) / 3.14159265 + 0.5;
            // PixelFx.SampleWrapX と同じ (横は回り込み、縦は端)
            float x = u * src.x - 0.5, y = clamp(v * (src.y - 1), 0, src.y - 1);
            x -= floor(x / src.x) * src.x;
            float fx = floor(x), fy = floor(y);
            float x0 = fx >= src.x ? 0 : fx;
            float x1 = x0 + 1 >= src.x ? 0 : x0 + 1, y1 = min(src.y - 1, fy + 1);
            float tx = floor((x - fx) * 256 + 0.5), ty = floor((y - fy) * 256 + 0.5);
            float4 col = lerp8(lerp8(fetch(float2(x0, fy)), fetch(float2(x1, fy)), tx), lerp8(fetch(float2(x0, y1)), fetch(float2(x1, y1)), tx), ty);
            float light = 1 - shade * (1 - nz);
            // 色だけを暗くする (乗算済みなので、色をアルファ以下に保ったまま)
            if (light < 1) col.rgb = lerp8(float4(0), col, floor(clamp(light, 0, 1) * 256)).rgb;
            float a = floor(edge * 256);
            return out8(a >= 256 ? col : lerp8(float4(0), col, a));
        }
        """);

    /// <summary>球体 (PixelFx.Sphere と同じ)。結果は一辺 d の正方形。m は 3x3 の回転 (行優先)。GPU 描画でない・使えないときは null。</summary>
    internal static Sprite? SphereOnGpu(Sprite s, EffectArgs a, int d, float[] m, float shade)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled || SphereShader.Effect is not { } effect) return null;
        using var image = s.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        var img = RunOnGpu(a, d, d, effect, u =>
        {
            u["src"] = new float[] { s.Width, s.Height }; u["size"] = (float)d;
            u["m0"] = new[] { m[0], m[1], m[2] }; u["m1"] = new[] { m[3], m[4], m[5] }; u["m2"] = new[] { m[6], m[7], m[8] }; u["shade"] = shade;
        }, kids => kids["image"] = image);
        return img == null ? null : new Sprite(a.Context.Own(img), d / 2f, d / 2f);
    }
    // ---------------- ASCII アート ----------------

    // マスごとの色と文字を、1 画素 = 1 マスの小さな画像に描く。色 (不透明の RGB) と、文字の番号 × 256 + 不透明度 (enc24) の 2 枚
    private const string AsciiCellSksl = """
        uniform shader image;
        uniform shader xs;      // マス cx の i 番目の点の x (cx × 3 + i 番目)
        uniform shader ys;
        uniform float len;      // 文字の数
        // マスの中の 3×3 点の合計 (乗算済みの 0〜255)
        float4 cellSum(float2 p) {
            float2 c = floor(p);
            float4 sum = float4(0);
            for (int j = 0; j < 3; j++) {
                float y = idx24(ys.eval(float2(c.y * 3 + float(j) + 0.5, 0.5)));
                for (int i = 0; i < 3; i++) {
                    float x = idx24(xs.eval(float2(c.x * 3 + float(i) + 0.5, 0.5)));
                    sum += floor(float4(image.eval(float2(x, y) + 0.5)) * 255 + 0.5);
                }
            }
            return sum;
        }
        // n / d の切り捨て (n・d は整数。+0.5 で、割り算の誤差で境目ちょうどの値が 1 つ下にならないようにする)
        float idiv(float n, float d) { return floor((n + 0.5) / d); }
        """;

    private static readonly Sksl AsciiColorShader = new(Val24Sksl + AsciiCellSksl + """
        half4 main(float2 p) {
            float4 s = cellSum(p);
            if (s.a <= 0) return half4(0, 0, 0, 1);
            return half4(half3(min(float3(255), float3(idiv(s.r * 255, s.a), idiv(s.g * 255, s.a), idiv(s.b * 255, s.a))) / 255), 1);
        }
        """);

    private static readonly Sksl AsciiInfoShader = new(Val24Sksl + Enc24Sksl + AsciiCellSksl + """
        half4 main(float2 p) {
            float4 s = cellSum(p);
            if (s.a <= 0) return enc24(0);
            float l = (0.299 * s.r + 0.587 * s.g + 0.114 * s.b) / s.a;
            float ch = clamp(floor(l * len), 0, len - 1);
            float al = min(255, idiv(s.a, 9));
            return enc24((ch * 256 + al) / 16777216);
        }
        """);

    // 文字を描くときの色: 今描いている行の、その画素の文字を描くマス (同じ組のマスのうち、いちばん近いもの) が、今描いている文字なら、そのマスの色
    private static readonly Sksl AsciiGlyphShader = new(Val24Sksl + """
        uniform shader colors;
        uniform shader infos;
        uniform shader ownerX;  // 行 0・1: 横の組 (偶数・奇数) ごとの、その列を受け持つマスの番号 + 1 (0 はなし)
        uniform float group;    // 横の組 (cx % 2)
        uniform float row;      // 今描いている行
        uniform float glyph;    // 今描いている文字の番号
        half4 main(float2 p) {
            float ox = idx24(ownerX.eval(float2(p.x, group + 0.5)));
            if (ox == 0) return half4(0);
            float2 cell = float2(ox - 0.5, row + 0.5);
            float info = idx24(infos.eval(cell));
            float ch = floor(info / 256), al = info - ch * 256;
            if (ch != glyph || al == 0) return half4(0);
            float a = al / 255;
            return half4(half3(float3(colors.eval(cell).rgb) * a), half(a));
        }
        """);

    /// <summary>
    /// ASCII アート (EffectsFilters.Ascii と同じ) を GPU で。マスごとの色と文字は SkSL で求め、文字は Skia で描く
    /// (行ごと・文字ごと・横の組 (偶数・奇数の列) ごとに、その組のすべてのマスにその文字を描き、色を決めるシェーダーで違う文字のマスを消す)。
    /// 行は上から順に描くので、上下にはみ出して重なる所 (ブロックの文字) は CPU と同じ順。左右に重なる所も、帯で描き分けて CPU と同じ順にする。
    /// Skia は文字の縁の濃さ (文字のガンマ) を塗りの色の明るさから決めるが、色をシェーダーで決めると中間の明るさのものを使うので、
    /// 縁や網目の文字 (░▒▓) の半端な画素が、塗りの色で描く (読み戻す) ときと少し違う。
    /// GPU 描画でない・使えないときは null。
    /// </summary>
    internal static Sprite? AsciiOnGpu(Sprite s, EffectArgs a, int cols, int rows, string ramp, bool color, SKColor bg, SKTypeface face)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled) return null;
        if (AsciiColorShader.Effect is not { } colorFx || AsciiInfoShader.Effect is not { } infoFx || AsciiGlyphShader.Effect is not { } glyphFx) return null;
        float cw = s.Width / (float)cols, ch = s.Height / (float)rows;
        using var xs = IndexImage(cols * 3, k => Math.Min(s.Width - 1, (int)(k / 3 * cw + (k % 3 + 0.5f) * cw / 3)));
        using var ys = IndexImage(rows * 3, k => Math.Min(s.Height - 1, (int)(k / 3 * ch + (k % 3 + 0.5f) * ch / 3)));
        // 列 x を受け持つマス: 画素の中心が入るマスと、その左右のうち、組の同じもの (文字はマスから半分まではみ出してよい)
        int Owner(float center, float cell, int count, int parity)
        {
            int c = (int)MathF.Floor(center / cell);
            if (((c % 2) + 2) % 2 != parity) c += center / cell - c < 0.5f ? -1 : 1;
            return c >= 0 && c < count ? c + 1 : 0;
        }
        using var ownerX = ValueImage(s.Width, 2, (x, g) => Owner(x + 0.5f, cw, cols, g) / 16777216f);
        if (xs == null || ys == null || ownerX == null) return null;
        using var xsShader = xs.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var ysShader = ys.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        SKImage? colors, infos;
        using (var image = s.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp))
        {
            void Kids(SKRuntimeEffectChildren k) { k["image"] = image; k["xs"] = xsShader; k["ys"] = ysShader; }
            colors = RunOnGpu(a, cols, rows, colorFx, u => u["len"] = (float)ramp.Length, Kids);
            infos = RunOnGpu(a, cols, rows, infoFx, u => u["len"] = (float)ramp.Length, Kids);
        }
        using var colorsOwner = colors;
        using var infosOwner = infos;
        if (colors == null || infos == null) return null;
        using var colorShader = colors.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var infoShader = infos.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var oxShader = ownerX.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var font = new SKFont(face, ch * 1.05f);
        return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
        {
            // 背景は絵の見えている所だけ
            using (var bgPaint = new SKPaint { Color = bg })
            using (var layer = PixelBlend.BeginLayer(c, SKRect.Create(x, y, s.Width, s.Height), SKBlendMode.SrcOver))
            {
                layer.Canvas.DrawRect(SKRect.Create(x, y, s.Width, s.Height), bgPaint);
                using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
                PixelBlend.DrawImage(layer.Canvas, s.Image, x, y, keep);
            }
            using var paint = new SKPaint { IsAntialias = true };
            // 左右に重なる所 (偶数の列のマスの左端と、その左の奇数の列の文字の右端が重なる帯)。CPU は左から順に描くので、
            // そこでは奇数の列の文字の上に偶数の列の文字が来る。偶数の列を先に描くと逆になるので、その帯だけ偶数の列を最後に描き直す
            float minLeft = 0, maxRight = cw;
            foreach (char chr in ramp)
            {
                if (chr == ' ') continue;
                font.MeasureText(chr.ToString(), out var b);
                minLeft = MathF.Min(minLeft, b.Left); maxRight = MathF.Max(maxRight, b.Right);
            }
            using var strips = new SKPath();
            for (int cx = 2; cx < cols; cx += 2)
            {
                float l = MathF.Floor(x + cx * cw + minLeft) - 1, r = MathF.Ceiling(x + (cx - 1) * cw + maxRight) + 1;
                if (r > l) strips.AddRect(new SKRect(l, y - s.Height, r, y + s.Height * 2));
            }
            bool overlap = !strips.IsEmpty;
            for (int cy = 0; cy < rows; cy++)
                for (int pass = 0; pass < (overlap ? 3 : 2); pass++)
                {
                    int gx = pass == 1 ? 1 : 0;
                    if (gx >= cols) continue;
                    c.Save();
                    if (overlap && pass != 1) c.ClipPath(strips, pass == 0 ? SKClipOperation.Difference : SKClipOperation.Intersect, false);
                    var pos = new SKPoint[(cols - gx + 1) / 2];
                    for (int i = 0; i < pos.Length; i++) pos[i] = new SKPoint(x + (gx + i * 2) * cw, y + (cy + 0.85f) * ch);
                    for (int k = 0; k < ramp.Length; k++)
                    {
                        if (ramp[k] == ' ') continue;
                        using var blob = SKTextBlob.CreatePositioned(new string(ramp[k], pos.Length), font, pos);
                        if (blob == null) continue;
                        var kids = new SKRuntimeEffectChildren(glyphFx) { ["colors"] = colorShader, ["infos"] = infoShader, ["ownerX"] = oxShader };
                        var u = new SKRuntimeEffectUniforms(glyphFx) { ["group"] = (float)gx, ["row"] = (float)cy, ["glyph"] = (float)k };
                        using var shader = glyphFx.ToShader(u, kids, SKMatrix.CreateTranslation(x, y));
                        paint.Shader = shader;
                        c.DrawText(blob, 0, 0, paint);
                        paint.Shader = null;
                    }
                    c.Restore();
                }
        });
    }
    // ---------------- カーソル点滅 ----------------

    // 横に 32 個ずつの最大 (1 回目は絵のアルファから「描かれている所の x + 1」、2 回目からは前の回の値)
    private static readonly Sksl RowRightShader = new(Val24Sksl + Enc24Sksl + """
        uniform shader src;
        uniform float n;        // 読む側の幅
        uniform float first;    // 1 なら src は絵
        half4 main(float2 p) {
            float o = floor(p.x) * 32, m = 0;
            for (int j = 0; j < 32; j++) {
                float x = o + float(j);
                if (x >= n) break;
                half4 c = src.eval(float2(x + 0.5, p.y));
                float v = first > 0 ? (floor(float(c.a) * 255 + 0.5) > 16 ? x + 1 : 0) : idx24(c);
                m = max(m, v);
            }
            return enc24(m / 16777216);
        }
        """);

    // 縦に 32 個ずつの最大。mode 0: 何か描かれている行の y + 1 (1 回目)、1: bottom より上の、何もない行の y + 1、
    // 2: top〜bottom の行の右端 + 1、3: 前の回の値の最大
    private static readonly Sksl ColumnMaxShader = new(Val24Sksl + Enc24Sksl + """
        uniform shader src;     // 1 列 (行ごとの右端 + 1。0 は何もない)、mode 3 では前の回の値
        uniform shader bottom;  // 1 × 1: いちばん下の描かれている行 + 1
        uniform shader top;     // 1 × 1: 最後の行のまとまりの上端 (= その上の何もない行 + 1)
        uniform float n;
        uniform float mode;
        half4 main(float2 p) {
            float o = floor(p.y) * 32, m = 0;
            float b = mode == 1 || mode == 2 ? idx24(bottom.eval(float2(0.5))) : 0;
            float t = mode == 2 ? idx24(top.eval(float2(0.5))) : 0;
            for (int j = 0; j < 32; j++) {
                float y = o + float(j);
                if (y >= n) break;
                float r = idx24(src.eval(float2(0.5, y + 0.5)));
                float v = mode == 0 ? (r > 0 ? y + 1 : 0)
                        : mode == 1 ? (r == 0 && y + 1 < b ? y + 1 : 0)
                        : mode == 2 ? (y >= t && y < b ? r : 0)
                        : r;
                m = max(m, v);
            }
            return enc24(m / 16777216);
        }
        """);

    private static readonly Sksl CursorBarShader = new(Val24Sksl + """
        uniform shader bottom;
        uniform shader top;
        uniform shader right;
        uniform float2 origin;   // 絵の左上 (描く先の座標)
        uniform float textH;     // テキストの文字の大きさ (絵の座標)。0 ならテキストでない
        uniform float h;         // 絵の高さ
        uniform float cy;        // 絵の中心 (縦)
        uniform float widthPct;
        uniform float4 color;    // 乗算済み
        half4 main(float2 p) {
            float b = idx24(bottom.eval(float2(0.5))), t = idx24(top.eval(float2(0.5))), r = idx24(right.eval(float2(0.5)));
            float lineH = b > 0 ? b - t : h;
            if (textH > 0) lineH = textH;
            lineH = max(2, lineH);
            float barW = max(1, lineH * widthPct / 100), gap = lineH * 0.08;
            float x0 = r > 0 ? r + gap : 0;
            float y1 = b > 0 ? b : cy + lineH / 2, y0 = y1 - lineH;
            // 画素と棒の重なる面積
            float2 q = p - 0.5 - origin;
            float cx = clamp(min(q.x + 1, x0 + barW) - max(q.x, x0), 0, 1), cyv = clamp(min(q.y + 1, y1) - max(q.y, y0), 0, 1);
            return half4(color * cx * cyv);
        }
        """);

    /// <summary>
    /// カーソル点滅 (EffectsAnimation.BlinkingCursor と同じ) を GPU で。最後の行のまとまりと右端は、32 個ずつの最大を何回か取って GPU で求め、
    /// 棒も SkSL で描く (位置を CPU に戻さない)。余白は棒が届きうるいちばん外まで付ける。textH はテキストの文字の大きさ (0 ならテキストでない)。
    /// </summary>
    internal static Sprite? CursorOnGpu(Sprite s, EffectArgs a, float textH, float widthPct, SKColor color)
    {
        if (a.Context.Gpu == null || GpuShadersDisabled) return null;
        if (RowRightShader.Effect is not { } rowFx || ColumnMaxShader.Effect is not { } colFx || CursorBarShader.Effect is not { } barFx) return null;
        int w = s.Width, h = s.Height;
        var owned = new List<IDisposable>();
        try
        {
            SKImage? Keep(SKImage? img) { if (img != null) owned.Add(img); return img; }
            SKShader Sh(SKImage img) { var sh = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp); owned.Add(sh); return sh; }
            // 行ごとの右端 + 1 (1 列になるまで)
            SKImage? cur = null;
            int n = w;
            var srcShader = s.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            owned.Add(srcShader);
            do
            {
                int nn = (n + 31) / 32;
                var src = cur == null ? srcShader : Sh(cur);
                float first = cur == null ? 1 : 0, width = n;
                cur = Keep(RunOnGpu(a, nn, h, rowFx, u => { u["n"] = width; u["first"] = first; }, k => k["src"] = src));
                if (cur == null) return null;
                n = nn;
            } while (n > 1);
            var rows = cur;
            using var empty = ValueImage(1, 1, (_, _) => 0);
            if (empty == null) return null;
            // 縦の最大 (mode の値を 1 × 1 になるまで)
            SKImage? Column(int mode, SKImage? bottom, SKImage? top)
            {
                SKImage? c = rows;
                int m = h, md = mode;
                do
                {
                    int mm = (m + 31) / 32;
                    var src = Sh(c!);
                    var bs = Sh(bottom ?? empty); var ts = Sh(top ?? empty);
                    float count = m, modeNow = md;
                    c = Keep(RunOnGpu(a, 1, mm, colFx, u => { u["n"] = count; u["mode"] = modeNow; }, k => { k["src"] = src; k["bottom"] = bs; k["top"] = ts; }));
                    if (c == null) return null;
                    m = mm; md = 3;
                } while (m > 1);
                return c;
            }
            var bottomImg = Column(0, null, null);
            var topImg = bottomImg == null ? null : Column(1, bottomImg, null);
            var rightImg = topImg == null ? null : Column(2, bottomImg, topImg);
            if (bottomImg == null || topImg == null || rightImg == null) return null;
            // 棒が届きうるいちばん外までの余白 (CPU の処理は棒の位置に合わせるが、GPU では位置を知らないので)
            float lineMax = Math.Max(2, textH > 0 ? textH : h);
            float barMax = Math.Max(1, lineMax * widthPct / 100), gapMax = lineMax * 0.08f;
            int padR = Math.Max(0, (int)MathF.Ceiling(barMax + gapMax) + 1);
            float noneTop = s.Cy + lineMax / 2 - lineMax;
            int padT = Math.Max(0, (int)MathF.Ceiling(Math.Max(-noneTop, textH > 0 ? textH - 1 : 0)));
            var bShader = Sh(bottomImg); var tShader = Sh(topImg); var rShader = Sh(rightImg);
            var premul = new[] { color.Red / 255f * color.Alpha / 255f, color.Green / 255f * color.Alpha / 255f, color.Blue / 255f * color.Alpha / 255f, color.Alpha / 255f };
            return Redraw(s, a, 0, padT, padR, 0, (cv, x, y) =>
            {
                cv.DrawImage(s.Image, x, y);
                var kids = new SKRuntimeEffectChildren(barFx) { ["bottom"] = bShader, ["top"] = tShader, ["right"] = rShader };
                var u = new SKRuntimeEffectUniforms(barFx)
                {
                    ["origin"] = new[] { x, y }, ["textH"] = textH, ["h"] = (float)h, ["cy"] = s.Cy, ["widthPct"] = widthPct, ["color"] = premul,
                };
                using var shader = barFx.ToShader(u, kids);
                using var paint = new SKPaint { Shader = shader };
                cv.DrawRect(SKRect.Create(0, 0, w + x + padR, h + y), paint);
            }) with { Scale = s.Scale };
        }
        finally { foreach (var d in owned) d.Dispose(); }
    }
    // ---------------- 油絵・水彩 (大きな筆) ----------------
    //
    // 区画 (左上・右上・左下・右下) の合計を、総和の表 (左上からの累積和) 4 か所の足し引きで求める (筆の大きさに上限がない)。
    // 足す量は R・G・B と、(明るさ - 128)² × 64 を四捨五入した整数 Q (20 ビット) の 256 進の 3 桁 (どれも 0〜255)。
    // 累積和は最大 16384² × 255 で 24 ビットを超えるので、4096 単位の上位と下位の 2 枚に分けて持つ (どの途中の値も 2^24 未満の整数)。
    // 作り方: 横の累積和 (32 個ずつの塊の合計 → 塊の累積和 → 塊の中を足す。1 行の合計は 16384 × 255 で 24 ビットに収まる)、
    // それを縦に同じように累積する (こちらは上位・下位に分ける)。

    // 足す量 (量の番号 q: 0〜2 = R・G・B、3〜5 = Q の 3 桁 (256 進)。どれも 0〜255)。子 image を読む
    private const string PaintValueSksl = """
        float pv(float4 c, float q) {
            if (q < 3) return q == 0 ? c.r : q == 1 ? c.g : c.b;
            float l = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
            float v = min(1048575, floor((l - 128) * (l - 128) * 64 + 0.5)), va = floor(v / 65536), vb = floor((v - va * 65536) / 256);
            return q == 3 ? va : q == 4 ? vb : v - va * 65536 - vb * 256;
        }
        float4 px8(float2 q) { return floor(float4(image.eval(q)) * 255 + 0.5); }
        """;

    // 横の塊 (32 個) の合計: 出力 (bx, y)
    private static readonly Sksl PaintRowBlockShader = new(Enc24Sksl + """
        uniform shader image;
        uniform float w;
        uniform float q;
        """ + PaintValueSksl + """
        half4 main(float2 p) {
            float2 c = floor(p);
            float sum = 0;
            for (int i = 0; i < 32; i++) {
                float x = c.x * 32 + float(i);
                if (x >= w) break;
                sum += pv(px8(float2(x, c.y) + 0.5), q);
            }
            return enc24(sum / 16777216);
        }
        """);

    // 累積 (自分より前の塊の合計)。axis の向きに、塊の画像 src を 0〜index - 1 まで足す。
    // 2 枚 (上位 hiSrc・下位 loSrc) を足して、part 0 = 下位をそのまま、1 = 上位、2 = 下位 (4096 未満)
    private static Sksl PaintPrefixOf(string output) => new(Val24Sksl + Enc24Sksl + $$"""

        uniform shader hiSrc;
        uniform shader loSrc;
        uniform float2 axis;
        half4 main(float2 p) {
            float2 c = floor(p);
            float idx = dot(c, axis), other = dot(c, 1 - axis), sh = 0, sl = 0;
            for (int i = 0; i < 513; i++) {
                float t = float(i);
                if (t >= idx) break;
                float2 q = axis * t + (1 - axis) * other + 0.5;
                sh += idx24(hiSrc.eval(q));
                sl += idx24(loSrc.eval(q));
            }
            float carry = floor(sl / 4096);
            {{output}}
        }
        """);

    private static readonly Sksl PaintPrefixPlainShader = PaintPrefixOf("return enc24(sl / 16777216);");
    private static readonly Sksl PaintPrefixHiShader = PaintPrefixOf("return enc24((sh + carry) / 16777216);");
    private static readonly Sksl PaintPrefixLoShader = PaintPrefixOf("return enc24((sl - carry * 4096) / 16777216);");

    // 横の累積和 P(x, y) = 塊の累積 + 塊の中の x までの合計 (最大 16384 × 255)
    private static readonly Sksl PaintRowPrefixShader = new(Val24Sksl + Enc24Sksl + """
        uniform shader image;
        uniform shader blocks;  // 塊の累積 (横 bx, 縦 y)
        uniform float q;
        """ + PaintValueSksl + """
        half4 main(float2 p) {
            float2 c = floor(p);
            float bx = floor(c.x / 32);
            float sum = idx24(blocks.eval(float2(bx, c.y) + 0.5));
            for (int i = 0; i < 32; i++) {
                float x = bx * 32 + float(i);
                if (x > c.x) break;
                sum += pv(px8(float2(x, c.y) + 0.5), q);
            }
            return enc24(sum / 16777216);
        }
        """);

    // 縦: 横の累積和 rows を、上から 32 行ずつの塊で合計 (block = 1) するか、塊の累積 (prefix) に塊の中の y までを足す (block = 0)。
    // part 1 = 上位、2 = 下位
    private static readonly Sksl PaintColumnShader = new(Val24Sksl + Enc24Sksl + """
        uniform shader rows;
        uniform shader prefixHi;
        uniform shader prefixLo;
        uniform float h;
        uniform float block;
        uniform float part;
        half4 main(float2 p) {
            float2 c = floor(p);
            float by = block > 0 ? c.y : floor(c.y / 32);
            float last = block > 0 ? min(h - 1, by * 32 + 31) : c.y;
            float sh = block > 0 ? 0 : idx24(prefixHi.eval(float2(c.x, by) + 0.5));
            float sl = block > 0 ? 0 : idx24(prefixLo.eval(float2(c.x, by) + 0.5));
            for (int i = 0; i < 32; i++) {
                float y = by * 32 + float(i);
                if (y > last) break;
                float v = idx24(rows.eval(float2(c.x, y) + 0.5)), vh = floor(v / 4096);
                sh += vh;
                sl += v - vh * 4096;
            }
            float carry = floor(sl / 4096);
            return enc24((part == 1 ? sh + carry : sl - carry * 4096) / 16777216);
        }
        """);

    private static readonly Sksl SatPaintShader = new(Val24Sksl + """
        uniform shader image;
        uniform float r;
        uniform float2 size;
        uniform float water;
        """ + VNoiseSksl(1) + string.Concat(Enumerable.Range(0, 6).Select(k => $$"""

        uniform shader sh{{k}};
        uniform shader sl{{k}};
        // 総和の表の (x, y) (どちらかが -1 なら 0)。上位・下位
        float2 at{{k}}(float x, float y) {
            if (x < 0 || y < 0) return float2(0);
            float2 q = float2(x, y) + 0.5;
            return float2(idx24(sh{{k}}.eval(q)), idx24(sl{{k}}.eval(q)));
        }
        // 長方形 [x0, x1] × [y0, y1] の合計 (上位・下位。下位は 4096 以上・負にもなりうる)
        float2 rect{{k}}(float x0, float x1, float y0, float y1) {
            return at{{k}}(x1, y1) - at{{k}}(x0 - 1, y1) - at{{k}}(x1, y0 - 1) + at{{k}}(x0 - 1, y0 - 1);
        }
        """)) + """

        float lum(float3 c) { return 0.299 * c.r + 0.587 * c.g + 0.114 * c.b; }
        // 画素の数 a × b を、上位 (4096 単位)・下位に分ける
        float2 count(float a, float b) {
            float ah = floor(a / 64), al = a - ah * 64;
            float u = ah * b, t = al * b;
            float uh = floor(u / 64);
            float r = (u - uh * 64) * 64 + t;
            float rh = floor(r / 4096);
            return float2(uh + rh, r - rh * 4096);
        }
        // (hi × 4096 + lo) / n を、商は正確に (整数の差で直す)、端数は浮動小数点で
        float meanHL(float2 v, float2 n) {
            float q = floor((v.x * 4096 + v.y) / (n.x * 4096 + n.y));
            for (int i = 0; i < 3; i++) {
                float d = (v.x - q * n.x) * 4096 + (v.y - q * n.y);
                float e = (v.x - (q + 1) * n.x) * 4096 + (v.y - (q + 1) * n.y);
                if (d < 0) q -= 1; else if (e >= 0) q += 1;
            }
            float rem = (v.x - q * n.x) * 4096 + (v.y - q * n.y);
            return q + rem / (n.x * 4096 + n.y);
        }
        half4 main(float2 p) {
            float2 q = p - 0.5;
            float al = floor(float(image.eval(p).a) * 255 + 0.5);
            if (al == 0) return half4(0);
            float x0 = max(0, q.x - r), x1 = min(size.x - 1, q.x + r), y0 = max(0, q.y - r), y1 = min(size.y - 1, q.y + r);
            float best = 1e30;
            float3 bc = float3(0);
            for (int k = 0; k < 4; k++) {
                // 区画 k: 横は左 [x0, x] か右 [x, x1]、縦は上 [y0, y] か下 [y, y1]
                bool right = mod(float(k), 2) == 1, bottom = k >= 2;
                float ax = right ? q.x : x0, bx = right ? x1 : q.x, ay = bottom ? q.y : y0, by = bottom ? y1 : q.y;
                float2 n = count(bx - ax + 1, by - ay + 1);
                float3 m = float3(meanHL(rect0(ax, bx, ay, by), n), meanHL(rect1(ax, bx, ay, by), n), meanHL(rect2(ax, bx, ay, by), n));
                // (明るさ - 128)² の平均 = Q の平均 / 64
                float eq = (meanHL(rect3(ax, bx, ay, by), n) * 65536 + meanHL(rect4(ax, bx, ay, by), n) * 256 + meanHL(rect5(ax, bx, ay, by), n)) / 64;
                float dm = lum(m) - 128;
                float variance = eq - dm * dm;
                // Q をまるめた分 (1/128 まで) の差は 0 とみなす。一色の区画のばらつきが色によって ±1/128 ずれると、
                // 一色の区画どうし (CPU ではばらつき 0 で並ぶ) で選ぶ区画が CPU と変わるため
                if (abs(variance) < 1.0 / 128) variance = 0;
                if (variance < best) { best = variance; bc = m; }
            }
            if (water > 0) {
                float edge = min(1, sqrt(max(0, best)) / 40);
                float paper = 0.94 + 0.06 * vn0(q * 0.08);
                bc = (bc + (al - bc) * 0.25 - edge * 35) * paper;
            }
            return half4(half3(floor(clamp(bc, 0, al)) / 255), half(al / 255));
        }
        """);

    /// <summary>
    /// 大きな筆の油絵・水彩 (筆の大きさに上限はない)。区画の合計を総和の表で求める。ばらつきの 2 乗の和は 1/64 の単位にまるめるので、
    /// ばらつきがほぼ同じ区画どうしは CPU と違う区画を選ぶことがある。
    /// </summary>
    private static Sprite? BigPaintOnGpu(Sprite s, EffectArgs a, int radius, bool water, uint noiseSeed)
    {
        if (PaintRowBlockShader.Effect is not { } blockFx || PaintPrefixPlainShader.Effect is not { } plainFx || PaintPrefixHiShader.Effect is not { } hiFx
            || PaintPrefixLoShader.Effect is not { } loFx || PaintRowPrefixShader.Effect is not { } rowFx || PaintColumnShader.Effect is not { } colFx
            || SatPaintShader.Effect == null) return null;
        int w = s.Width, h = s.Height, nbx = (w + 31) / 32, nby = (h + 31) / 32;
        var owned = new List<IDisposable>();
        try
        {
            SKShader? Run(int ow, int oh, SKRuntimeEffect fx, Action<SKRuntimeEffectUniforms> uniforms, Action<SKRuntimeEffectChildren> children)
            {
                var img = RunOnGpu(a, ow, oh, fx, uniforms, children);
                if (img == null) return null;
                owned.Add(img);
                var sh2 = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                owned.Add(sh2);
                return sh2;
            }
            var image = s.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            owned.Add(image);
            var zeroImg = ValueImage(1, 1, (_, _) => 0);
            if (zeroImg == null) return null;
            owned.Add(zeroImg);
            var zero = zeroImg.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            owned.Add(zero);
            var sat = new SKShader[12];
            for (int k = 0; k < 6; k++)
            {
                float qk = k;
                // 横の累積和
                var blocks = Run(nbx, h, blockFx, u => { u["w"] = (float)w; u["q"] = qk; }, kids => kids["image"] = image);
                if (blocks == null) return null;
                var blockPrefix = Run(nbx, h, plainFx, u => u["axis"] = new float[] { 1, 0 }, kids => { kids["hiSrc"] = zero; kids["loSrc"] = blocks; });
                if (blockPrefix == null) return null;
                var rows = Run(w, h, rowFx, u => u["q"] = qk, kids => { kids["image"] = image; kids["blocks"] = blockPrefix; });
                if (rows == null) return null;
                // 縦: 32 行ずつの塊の合計 → 塊の累積 → 塊の中を足す (上位・下位)
                SKShader? Column(int oh, bool block, int part, SKShader prefixHi, SKShader prefixLo) =>
                    Run(w, oh, colFx, u => { u["h"] = (float)h; u["block"] = block ? 1f : 0f; u["part"] = (float)part; },
                        kids => { kids["rows"] = rows; kids["prefixHi"] = prefixHi; kids["prefixLo"] = prefixLo; });
                var cbHi = Column(nby, true, 1, zero, zero);
                var cbLo = Column(nby, true, 2, zero, zero);
                if (cbHi == null || cbLo == null) return null;
                var cpHi = Run(w, nby, hiFx, u => u["axis"] = new float[] { 0, 1 }, kids => { kids["hiSrc"] = cbHi; kids["loSrc"] = cbLo; });
                var cpLo = Run(w, nby, loFx, u => u["axis"] = new float[] { 0, 1 }, kids => { kids["hiSrc"] = cbHi; kids["loSrc"] = cbLo; });
                if (cpHi == null || cpLo == null) return null;
                var satHi = Column(h, false, 1, cpHi, cpLo);
                var satLo = Column(h, false, 2, cpHi, cpLo);
                if (satHi == null || satLo == null) return null;
                sat[k * 2] = satHi; sat[k * 2 + 1] = satLo;
            }
            var lats = new Lattices();
            owned.Add(lats);
            lats.Add(water ? LatticeImage(0, (w - 1) * 0.08f, 0, (h - 1) * 0.08f, noiseSeed) : (ValueImage(1, 1, (_, _) => 0), 0, 0));
            if (lats.Failed) return null;
            return ApplyGpuShader(s, a, SatPaintShader, u =>
            {
                lats.Set(u);
                u["r"] = (float)radius; u["size"] = new float[] { w, h }; u["water"] = water ? 1f : 0f;
            }, (kids, _, _) =>
            {
                lats.Set(kids);
                for (int k = 0; k < 6; k++) { kids["sh" + k] = sat[k * 2]; kids["sl" + k] = sat[k * 2 + 1]; }
            });
        }
        finally { foreach (var d in owned) d.Dispose(); }
    }
}
