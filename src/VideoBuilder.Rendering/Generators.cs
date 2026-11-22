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

    /// <summary>テキストの飾り (縦書き・下線・取り消し線)。既定は横書きで線なし。</summary>
    public readonly record struct TextDecor(bool Vertical, bool Underline, bool Strike)
    {
        public string Key => $"{(Vertical ? 1 : 0)}{(Underline ? 1 : 0)}{(Strike ? 1 : 0)}";
    }

    /// <summary>
    /// 1 文字の置き方。(X, Y) は立てて描くときの基準線の位置。Rotate なら (CenterX, CenterY) を中心に 90 度回して描く (縦書きの括弧・長音など)。
    /// Span0〜Span1 は行の向きに沿った文字の範囲、Pos は線を引く基準 (横書きは基準線の高さ、縦書きは列の中心)。線は行 (Line) ごとにまとめて引く。
    /// </summary>
    private readonly record struct Glyph(string Text, SKTypeface Face, float X, float Y, float Advance, int Index,
        bool Rotate, float CenterX, float CenterY, int Line, float Span0, float Span1, float Pos);

    // 縦書きで 90 度回して描く文字 (括弧・長音・ダッシュ・波線・三点リーダーなど)
    private const string VerticalRotated = "ー－―‐-–—～〜~…‥「」『』（）()［］[]【】〈〉《》｛｝{}<>＜＞→←＝=：:；;｜|＿_";
    // 縦書きで右上に寄せる文字 (句読点)
    private const string VerticalPunct = "、。，．､｡,.";
    // 縦書きで少し右上に寄せる小さい仮名
    private const string VerticalSmall = "ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ";

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
        public required List<Glyph> Chars;
        /// <summary>縦書き (Runs は使わず、いつも 1 文字ずつ描く)。</summary>
        public required bool Vertical;
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

    private static TextLayout? LayoutFor(string text, string family, float size, bool bold, bool italic, int align, float spacing, float leading, bool animated, bool vertical)
    {
        string key = string.Join('\u0001', text, family, F(size), bold, italic, align, F(spacing), F(leading), animated, vertical);
        lock (Layouts)
        {
            if (Layouts.TryGetValue(key, out var known))
            {
                LayoutOrder.Remove(key);
                LayoutOrder.AddFirst(key);
                return known;
            }
        }
        var made = BuildLayout(text, family, size, bold, italic, align, spacing, leading, animated, vertical);
        Interlocked.Increment(ref _layoutsBuilt);
        lock (Layouts)
        {
            if (Layouts.TryAdd(key, made)) LayoutOrder.AddFirst(key);
            // 古いものは忘れる (書体は使っている描画があるかもしれないので、ここでは破棄しない)
            while (LayoutOrder.Count > MaxLayouts) { Layouts.Remove(LayoutOrder.Last!.Value); LayoutOrder.RemoveLast(); }
            return Layouts[key];
        }
    }

    private static TextLayout? BuildLayout(string text, string family, float size, bool bold, bool italic, int align, float spacing, float leading, bool animated, bool vertical)
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
        if (vertical) return BuildVertical(text, size, align, spacing, lineHeight, animated, italic, baseFace, font.Embolden, font.SkewX, faces, FaceFor, MeasureRaw, m);
        float maxWidth = lines.Max(l => l.Width);
        const int pad = 2;
        int padY = pad + (animated ? (int)MathF.Ceiling(size * 0.7f) : 0);
        int w = (int)MathF.Ceiling(maxWidth) + pad * 2 + (italic ? (int)(size * 0.25f) : 0);
        int h = (int)MathF.Ceiling(lineHeight * lines.Count - leading) + padY * 2;
        if (w <= pad * 2 || h <= 0 || w > 16384 || h > 16384) return null;

        var runList = new List<(string, SKTypeface, float, float)>();
        var chars = new List<Glyph>();
        float middle = (m.Ascent + m.Descent) / 2;
        float y = padY - m.Ascent;
        int index = 0, lineNo = 0;
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
                    chars.Add(new Glyph(el, run.Face, cx, y, adv, index++, false, cx + adv / 2, y + middle, lineNo, cx, cx + adv, y));
                    cx += adv + spacing;
                }
                x += run.Width;
            }
            y += lineHeight;
            index++;   // 改行も 1 文字
            lineNo++;
        }
        return new TextLayout
        {
            BaseFace = baseFace, Size = size, Embolden = font.Embolden, SkewX = font.SkewX, Width = w, Height = h,
            Runs = runList, Chars = chars, MiddleFromBaseline = middle, Faces = faces, Vertical = false,
        };
    }

    /// <summary>
    /// 縦書きの並び。行は右から左へ並ぶ列になり、文字は上から下へ 1 文字ずつ (送りは文字の大きさ + 字間) 並ぶ。
    /// 揃え (左寄せ・中央・右寄せ) は、縦書きでは上揃え・中央・下揃えになる。括弧や長音は 90 度回し、句読点・小さい仮名は右上に寄せる。
    /// </summary>
    private static TextLayout? BuildVertical(string text, float size, int align, float spacing, float columnPitch, bool animated, bool italic,
        SKTypeface baseFace, bool embolden, float skewX, List<SKTypeface> faces, Func<int, SKTypeface> faceFor, Func<string, SKTypeface, float> measure, SKFontMetrics m)
    {
        var columns = text.Replace("\r\n", "\n").Split('\n').Select(l =>
        {
            var els = new List<string>();
            var e = StringInfo.GetTextElementEnumerator(l);
            while (e.MoveNext()) els.Add(e.GetTextElement());
            return els;
        }).ToList();
        float cell = size + spacing;
        float Height(int n) => n <= 0 ? 0 : n * cell - spacing;
        float maxHeight = Math.Max(size, columns.Max(c => Height(c.Count)));
        // 列の幅は、右側に引く傍線 (下線) が入るよう、文字の大きさの 1.25 倍以上にする
        float colWidth = Math.Max(size * 1.25f, columnPitch);
        const int pad = 2;
        int padY = pad + (animated ? (int)MathF.Ceiling(size * 0.7f) : 0);
        int w = (int)MathF.Ceiling(colWidth * columns.Count) + pad * 2 + (italic ? (int)(size * 0.25f) : 0);
        int h = (int)MathF.Ceiling(maxHeight) + padY * 2;
        if (w <= pad * 2 || h <= padY * 2 || w > 16384 || h > 16384) return null;

        float middle = (m.Ascent + m.Descent) / 2;
        var chars = new List<Glyph>();
        int index = 0;
        for (int c = 0; c < columns.Count; c++)
        {
            float centerX = w - pad - colWidth * c - colWidth / 2;
            float colH = Height(columns[c].Count);
            float top = padY + align switch { 1 => (maxHeight - colH) / 2, 2 => maxHeight - colH, _ => 0 };
            for (int k = 0; k < columns[c].Count; k++)
            {
                string el = columns[c][k];
                var face = faceFor(char.ConvertToUtf32(el, 0));
                float adv = measure(el, face);
                float cellTop = top + k * cell, centerY = cellTop + size / 2;
                bool rotate = el.Length == 1 && VerticalRotated.Contains(el[0]);
                float dx = 0, dy = 0;
                if (el.Length == 1 && VerticalPunct.Contains(el[0])) { dx = size * 0.6f; dy = -size * 0.6f; }
                else if (el.Length == 1 && VerticalSmall.Contains(el[0])) { dx = size * 0.1f; dy = -size * 0.1f; }
                chars.Add(new Glyph(el, face, centerX - adv / 2 + dx, centerY - middle + dy, adv, index++, rotate, centerX, centerY, c, cellTop, cellTop + size, centerX));
            }
            index++;   // 改行も 1 文字
        }
        return new TextLayout
        {
            BaseFace = baseFace, Size = size, Embolden = embolden, SkewX = skewX, Width = w, Height = h,
            Runs = new(), Chars = chars, MiddleFromBaseline = middle, Faces = faces, Vertical = true,
        };
    }

    /// <param name="perChar">
    /// null でなければ、文字ごとに描き方を変える (テキストアニメーション)。引数は文字の番号 (改行も 1 文字)。
    /// 字の並びはアニメーションがないときと同じ。下からのずれ・拡大で絵からはみ出さないよう、上下に余白を足す (中心は変わらない)。
    /// </param>
    /// <param name="decor">縦書き・下線・取り消し線。</param>
    public static SKImage? RenderText(string text, string family, float size, SKColor color, bool bold, bool italic, int align, float spacing, float leading,
        Func<int, CharStyle>? perChar = null, TextDecor decor = default)
    {
        if (string.IsNullOrEmpty(text) || size <= 0) return null;
        if (string.IsNullOrWhiteSpace(family)) family = DefaultFontFamily;
        var layout = LayoutFor(text, family, size, bold, italic, align, spacing, leading, perChar != null, decor.Vertical);
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
            bool lines = decor.Underline || decor.Strike;
            if (perChar != null)
            {
                // 1 文字ずつ、決めた透明度・ずれ・拡大率で描く (線も 1 文字ずつ、その文字の透明度で)
                foreach (var g in layout.Chars)
                {
                    var st = perChar(g.Index);
                    if (st.Alpha <= 0 || st.Scale <= 0) continue;
                    paint.Color = color.WithAlpha((byte)Math.Clamp(color.Alpha * st.Alpha, 0, 255));
                    canvas.Save();
                    canvas.Translate(0, st.Dy);
                    if (st.Scale != 1) canvas.Scale(st.Scale, st.Scale, g.CenterX, g.CenterY);
                    DrawGlyph(canvas, font, paint, g, layout);
                    if (lines) DrawLines(canvas, paint, layout, decor, g.Span0, g.Span1 + (layout.Vertical ? 0 : spacing), g.Pos);
                    canvas.Restore();
                }
            }
            else
            {
                if (layout.Vertical || spacing != 0) foreach (var g in layout.Chars) DrawGlyph(canvas, font, paint, g, layout);
                else foreach (var (t, face, x, y) in layout.Runs) { font.Typeface = face; canvas.DrawText(t, x, y, font, paint); }
                if (lines)
                    foreach (var line in layout.Chars.GroupBy(g => g.Line))
                        DrawLines(canvas, paint, layout, decor, line.Min(g => g.Span0), line.Max(g => g.Span1), line.First().Pos);
            }
            canvas.Flush();
        }
        return PixelPool.ToImage(buffer, info, rowBytes);
    }

    private static void DrawGlyph(SKCanvas canvas, SKFont font, SKPaint paint, Glyph g, TextLayout layout)
    {
        font.Typeface = g.Face;
        if (!g.Rotate) { canvas.DrawText(g.Text, g.X, g.Y, font, paint); return; }
        canvas.Save();
        canvas.RotateDegrees(90, g.CenterX, g.CenterY);
        canvas.DrawText(g.Text, g.CenterX - g.Advance / 2, g.CenterY - layout.MiddleFromBaseline, font, paint);
        canvas.Restore();
    }

    /// <summary>
    /// 下線・取り消し線を引く。横書きは from〜to が横の範囲で pos が基準線の高さ。縦書きは縦の範囲で pos が列の中心
    /// (下線は文字の右側の傍線になる)。太さは文字の大きさの 6%。
    /// </summary>
    private static void DrawLines(SKCanvas canvas, SKPaint paint, TextLayout layout, TextDecor decor, float from, float to, float pos)
    {
        if (to <= from) return;
        float size = layout.Size, thick = Math.Max(1, size * 0.06f);
        void Bar(float across)
        {
            var r = layout.Vertical ? SKRect.Create(across - thick / 2, from, thick, to - from) : SKRect.Create(from, across - thick / 2, to - from, thick);
            canvas.DrawRect(r, paint);
        }
        if (layout.Vertical)
        {
            if (decor.Underline) Bar(pos + size * 0.58f);
            if (decor.Strike) Bar(pos);
        }
        else
        {
            if (decor.Underline) Bar(pos + size * 0.12f);
            if (decor.Strike) Bar(pos + layout.MiddleFromBaseline);
        }
    }

    /// <param name="type">EffectRegistry.ShapeTypes の番号。</param>
    public static SKImage? RenderShape(int type, float size, float aspect, float line, SKColor color, int screenW, int screenH, ShapeStyle? style = null) =>
        Shapes.Render(type, size, aspect, line, color, style ?? ShapeStyle.Default, screenW, screenH);

    internal static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
