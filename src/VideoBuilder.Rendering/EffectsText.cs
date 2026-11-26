using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>
/// 文字を飾る効果 (どのオブジェクトにも使える): 背景の帯・内側シャドー・立体 (押し出し)・背景と被る色を反転。
/// 「背景と被る色を反転」は、オブジェクトを画面に重ねるときに背景を読んで色を決めるので、効果そのものは絵を変えず、
/// 描画 (RenderEngine.DrawPlaced) が DrawOverlapInvert を呼ぶ。
/// </summary>
public static partial class Effects
{
    public const string OverlapInvertId = "filter.overlapinvert";

    private static void RegisterText()
    {
        Register(new Backplate()); Register(new InnerShadow()); Register(new Extrude()); Register(new OverlapInvertMarker());
    }

    /// <summary>不透明な部分 (アルファが 0 でない部分) を囲む四角。読めない・全部透明なら null。</summary>
    internal static unsafe SKRectI? OpaqueBounds(SKImage image)
    {
        var info = Info(image.Width, image.Height);
        int rowBytes = info.Width * 4;
        long bytes = (long)rowBytes * info.Height;
        var buffer = PixelPool.Rent(bytes);
        try
        {
            if (!image.ReadPixels(info, buffer, rowBytes, 0, 0)) return null;
            int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
            for (int y = 0; y < info.Height; y++)
            {
                byte* row = (byte*)buffer + (long)y * rowBytes;
                for (int x = 0; x < info.Width; x++)
                {
                    if (row[x * 4 + 3] == 0) continue;
                    if (x < l) l = x;
                    if (x > r) r = x;
                    if (y < t) t = y;
                    b = y;
                }
            }
            return r < 0 ? null : new SKRectI(l, t, r + 1, b + 1);
        }
        finally { PixelPool.Return(buffer, bytes); }
    }

    /// <summary>背景の帯 (座布団): 文字の後ろに、余白・角の丸み・枠線付きの四角を敷く。</summary>
    private sealed class Backplate : IVideoEffect
    {
        public string Id => "filter.backplate";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float px = Math.Max(0, a.Track("padx")), py = Math.Max(0, a.Track("pady")), radius = Math.Max(0, a.Track("radius")), line = Math.Max(0, a.Track("line"));
            float opacity = Math.Clamp(1 - a.Track("alpha") / 100f, 0, 1);
            // 文字に合わせる: 不透明な部分を囲む (テキストの上下の余白や、アニメーション用の余白を含めない)
            var inner = a.Effect.GetInt("fit") == 0 && OpaqueBounds(s.Image) is { } ob ? SKRect.Create(ob.Left, ob.Top, ob.Width, ob.Height) : SKRect.Create(s.Width, s.Height);
            var plate = SKRect.Create(inner.Left - px, inner.Top - py, inner.Width + px * 2, inner.Height + py * 2);
            float half = line / 2;
            int padL = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(Math.Max(0, -(plate.Left - half)));
            int padT = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(Math.Max(0, -(plate.Top - half)));
            int padR = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(Math.Max(0, plate.Right + half - s.Width));
            int padB = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(Math.Max(0, plate.Bottom + half - s.Height));
            using var fill = new SKPaint { Color = a.Color("color").WithAlpha((byte)(opacity * a.Color("color").Alpha)), IsAntialias = true };
            using var stroke = line > 0 ? new SKPaint { Color = a.Color("linecolor"), IsAntialias = true, IsStroke = true, StrokeWidth = line } : null;
            return Redraw(s, a, padL, padT, padR, padB, (c, x, y) =>
            {
                var r = plate;
                r.Offset(x, y);
                float rad = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
                if (fill.Color.Alpha > 0) c.DrawRoundRect(r, rad, rad, fill);
                if (stroke != null) c.DrawRoundRect(r, rad, rad, stroke);
                c.DrawImage(s.Image, x, y);
            });
        }
    }

    /// <summary>内側シャドー: 形の内側の縁に、ずらした影を落とす (絵の大きさは変えない)。</summary>
    private sealed class InnerShadow : IVideoEffect
    {
        public string Id => "filter.innershadow";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            float dx = a.Track("x"), dy = a.Track("y"), spread = Math.Max(0, a.Track("spread"));
            float density = Math.Clamp(a.Track("density") / 100f, 0, 1);
            if (density <= 0) return s;
            var color = a.Color("color");
            using var shade = new SKPaint { Color = color.WithAlpha((byte)(density * 255)) };
            using var blur = spread > 0 ? SKImageFilter.CreateBlur(spread / 2, spread / 2) : null;
            using var hole = new SKPaint { BlendMode = SKBlendMode.DstOut, ImageFilter = blur };
            using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
            return Redraw(s, a, 0, 0, 0, 0, (c, x, y) =>
            {
                c.DrawImage(s.Image, x, y);
                // 影の層: 一面に影の色を塗り、ずらした形 (ぼかしたもの) を抜いて、元の形の中だけを残す
                var bounds = SKRect.Create(x, y, s.Width, s.Height);
                // 影のレイヤー (CPU 描画では自前で合成する。PixelBlend)
                using var layer = PixelBlend.BeginLayer(c, bounds, SKBlendMode.SrcOver);
                var lc = layer.Canvas;
                lc.DrawRect(bounds, shade);
                // 抜く形はぼかしで広がるので、範囲はレイヤー全体にする (DstOut は範囲が広くても結果は同じ)
                PixelBlend.Draw(lc, bounds, hole, (cv, p) => cv.DrawImage(s.Image, x + dx, y + dy, p));
                PixelBlend.DrawImage(lc, s.Image, x, y, keep);
            });
        }
    }

    /// <summary>立体 (押し出し): 形を向きに沿って奥へ押し出したように、色を変えながら重ねて描く。</summary>
    private sealed class Extrude : IVideoEffect
    {
        public string Id => "filter.extrude";
        public Sprite Apply(Sprite s, EffectArgs a)
        {
            int depth = Math.Clamp((int)MathF.Round(a.Track("depth")), 0, 200);
            if (depth <= 0) return s;
            float rad = a.Track("angle") * MathF.PI / 180f;
            float ux = MathF.Cos(rad), uy = MathF.Sin(rad);
            float ex = ux * depth, ey = uy * depth;
            int padL = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(Math.Max(0, -ex)), padR = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(Math.Max(0, ex));
            int padT = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(Math.Max(0, -ey)), padB = a.IsFrameBuffer ? 0 : (int)MathF.Ceiling(Math.Max(0, ey));
            SKColor near = a.Color("color"), far = a.Color("color2");
            return Redraw(s, a, padL, padT, padR, padB, (c, x, y) =>
            {
                // 奥から手前へ 1 ピクセルずつ。奥ほど「奥の色」に近づける
                for (int k = depth; k >= 1; k--)
                {
                    float t = depth <= 1 ? 0 : (k - 1) / (float)(depth - 1);
                    var col = new SKColor(Lerp(near.Red, far.Red, t), Lerp(near.Green, far.Green, t), Lerp(near.Blue, far.Blue, t), Lerp(near.Alpha, far.Alpha, t));
                    using var cf = SKColorFilter.CreateBlendMode(col, SKBlendMode.SrcIn);
                    using var p = new SKPaint { ColorFilter = cf };
                    c.DrawImage(s.Image, x + ux * k, y + uy * k, p);
                }
                c.DrawImage(s.Image, x, y);
            });
        }

        private static byte Lerp(byte a, byte b, float t) => (byte)Math.Clamp(MathF.Round(a + (b - a) * t), 0, 255);
    }

    /// <summary>背景と被る色を反転: 絵そのものは変えない (重ねるときに RenderEngine が DrawOverlapInvert を使う)。</summary>
    private sealed class OverlapInvertMarker : IVideoEffect
    {
        public string Id => OverlapInvertId;
        public Sprite Apply(Sprite s, EffectArgs a) => s;
    }

    /// <summary>背景と被る色を反転の設定。Range・Soft・Strength は 0〜1。Mode は 0 = 背景の反転色、1 = 白か黒 (見やすい方)。</summary>
    public readonly record struct OverlapInvertParams(float Range, float Soft, float Strength, int Mode);

    /// <summary>
    /// 絵の 1 画素ずつ、下にある背景の色と比べて、近ければ (色の距離が Range 以下) 背景の反転色 (または白か黒) にする。
    /// Range から Range + Soft の間は、なめらかに混ぜる。layer は重ねる絵 (BGRA・乗算済み)、background は同じ範囲の背景 (BGRA・乗算済み)。
    /// layer をその場で書き換える。背景が透明なところは黒の上にあるとみなす。
    /// </summary>
    public static unsafe void OverlapInvertPixels(IntPtr layer, IntPtr background, int width, int height, int rowBytes, OverlapInvertParams p)
    {
        float r0 = p.Range, r1 = p.Range + Math.Max(0.001f, p.Soft), strength = Math.Clamp(p.Strength, 0, 1);
        if (strength <= 0) return;
        Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = height >= 64 ? Environment.ProcessorCount : 1 }, y =>
        {
            byte* L = (byte*)layer + (long)y * rowBytes;
            byte* B = (byte*)background + (long)y * rowBytes;
            for (int x = 0; x < width; x++, L += 4, B += 4)
            {
                int a = L[3];
                if (a == 0) continue;
                float inv = 255f / a;
                // 絵の色 (乗算を戻す) と背景の色 (黒の上に重ねた色 = 乗算済みのまま)
                float sb = L[0] * inv / 255f, sg = L[1] * inv / 255f, sr = L[2] * inv / 255f;
                float bb = B[0] / 255f, bg = B[1] / 255f, br = B[2] / 255f;
                float d = MathF.Sqrt(((sr - br) * (sr - br) + (sg - bg) * (sg - bg) + (sb - bb) * (sb - bb)) / 3f);
                float t;
                if (d <= r0) t = 1;
                else if (d >= r1) continue;
                else { float q = (d - r0) / (r1 - r0); t = 1 - q * q * (3 - 2 * q); }
                t *= strength;
                float tb, tg, tr;
                if (p.Mode == 1)
                {
                    float lum = 0.2126f * br + 0.7152f * bg + 0.0722f * bb;
                    tb = tg = tr = lum > 0.5f ? 0 : 1;
                }
                else { tb = 1 - bb; tg = 1 - bg; tr = 1 - br; }
                float ob = sb + (tb - sb) * t, og = sg + (tg - sg) * t, or = sr + (tr - sr) * t;
                L[0] = (byte)Math.Clamp(MathF.Round(ob * a), 0, a);
                L[1] = (byte)Math.Clamp(MathF.Round(og * a), 0, a);
                L[2] = (byte)Math.Clamp(MathF.Round(or * a), 0, a);
            }
        });
    }

    /// <summary>
    /// 「背景と被る色を反転」で絵を重ねる。canvas には絵を置く変換がかかっていて、絵は (-Cx, -Cy) に描く前提。
    /// 重ねる範囲の背景を surface から読み、変換した絵を同じ範囲に描いたものと画素ごとに比べて色を決め、そのまま重ねる。
    /// 描けたら true (false なら呼び出し元がふつうに描く)。
    /// </summary>
    public static unsafe bool DrawOverlapInvert(SKCanvas canvas, SKSurface surface, Sprite sprite, SKPaint paint, OverlapInvertParams p)
    {
        var m = canvas.TotalMatrix;
        var corners = new[]
        {
            new SKPoint(-sprite.Cx, -sprite.Cy), new SKPoint(sprite.Width - sprite.Cx, -sprite.Cy),
            new SKPoint(sprite.Width - sprite.Cx, sprite.Height - sprite.Cy), new SKPoint(-sprite.Cx, sprite.Height - sprite.Cy),
        };
        var mapped = m.MapPoints(corners);
        float minX = mapped.Min(q => q.X), minY = mapped.Min(q => q.Y), maxX = mapped.Max(q => q.X), maxY = mapped.Max(q => q.Y);
        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY)) return false;
        var region = SKRectI.Intersect(new SKRectI((int)MathF.Floor(minX) - 1, (int)MathF.Floor(minY) - 1, (int)MathF.Ceiling(maxX) + 1, (int)MathF.Ceiling(maxY) + 1), canvas.DeviceClipBounds);
        if (region.Width <= 0 || region.Height <= 0) return true;   // 画面の外
        if ((long)region.Width * region.Height > 64L * 1024 * 1024) return false;

        var info = Info(region.Width, region.Height);
        int rowBytes = info.Width * 4;
        long bytes = (long)rowBytes * info.Height;
        var layer = PixelPool.Rent(bytes);
        var back = PixelPool.Rent(bytes);
        try
        {
            // 背景 (GPU の描画先でも読み戻せる)
            if (!surface.ReadPixels(info, back, rowBytes, region.Left, region.Top)) return false;
            // 変換した絵を、同じ範囲の作業用の絵に描く
            using (var layerSurface = SKSurface.Create(info, layer, rowBytes))
            {
                if (layerSurface == null) return false;
                var lc = layerSurface.Canvas;
                lc.Clear(SKColors.Transparent);
                lc.Translate(-region.Left, -region.Top);
                lc.Concat(ref m);
                using var copy = paint.Clone();
                copy.BlendMode = SKBlendMode.SrcOver;
                lc.DrawImage(sprite.Image, -sprite.Cx, -sprite.Cy, copy);
                lc.Flush();
            }
            OverlapInvertPixels(layer, back, region.Width, region.Height, rowBytes, p);
            // 作業用のバッファはすぐ返すので、複製した画像で描く
            using var result = SKImage.FromPixelCopy(info, layer, rowBytes);
            if (result == null) return false;
            canvas.Save();
            canvas.ResetMatrix();
            using var over = new SKPaint { BlendMode = SKBlendMode.SrcOver };
            canvas.DrawImage(result, region.Left, region.Top, over);
            canvas.Restore();
            return true;
        }
        finally
        {
            PixelPool.Return(layer, bytes);
            PixelPool.Return(back, bytes);
        }
    }
}
