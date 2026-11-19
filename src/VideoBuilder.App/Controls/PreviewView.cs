using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using VideoBuilder.App.Services;
using VideoBuilder.Core;

namespace VideoBuilder.App.Controls;

/// <summary>プレビュー画面。フレームの表示と、選択オブジェクトの枠・ドラッグ移動を受け持つ。</summary>
public sealed class PreviewView : Control
{
    private Rect _dest;

    private bool _pressed, _dragging;
    private Point _pressPoint;
    private readonly List<(TimelineObject Obj, int IndexX, int IndexY, double X, double Y)> _dragTargets = new();

    /// <summary>表示倍率 (0 = ウィンドウに合わせる)。</summary>
    public double Zoom { get; set; }

    public PreviewView()
    {
        ClipToBounds = true;
        Focusable = true;
        Svc.Preview.FrameReady += OnFrameReady;
        Svc.Session.SelectionChanged += InvalidateVisual;
        Svc.Preview.PlayStateChanged += InvalidateVisual;
        Svc.Theme.Changed += InvalidateVisual;
    }

    // フレームは、描画のスレッドが描いたものを画面の描画でそのまま描く (以前は UI スレッドで WriteableBitmap に写していた)
    private void OnFrameReady() => InvalidateVisual();

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

        if (Svc.Preview.HasFrame)
        {
            // 再生中は軽い補間にして、止まっているときだけ高品質にする
            var quality = Svc.Preview.IsPlaying ? SKFilterQuality.Low : SKFilterQuality.High;
            context.Custom(new FrameDrawOperation(_dest, new Rect(Bounds.Size), quality));
        }
        else context.FillRectangle(Brushes.Black, _dest);
        context.DrawRectangle(null, new Pen(Svc.Brush("Border.Default")), _dest.Inflate(0.5));

        // 選択中オブジェクトの枠 (角ばった 1px 枠 + 四隅のハンドル)
        var accent = Svc.Brush("Accent");
        var pen = new Pen(accent, 1);
        foreach (var (id, quad) in Svc.Preview.Quads)
        {
            if (!Svc.Session.IsSelected(id)) continue;
            var pts = quad.Select(ToView).ToArray();
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
            Svc.Session.BeginEdit("オブジェクトの移動");
        }
        var s = Svc.Session.Project.Settings;
        double dx = (pos.X - _pressPoint.X) / _dest.Width * s.Width, dy = (pos.Y - _pressPoint.Y) / _dest.Height * s.Height;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0;   // Shift: 水平 / 垂直に固定
        }
        foreach (var (o, ix, iy, x0, y0) in _dragTargets)
        {
            // BeginEdit 後もオブジェクトの実体は同じ (複製されるのは「編集前」の側)
            var draw = o.Draw!;
            draw.Tracks["x"].Values[ix] = Math.Round(x0 + dx, 1);
            draw.Tracks["y"].Values[iy] = Math.Round(y0 + dy, 1);
        }
        Svc.Session.NotifyChanged();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging) Svc.Session.CommitEdit();
        _pressed = _dragging = false;
        _dragTargets.Clear();
        e.Pointer.Capture(null);
    }
}
