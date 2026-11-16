using System.Globalization;
using System.Text;
using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>テキストと図形の絵を作る。</summary>
public static class Generators
{
    private static readonly string[] DefaultFamilies = { "Noto Sans CJK JP", "Noto Sans JP", "IPAGothic", "Yu Gothic UI", "Hiragino Sans", "Meiryo" };
    private static string? _defaultFamily;

    private static readonly Dictionary<string, (SKTypeface Regular, SKTypeface? Bold)> Bundled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>アプリに同梱したフォントを登録する。最初に登録したものがテキストの既定フォントになる。</summary>
    public static void RegisterBundledFont(SKTypeface regular, SKTypeface? bold)
    {
        lock (Bundled)
        {
            if (Bundled.Count == 0) _defaultFamily = regular.FamilyName;
            Bundled[regular.FamilyName] = (regular, bold);
        }
    }

    public static string DefaultFontFamily
    {
        get
        {
            if (_defaultFamily != null) return _defaultFamily;
            var installed = new HashSet<string>(SKFontManager.Default.FontFamilies, StringComparer.OrdinalIgnoreCase);
            _defaultFamily = DefaultFamilies.FirstOrDefault(installed.Contains) ?? SKTypeface.Default.FamilyName;
            return _defaultFamily;
        }
    }

    public static IReadOnlyList<string> FontFamilies
    {
        get
        {
            string[] bundled;
            lock (Bundled) bundled = Bundled.Keys.ToArray();
            return bundled.Concat(SKFontManager.Default.FontFamilies.Except(bundled, StringComparer.OrdinalIgnoreCase)
                .Distinct().OrderBy(f => f, StringComparer.CurrentCulture)).ToList();
        }
    }

    private readonly record struct Run(string Text, SKTypeface Face, float Width);

    public static SKImage? RenderText(string text, string family, float size, SKColor color, bool bold, bool italic, int align, float spacing, float leading)
    {
        if (string.IsNullOrEmpty(text) || size <= 0) return null;
        if (string.IsNullOrWhiteSpace(family)) family = DefaultFontFamily;
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        // 同梱フォントは使い回すので破棄しない
        SKTypeface? bundledFace = null;
        lock (Bundled)
        {
            if (Bundled.TryGetValue(family, out var b)) bundledFace = bold && b.Bold != null ? b.Bold : b.Regular;
        }
        using var ownedFace = bundledFace == null ? SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default : null;
        var baseFace = bundledFace ?? ownedFace!;
        using var font = new SKFont(baseFace, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        // 指定スタイルの書体がないフォントは、疑似的に太字・斜体にする
        if (bold && !baseFace.IsBold) font.Embolden = true;
        if (italic && !baseFace.IsItalic) font.SkewX = -0.25f;
        using var paint = new SKPaint { Color = color, IsAntialias = true };

        var fallbacks = new Dictionary<int, SKTypeface?>();
        var owned = new List<SKTypeface>();
        SKTypeface FaceFor(int cp)
        {
            if (baseFace.ContainsGlyph(cp) || cp < 0x20) return baseFace;
            if (!fallbacks.TryGetValue(cp, out var f))
            {
                f = SKFontManager.Default.MatchCharacter(family, style, null, cp);
                if (f != null) owned.Add(f);
                fallbacks[cp] = f;
            }
            return f ?? baseFace;
        }

        using var measure = new SKPaint { TextSize = size, IsAntialias = true, SubpixelText = true, FakeBoldText = font.Embolden, TextSkewX = font.SkewX };
        float MeasureRaw(string s, SKTypeface face)
        {
            measure.Typeface = face;
            return measure.MeasureText(s);
        }

        float Measure(string s, SKTypeface face)
        {
            font.Typeface = face;
            float w = MeasureRaw(s, face);
            if (spacing != 0) w += spacing * new StringInfo(s).LengthInTextElements;
            return w;
        }

        var lines = new List<(List<Run> Runs, float Width)>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var runs = new List<Run>();
            var sb = new StringBuilder();
            SKTypeface? current = null;
            foreach (var rune in line.EnumerateRunes())
            {
                var face = FaceFor(rune.Value);
                if (current != null && face != current && sb.Length > 0)
                {
                    runs.Add(new Run(sb.ToString(), current, Measure(sb.ToString(), current)));
                    sb.Clear();
                }
                current = face;
                sb.Append(rune.ToString());
            }
            if (sb.Length > 0 && current != null) runs.Add(new Run(sb.ToString(), current, Measure(sb.ToString(), current)));
            lines.Add((runs, runs.Sum(r => r.Width)));
        }

        font.Typeface = baseFace;
        var m = font.Metrics;
        float lineHeight = m.Descent - m.Ascent + m.Leading + leading;
        float maxWidth = lines.Max(l => l.Width);
        const int pad = 2;
        int w = (int)MathF.Ceiling(maxWidth) + pad * 2 + (italic ? (int)(size * 0.25f) : 0);
        int h = (int)MathF.Ceiling(lineHeight * lines.Count - leading) + pad * 2;
        if (w <= pad * 2 || h <= 0 || w > 16384 || h > 16384) { Cleanup(); return null; }

        using var surface = SKSurface.Create(Effects.Info(w, h));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        float y = pad - m.Ascent;
        foreach (var (runs, width) in lines)
        {
            float x = pad + align switch { 1 => (maxWidth - width) / 2, 2 => maxWidth - width, _ => 0 };
            foreach (var run in runs)
            {
                font.Typeface = run.Face;
                if (spacing == 0) canvas.DrawText(run.Text, x, y, font, paint);
                else
                {
                    float cx = x;
                    var e = StringInfo.GetTextElementEnumerator(run.Text);
                    while (e.MoveNext())
                    {
                        string el = e.GetTextElement();
                        canvas.DrawText(el, cx, y, font, paint);
                        cx += MeasureRaw(el, run.Face) + spacing;
                    }
                }
                x += run.Width;
            }
            y += lineHeight;
        }
        var img = surface.Snapshot();
        Cleanup();
        return img;

        void Cleanup()
        {
            font.Typeface = baseFace;
            foreach (var f in owned) f.Dispose();
        }
    }

    /// <param name="type">EffectRegistry.ShapeTypes の番号。</param>
    public static SKImage? RenderShape(int type, float size, float aspect, float line, SKColor color, int screenW, int screenH)
    {
        float w, h;
        if (type == 0) { w = screenW; h = screenH; }
        else
        {
            if (size <= 0) return null;
            w = h = size;
            // 縦横比: 正で横を縮め、負で縦を縮める (AviUtl と同じ向き)
            if (aspect > 0) w *= 1 - aspect / 100f; else h *= 1 + aspect / 100f;
        }
        int iw = (int)MathF.Ceiling(w), ih = (int)MathF.Ceiling(h);
        if (iw <= 0 || ih <= 0 || iw > 16384 || ih > 16384) return null;
        using var surface = SKSurface.Create(Effects.Info(iw, ih));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        bool fill = type == 0 || line <= 0 || line >= Math.Min(w, h) / 2;
        using var paint = new SKPaint { Color = color, IsAntialias = type != 0 && type != 2, Style = fill ? SKPaintStyle.Fill : SKPaintStyle.Stroke, StrokeWidth = line, StrokeJoin = SKStrokeJoin.Miter };
        float inset = fill ? 0 : line / 2;
        var rect = new SKRect(inset, inset, w - inset, h - inset);
        using var path = new SKPath();
        switch (type)
        {
            case 0: case 2: path.AddRect(rect); break;
            case 1: path.AddOval(rect); break;
            case 3: Polygon(path, rect, 3, 1); break;
            case 4: Polygon(path, rect, 5, 1); break;
            case 5: Polygon(path, rect, 6, 1); break;
            default: Polygon(path, rect, 10, 0.382f); break;
        }
        // 線のときは図形の外にはみ出さないよう内側だけ描く
        if (!fill) { c.Save(); }
        c.DrawPath(path, paint);
        return surface.Snapshot();
    }

    private static void Polygon(SKPath path, SKRect rect, int points, float innerRatio)
    {
        float cx = rect.MidX, cy = rect.MidY, rx = rect.Width / 2, ry = rect.Height / 2;
        for (int i = 0; i < points; i++)
        {
            float ratio = innerRatio < 1 && i % 2 == 1 ? innerRatio : 1;
            double ang = -Math.PI / 2 + i * 2 * Math.PI / points;
            float x = cx + (float)Math.Cos(ang) * rx * ratio, y = cy + (float)Math.Sin(ang) * ry * ratio;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        path.Close();
    }

    internal static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
