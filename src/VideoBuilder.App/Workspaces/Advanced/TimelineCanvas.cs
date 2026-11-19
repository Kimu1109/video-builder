using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Workspaces.Advanced;

public enum TimelineArea { Corner, Ruler, Header, Content }
public enum ObjectPart { None, Body, LeftEdge, RightEdge, KeyFrame }

public readonly record struct TimelineHit(TimelineArea Area, int Layer, int Frame, TimelineObject? Object, ObjectPart Part, int KeyIndex);

/// <summary>
/// タイムライン本体。時間ルーラー・レイヤーヘッダ・オブジェクトを、表示範囲だけ直接描く (仮想化)。
/// </summary>
public sealed class TimelineCanvas : Control
{
    public const double HeaderWidth = 112;
    public const double RulerHeight = 22;
    private const double EdgeGrip = 5;

    private readonly EditorSession _session = Svc.Session;
    private double _ppf = 4;
    private double _scrollX, _scrollY;

    private enum Drag { None, Seek, Pending, Move, ResizeLeft, ResizeRight, Key, Rubber, EmptyPending }
    private List<string> _rubberBase = new();
    private Drag _drag;
    private Point _pressPoint;
    private TimelineHit _pressHit;
    private readonly List<TimelineObject> _originals = new();
    private Rect _rubber;
    private Typeface _typeface, _mono;

    /// <summary>スクロール位置や拡大率が変わった。</summary>
    public event Action? ViewChanged;
    public event Action<TimelineHit, PointerEventArgs>? MenuRequested;
    public event Action<TimelineObject>? ObjectActivated;
    public event Action<string>? ErrorRaised;

    public TimelineCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            bool files = e.Data.Contains(DataFormats.Files);
            e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
            ShowDropTarget(files ? e.GetPosition(this) : null);
            e.Handled = true;
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ShowDropTarget(null));
        AddHandler(DragDrop.DropEvent, OnDrop);
        _session.ProjectChanged += InvalidateVisual;
        _session.SelectionChanged += InvalidateVisual;
        _session.SceneChanged += () => { _scrollX = _scrollY = 0; ViewChanged?.Invoke(); InvalidateVisual(); };
        _session.FrameChanged += OnFrameChanged;
        Svc.Theme.Changed += () => { _brushes.Clear(); _pens.Clear(); _textCache.Clear(); InvalidateVisual(); };
        Svc.Waveforms.Updated += InvalidateVisual;
        Svc.Thumbnails.Updated += InvalidateVisual;
    }

    // ---------------- 表示範囲 ----------------

    public double LayerHeight => Math.Clamp(Svc.Settings.LayerHeight, 16, 48);

    public double PixelsPerFrame
    {
        get => _ppf;
        set { _ppf = Math.Clamp(value, 0.02, 40); ClampScroll(); ViewChanged?.Invoke(); InvalidateVisual(); }
    }

    public double ScrollX
    {
        get => _scrollX;
        set { _scrollX = value; ClampScroll(); ViewChanged?.Invoke(); InvalidateVisual(); }
    }

    public double ScrollY
    {
        get => _scrollY;
        set { _scrollY = value; ClampScroll(); ViewChanged?.Invoke(); InvalidateVisual(); }
    }

    public double ViewportWidth => Math.Max(0, Bounds.Width - HeaderWidth);
    public double ViewportHeight => Math.Max(0, Bounds.Height - RulerHeight);
    /// <summary>横スクロールできる全体の幅 (px)。末尾に余白を付ける。</summary>
    public double ExtentWidth => Math.Max(ViewportWidth, (_session.SceneLength + _session.Project.SecondsToFrames(10)) * _ppf + ViewportWidth * 0.5);
    public double ExtentHeight => Scene.LayerCount * LayerHeight;

    private void ClampScroll()
    {
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, ExtentHeight - ViewportHeight));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampScroll();
        ViewChanged?.Invoke();
    }

    public double FrameToX(double frame) => HeaderWidth + frame * _ppf - _scrollX;
    public int XToFrame(double x) => (int)Math.Floor((x - HeaderWidth + _scrollX) / _ppf);
    public double LayerToY(int layer) => RulerHeight + layer * LayerHeight - _scrollY;
    public int YToLayer(double y) => (int)Math.Floor((y - RulerHeight + _scrollY) / LayerHeight);

    private void OnFrameChanged()
    {
        // 再生中は現在位置が画面の外に出たらページ送りする
        if (Svc.Preview.IsPlaying && ViewportWidth > 0)
        {
            double x = FrameToX(_session.CurrentFrame);
            if (x > Bounds.Width - 8 || x < HeaderWidth)
            {
                _scrollX = Math.Max(0, _session.CurrentFrame * _ppf - ViewportWidth * 0.1);
                ClampScroll();
                ViewChanged?.Invoke();
                InvalidateVisual();
            }
        }
        // 現在位置が動いただけでは描き直さない (線は PlayheadOverlay が動かす)
    }

    /// <summary>現在位置が見えるようにスクロールする。</summary>
    public void EnsureFrameVisible(int frame)
    {
        double x = FrameToX(frame);
        if (x < HeaderWidth + 8 || x > Bounds.Width - 8)
            ScrollX = frame * _ppf - ViewportWidth / 2;
    }

    // ---------------- 描画 ----------------

    private readonly Dictionary<string, IBrush> _brushes = new();

    private readonly Dictionary<(string, double), IPen> _pens = new();

    private IPen P(string key, double thickness = 1)
    {
        if (!_pens.TryGetValue((key, thickness), out var p)) _pens[(key, thickness)] = p = new Pen(B(key), thickness);
        return p;
    }

    private IBrush B(string key)
    {
        if (!_brushes.TryGetValue(key, out var b)) _brushes[key] = b = key.StartsWith('#') ? new SolidColorBrush(Color.Parse(key)) : Svc.Brush(key);
        return b;
    }

    // 文字の組版は重いので、同じ文字・大きさ・色のものは使い回す
    private readonly Dictionary<(string, double, bool, IBrush), FormattedText> _textCache = new();

    private FormattedText Text(string s, IBrush brush, double size = 11, bool mono = false)
    {
        var key = (s, size, mono, brush);
        if (_textCache.TryGetValue(key, out var t)) return t;
        if (_textCache.Count > 800) _textCache.Clear();
        t = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, mono ? _mono : _typeface, size, brush);
        _textCache[key] = t;
        return t;
    }

    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush Highlight = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Colors.White, 0.18);
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush WaveBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Colors.White, 0.45);
    private static readonly IPen KeyLinePen = new Avalonia.Media.Immutable.ImmutablePen(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Colors.White, 0.7));
    private static readonly IPen WavePen = new Avalonia.Media.Immutable.ImmutablePen(WaveBrush);
    private static readonly Geometry Diamond = CreateDiamond();

    private static Geometry CreateDiamond()
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(0, -4), true);
            s.LineTo(new Point(4, 0)); s.LineTo(new Point(0, 4)); s.LineTo(new Point(-4, 0));
            s.EndFigure(true);
        }
        return g;
    }

    // 波形の図形は、表示範囲が変わらない限り作り直さない
    private readonly Dictionary<string, (string Signature, Geometry Geometry)> _waveCache = new();

    /// <summary>描き直した回数と、それにかかった合計時間 (計測用)。</summary>
    public int RenderCount { get; private set; }
    public double RenderMsTotal { get; private set; }

    public override void Render(DrawingContext ctx)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderCore(ctx);
        RenderCount++;
        RenderMsTotal += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void RenderCore(DrawingContext ctx)
    {
        if (_typeface == default)
        {
            _typeface = new Typeface(this.FindResource("UiFont") as FontFamily ?? FontFamily.Default);
            _mono = new Typeface(this.FindResource("MonoFont") as FontFamily ?? FontFamily.Default);
        }
        double W = Bounds.Width, H = Bounds.Height, lh = LayerHeight;
        var scene = _session.Scene;
        var content = new Rect(HeaderWidth, RulerHeight, ViewportWidth, ViewportHeight);
        var borderPen = new Pen(B("Border.Default"));

        ctx.FillRectangle(B("Bg.Base"), new Rect(0, 0, W, H));
        int firstLayer = Math.Max(0, YToLayer(RulerHeight)), lastLayer = Math.Min(Scene.LayerCount - 1, YToLayer(H));
        int firstFrame = Math.Max(0, XToFrame(HeaderWidth)), lastFrame = XToFrame(W) + 1;
        var (major, minor) = TickSteps();

        using (ctx.PushClip(content))
        {
            // レイヤー行
            for (int l = firstLayer; l <= lastLayer; l++)
            {
                double y = LayerToY(l);
                var info = scene.GetLayer(l);
                if (!info.Visible) ctx.FillRectangle(B("Timeline.RowHidden"), new Rect(HeaderWidth, y, ViewportWidth, lh));
                else if (l % 2 == 1) ctx.FillRectangle(B("Timeline.RowAlt"), new Rect(HeaderWidth, y, ViewportWidth, lh));
                ctx.DrawLine(borderPen, new Point(HeaderWidth, y + lh - 0.5), new Point(W, y + lh - 0.5));
            }
            // 縦のグリッド: 時間 (大目盛ごと) または BPM (拍・小節ごと)
            var gridPen = new Pen(B("Border.Default"), 1);
            var grid = _session.Project.Grid;
            if (grid.Mode == GridMode.Time)
            {
                for (long f = firstFrame / major * major; f <= lastFrame; f += major)
                {
                    double x = Math.Round(FrameToX(f)) + 0.5;
                    ctx.DrawLine(gridPen, new Point(x, RulerHeight), new Point(x, H));
                }
            }
            else if (grid.Mode == GridMode.Bpm && grid.Bpm > 0)
            {
                double fps = _session.Project.Settings.Fps.Value, beatFrames = 60.0 / grid.Bpm * fps, offset = grid.OffsetSeconds * fps;
                int beats = Math.Max(1, grid.Beats);
                bool showBeats = beatFrames * _ppf >= 6, showBars = beatFrames * beats * _ppf >= 6;
                var barPen = new Pen(B("Border.Strong"), 1);
                if (showBars && beatFrames > 0)
                {
                    long k0 = (long)Math.Ceiling((firstFrame - offset) / beatFrames);
                    for (long k = Math.Max(0, k0); ; k++)
                    {
                        double f = offset + k * beatFrames;
                        if (f > lastFrame) break;
                        bool bar = k % beats == 0;
                        if (!bar && !showBeats) continue;
                        double x = Math.Round(FrameToX(Math.Round(f))) + 0.5;
                        ctx.DrawLine(bar ? barPen : gridPen, new Point(x, RulerHeight), new Point(x, H));
                    }
                }
            }
            // 出力範囲の両端
            if (scene.RangeStart != null || scene.RangeEnd != null)
            {
                var (rs, re) = _session.EffectiveRange;
                var rangePen = new Pen(B("Accent"), 1) { DashStyle = DashStyle.Dash };
                foreach (double f in new double[] { rs, re + 1 })
                {
                    double x = Math.Round(FrameToX(f)) + 0.5;
                    ctx.DrawLine(rangePen, new Point(x, RulerHeight), new Point(x, H));
                }
            }
            // シーンの終端より後ろを少し暗くする
            double endX = FrameToX(scene.Length);
            if (scene.Length > 0 && endX < W)
                ctx.FillRectangle(new SolidColorBrush(Colors.Black, 0.12), new Rect(Math.Max(HeaderWidth, endX), RulerHeight, W - endX, ViewportHeight));

            // オブジェクト
            foreach (var o in scene.Objects)
            {
                if (o.Layer < firstLayer || o.Layer > lastLayer || o.End < firstFrame || o.Start > lastFrame) continue;
                DrawObject(ctx, o, lh);
            }
            if (_dropTarget is { } drop)
            {
                // ドロップ先: そのレイヤーの行を薄く塗り、置き始める位置に縦線を引く
                double dy = LayerToY(drop.Layer), dx = Math.Round(FrameToX(drop.Frame)) + 0.5;
                var accent = ((ISolidColorBrush)B("Accent")).Color;
                ctx.FillRectangle(new SolidColorBrush(accent, 0.18), new Rect(Math.Max(HeaderWidth, dx), dy, Math.Max(0, W - dx), lh));
                ctx.DrawLine(new Pen(B("Accent"), 2), new Point(dx, dy), new Point(dx, dy + lh));
            }
            if (_drag == Drag.Rubber)
                ctx.DrawRectangle(new SolidColorBrush(((ISolidColorBrush)B("Accent")).Color, 0.15), new Pen(B("Accent")), _rubber);
        }

        // ルーラー
        ctx.FillRectangle(B("Bg.Panel"), new Rect(0, 0, W, RulerHeight));
        using (ctx.PushClip(new Rect(HeaderWidth, 0, ViewportWidth, RulerHeight)))
        {
            var tickPen = new Pen(B("Border.Strong"));
            var fg = B("Fg.Secondary");
            for (long f = firstFrame / minor * minor; f <= lastFrame; f += minor)
            {
                double x = Math.Round(FrameToX(f)) + 0.5;
                bool isMajor = f % major == 0;
                ctx.DrawLine(tickPen, new Point(x, isMajor ? 4 : RulerHeight - 5), new Point(x, RulerHeight));
                if (isMajor) ctx.DrawText(Text(RulerLabel((int)f, major), fg, 10, true), new Point(x + 3, 3));
            }
        }
        if (scene.RangeStart != null || scene.RangeEnd != null)
        {
            // 出力範囲: ルーラーの下端に帯で示す
            var (rs, re) = _session.EffectiveRange;
            double x1 = Math.Max(HeaderWidth, FrameToX(rs)), x2 = Math.Min(W, FrameToX(re + 1));
            if (x2 > x1) ctx.FillRectangle(B("Accent"), new Rect(x1, RulerHeight - 4, x2 - x1, 3));
        }
        ctx.DrawLine(borderPen, new Point(0, RulerHeight - 0.5), new Point(W, RulerHeight - 0.5));

        // レイヤーヘッダ
        ctx.FillRectangle(B("Bg.Panel"), new Rect(0, RulerHeight, HeaderWidth, ViewportHeight));
        using (ctx.PushClip(new Rect(0, RulerHeight, HeaderWidth, ViewportHeight)))
        {
            for (int l = firstLayer; l <= lastLayer; l++)
            {
                double y = LayerToY(l);
                var info = scene.GetLayer(l);
                var fg = info.Visible ? B("Fg.Primary") : B("Fg.Disabled");
                var label = Text(string.IsNullOrEmpty(info.Name) ? L.T($"レイヤー {l + 1}") : info.Name, fg);
                ctx.DrawText(label, new Point(8, y + (lh - label.Height) / 2));
                double iy = y + (lh - 14) / 2;
                var eye = Icons.Get(info.Visible ? "Visibility" : "VisibilityOff");
                if (eye != null) Icon.Draw(ctx, eye, info.Visible ? B("Fg.Secondary") : B("Fg.Disabled"), new Point(HeaderWidth - 40, iy), 14);
                var lockIcon = Icons.Get(info.Locked ? "Lock.Fill" : "LockOpen");
                if (lockIcon != null) Icon.Draw(ctx, lockIcon, info.Locked ? B("Warning") : B("Fg.Disabled"), new Point(HeaderWidth - 20, iy), 14);
                ctx.DrawLine(borderPen, new Point(0, y + lh - 0.5), new Point(HeaderWidth, y + lh - 0.5));
            }
        }
        ctx.DrawLine(new Pen(B("Border.Strong")), new Point(HeaderWidth - 0.5, 0), new Point(HeaderWidth - 0.5, H));

        // 現在位置カーソルは PlayheadOverlay が描く (再生中にこのコントロール全体を描き直さないため)
    }

    private void DrawObject(DrawingContext ctx, TimelineObject o, double lh)
    {
        double x1 = Math.Round(FrameToX(o.Start)), x2 = Math.Round(FrameToX(o.End + 1));
        double y = LayerToY(o.Layer);
        var rect = new Rect(x1, y, Math.Max(2, x2 - x1), lh - 1);
        var def = o.Definition;
        bool selected = _session.IsSelected(o.Id);

        using (ctx.PushOpacity(o.Enabled ? 1 : 0.45))
        {
            ctx.FillRectangle(B(def.Color), rect);
            ctx.FillRectangle(Highlight, new Rect(rect.X, rect.Y, rect.Width, 1));   // 上端のハイライト
            if (rect.Width > 8 && def.Id == EffectRegistry.Audio && Svc.Settings.ShowWaveform) DrawWaveform(ctx, o, rect);
            if (rect.Width > 8)
            {
                using (ctx.PushClip(rect.Deflate(new Thickness(2, 0))))
                {
                    double tx = Math.Max(rect.X, HeaderWidth) + 4;
                    if (rect.Width > 60 && def.Id is EffectRegistry.Video or EffectRegistry.Image && Svc.Settings.ShowThumbnails)
                    {
                        // 先頭のサムネイル (できあがるまでは出さない)
                        double t = def.Id == EffectRegistry.Video ? VideoBuilder.Rendering.RenderEngine.SourceTime(_session.Project, o, o.Bounds, o.Start) : 0;
                        var thumb = Svc.Thumbnails.Get(o.Main.GetString("file"), t, def.Id == EffectRegistry.Video);
                        if (thumb != null)
                        {
                            double th = lh - 3, tw = Math.Round(thumb.Size.Width * th / thumb.Size.Height);
                            ctx.DrawImage(thumb, new Rect(rect.X + 1, y + 1, tw, th));
                            tx = Math.Max(tx, rect.X + tw + 5);
                        }
                    }
                    var icon = Icons.Get(def.Icon);
                    if (icon != null && rect.Width > 22)
                    {
                        Icon.Draw(ctx, icon, Brushes.White, new Point(tx, y + (lh - 15) / 2), 14);
                        tx += 18;
                    }
                    var label = Text(Labels.Object(o), Brushes.White);
                    ctx.DrawText(label, new Point(tx, y + (lh - 1 - label.Height) / 2));
                }
            }
            // 中間点: 縦線 + ひし形
            var diamondPen = P("Timeline.ObjectBorder");
            foreach (var k in o.KeyFrames)
            {
                double kx = Math.Round(FrameToX(k)) + 0.5, cy = y + (lh - 1) / 2;
                ctx.DrawLine(KeyLinePen, new Point(kx, y + 1), new Point(kx, y + lh - 2));
                using (ctx.PushTransform(Matrix.CreateTranslation(kx, cy)))
                    ctx.DrawGeometry(Brushes.White, diamondPen, Diamond);
            }
        }
        if (selected) ctx.DrawRectangle(null, P("Accent", 2), rect.Deflate(1));
        else ctx.DrawRectangle(null, P("Timeline.ObjectBorder"), rect.Deflate(0.5));
    }

    /// <summary>音声オブジェクトの中に、ピーク値の波形を描く。</summary>
    private void DrawWaveform(DrawingContext ctx, TimelineObject o, Rect rect)
    {
        var peaks = Svc.Waveforms.Get(o.Main.GetString("file"));
        if (peaks == null || peaks.Values.Length == 0) return;
        var project = _session.Project;
        var bounds = o.Bounds;
        double speed = o.Main.Eval("speed", bounds, o.Start) / 100.0;
        double offset = o.Main.Eval("position", bounds, o.Start) + project.FrameToSeconds(o.SourceOffsetFrames);
        bool loop = o.Main.GetBool("loop");
        double fps = project.Settings.Fps.Value;
        double x1 = Math.Max(rect.X, HeaderWidth), x2 = Math.Min(rect.Right, Bounds.Width);
        double mid = rect.Y + rect.Height / 2, half = rect.Height / 2 - 1;
        string signature = FormattableString.Invariant($"{x1}|{x2}|{rect.Y}|{rect.Height}|{_scrollX}|{_ppf}|{o.Start}|{speed}|{offset}|{loop}|{fps}|{peaks.Values.Length}");
        if (_waveCache.TryGetValue(o.Id, out var cached) && cached.Signature == signature)
        {
            ctx.DrawGeometry(null, WavePen, cached.Geometry);
            return;
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (double x = Math.Floor(x1); x < x2; x += 1)
            {
                // この 1px が表す時間の範囲で、いちばん大きい値を使う
                double f0 = (x - HeaderWidth + _scrollX) / _ppf - o.Start, f1 = f0 + 1 / _ppf;
                double t0 = offset + f0 / fps * speed, t1 = offset + f1 / fps * speed;
                if (loop && peaks.Duration > 0) { double d = t1 - t0; t0 %= peaks.Duration; t1 = t0 + d; }
                int i0 = (int)(t0 * WaveformCache.PeaksPerSecond), i1 = Math.Max(i0, (int)(t1 * WaveformCache.PeaksPerSecond));
                if (i0 < 0 || i0 >= peaks.Values.Length) continue;
                int max = 0;
                for (int i = i0; i <= Math.Min(i1, peaks.Values.Length - 1); i++) max = Math.Max(max, peaks.Values[i]);
                double hgt = Math.Max(0.5, max / 255.0 * half);
                g.BeginFigure(new Point(x + 0.5, mid - hgt), false);
                g.LineTo(new Point(x + 0.5, mid + hgt));
            }
        }
        if (_waveCache.Count > 200) _waveCache.Clear();
        _waveCache[o.Id] = (signature, geo);
        ctx.DrawGeometry(null, WavePen, geo);
    }

    /// <summary>オブジェクトの帯に出す名前 (表示言語に合わせる)。</summary>
    /// <summary>大目盛が 90px 以上になる間隔を選ぶ。</summary>
    private (long Major, long Minor) TickSteps()
    {
        double fps = Math.Max(1, _session.Project.Settings.Fps.Value);
        long sec = (long)Math.Round(fps);
        long[] steps = { 1, 2, 5, 10, sec, sec * 2, sec * 5, sec * 10, sec * 30, sec * 60, sec * 120, sec * 300, sec * 600, sec * 1800, sec * 3600 };
        long major = steps[^1];
        foreach (var s in steps) { if (s * _ppf >= 90) { major = s; break; } }
        long minor = major >= sec * 60 ? major / 6 : major >= sec ? Math.Max(1, major / 5) : Math.Max(1, major / (major % 5 == 0 ? 5 : 2));
        if (major % minor != 0) minor = major;
        return (Math.Max(1, major), Math.Max(1, minor));
    }

    private string RulerLabel(int frame, long major)
    {
        var p = _session.Project;
        double fps = Math.Max(1, p.Settings.Fps.Value);
        if (major < Math.Round(fps)) return p.FormatTime(frame);
        double sec = p.FrameToSeconds(frame);
        int total = (int)Math.Round(sec);
        return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60:00}:{total % 60:00}";
    }

    // ---------------- 当たり判定 ----------------

    public TimelineHit HitTest(Point p)
    {
        int frame = Math.Max(0, XToFrame(p.X)), layer = Math.Clamp(YToLayer(p.Y), 0, Scene.LayerCount - 1);
        if (p.Y < RulerHeight) return new TimelineHit(p.X < HeaderWidth ? TimelineArea.Corner : TimelineArea.Ruler, layer, frame, null, ObjectPart.None, -1);
        if (p.X < HeaderWidth) return new TimelineHit(TimelineArea.Header, layer, frame, null, ObjectPart.None, -1);
        foreach (var o in _session.Scene.Objects)
        {
            if (o.Layer != layer) continue;
            double x1 = FrameToX(o.Start), x2 = FrameToX(o.End + 1);
            if (p.X < x1 || p.X >= Math.Max(x2, x1 + 2)) continue;
            for (int i = 0; i < o.KeyFrames.Count; i++)
                if (Math.Abs(FrameToX(o.KeyFrames[i]) - p.X) <= 4) return new TimelineHit(TimelineArea.Content, layer, frame, o, ObjectPart.KeyFrame, i);
            double grip = Math.Min(EdgeGrip, (x2 - x1) / 3);
            var part = p.X - x1 < grip ? ObjectPart.LeftEdge : x2 - p.X <= grip ? ObjectPart.RightEdge : ObjectPart.Body;
            return new TimelineHit(TimelineArea.Content, layer, frame, o, part, -1);
        }
        return new TimelineHit(TimelineArea.Content, layer, frame, null, ObjectPart.None, -1);
    }

    // ---------------- マウス操作 ----------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pos = e.GetPosition(this);
        var hit = HitTest(pos);
        var props = e.GetCurrentPoint(this).Properties;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (props.IsRightButtonPressed)
        {
            if (hit.Object != null && !_session.IsSelected(hit.Object.Id) && !IsLocked(hit.Object)) _session.Select(hit.Object.Id);
            MenuRequested?.Invoke(hit, e);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        _pressPoint = pos; _pressHit = hit;

        switch (hit.Area)
        {
            case TimelineArea.Ruler:
                _drag = Drag.Seek;
                _session.CurrentFrame = hit.Frame;
                break;
            case TimelineArea.Header:
            {
                // 目のアイコンで表示、鍵のアイコンでロックを切り替える。名前の部分は、そのレイヤーのオブジェクトを選ぶ
                // (以前は名前の部分でも表示が切り替わり、うっかりレイヤーを隠してしまいやすかった)
                var info = _session.Scene.GetLayer(hit.Layer);
                if (pos.X >= HeaderWidth - 22) _session.SetLayerLocked(hit.Layer, !info.Locked);
                else if (pos.X >= HeaderWidth - 44) _session.SetLayerVisible(hit.Layer, !info.Visible);
                else if (!info.Locked)
                {
                    var ids = _session.Scene.Objects.Where(o => o.Layer == hit.Layer).Select(o => o.Id);
                    _session.SelectMany(ctrl ? _session.SelectionIds.Concat(ids) : ids);
                }
                break;
            }
            case TimelineArea.Content when hit.Object != null:
            {
                var o = hit.Object;
                if (IsLocked(o)) break;
                if (e.ClickCount == 2) { _session.Select(o.Id); ObjectActivated?.Invoke(o); break; }
                if (ctrl) _session.Select(o.Id, additive: true);
                else if (!_session.IsSelected(o.Id)) _session.Select(o.Id);
                else if (_session.SelectionIds.Count > 1) MakePrimary(o.Id);
                if (_session.IsSelected(o.Id)) _drag = Drag.Pending;
                break;
            }
            case TimelineArea.Content:
                // 何もない所: ドラッグすると範囲選択 (Ctrl で今の選択に追加)、クリックだけなら選択を外して現在位置を移す
                _rubberBase = ctrl ? _session.SelectionIds.ToList() : new List<string>();
                _drag = Drag.EmptyPending;
                break;
        }
        if (_drag != Drag.None) e.Pointer.Capture(this);
    }

    /// <summary>複数選択中のオブジェクトを押したときは、選択を保ったまま主選択だけ切り替える。</summary>
    private void MakePrimary(string id)
    {
        var ids = _session.SelectionIds.Where(x => x != id).Append(id).ToList();
        _session.SelectMany(ids);
    }

    private bool IsLocked(TimelineObject o) => _session.Scene.GetLayer(o.Layer).Locked;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            var h = HitTest(pos);
            Cursor = h.Part is ObjectPart.LeftEdge or ObjectPart.RightEdge or ObjectPart.KeyFrame && h.Object != null && !IsLocked(h.Object)
                ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
            return;
        }
        bool noSnap = e.KeyModifiers.HasFlag(KeyModifiers.Alt) || !Svc.Settings.Snap;

        switch (_drag)
        {
            case Drag.Seek:
                _session.CurrentFrame = Math.Max(0, XToFrame(pos.X));
                AutoScroll(pos);
                break;
            case Drag.Pending:
                if (Math.Abs(pos.X - _pressPoint.X) < 3 && Math.Abs(pos.Y - _pressPoint.Y) < 3) break;
                BeginObjectDrag();
                goto default;
            case Drag.EmptyPending:
                if (Math.Abs(pos.X - _pressPoint.X) < 4 && Math.Abs(pos.Y - _pressPoint.Y) < 4) break;
                _drag = Drag.Rubber;
                goto case Drag.Rubber;
            case Drag.Rubber:
                _rubber = new Rect(_pressPoint, pos).Normalize();
                SelectInRubber();
                InvalidateVisual();
                break;
            default:
                UpdateObjectDrag(pos, noSnap);
                AutoScroll(pos);
                break;
        }
    }

    private void BeginObjectDrag()
    {
        var o = _pressHit.Object!;
        string description;
        IEnumerable<TimelineObject> targets;
        switch (_pressHit.Part)
        {
            case ObjectPart.LeftEdge: _drag = Drag.ResizeLeft; description = "長さの変更"; targets = _session.WithGroup(new[] { o }); break;
            case ObjectPart.RightEdge: _drag = Drag.ResizeRight; description = "長さの変更"; targets = _session.WithGroup(new[] { o }); break;
            case ObjectPart.KeyFrame: _drag = Drag.Key; description = "キーフレームの移動"; targets = new[] { o }; break;
            default: _drag = Drag.Move; description = "オブジェクトの移動"; targets = _session.WithGroup(_session.SelectedObjects.ToList()); break;
        }
        _originals.Clear();
        _originals.AddRange(targets.Where(t => !IsLocked(t)).Select(t => t.Clone()));
        _session.BeginEdit(description);
    }

    private void UpdateObjectDrag(Point pos, bool noSnap)
    {
        var scene = _session.Scene;
        var ids = _originals.Select(o => o.Id).ToHashSet();
        int dFrames = (int)Math.Round((pos.X - _pressPoint.X) / _ppf);
        int snapRange = Math.Max(1, (int)Math.Round(8 / _ppf));

        // 一度すべて元に戻してから、今回の移動量を当て直す
        var live = new List<(TimelineObject Live, TimelineObject Orig)>();
        foreach (var orig in _originals)
        {
            var l = scene.Find(orig.Id);
            if (l == null) continue;
            l.RestoreFrom(orig);
            live.Add((l, orig));
        }
        if (live.Count == 0) return;

        switch (_drag)
        {
            case Drag.Move:
            {
                int dLayers = YToLayer(pos.Y) - _pressHit.Layer;
                dFrames = Math.Max(dFrames, -_originals.Min(o => o.Start));
                dLayers = Math.Clamp(dLayers, -_originals.Min(o => o.Layer), Scene.LayerCount - 1 - _originals.Max(o => o.Layer));
                if (!noSnap) dFrames += SnapDelta(_originals.SelectMany(o => new[] { o.Start + dFrames, o.End + 1 + dFrames }), ids, snapRange);
                dFrames = Math.Max(dFrames, -_originals.Min(o => o.Start));
                // 置けない場所なら、近い移動量から順に空きを探す
                var found = FindFreeDelta(dFrames, dLayers, ids);
                if (found == null) break;
                foreach (var (l, orig) in live)
                {
                    l.Layer = orig.Layer + dLayers;
                    l.SetRange(orig.Start + found.Value, orig.End + found.Value);
                }
                break;
            }
            case Drag.ResizeLeft:
            {
                foreach (var (l, orig) in live)
                {
                    int start = orig.Start + dFrames;
                    if (!noSnap) start += SnapDelta(new[] { start }, ids, snapRange);
                    int min = 0;
                    foreach (var other in scene.Objects)
                        if (other.Layer == orig.Layer && !ids.Contains(other.Id) && other.End < orig.Start) min = Math.Max(min, other.End + 1);
                    start = Math.Clamp(start, min, orig.End);
                    l.SetRange(start, orig.End);
                    // 動画・音声は、左端を縮めても絵と音の位置がずれないようにする
                    if (l.Main.DefinitionId is EffectRegistry.Video or EffectRegistry.Audio or EffectRegistry.SceneRef)
                        l.SourceOffsetFrames = orig.SourceOffsetFrames + (start - orig.Start);
                }
                break;
            }
            case Drag.ResizeRight:
            {
                foreach (var (l, orig) in live)
                {
                    int end = orig.End + dFrames;
                    if (!noSnap) end += SnapDelta(new[] { end + 1 }, ids, snapRange);
                    int max = int.MaxValue - 1;
                    foreach (var other in scene.Objects)
                        if (other.Layer == orig.Layer && !ids.Contains(other.Id) && other.Start > orig.End) max = Math.Min(max, other.Start - 1);
                    end = Math.Clamp(end, orig.Start, max);
                    l.SetRange(orig.Start, end);
                }
                break;
            }
            case Drag.Key:
            {
                var (l, orig) = live[0];
                int i = _pressHit.KeyIndex;
                if (i < 0 || i >= orig.KeyFrames.Count) break;
                int k = orig.KeyFrames[i] + dFrames;
                if (!noSnap) k += SnapDelta(new[] { k }, ids, snapRange);
                int lo = (i == 0 ? orig.Start : orig.KeyFrames[i - 1]) + 1;
                int hi = i == orig.KeyFrames.Count - 1 ? orig.End : orig.KeyFrames[i + 1] - 1;
                if (lo > hi) break;
                l.KeyFrames[i] = Math.Clamp(k, lo, hi);
                break;
            }
        }
        _session.NotifyChanged();
    }

    private int? FindFreeDelta(int dFrames, int dLayers, HashSet<string> ids)
    {
        var scene = _session.Scene;
        bool Free(int d) => d >= -_originals.Min(o => o.Start) && _originals.All(o =>
            !scene.GetLayer(o.Layer + dLayers).Locked && scene.IsFree(o.Layer + dLayers, o.Start + d, o.End + d, ids));
        if (Free(dFrames)) return dFrames;
        // 重なる相手の前後にぴったり付く位置を候補にする
        var candidates = new SortedSet<int>();
        foreach (var o in _originals)
        foreach (var other in scene.Objects)
        {
            if (ids.Contains(other.Id) || other.Layer != o.Layer + dLayers) continue;
            candidates.Add(other.End + 1 - o.Start);
            candidates.Add(other.Start - 1 - o.End);
        }
        foreach (var d in candidates.OrderBy(c => Math.Abs(c - dFrames)))
        {
            if (Math.Abs(d - dFrames) * _ppf > 48) break;
            if (Free(d)) return d;
        }
        return null;
    }

    /// <summary>edges のどれかを、他のオブジェクトの端・現在位置に合わせるための補正量。</summary>
    private int SnapDelta(IEnumerable<int> edges, HashSet<string> ignore, int range)
    {
        var points = new List<int> { 0, _session.CurrentFrame };
        var grid = _session.Project.Grid;
        if (grid.Mode == GridMode.Bpm && grid.Bpm > 0)
        {
            // BPM グリッド表示中は、いちばん近い拍にも合わせる
            double fps = _session.Project.Settings.Fps.Value, beat = 60.0 / grid.Bpm * fps, off = grid.OffsetSeconds * fps;
            foreach (var edge in edges)
            {
                double k = Math.Round((edge - off) / beat);
                if (k >= 0) points.Add((int)Math.Round(off + k * beat));
            }
        }
        foreach (var o in _session.Scene.Objects)
        {
            if (ignore.Contains(o.Id)) continue;
            points.Add(o.Start); points.Add(o.End + 1);
        }
        int best = 0, bestAbs = range + 1;
        foreach (var e in edges)
        foreach (var p in points)
        {
            int d = p - e;
            if (Math.Abs(d) < bestAbs) { bestAbs = Math.Abs(d); best = d; }
        }
        return bestAbs <= range ? best : 0;
    }

    private void SelectInRubber()
    {
        int f1 = XToFrame(_rubber.Left), f2 = XToFrame(_rubber.Right);
        int l1 = YToLayer(_rubber.Top), l2 = YToLayer(_rubber.Bottom);
        _session.SelectMany(_rubberBase.Concat(_session.Scene.Objects
            .Where(o => o.Layer >= l1 && o.Layer <= l2 && o.Start <= f2 && o.End >= f1 && !IsLocked(o)).Select(o => o.Id)));
    }

    private void AutoScroll(Point pos)
    {
        if (pos.X > Bounds.Width - 12) ScrollX += 16;
        else if (pos.X < HeaderWidth + 12 && _scrollX > 0) ScrollX -= 16;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is Drag.Move or Drag.ResizeLeft or Drag.ResizeRight or Drag.Key) _session.CommitEdit();
        // 何もない所をクリックしただけ (ドラッグしていない): 選択を外して、現在位置をそこへ移す
        if (_drag == Drag.EmptyPending)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) _session.Select(null);
            _session.CurrentFrame = Math.Max(0, XToFrame(_pressPoint.X));
        }
        // 複数選択したオブジェクトをドラッグせずに離したら、そのオブジェクトだけの選択にする
        if (_drag == Drag.Pending && _pressHit.Object != null && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && _session.SelectionIds.Count > 1)
            _session.Select(_pressHit.Object.Id);
        _drag = Drag.None;
        _originals.Clear();
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is Drag.Move or Drag.ResizeLeft or Drag.ResizeRight or Drag.Key) _session.CommitEdit();
        _drag = Drag.None;
        _originals.Clear();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var pos = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // カーソル位置のフレームを動かさずに拡大縮小する
            double frame = (pos.X - HeaderWidth + _scrollX) / _ppf;
            _ppf = Math.Clamp(_ppf * (e.Delta.Y > 0 ? 1.25 : 0.8), 0.02, 40);
            _scrollX = frame * _ppf - (pos.X - HeaderWidth);
            ClampScroll();
            ViewChanged?.Invoke();
            InvalidateVisual();
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
        {
            double d = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            ScrollX -= d * 80;
        }
        else ScrollY -= e.Delta.Y * LayerHeight * 2;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        ShowDropTarget(null);
        if (files == null) return;
        DropFiles(files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList(), e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>ドロップ先として示している場所 (レイヤー, フレーム)。ドラッグ中だけ。</summary>
    private (int Layer, int Frame)? _dropTarget;

    /// <summary>位置 (このコントロール内の座標) に対応するレイヤーとフレーム。ルーラーやレイヤー名の上なら端に寄せる。</summary>
    private (int Layer, int Frame) DropCell(Point pos) =>
        (Math.Clamp(YToLayer(Math.Max(pos.Y, RulerHeight)), 0, Scene.LayerCount - 1), Math.Max(0, XToFrame(Math.Max(pos.X, HeaderWidth))));

    /// <summary>ドラッグ中に、ドロップされる場所を示す (null で消す)。</summary>
    public void ShowDropTarget(Point? pos)
    {
        (int, int)? cell = pos is { } p ? DropCell(p) : null;
        if (cell == _dropTarget) return;
        _dropTarget = cell;
        InvalidateVisual();
    }

    /// <summary>
    /// ファイルをタイムラインに置く。pos (このコントロール内の座標) のレイヤー・フレームから置き始め、
    /// 2 つ目以降は下のレイヤーへ順に置く。pos が null なら現在位置に置く。
    /// </summary>
    public void DropFiles(IReadOnlyList<string> paths, Point? pos)
    {
        var (layer, frame) = pos is { } p ? DropCell(p) : (0, _session.CurrentFrame);
        var errors = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { errors.Add($"フォルダは置けません: {L.Name(Path.GetFileName(path))}"); continue; }
            var error = MediaImport.AddFile(path, layer, frame);
            if (error != null) { errors.Add(error); continue; }
            var added = _session.Primary;
            if (added != null) layer = Math.Min(Scene.LayerCount - 1, added.Layer + (added.GroupId != null ? 2 : 1));
        }
        if (errors.Count > 0) ErrorRaised?.Invoke(string.Join("\n", errors));
        Focus();
    }
}
