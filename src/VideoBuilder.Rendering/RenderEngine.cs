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

    public MediaCache Media { get; }
    /// <summary>GPU 描画に使うコンテキスト。null なら CPU 描画。</summary>
    public GpuContext? Gpu { get; }
    private SKSurface? _gpuSurface;

    public RenderEngine(GpuContext? gpu = null)
    {
        Gpu = gpu;
        // CPU 描画では動画フレームを複製せずに使う。GPU 描画は描画が後回しになることがあるので複製する
        Media = new MediaCache(zeroCopy: gpu == null, gpu: gpu?.Context);
    }

    /// <summary>
    /// プレビュー用の軽い描画にする (拡大縮小の補間を簡略化し、動画はプロキシがあればプロキシを使う)。
    /// 出力では false のままにする。
    /// </summary>
    public bool Draft
    {
        get => _draft;
        set { _draft = value; Media.UseProxies = value; }
    }
    private bool _draft;

    /// <summary>
    /// 連続したフレームを順に描く (再生中・書き出し)。true のときは、動画の次のフレームを裏でデコードしておき、
    /// 合成している間にデコードと変換を進める。シーク (飛び飛びの位置を描く) のときは false にする。
    /// </summary>
    public bool Lookahead { get; set; }

    /// <summary>今の描画の、プロジェクトの 1 ピクセルが描画先の何ピクセルか (プレビューで縮めて描くとき 1 より小さい)。</summary>
    private float _renderScale = 1;

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

    /// <summary>
    /// カメラ: ワールド座標 → カメラ座標 (X 右, Y 下, Z 奥) の変換と、焦点距離。
    /// ZSort: 対象のオブジェクトを奥から順に描く。Dof: 深度ぼけの強さ (ピクセル)、FocusDistance: ピントの合う距離。
    /// </summary>
    private readonly record struct CameraState(int FromLayer, int ToLayer, Matrix4x4 View, float Focal, bool ZSort = false, float Dof = 0, float FocusDistance = 0);

    /// <summary>オブジェクトを画面のどこにどう描くか (Place で求め、DrawPlaced で描く)。</summary>
    private readonly record struct Placement(SKMatrix Matrix, float Depth, float Alpha, float Scale, int Blend, int Camera);

    /// <summary>奥行きで並べ替えるために、描くのを待っているオブジェクト。</summary>
    private readonly record struct DeferredDraw(Sprite Sprite, TimelineObject Obj, Placement Place, int Order, bool ChangesEveryFrame);

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
        bool onGpu = (_gpuSurface != null && ReferenceEquals(surface, _gpuSurface)) || (_externalGpuSurface != null && ReferenceEquals(surface, _externalGpuSurface));
        var ctx = new RenderContext { Project = project, Frame = frame, Draft = Draft, Gpu = onGpu ? Gpu?.Context : null };
        try
        {
            var canvas = surface.Canvas;
            Media.BeginFrame();
            Media.DrawingOnGpu = ctx.Gpu != null;
            _frameStartTick = _tick + 1;
            LastQuads.Clear();
            canvas.ResetMatrix();
            canvas.Clear(SKColors.Black);
            float scale = surface.Canvas.DeviceClipBounds.Width / (float)project.Settings.Width;
            _renderScale = Math.Clamp(scale, 1f / 64, 1);
            canvas.Scale(scale);
            RenderScene(ctx, scene, frame, surface, 0);
            canvas.Flush();
        }
        finally { ctx.DisposeTemporaries(); }
    }

    /// <summary>
    /// GPU の描画先 (GPU コンテキストで作った SKSurface。画面と共有するテクスチャなど) に描く。読み戻さない。
    /// GPU 描画のエンジン (Gpu が null でない) でだけ使う。
    /// </summary>
    public void RenderToGpuSurface(Project project, Scene scene, int frame, SKSurface surface)
    {
        if (Gpu == null) throw new InvalidOperationException("GPU 描画のエンジンではありません");
        Gpu.MakeCurrent();
        _externalGpuSurface = surface;
        try { Render(project, scene, frame, surface); }
        finally { _externalGpuSurface = null; }
    }

    private SKSurface? _externalGpuSurface;

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
        var ctx = depth == 0 ? root : new RenderContext { Project = project, Frame = frame, Draft = root.Draft, Gpu = root.Gpu };
        var canvas = surface.Canvas;
        int W = project.Settings.Width, H = project.Settings.Height;
        var groups = new List<GroupState>();
        var cameras = new List<CameraState>();
        var deferred = new List<DeferredDraw>();
        var active = scene.ObjectsAt(frame).ToList();
        PrefetchVideos(ctx, scene, active, frame, depth);
        // CPU 描画のときは描画先のピクセルを直接つかんでおく (大きな絵を帯に分けて並列に描くため)
        using var target = ctx.Gpu == null ? surface.PeekPixels() : null;

        try
        {
            foreach (var obj in active)
            {
                if (!obj.Enabled || obj.Effects.Count == 0 || !scene.GetLayer(obj.Layer).Visible) continue;
                var bounds = obj.Bounds;
                var main = obj.Main;
                var def = main.Definition;

                switch (def.Category)
                {
                    case EffectCategory.Control when def.Id == EffectRegistry.GroupControl:
                        groups.Add(GroupOf(obj, bounds, frame));
                        break;
                    case EffectCategory.Control when def.Id == EffectRegistry.CameraControl:
                    {
                        float E(string k) => (float)main.Eval(k, bounds, frame);
                        int n = (int)E("layers");
                        float fov = E("fov");
                        float focal = fov > 0.5f ? H / 2f / MathF.Tan(fov * MathF.PI / 360f) : DefaultCameraDistance;
                        var eye = new Vector3(E("x"), E("y"), E("z"));
                        var aim = new Vector3(E("tx"), E("ty"), E("tz"));
                        float focus = E("focus");
                        if (focus <= 0) focus = Math.Max(1, Vector3.Distance(eye, aim));   // 0 = 目標の位置にピントを合わせる
                        // 新しいカメラより前に待たせていたものは、前のカメラの並びで描き終えておく
                        FlushDeferred(canvas, deferred, cameras, W, H, depth == 0 ? LastQuads : null, target, ctx, depth);
                        cameras.Add(new CameraState(obj.Layer + 1, n <= 0 ? int.MaxValue : obj.Layer + n,
                            LookAt(eye, aim, E("roll")), focal, main.GetBool("zsort"), Math.Max(0, E("dof")), focus));
                        break;
                    }
                    case EffectCategory.Filter:
                        // 画面全体にかける効果は、それまでに描くはずだったものを描き終えてからかける
                        FlushDeferred(canvas, deferred, cameras, W, H, depth == 0 ? LastQuads : null, target, ctx, depth);
                        ApplyToFrameBuffer(ctx, obj, bounds, surface);
                        break;
                    case EffectCategory.VideoSource:
                    {
                        _sourceScale = 1;
                        _sourceAnimated = false;
                        _sourceStatic = false;
                        _detail = Detail(ctx, obj, bounds, frame, groups, cameras.Count > 0);
                        var src = GetSource(ctx, obj, bounds, frame, depth);
                        if (src == null) break;
                        var sprite = Sprite.Centered(src);
                        if (_sourceScale != 1)
                        {
                            // 小さく作った絵 (プロキシ・縮めてデコードした動画・縮めて描いたシーン)。フィルタはピクセル単位の大きさ
                            // (ぼかしの範囲など) で動くので、そういうフィルタがあるときだけ元の大きさに戻してからかける。
                            // なければ縮んだまま描いて、描くときに拡大する
                            sprite = NeedsFullResolution(obj) ? Upscale(ctx, sprite, _sourceScale) : sprite with { Scale = _sourceScale };
                        }
                        ctx.Modifiers = DrawModifiers.None;
                        int next = ApplyCachedFilters(ctx, obj, bounds, frame, depth, ref sprite);
                        sprite = Effects.ApplyRange(sprite, obj, bounds, ctx, next, obj.Effects.Count);
                        bool changes = def.Id == EffectRegistry.Video || (def.Id == EffectRegistry.SceneRef && !_sourceStatic) || (def.Id == EffectRegistry.Vector && _sourceAnimated);
                        if (Place(sprite, obj, bounds, frame, groups, cameras, W, H, ctx.Modifiers) is not { } place) break;
                        if (place.Camera >= 0 && cameras[place.Camera].ZSort)
                        {
                            // 奥行きで並べ替える: 同じカメラのものが続く間は描かずに待たせる
                            if (deferred.Count > 0 && deferred[0].Place.Camera != place.Camera)
                                FlushDeferred(canvas, deferred, cameras, W, H, depth == 0 ? LastQuads : null, target, ctx, depth);
                            deferred.Add(new DeferredDraw(sprite, obj, place, deferred.Count, changes));
                            break;
                        }
                        FlushDeferred(canvas, deferred, cameras, W, H, depth == 0 ? LastQuads : null, target, ctx, depth);
                        DrawPlaced(canvas, ApplyDepthOfField(ctx, sprite, obj, place, cameras, depth), sprite, obj, place, W, depth == 0 ? LastQuads : null, Draft, target, changes);
                        break;
                    }
                }
            }
            FlushDeferred(canvas, deferred, cameras, W, H, depth == 0 ? LastQuads : null, target, ctx, depth);
        }
        finally
        {
            if (depth != 0) ctx.DisposeTemporaries();
        }
    }

    /// <summary>待たせていたものを、奥 (カメラから遠い) から順に描く。同じ距離ならレイヤー順。</summary>
    private void FlushDeferred(SKCanvas canvas, List<DeferredDraw> deferred, List<CameraState> cameras, int W, int H,
        List<(string, SKPoint[])>? quads, SKPixmap? target, RenderContext ctx, int depth)
    {
        if (deferred.Count == 0) return;
        deferred.Sort((a, b) =>
        {
            int c = b.Place.Depth.CompareTo(a.Place.Depth);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });
        foreach (var d in deferred)
            DrawPlaced(canvas, ApplyDepthOfField(ctx, d.Sprite, d.Obj, d.Place, cameras, depth), d.Sprite, d.Obj, d.Place, W, quads, Draft, target, d.ChangesEveryFrame);
        deferred.Clear();
    }

    /// <summary>深度ぼけ: ピントの位置から離れているほど、絵をぼかす。</summary>
    private sealed class DofEntry
    {
        public uint SourceId;
        public float Radius;
        public Sprite Sprite;
        public long LastUsed;
    }
    // 深度ぼけの結果。元の絵とぼけの半径が前のフレームと同じなら使い回す (止まっている背景を毎フレームぼかさない)
    private readonly Dictionary<string, DofEntry> _dofCache = new();

    private Sprite ApplyDepthOfField(RenderContext ctx, Sprite sprite, TimelineObject obj, Placement place, List<CameraState> cameras, int depth)
    {
        if (place.Camera < 0) return sprite;
        var cam = cameras[place.Camera];
        if (cam.Dof <= 0 || place.Alpha <= 0) return sprite;
        // 画面上でのぼけの半径 (プロジェクトのピクセル)。ピントの位置で 0、遠くへ行くほど Dof に近づき、手前ではさらに大きくなる
        float screenRadius = cam.Dof * MathF.Abs(place.Depth - cam.FocusDistance) / place.Depth;
        // 絵の 1 ピクセルが画面で何ピクセルになるか (オブジェクトの中心のあたりで測る)
        var m = place.Matrix;
        var o = m.MapPoint(0, 0);
        var ux = m.MapPoint(1, 0);
        var uy = m.MapPoint(0, 1);
        float pixel = MathF.Sqrt(Math.Max(Dist2(ux, o), Dist2(uy, o))) * sprite.Scale;
        if (pixel <= 1e-4f) return sprite;
        float radius = Math.Min(screenRadius / pixel, 500);
        if (radius < 0.5f) return sprite;
        radius = MathF.Round(radius * 4) / 4;   // 細かな揺れで作り直さない
        string key = depth == 0 ? obj.Id : obj.Id + "@" + depth;
        _tick++;
        if (_dofCache.TryGetValue(key, out var hit) && hit.SourceId == sprite.Image.UniqueId && hit.Radius == radius)
        {
            hit.LastUsed = _tick;
            return hit.Sprite;
        }
        // ぼかした絵は元と同じ縮尺 (プロキシなら小さいまま) で描く
        var blurred = Effects.GaussianBlur(sprite, ctx, radius) with { Scale = sprite.Scale };
        if (ReferenceEquals(blurred.Image, sprite.Image) || !ctx.Temporaries.Remove(blurred.Image)) return blurred;
        if (hit != null) hit.Sprite.Image.Dispose();
        else
        {
            if (_dofCache.Count >= 64)
                foreach (var k in _dofCache.Where(kv => kv.Value.LastUsed < _frameStartTick).Select(kv => kv.Key).ToList())
                {
                    _dofCache[k].Sprite.Image.Dispose();
                    _dofCache.Remove(k);
                }
            _dofCache[key] = hit = new DofEntry();
        }
        hit.SourceId = sprite.Image.UniqueId; hit.Radius = radius; hit.Sprite = blurred; hit.LastUsed = _tick;
        return blurred;

        static float Dist2(SKPoint a, SKPoint b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
    }

    /// <summary>オブジェクトの変形・グループ制御・カメラから、画面への変換を求める。描かないもの (カメラの後ろ) は null。</summary>
    private static Placement? Place(Sprite sprite, TimelineObject obj, int[] bounds, int frame,
        List<GroupState> groups, List<CameraState> cameras, int W, int H, DrawModifiers mod)
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
        x += mod.Ox; y += mod.Oy; z += mod.Oz;
        rot += mod.Rz; rx += mod.Rx; ry += mod.Ry;
        scale *= mod.Zoom; alpha *= mod.Alpha;
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
        int camera = -1;
        for (int ci = 0; ci < cameras.Count; ci++)
        {
            var c = cameras[ci];
            if (obj.Layer >= c.FromLayer && obj.Layer <= c.ToLayer) { view = c.View; focal = c.Focal; camera = ci; }
        }
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
        if (m.M43 <= 1f) return null;
        // 全体を同じ数で割っても同じ変換を表す。右下を 1 にそろえておくと、遠近のない場合に
        // Skia が「平行移動・拡大・回転だけ」と判断できて、速い描画経路を使える
        float inv = 1f / m.M43;
        h.ScaleX *= inv; h.SkewX *= inv; h.TransX *= inv;
        h.SkewY *= inv; h.ScaleY *= inv; h.TransY *= inv;
        h.Persp0 *= inv; h.Persp1 *= inv; h.Persp2 = 1;
        return new Placement(h, m.M43, Math.Clamp(alpha, 0, 1), scale, blend, camera);
    }

    /// <summary>Place で求めた位置に描く。quadSprite は選択枠に使う絵 (ぼかして広がる前の大きさ)。</summary>
    private static void DrawPlaced(SKCanvas canvas, Sprite sprite, Sprite quadSprite, TimelineObject obj, Placement place, int W,
        List<(string, SKPoint[])>? quads, bool draft, SKPixmap? target, bool changesEveryFrame)
    {
        var h = place.Matrix;
        float alpha = place.Alpha, scale = place.Scale;
        int blend = place.Blend;
        canvas.Save();
        canvas.Concat(ref h);
        if (quads != null)
        {
            // デバイス座標 → プロジェクト座標 (プレビューの縮小率を打ち消す)
            float k = W / (float)Math.Max(1, canvas.DeviceClipBounds.Width);
            var total = canvas.TotalMatrix;
            if (quadSprite.Scale != 1) total = total.PreConcat(SKMatrix.CreateScale(quadSprite.Scale, quadSprite.Scale));
            var rect = SKRect.Create(-quadSprite.Cx, -quadSprite.Cy, quadSprite.Width, quadSprite.Height);
            var pts = new[] { new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top), new SKPoint(rect.Right, rect.Bottom), new SKPoint(rect.Left, rect.Bottom) };
            for (int i = 0; i < 4; i++) { var q = total.MapPoint(pts[i]); pts[i] = new SKPoint(q.X * k, q.Y * k); }
            quads.Add((obj.Id, pts));
        }
        if (sprite.Scale != 1) canvas.Scale(sprite.Scale);   // 小さく作った絵 (プロキシ) を元の大きさで描く
        // 毎フレーム変わる絵 (動画など) を半分より小さくするときは、先に半分ずつ縮めておく。
        // そのまま描くと、Skia が縮小用の中間画像を毎フレーム 1 スレッドで作り直してしまう
        SKImage? prescaled = null;
        if (changesEveryFrame && target != null && alpha > 0 && (long)sprite.Width * sprite.Height >= 200_000)
        {
            var tm = canvas.TotalMatrix;
            if (tm.Persp0 == 0 && tm.Persp1 == 0)
            {
                float sx = MathF.Sqrt(tm.ScaleX * tm.ScaleX + tm.SkewY * tm.SkewY), sy = MathF.Sqrt(tm.ScaleY * tm.ScaleY + tm.SkewX * tm.SkewX);
                float min = Math.Min(sx, sy);
                int factor = 1;
                var current = sprite.Image;
                while (min * factor < 0.5f && factor < 16 && current.Width > 8 && current.Height > 8)
                {
                    var next = PixelOps.Halve(current, current.Width, current.Height);
                    prescaled?.Dispose();
                    prescaled = current = next;
                    factor *= 2;
                }
                if (prescaled != null)
                {
                    // 縮めたぶんだけ拡大して描けば、位置と大きさは元と同じになる
                    canvas.Scale(sprite.Width / (float)prescaled.Width, sprite.Height / (float)prescaled.Height);
                    float kx = prescaled.Width / (float)sprite.Width, ky = prescaled.Height / (float)sprite.Height;
                    sprite = new Sprite(prescaled, sprite.Cx * kx, sprite.Cy * ky);
                }
            }
        }
        using var prescaledOwner = prescaled;
        if (alpha > 0 && scale != 0)
        {
            SnapHalfPixel(canvas, sprite);
            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)MathF.Round(alpha * 255)),
                FilterQuality = QualityFor(canvas.TotalMatrix, draft, new SKPoint(-sprite.Cx, -sprite.Cy)),
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
                // 出力 (CPU) の高品質な拡大は、Skia ではなく自前の並列のバイキュービックで行う (Skia は 4K で 90 ms ほどかかる)
                bool enlarged = target != null && paint.FilterQuality == SKFilterQuality.High
                    && PixelResample.DrawEnlarged(canvas, target, sprite.Image, -sprite.Cx, -sprite.Cy, paint);
                if (!enlarged && !DrawSpriteParallel(canvas, target, sprite, paint))
                    canvas.DrawImage(sprite.Image, -sprite.Cx, -sprite.Cy, paint);
            }
        }
        canvas.Restore();
    }

    /// <summary>
    /// 等倍で描く画像の左上が、ちょうど画素の半分 (x.5) にあるなら、整数の位置に寄せる。
    /// <para>
    /// 幅や高さが奇数の画像 (文字など) は、中心で置くと左上が x.5 になる。そこに補間なし (最近傍) で描くと、
    /// どちらの画素を拾うかが CPU と GPU (Direct3D など) で逆になり、絵が 1 画素ずれる。寄せても動くのは 0.5 画素だけで、絵はくっきりしたまま。
    /// 少しずつ動いている (x.3 など) ときは寄せない (補間して、なめらかに動かす)。
    /// </para>
    /// </summary>
    internal static void SnapHalfPixel(SKCanvas canvas, Sprite sprite)
    {
        var m = canvas.TotalMatrix;
        if (!IsUnitScale(m)) return;
        var o = m.MapPoint(-sprite.Cx, -sprite.Cy);
        static float Nudge(float v) => MathF.Abs(v - MathF.Floor(v) - 0.5f) < 1e-3f ? MathF.Floor(v + 0.5f) - v : 0;
        float dx = Nudge(o.X), dy = Nudge(o.Y);
        if (dx == 0 && dy == 0) return;
        canvas.SetMatrix(SKMatrix.Concat(SKMatrix.CreateTranslation(dx, dy), m));
    }

    private static bool IsUnitScale(SKMatrix m) =>
        m.Persp0 == 0 && m.Persp1 == 0 && m.SkewX == 0 && m.SkewY == 0 && Math.Abs(m.ScaleX - 1) < 1e-4f && Math.Abs(m.ScaleY - 1) < 1e-4f;

    /// <summary>
    /// 拡大縮小の補間方法を選ぶ。等倍で動かすだけなら補間は不要。プレビューでは軽い方法を使い、
    /// 出力では常に高品質にする。origin は画像の左上 (描く座標系で)。等倍のとき、これが画素にそろっているかで決める。
    /// </summary>
    private static SKFilterQuality QualityFor(SKMatrix m, bool draft, SKPoint origin = default)
    {
        bool flat = m.Persp0 == 0 && m.Persp1 == 0 && m.SkewX == 0 && m.SkewY == 0;
        if (IsUnitScale(m))
        {
            var o = m.MapPoint(origin);
            bool aligned = Math.Abs(o.X - MathF.Round(o.X)) < 1e-3f && Math.Abs(o.Y - MathF.Round(o.Y)) < 1e-3f;
            return aligned ? SKFilterQuality.None : SKFilterQuality.Low;
        }
        if (!flat) return draft ? SKFilterQuality.Low : SKFilterQuality.High;
        float sx = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY), sy = MathF.Sqrt(m.ScaleY * m.ScaleY + m.SkewX * m.SkewX);
        float min = Math.Min(sx, sy);
        // 半分より小さくするときだけ、縮小用の中間画像 (ミップマップ) を使う。
        // 半分〜等倍の縮小は、隣り合う 4 点の補間で十分きれいに縮まる (中間画像を毎フレーム作らずに済む)
        if (min < 0.5f) return SKFilterQuality.Medium;
        if (min <= 1f) return SKFilterQuality.Low;
        // 拡大: 出力ではなめらかな補間 (バイキュービック)、プレビューでは軽い補間
        return draft ? SKFilterQuality.Low : SKFilterQuality.High;
    }

    /// <summary>これより広い範囲に描くときは、帯に分けて複数スレッドで描く。</summary>
    private const int ParallelDrawPixels = 300_000;

    /// <summary>
    /// 大きな絵を、描画先を横帯に分けて複数スレッドで描く (CPU 描画のみ)。
    /// 帯ごとに同じ変換・同じ絵で描くので、結果は 1 スレッドで描いたときと変わらない。
    /// 条件に合わず描かなかったときは false を返す。
    /// </summary>
    private static bool DrawSpriteParallel(SKCanvas canvas, SKPixmap? target, Sprite sprite, SKPaint paint)
    {
        int cores = Environment.ProcessorCount;
        if (target == null || cores < 2 || target.ColorType != SKColorType.Bgra8888) return false;
        // 等倍でそのまま置くだけの描画はメモリの読み書きが大半なので、分けても速くならない
        if (paint.FilterQuality == SKFilterQuality.None) return false;
        var total = canvas.TotalMatrix;
        // 画面上で絵が占める範囲を求める
        var rect = SKRect.Create(-sprite.Cx, -sprite.Cy, sprite.Width, sprite.Height);
        float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
        foreach (var corner in new[] { new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top), new SKPoint(rect.Right, rect.Bottom), new SKPoint(rect.Left, rect.Bottom) })
        {
            // 遠近のある変換でカメラの後ろに回る角があると範囲を求められないので、通常の描画に任せる
            float w = total.Persp0 * corner.X + total.Persp1 * corner.Y + total.Persp2;
            if (w <= 1e-3f) return false;
            var q = total.MapPoint(corner);
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }
        int W = target.Width, H = target.Height;
        int y0 = Math.Clamp((int)MathF.Floor(minY) - 1, 0, H), y1 = Math.Clamp((int)MathF.Ceiling(maxY) + 1, 0, H);
        float visibleWidth = Math.Min(maxX, W) - Math.Max(minX, 0);
        if (y1 - y0 < 64 || visibleWidth <= 0 || (long)(y1 - y0) * (long)visibleWidth < ParallelDrawPixels) return false;

        int bands = Math.Min(cores, (y1 - y0) / 32);
        IntPtr pixels = target.GetPixels();
        int rowBytes = target.RowBytes;
        var image = sprite.Image;
        float left = -sprite.Cx, top = -sprite.Cy;
        Parallel.For(0, bands, b =>
        {
            int ya = y0 + (int)((long)(y1 - y0) * b / bands), yb = y0 + (int)((long)(y1 - y0) * (b + 1) / bands);
            if (yb <= ya) return;
            using var band = SKSurface.Create(new SKImageInfo(W, yb - ya, SKColorType.Bgra8888, SKAlphaType.Premul), pixels + ya * rowBytes, rowBytes);
            var c = band.Canvas;
            c.Translate(0, -ya);
            c.Concat(ref total);
            c.DrawImage(image, left, top, paint);
            c.Flush();
        });
        return true;
    }

    /// <summary>
    /// 同じフレームに動画が複数あるときは、先にまとめて (並列に) デコードしておく。
    /// 先読み (Lookahead) のときは 1 本でも先に取り出し、続けて次のフレームのデコードを裏で始める。
    /// </summary>
    private void PrefetchVideos(RenderContext ctx, Scene scene, List<TimelineObject> active, int frame, int depth)
    {
        var project = ctx.Project;
        List<(string, string, double, bool, float)>? requests = null;
        List<(string, string, double, bool)>? next = null;
        // 細かさ (Detail) を合成のときと同じに求めるため、グループ制御・カメラ制御を同じ順にたどる
        var groups = new List<GroupState>();
        bool camera = false;
        foreach (var o in active)
        {
            if (!o.Enabled || o.Effects.Count == 0 || !scene.GetLayer(o.Layer).Visible) continue;
            var id = o.Main.DefinitionId;
            if (id == EffectRegistry.GroupControl) { groups.Add(GroupOf(o, o.Bounds, frame)); continue; }
            if (id == EffectRegistry.CameraControl) { camera = true; continue; }
            if (id != EffectRegistry.Video) continue;
            var bounds = o.Bounds;
            string file = o.Main.GetString("file");
            bool loop = o.Main.GetBool("loop");
            (requests ??= new()).Add((o.Id, file, SourceTime(project, o, bounds, frame), loop, Detail(ctx, o, bounds, frame, groups, camera)));
            // 入れ子のシーンの中は、次に描くフレームが 1 つ先とは限らないので先読みしない
            if (Lookahead && depth == 0 && o.Contains(frame + 1))
                (next ??= new()).Add((o.Id, file, SourceTime(project, o, bounds, frame + 1), loop));
        }
        if (Lookahead && depth == 0) PreloadUpcoming(project, scene, frame);
        if (requests == null) return;
        if (requests.Count >= 2 || next != null) Media.Prefetch(requests);
        if (next != null) Media.Lookahead(next);
    }

    /// <summary>1 秒以内に始まる動画のクリップを、裏で開いておく (再生・出力のとき)。</summary>
    private void PreloadUpcoming(Project project, Scene scene, int frame)
    {
        int horizon = Math.Max(1, (int)Math.Ceiling(project.Settings.Fps.Value));
        List<(int Start, string Key, string Path, double Seconds, bool Loop)>? upcoming = null;
        foreach (var o in scene.Objects)
        {
            if (o.Start <= frame || o.Start > frame + horizon || !o.Enabled || o.Effects.Count == 0) continue;
            if (o.Main.DefinitionId != EffectRegistry.Video || !scene.GetLayer(o.Layer).Visible) continue;
            (upcoming ??= new()).Add((o.Start, o.Id, o.Main.GetString("file"), SourceTime(project, o, o.Bounds, o.Start), o.Main.GetBool("loop")));
        }
        if (upcoming == null) return;
        Media.Preload(upcoming.OrderBy(u => u.Start).Select(u => (u.Key, u.Path, u.Seconds, u.Loop)).ToList());
    }

    private static GroupState GroupOf(TimelineObject obj, int[] bounds, int frame)
    {
        var main = obj.Main;
        int n = (int)main.Eval("layers", bounds, frame);
        return new GroupState(obj.Layer + 1, n <= 0 ? int.MaxValue : obj.Layer + n,
            (float)main.Eval("x", bounds, frame), (float)main.Eval("y", bounds, frame),
            (float)main.Eval("scale", bounds, frame) / 100f, (float)main.Eval("rotation", bounds, frame),
            1 - (float)main.Eval("alpha", bounds, frame) / 100f);
    }

    /// <summary>
    /// プレビューで、オブジェクトの絵にどれだけの細かさが要るか (1 = 元の大きさ)。
    /// 画面に出る大きさ (プレビューの縮小率 × 拡大率 × グループの拡大率 × 手前に出した分) より細かく作っても見えないので、
    /// 動画のデコードやシーンの描画をその大きさで済ませる。
    /// カメラ制御があるとき (どれだけ大きく映るか前もって分からない) と、ピクセル単位で動くフィルタがあるときは 1。
    /// </summary>
    private float Detail(RenderContext ctx, TimelineObject obj, int[] bounds, int frame, List<GroupState> groups, bool camera)
    {
        if (!ctx.Draft || _renderScale >= 1 || camera || NeedsFullResolution(obj)) return 1;
        float k = _renderScale;
        if (obj.Draw is { } draw)
        {
            k *= MathF.Abs((float)draw.Eval("scale", bounds, frame)) / 100f;
            float z = (float)draw.Eval("z", bounds, frame);
            if (z < 0) k *= DefaultCameraDistance / Math.Max(1, DefaultCameraDistance + z);   // 手前に出すと大きく映る
        }
        foreach (var g in groups)
            if (obj.Layer >= g.FromLayer && obj.Layer <= g.ToLayer) k *= MathF.Abs(g.Scale);
        if (float.IsNaN(k) || k <= 0) return 1;
        return Math.Clamp(k, 1f / 64, 1);
    }

    /// <summary>
    /// 小さく作った絵のままではかけられないフィルタ (ぼかしの範囲など、ピクセル単位の大きさで動くもの) があるか。
    /// 色だけを変えるもの・不透明度や位置だけを変えるものは、小さい絵にかけても結果は同じ。
    /// </summary>
    private static bool NeedsFullResolution(TimelineObject obj)
    {
        for (int i = 1; i < obj.Effects.Count; i++)
        {
            var e = obj.Effects[i];
            if (!e.Enabled || e.Definition.Category != EffectCategory.Filter) continue;
            bool independent = e.DefinitionId switch
            {
                "filter.color" or "filter.mono" or "filter.fade" => true,
                "filter.transition" => e.GetInt("type") is 0 or >= 6,   // フェード・スライド・ズーム (ワイプはぼかしの幅がピクセル単位)
                _ => false,
            };
            if (!independent) return true;
        }
        return false;
    }

    // ---------------- フィルタ結果のキャッシュ ----------------

    private sealed class FilterCacheEntry
    {
        public string Signature = "";
        public Sprite Sprite;
        public long LastUsed;
    }

    private readonly Dictionary<string, FilterCacheEntry> _filterCache = new();
    private readonly System.Text.StringBuilder _signature = new();
    // キャッシュの上限は「個数」ではなくメモリ量で決める (小さなテロップなら何百個でも持てるように)
    private const long FilterCacheBudgetBytes = 384L * 1024 * 1024;
    private long _filterCacheBytes;
    private long _frameStartTick;

    private static long BytesOf(Sprite s) => (long)s.Width * s.Height * 4;

    /// <summary>フィルタ結果のキャッシュを使った回数 / 作り直した回数 (計測用)。</summary>
    public (long Hits, long Misses) FilterCacheStats { get; private set; }

    /// <summary>
    /// オブジェクトの先頭から続く「結果が入力だけで決まるフィルタ」をまとめて処理する。
    /// 元の絵とパラメータが前のフレームと同じなら、前回の結果をそのまま使う (動かないテロップの縁取りなどを毎フレーム計算しない)。
    /// 戻り値は、まだ処理していない最初の効果の番号。
    /// </summary>
    private int ApplyCachedFilters(RenderContext ctx, TimelineObject obj, int[] bounds, int frame, int depth, ref Sprite sprite)
    {
        int end = 1;
        bool any = false;
        while (end < obj.Effects.Count)
        {
            var e = obj.Effects[end];
            if (e.Definition.Category != EffectCategory.Filter) break;
            if (e.Enabled)
            {
                if (!Effects.IsDeterministic(e.DefinitionId, Effects.Find(e.DefinitionId))) break;
                any = true;
            }
            end++;
        }
        if (!any) return 1;

        // 署名: 元の絵 + 各フィルタのパラメータの今の値
        var sb = _signature.Clear();
        sb.Append(sprite.Image.UniqueId).Append('|').Append(sprite.Width).Append('x').Append(sprite.Height);
        for (int i = 1; i < end; i++)
        {
            var e = obj.Effects[i];
            if (!e.Enabled) continue;
            sb.Append('|').Append(e.DefinitionId);
            foreach (var d in e.Definition.Params)
            {
                sb.Append(';');
                if (d.Kind == ParamKind.Track) sb.Append(e.Eval(d.Key, bounds, frame).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                else sb.Append(e.GetString(d.Key));
            }
        }
        string signature = sb.ToString();
        string key = depth == 0 ? obj.Id : obj.Id + "@" + depth;
        _tick++;
        if (_filterCache.TryGetValue(key, out var entry) && entry.Signature == signature)
        {
            entry.LastUsed = _tick;
            sprite = entry.Sprite;
            FilterCacheStats = (FilterCacheStats.Hits + 1, FilterCacheStats.Misses);
            return end;
        }

        var source = sprite;
        sprite = Effects.ApplyRange(sprite, obj, bounds, ctx, 1, end);
        FilterCacheStats = (FilterCacheStats.Hits, FilterCacheStats.Misses + 1);
        if (ReferenceEquals(sprite.Image, source.Image)) return end;   // 何も変わらなかった (強さ 0 など)

        // 結果の画像を「このフレームで捨てるもの」から外して、キャッシュの持ち物にする
        if (!ctx.Temporaries.Remove(sprite.Image)) return end;
        if (entry != null)
        {
            _filterCacheBytes -= BytesOf(entry.Sprite);
            entry.Sprite.Image.Dispose();
        }
        else _filterCache[key] = entry = new FilterCacheEntry();
        entry.Signature = signature; entry.Sprite = sprite; entry.LastUsed = _tick;
        _filterCacheBytes += BytesOf(sprite);
        if (_filterCacheBytes > FilterCacheBudgetBytes) TrimFilterCache();
        return end;
    }

    /// <summary>メモリの上限を超えたら、今のフレームで使っていないものを古い順に捨てる。</summary>
    private void TrimFilterCache()
    {
        foreach (var (k, e) in _filterCache.Where(kv => kv.Value.LastUsed < _frameStartTick).OrderBy(kv => kv.Value.LastUsed).ToList())
        {
            if (_filterCacheBytes <= FilterCacheBudgetBytes * 3 / 4) break;
            _filterCacheBytes -= BytesOf(e.Sprite);
            e.Sprite.Image.Dispose();
            _filterCache.Remove(k);
        }
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
        var canvas = surface.Canvas;
        // pending: 効果をかけている途中の画面。null のときは、画面 (surface) そのものが最新
        Sprite? pending = null;
        float originX = 0, originY = 0;
        // CPU 描画で、画面のピクセルを複製せずに指しているだけの画像 (効果が何も変えなければ、描き戻さずに済む)
        SKImage? screenView = null;

        void WriteBack()
        {
            if (pending is not { } p) return;
            pending = null;
            if (ReferenceEquals(p.Image, screenView)) return;   // 画面そのもの: 何も変わっていない
            // CPU 描画で、画面と同じ大きさ・同じ位置の絵なら、行ごとに並列に写す (Skia の描画は 1 スレッドで遅い)
            if (ctx.Gpu == null && CopyOver(p, originX - p.Cx, originY - p.Cy, surface)) return;
            canvas.Save();
            canvas.ResetMatrix();
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(p.Image, originX - p.Cx, originY - p.Cy, paint);
            canvas.Restore();
        }

        // 続けて並んだ色の変換は、行列を掛け合わせて 1 回で処理する
        float[]? matrix = null;
        EffectArgs matrixArgs = default;
        void FlushMatrix()
        {
            if (matrix == null) return;
            var m = matrix; matrix = null;
            // CPU 描画なら画面のピクセルをその場で書き換える (複製も描き戻しも不要)
            if (ctx.Gpu == null && pending == null && PixelOps.CanApply(m))
            {
                using var pixmap = surface.PeekPixels();
                if (pixmap != null && pixmap.ColorType == SKColorType.Bgra8888)
                {
                    PixelOps.ColorMatrix(pixmap.GetPixels(), pixmap.Width, pixmap.Height, pixmap.RowBytes, m);
                    return;
                }
            }
            if (pending == null)
            {
                SKImage? view = null;
                if (ctx.Gpu == null)
                {
                    using var current = surface.PeekPixels();
                    if (current != null) view = SKImage.FromPixels(current);
                }
                screenView = view;
                var snap = Sprite.Centered(ctx.Own(view ?? surface.Snapshot()));
                originX = snap.Cx; originY = snap.Cy;
                pending = snap;
            }
            pending = Effects.ApplyMatrix(pending.Value, matrixArgs, m);
        }

        foreach (var e in obj.Effects)
        {
            if (!e.Enabled || e.Definition.Category != EffectCategory.Filter) continue;
            var fx = Effects.Find(e.DefinitionId);
            if (fx == null) continue;
            var args = new EffectArgs { Effect = e, Object = obj, Bounds = bounds, Context = ctx, IsFrameBuffer = true };

            if (fx is IColorMatrixEffect matrixEffect)
            {
                if (matrixEffect.GetMatrix(args) is { } m)
                {
                    if (matrix == null) WriteBack();
                    matrix = matrix == null ? m : Effects.Concat(m, matrix);
                    matrixArgs = args;
                }
                continue;
            }
            FlushMatrix();
            if (ctx.Gpu == null && fx is IPixelEffect pixelEffect)
            {
                WriteBack();
                using var pixmap = surface.PeekPixels();
                if (pixmap != null && pixmap.ColorType == SKColorType.Bgra8888)
                {
                    pixelEffect.Process(new PixelBuffer(pixmap.GetPixels(), pixmap.Width, pixmap.Height, pixmap.RowBytes), args);
                    continue;
                }
            }
            if (pending == null)
            {
                // 今の画面を効果に渡す。CPU 描画では複製せずに画面のピクセルを指す画像にする
                // (効果は渡された絵を書き換えず、新しい絵を返す。描き戻すまで画面には何も描かない)
                SKImage? view = null;
                if (ctx.Gpu == null)
                {
                    using var current = surface.PeekPixels();
                    if (current != null) view = SKImage.FromPixels(current);
                }
                screenView = view;
                var snap = Sprite.Centered(ctx.Own(view ?? surface.Snapshot()));
                originX = snap.Cx; originY = snap.Cy;
                pending = snap;
            }
            var before = pending.Value;
            var after = fx.Apply(before, args);
            pending = after;
        }
        FlushMatrix();
        WriteBack();
    }

    /// <summary>
    /// 絵 p を、画面の (x, y) にそのまま (Src で) 写す。画面と同じ大きさ・整数の位置・同じ形式のときだけ、
    /// 行ごとに並列に写して true を返す。条件に合わなければ何もせず false。
    /// </summary>
    private static unsafe bool CopyOver(Sprite p, float x, float y, SKSurface surface)
    {
        if (p.Scale != 1 || MathF.Abs(x) > 1e-3f || MathF.Abs(y) > 1e-3f) return false;
        using var dst = surface.PeekPixels();
        using var src = p.Image.PeekPixels();
        if (dst == null || src == null || src.Width != dst.Width || src.Height != dst.Height
            || src.ColorType != dst.ColorType || src.AlphaType != dst.AlphaType || dst.ColorType != SKColorType.Bgra8888) return false;
        byte* s = (byte*)src.GetPixels(), d = (byte*)dst.GetPixels();
        int srcRow = src.RowBytes, dstRow = dst.RowBytes, bytes = dst.Width * 4;
        PixelOps.ForBands(dst.Width, dst.Height, (y0, y1) =>
        {
            for (int row = y0; row < y1; row++) Buffer.MemoryCopy(s + (long)row * srcRow, d + (long)row * dstRow, bytes, bytes);
        });
        return true;
    }

    /// <summary>直前の GetSource が返した絵の倍率 (プロキシのとき 1 より大きい)。</summary>
    private float _sourceScale = 1;
    /// <summary>直前の GetSource が返した絵が、フレームごとに変わるもの (Lottie) か。</summary>
    private bool _sourceAnimated;
    /// <summary>直前の GetSource が返したシーンの絵が、前のフレームのものを使い回したもの (止まっているシーン) か。</summary>
    private bool _sourceStatic;
    /// <summary>今描いているオブジェクトに要る細かさ (Detail)。</summary>
    private float _detail = 1;
    private readonly VectorSources _vectors = new();

    /// <summary>小さく作った絵を、元の大きさの絵にする。</summary>
    private static Sprite Upscale(RenderContext ctx, Sprite s, float scale)
    {
        int w = (int)MathF.Round(s.Width * scale), h = (int)MathF.Round(s.Height * scale);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, BlendMode = SKBlendMode.Src };
        SKImage image;
        if (ctx.Gpu == null)
            image = PixelOps.DrawParallel(w, h, c => c.DrawImage(s.Image, SKRect.Create(s.Width, s.Height), SKRect.Create(w, h), paint));
        else
        {
            using var surface = ctx.CreateSurface(w, h);
            if (surface == null) return s with { Scale = scale };
            surface.Canvas.DrawImage(s.Image, SKRect.Create(s.Width, s.Height), SKRect.Create(w, h), paint);
            image = surface.Snapshot();
        }
        return new Sprite(ctx.Own(image), w / 2f, h / 2f);
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
                var image = Media.GetVideoFrame(obj.Id, main.GetString("file"), t, main.GetBool("loop"), out var scale, _detail);
                _sourceScale = scale;
                return image;
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
                int anim = main.GetInt("anim");
                if (anim <= 0 || text.Length == 0)
                    return Cached(key, () => Generators.RenderText(text, font, size, ParseColor(color), bold, italic, align, sp, ld));
                // テキストアニメーション: 動いている間は毎フレーム描く。終わったら同じ絵を使い回す
                double t = p.FrameToSeconds(frame - obj.Start);
                double interval = main.Eval("interval", bounds, obj.Start), duration = main.Eval("duration", bounds, obj.Start);
                bool reverse = main.GetBool("reverse");
                int count = Generators.CountChars(text);
                double end = Math.Max(0, count - 1) * interval + (anim == 1 ? 0 : duration);
                Generators.CharStyle Style(int i)
                {
                    int order = reverse ? count - 1 - i : i;
                    double local = t - order * interval;
                    if (anim == 1) return new(local >= 0 ? 1 : 0, 0, 1);
                    double q = duration <= 0 ? (local >= 0 ? 1 : 0) : Math.Clamp(local / duration, 0, 1);
                    float e = (float)(q * q * (3 - 2 * q));
                    return anim switch
                    {
                        2 => new(e, 0, 1),
                        3 => new(e, (1 - e) * size * 0.6f, 1),
                        _ => new(e > 0 ? 1 : 0, 0, e),
                    };
                }
                if (t >= end) return Cached($"{key}|A{anim}|{reverse}|done", () => Generators.RenderText(text, font, size, ParseColor(color), bold, italic, align, sp, ld, Style));
                var img = Generators.RenderText(text, font, size, ParseColor(color), bold, italic, align, sp, ld, Style);
                return img == null ? null : ctx.Own(img);
            }
            case EffectRegistry.Shape:
            {
                int type = main.GetInt("type");
                float size = (float)main.Eval("size", bounds, frame), aspect = (float)main.Eval("aspect", bounds, frame), line = (float)main.Eval("line", bounds, frame);
                string color = main.GetString("color");
                string key = $"S|{type}|{Generators.F(size)}|{Generators.F(aspect)}|{Generators.F(line)}|{color}|{p.Settings.Width}x{p.Settings.Height}";
                return Cached(key, () => Generators.RenderShape(type, size, aspect, line, ParseColor(color), p.Settings.Width, p.Settings.Height));
            }
            case EffectRegistry.Vector:
            {
                string file = main.GetString("file");
                float size = (float)main.Eval("size", bounds, frame);
                var palette = VectorColors.PaletteOfFile(file);
                var hidden = VectorFiles.ParseIds(main.GetString("hide"));
                var info = palette.Count == 0 && hidden.Count == 0 ? _vectors.Get(file) : _vectors.Get(file, VectorColors.MapFor(main, palette), hidden);
                if (info == null) return null;
                if (!info.Animated)
                    return Cached($"V|{file}|{info.Version}|{Generators.F(size)}", () => _vectors.Render(info, size, 0));
                _sourceAnimated = true;
                double t = SourceTime(p, obj, bounds, frame);
                double at = main.GetBool("loop") ? t % Math.Max(1e-6, info.Duration) : Math.Min(t, info.Duration);
                return AnimatedVector(obj, depth, info, size, at);
            }
            case EffectRegistry.SceneRef:
            {
                if (depth >= MaxSceneDepth) return null;
                var scene = p.FindScene(main.GetString("scene"));
                if (scene == null) return null;
                int inner = SceneTime.InnerFrame(p, obj, scene, bounds, frame);
                return RenderSceneRef(ctx, obj, scene, inner, depth);
            }
        }
        // プラグインが追加したメディアオブジェクト
        var source = Effects.FindSource(main.DefinitionId);
        if (source == null) return null;
        var sourceArgs = new EffectArgs { Effect = main, Object = obj, Bounds = bounds, Context = ctx };
        // CacheKey を返すプラグインは、同じキーの間は前に作った絵を使い回す (動かない背景を毎フレーム描かない)
        string? cacheKey = source.CacheKey(sourceArgs);
        if (cacheKey == null) return source.Render(sourceArgs);
        string fullKey = "plugin|" + main.DefinitionId + "|" + (ctx.Gpu != null ? "g|" : "c|") + cacheKey;
        _tick++;
        if (_generated.TryGetValue(fullKey, out var hit)) { _generatedUse[fullKey] = _tick; return hit; }
        var made = source.Render(sourceArgs);
        // エンジンの持ち物 (ctx.Own したもの) だけをキャッシュに移す。プラグインが自分で持っている絵は預からない
        if (made == null || !ctx.Temporaries.Remove(made)) return made;
        return Cached(fullKey, () => made);
    }

    // ---------------- シーンオブジェクト ----------------

    private sealed class SceneCacheEntry
    {
        public string SceneId = "";
        public List<TimelineObject> Objects = new();
        public List<bool> Visible = new();
        public long FileStamp;
        public int Frame;            // 止まっているシーンなら -1 (どのフレームでも同じ絵)
        public int Width, Height;
        public SKImage Image = null!;
        public long LastUsed;
    }
    // シーンオブジェクトの絵。中身が同じ (同じオブジェクト・同じフレーム、止まっているシーンならフレームは問わない) なら使い回す
    private readonly Dictionary<string, SceneCacheEntry> _sceneCache = new();
    private const int SceneCacheLimit = 32;

    /// <summary>シーンオブジェクトの絵を使い回した回数 / 描き直した回数 (計測用)。</summary>
    public (long Hits, long Misses) SceneCacheStats { get; private set; }

    /// <summary>
    /// 入れ子のシーンを描いて絵にする。プレビューでは、画面に出る大きさ (Detail) までしか細かく描かない。
    /// 中身が前と同じなら、前に描いた絵を返す。
    /// </summary>
    private SKImage? RenderSceneRef(RenderContext ctx, TimelineObject obj, Scene scene, int inner, int depth)
    {
        var p = ctx.Project;
        int W = p.Settings.Width, H = p.Settings.Height;
        // 細かさは 1/8 刻みに切り上げる (拡大率が少し動くたびに大きさが変わって、使い回せなくならないように)
        float detail = Math.Min(1, MathF.Ceiling(_detail * 8) / 8);
        int w = Math.Max(1, (int)MathF.Ceiling(W * detail)), h = Math.Max(1, (int)MathF.Ceiling(H * detail));

        var active = scene.ObjectsAt(inner).ToList();
        bool still = active.All(IsStill);
        var visible = active.Select(o => scene.GetLayer(o.Layer).Visible).ToList();
        long stamp = FileStampOf(active);
        string key = depth == 0 ? obj.Id : obj.Id + "@" + depth;
        _tick++;
        if (_sceneCache.TryGetValue(key, out var hit) && hit.SceneId == scene.Id && hit.Width == w && hit.Height == h
            && hit.Frame == (still ? -1 : inner) && hit.Objects.Count == active.Count
            && hit.Objects.Zip(active).All(x => ReferenceEquals(x.First, x.Second)) && hit.Visible.SequenceEqual(visible) && hit.FileStamp == stamp)
        {
            hit.LastUsed = _tick;
            _sourceScale = W / (float)w;
            _sourceStatic = true;
            SceneCacheStats = (SceneCacheStats.Hits + 1, SceneCacheStats.Misses);
            return hit.Image;
        }

        using var surface = ctx.CreateSurface(w, h);
        if (surface == null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(w / (float)W, h / (float)H);
        // 中のオブジェクトの細かさ (Detail) は、この絵の縮小率で決める
        float outerScale = _renderScale;
        _renderScale = Math.Clamp(w / (float)W, 1f / 64, 1);
        try { RenderScene(ctx, scene, inner, surface, depth + 1); }
        finally { _renderScale = outerScale; }
        surface.Canvas.ResetMatrix();
        var image = surface.Snapshot();
        SceneCacheStats = (SceneCacheStats.Hits, SceneCacheStats.Misses + 1);
        // 中の描画で書き換わるので、描き終えてから決める
        _sourceScale = W / (float)w;
        _sourceStatic = false;

        if (hit == null)
        {
            if (_sceneCache.Count >= SceneCacheLimit)
                foreach (var k in _sceneCache.Where(kv => kv.Value.LastUsed < _frameStartTick).OrderBy(kv => kv.Value.LastUsed)
                             .Take(SceneCacheLimit / 2).Select(kv => kv.Key).ToList())
                {
                    _sceneCache[k].Image.Dispose();
                    _sceneCache.Remove(k);
                }
            _sceneCache[key] = hit = new SceneCacheEntry();
        }
        else hit.Image.Dispose();
        hit.SceneId = scene.Id; hit.Objects = active; hit.Visible = visible; hit.FileStamp = stamp; hit.Frame = still ? -1 : inner;
        hit.Width = w; hit.Height = h; hit.Image = image; hit.LastUsed = _tick;
        return image;
    }

    /// <summary>画像ファイルの更新日時をまとめた値 (ファイルが書き換えられたら、シーンの絵を描き直すため)。</summary>
    private static long FileStampOf(List<TimelineObject> objects)
    {
        long stamp = 0;
        foreach (var o in objects)
        {
            if (o.Effects.Count == 0 || o.Main.DefinitionId != EffectRegistry.Image) continue;
            string file = o.Main.GetString("file");
            long t = 0;
            try { if (file.Length > 0 && File.Exists(file)) t = File.GetLastWriteTimeUtc(file).Ticks; }
            catch { t = 0; }
            stamp = stamp * 31 + t;
        }
        return stamp;
    }

    /// <summary>
    /// 時間がたっても見た目が変わらないオブジェクトか (シーンの絵を使い回してよいかの判定)。
    /// 動画・アニメーション・スクリプトなど、フレームごとに変わりうるものは false。迷うものも false にする。
    /// </summary>
    private static bool IsStill(TimelineObject o)
    {
        if (!o.Enabled || o.Effects.Count == 0) return true;
        var main = o.Main;
        switch (main.Definition.Category)
        {
            case EffectCategory.AudioSource: return true;   // 絵には出ない
            case EffectCategory.VideoSource:
                if (main.DefinitionId is not (EffectRegistry.Image or EffectRegistry.Shape or EffectRegistry.Text)) return false;
                if (main.DefinitionId == EffectRegistry.Text && main.GetInt("anim") > 0) return false;
                break;
            case EffectCategory.Control:
            case EffectCategory.Filter:
                break;
            default: return false;
        }
        foreach (var e in o.Effects)
        {
            if (!e.Enabled) continue;
            var cat = e.Definition.Category;
            if (cat is EffectCategory.AudioPlay or EffectCategory.AudioFilter) continue;
            // フェード・トランジションは時刻で変わる。結果が入力だけで決まらないもの (スクリプトなど) も変わりうる
            if (cat == EffectCategory.Filter && (e.DefinitionId is "filter.fade" or "filter.transition"
                || !Effects.IsDeterministic(e.DefinitionId, Effects.Find(e.DefinitionId)))) return false;
            foreach (var v in e.Tracks.Values)
            {
                if (v.IsMoving) return false;
                for (int i = 1; i < v.Values.Count; i++) if (!v.Values[i].Equals(v.Values[0])) return false;   // 中間点で値が切り替わる
            }
        }
        return true;
    }

    // Lottie の、オブジェクトごとの直前の絵。同じ時刻・大きさなら描き直さない (止まって編集しているとき、
    // 同じフレームを何度も描き直すので)
    private readonly Dictionary<string, (string Key, SKImage? Image, long LastUsed)> _lottieLast = new();

    /// <summary>Lottie の絵を描き直した回数 (計測・テスト用)。</summary>
    public long LottieRenders { get; private set; }

    private SKImage? AnimatedVector(TimelineObject obj, int depth, VectorSources.Item info, float size, double at)
    {
        string key = $"{info.Version}|{Generators.F(size)}|{Generators.F(at)}";
        string id = depth == 0 ? obj.Id : obj.Id + "@" + depth;
        _tick++;
        if (_lottieLast.TryGetValue(id, out var last) && last.Key == key)
        {
            _lottieLast[id] = last with { LastUsed = _tick };
            return last.Image;
        }
        var img = _vectors.Render(info, size, at);
        LottieRenders++;
        if (_lottieLast.Count >= 64 && !_lottieLast.ContainsKey(id))
            foreach (var k in _lottieLast.Where(kv => kv.Value.LastUsed < _frameStartTick).Select(kv => kv.Key).ToList())
            {
                _lottieLast[k].Image?.Dispose();
                _lottieLast.Remove(k);
            }
        last.Image?.Dispose();
        _lottieLast[id] = (key, img, _tick);
        return img;
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
        if (_generated.Count >= 512)
        {
            // 今のフレームで使っていないものを、古い順に半分捨てる
            foreach (var k in _generatedUse.Where(kv => kv.Value < _frameStartTick).OrderBy(kv => kv.Value).Take(256).Select(kv => kv.Key).ToList())
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
        foreach (var e in _filterCache.Values) e.Sprite.Image.Dispose();
        foreach (var e in _dofCache.Values) e.Sprite.Image.Dispose();
        foreach (var e in _sceneCache.Values) e.Image.Dispose();
        foreach (var e in _lottieLast.Values) e.Image?.Dispose();
        _lottieLast.Clear();
        _sceneCache.Clear();
        _dofCache.Clear();
        _filterCache.Clear();
        _filterCacheBytes = 0;
        _gpuSurface?.Dispose();
        _gpuSurface = null;
        foreach (var img in _generated.Values) img?.Dispose();
        _generated.Clear();
        _vectors.Dispose();
        Media.Dispose();
    }
}
