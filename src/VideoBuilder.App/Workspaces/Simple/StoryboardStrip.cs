using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Workspaces.Simple;

/// <summary>
/// ストーリーボード: クリップを長さに比例した幅で 1 列に並べ、その下に音楽を並べる。
/// クリックで選択と現在位置の移動、ドラッグでクリップの並べ替えと音楽の位置の変更、ファイルのドロップで追加。
/// </summary>
public sealed class StoryboardStrip : Control
{
    public const double RulerHeight = 22, ClipTop = 28, ClipHeight = 88, MusicGap = 8, MusicHeight = 30, Pad = 12;
    private static double MusicTop => ClipTop + ClipHeight + MusicGap;

    private readonly StoryboardEditor _editor = Svc.Storyboard;
    private readonly EditorSession _session = Svc.Session;
    private double _pps = 40;   // 1 秒あたりのピクセル
    private double _scrollX;

    public event Action? ViewChanged;
    /// <summary>右クリック。対象 (クリップ・音楽の ID。何もない所なら null) と、その位置のフレーム。</summary>
    public event Action<string?, int>? MenuRequested;
    /// <summary>ファイルが落とされた (並びの何番目に入れるか、落とした位置の時刻 (秒、音楽を鳴らし始める所))。</summary>
    public event Action<IReadOnlyList<string>, int, double>? FilesDropped;

    public StoryboardStrip()
    {
        ClipToBounds = true;
        Focusable = true;
        MinHeight = MusicTop + MusicHeight + 10;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            bool files = e.Data.Contains(DataFormats.Files);
            e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
            ShowDropTarget(files ? e.GetPosition(this) : null);
            e.Handled = true;
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ShowDropTarget(null));
        _session.ProjectChanged += SimpleMedia.ForgetExists;
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
            var pos = e.GetPosition(this);
            var at = InsertIndexAt(pos.X);
            ShowDropTarget(null);
            if (files is { Count: > 0 }) FilesDropped?.Invoke(files, at, SecondsAt(pos.X));
            e.Handled = true;
        });
        _session.ProjectChanged += Changed;
        _session.FrameChanged += InvalidateVisual;
        _editor.SelectionChanged += InvalidateVisual;
        Svc.Theme.Changed += InvalidateVisual;
        Svc.Thumbnails.Updated += InvalidateVisual;
        Svc.Waveforms.Updated += InvalidateVisual;
    }

    private void Changed()
    {
        ClampScroll();
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    // ---------------- 表示範囲 ----------------

    private Project Project => _session.Project;
    private double Fps => Math.Max(1, Project.Settings.Fps.Value);

    public double PixelsPerSecond
    {
        get => _pps;
        set { _pps = Math.Clamp(value, 4, 600); ClampScroll(); ViewChanged?.Invoke(); InvalidateVisual(); }
    }

    public double ScrollX
    {
        get => _scrollX;
        set { _scrollX = value; ClampScroll(); ViewChanged?.Invoke(); InvalidateVisual(); }
    }

    public double ExtentWidth => Pad * 2 + Math.Max(_editor.Layout.Length, 1) / Fps * _pps + 200;
    public double ViewportWidth => Bounds.Width;

    private void ClampScroll() => _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, ExtentWidth - ViewportWidth));

    public double FrameToX(double frame) => Pad + frame / Fps * _pps - _scrollX;
    /// <summary>x の位置の時刻 (秒、0 より前にはならない)。</summary>
    public double SecondsAt(double x) => Math.Max(0, (x - Pad + _scrollX) / _pps);
    public int XToFrame(double x) => Math.Max(0, (int)Math.Floor((x - Pad + _scrollX) / _pps * Fps));

    /// <summary>作品全体 (クリップと音楽) が横幅に収まる倍率にして、先頭から表示する。</summary>
    public void ZoomToFit()
    {
        double width = Bounds.Width - Pad * 2;
        if (width <= 0) return;
        double seconds = Math.Max(_editor.Layout.Length, 1) / Fps;
        _pps = Math.Clamp(width / seconds, 4, 600);
        _scrollX = 0;
        ClampScroll();
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void EnsureFrameVisible(int frame)
    {
        double x = FrameToX(frame);
        if (x < Pad || x > Bounds.Width - Pad) ScrollX = frame / Fps * _pps - ViewportWidth / 2;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampScroll();
        ViewChanged?.Invoke();
    }

    // ---------------- 当たり判定 ----------------

    /// <summary>クリップの、画面に見えている範囲 (次のクリップと重なる所は次のクリップが上)。</summary>
    private Rect ClipRect(StoryboardLayout layout, ClipSpan span)
    {
        double x0 = FrameToX(span.Start), x1 = FrameToX(span.End + 1);
        return new Rect(x0, ClipTop, Math.Max(2, x1 - x0), ClipHeight);
    }

    private Rect MusicRect(MusicSpan span) =>
        new(FrameToX(span.Start), MusicTop, Math.Max(2, FrameToX(span.End + 1) - FrameToX(span.Start)), MusicHeight);

    /// <summary>位置にあるクリップか音楽の ID。重なっている所は後ろ (手前に描いた) のクリップ。</summary>
    public string? HitTest(Point p)
    {
        var layout = _editor.Layout;
        if (p.Y >= MusicTop && p.Y <= MusicTop + MusicHeight)
            return layout.Music.LastOrDefault(m => MusicRect(m).Contains(p))?.Music.Id;
        if (p.Y >= ClipTop && p.Y <= ClipTop + ClipHeight)
            return layout.Clips.LastOrDefault(c => ClipRect(layout, c).Contains(p))?.Clip.Id;
        return null;
    }

    /// <summary>クリップの見えている所 (次のクリップと重なっている所は次のクリップの下なので除く)。</summary>
    private Rect VisibleClipRect(StoryboardLayout layout, ClipSpan c)
    {
        var r = ClipRect(layout, c);
        return r.WithWidth(Math.Max(2, Math.Min(r.Right, FrameToX(c.End + 1 - c.FadeOut)) - r.X));
    }

    /// <summary>端をドラッグして長さを変えられる幅 (ピクセル)。</summary>
    private const double EdgeGrip = 6;

    /// <summary>位置が、クリップか音楽の端 (長さを変えるつまみ) か。繰り返す音楽は端で長さを変えない。</summary>
    private (string Id, bool Music, bool Start)? EdgeAt(Point p)
    {
        var layout = _editor.Layout;
        if (p.Y >= MusicTop && p.Y <= MusicTop + MusicHeight)
        {
            // 境目では、ポインタの下にある方の端を使う (なければ、少し外れていても近い方)
            var m = layout.Music.LastOrDefault(x => MusicRect(x).Contains(p))
                    ?? layout.Music.LastOrDefault(x => MusicRect(x).Inflate(new Thickness(EdgeGrip, 0)).Contains(p));
            if (m == null || m.Music.Loop) return null;
            return Edge(MusicRect(m), m.Music.Id, true);
        }
        if (p.Y >= ClipTop && p.Y <= ClipTop + ClipHeight)
        {
            var c = layout.Clips.LastOrDefault(x => VisibleClipRect(layout, x).Contains(p))
                    ?? layout.Clips.LastOrDefault(x => VisibleClipRect(layout, x).Inflate(new Thickness(EdgeGrip, 0)).Contains(p));
            if (c == null) return null;
            return Edge(VisibleClipRect(layout, c), c.Clip.Id, false);
        }
        return null;

        (string, bool, bool)? Edge(Rect r, string id, bool music)
        {
            if (r.Width < EdgeGrip * 3) return null;   // 細すぎるものは、選びやすさを優先する
            if (Math.Abs(p.X - r.Left) <= EdgeGrip) return (id, music, true);
            if (Math.Abs(p.X - r.Right) <= EdgeGrip) return (id, music, false);
            return null;
        }
    }

    /// <summary>x の位置にドロップしたとき、並びの何番目に入るか (クリップの真ん中より左なら、その前)。</summary>
    public int InsertIndexAt(double x)
    {
        var layout = _editor.Layout;
        for (int i = 0; i < layout.Clips.Count; i++)
        {
            var c = layout.Clips[i];
            if (x < (FrameToX(c.Start) + FrameToX(c.End + 1)) / 2) return i;
        }
        return layout.Clips.Count;
    }

    // ---------------- マウス ----------------

    private enum DragKind { None, Seek, Clip, Music, Resize }
    private DragKind _drag;
    // 端のドラッグ: 対象、始まりの端か、ドラッグ前の設定、1 回のドラッグを 1 回の取り消しにまとめる印
    private bool _resizeStart, _resizeMusic;
    private StoryClip? _resizeClip;
    private MusicClip? _resizeMusicOriginal;
    private string? _resizeKey;
    private int _resizeSerial;
    private string? _dragId;
    private Point _pressPoint;
    private bool _dragging;
    private int? _dropIndex;
    private double _musicDelta;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        string? hit = HitTest(p);
        int frame = XToFrame(p.X);
        if (props.IsRightButtonPressed)
        {
            // いくつも選んでいるものの 1 つなら、選択はそのまま (まとめて削除などできるように)
            if (hit != null && !_editor.IsSelected(hit)) _editor.Select(hit);
            MenuRequested?.Invoke(hit, frame);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        _pressPoint = p;
        _dragging = false;
        Svc.Preview.Stop();
        var mods = e.KeyModifiers;
        if (hit != null && p.Y >= RulerHeight && (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Shift)))
        {
            // Ctrl: 選択に加える / 外す、Shift: 範囲で選ぶ (ドラッグはしない)
            if (mods.HasFlag(KeyModifiers.Shift)) _editor.SelectRange(hit); else _editor.ToggleSelect(hit);
            e.Handled = true;
            return;
        }
        if (p.Y >= RulerHeight && _editor.CanEdit && EdgeAt(p) is { } edge)
        {
            _editor.Select(edge.Id);
            _drag = DragKind.Resize;
            _dragId = edge.Id;
            _resizeStart = edge.Start;
            _resizeMusic = edge.Music;
            _resizeClip = edge.Music ? null : _editor.Board?.FindClip(edge.Id)?.Clone();
            _resizeMusicOriginal = edge.Music ? _editor.Board?.FindMusic(edge.Id)?.Clone() : null;
            _resizeKey = $"resize:{edge.Id}:{++_resizeSerial}";
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (p.Y < RulerHeight || hit == null)
        {
            if (p.Y >= RulerHeight) _editor.Select(null);
            _drag = DragKind.Seek;
            Seek(frame);
        }
        else
        {
            _editor.Select(hit);
            _dragId = hit;
            _drag = _editor.Board?.FindMusic(hit) != null ? DragKind.Music : DragKind.Clip;
            Seek(frame);
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void Seek(int frame) => _session.CurrentFrame = Math.Min(frame, Math.Max(0, _session.SceneLength - 1));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag == DragKind.None)
        {
            // 端の上では、長さを変えられることをカーソルで示す
            bool edge = p.Y >= RulerHeight && _editor.CanEdit && EdgeAt(p) != null;
            Cursor = edge ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
            return;
        }
        if (_drag == DragKind.Seek) { Seek(XToFrame(p.X)); return; }
        if (_drag == DragKind.Resize) { Resize(p); return; }
        if (!_dragging && Math.Abs(p.X - _pressPoint.X) < 5) return;
        _dragging = true;
        if (_drag == DragKind.Clip) _dropIndex = InsertIndexAt(p.X);
        else _musicDelta = SnapMusic((p.X - _pressPoint.X) / _pps, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragKind.None) return;
        e.Pointer.Capture(null);
        var kind = _drag;
        _drag = DragKind.None;
        if (kind == DragKind.Resize)
        {
            _resizeClip = null; _resizeMusicOriginal = null; _resizeKey = null; _dragId = null; _dragging = false;
            InvalidateVisual();
            return;
        }
        if (_dragging && _dragId != null)
        {
            if (kind == DragKind.Clip && _dropIndex is { } at && _editor.Board is { } board)
            {
                int from = board.Clips.FindIndex(c => c.Id == _dragId);
                // 自分より後ろに入れるときは、自分が抜けたぶん 1 つ前になる
                if (from >= 0) _editor.Move(_dragId, at > from ? at - 1 : at);
            }
            else if (kind == DragKind.Music && Math.Abs(_musicDelta) > 0)
            {
                double delta = _musicDelta;
                _editor.UpdateMusic(_dragId, "音楽の移動", m => m.StartSeconds = Math.Max(0, Math.Round(m.StartSeconds + delta, 3)));
            }
        }
        _dragging = false; _dropIndex = null; _musicDelta = 0; _dragId = null; _snapSeconds = null;
        InvalidateVisual();
    }

    /// <summary>端のドラッグ中: ドラッグ前の設定から、動かした分だけ長さを変える (1 回の取り消しにまとまる)。</summary>
    private void Resize(Point p)
    {
        if (_dragId == null) return;
        if (!_dragging && Math.Abs(p.X - _pressPoint.X) < 2) return;
        _dragging = true;
        double delta = (p.X - _pressPoint.X) / _pps;
        double startDelta = _resizeStart ? delta : 0, endDelta = _resizeStart ? 0 : delta;
        if (_resizeMusic && _resizeMusicOriginal != null)
        {
            _editor.ResizeMusic(_dragId, _resizeMusicOriginal, startDelta, endDelta, _resizeKey);
            return;
        }
        if (_resizeClip == null) return;
        _editor.ResizeClip(_dragId, _resizeClip, startDelta, endDelta, _resizeKey);
        // 切った所の絵が見えるように、現在位置をクリップの始まり / 終わりに合わせる
        if (_editor.Layout.Find(_dragId) is { } span) Seek(_resizeStart ? span.Start : span.End);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // ポインタの下の時刻を動かさずに拡大縮小する
            double x = e.GetPosition(this).X;
            double seconds = (x - Pad + _scrollX) / _pps;
            _pps = Math.Clamp(_pps * (e.Delta.Y > 0 ? 1.25 : 0.8), 4, 600);
            _scrollX = seconds * _pps - (x - Pad);
            ClampScroll();
            ViewChanged?.Invoke();
            InvalidateVisual();
        }
        else ScrollX -= (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y) * 60;
        e.Handled = true;
    }

    /// <summary>吸着する距離 (ピクセル)。</summary>
    private const double SnapPixels = 8;
    private double? _snapSeconds;

    /// <summary>
    /// 音楽を delta 秒動かすとき、始まりか終わりが近くの区切り (作品の先頭・クリップの境目・映像の終わり・現在位置) に来るなら、
    /// そこにぴったり合わせた移動量にする。Alt を押している間は合わせない。
    /// </summary>
    private double SnapMusic(double delta, bool off)
    {
        _snapSeconds = null;
        if (off || _dragId == null || _editor.Layout is not { } layout || layout.FindMusic(_dragId) is not { } span) return delta;
        double start = span.Start / Fps, length = span.Length / Fps;
        var marks = new List<double> { 0, layout.VideoLength / Fps, _session.CurrentFrame / Fps };
        marks.AddRange(layout.Clips.Select(c => c.Start / Fps));
        double best = SnapPixels / _pps, result = delta;
        foreach (var mark in marks)
            foreach (var edge in new[] { start, start + length })
            {
                double d = mark - (edge + delta);
                if (Math.Abs(d) < best) { best = Math.Abs(d); result = delta + d; _snapSeconds = mark; }
            }
        return Math.Max(-start, result);
    }

    // ---------------- ほかのアプリからのドラッグ ----------------

    /// <summary>ドラッグ中に、入る位置を示す (null で消す)。</summary>
    public void ShowDropTarget(Point? pos)
    {
        int? index = pos is { } p ? InsertIndexAt(p.X) : null;
        if (index == _dropIndex && _drag == DragKind.None) return;
        if (_drag != DragKind.None) return;   // 並べ替えの途中は、そちらの表示を優先する
        _dropIndex = index;
        InvalidateVisual();
    }

    // ---------------- 描画 ----------------

    private static readonly Dictionary<StoryClipKind, Color> KindColors = new()
    {
        [StoryClipKind.Video] = Color.Parse("#3A6EA5"),
        [StoryClipKind.Photo] = Color.Parse("#3A8F6E"),
        [StoryClipKind.Title] = Color.Parse("#6E5AA5"),
    };
    private static readonly IBrush MusicBrush = new SolidColorBrush(Color.Parse("#A5503A"));
    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));

    private FormattedText Text(string s, double size, IBrush brush, bool verbatim = true) =>
        new(verbatim ? L.StripNames(s) : L.Display(s), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, brush);

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Svc.Brush("Bg.Base"), bounds);
        var layout = _editor.Layout;
        var fg = Svc.Brush("Fg.Primary");
        var secondary = Svc.Brush("Fg.Secondary");
        var border = new Pen(Svc.Brush("Border.Default"), 1);

        DrawRuler(ctx, bounds, secondary, border);

        // 音楽の帯の背景
        ctx.FillRectangle(Svc.Brush("Bg.Panel"), new Rect(0, MusicTop, bounds.Width, MusicHeight));

        if (layout.Clips.Count == 0)
        {
            var hint = Text("ここに動画や写真をドラッグするか、「ビデオと写真」を押して追加します", 13, secondary, verbatim: false);
            ctx.DrawText(hint, new Point(Math.Max(Pad, (bounds.Width - hint.Width) / 2), ClipTop + (ClipHeight - hint.Height) / 2));
        }

        string? selected = _editor.SelectedId;
        foreach (var span in layout.Clips) DrawClip(ctx, layout, span, _editor.IsSelected(span.Clip.Id));

        if (layout.Music.Count == 0)
        {
            var hint = Text("♪ 音楽はここに表示されます", 11, secondary, verbatim: false);
            ctx.DrawText(hint, new Point(Pad + 4, MusicTop + (MusicHeight - hint.Height) / 2));
        }
        foreach (var m in layout.Music) DrawMusic(ctx, m, m.Music.Id == selected);

        // ドロップ・並べ替えで入る位置
        if (_dropIndex is { } at)
        {
            double x = at < layout.Clips.Count ? FrameToX(layout.Clips[at].Start) : FrameToX(layout.VideoLength);
            ctx.FillRectangle(Svc.Brush("Accent"), new Rect(x - 2, ClipTop - 4, 4, ClipHeight + 8));
        }

        // 音楽を合わせた所
        if (_snapSeconds is { } snap && _drag == DragKind.Music)
        {
            double sx = Math.Round(Pad + snap * _pps - _scrollX) + 0.5;
            ctx.DrawLine(new Pen(Svc.Brush("Accent"), 1, new DashStyle(new double[] { 4, 3 }, 0)), new Point(sx, RulerHeight), new Point(sx, bounds.Height));
        }

        // 現在位置
        double px = Math.Round(FrameToX(_session.CurrentFrame)) + 0.5;
        if (px >= 0 && px <= bounds.Width)
        {
            var red = new Pen(Svc.Brush("Playhead"), 1);
            ctx.DrawLine(red, new Point(px, 0), new Point(px, bounds.Height));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(px - 5, 0), true);
                g.LineTo(new Point(px + 5, 0)); g.LineTo(new Point(px, 7)); g.EndFigure(true);
            }
            ctx.DrawGeometry(Svc.Brush("Playhead"), null, head);
        }
    }

    private void DrawRuler(DrawingContext ctx, Rect bounds, IBrush secondary, IPen border)
    {
        ctx.FillRectangle(Svc.Brush("Bg.Panel"), new Rect(0, 0, bounds.Width, RulerHeight));
        ctx.DrawLine(border, new Point(0, RulerHeight - 0.5), new Point(bounds.Width, RulerHeight - 0.5));
        double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double step = steps.FirstOrDefault(s => s * _pps >= 70, 600);
        double first = Math.Floor(_scrollX / _pps / step) * step;
        for (double t = first; ; t += step)
        {
            double x = Pad + t * _pps - _scrollX;
            if (x > bounds.Width) break;
            if (x < 0) continue;
            ctx.DrawLine(border, new Point(Math.Round(x) + 0.5, RulerHeight - 6), new Point(Math.Round(x) + 0.5, RulerHeight));
            int total = (int)Math.Round(t);
            ctx.DrawText(Text($"{total / 60}:{total % 60:00}", 10, secondary), new Point(x + 3, 4));
        }
    }

    private void DrawClip(DrawingContext ctx, StoryboardLayout layout, ClipSpan span, bool selected)
    {
        var clip = span.Clip;
        var rect = ClipRect(layout, span);
        if (rect.Right < 0 || rect.Left > Bounds.Width) return;
        // 次のクリップと重なる所は次のクリップの下になるので、見える幅で描く
        var inner = rect.Deflate(new Thickness(1, 0));
        if (_drag == DragKind.Clip && _dragging && _dragId == clip.Id) inner = inner.Deflate(new Thickness(0, 6));
        var clipState = ctx.PushClip(inner);
        var color = KindColors[clip.Kind];
        ctx.FillRectangle(new SolidColorBrush(color), inner);

        // サムネイル (縦の大きさに合わせて、幅いっぱいに並べる)
        const double thumbTop = 18, thumbHeight = ClipHeight - 18 - 18;
        if (clip.Kind == StoryClipKind.Title)
        {
            var bg = Color.TryParse(clip.Background, out var b) ? b : Colors.Black;
            var area = new Rect(inner.X, inner.Y + thumbTop, inner.Width, thumbHeight);
            ctx.FillRectangle(new SolidColorBrush(bg), area);
            var fgColor = Color.TryParse(clip.TextColor, out var f) ? f : Colors.White;
            var t = Text(clip.Text.Split('\n')[0], 13, new SolidColorBrush(fgColor));
            ctx.DrawText(t, new Point(area.X + Math.Max(4, (area.Width - t.Width) / 2), area.Y + (area.Height - t.Height) / 2));
        }
        else if (Svc.Settings.ShowThumbnails)
        {
            bool video = clip.Kind == StoryClipKind.Video;
            double x = inner.X;
            double t = clip.TrimStart;
            while (x < inner.Right && x < Bounds.Width)
            {
                var thumb = Svc.Thumbnails.Get(clip.File, t, video);
                double w = thumb != null ? thumb.Size.Width * thumbHeight / Math.Max(1, thumb.Size.Height) : thumbHeight * 16 / 9;
                if (x + w >= 0 && thumb != null) ctx.DrawImage(thumb, new Rect(thumb.Size), new Rect(x, inner.Y + thumbTop, w, thumbHeight));
                x += w;
                if (video) t += w / _pps * clip.EffectiveSpeed;
                if (!video && thumb == null) break;
            }
        }

        if (clip.UsesFile && !SimpleMedia.Exists(clip.File)) DrawMissing(ctx, new Rect(inner.X, inner.Y + thumbTop, inner.Width, thumbHeight));

        // 名前と長さ。右端の印は、次のクリップと重なる所 (次のクリップの下になる) より手前に出す
        var white = Brushes.White;
        double visibleRight = Math.Min(inner.Right, FrameToX(span.End + 1 - span.FadeOut));
        string name = clip.Kind == StoryClipKind.Title ? L.T("タイトル") : Path.GetFileName(clip.File);
        ctx.DrawText(Text(name, 11, white), new Point(inner.X + 4, inner.Y + 2));
        string length = FormatSeconds(clip.Seconds) + (clip.EffectiveSpeed != 1 ? $"  ×{clip.EffectiveSpeed:0.##}" : "");
        ctx.DrawText(Text(length, 10, white), new Point(inner.X + 4, inner.Bottom - 15));
        // 付いている設定の印
        var marks = new List<string>();
        if (clip.Caption.Trim().Length > 0) marks.Add("T");
        if (clip.Motion != ClipMotion.None) marks.Add("↗");
        if (clip.Look != ClipLook.None) marks.Add("✦");
        if (clip.Kind == StoryClipKind.Video && (clip.Muted || !clip.HasAudio)) marks.Add("🔇");
        if (marks.Count > 0)
        {
            var m = Text(string.Join(" ", marks), 10, white);
            ctx.DrawText(m, new Point(Math.Max(inner.X + 40, visibleRight - m.Width - 4), inner.Bottom - 15));
        }

        // 切り替え効果の印 (クリップの入り)
        if (clip.Transition > 0 && span.FadeIn > 0)
        {
            double w = Math.Max(6, FrameToX(span.Start + span.FadeIn) - FrameToX(span.Start));
            var tr = new Rect(inner.X, inner.Y + thumbTop, w, thumbHeight);
            var grad = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(200, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
            };
            ctx.FillRectangle(grad, tr);
            ctx.DrawText(Text("◆", 10, white), new Point(inner.X + 3, inner.Y + thumbTop + 2));
        }
        // 最後に黒へフェードアウトする印 (クリップの出)
        var board = _editor.Board;
        if (board is { EndFadeSeconds: > 0 } && span.Index == layout.Clips.Count - 1)
        {
            double w = Math.Max(6, Math.Min(inner.Width / 2, board.EndFadeSeconds * _pps));
            var tr = new Rect(inner.Right - w, inner.Y + thumbTop, w, thumbHeight);
            var grad = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(220, 0, 0, 0), 1) },
            };
            ctx.FillRectangle(grad, tr);
        }
        clipState.Dispose();

        if (selected) ctx.DrawRectangle(null, new Pen(Svc.Brush("Accent"), 2), inner.Deflate(1));
        else ctx.DrawRectangle(null, new Pen(Shade, 1), inner);
    }

    private void DrawMusic(DrawingContext ctx, MusicSpan span, bool selected)
    {
        var rect = MusicRect(span);
        if (_drag == DragKind.Music && _dragging && _dragId == span.Music.Id) rect = rect.Translate(new Vector(_musicDelta * _pps, 0));
        if (rect.Right < 0 || rect.Left > Bounds.Width) return;
        var inner = rect.Deflate(new Thickness(1, 1));
        using (ctx.PushClip(inner))
        {
            ctx.FillRectangle(MusicBrush, inner);
            if (Svc.Settings.ShowWaveform && Svc.Waveforms.Get(span.Music.File) is { } peaks)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
                double mid = inner.Center.Y, half = inner.Height / 2 - 2;
                for (double x = Math.Max(inner.X, 0); x < Math.Min(inner.Right, Bounds.Width); x += 2)
                {
                    double t = span.Music.TrimStart + (x - inner.X) / _pps;
                    int i = (int)(t * WaveformCache.PeaksPerSecond);
                    if (i < 0 || i >= peaks.Ready) continue;   // まだ計算していない所は描かない
                    double h = peaks.Values[i] / 255.0 * half;
                    ctx.DrawLine(pen, new Point(x, mid - h), new Point(x, mid + h));
                }
            }
            if (span.Music.Loop)
            {
                // 繰り返す所に区切りの線を引く
                double cycle = Math.Max(StoryClip.MinSeconds, span.Music.Seconds) * _pps;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1, new DashStyle(new double[] { 3, 3 }, 0));
                for (double x = inner.X + cycle; x < inner.Right && x < Bounds.Width; x += cycle)
                    if (x >= 0) ctx.DrawLine(pen, new Point(Math.Round(x) + 0.5, inner.Y), new Point(Math.Round(x) + 0.5, inner.Bottom));
            }
            if (!SimpleMedia.Exists(span.Music.File)) DrawMissing(ctx, inner, small: true);
            string label = "♪ " + Path.GetFileName(span.Music.File) + (span.Music.Loop ? "  ↻" : "") + (span.Music.Duck ? "  ↓" : "");
            ctx.DrawText(Text(label, 11, Brushes.White), new Point(inner.X + 4, inner.Y + (inner.Height - 15) / 2));
        }
        if (selected) ctx.DrawRectangle(null, new Pen(Svc.Brush("Accent"), 2), inner.Deflate(1));
    }

    /// <summary>素材が見つからない印: 赤い斜線と「見つかりません」。</summary>
    private void DrawMissing(DrawingContext ctx, Rect area, bool small = false)
    {
        var error = Svc.Brush("Error");
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(140, 60, 0, 0)), area);
        var pen = new Pen(error, 2);
        using (ctx.PushClip(area))
            for (double x = area.X - area.Height; x < area.Right; x += 12)
                ctx.DrawLine(pen, new Point(x, area.Bottom), new Point(x + area.Height, area.Y));
        var t = Text("⚠ " + L.T("ファイルが見つかりません"), small ? 10 : 11, Brushes.White, verbatim: false);
        var bg = new Rect(area.X + 4, area.Y + (area.Height - t.Height) / 2 - 1, Math.Min(t.Width + 6, Math.Max(0, area.Width - 8)), t.Height + 2);
        if (small) bg = bg.WithX(Math.Max(area.X + 4, area.Right - bg.Width - 4));
        using (ctx.PushClip(area))
        {
            ctx.FillRectangle(error, bg);
            ctx.DrawText(t, new Point(bg.X + 3, bg.Y + 1));
        }
    }

    public static string FormatSeconds(double seconds)
    {
        int m = (int)(seconds / 60);
        double s = seconds - m * 60;
        return $"{m}:{s:00.0}";
    }
}
