using System.Text;
using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// 図形オブジェクトの見た目の設定 (図形の種類・サイズ・ライン幅・色以外)。
/// 既定値は「今までの図形」と同じ見た目になる。割合 (%) は図形の枠の幅・高さに対する割合。
/// </summary>
public sealed record ShapeStyle
{
    public static readonly ShapeStyle Default = new();

    // ---- 形 ----
    /// <summary>角の丸み (px)。</summary>
    public float Corner { get; init; }
    /// <summary>多角形・星の頂点の数。</summary>
    public int Points { get; init; } = 5;
    /// <summary>星のへこみ: 内側の頂点の、外側に対する距離 (%)。</summary>
    public float Inner { get; init; } = 38.2f;
    /// <summary>台形・平行四辺形の傾き (%)。</summary>
    public float Slant { get; init; } = 25;
    /// <summary>矢じり (山形矢印では先のとがり) の長さ (幅に対する %)。</summary>
    public float Head { get; init; } = 40;
    /// <summary>矢じりの幅 (高さに対する %)。</summary>
    public float HeadWidth { get; init; } = 100;
    /// <summary>矢印の軸・十字・角括弧の太さ (%)。直線では枠の高さの 1/4 を 100% とする。</summary>
    public float Thickness { get; init; } = 40;
    /// <summary>吹き出しのしっぽ: 横の位置 (-100 = 左端, 100 = 右端)・長さ (高さに対する %)・根元の幅 (幅に対する %)。</summary>
    public float TailX { get; init; } = -40;
    public float TailLength { get; init; } = 25;
    public float TailWidth { get; init; } = 20;
    /// <summary>扇形: 開始角度 (度。0 = 真上、時計回り) と中心角 (度)。</summary>
    public float StartAngle { get; init; }
    public float Sweep { get; init; } = 270;
    /// <summary>円・扇形の穴の大きさ (%)。</summary>
    public float Hole { get; init; }
    /// <summary>直線の始点・終点の形 (EffectRegistry.LineEnds の番号)。</summary>
    public int StartCap { get; init; }
    public int EndCap { get; init; } = 1;

    // ---- 塗り ----
    /// <summary>EffectRegistry.ShapeFills の番号 (0 = 単色, 1 = 線形, 2 = 円形グラデーション)。</summary>
    public int Fill { get; init; }
    public SKColor Color2 { get; init; } = SKColors.Black;
    /// <summary>線形グラデーションの向き (度。0 = 左 → 右、90 = 上 → 下)。</summary>
    public float GradientAngle { get; init; } = 90;
    /// <summary>2 色のちょうど中間になる位置 (%)。</summary>
    public float GradientCenter { get; init; } = 50;

    // ---- 縁の線 ----
    public float Border { get; init; }
    public SKColor BorderColor { get; init; } = SKColors.Black;

    // ---- 線の描き方 ----
    /// <summary>EffectRegistry.LineStyles の番号 (0 = 実線, 1 = 破線, 2 = 点線)。</summary>
    public int Dash { get; init; }
    public float DashLength { get; init; } = 20;
    public float DashGap { get; init; } = 12;
    public float DashOffset { get; init; }
    /// <summary>EffectRegistry.LineJoins の番号。</summary>
    public int Join { get; init; }

    /// <summary>描画のキャッシュのキー。</summary>
    public string Key()
    {
        var b = new StringBuilder();
        foreach (var v in new object[]
                 {
                     Corner, Points, Inner, Slant, Head, HeadWidth, Thickness, TailX, TailLength, TailWidth, StartAngle, Sweep, Hole, StartCap, EndCap,
                     Fill, Color2, GradientAngle, GradientCenter, Border, BorderColor, Dash, DashLength, DashGap, DashOffset, Join,
                 })
            b.Append(v is float f ? Generators.F(f) : v.ToString()).Append(',');
        return b.ToString();
    }

    /// <summary>図形オブジェクトのパラメータから作る。</summary>
    public static ShapeStyle From(Effect e, IReadOnlyList<int> bounds, int frame)
    {
        float T(string key) => (float)e.Eval(key, bounds, frame);
        static SKColor Col(string s, SKColor fallback) => SKColor.TryParse(s, out var c) ? c : fallback;
        return new ShapeStyle
        {
            Corner = T("corner"), Points = (int)Math.Round(T("points")), Inner = T("inner"), Slant = T("slant"),
            Head = T("head"), HeadWidth = T("headWidth"), Thickness = T("thickness"),
            TailX = T("tailX"), TailLength = T("tailLength"), TailWidth = T("tailWidth"),
            StartAngle = T("startAngle"), Sweep = T("sweep"), Hole = T("hole"),
            StartCap = e.GetInt("startCap"), EndCap = e.GetInt("endCap"),
            Fill = e.GetInt("fill"), Color2 = Col(e.GetString("color2"), SKColors.Black),
            GradientAngle = T("gradAngle"), GradientCenter = T("gradCenter"),
            Border = T("border"), BorderColor = Col(e.GetString("borderColor"), SKColors.Black),
            Dash = e.GetInt("dash"), DashLength = T("dashLength"), DashGap = T("dashGap"), DashOffset = T("dashOffset"), Join = e.GetInt("join"),
        };
    }
}

/// <summary>図形の形 (パス) を作って描く。</summary>
public static class Shapes
{
    /// <param name="type">EffectRegistry.ShapeTypes の番号。</param>
    public static SKImage? Render(int type, float size, float aspect, float line, SKColor color, ShapeStyle style, int screenW, int screenH)
    {
        var kind = (ShapeType)type;
        if (!Enum.IsDefined(kind)) kind = ShapeType.Star;   // 今までどおり、知らない番号は星型
        float w, h;
        if (kind == ShapeType.Background) { w = screenW; h = screenH; }
        else
        {
            if (size <= 0) return null;
            w = h = size;
            // 縦横比: 正で横を縮め、負で縦を縮める (AviUtl と同じ向き)
            if (aspect > 0) w *= 1 - aspect / 100f; else h *= 1 + aspect / 100f;
        }
        int iw = (int)MathF.Ceiling(w), ih = (int)MathF.Ceiling(h);
        if (iw <= 0 || ih <= 0 || iw > 16384 || ih > 16384) return null;

        // ライン幅が図形の半分より細いときは、塗らずに線で描く (直線はいつも塗りの形)
        bool outline = kind is not (ShapeType.Background or ShapeType.Line) && line > 0 && line < Math.Min(w, h) / 2;
        float stroke = outline ? line : 0;
        float border = Math.Max(0, style.Border);
        // 線は図形の枠からはみ出さないよう、線の太さの半分だけ内側に形を作る
        float inset = Math.Max(stroke, border) / 2;
        var rect = new SKRect(inset, inset, w - inset, h - inset);
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        using var path = BuildPath(kind, rect, style);
        if (path == null) return null;

        using var surface = SKSurface.Create(Effects.Info(iw, ih));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        // 軸にそろった四角形は、ぼかさずにくっきり描く
        bool crisp = kind is ShapeType.Background or ShapeType.Rectangle && style.Corner <= 0;
        using var shader = FillShader(style, color, new SKRect(0, 0, w, h));
        using var paint = new SKPaint { Color = color, Shader = shader, IsAntialias = !crisp };
        if (outline) Stroke(paint, stroke, style, dashed: true);
        c.DrawPath(path, paint);
        if (border > 0)
        {
            using var edge = new SKPaint { Color = style.BorderColor, IsAntialias = !crisp };
            Stroke(edge, border, style, dashed: kind != ShapeType.Line);
            c.DrawPath(path, edge);
        }
        return surface.Snapshot();
    }

    /// <summary>線の太さ・破線・角の形を設定する。</summary>
    private static void Stroke(SKPaint paint, float width, ShapeStyle style, bool dashed)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = width;
        paint.StrokeJoin = Join(style.Join);
        if (!dashed) return;
        var effect = DashEffect(style, width);
        if (effect == null) return;
        paint.PathEffect = effect;
        if (style.Dash == 2) paint.StrokeCap = SKStrokeCap.Round;
    }

    private static SKStrokeJoin Join(int join) => join switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };

    /// <summary>破線 (点線) の模様。実線なら null。点線は線の太さの丸を、間隔をあけて並べる。</summary>
    private static SKPathEffect? DashEffect(ShapeStyle style, float width)
    {
        float gap = Math.Max(0, style.DashGap);
        float[] intervals;
        switch (style.Dash)
        {
            case 1:
                float on = Math.Max(0.5f, style.DashLength);
                if (gap <= 0) return null;
                intervals = new[] { on, gap };
                break;
            case 2:
                // 長さ 0 に近い線分に丸い端を付けると丸になる
                intervals = new[] { 0.01f, Math.Max(0.5f, width + gap) - 0.01f };
                break;
            default: return null;
        }
        return SKPathEffect.CreateDash(intervals, style.DashOffset);
    }

    /// <summary>塗りの色。単色なら null (SKPaint.Color を使う)。</summary>
    private static SKShader? FillShader(ShapeStyle style, SKColor color, SKRect bounds)
    {
        if (style.Fill is not (1 or 2)) return null;
        float mid = Math.Clamp(style.GradientCenter, 1, 99) / 100f;
        var middle = Lerp(color, style.Color2, 0.5f);
        var colors = new[] { color, middle, style.Color2 };
        var positions = new[] { 0f, mid, 1f };
        float cx = bounds.MidX, cy = bounds.MidY;
        if (style.Fill == 1)
        {
            double a = style.GradientAngle * Math.PI / 180;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            // 向きに沿って、枠の端から端までがちょうど 0〜1 になる長さ
            float half = Math.Abs(dx) * bounds.Width / 2 + Math.Abs(dy) * bounds.Height / 2;
            return SKShader.CreateLinearGradient(new SKPoint(cx - dx * half, cy - dy * half), new SKPoint(cx + dx * half, cy + dy * half), colors, positions, SKShaderTileMode.Clamp);
        }
        // 円形: 中心から枠の辺までが 0〜1。縦横比に合わせて楕円にする
        float r = Math.Max(1, bounds.Width / 2);
        return SKShader.CreateRadialGradient(new SKPoint(cx, cy), r, colors, positions, SKShaderTileMode.Clamp,
            SKMatrix.CreateScale(1, bounds.Height / Math.Max(1, bounds.Width), cx, cy));
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)Math.Round(a.Red + (b.Red - a.Red) * t), (byte)Math.Round(a.Green + (b.Green - a.Green) * t),
        (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * t), (byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * t));

    /// <summary>図形の形。rect の中に収まるように作る。描くものがないときは null。</summary>
    public static SKPath? BuildPath(ShapeType kind, SKRect rect, ShapeStyle style)
    {
        float x0 = rect.Left, y0 = rect.Top, x1 = rect.Right, y1 = rect.Bottom, w = rect.Width, h = rect.Height, cx = rect.MidX, cy = rect.MidY;
        float corner = Math.Max(0, style.Corner);
        var path = new SKPath();
        switch (kind)
        {
            case ShapeType.Background:
                path.AddRect(rect);
                return path;
            case ShapeType.Rectangle:
                if (corner > 0) path.AddRoundRect(rect, Math.Min(corner, w / 2), Math.Min(corner, h / 2));
                else path.AddRect(rect);
                return path;
            case ShapeType.Circle:
                path.AddOval(rect);
                AddHole(path, rect, style.Hole);
                return path;
            case ShapeType.Heart:
                Heart(path, rect);
                return path;
            case ShapeType.BubbleRect:
            case ShapeType.BubbleOval:
                path.Dispose();
                return Bubble(kind == ShapeType.BubbleOval, rect, style);
            case ShapeType.Pie:
                Pie(path, rect, style);
                break;
            case ShapeType.Line:
                path.Dispose();
                return Line(rect, style);
            case ShapeType.Triangle: Polygon(path, rect, 3, 1); break;
            case ShapeType.Pentagon: Polygon(path, rect, 5, 1); break;
            case ShapeType.Hexagon: Polygon(path, rect, 6, 1); break;
            case ShapeType.Polygon: Polygon(path, rect, Math.Clamp(style.Points, 3, 100), 1); break;
            case ShapeType.Star: Polygon(path, rect, Math.Clamp(style.Points, 3, 100) * 2, Math.Clamp(style.Inner, 1, 100) / 100f); break;
            case ShapeType.Diamond:
                Poly(path, (cx, y0), (x1, cy), (cx, y1), (x0, cy));
                break;
            case ShapeType.Trapezoid:
            {
                // 正で上の辺が短く、負で下の辺が短い
                float s = Math.Clamp(style.Slant, -100, 100) / 100f * w / 2, top = Math.Max(0, s), bottom = Math.Max(0, -s);
                Poly(path, (x0 + top, y0), (x1 - top, y0), (x1 - bottom, y1), (x0 + bottom, y1));
                break;
            }
            case ShapeType.Parallelogram:
            {
                // 正で上の辺が右にずれる
                float s = Math.Clamp(style.Slant, -100, 100) / 100f * w / 2, o = Math.Abs(s);
                if (s >= 0) Poly(path, (x0 + o, y0), (x1, y0), (x1 - o, y1), (x0, y1));
                else Poly(path, (x0, y0), (x1 - o, y0), (x1, y1), (x0 + o, y1));
                break;
            }
            case ShapeType.Arrow:
            case ShapeType.DoubleArrow:
            {
                bool both = kind == ShapeType.DoubleArrow;
                float head = Math.Clamp(style.Head, 0, 100) / 100f * w;
                if (both) head = Math.Min(head, w / 2);
                float shaft = Math.Clamp(style.Thickness, 1, 100) / 100f * h / 2;
                float tip = Math.Max(shaft, Math.Clamp(style.HeadWidth, 0, 100) / 100f * h / 2);
                if (both)
                    Poly(path, (x0, cy), (x0 + head, cy - tip), (x0 + head, cy - shaft), (x1 - head, cy - shaft), (x1 - head, cy - tip), (x1, cy),
                        (x1 - head, cy + tip), (x1 - head, cy + shaft), (x0 + head, cy + shaft), (x0 + head, cy + tip));
                else
                    Poly(path, (x0, cy - shaft), (x1 - head, cy - shaft), (x1 - head, cy - tip), (x1, cy), (x1 - head, cy + tip), (x1 - head, cy + shaft), (x0, cy + shaft));
                break;
            }
            case ShapeType.Chevron:
            {
                float d = Math.Clamp(style.Head, 0, 100) / 100f * w / 2;
                Poly(path, (x0, y0), (x1 - d, y0), (x1, cy), (x1 - d, y1), (x0, y1), (x0 + d, cy));
                break;
            }
            case ShapeType.Cross:
            {
                float t = Math.Clamp(style.Thickness, 1, 100) / 100f, hx = w * t / 2, hy = h * t / 2;
                Poly(path, (cx - hx, y0), (cx + hx, y0), (cx + hx, cy - hy), (x1, cy - hy), (x1, cy + hy), (cx + hx, cy + hy),
                    (cx + hx, y1), (cx - hx, y1), (cx - hx, cy + hy), (x0, cy + hy), (x0, cy - hy), (cx - hx, cy - hy));
                break;
            }
            case ShapeType.Bracket:
            {
                // 縦の棒と上下の腕は同じ太さ
                float t = Math.Clamp(style.Thickness, 1, 100) / 100f * Math.Min(w, h / 2);
                Poly(path, (x0, y0), (x1, y0), (x1, y0 + t), (x0 + t, y0 + t), (x0 + t, y1 - t), (x1, y1 - t), (x1, y1), (x0, y1));
                break;
            }
        }
        return corner > 0 ? RoundCorners(path, corner) : path;
    }

    /// <summary>直線の角を丸める (path は捨てる)。</summary>
    private static SKPath RoundCorners(SKPath path, float radius)
    {
        using (path)
        using (var effect = SKPathEffect.CreateCorner(radius))
        using (var paint = new SKPaint { Style = SKPaintStyle.Fill, PathEffect = effect })
        {
            var rounded = new SKPath { FillType = path.FillType };
            paint.GetFillPath(path, rounded);
            return rounded;
        }
    }

    private static void Poly(SKPath path, params (float X, float Y)[] points)
    {
        path.MoveTo(points[0].X, points[0].Y);
        for (int i = 1; i < points.Length; i++) path.LineTo(points[i].X, points[i].Y);
        path.Close();
    }

    internal static void Polygon(SKPath path, SKRect rect, int points, float innerRatio)
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

    /// <summary>真ん中に穴を開ける (外側と逆回りの楕円を足す)。</summary>
    private static void AddHole(SKPath path, SKRect rect, float hole)
    {
        float k = Math.Clamp(hole, 0, 99) / 100f;
        if (k <= 0) return;
        float rx = rect.Width / 2 * k, ry = rect.Height / 2 * k;
        path.FillType = SKPathFillType.EvenOdd;
        path.AddOval(new SKRect(rect.MidX - rx, rect.MidY - ry, rect.MidX + rx, rect.MidY + ry));
    }

    private static void Pie(SKPath path, SKRect rect, ShapeStyle style)
    {
        float sweep = Math.Clamp(style.Sweep, 0, 360);
        if (sweep <= 0) return;
        float k = Math.Clamp(style.Hole, 0, 99) / 100f;
        if (sweep >= 360)
        {
            path.AddOval(rect);
            AddHole(path, rect, style.Hole);
            return;
        }
        // Skia の角度は 3 時の向きが 0。ここでは真上を 0 にする
        float start = style.StartAngle - 90;
        float rx = rect.Width / 2 * k, ry = rect.Height / 2 * k;
        var inner = new SKRect(rect.MidX - rx, rect.MidY - ry, rect.MidX + rx, rect.MidY + ry);
        path.ArcTo(rect, start, sweep, true);
        if (k > 0) path.ArcTo(inner, start + sweep, -sweep, false);
        else path.LineTo(rect.MidX, rect.MidY);
        path.Close();
    }

    private static void Heart(SKPath path, SKRect r)
    {
        SKPoint P(float x, float y) => new(r.Left + x * r.Width, r.Top + y * r.Height);
        path.MoveTo(P(0.5f, 0.22f));
        path.CubicTo(P(0.42f, 0.05f), P(0.25f, 0f), P(0.12f, 0.06f));
        path.CubicTo(P(0f, 0.12f), P(0f, 0.35f), P(0.06f, 0.48f));
        path.CubicTo(P(0.15f, 0.66f), P(0.35f, 0.8f), P(0.5f, 1f));
        path.CubicTo(P(0.65f, 0.8f), P(0.85f, 0.66f), P(0.94f, 0.48f));
        path.CubicTo(P(1f, 0.35f), P(1f, 0.12f), P(0.88f, 0.06f));
        path.CubicTo(P(0.75f, 0f), P(0.58f, 0.05f), P(0.5f, 0.22f));
        path.Close();
    }

    /// <summary>吹き出し: 本体 (角丸の四角形か楕円) の下に、しっぽの三角形を付ける。</summary>
    private static SKPath Bubble(bool oval, SKRect rect, ShapeStyle style)
    {
        float tail = Math.Clamp(style.TailLength, 0, 90) / 100f * rect.Height;
        var body = new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom - tail);
        using var bodyPath = new SKPath();
        if (oval) bodyPath.AddOval(body);
        else
        {
            float r = Math.Max(0, style.Corner);
            if (r > 0) bodyPath.AddRoundRect(body, Math.Min(r, body.Width / 2), Math.Min(r, body.Height / 2));
            else bodyPath.AddRect(body);
        }
        if (tail <= 0) return new SKPath(bodyPath);

        float pos = Math.Clamp(style.TailX, -100, 100) / 100f;
        float halfBase = Math.Clamp(style.TailWidth, 1, 100) / 100f * rect.Width / 2;
        // 根元は本体の中に置く (楕円は下の方が細いので、内側に寄せる)
        float reach = oval ? body.Width / 2 * 0.6f : body.Width / 2 - Math.Min(Math.Max(0, style.Corner), body.Width / 2);
        float baseX = body.MidX + pos * Math.Max(0, reach - halfBase);
        float baseY = oval ? body.MidY + body.Height / 2 * 0.6f : body.Bottom - Math.Min(body.Height / 2, Math.Max(1, Math.Min(Math.Max(0, style.Corner), body.Height / 2)));
        float tipX = Math.Clamp(baseX + pos * rect.Width * 0.15f, rect.Left, rect.Right);
        using var tailPath = new SKPath();
        Poly(tailPath, (baseX - halfBase, baseY), (baseX + halfBase, baseY), (tipX, rect.Bottom));
        return bodyPath.Op(tailPath, SKPathOp.Union) ?? new SKPath(bodyPath);
    }

    /// <summary>
    /// 直線 (左から右) と、両端の飾り。塗りの形として返す。
    /// 飾りは枠の高さの大きさ。線の太さは、端の飾りが目立つよう「枠の高さの 1/4」を 100% とする。
    /// </summary>
    private static SKPath Line(SKRect rect, ShapeStyle style)
    {
        float h = rect.Height, cy = rect.MidY;
        float thick = Math.Max(0.5f, Math.Clamp(style.Thickness, 1, 100) / 100f * h / 4);
        // 飾りの大きさは枠の高さ。長さが足りないときは縮める
        float cap = Math.Min(h, rect.Width / 2);
        float start = rect.Left + CapInset(style.StartCap, cap, thick), end = rect.Right - CapInset(style.EndCap, cap, thick);

        var result = new SKPath();
        if (end > start)
        {
            using var center = new SKPath();
            center.MoveTo(start, cy);
            center.LineTo(end, cy);
            using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = thick, StrokeCap = SKStrokeCap.Butt };
            using var dash = DashEffect(style, thick);
            if (dash != null) { paint.PathEffect = dash; if (style.Dash == 2) paint.StrokeCap = SKStrokeCap.Round; }
            paint.GetFillPath(center, result);
        }
        AddCap(ref result, style.StartCap, rect.Left, cy, -1, cap, thick, style.Join);
        AddCap(ref result, style.EndCap, rect.Right, cy, 1, cap, thick, style.Join);
        return result;
    }

    /// <summary>飾りの分だけ、線を端から縮める長さ。</summary>
    private static float CapInset(int capType, float cap, float thick) => capType switch
    {
        1 => thick / 2,          // 矢印: 先端まで線を伸ばす (線の太さの半分は先端の角に使う)
        2 => cap * 0.5f,         // 三角: 三角の中ほどまで
        3 or 4 => cap / 2,       // 円・四角: 真ん中まで
        _ => 0,
    };

    /// <summary>端の飾りを足す。edge は端の x、dir は外向き (-1 = 左, 1 = 右)。</summary>
    private static void AddCap(ref SKPath path, int capType, float edge, float cy, int dir, float cap, float thick, int join)
    {
        if (capType is < 1 or > 4) return;
        using var shape = new SKPath();
        float half = cap / 2;
        switch (capType)
        {
            case 1:
            {
                // 開いた矢印 (く の字)。線と同じ太さ
                float tip = edge - dir * thick / 2, back = tip - dir * Math.Max(0, cap - thick);
                float arm = Math.Max(0, half - thick / 2);
                using var v = new SKPath();
                v.MoveTo(back, cy - arm);
                v.LineTo(tip, cy);
                v.LineTo(back, cy + arm);
                using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = thick, StrokeJoin = Join(join), StrokeCap = join == 1 ? SKStrokeCap.Round : SKStrokeCap.Butt };
                paint.GetFillPath(v, shape);
                break;
            }
            case 2:
                Poly(shape, (edge, cy), (edge - dir * cap, cy - half), (edge - dir * cap, cy + half));
                break;
            case 3:
                shape.AddCircle(edge - dir * half, cy, half);
                break;
            case 4:
                shape.AddRect(new SKRect(Math.Min(edge, edge - dir * cap), cy - half, Math.Max(edge, edge - dir * cap), cy + half));
                break;
        }
        var joined = path.Op(shape, SKPathOp.Union);
        if (joined == null) return;
        path.Dispose();
        path = joined;
    }
}
