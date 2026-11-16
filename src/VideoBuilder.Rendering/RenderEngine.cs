using System.Numerics;
using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// プロジェクトと時刻からフレームを合成する。インスタンスは 1 つのスレッドで使う
/// (プレビュー用と出力用は別インスタンスにする)。
/// </summary>
public sealed class RenderEngine : IDisposable
{
    private const int MaxSceneDepth = 4;

    public MediaCache Media { get; } = new();
    /// <summary>GPU 描画に使うコンテキスト。null なら CPU 描画。</summary>
    public GpuContext? Gpu { get; }
    private SKSurface? _gpuSurface;

    public RenderEngine(GpuContext? gpu = null) => Gpu = gpu;

    // テキスト・図形は、パラメータが同じ間は作り直さない
    private readonly Dictionary<string, SKImage?> _generated = new();
    private readonly Dictionary<string, long> _generatedUse = new();
    private long _tick;

    /// <summary>
    /// 直前に描いたフレームでの、各オブジェクトの四隅 (プロジェクト座標, 左上から時計回り)。
    /// プレビューの選択枠とクリック判定に使う。キーはオブジェクト ID、順序は描画順。
    /// </summary>
    public List<(string Id, SKPoint[] Quad)> LastQuads { get; } = new();

    private readonly record struct GroupState(int FromLayer, int ToLayer, float X, float Y, float Scale, float Rotation, float Alpha);

    /// <summary>カメラ: ワールド座標 → カメラ座標 (X 右, Y 下, Z 奥) の変換と、焦点距離。</summary>
    private readonly record struct CameraState(int FromLayer, int ToLayer, Matrix4x4 View, float Focal);

    /// <summary>カメラ制御がないときの視点。Z = 0 の平面がちょうど等倍で映る。</summary>
    public const float DefaultCameraDistance = 1024;

    private static Matrix4x4 LookAt(Vector3 eye, Vector3 target, float rollDegrees)
    {
        var forward = target - eye;
        if (forward.LengthSquared() < 1e-6f) forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        // 真上・真下を向いたときは、基準にする「下」方向を切り替える
        var downRef = Math.Abs(forward.Y) > 0.999f ? Vector3.UnitZ * -Math.Sign(forward.Y) : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(downRef, forward));
        var down = Vector3.Cross(forward, right);
        if (rollDegrees != 0)
        {
            float r = rollDegrees * MathF.PI / 180f, c = MathF.Cos(r), sn = MathF.Sin(r);
            (right, down) = (right * c + down * sn, down * c - right * sn);
        }
        var rot = new Matrix4x4(
            right.X, down.X, forward.X, 0,
            right.Y, down.Y, forward.Y, 0,
            right.Z, down.Z, forward.Z, 0,
            0, 0, 0, 1);
        return Matrix4x4.CreateTranslation(-eye) * rot;
    }

    /// <summary>surface いっぱいにフレームを描く。surface の大きさがプロジェクトより小さければ縮小して描く。</summary>
    public void Render(Project project, Scene scene, int frame, SKSurface surface)
    {
        var ctx = new RenderContext { Project = project, Frame = frame, Gpu = _gpuSurface != null && ReferenceEquals(surface, _gpuSurface) ? Gpu?.Context : null };
        try
        {
            var canvas = surface.Canvas;
            LastQuads.Clear();
            canvas.ResetMatrix();
            canvas.Clear(SKColors.Black);
            float scale = surface.Canvas.DeviceClipBounds.Width / (float)project.Settings.Width;
            canvas.Scale(scale);
            RenderScene(ctx, scene, frame, surface, 0);
            canvas.Flush();
        }
        finally { ctx.DisposeTemporaries(); }
    }

    /// <summary>プロジェクトの解像度でフレームを作って返す。</summary>
    public SKImage RenderImage(Project project, Scene scene, int frame)
    {
        using var bitmap = new SKBitmap(Effects.Info(project.Settings.Width, project.Settings.Height));
        RenderToBitmap(project, scene, frame, bitmap);
        return SKImage.FromBitmap(bitmap);   // ビットマップが変更可能なので、ここで内容が複製される
    }

    /// <summary>
    /// dest の大きさでフレームを描いて、ピクセルを dest に入れる。
    /// GPU が使えるときは GPU で描いてから読み戻し、使えないときは CPU で直接描く。
    /// </summary>
    public void RenderToBitmap(Project project, Scene scene, int frame, SKBitmap dest)
    {
        if (Gpu != null)
        {
            Gpu.MakeCurrent();
            if (_gpuSurface == null || _gpuSize != (dest.Width, dest.Height))
            {
                _gpuSurface?.Dispose();
                _gpuSurface = SKSurface.Create(Gpu.Context, false, new SKImageInfo(dest.Width, dest.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                _gpuSize = (dest.Width, dest.Height);
            }
            if (_gpuSurface != null)
            {
                Render(project, scene, frame, _gpuSurface);
                if (_gpuSurface.ReadPixels(dest.Info, dest.GetPixels(), dest.RowBytes, 0, 0)) return;
            }
        }
        using var surface = SKSurface.Create(dest.Info, dest.GetPixels(), dest.RowBytes);
        Render(project, scene, frame, surface);
    }

    private (int, int) _gpuSize;

    private void RenderScene(RenderContext root, Scene scene, int frame, SKSurface surface, int depth)
    {
        var project = root.Project;
        var ctx = depth == 0 ? root : new RenderContext { Project = project, Frame = frame, Gpu = root.Gpu };
        var canvas = surface.Canvas;
        int W = project.Settings.Width, H = project.Settings.Height;
        var groups = new List<GroupState>();
        var cameras = new List<CameraState>();

        try
        {
            foreach (var obj in scene.ObjectsAt(frame))
            {
                if (!obj.Enabled || obj.Effects.Count == 0 || !scene.GetLayer(obj.Layer).Visible) continue;
                var bounds = obj.Bounds;
                var main = obj.Main;
                var def = main.Definition;

                switch (def.Category)
                {
                    case EffectCategory.Control when def.Id == EffectRegistry.GroupControl:
                    {
                        int n = (int)main.Eval("layers", bounds, frame);
                        groups.Add(new GroupState(obj.Layer + 1, n <= 0 ? int.MaxValue : obj.Layer + n,
                            (float)main.Eval("x", bounds, frame), (float)main.Eval("y", bounds, frame),
                            (float)main.Eval("scale", bounds, frame) / 100f, (float)main.Eval("rotation", bounds, frame),
                            1 - (float)main.Eval("alpha", bounds, frame) / 100f));
                        break;
                    }
                    case EffectCategory.Control when def.Id == EffectRegistry.CameraControl:
                    {
                        float E(string k) => (float)main.Eval(k, bounds, frame);
                        int n = (int)E("layers");
                        float fov = E("fov");
                        float focal = fov > 0.5f ? H / 2f / MathF.Tan(fov * MathF.PI / 360f) : DefaultCameraDistance;
                        cameras.Add(new CameraState(obj.Layer + 1, n <= 0 ? int.MaxValue : obj.Layer + n,
                            LookAt(new Vector3(E("x"), E("y"), E("z")), new Vector3(E("tx"), E("ty"), E("tz")), E("roll")), focal));
                        break;
                    }
                    case EffectCategory.Filter:
                        ApplyToFrameBuffer(ctx, obj, bounds, surface);
                        break;
                    case EffectCategory.VideoSource:
                    {
                        var src = GetSource(ctx, obj, bounds, frame, depth);
                        if (src == null) break;
                        var sprite = Sprite.Centered(src);
                        for (int i = 1; i < obj.Effects.Count; i++)
                        {
                            var e = obj.Effects[i];
                            if (!e.Enabled || e.Definition.Category != EffectCategory.Filter) continue;
                            var fx = Effects.Find(e.DefinitionId);
                            if (fx != null)
                                sprite = fx.Apply(sprite, new EffectArgs { Effect = e, Object = obj, Bounds = bounds, Context = ctx });
                        }
                        DrawSprite(canvas, sprite, obj, bounds, frame, groups, cameras, W, H, depth == 0 ? LastQuads : null);
                        break;
                    }
                }
            }
        }
        finally
        {
            if (depth != 0) ctx.DisposeTemporaries();
        }
    }

    private static void DrawSprite(SKCanvas canvas, Sprite sprite, TimelineObject obj, int[] bounds, int frame,
        List<GroupState> groups, List<CameraState> cameras, int W, int H, List<(string, SKPoint[])>? quads)
    {
        var draw = obj.Draw;
        float x = 0, y = 0, z = 0, scale = 1, rot = 0, rx = 0, ry = 0, alpha = 1;
        int blend = 0;
        if (draw != null)
        {
            float E(string k) => (float)draw.Eval(k, bounds, frame);
            x = E("x"); y = E("y"); z = E("z");
            scale = E("scale") / 100f;
            rot = E("rotation"); rx = E("rx"); ry = E("ry");
            alpha = 1 - E("alpha") / 100f;
            blend = draw.GetInt("blend");
        }
        const float Rad = MathF.PI / 180f;
        // オブジェクト自身の変形 (拡大 → 回転 → 移動)。そのあとにグループ制御を内側から順にかける
        var model = Matrix4x4.CreateScale(scale, scale, 1) * Matrix4x4.CreateRotationZ(rot * Rad);
        if (rx != 0) model *= Matrix4x4.CreateRotationX(rx * Rad);
        if (ry != 0) model *= Matrix4x4.CreateRotationY(ry * Rad);
        model *= Matrix4x4.CreateTranslation(x, y, z);
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            var g = groups[i];
            if (obj.Layer < g.FromLayer || obj.Layer > g.ToLayer) continue;
            model *= Matrix4x4.CreateScale(g.Scale, g.Scale, 1) * Matrix4x4.CreateRotationZ(g.Rotation * Rad) * Matrix4x4.CreateTranslation(g.X, g.Y, 0);
            alpha *= g.Alpha;
        }
        // 対象レイヤーに入っている、いちばん下 (最後) のカメラ制御を使う
        var view = Matrix4x4.CreateTranslation(0, 0, DefaultCameraDistance);
        float focal = DefaultCameraDistance;
        foreach (var c in cameras)
            if (obj.Layer >= c.FromLayer && obj.Layer <= c.ToLayer) { view = c.View; focal = c.Focal; }
        var m = model * view;

        // オブジェクトは平面なので、(lx, ly, 1) → 画面座標の射影変換 (3x3) にまとめられる
        float cx = W / 2f, cy = H / 2f;
        var h = new SKMatrix
        {
            ScaleX = focal * m.M11 + cx * m.M13, SkewX = focal * m.M21 + cx * m.M23, TransX = focal * m.M41 + cx * m.M43,
            SkewY = focal * m.M12 + cy * m.M13, ScaleY = focal * m.M22 + cy * m.M23, TransY = focal * m.M42 + cy * m.M43,
            Persp0 = m.M13, Persp1 = m.M23, Persp2 = m.M43,
        };
        // 中心がカメラより後ろにあるものは描かない
        if (m.M43 <= 1f) return;

        canvas.Save();
        canvas.Concat(ref h);
        alpha = Math.Clamp(alpha, 0, 1);
        if (quads != null)
        {
            // デバイス座標 → プロジェクト座標 (プレビューの縮小率を打ち消す)
            float k = W / (float)Math.Max(1, canvas.DeviceClipBounds.Width);
            var total = canvas.TotalMatrix;
            var rect = SKRect.Create(-sprite.Cx, -sprite.Cy, sprite.Width, sprite.Height);
            var pts = new[] { new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top), new SKPoint(rect.Right, rect.Bottom), new SKPoint(rect.Left, rect.Bottom) };
            for (int i = 0; i < 4; i++) { var q = total.MapPoint(pts[i]); pts[i] = new SKPoint(q.X * k, q.Y * k); }
            quads.Add((obj.Id, pts));
        }
        if (alpha > 0 && scale != 0)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)MathF.Round(alpha * 255)),
                FilterQuality = SKFilterQuality.High,
                IsAntialias = true,
            };
            if (blend == 2)
            {
                // 減算: 背景を反転 → 加算 → もう一度反転
                using var invert = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Difference };
                canvas.Save(); canvas.ResetMatrix(); canvas.DrawPaint(invert); canvas.Restore();
                paint.BlendMode = SKBlendMode.Plus;
                canvas.DrawImage(sprite.Image, -sprite.Cx, -sprite.Cy, paint);
                canvas.ResetMatrix(); canvas.DrawPaint(invert);
            }
            else
            {
                paint.BlendMode = BlendModeOf(blend);
                canvas.DrawImage(sprite.Image, -sprite.Cx, -sprite.Cy, paint);
            }
        }
        canvas.Restore();
    }

    private static SKBlendMode BlendModeOf(int index) => index switch
    {
        1 => SKBlendMode.Plus,
        3 => SKBlendMode.Multiply,
        4 => SKBlendMode.Screen,
        5 => SKBlendMode.Overlay,
        6 => SKBlendMode.Lighten,
        7 => SKBlendMode.Darken,
        8 => SKBlendMode.Luminosity,
        9 => SKBlendMode.Color,
        10 => SKBlendMode.ColorBurn,
        11 => SKBlendMode.HardLight,
        12 => SKBlendMode.Difference,
        _ => SKBlendMode.SrcOver,
    };

    /// <summary>フィルタオブジェクト: それまでに描いた画面全体に効果をかける。</summary>
    private static void ApplyToFrameBuffer(RenderContext ctx, TimelineObject obj, int[] bounds, SKSurface surface)
    {
        var sprite = Sprite.Centered(ctx.Own(surface.Snapshot()));
        var original = sprite;
        foreach (var e in obj.Effects)
        {
            if (!e.Enabled || e.Definition.Category != EffectCategory.Filter) continue;
            var fx = Effects.Find(e.DefinitionId);
            if (fx != null)
                sprite = fx.Apply(sprite, new EffectArgs { Effect = e, Object = obj, Bounds = bounds, Context = ctx, IsFrameBuffer = true });
        }
        if (ReferenceEquals(sprite.Image, original.Image)) return;
        var canvas = surface.Canvas;
        canvas.Save();
        canvas.ResetMatrix();
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(sprite.Image, original.Cx - sprite.Cx, original.Cy - sprite.Cy, paint);
        canvas.Restore();
    }

    private SKImage? GetSource(RenderContext ctx, TimelineObject obj, int[] bounds, int frame, int depth)
    {
        var p = ctx.Project;
        var main = obj.Main;
        switch (main.DefinitionId)
        {
            case EffectRegistry.Video:
            {
                double t = SourceTime(p, obj, bounds, frame);
                return Media.GetVideoFrame(obj.Id, main.GetString("file"), t, main.GetBool("loop"));
            }
            case EffectRegistry.Image:
                return Media.GetImage(main.GetString("file"));
            case EffectRegistry.Text:
            {
                float size = (float)main.Eval("size", bounds, frame), sp = (float)main.Eval("spacing", bounds, frame), ld = (float)main.Eval("leading", bounds, frame);
                string text = main.GetString("text"), font = main.GetString("font"), color = main.GetString("color");
                bool bold = main.GetBool("bold"), italic = main.GetBool("italic");
                int align = main.GetInt("align");
                string key = $"T|{text}|{font}|{Generators.F(size)}|{color}|{bold}|{italic}|{align}|{Generators.F(sp)}|{Generators.F(ld)}";
                return Cached(key, () => Generators.RenderText(text, font, size, ParseColor(color), bold, italic, align, sp, ld));
            }
            case EffectRegistry.Shape:
            {
                int type = main.GetInt("type");
                float size = (float)main.Eval("size", bounds, frame), aspect = (float)main.Eval("aspect", bounds, frame), line = (float)main.Eval("line", bounds, frame);
                string color = main.GetString("color");
                string key = $"S|{type}|{Generators.F(size)}|{Generators.F(aspect)}|{Generators.F(line)}|{color}|{p.Settings.Width}x{p.Settings.Height}";
                return Cached(key, () => Generators.RenderShape(type, size, aspect, line, ParseColor(color), p.Settings.Width, p.Settings.Height));
            }
            case EffectRegistry.SceneRef:
            {
                if (depth >= MaxSceneDepth) return null;
                var scene = p.FindScene(main.GetString("scene"));
                if (scene == null) return null;
                int inner = frame - obj.Start + obj.SourceOffsetFrames + p.SecondsToFrames(main.Eval("position", bounds, frame));
                using var surface = ctx.CreateSurface(p.Settings.Width, p.Settings.Height);
                if (surface == null) return null;
                surface.Canvas.Clear(SKColors.Transparent);
                RenderScene(ctx, scene, inner, surface, depth + 1);
                return ctx.Own(surface.Snapshot());
            }
        }
        return null;
    }

    /// <summary>オブジェクト内のフレーム位置を、素材の再生位置 (秒) に直す。</summary>
    public static double SourceTime(Project p, TimelineObject obj, int[] bounds, int frame)
    {
        var main = obj.Main;
        double speed = main.Eval("speed", bounds, frame) / 100.0;
        double local = p.FrameToSeconds(frame - obj.Start + obj.SourceOffsetFrames);
        return Math.Max(0, main.Eval("position", bounds, obj.Start) + local * speed);
    }

    private static SKColor ParseColor(string s) => SKColor.TryParse(s, out var c) ? c : SKColors.White;

    private SKImage? Cached(string key, Func<SKImage?> create)
    {
        _tick++;
        if (_generated.TryGetValue(key, out var img)) { _generatedUse[key] = _tick; return img; }
        if (_generated.Count >= 96)
        {
            // 古い半分を捨てる。直近のフレームで使ったものは残る
            foreach (var k in _generatedUse.OrderBy(kv => kv.Value).Take(48).Select(kv => kv.Key).ToList())
            {
                _generated[k]?.Dispose();
                _generated.Remove(k); _generatedUse.Remove(k);
            }
        }
        img = create();
        _generated[key] = img; _generatedUse[key] = _tick;
        return img;
    }

    public void Dispose()
    {
        Gpu?.MakeCurrent();
        _gpuSurface?.Dispose();
        _gpuSurface = null;
        foreach (var img in _generated.Values) img?.Dispose();
        _generated.Clear();
        Media.Dispose();
    }
}
