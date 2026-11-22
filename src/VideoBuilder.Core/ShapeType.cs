namespace VideoBuilder.Core;

/// <summary>図形オブジェクトの「図形の種類」(EffectRegistry.ShapeTypes の番号)。プロジェクトファイルには番号で残る。</summary>
public enum ShapeType
{
    Background = 0,
    Circle = 1,
    Rectangle = 2,
    Triangle = 3,
    Pentagon = 4,
    Hexagon = 5,
    Star = 6,
    Diamond = 7,
    Trapezoid = 8,
    Parallelogram = 9,
    Polygon = 10,
    /// <summary>右向きの矢印。向きは標準描画の回転で変える。</summary>
    Arrow = 11,
    DoubleArrow = 12,
    /// <summary>山形 (シェブロン)。右向き。</summary>
    Chevron = 13,
    Cross = 14,
    Heart = 15,
    BubbleRect = 16,
    BubbleOval = 17,
    /// <summary>扇形・円弧。穴を開けるとドーナツ状の弧になる。</summary>
    Pie = 18,
    /// <summary>左側の角括弧「[」の形。</summary>
    Bracket = 19,
    /// <summary>左から右への直線。太さはライン幅、両端に矢印などを付けられる。</summary>
    Line = 20,
}
