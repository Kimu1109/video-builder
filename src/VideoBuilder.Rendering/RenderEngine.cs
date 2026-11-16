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

    // テキスト・図形は、パラメータが同じ間は作り直さない
    private readonly Dictionary<string, SKImage?> _generated = new();
    private readonly Dictionary<string, long> _generatedUse = new();
    private long _tick;

    private readonly record struct GroupState(int FromLayer, int ToLayer, float X, float Y, float Scale, float Rotation, float Alpha);

    /// <summary>surface いっぱいにフレームを描く。surface の大きさがプロジェクトより小さければ縮小して描く。</summary>
    public void Render(Project project, Scene scene, int frame, SKSurface surface)
    {
        var ctx = new RenderContext { Project = project, Frame = frame };
        try
        {
            var canvas = surface.Canvas;
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
        using var surface = SKSurface.Create(Effects.Info(project.Settings.Width, project.Settings.Height));
        Render(project, scene, frame, surface);
        return surface.Snapshot();
    }

    private void RenderScene(RenderContext root, Scene scene, int frame, SKSurface surface, int depth)
    {
        var project = root.Project;
        var ctx = depth == 0 ? root : new RenderContext { Project = project, Frame = frame };
        var canvas = surface.Canvas;
        int W = project.Settings.Width, H = project.Settings.Height;
        var groups = new List<GroupState>();

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
                        DrawSprite(canvas, sprite, obj, bounds, frame, groups, W, H);
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

    private static void DrawSprite(SKCanvas canvas, Sprite sprite, TimelineObject obj, int[] bounds, int frame, List<GroupState> groups, int W, int H)
    {
        var draw = obj.Draw;
        float x = 0, y = 0, scale = 1, rot = 0, alpha = 1;
        int blend = 0;
        if (draw != null)
        {
            x = (float)draw.Eval("x", bounds, frame); y = (float)draw.Eval("y", bounds, frame);
            scale = (float)draw.Eval("scale", bounds, frame) / 100f;
            rot = (float)draw.Eval("rotation", bounds, frame);
            alpha = 1 - (float)draw.Eval("alpha", bounds, frame) / 100f;
            blend = draw.GetInt("blend");
        }
        canvas.Save();
        canvas.Translate(W / 2f, H / 2f);
        foreach (var g in groups)
        {
            if (obj.Layer < g.FromLayer || obj.Layer > g.ToLayer) continue;
            canvas.Translate(g.X, g.Y);
            canvas.RotateDegrees(g.Rotation);
            canvas.Scale(g.Scale);
            alpha *= g.Alpha;
        }
        canvas.Translate(x, y);
        canvas.RotateDegrees(rot);
        canvas.Scale(scale);
        alpha = Math.Clamp(alpha, 0, 1);
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
                using var surface = SKSurface.Create(Effects.Info(p.Settings.Width, p.Settings.Height));
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
        foreach (var img in _generated.Values) img?.Dispose();
        _generated.Clear();
        Media.Dispose();
    }
}
