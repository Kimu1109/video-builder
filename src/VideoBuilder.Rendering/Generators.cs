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

    /// <summary>1 文字ごとの描き方 (透明度 0〜1・下へのずれ px・拡大率)。</summary>
    public readonly record struct CharStyle(float Alpha, float Dy, float Scale);

    /// <summary>テキストの文字 (テキストの要素) の数。改行も 1 文字として数える (テキストアニメーションの順番)。</summary>
    public static int CountChars(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Sum(l => new StringInfo(l).LengthInTextElements) + lines.Length - 1;
    }

    /// <summary>
    /// テキストの字の並び (使う書体・字ごとの位置)。文字と書式が同じなら使い回す。
    /// 作るのが重いのは書体の検索 (システムのフォントを名前や文字から探す) と字の幅の計測で、
    /// テキストアニメーションでは同じ並びのまま毎フレーム描き直すので、並びを覚えておけば描くだけで済む。
    /// 中身は作ったあと変えないので、複数のスレッド (プレビューと出力) から同時に使ってよい。
    /// </summary>
    private sealed class TextLayout
    {
        public required SKTypeface BaseFace;
        public required float Size;
        public required bool Embolden;
        public required float SkewX;
        public required int Width, Height;
        /// <summary>行ごとにまとめて描くもの (字間が 0 でアニメーションがないとき)。</summary>
        public required List<(string Text, SKTypeface Face, float X, float Y)> Runs;
        /// <summary>1 文字ずつ描くもの (字の位置・送り幅・アニメーションの順番)。</summary>
        public required List<(string Text, SKTypeface Face, float X, float Y, float Advance, int Index)> Chars;
        /// <summary>拡大の中心にする高さ (基準線からの距離)。</summary>
        public required float MiddleFromBaseline;
        /// <summary>書体を持っておく (使っている間に破棄されないように)。</summary>
        public required List<SKTypeface> Faces;
    }

    private static readonly Dictionary<string, TextLayout?> Layouts = new();
    private static readonly LinkedList<string> LayoutOrder = new();
    private const int MaxLayouts = 64;

    /// <summary>字の並びを作った回数 (計測・テスト用)。</summary>
    public static long TextLayoutsBuilt => Interlocked.Read(ref _layoutsBuilt);
    private static long _layoutsBuilt;

    private static TextLayout? LayoutFor(string text, string family, float size, bool bold, bool italic, int align, float spacing, float leading, bool animated)
    {
        string key = string.Join('\u0001', text, family, F(size), bold, italic, align, F(spacing), F(leading), animated);
        lock (Layouts)
        {
            if (Layouts.TryGetValue(key, out var known))
            {
                LayoutOrder.Remove(key);
                LayoutOrder.AddFirst(key);
                return known;
            }
        }
        var made = BuildLayout(text, family, size, bold, italic, align, spacing, leading, animated);
        Interlocked.Increment(ref _layoutsBuilt);
        lock (Layouts)
        {
            if (Layouts.TryAdd(key, made)) LayoutOrder.AddFirst(key);
            // 古いものは忘れる (書体は使っている描画があるかもしれないので、ここでは破棄しない)
            while (LayoutOrder.Count > MaxLayouts) { Layouts.Remove(LayoutOrder.Last!.Value); LayoutOrder.RemoveLast(); }
            return Layouts[key];
        }
    }

    private static TextLayout? BuildLayout(string text, string family, float size, bool bold, bool italic, int align, float spacing, float leading, bool animated)
    {
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        // 同梱フォントは使い回す
        SKTypeface? bundledFace = null;
        lock (Bundled)
        {
            if (Bundled.TryGetValue(family, out var b)) bundledFace = bold && b.Bold != null ? b.Bold : b.Regular;
        }
        var baseFace = bundledFace ?? SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
        var faces = new List<SKTypeface> { baseFace };
        using var font = new SKFont(baseFace, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        // 指定スタイルの書体がないフォントは、疑似的に太字・斜体にする
        if (bold && !baseFace.IsBold) font.Embolden = true;
        if (italic && !baseFace.IsItalic) font.SkewX = -0.25f;

        var fallbacks = new Dictionary<int, SKTypeface?>();
        SKTypeface FaceFor(int cp)
        {
            if (baseFace.ContainsGlyph(cp) || cp < 0x20) return baseFace;
            if (!fallbacks.TryGetValue(cp, out var f))
            {
                f = SKFontManager.Default.MatchCharacter(family, style, null, cp);
                if (f != null) faces.Add(f);
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

        var m = font.Metrics;
        float lineHeight = m.Descent - m.Ascent + m.Leading + leading;
        float maxWidth = lines.Max(l => l.Width);
        const int pad = 2;
        int padY = pad + (animated ? (int)MathF.Ceiling(size * 0.7f) : 0);
        int w = (int)MathF.Ceiling(maxWidth) + pad * 2 + (italic ? (int)(size * 0.25f) : 0);
        int h = (int)MathF.Ceiling(lineHeight * lines.Count - leading) + padY * 2;
        if (w <= pad * 2 || h <= 0 || w > 16384 || h > 16384) return null;

        var runList = new List<(string, SKTypeface, float, float)>();
        var chars = new List<(string, SKTypeface, float, float, float, int)>();
        float y = padY - m.Ascent;
        int index = 0;
        foreach (var (runs, width) in lines)
        {
            float x = pad + align switch { 1 => (maxWidth - width) / 2, 2 => maxWidth - width, _ => 0 };
            foreach (var run in runs)
            {
                runList.Add((run.Text, run.Face, x, y));
                float cx = x;
                var e = StringInfo.GetTextElementEnumerator(run.Text);
                while (e.MoveNext())
                {
                    string el = e.GetTextElement();
                    float adv = MeasureRaw(el, run.Face);
                    chars.Add((el, run.Face, cx, y, adv, index++));
                    cx += adv + spacing;
                }
                x += run.Width;
            }
            y += lineHeight;
            index++;   // 改行も 1 文字
        }
        return new TextLayout
        {
            BaseFace = baseFace, Size = size, Embolden = font.Embolden, SkewX = font.SkewX, Width = w, Height = h,
            Runs = runList, Chars = chars, MiddleFromBaseline = (m.Ascent + m.Descent) / 2, Faces = faces,
        };
    }

    /// <param name="perChar">
    /// null でなければ、文字ごとに描き方を変える (テキストアニメーション)。引数は文字の番号 (改行も 1 文字)。
    /// 字の並びはアニメーションがないときと同じ。下からのずれ・拡大で絵からはみ出さないよう、上下に余白を足す (中心は変わらない)。
    /// </param>
    public static SKImage? RenderText(string text, string family, float size, SKColor color, bool bold, bool italic, int align, float spacing, float leading,
        Func<int, CharStyle>? perChar = null)
    {
        if (string.IsNullOrEmpty(text) || size <= 0) return null;
        if (string.IsNullOrWhiteSpace(family)) family = DefaultFontFamily;
        var layout = LayoutFor(text, family, size, bold, italic, align, spacing, leading, perChar != null);
        if (layout == null) return null;

        using var font = new SKFont(layout.BaseFace, layout.Size) { Subpixel = true, Edging = SKFontEdging.Antialias, Embolden = layout.Embolden, SkewX = layout.SkewX };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        // 描く先は使い回しのバッファにする (アニメーション中は毎フレーム作るので、確保し直さない)
        var info = Effects.Info(layout.Width, layout.Height);
        int rowBytes = info.Width * 4;
        var buffer = PixelPool.Rent((long)rowBytes * info.Height);
        using (var surface = SKSurface.Create(info, buffer, rowBytes))
        {
            if (surface == null) { PixelPool.Return(buffer, (long)rowBytes * info.Height); return null; }
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            if (perChar != null)
            {
                // 1 文字ずつ、決めた透明度・ずれ・拡大率で描く
                foreach (var (el, face, x, y, adv, index) in layout.Chars)
                {
                    var st = perChar(index);
                    if (st.Alpha <= 0 || st.Scale <= 0) continue;
                    font.Typeface = face;
                    paint.Color = color.WithAlpha((byte)Math.Clamp(color.Alpha * st.Alpha, 0, 255));
                    canvas.Save();
                    canvas.Translate(0, st.Dy);
                    if (st.Scale != 1) canvas.Scale(st.Scale, st.Scale, x + adv / 2, y + layout.MiddleFromBaseline);
                    canvas.DrawText(el, x, y, font, paint);
                    canvas.Restore();
                }
            }
            else if (spacing == 0)
            {
                foreach (var (t, face, x, y) in layout.Runs) { font.Typeface = face; canvas.DrawText(t, x, y, font, paint); }
            }
            else
            {
                foreach (var (el, face, x, y, _, _) in layout.Chars) { font.Typeface = face; canvas.DrawText(el, x, y, font, paint); }
            }
            canvas.Flush();
        }
        return PixelPool.ToImage(buffer, info, rowBytes);
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
