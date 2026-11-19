using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VideoBuilder.App.Controls;
using VideoBuilder.App.Services;

namespace VideoBuilder.App.Workspaces.Advanced;

/// <summary>
/// タイムラインの現在位置カーソル (赤い線)。タイムライン本体とは別のコントロールにして、
/// 再生中は「この細い線を動かすだけ」で済むようにする (本体のレイヤーやオブジェクトを毎フレーム描き直さない)。
/// </summary>
public sealed class PlayheadOverlay : Control
{
    private const double HalfWidth = 4.5;
    private readonly TimelineCanvas _canvas;
    private readonly TranslateTransform _move = new();

    public PlayheadOverlay(TimelineCanvas canvas)
    {
        _canvas = canvas;
        IsHitTestVisible = false;
        Width = HalfWidth * 2;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        RenderTransform = _move;
        RenderTransformOrigin = RelativePoint.TopLeft;
        Svc.Session.FrameChanged += UpdatePosition;
        Svc.Session.SceneChanged += UpdatePosition;
        canvas.ViewChanged += UpdatePosition;
        Svc.Theme.Changed += InvalidateVisual;
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        double x = Math.Round(_canvas.FrameToX(Svc.Session.CurrentFrame));
        // レイヤー名の列に重なる位置では出さない
        bool visible = x >= TimelineCanvas.HeaderWidth;
        if (IsVisible != visible) IsVisible = visible;
        if (visible && _move.X != x - 4) _move.X = x - 4;
    }

    public override void Render(DrawingContext context)
    {
        var red = Svc.Brush("Playhead");
        context.FillRectangle(red, new Rect(0, 0, 9, 6));
        context.FillRectangle(red, new Rect(4, 0, 1, Bounds.Height));
    }
}
