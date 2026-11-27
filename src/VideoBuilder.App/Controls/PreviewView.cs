using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Controls;

/// <summary>プレビュー画面。フレームの表示と、選択オブジェクトの枠・ドラッグ移動を受け持つ。</summary>
public sealed class PreviewView : Control
{
    private Rect _dest;

    private bool _pressed, _dragging;
    private Point _pressPoint;
    private readonly List<(TimelineObject Obj, int IndexX, int IndexY, double X, double Y)> _dragTargets = new();
    /// <summary>移動の軌跡の点 (中間点など) をドラッグしているときの対象。IndexX・IndexY が -1 の軸は動かさない。</summary>
    private (TimelineObject Obj, int IndexX, int IndexY, double X, double Y)? _pathTarget;

    /// <summary>表示倍率 (0 = ウィンドウに合わせる)。</summary>
    public double Zoom { get; set; }

    /// <summary>
    /// 選択中のオブジェクトの枠を描く層。プレビューの上に重ねて置く (PreviewArea)。
    /// 画面と共有するテクスチャでフレームを出すときは、フレームがこのコントロールの描いたものより上に出るので、枠は別の層にする。
    /// </summary>
    public Control Selection { get; }

    public PreviewView()
    {
        ClipToBounds = true;
        Focusable = true;
        Selection = new SelectionLayer(this) { IsHitTestVisible = false, ClipToBounds = true };
        Svc.Preview.FrameReady += OnFrameReady;
        Svc.Session.SelectionChanged += Invalidate;
        Svc.Preview.PlayStateChanged += Invalidate;
        Svc.Theme.Changed += Invalidate;
        Svc.Preview.BackendChanged += Invalidate;
    }

    private void Invalidate()
    {
        InvalidateVisual();
        Selection.InvalidateVisual();
    }

    // フレームは、描画のスレッドが描いたものを画面の描画でそのまま描く (以前は UI スレッドで WriteableBitmap に写していた)
    private void OnFrameReady() => Invalidate();

    // ---------------- 画面と共有するテクスチャ ----------------

    private SharedPreviewTarget? _shared;
    private bool _creatingShared;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 再生は、このプレビューを表示している画面の更新ごとに進める
        Svc.Preview.FrameClock = TopLevel.GetTopLevel(this);
        _ = AttachSharedAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (ReferenceEquals(Svc.Preview.FrameClock, e.RootVisual as TopLevel)) Svc.Preview.FrameClock = null;
        // 別のウィンドウへ移るときなど: 共有をやめる (後片付けは描画のスレッドが行う)
        if (_shared != null)
        {
            ElementComposition.SetElementChildVisual(this, null);
            _shared.Presented -= Invalidate;
            if (ReferenceEquals(Svc.Preview.SharedTarget, _shared)) Svc.Preview.SharedTarget = null;
            _shared = null;
        }
    }

    /// <summary>画面の描画がテクスチャの共有に対応していれば、共有の描画先を作ってプレビューに渡す。</summary>
    private async Task AttachSharedAsync()
    {
        if (_shared != null || _creatingShared || Environment.GetEnvironmentVariable("VIDEO_BUILDER_SHARED_PREVIEW") == "0") return;
        _creatingShared = true;
        try
        {
            var shared = await SharedPreviewTarget.TryCreateAsync(this);
            if (shared == null) return;
            if (VisualRoot == null) { shared.DisposeContext(); return; }   // 作っている間に外された
            _shared = shared;
            ElementComposition.SetElementChildVisual(this, shared.Visual);
            shared.Presented += Invalidate;
            Svc.Preview.SharedTarget = shared;
        }
        finally { _creatingShared = false; }
    }

    /// <summary>
    /// 選択中で X か Y が動くオブジェクトの、移動の軌跡 (モーションパス) と区間の境目の点 (始め・中間点・終わり)。
    /// 点は画面上の位置 (プロジェクトの座標)。親の動きは含め、グループ制御・カメラ・効果の動きは含めない。
    /// </summary>
    private static IEnumerable<(TimelineObject Obj, List<(int Frame, float X, float Y)> Path, List<(int Bound, float X, float Y)> Points)> MotionPaths()
    {
        var session = Svc.Session;
        var project = session.Project;
        foreach (var o in session.SelectedObjects)
        {
            var draw = o.Draw;
            if (draw == null) continue;
            bool moving = (draw.Tracks.GetValueOrDefault("x")?.IsMoving ?? false) || (draw.Tracks.GetValueOrDefault("y")?.IsMoving ?? false);
            bool parented = draw.Tracks.TryGetValue("parent", out var parent) && parent.Values.Any(v => v >= 0.5);
            if (!moving && !parented) continue;
            int step = Math.Max(1, o.Length / 240);
            var path = ObjectMotion.Path(session.Scene, project, o, step);
            var bounds = o.Bounds;
            float cx = project.Settings.Width / 2f, cy = project.Settings.Height / 2f;
            var points = new List<(int, float, float)>();
            if (moving)
                for (int k = 0; k < bounds.Length; k++)
                {
                    int f = Math.Clamp(k == bounds.Length - 1 ? o.End : bounds[k], o.Start, o.End);
                    var (_, x, y) = ObjectMotion.PointAt(session.Scene, project, o, bounds, f, cx, cy);
                    points.Add((k, x, y));
                }
            yield return (o, path, points);
        }
    }

    /// <summary>選択中のオブジェクトの枠 (角ばった 1px 枠 + 四隅のハンドル) と、移動の軌跡。</summary>
    private sealed class SelectionLayer(PreviewView owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (owner._dest.Width <= 0) return;
            var accent = Svc.Brush("Accent");
            var pen = new Pen(accent, 1);
            if (!Svc.Preview.IsPlaying)
            {
                // 移動の軌跡: 点線で道筋を、四角で区間の境目 (ドラッグで位置を変えられる) を描く
                var pathPen = new Pen(accent, 1, new DashStyle(new double[] { 4, 3 }, 0));
                foreach (var (_, path, points) in MotionPaths())
                {
                    if (path.Count >= 2)
                    {
                        var geo = new StreamGeometry();
                        using (var g = geo.Open())
                        {
                            g.BeginFigure(owner.ToView(new SKPoint(path[0].X, path[0].Y)), false);
                            foreach (var (_, x, y) in path.Skip(1)) g.LineTo(owner.ToView(new SKPoint(x, y)));
                            g.EndFigure(false);
                        }
                        context.DrawGeometry(null, pathPen, geo);
                    }
                    foreach (var (_, x, y) in points)
                    {
                        var v = owner.ToView(new SKPoint(x, y));
                        context.DrawEllipse(Brushes.White, pen, v, 4.5, 4.5);
                    }
                }
            }
            foreach (var (id, quad) in Svc.Preview.Quads)
            {
                if (!Svc.Session.IsSelected(id)) continue;
                var pts = quad.Select(owner.ToView).ToArray();
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(pts[0], false);
                    g.LineTo(pts[1]); g.LineTo(pts[2]); g.LineTo(pts[3]);
                    g.EndFigure(true);
                }
                context.DrawGeometry(null, pen, geo);
                foreach (var p in pts)
                    context.DrawRectangle(Brushes.White, pen, new Rect(Math.Round(p.X) - 3, Math.Round(p.Y) - 3, 6, 6));
            }
        }
    }

    /// <summary>プレビューのフレームを Skia で直接描く (画面の描画のスレッドで動く)。</summary>
    private sealed class FrameDrawOperation(Rect dest, Rect clip, SKFilterQuality quality) : ICustomDrawOperation
    {
        public Rect Bounds => clip;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;   // フレームは毎回変わりうるので、いつも描き直す
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var rect = new SKRect((float)dest.Left, (float)dest.Top, (float)dest.Right, (float)dest.Bottom);
            if (Svc.Preview.DrawFrame(canvas, rect, quality, lease.GrContext)) return;
            using var black = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(rect, black);
        }
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Svc.Brush("Bg.Base"), new Rect(Bounds.Size));
        var settings = Svc.Session.Project.Settings;
        double pw = settings.Width, ph = settings.Height;
        double scale = Zoom > 0 ? Zoom : Math.Min((Bounds.Width - 16) / pw, (Bounds.Height - 16) / ph);
        if (scale <= 0 || double.IsNaN(scale)) return;
        double w = Math.Round(pw * scale), h = Math.Round(ph * scale);
        _dest = new Rect(Math.Round((Bounds.Width - w) / 2), Math.Round((Bounds.Height - h) / 2), w, h);
        // 表示される大きさ (物理ピクセル) を知らせる。自動の設定なら、この大きさで描かれる
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        Svc.Preview.DisplaySize = ((int)Math.Ceiling(w * scaling), (int)Math.Ceiling(h * scaling));

        bool shared = _shared != null && Svc.Preview.PresentsToShared;
        if (_shared != null)
        {
            // 共有のテクスチャの絵は、フレームを出す場所にぴったり重ねる
            _shared.Visual.Offset = new Avalonia.Vector3D(_dest.X, _dest.Y, 0);
            _shared.Visual.Size = new Vector(_dest.Width, _dest.Height);
            _shared.Visual.Visible = shared && _shared.HasFrame;
        }
        if (shared)
        {
            if (!_shared!.HasFrame) context.FillRectangle(Brushes.Black, _dest);
        }
        else if (Svc.Preview.HasFrame)
        {
            // 画面に合わせて拡大するときは、いつも軽い補間 (バイリニア) にする (止まっているときも。高品質の拡大は重い)。
            // 縮小して表示するときだけ、止まっている間はなめらかに縮める (ちらつきを抑える)
            var (fw, fh) = Svc.Preview.LastRenderSize;
            bool enlarging = fw <= 0 || (_dest.Width * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) >= fw - 0.5);
            var quality = Svc.Preview.IsPlaying || enlarging ? SKFilterQuality.Low : SKFilterQuality.Medium;
            context.Custom(new FrameDrawOperation(_dest, new Rect(Bounds.Size), quality));
        }
        else context.FillRectangle(Brushes.Black, _dest);
        context.DrawRectangle(null, new Pen(Svc.Brush("Border.Default")), _dest.Inflate(0.5));
    }

    private Point ToView(SKPoint p)
    {
        var s = Svc.Session.Project.Settings;
        return new Point(_dest.X + p.X / s.Width * _dest.Width, _dest.Y + p.Y / s.Height * _dest.Height);
    }

    private Point ToProject(Point p)
    {
        var s = Svc.Session.Project.Settings;
        return new Point((p.X - _dest.X) / _dest.Width * s.Width, (p.Y - _dest.Y) / _dest.Height * s.Height);
    }

    private static bool InQuad(SKPoint[] q, Point p)
    {
        bool? sign = null;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i]; var b = q[(i + 1) % 4];
            double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (Math.Abs(cross) < 1e-9) continue;
            bool s = cross > 0;
            if (sign == null) sign = s; else if (sign != s) return false;
        }
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _dest.Width <= 0) return;
        var session = Svc.Session;
        var pp = ToProject(e.GetPosition(this));
        // 移動の軌跡の点: ドラッグでその区間の境目の位置 (X・Y の値) を変える
        var view = e.GetPosition(this);
        foreach (var (o, _, points) in MotionPaths())
        {
            foreach (var (k, x, y) in points)
            {
                var v = ToView(new SKPoint(x, y));
                if (Math.Abs(v.X - view.X) > 7 || Math.Abs(v.Y - view.Y) > 7) continue;
                var draw = o.Draw!;
                var vx = draw.Tracks["x"]; var vy = draw.Tracks["y"];
                vx.EnsureSections(o.Sections); vy.EnsureSections(o.Sections);
                int ix = vx.IsMoving ? k : -1, iy = vy.IsMoving ? k : -1;
                _pathTarget = (o, ix, iy, ix >= 0 ? vx.Values[ix] : 0, iy >= 0 ? vy.Values[iy] : 0);
                _pressed = true;
                _dragging = false;
                _pressPoint = view;
                e.Pointer.Capture(this);
                return;
            }
        }
        string? hit = null;
        var quads = Svc.Preview.Quads;
        for (int i = quads.Count - 1; i >= 0; i--)
        {
            var o = session.Scene.Find(quads[i].Id);
            if (o == null || session.Scene.GetLayer(o.Layer).Locked) continue;
            if (InQuad(quads[i].Quad, pp)) { hit = quads[i].Id; break; }
        }
        if (hit == null) { session.Select(null); return; }
        if (!session.IsSelected(hit)) session.Select(hit, e.KeyModifiers.HasFlag(KeyModifiers.Control));

        _dragTargets.Clear();
        int frame = session.CurrentFrame;
        foreach (var o in session.SelectedObjects)
        {
            var draw = o.Draw;
            if (draw == null || !o.Contains(frame)) continue;
            var vx = draw.Tracks["x"]; var vy = draw.Tracks["y"];
            int ix = o.ValueIndexAt(vx, frame), iy = o.ValueIndexAt(vy, frame);
            _dragTargets.Add((o, ix, iy, vx.Values[ix], vy.Values[iy]));
        }
        _pressed = _dragTargets.Count > 0;
        _dragging = false;
        _pressPoint = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed) return;
        var pos = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _pressPoint.X) < 3 && Math.Abs(pos.Y - _pressPoint.Y) < 3) return;
            _dragging = true;
            Svc.Session.BeginEdit(_pathTarget != null ? "移動の軌跡の変更" : "オブジェクトの移動");
        }
        var s = Svc.Session.Project.Settings;
        double dx = (pos.X - _pressPoint.X) / _dest.Width * s.Width, dy = (pos.Y - _pressPoint.Y) / _dest.Height * s.Height;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0;   // Shift: 水平 / 垂直に固定
        }
        if (_pathTarget is { } pt)
        {
            var d = pt.Obj.Draw!;
            if (pt.IndexX >= 0) d.Tracks["x"].Values[pt.IndexX] = Math.Round(pt.X + dx, 1);
            if (pt.IndexY >= 0) d.Tracks["y"].Values[pt.IndexY] = Math.Round(pt.Y + dy, 1);
            Svc.Session.NotifyChanged(new[] { pt.Obj });
            Selection.InvalidateVisual();
            return;
        }
        foreach (var (o, ix, iy, x0, y0) in _dragTargets)
        {
            // BeginEdit 後もオブジェクトの実体は同じ (複製されるのは「編集前」の側)
            var draw = o.Draw!;
            draw.Tracks["x"].Values[ix] = Math.Round(x0 + dx, 1);
            draw.Tracks["y"].Values[iy] = Math.Round(y0 + dy, 1);
        }
        Svc.Session.NotifyChanged(_dragTargets.Select(t => t.Obj));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging) Svc.Session.CommitEdit();
        _pressed = _dragging = false;
        _dragTargets.Clear();
        _pathTarget = null;
        e.Pointer.Capture(null);
    }
}
