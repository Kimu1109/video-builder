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
}
