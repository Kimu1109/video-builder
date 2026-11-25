using System.Globalization;
using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// 追加したフィルタ: 色 (LUT・トーンカーブ・色温度・シャドウ / ハイライト・グラデーションマップ・カラープリセット)、
/// 質感 (フィルム粒子・VHS・ブラウン管・グリッチ・ハーフトーン・油絵 / 水彩・スケッチ・ドット絵化・ASCII アート)、
/// 光 (レンズフレア・光漏れ)、変形 (渦巻き・膨張 / 収縮・魚眼・ミラータイル)、生成 (パーティクル・集中線・枠)。
/// 時間・合成のフィルタ (コマ落とし・残像・トラックマット) は描画エンジン (RenderEngine) が扱う。
/// </summary>
public static partial class Effects
{
    private static void RegisterFilters()
    {
        Register(new Lut()); Register(new Curves()); Register(new WhiteBalance()); Register(new ShadowHighlight());
        Register(new GradientMap()); Register(new Look());
        Register(new Grain()); Register(new Vhs()); Register(new Crt()); Register(new GlitchFilter()); Register(new Halftone());
        Register(new Paint()); Register(new Sketch()); Register(new PixelArt()); Register(new Ascii());
        Register(new LensFlare()); Register(new LightLeakFilter());
        Register(new SwirlFilter()); Register(new Bulge()); Register(new Fisheye()); Register(new MirrorTile());
        Register(new Particles()); Register(new SpeedLines()); Register(new Frame());
        // 時間・合成は描画エンジンが扱う (絵そのものは変えない)
        Register(new PassThrough("filter.strobe")); Register(new PassThrough("filter.trail")); Register(new PassThrough("filter.trackmatte"));
    }

    /// <summary>絵を変えない効果 (描画エンジンが別の所で扱うもの)。</summary>
    private sealed class PassThrough(string id) : IVideoEffect
    {
        public string Id => id;
        public Sprite Apply(Sprite s, EffectArgs a) => s;
        public EffectFlags Flags => EffectFlags.Deterministic;
    }

    // ================= 共通 =================

    /// <summary>1 ピクセルの色 (乗算を戻した 0〜1) を変える関数。</summary>
    private delegate void ColorFn(ref float r, ref float g, ref float b);

    /// <summary>色を変える (乗算済みアルファを戻してから f を呼び、strength (0〜1) の割合で元の色と混ぜる)。</summary>
    private static unsafe void MapColors(IntPtr pixels, int w, int h, int rowBytes, float strength, ColorFn f)
    {
        if (strength <= 0) return;
        PixelOps.ForBands(w, h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                byte* p = (byte*)pixels + (long)y * rowBytes;
                for (int x = 0; x < w; x++, p += 4)
                {
                    int a = p[3];
                    if (a == 0) continue;
                    float ia = 1f / a;
                    float b0 = p[0] * ia, g0 = p[1] * ia, r0 = p[2] * ia;
                    float r = r0, g = g0, b = b0;
                    f(ref r, ref g, ref b);
                    if (strength < 1) { r = r0 + (r - r0) * strength; g = g0 + (g - g0) * strength; b = b0 + (b - b0) * strength; }
                    p[0] = (byte)(Math.Clamp(b, 0, 1) * a + 0.5f);
                    p[1] = (byte)(Math.Clamp(g, 0, 1) * a + 0.5f);
                    p[2] = (byte)(Math.Clamp(r, 0, 1) * a + 0.5f);
                }
            }
        });
    }

    private static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    private static void Saturate(ref float r, ref float g, ref float b, float k)
    {
        float l = Luma(r, g, b);
        r = l + (r - l) * k; g = l + (g - l) * k; b = l + (b - l) * k;
    }

    private static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>src を読みながら dst に書く処理を、余白なしで行う (CPU / GPU どちらでも同じ処理)。</summary>
    private static Sprite Remap(Sprite s, EffectArgs a, Func<IntPtr, IntPtr, int, int, int, IntPtr> op, int pad = 0) =>
        Resample(s, a, pad, pad, op) ?? s;

    // ================= 色 =================

    internal sealed class CubeLut
    {
        public required int Size;
        public required float[] Data;   // r が最も速く変わる順 (r, g, b)
        public required DateTime Stamp;
    }

    private static readonly Dictionary<string, CubeLut?> Cubes = new();

    /// <summary>.cube 形式の 3D LUT を読む (変わっていなければ前に読んだものを使う)。読めなければ null。</summary>
    internal static CubeLut? LoadCube(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (Cubes) if (Cubes.TryGetValue(path, out var known) && known != null && known.Stamp == stamp) return known;
        CubeLut? made = null;
        try
        {
            int size = 0;
            float[] min = { 0, 0, 0 }, max = { 1, 1, 1 };
            var data = new List<float>();
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts[0] == "LUT_3D_SIZE") { size = int.Parse(parts[1], CultureInfo.InvariantCulture); continue; }
                if (parts[0] == "DOMAIN_MIN") { for (int i = 0; i < 3; i++) min[i] = float.Parse(parts[i + 1], CultureInfo.InvariantCulture); continue; }
                if (parts[0] == "DOMAIN_MAX") { for (int i = 0; i < 3; i++) max[i] = float.Parse(parts[i + 1], CultureInfo.InvariantCulture); continue; }
                if (char.IsLetter(parts[0][0])) continue;   // TITLE・LUT_1D_SIZE など
                if (parts.Length < 3) continue;
                for (int i = 0; i < 3; i++)
                    data.Add((float.Parse(parts[i], CultureInfo.InvariantCulture) - min[i]) / Math.Max(1e-6f, max[i] - min[i]));
            }
            if (size >= 2 && data.Count >= size * size * size * 3) made = new CubeLut { Size = size, Data = data.ToArray(), Stamp = stamp };
        }
        catch (Exception e) when (e is FormatException or IOException or IndexOutOfRangeException or UnauthorizedAccessException) { }
        lock (Cubes) Cubes[path] = made;
        return made;
    }

    /// <summary>3D LUT を三線形補間で引く。</summary>
    private static void SampleCube(CubeLut lut, ref float r, ref float g, ref float b)
    {
        int n = lut.Size, m = n - 1;
        float fr = Math.Clamp(r, 0, 1) * m, fg = Math.Clamp(g, 0, 1) * m, fb = Math.Clamp(b, 0, 1) * m;
        int r0 = Math.Min((int)fr, m - 1), g0 = Math.Min((int)fg, m - 1), b0 = Math.Min((int)fb, m - 1);
        float tr = fr - r0, tg = fg - g0, tb = fb - b0;
        var d = lut.Data;
        float rr = 0, gg = 0, bb = 0;
        for (int k = 0; k < 8; k++)
        {
            int dr = k & 1, dg = (k >> 1) & 1, db = (k >> 2) & 1;
            float wgt = (dr == 1 ? tr : 1 - tr) * (dg == 1 ? tg : 1 - tg) * (db == 1 ? tb : 1 - tb);
            int i = (((b0 + db) * n + (g0 + dg)) * n + (r0 + dr)) * 3;
            rr += d[i] * wgt; gg += d[i + 1] * wgt; bb += d[i + 2] * wgt;
        }
        r = rr; g = gg; b = bb;
    }

    /// <summary>LUT (.cube): 3D LUT で色を変える (カラーグレーディング)。</summary>
    private sealed class Lut : PixelEffectBase
    {
        public override string Id => "filter.lut";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            var lut = LoadCube(a.Effect.GetString("file"));
            float k = Math.Clamp(a.Track("strength") / 100f, 0, 1);
            if (lut == null || k <= 0) return null;
            return (px, w, h, rb) => MapColors(px, w, h, rb, k, (ref float r, ref float g, ref float b) => SampleCube(lut, ref r, ref g, ref b));
        }

        protected override Sprite? OnGpu(Sprite s, EffectArgs a)
        {
            var lut = LoadCube(a.Effect.GetString("file"));
            float k = Math.Clamp(a.Track("strength") / 100f, 0, 1);
            if (lut == null || k <= 0) return s;
            if (CubeImage(lut) is not { } cube) return null;
            using var cubeShader = cube.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            return MapColorsOnGpu(s, a, CubeShader, k, u => u["n"] = (float)lut.Size, kids => kids["cube"] = cubeShader);
        }
    }

    /// <summary>5 点 (入力 0・64・128・192・255) を通るなめらかな曲線の変換表 (単調な 3 次補間)。</summary>
    internal static byte[] CurveLut(float[] outputs)
    {
        float[] xs = { 0, 64, 128, 192, 255 };
        int n = xs.Length;
        var d = new float[n - 1];
        var m = new float[n];
        for (int i = 0; i < n - 1; i++) d[i] = (outputs[i + 1] - outputs[i]) / (xs[i + 1] - xs[i]);
        m[0] = d[0]; m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        // 行き過ぎないように傾きを抑える (Fritsch–Carlson)
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = m[i + 1] = 0; continue; }
            float al = m[i] / d[i], be = m[i + 1] / d[i], s2 = al * al + be * be;
            if (s2 > 9) { float t = 3 / MathF.Sqrt(s2); m[i] = t * al * d[i]; m[i + 1] = t * be * d[i]; }
        }
        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            int i = Math.Min(n - 2, v / 64);
            if (v >= 192) i = 3;
            float hgt = xs[i + 1] - xs[i], t = (v - xs[i]) / hgt, t2 = t * t, t3 = t2 * t;
            float y = (2 * t3 - 3 * t2 + 1) * outputs[i] + (t3 - 2 * t2 + t) * hgt * m[i] + (-2 * t3 + 3 * t2) * outputs[i + 1] + (t3 - t2) * hgt * m[i + 1];
            lut[v] = (byte)Math.Clamp((int)MathF.Round(y), 0, 255);
        }
        return lut;
    }

    /// <summary>トーンカーブ: 5 点の出力の明るさから曲線を作り、RGB (または 1 つの色) にかける。</summary>
    private sealed class Curves : PixelEffectBase
    {
        public override string Id => "filter.curves";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            float[] outs = { a.Track("p0"), a.Track("p1"), a.Track("p2"), a.Track("p3"), a.Track("p4") };
            if (outs[0] == 0 && outs[1] == 64 && outs[2] == 128 && outs[3] == 192 && outs[4] == 255) return null;
            var lut = CurveLut(outs);
            int channel = a.Effect.GetInt("channel");
            if (channel == 0) return (px, w, h, rb) => PixelFx.ApplyLut(px, w, h, rb, lut);
            var f = new float[256];
            for (int i = 0; i < 256; i++) f[i] = lut[i] / 255f;
            float Map(float v) => f[Math.Clamp((int)(v * 255 + 0.5f), 0, 255)];
            return (px, w, h, rb) => MapColors(px, w, h, rb, 1, (ref float r, ref float g, ref float b) =>
            {
                if (channel == 1) r = Map(r); else if (channel == 2) g = Map(g); else b = Map(b);
            });
        }

        protected override Sprite? OnGpu(Sprite s, EffectArgs a)
        {
            float[] outs = { a.Track("p0"), a.Track("p1"), a.Track("p2"), a.Track("p3"), a.Track("p4") };
            if (outs[0] == 0 && outs[1] == 64 && outs[2] == 128 && outs[3] == 192 && outs[4] == 255) return s;
            var lut = CurveLut(outs);
            int channel = a.Effect.GetInt("channel");
            // 全部の色: 表のカラーフィルタ (レベル補正と同じ。GPU のまま)
            if (channel == 0) return ApplyLut(s, a, lut);
            var px = new byte[256 * 4];
            for (int i = 0; i < 256; i++) { px[i * 4] = lut[i]; px[i * 4 + 3] = 255; }
            using var table = SKImage.FromPixelCopy(new SKImageInfo(256, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul), px);
            if (table == null) return null;
            using var tableShader = table.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            return MapColorsOnGpu(s, a, CurveChannelShader, 1, u => u["channel"] = (float)channel, kids => kids["table"] = tableShader);
        }
    }

    /// <summary>色温度・色かぶり: 青 ↔ 黄、緑 ↔ 赤紫の向きに色を寄せる (色の変換なので、ほかの色の変換とまとめて処理される)。</summary>
    private sealed class WhiteBalance : IVideoEffect, IColorMatrixEffect
    {
        public string Id => "filter.whitebalance";
        public float[]? GetMatrix(EffectArgs a)
        {
            float t = Math.Clamp(a.Track("temperature") / 100f, -1, 1), m = Math.Clamp(a.Track("tint") / 100f, -1, 1);
            if (t == 0 && m == 0) return null;
            float r = 1 + 0.22f * t + 0.08f * m, g = 1 + 0.04f * t - 0.16f * m, b = 1 - 0.22f * t + 0.08f * m;
            return new float[] { r, 0, 0, 0, 0, 0, g, 0, 0, 0, 0, 0, b, 0, 0, 0, 0, 0, 1, 0 };
        }
        public Sprite Apply(Sprite s, EffectArgs a) => GetMatrix(a) is { } m ? ApplyMatrix(s, a, m) : s;
    }

    /// <summary>シャドウ・ハイライト: 暗い所だけを明るく、明るい所だけを暗くする (中間の明るさはほぼそのまま)。</summary>
    private sealed class ShadowHighlight : PixelEffectBase
    {
        public override string Id => "filter.shadowhighlight";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            float sh = Math.Clamp(a.Track("shadows") / 100f, -1, 1), hi = Math.Clamp(a.Track("highlights") / 100f, -1, 1);
            if (sh == 0 && hi == 0) return null;
            return (px, w, h, rb) => MapColors(px, w, h, rb, 1, (ref float r, ref float g, ref float b) =>
            {
                float l = Luma(r, g, b);
                float ws = (1 - l) * (1 - l) * 0.7f, wh = l * l * 0.6f;
                r += sh * ws * (1 - r) - hi * wh * r;
                g += sh * ws * (1 - g) - hi * wh * g;
                b += sh * ws * (1 - b) - hi * wh * b;
            });
        }

        protected override Sprite? OnGpu(Sprite s, EffectArgs a)
        {
            float sh = Math.Clamp(a.Track("shadows") / 100f, -1, 1), hi = Math.Clamp(a.Track("highlights") / 100f, -1, 1);
            if (sh == 0 && hi == 0) return s;
            return MapColorsOnGpu(s, a, ShadowHighlightShader, 1, u => { u["sh"] = sh; u["hi"] = hi; });
        }
    }

    /// <summary>グラデーションマップ: 明るさに合わせて、暗い所・中間・明るい所の色に置き換える (2 色ならデュオトーン、3 色ならトライトーン)。</summary>
    private sealed class GradientMap : PixelEffectBase
    {
        public override string Id => "filter.gradmap";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            float k = Math.Clamp(a.Track("strength") / 100f, 0, 1);
            if (k <= 0) return null;
            var dark = a.Color("dark"); var mid = a.Color("mid"); var light = a.Color("light");
            bool useMid = a.Check("useMid");
            return (px, w, h, rb) => MapColors(px, w, h, rb, k, (ref float r, ref float g, ref float b) =>
            {
                float l = Luma(r, g, b);
                SKColor c0 = dark, c1 = light;
                float t = l;
                if (useMid) { if (l < 0.5f) { c1 = mid; t = l * 2; } else { c0 = mid; t = l * 2 - 1; } }
                r = (c0.Red + (c1.Red - c0.Red) * t) / 255f;
                g = (c0.Green + (c1.Green - c0.Green) * t) / 255f;
                b = (c0.Blue + (c1.Blue - c0.Blue) * t) / 255f;
            });
        }

        protected override Sprite? OnGpu(Sprite s, EffectArgs a)
        {
            float k = Math.Clamp(a.Track("strength") / 100f, 0, 1);
            if (k <= 0) return s;
            var dark = a.Color("dark"); var mid = a.Color("mid"); var light = a.Color("light");
            return MapColorsOnGpu(s, a, GradientMapShader, k, u =>
            {
                u["dark"] = new float[] { dark.Red, dark.Green, dark.Blue }; u["mid"] = new float[] { mid.Red, mid.Green, mid.Blue };
                u["light"] = new float[] { light.Red, light.Green, light.Blue }; u["useMid"] = a.Check("useMid") ? 1f : 0f;
            });
        }
    }

    /// <summary>カラープリセット: よく使う色の雰囲気をまとめたもの (EffectRegistry.LookPresets)。</summary>
    private sealed class Look : PixelEffectBase
    {
        public override string Id => "filter.look";
        protected override Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            float k = Math.Clamp(a.Track("strength") / 100f, 0, 1);
            if (k <= 0) return null;
            ColorFn f = a.Effect.GetInt("preset") switch
            {
                0 => (ref float r, ref float g, ref float b) =>   // セピア
                {
                    float l = Luma(r, g, b);
                    r = l * 1.07f + 0.05f; g = l * 0.88f + 0.02f; b = l * 0.66f;
                },
                1 => (ref float r, ref float g, ref float b) =>   // ティール & オレンジ: 暗い所を青緑、明るい所を橙に
                {
                    float t = Smooth01(Luma(r, g, b));
                    r += (-0.08f + 0.2f * t); g += (0.02f + 0.0f * t); b += (0.1f - 0.24f * t);
                    Saturate(ref r, ref g, ref b, 1.1f);
                },
                2 => (ref float r, ref float g, ref float b) =>   // 褪色: 黒を持ち上げ、彩度を下げ、少し暖かく
                {
                    Saturate(ref r, ref g, ref b, 0.7f);
                    r = 0.08f + r * 0.86f + 0.02f; g = 0.08f + g * 0.84f; b = 0.07f + b * 0.8f;
                },
                3 => (ref float r, ref float g, ref float b) => { r *= 0.9f; g *= 0.98f; b = b * 1.06f + 0.03f; },   // クール
                4 => (ref float r, ref float g, ref float b) => { r = r * 1.06f + 0.03f; g *= 1.0f; b *= 0.88f; },    // ウォーム
                5 => (ref float r, ref float g, ref float b) =>   // 白黒 (高コントラスト)
                {
                    float l = Smooth01((Luma(r, g, b) - 0.5f) * 1.5f + 0.5f);
                    r = g = b = l;
                },
                6 => (ref float r, ref float g, ref float b) =>   // ビビッド
                {
                    Saturate(ref r, ref g, ref b, 1.45f);
                    r = (r - 0.5f) * 1.1f + 0.5f; g = (g - 0.5f) * 1.1f + 0.5f; b = (b - 0.5f) * 1.1f + 0.5f;
                },
                7 => (ref float r, ref float g, ref float b) =>   // ブリーチバイパス: 彩度を落としてコントラストを上げる
                {
                    Saturate(ref r, ref g, ref b, 0.45f);
                    r = Smooth01((r - 0.5f) * 1.25f + 0.5f); g = Smooth01((g - 0.5f) * 1.25f + 0.5f); b = Smooth01((b - 0.5f) * 1.25f + 0.5f);
                },
                8 => (ref float r, ref float g, ref float b) =>   // 夕焼け: 明るい所を橙、暗い所を紫に
                {
                    float t = Luma(r, g, b);
                    r = r * 1.1f + 0.06f * t + 0.03f; g = g * 0.92f; b = b * 0.78f + 0.08f * (1 - t);
                },
                _ => (ref float r, ref float g, ref float b) =>   // 月明かり: 彩度を落として青く暗く
                {
                    Saturate(ref r, ref g, ref b, 0.45f);
                    r *= 0.75f; g *= 0.85f; b = b * 1.05f + 0.04f;
                },
            };
            return (px, w, h, rb) => MapColors(px, w, h, rb, k, f);
        }

        protected override Sprite? OnGpu(Sprite s, EffectArgs a)
        {
            float k = Math.Clamp(a.Track("strength") / 100f, 0, 1);
            if (k <= 0) return s;
            int preset = a.Effect.GetInt("preset");
            // 0〜8 以外 (負の数も) は月明かり (CPU の switch の既定と同じ)
            return MapColorsOnGpu(s, a, LookShader, k, u => u["preset"] = preset is >= 0 and <= 8 ? preset : 9f);
        }
    }

    // ================= 質感 =================

    /// <summary>フィルム粒子: 明るさにかかる細かい粒 (中間の明るさで強く、暗部・明部で弱い)。</summary>
    private sealed class Grain : PixelEffectBase
    {
        public override string Id => "filter.grain";
        public override EffectFlags Flags => EffectFlags.None;   // 毎フレーム変わる
        protected override unsafe Action<IntPtr, int, int, int>? Op(EffectArgs a)
        {
            float amount = a.Track("amount") / 100f * 0.35f, size = Math.Max(0.5f, a.Track("size"));
            if (amount <= 0) return null;
            bool color = a.Check("color");
            uint seed = (uint)(int)a.Track("seed") * 7919u + (a.Check("animate") ? (uint)a.Frame * 2654435761u : 0);
            return (px, w, h, rb) => PixelOps.ForBands(w, h, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* p = (byte*)px + (long)y * rb;
                    for (int x = 0; x < w; x++, p += 4)
                    {
                        int al = p[3];
                        if (al == 0) continue;
                        // 粒の大きさのマスごとの乱数を、なめらかにつなぐ
                        float n = ValueNoise(x / size, y / size, seed) + ValueNoise(x / size + 31.7f, y / size + 11.3f, seed ^ 0x55u) - 1;
                        float l = (p[2] * 0.2126f + p[1] * 0.7152f + p[0] * 0.0722f) / Math.Max(1, al);
                        float k = amount * (0.35f + 2.6f * l * (1 - l)) * al;
                        for (int c = 0; c < 3; c++)
                        {
                            float nc = color ? ValueNoise(x / size + c * 17.1f, y / size, seed + (uint)c * 101) * 2 - 1 : n;
                            p[c] = (byte)Math.Clamp(p[c] + nc * k, 0, al);
                        }
                    }
                }
            });
        }
    }

    /// <summary>VHS: 色のにじみとずれ、横の揺れ、ノイズの帯、下端の乱れ。</summary>
    private sealed class Vhs : IVideoEffect
    {
        public string Id => "filter.vhs";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            float k = a.Track("strength") / 100f;
            if (k <= 0) return s;
            float noise = a.Track("noise") / 100f, wobble = a.Track("wobble") / 100f;
            uint seed = (uint)(int)a.Track("seed") * 977u + (uint)a.Frame * 2654435761u;
            float t = (float)a.Time;
            return Remap(s, a, (src, dst, w, h, rb) =>
            {
                uint* sp = (uint*)src, dp = (uint*)dst;
                int stride = rb / 4;
                float shift = Math.Max(1, w * 0.004f) * k, scroll = (t * 0.15f) % 1;
                PixelOps.ForBands(w, h, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        float vy = y / (float)h;
                        // 横の揺れ: ゆっくりした揺れ + ときどき強く
                        float off = wobble * k * w * 0.004f * MathF.Sin(vy * 40 + t * 7) + wobble * k * w * 0.01f * (ValueNoise(vy * 30, t * 3, seed >> 8) - 0.5f);
                        // 下端の乱れ (テープの継ぎ目)
                        if (vy > 0.94f) off += k * w * 0.03f * (Rand01((uint)y, seed, 3) - 0.5f) * (vy - 0.94f) / 0.06f * 3;
                        // 流れるノイズの帯
                        float band = MathF.Abs(((vy - scroll) % 1 + 1) % 1 - 0.5f);
                        bool inBand = band < 0.015f * noise * 3;
                        uint* row = dp + (long)y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            float sx = x - off;
                            uint c = PixelFx.Sample(sp, w, h, stride, sx, y, true);
                            // 色のにじみ: 赤と青を左右にずらし、少しぼかす
                            uint r1 = PixelFx.Sample(sp, w, h, stride, sx - shift, y, true), r2 = PixelFx.Sample(sp, w, h, stride, sx - shift * 2.5f, y, true);
                            uint b1 = PixelFx.Sample(sp, w, h, stride, sx + shift, y, true), b2 = PixelFx.Sample(sp, w, h, stride, sx + shift * 2.5f, y, true);
                            uint al = c >> 24;
                            float r = (((r1 >> 16) & 0xFF) + ((r2 >> 16) & 0xFF)) / 2f, g = (c >> 8) & 0xFF, b = ((b1 & 0xFF) + (b2 & 0xFF)) / 2f;
                            // 彩度を少し下げる
                            float l = 0.3f * r + 0.59f * g + 0.11f * b, sat = 1 - 0.25f * k;
                            r = l + (r - l) * sat; g = l + (g - l) * sat; b = l + (b - l) * sat;
                            float n = 0;
                            if (noise > 0)
                            {
                                float rnd = Rand01((uint)(y * 8191 + x), seed, 5);
                                n = (rnd - 0.5f) * 40 * noise * k;
                                if (inBand) n += 120 * noise * Rand01((uint)(y * 131 + x / 3), seed, 6);
                                else if (rnd > 1 - 0.0015f * noise) n += 200;   // 白い点
                            }
                            r = Math.Clamp(r + n, 0, al); g = Math.Clamp(g + n, 0, al); b = Math.Clamp(b + n, 0, al);
                            row[x] = al << 24 | (uint)r << 16 | (uint)g << 8 | (uint)b;
                        }
                    }
                });
                return dst;
            });
        }
    }

    /// <summary>ブラウン管: 画面の丸み、走査線、RGB の格子、周辺減光。</summary>
    private sealed class Crt : IVideoEffect
    {
        public string Id => "filter.crt";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            float scan = a.Track("scanline") / 100f, mask = a.Track("mask") / 100f, curve = a.Track("curvature") / 100f, vig = a.Track("vignette") / 100f;
            if (scan <= 0 && mask <= 0 && curve <= 0 && vig <= 0) return s;
            float pitch = Math.Max(2, 3 / s.Scale);
            return Remap(s, a, (src, dst, w, h, rb) =>
            {
                uint* sp = (uint*)src, dp = (uint*)dst;
                int stride = rb / 4;
                float cx = w / 2f, cy = h / 2f;
                PixelOps.ForBands(w, h, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        uint* row = dp + (long)y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            // 丸み: 中心から離れるほど外を読む (樽型)
                            float nx = (x - cx) / cx, ny = (y - cy) / cy, r2 = nx * nx + ny * ny;
                            float k = 1 + curve * 0.12f * r2;
                            float sx = cx + nx * k * cx, sy = cy + ny * k * cy;
                            if (sx < -0.5f || sy < -0.5f || sx > w - 0.5f || sy > h - 0.5f) { row[x] = 0xFF000000u & (sp[(long)y * stride + x] & 0xFF000000u); continue; }
                            uint c = PixelFx.Sample(sp, w, h, stride, sx, sy, true);
                            float r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                            uint al = c >> 24;
                            float line = 1 - scan * 0.55f * MathF.Pow(MathF.Abs(MathF.Sin(MathF.PI * sy / pitch)), 2);
                            int col = (int)(x / (pitch / 3)) % 3;
                            float mr = col == 0 ? 1 : 1 - mask * 0.55f, mg = col == 1 ? 1 : 1 - mask * 0.55f, mb = col == 2 ? 1 : 1 - mask * 0.55f;
                            float v = 1 - vig * 0.6f * MathF.Pow(r2 / 2, 1.5f);
                            float boost = 1 + 0.25f * (scan + mask) / 2;
                            r = Math.Clamp(r * line * mr * v * boost, 0, al); g = Math.Clamp(g * line * mg * v * boost, 0, al); b = Math.Clamp(b * line * mb * v * boost, 0, al);
                            row[x] = al << 24 | (uint)r << 16 | (uint)g << 8 | (uint)b;
                        }
                    }
                });
                return dst;
            });
        }
    }

    /// <summary>グリッチ: ときどき (頻度)、四角いブロックがずれて色が分かれる。</summary>
    private sealed class GlitchFilter : IVideoEffect
    {
        public string Id => "filter.glitch";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            float k = a.Track("strength") / 100f, freq = a.Track("frequency") / 100f;
            uint seed = (uint)(int)a.Track("seed") * 31u;
            // 4 フレームごとに、グリッチが起きるかを決める (起きているときは毎フレーム変わる)
            uint slot = (uint)(a.Frame / 4);
            if (k <= 0 || Rand01(slot, seed, 81) >= freq) return s;
            uint fs = (uint)a.Frame * 2654435761u + seed;
            float block = Math.Max(2, a.Track("block") / s.Scale);
            return Remap(s, a, (src, dst, w, h, rb) =>
            {
                uint* sp = (uint*)src, dp = (uint*)dst;
                int stride = rb / 4;
                float split = k * w * 0.012f;
                PixelOps.ForBands(w, h, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        uint* row = dp + (long)y * stride;
                        int by = (int)(y / block);
                        for (int x = 0; x < w; x++)
                        {
                            int bx = (int)(x / (block * 3));
                            float pick = Rand01((uint)(by * 977 + bx), fs, 82);
                            float off = pick < 0.25f * k ? (Rand01((uint)(by * 977 + bx), fs, 83) - 0.5f) * k * w * 0.2f : 0;
                            float rowOff = Rand01((uint)by, fs, 84) < 0.2f * k ? (Rand01((uint)by, fs, 85) - 0.5f) * k * w * 0.08f : 0;
                            float sx = x - off - rowOff;
                            uint r = PixelFx.Sample(sp, w, h, stride, sx - split, y, true), c = PixelFx.Sample(sp, w, h, stride, sx, y, true), b = PixelFx.Sample(sp, w, h, stride, sx + split, y, true);
                            uint al = Math.Max(c >> 24, Math.Max(r >> 24, b >> 24));
                            uint outc = (r & 0xFF0000u) | (c & 0xFF00u) | (b & 0xFFu) | al << 24;
                            // まれに色を反転したブロック
                            if (pick > 1 - 0.04f * k) outc = (outc & 0xFF000000u) | ((al * 0x010101u) - (outc & 0xFFFFFFu)) & 0xFFFFFFu;
                            row[x] = outc;
                        }
                    }
                });
                return dst;
            });
        }
    }

    /// <summary>ハーフトーン (網点): 斜めの格子のマスごとに、暗いほど大きい点で描く。CMY は 4 色の網点を重ねる。</summary>
    private sealed class Halftone : IVideoEffect
    {
        public string Id => "filter.halftone";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            float size = Math.Max(2, a.Track("size") / s.Scale), angle = a.Track("angle") * MathF.PI / 180;
            int mode = a.Effect.GetInt("mode");
            return Remap(s, a, (src, dst, w, h, rb) =>
            {
                uint* sp = (uint*)src, dp = (uint*)dst;
                int stride = rb / 4;
                // 1 つの網点の濃さ (0〜1): 角度 ang の格子で、(x, y) が点の中にあるか (境目はなめらかに)
                float Dot(float x, float y, float ang, Func<uint, float> ink)
                {
                    float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
                    float u = x * cs + y * sn, v = -x * sn + y * cs;
                    float cu = (MathF.Floor(u / size) + 0.5f) * size, cv = (MathF.Floor(v / size) + 0.5f) * size;
                    float px = cu * cs - cv * sn, py = cu * sn + cv * cs;
                    float amount = ink(PixelFx.Sample(sp, w, h, stride, px, py, true));
                    float radius = size * 0.71f * MathF.Sqrt(Math.Clamp(amount, 0, 1));
                    float d = MathF.Sqrt((u - cu) * (u - cu) + (v - cv) * (v - cv));
                    return Math.Clamp(radius - d + 0.5f, 0, 1);
                }
                static float Un(uint c, int shift) { uint al = c >> 24; return al == 0 ? 0 : ((c >> shift) & 0xFF) / (float)al; }
                PixelOps.ForBands(w, h, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        uint* row = dp + (long)y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            uint orig = sp[(long)y * stride + x];
                            uint al = orig >> 24;
                            if (al == 0) { row[x] = 0; continue; }
                            float r, g, b;
                            if (mode == 2)
                            {
                                // CMY + 黒: 色ごとの角度で網点を重ねる (紙の白から引く)
                                float c = Dot(x, y, 15 * MathF.PI / 180, q => { float k0 = 1 - MathF.Max(Un(q, 16), MathF.Max(Un(q, 8), Un(q, 0))); return k0 >= 1 ? 0 : (1 - Un(q, 16) - k0) / (1 - k0); });
                                float m = Dot(x, y, 75 * MathF.PI / 180, q => { float k0 = 1 - MathF.Max(Un(q, 16), MathF.Max(Un(q, 8), Un(q, 0))); return k0 >= 1 ? 0 : (1 - Un(q, 8) - k0) / (1 - k0); });
                                float yy = Dot(x, y, 0, q => { float k0 = 1 - MathF.Max(Un(q, 16), MathF.Max(Un(q, 8), Un(q, 0))); return k0 >= 1 ? 0 : (1 - Un(q, 0) - k0) / (1 - k0); });
                                float k = Dot(x, y, 45 * MathF.PI / 180, q => (1 - MathF.Max(Un(q, 16), MathF.Max(Un(q, 8), Un(q, 0)))) * 0.9f);
                                r = (1 - c) * (1 - k); g = (1 - m) * (1 - k); b = (1 - yy) * (1 - k);
                            }
                            else
                            {
                                float dot = Dot(x, y, angle, q => 1 - (0.2126f * Un(q, 16) + 0.7152f * Un(q, 8) + 0.0722f * Un(q, 0)));
                                if (mode == 1)
                                {
                                    // 元の色の点を、白い紙の上に
                                    r = 1 - dot + dot * Un(orig, 16); g = 1 - dot + dot * Un(orig, 8); b = 1 - dot + dot * Un(orig, 0);
                                }
                                else r = g = b = 1 - dot;
                            }
                            row[x] = al << 24 | (uint)(Math.Clamp(r, 0, 1) * al) << 16 | (uint)(Math.Clamp(g, 0, 1) * al) << 8 | (uint)(Math.Clamp(b, 0, 1) * al);
                        }
                    }
                });
                return dst;
            });
        }
    }

    /// <summary>
    /// 油絵・水彩: 桑原フィルタ (周りの 4 つの四角のうち、色のばらつきがいちばん小さいものの平均) で筆で塗ったような面にする。
    /// 平均とばらつきは累積和の表で求めるので、筆の大きさによらず速い。水彩は、さらに輪郭を濃くし、紙のむらを足す。
    /// </summary>
    private sealed class Paint : IVideoEffect
    {
        public string Id => "filter.paint";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            int radius = Math.Max(1, (int)MathF.Round(a.Track("radius") / s.Scale));
            bool water = a.Effect.GetInt("type") == 1;
            return Remap(s, a, (src, dst, w, h, rb) =>
            {
                uint* sp = (uint*)src, dp = (uint*)dst;
                int stride = rb / 4, W1 = w + 1;
                // 累積和 (R・G・B・明るさの 2 乗)。乗算済みのまま扱う
                var sr = new double[W1 * (h + 1)]; var sg = new double[W1 * (h + 1)]; var sb = new double[W1 * (h + 1)]; var sl = new double[W1 * (h + 1)];
                for (int y = 0; y < h; y++)
                {
                    double rr = 0, gg = 0, bb = 0, ll = 0;
                    for (int x = 0; x < w; x++)
                    {
                        uint c = sp[(long)y * stride + x];
                        double r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF, l = 0.299 * r + 0.587 * g + 0.114 * b;
                        rr += r; gg += g; bb += b; ll += l * l;
                        int i = (y + 1) * W1 + x + 1, up = y * W1 + x + 1;
                        sr[i] = sr[up] + rr; sg[i] = sg[up] + gg; sb[i] = sb[up] + bb; sl[i] = sl[up] + ll;
                    }
                }
                double Sum(double[] t, int x0, int y0, int x1, int y1) => t[y1 * W1 + x1] - t[y0 * W1 + x1] - t[y1 * W1 + x0] + t[y0 * W1 + x0];
                PixelOps.ForBands(w, h, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        uint* row = dp + (long)y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            uint orig = sp[(long)y * stride + x];
                            uint al = orig >> 24;
                            if (al == 0) { row[x] = 0; continue; }
                            double best = double.MaxValue, br = 0, bg = 0, bb = 0;
                            for (int q = 0; q < 4; q++)
                            {
                                int qx0 = (q & 1) == 0 ? Math.Max(0, x - radius) : x, qx1 = (q & 1) == 0 ? x + 1 : Math.Min(w, x + radius + 1);
                                int qy0 = (q & 2) == 0 ? Math.Max(0, y - radius) : y, qy1 = (q & 2) == 0 ? y + 1 : Math.Min(h, y + radius + 1);
                                double n = (qx1 - qx0) * (qy1 - qy0);
                                double mr = Sum(sr, qx0, qy0, qx1, qy1) / n, mg = Sum(sg, qx0, qy0, qx1, qy1) / n, mb = Sum(sb, qx0, qy0, qx1, qy1) / n;
                                double ml = 0.299 * mr + 0.587 * mg + 0.114 * mb;
                                double variance = Sum(sl, qx0, qy0, qx1, qy1) / n - ml * ml;
                                if (variance < best) { best = variance; br = mr; bg = mg; bb = mb; }
                            }
                            if (water)
                            {
                                // 水彩: 明るく淡く、ばらつきの大きい所 (輪郭) を少し濃く、紙のむら
                                double edge = Math.Min(1, Math.Sqrt(Math.Max(0, best)) / 40);
                                double paper = 0.94 + 0.06 * ValueNoise(x * 0.08f, y * 0.08f, 0xA7);
                                br = (br + (al - br) * 0.25 - edge * 35) * paper; bg = (bg + (al - bg) * 0.25 - edge * 35) * paper; bb = (bb + (al - bb) * 0.25 - edge * 35) * paper;
                            }
                            row[x] = al << 24 | (uint)Math.Clamp(br, 0, al) << 16 | (uint)Math.Clamp(bg, 0, al) << 8 | (uint)Math.Clamp(bb, 0, al);
                        }
                    }
                });
                return dst;
            });
        }
    }

    /// <summary>
    /// スケッチ (鉛筆画): 明るさを反転してぼかしたものを「覆い焼き」で重ねる (輪郭だけが線として残る)。紙の色を掛ける。
    /// </summary>
    private sealed class Sketch : IVideoEffect
    {
        public string Id => "filter.sketch";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            float strength = a.Track("strength") / 100f;
            bool color = a.Check("color");
            var paper = a.Color("paper");
            int radius = Math.Max(1, (int)MathF.Round(4 / s.Scale));
            return Remap(s, a, (src, dst, w, h, rb) =>
            {
                uint* sp = (uint*)src, dp = (uint*)dst;
                int stride = rb / 4;
                var lum = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        uint c = sp[(long)y * stride + x];
                        uint al = c >> 24;
                        lum[y * w + x] = al == 0 ? 1 : (0.299f * ((c >> 16) & 0xFF) + 0.587f * ((c >> 8) & 0xFF) + 0.114f * (c & 0xFF)) / al;
                    }
                // 反転した明るさを、箱型のぼかし 3 回 (ほぼガウス) でぼかす
                var inv = new float[w * h];
                for (int i = 0; i < inv.Length; i++) inv[i] = 1 - lum[i];
                var tmp = new float[w * h];
                for (int pass = 0; pass < 3; pass++) { BoxBlur(inv, tmp, w, h, radius, true); BoxBlur(tmp, inv, w, h, radius, false); }
                PixelOps.ForBands(w, h, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        uint* row = dp + (long)y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            uint orig = sp[(long)y * stride + x];
                            uint al = orig >> 24;
                            if (al == 0) { row[x] = 0; continue; }
                            int i = y * w + x;
                            float v = Math.Min(1, lum[i] / Math.Max(0.02f, 1 - inv[i]));   // 覆い焼き
                            v = MathF.Pow(v, 1 + strength * 2);                            // 線を濃く
                            float r = v * paper.Red / 255f, g = v * paper.Green / 255f, b = v * paper.Blue / 255f;
                            if (color)
                            {
                                float k = 1f / al;
                                r *= 0.55f + 0.45f * ((orig >> 16) & 0xFF) * k; g *= 0.55f + 0.45f * ((orig >> 8) & 0xFF) * k; b *= 0.55f + 0.45f * (orig & 0xFF) * k;
                            }
                            row[x] = al << 24 | (uint)(Math.Clamp(r, 0, 1) * al) << 16 | (uint)(Math.Clamp(g, 0, 1) * al) << 8 | (uint)(Math.Clamp(b, 0, 1) * al);
                        }
                    }
                });
                return dst;
            });
        }
    }

    /// <summary>箱型のぼかし (横か縦の 1 方向)。</summary>
    private static void BoxBlur(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        int outer = horizontal ? h : w, inner = horizontal ? w : h;
        Parallel.For(0, outer, o =>
        {
            float sum = 0;
            int Idx(int i) => horizontal ? o * w + Math.Clamp(i, 0, w - 1) : Math.Clamp(i, 0, h - 1) * w + o;
            for (int i = -r; i <= r; i++) sum += src[Idx(i)];
            float k = 1f / (2 * r + 1);
            for (int i = 0; i < inner; i++)
            {
                dst[horizontal ? o * w + i : i * w + o] = sum * k;
                sum += src[Idx(i + r + 1)] - src[Idx(i - r)];
            }
        });
    }

    private static readonly float[] Bayer4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    /// <summary>ドット絵化: ドットの大きさのマスの平均の色にし、色の段階を減らす (ディザで中間色を網目で表す)。</summary>
    private sealed class PixelArt : IVideoEffect
    {
        public string Id => "filter.pixelart";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            int size = Math.Max(1, (int)MathF.Round(a.Track("size") / s.Scale));
            int levels = Math.Clamp((int)a.Track("levels"), 2, 32);
            bool dither = a.Check("dither");
            return WithPixels(s, a, (px, w, h, rb) =>
            {
                int cols = (w + size - 1) / size, rows = (h + size - 1) / size;
                Parallel.For(0, rows, cy =>
                {
                    for (int cx = 0; cx < cols; cx++)
                    {
                        int x0 = cx * size, y0 = cy * size, x1 = Math.Min(w, x0 + size), y1 = Math.Min(h, y0 + size);
                        long sr = 0, sg = 0, sb = 0, sa = 0;
                        for (int y = y0; y < y1; y++)
                        {
                            byte* p = (byte*)px + (long)y * rb + x0 * 4;
                            for (int x = x0; x < x1; x++, p += 4) { sb += p[0]; sg += p[1]; sr += p[2]; sa += p[3]; }
                        }
                        int n = (x1 - x0) * (y1 - y0);
                        int al = (int)(sa / n);
                        // 半透明の縁は、ドット絵らしく不透明か透明かに分ける
                        al = al >= 128 ? 255 : 0;
                        float r = 0, g = 0, b = 0;
                        if (sa > 0) { r = sr / (float)sa; g = sg / (float)sa; b = sb / (float)sa; }
                        float th = dither ? Bayer4[(cy & 3) * 4 + (cx & 3)] / 16f - 0.5f : 0;
                        float Q(float v) => Math.Clamp(MathF.Round(v * (levels - 1) + th), 0, levels - 1) / (levels - 1);
                        byte qr = (byte)(Q(r) * al), qg = (byte)(Q(g) * al), qb = (byte)(Q(b) * al);
                        for (int y = y0; y < y1; y++)
                        {
                            byte* p = (byte*)px + (long)y * rb + x0 * 4;
                            for (int x = x0; x < x1; x++, p += 4) { p[0] = qb; p[1] = qg; p[2] = qr; p[3] = (byte)al; }
                        }
                    }
                });
            }, alsoOnGpu: true) ?? s;
        }
    }

    /// <summary>ASCII アート: マスの明るさに合う文字で描き直す (暗い所ほど詰まった文字)。</summary>
    private sealed class Ascii : IVideoEffect
    {
        public string Id => "filter.ascii";
        // 明るい (薄い) 文字から暗い (詰まった) 文字の順
        private static readonly string[] Ramps = { " .:-=+*#%@", " ░▒▓█", " 1742356980" };

        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            float size = Math.Max(4, a.Track("size") / s.Scale);
            bool color = a.Check("color");
            var bg = a.Color("background");
            string ramp = Ramps[Math.Clamp(a.Effect.GetInt("chars"), 0, Ramps.Length - 1)];
            if (CopyPixels(s) is not { } copy) return s;
            int cols, rows;
            (char Ch, SKColor Color)[] cells;
            try
            {
                cols = Math.Max(1, (int)(s.Width / (size * 0.6f))); rows = Math.Max(1, (int)(s.Height / size));
                cells = new (char, SKColor)[cols * rows];
                float cw = s.Width / (float)cols, ch = s.Height / (float)rows;
                for (int cy = 0; cy < rows; cy++)
                    for (int cx = 0; cx < cols; cx++)
                    {
                        // マスの中の 3×3 点の平均
                        float r = 0, g = 0, b = 0, al = 0;
                        for (int j = 0; j < 3; j++)
                            for (int i = 0; i < 3; i++)
                            {
                                int x = Math.Min(s.Width - 1, (int)(cx * cw + (i + 0.5f) * cw / 3)), y = Math.Min(s.Height - 1, (int)(cy * ch + (j + 0.5f) * ch / 3));
                                byte* p = (byte*)copy.Buffer + (long)y * copy.RowBytes + x * 4;
                                b += p[0]; g += p[1]; r += p[2]; al += p[3];
                            }
                        if (al <= 0) { cells[cy * cols + cx] = (' ', SKColors.Transparent); continue; }
                        float l = (0.299f * r + 0.587f * g + 0.114f * b) / al;
                        char c = ramp[Math.Clamp((int)(l * ramp.Length), 0, ramp.Length - 1)];
                        var col = color ? new SKColor((byte)Math.Min(255, r / al * 255), (byte)Math.Min(255, g / al * 255), (byte)Math.Min(255, b / al * 255)) : SKColors.White;
                        cells[cy * cols + cx] = (c, col.WithAlpha((byte)Math.Min(255, al / 9)));
                    }
            }
            finally { PixelPool.Return(copy.Buffer, (long)copy.RowBytes * s.Height); }
            float cellW = s.Width / (float)cols, cellH = s.Height / (float)rows;
            var face = SKFontManager.Default.MatchFamily("DejaVu Sans Mono") ?? SKTypeface.FromFamilyName("monospace") ?? SKTypeface.Default;
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                using var bgPaint = new SKPaint { Color = bg };
                using var font = new SKFont(face, cellH * 1.05f);
                using var paint = new SKPaint { IsAntialias = true };
                // 背景は絵の見えている所だけ (CPU 描画では自前で合成する。PixelBlend)
                using (var layer = PixelBlend.BeginLayer(c, SKRect.Create(x, y, s.Width, s.Height), SKBlendMode.SrcOver))
                {
                    layer.Canvas.DrawRect(SKRect.Create(x, y, s.Width, s.Height), bgPaint);
                    using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
                    PixelBlend.DrawImage(layer.Canvas, s.Image, x, y, keep);
                }
                for (int cy = 0; cy < rows; cy++)
                    for (int cx = 0; cx < cols; cx++)
                    {
                        var (chr, col) = cells[cy * cols + cx];
                        if (chr == ' ' || col.Alpha == 0) continue;
                        paint.Color = col;
                        c.DrawText(chr.ToString(), x + cx * cellW, y + (cy + 0.85f) * cellH, font, paint);
                    }
            });
        }
    }

    // ================= 光 =================

    /// <summary>レンズフレア: 光源の光と放射状の筋、光源から中心を通る線の上に並ぶ光の輪 (ゴースト)。</summary>
    private sealed class LensFlare : IVideoEffect
    {
        public string Id => "filter.lensflare";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float k = a.Track("strength") / 100f, size = a.Track("size") / 100f;
            if (k <= 0) return s;
            var color = a.Color("color");
            float lx = s.Cx + a.Track("x") / s.Scale, ly = s.Cy + a.Track("y") / s.Scale;
            float diag = MathF.Sqrt(s.Width * s.Width + s.Height * s.Height) * size;
            byte A(float v) => (byte)Math.Clamp(v * 255, 0, 255);
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                c.DrawImage(s.Image, x, y);
                c.Save();
                c.ClipRect(SKRect.Create(x, y, s.Width, s.Height));
                c.Translate(x, y);
                using var paint = new SKPaint { BlendMode = SKBlendMode.Screen, IsAntialias = true };
                void Glow(float px, float py, float r, SKColor col, float alpha)
                {
                    using var sh = SKShader.CreateRadialGradient(new SKPoint(px, py), Math.Max(1, r), new[] { col.WithAlpha(A(alpha)), col.WithAlpha(0) }, SKShaderTileMode.Clamp);
                    paint.Shader = sh;
                    // スクリーンで重ねる。PixelBlend (作業用の画像に描いてから合成) は使わない:
                    // グラデーションの円を 1 つずつ作業用の画像に描くと、Skia で直接描くより遅くなる (設計書 22.53)
                    c.DrawCircle(px, py, r, paint);
                    paint.Shader = null;
                }
                // 光源
                Glow(lx, ly, diag * 0.28f, color, 0.55f * k);
                Glow(lx, ly, diag * 0.06f, SKColors.White, 0.95f * k);
                // 放射状の筋 (横に長く、ほかは短く)
                for (int i = 0; i < 6; i++)
                {
                    c.Save();
                    c.RotateDegrees(i * 30 + 15, lx, ly);
                    float len = (i == 0 ? 0.55f : 0.2f) * diag;
                    using var sh = SKShader.CreateRadialGradient(new SKPoint(lx, ly), len, new[] { SKColors.White.WithAlpha(A(0.6f * k)), color.WithAlpha(0) }, SKShaderTileMode.Clamp);
                    paint.Shader = sh;
                    c.DrawOval(new SKRect(lx - len, ly - len * 0.012f - 0.5f, lx + len, ly + len * 0.012f + 0.5f), paint);
                    paint.Shader = null;
                    c.Restore();
                }
                // ゴースト: 光源 → 中心 → 反対側の線の上に、色と大きさの違う丸
                float dx = s.Cx - lx, dy = s.Cy - ly;
                (float T, float R, SKColor Col, float Al)[] ghosts =
                {
                    (0.45f, 0.035f, new SKColor(255, 220, 140), 0.25f), (0.8f, 0.07f, new SKColor(140, 255, 200), 0.12f), (1.2f, 0.025f, new SKColor(255, 150, 220), 0.3f),
                    (1.45f, 0.11f, new SKColor(120, 170, 255), 0.1f), (1.9f, 0.05f, new SKColor(255, 200, 120), 0.18f),
                };
                foreach (var (t, r, col, al) in ghosts) Glow(lx + dx * t, ly + dy * t, diag * r, col, al * k);
                // 光の輪
                using (var ring = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, diag * 0.006f), Color = color.WithAlpha(A(0.18f * k)), BlendMode = SKBlendMode.Screen, IsAntialias = true })
                    c.DrawCircle(lx + dx * 2.1f, ly + dy * 2.1f, diag * 0.16f, ring);
                c.Restore();
            });
        }
    }

    /// <summary>光漏れ: 暖かい色の光が、ゆっくり形と場所を変えながら端から差し込む (絵の見えている所だけ)。</summary>
    private sealed class LightLeakFilter : IVideoEffect
    {
        public string Id => "filter.lightleak";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float k = a.Track("strength") / 100f;
            if (k <= 0) return s;
            float t = (float)a.Time * a.Track("speed");
            uint seed = (uint)(int)a.Track("seed") + 7;
            byte A(float v) => (byte)Math.Clamp(v * 255, 0, 255);
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                c.DrawImage(s.Image, x, y);
                // スクリーンのレイヤー (CPU 描画では自前で合成する。PixelBlend)
                using var layer = PixelBlend.BeginLayer(c, SKRect.Create(x, y, s.Width, s.Height), SKBlendMode.Screen);
                var lc = layer.Canvas;
                lc.Translate(x, y);
                float big = Math.Max(s.Width, s.Height);
                SKColor[] cols = { new(255, 120, 40), new(255, 60, 90), new(255, 200, 90) };
                for (int i = 0; i < 3; i++)
                {
                    float px = ValueNoise(t + i * 7.3f, 0.5f, seed + (uint)i) * 1.4f - 0.2f, py = ValueNoise(0.5f, t * 0.8f + i * 3.1f, seed + 10 + (uint)i) * 1.4f - 0.2f;
                    // 端に寄せる
                    px = px < 0.5f ? px * 0.4f : 1 - (1 - px) * 0.4f;
                    float pulse = 0.6f + 0.4f * ValueNoise(t * 1.7f, i * 5f, seed + 20);
                    using var sh = SKShader.CreateRadialGradient(new SKPoint(px * s.Width, py * s.Height), big * (0.35f + 0.25f * pulse),
                        new[] { cols[i].WithAlpha(A(0.75f * k * pulse)), cols[i].WithAlpha(0) }, SKShaderTileMode.Clamp);
                    using var p = new SKPaint { Shader = sh };
                    lc.DrawRect(SKRect.Create(0, 0, s.Width, s.Height), p);
                }
                using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn }) PixelBlend.DrawImage(lc, s.Image, 0, 0, keep);
            });
        }
    }

    // ================= 変形 =================

    /// <summary>中心 (cx, cy)・半径 R の円の中で、中心からの距離 r の点が、どの距離の点を読むか (変形の共通部分)。</summary>
    private static unsafe Sprite RadialWarp(Sprite s, EffectArgs a, float cx, float cy, float R, Func<float, float> sourceDistance, Func<float, float>? twist = null)
    {
        return Remap(s, a, (src, dst, w, h, rb) =>
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
                        float ox = x - cx, oy = y - cy, r = MathF.Sqrt(ox * ox + oy * oy);
                        if (r >= R || r < 1e-4f) { row[x] = sp[(long)y * stride + x]; continue; }
                        float d = r / R, k = sourceDistance(d) / d;
                        float sx = ox * k, sy = oy * k;
                        if (twist != null)
                        {
                            float ang = twist(d), cs = MathF.Cos(ang), sn = MathF.Sin(ang);
                            (sx, sy) = (sx * cs - sy * sn, sx * sn + sy * cs);
                        }
                        row[x] = PixelFx.Sample(sp, w, h, stride, cx + sx, cy + sy, false);
                    }
                }
            });
            return dst;
        });
    }

    /// <summary>渦巻き: 中心ほど強くねじる。</summary>
    private sealed class SwirlFilter : IVideoEffect
    {
        public string Id => "filter.swirl";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float angle = a.Track("angle") * MathF.PI / 180;
            if (MathF.Abs(angle) < 1e-3f) return s;
            float R = Math.Max(1, a.Track("radius") / s.Scale);
            return RadialWarp(s, a, s.Cx + a.Track("x") / s.Scale, s.Cy + a.Track("y") / s.Scale, R, d => d, d => angle * (1 - d) * (1 - d));
        }
    }

    /// <summary>膨張・収縮: 円の中を、ふくらませる (中心を拡大) / すぼめる。</summary>
    private sealed class Bulge : IVideoEffect
    {
        public string Id => "filter.bulge";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float amount = Math.Clamp(a.Track("amount") / 100f, -1, 1);
            if (MathF.Abs(amount) < 1e-3f) return s;
            float R = Math.Max(1, a.Track("radius") / s.Scale);
            float power = amount > 0 ? 1 + amount * 1.5f : 1 / (1 - amount * 1.5f);
            return RadialWarp(s, a, s.Cx + a.Track("x") / s.Scale, s.Cy + a.Track("y") / s.Scale, R, d => MathF.Pow(d, power));
        }
    }

    /// <summary>魚眼レンズ: 絵全体を、中心がふくらむ (魚眼) / 端が広がる (糸巻き・魚眼の補正) ようにゆがめる。</summary>
    private sealed class Fisheye : IVideoEffect
    {
        public string Id => "filter.fisheye";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float k = Math.Clamp(a.Track("strength") / 100f, -1, 1) * 0.6f;
            if (MathF.Abs(k) < 1e-3f) return s;
            float R = MathF.Sqrt(s.Width * s.Width + s.Height * s.Height) / 2 + 1;
            // r_src = r × (1 − k + k r²): 魚眼 (k > 0) は中心を拡大し、端はそのまま
            return RadialWarp(s, a, s.Width / 2f, s.Height / 2f, R, d => d * (1 - k + k * d * d));
        }
    }

    /// <summary>ミラータイル: 絵を縦横に並べて縮め、交互に鏡に写す (継ぎ目がつながる)。</summary>
    private sealed class MirrorTile : IVideoEffect
    {
        public string Id => "filter.mirrortile";
        public unsafe Sprite Apply(Sprite s, EffectArgs a)
        {
            int nx = Math.Max(1, (int)a.Track("countX")), ny = Math.Max(1, (int)a.Track("countY"));
            if (nx == 1 && ny == 1) return s;
            bool mirror = a.Check("mirror");
            return Remap(s, a, (src, dst, w, h, rb) =>
            {
                uint* sp = (uint*)src, dp = (uint*)dst;
                int stride = rb / 4;
                PixelOps.ForBands(w, h, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        uint* row = dp + (long)y * stride;
                        float v = (y + 0.5f) / h * ny;
                        int ty = (int)v;
                        float fy = v - ty;
                        if (mirror && (ty & 1) == 1) fy = 1 - fy;
                        for (int x = 0; x < w; x++)
                        {
                            float u = (x + 0.5f) / w * nx;
                            int tx = (int)u;
                            float fx = u - tx;
                            if (mirror && (tx & 1) == 1) fx = 1 - fx;
                            row[x] = PixelFx.Sample(sp, w, h, stride, fx * w - 0.5f, fy * h - 0.5f, true);
                        }
                    }
                });
                return dst;
            });
        }
    }

    // ================= 生成 =================

    /// <summary>
    /// パーティクル: 紙吹雪・雪・雨・火花・泡・きらめきを、絵の範囲に描く (時刻と番号から位置が決まるので、どのフレームからでも同じ)。
    /// 画面全体に降らせるときは、フィルタオブジェクト (調整レイヤー) か、画面いっぱいの図形にかける。
    /// </summary>
    private sealed class Particles : IVideoEffect
    {
        public string Id => "filter.particles";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int type = a.Effect.GetInt("type");
            int count = Math.Clamp((int)a.Track("count"), 0, 5000);
            if (count == 0) return s;
            float speed = a.Track("speed") / 100f, scale = a.Track("size") / 100f / s.Scale, wind = a.Track("wind") / s.Scale;
            uint seed = (uint)(int)a.Track("seed") * 131u + 17;
            float t = (float)a.Time;
            bool customColor = SKColor.TryParse(a.Effect.GetString("color"), out var custom);
            int W = s.Width, H = s.Height;
            SKColor[] confetti = { new(255, 82, 82), new(255, 196, 0), new(64, 196, 255), new(120, 220, 120), new(200, 120, 255), new(255, 140, 200) };
            return Redraw(s, a, 0, 0, 0, 0, (c, ox, oy) =>
            {
                c.DrawImage(s.Image, ox, oy);
                c.Save();
                c.ClipRect(SKRect.Create(ox, oy, W, H));
                c.Translate(ox, oy);
                using var paint = new SKPaint { IsAntialias = true };
                float R(int i, uint salt) => Rand01((uint)i, seed, salt);
                for (int i = 0; i < count; i++)
                {
                    float x0 = R(i, 1) * W, ph = R(i, 2), sp = 0.6f + 0.8f * R(i, 3), sz = (0.6f + 0.8f * R(i, 4)) * scale;
                    switch (type)
                    {
                        case 0:   // 紙吹雪: ひらひら回りながら落ちる (裏返る動きを横の縮みで表す)
                        {
                            float fall = 140 * speed * sp, period = (H + 40) / Math.Max(1, fall);
                            float age = (t / period + ph) % 1;
                            float y = -20 + age * (H + 40), x = x0 + wind * t + 25 * MathF.Sin(t * 2 * sp + ph * 6) * scale;
                            x = ((x % W) + W) % W;
                            c.Save();
                            c.Translate(x, y);
                            c.RotateDegrees((t * 180 * sp + ph * 360) % 360);
                            c.Scale(MathF.Cos(t * 5 * sp + ph * 6.28f), 1);
                            paint.Color = customColor ? custom : confetti[i % confetti.Length];
                            c.DrawRect(-5 * sz, -3 * sz, 10 * sz, 6 * sz, paint);
                            c.Restore();
                            break;
                        }
                        case 1:   // 雪: ゆっくり、ゆらゆら落ちる柔らかい丸
                        {
                            float fall = 45 * speed * sp, period = (H + 20) / Math.Max(1, fall);
                            float age = (t / period + ph) % 1;
                            float y = -10 + age * (H + 20), x = x0 + wind * t + 18 * MathF.Sin(t * sp + ph * 6) * scale;
                            x = ((x % W) + W) % W;
                            paint.Color = (customColor ? custom : SKColors.White).WithAlpha((byte)(150 + 100 * R(i, 5)));
                            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f * sz);
                            paint.MaskFilter = blur;
                            c.DrawCircle(x, y, 3 * sz, paint);
                            paint.MaskFilter = null;
                            break;
                        }
                        case 2:   // 雨: 速く落ちる斜めの線
                        {
                            float fall = 900 * speed * sp, period = (H + 60) / Math.Max(1, fall);
                            float age = (t / period + ph) % 1;
                            float y = -30 + age * (H + 60), x = x0 + wind * t * 2;
                            x = ((x % W) + W) % W;
                            float len = 22 * sz, slant = wind / Math.Max(1, fall) * len * 2;
                            paint.Color = (customColor ? custom : new SKColor(190, 210, 235)).WithAlpha(140);
                            paint.StrokeWidth = Math.Max(1, 1.3f * sz);
                            c.DrawLine(x, y, x + slant, y + len, paint);
                            break;
                        }
                        case 3:   // 火花: 下から跳ね上がって、重力で落ちながら消える
                        {
                            float life = 1.1f / Math.Max(0.05f, speed), age = ((t / life + ph) % 1) * life;
                            float vx = (R(i, 6) - 0.5f) * 420 * speed + wind, vy = -(320 + 380 * R(i, 7)) * speed, g = 700 * speed * speed;
                            float sx = W * (0.35f + 0.3f * R(i, 8)), sy = H * 0.98f;
                            float x = sx + vx * age, y = sy + vy * age + 0.5f * g * age * age;
                            float fade = 1 - age / life;
                            float tx = vx, ty = vy + g * age, tl = MathF.Max(1, MathF.Sqrt(tx * tx + ty * ty));
                            float len = 10 * sz;
                            paint.Color = (customColor ? custom : new SKColor(255, (byte)(150 + 100 * R(i, 9)), 60)).WithAlpha((byte)(255 * fade));
                            paint.StrokeWidth = Math.Max(1, 2 * sz);
                            paint.StrokeCap = SKStrokeCap.Round;
                            c.DrawLine(x, y, x - tx / tl * len, y - ty / tl * len, paint);
                            break;
                        }
                        case 4:   // 泡: 下から揺れながら上る、光る縁の丸
                        {
                            float rise = 70 * speed * sp, period = (H + 60) / Math.Max(1, rise);
                            float age = (t / period + ph) % 1;
                            float y = H + 30 - age * (H + 60), x = x0 + wind * t + 14 * MathF.Sin(t * 2 * sp + ph * 6) * scale;
                            x = ((x % W) + W) % W;
                            float r = 7 * sz;
                            var col = customColor ? custom : new SKColor(200, 235, 255);
                            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = Math.Max(1, 1.2f * sz); paint.Color = col.WithAlpha(200);
                            c.DrawCircle(x, y, r, paint);
                            paint.Style = SKPaintStyle.Fill; paint.Color = col.WithAlpha(40);
                            c.DrawCircle(x, y, r, paint);
                            paint.Color = SKColors.White.WithAlpha(200);
                            c.DrawCircle(x - r * 0.35f, y - r * 0.35f, r * 0.2f, paint);
                            break;
                        }
                        default:   // きらめき: その場で光ったり消えたりする 4 本の光
                        {
                            float y0 = R(i, 10) * H;
                            float pulse = MathF.Pow(MathF.Max(0, MathF.Sin((t * 2.5f * speed * sp + ph) * MathF.PI * 2)), 3);
                            if (pulse < 0.02f) break;
                            float r = 9 * sz * pulse;
                            paint.Color = (customColor ? custom : new SKColor(255, 250, 220)).WithAlpha((byte)(255 * pulse));
                            using var path = new SKPath();
                            path.MoveTo(x0, y0 - r); path.LineTo(x0 + r * 0.18f, y0 - r * 0.18f); path.LineTo(x0 + r, y0); path.LineTo(x0 + r * 0.18f, y0 + r * 0.18f);
                            path.LineTo(x0, y0 + r); path.LineTo(x0 - r * 0.18f, y0 + r * 0.18f); path.LineTo(x0 - r, y0); path.LineTo(x0 - r * 0.18f, y0 - r * 0.18f);
                            path.Close();
                            c.DrawPath(path, paint);
                            break;
                        }
                    }
                }
                c.Restore();
            });
        }
    }

    /// <summary>集中線・スピード線: 外から中心へ向かう細い三角形の線 / 横・縦に流れる線。毎フレーム変えると、漫画の動きのように揺れる。</summary>
    private sealed class SpeedLines : IVideoEffect
    {
        public string Id => "filter.speedlines";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int type = a.Effect.GetInt("type"), count = Math.Clamp((int)a.Track("count"), 1, 2000);
            float inner = Math.Clamp(a.Track("inner") / 100f, 0, 1), thick = a.Track("thickness") / 100f;
            var color = a.Color("color");
            uint seed = (uint)(int)a.Track("seed") * 53u + (a.Check("animate") ? (uint)(a.Frame / 2) * 2654435761u : 0);
            int W = s.Width, H = s.Height;
            return Redraw(s, a, 0, 0, 0, 0, (c, ox, oy) =>
            {
                c.DrawImage(s.Image, ox, oy);
                c.Save();
                c.ClipRect(SKRect.Create(ox, oy, W, H));
                c.Translate(ox, oy);
                using var paint = new SKPaint { IsAntialias = true, Color = color };
                using var path = new SKPath();
                float R(int i, uint salt) => Rand01((uint)i, seed, salt);
                if (type == 0)
                {
                    float cx = W / 2f, cy = H / 2f, outer = MathF.Sqrt(cx * cx + cy * cy) * 1.05f;
                    for (int i = 0; i < count; i++)
                    {
                        float ang = (i + R(i, 1) * 0.8f) / count * MathF.PI * 2;
                        float start = outer * (inner + (1 - inner) * 0.35f * R(i, 2)) * MathF.Max(0.1f, 1 - 0.15f * R(i, 4));
                        float wdt = (0.004f + 0.012f * R(i, 3)) * thick;
                        float a1 = ang - wdt, a2 = ang + wdt;
                        path.MoveTo(cx + MathF.Cos(ang) * start, cy + MathF.Sin(ang) * start);
                        path.LineTo(cx + MathF.Cos(a1) * outer, cy + MathF.Sin(a1) * outer);
                        path.LineTo(cx + MathF.Cos(a2) * outer, cy + MathF.Sin(a2) * outer);
                        path.Close();
                    }
                }
                else
                {
                    bool horizontal = type == 1;
                    float span = horizontal ? W : H, across = horizontal ? H : W;
                    for (int i = 0; i < count; i++)
                    {
                        float pos = R(i, 1) * across;
                        // 中央 (空ける範囲) には少なく
                        float mid = MathF.Abs(pos / across - 0.5f) * 2;
                        if (mid < inner * R(i, 5)) continue;
                        float len = span * (0.2f + 0.6f * R(i, 2)), start = R(i, 3) * (span + len) - len;
                        float wdt = (0.6f + 2.4f * R(i, 4)) * thick;
                        if (horizontal) { path.MoveTo(start, pos); path.LineTo(start + len, pos - wdt / 2); path.LineTo(start + len, pos + wdt / 2); }
                        else { path.MoveTo(pos, start); path.LineTo(pos - wdt / 2, start + len); path.LineTo(pos + wdt / 2, start + len); }
                        path.Close();
                    }
                }
                c.DrawPath(path, paint);
                c.Restore();
            });
        }
    }

    /// <summary>枠: 額縁 (外に縁)・ポラロイド (下を広く)・映画の黒帯 (上下を 2.39:1 に)・角丸 (角を丸く切り抜き、縁を付ける)。</summary>
    private sealed class Frame : IVideoEffect
    {
        public string Id => "filter.frame";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int type = a.Effect.GetInt("type");
            float width = Math.Max(0, a.Track("width") / s.Scale), radius = Math.Max(0, a.Track("radius") / s.Scale);
            var color = a.Color("color");
            bool shadow = a.Check("shadow");
            int W = s.Width, H = s.Height;
            switch (type)
            {
                case 2:   // 映画の黒帯
                {
                    float bar = Math.Max(0, (H - W / 2.39f) / 2);
                    if (bar < 0.5f) return s;
                    return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
                    {
                        c.DrawImage(s.Image, x, y);
                        using var p = new SKPaint { Color = SKColors.Black };
                        c.DrawRect(x, y, W, bar, p);
                        c.DrawRect(x, y + H - bar, W, bar, p);
                    });
                }
                case 3:   // 角丸
                {
                    var rect = SKRect.Create(0, 0, W, H);
                    return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
                    {
                        var r = rect; r.Offset(x, y);
                        using var path = new SKPath();
                        path.AddRoundRect(r, radius, radius);
                        c.Save();
                        c.ClipPath(path, SKClipOperation.Intersect, true);
                        c.DrawImage(s.Image, x, y);
                        c.Restore();
                        if (width > 0)
                        {
                            using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = width * 2, Color = color, IsAntialias = true };
                            c.Save();
                            c.ClipPath(path, SKClipOperation.Intersect, true);
                            c.DrawPath(path, stroke);
                            c.Restore();
                        }
                    });
                }
                default:   // 額縁・ポラロイド: 絵の外に縁を付ける
                {
                    int side = (int)MathF.Ceiling(width), bottom = type == 1 ? (int)MathF.Ceiling(width * 3.5f) : side;
                    int sh = shadow ? (int)MathF.Ceiling(Math.Max(4, width * 0.6f)) : 0;
                    var r = Redraw(s, a, side + sh, side + sh, side + sh, bottom + sh, (c, x, y) =>
                    {
                        var outer = new SKRect(x - side, y - side, x + W + side, y + H + bottom);
                        float corner = type == 0 ? Math.Min(radius, side) : 2;
                        if (shadow)
                        {
                            using var sp = new SKPaint { Color = new SKColor(0, 0, 0, 110), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sh / 2f), IsAntialias = true };
                            var so = outer; so.Offset(sh * 0.3f, sh * 0.5f);
                            c.DrawRoundRect(so, corner, corner, sp);
                        }
                        using (var fill = new SKPaint { Color = color, IsAntialias = true }) c.DrawRoundRect(outer, corner, corner, fill);
                        if (type == 0)
                        {
                            // 額縁: 内側に細い影の線
                            using var inner = new SKPaint { Color = new SKColor(0, 0, 0, 60), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, side * 0.12f), IsAntialias = true };
                            c.DrawRect(new SKRect(x - side * 0.5f, y - side * 0.5f, x + W + side * 0.5f, y + H + side * 0.5f), inner);
                        }
                        c.DrawImage(s.Image, x, y);
                    });
                    // 中心は絵の中心のまま (下を広げたポラロイドでも、写真の位置は動かない)
                    return r with { Scale = s.Scale };
                }
            }
        }
    }
}
