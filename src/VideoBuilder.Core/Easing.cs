namespace VideoBuilder.Core;

/// <summary>イージング (動き方) の種類。番号はプロジェクトファイルに残るので、増やすときは末尾に足す。</summary>
public enum EasingKind
{
    Sine, Quad, Cubic, Quart, Quint, Expo, Circ,
    /// <summary>行き過ぎて戻る。強さで行き過ぎる量が変わる。</summary>
    Back,
    /// <summary>ゴムのように揺れて止まる。強さで揺れる回数、減衰で揺れの収まり方が変わる。</summary>
    Elastic,
    /// <summary>弾んで止まる。</summary>
    Bounce,
    /// <summary>ばね。強さで揺れる速さ、減衰 (0〜1) で揺れの収まり方が変わる (1 で揺れない)。</summary>
    Spring,
    /// <summary>3 次ベジェ曲線 (0,0)-(X1,Y1)-(X2,Y2)-(1,1)。向きは使わない。</summary>
    Bezier,
}

/// <summary>イージングの向き。</summary>
public enum EasingDirection
{
    /// <summary>加速する (ゆっくり始まる)。</summary>
    In,
    /// <summary>減速する (ゆっくり止まる)。</summary>
    Out,
    /// <summary>加速して減速する。</summary>
    InOut,
}

/// <summary>
/// 区間の進み具合 (0〜1) を、動き方に合わせて変える。
/// 行き過ぎて戻る・ゴム・ばね・ベジェでは、途中で 0〜1 の外に出ることがある (行き過ぎ)。始め (0) と終わり (1) はいつもそのまま。
/// </summary>
public sealed class Easing
{
    public EasingKind Kind { get; set; } = EasingKind.Cubic;
    public EasingDirection Direction { get; set; } = EasingDirection.Out;
    /// <summary>強さ (1 = 標準)。行き過ぎて戻るの行き過ぎる量・ゴムの揺れる回数・ばねの揺れる速さ。</summary>
    public double Strength { get; set; } = 1;
    /// <summary>減衰 (0〜1、0.5 = 標準)。ゴム・ばねの揺れの収まり方。</summary>
    public double Damping { get; set; } = 0.5;
    /// <summary>ベジェ曲線の制御点 (既定は CSS の ease)。</summary>
    public double X1 { get; set; } = 0.25;
    public double Y1 { get; set; } = 0.1;
    public double X2 { get; set; } = 0.25;
    public double Y2 { get; set; } = 1;

    public Easing Clone() => (Easing)MemberwiseClone();

    public static bool Same(Easing? a, Easing? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a.Kind == b.Kind && a.Direction == b.Direction && a.Strength.Equals(b.Strength) && a.Damping.Equals(b.Damping)
            && a.X1.Equals(b.X1) && a.Y1.Equals(b.Y1) && a.X2.Equals(b.X2) && a.Y2.Equals(b.Y2);
    }

    public double Apply(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        switch (Kind)
        {
            case EasingKind.Bezier: return CubicBezier(t, X1, Y1, X2, Y2);
            // 揺れて止まるもの: 元の形が「減速」(終わりで揺れる)
            case EasingKind.Elastic or EasingKind.Spring or EasingKind.Bounce:
                return Direction switch
                {
                    EasingDirection.Out => SettleOut(t),
                    EasingDirection.In => 1 - SettleOut(1 - t),
                    _ => t < 0.5 ? (1 - SettleOut(1 - 2 * t)) / 2 : (1 + SettleOut(2 * t - 1)) / 2,
                };
        }
        return Direction switch
        {
            EasingDirection.In => In(t),
            EasingDirection.Out => 1 - In(1 - t),
            _ => t < 0.5 ? In(2 * t) / 2 : 1 - In(2 - 2 * t) / 2,
        };
    }

    /// <summary>加速の形 (0 → 1)。</summary>
    private double In(double t) => Kind switch
    {
        EasingKind.Sine => 1 - Math.Cos(t * Math.PI / 2),
        EasingKind.Quad => t * t,
        EasingKind.Cubic => t * t * t,
        EasingKind.Quart => t * t * t * t,
        EasingKind.Quint => t * t * t * t * t,
        EasingKind.Expo => t <= 0 ? 0 : Math.Pow(2, 10 * t - 10),
        EasingKind.Circ => 1 - Math.Sqrt(Math.Max(0, 1 - t * t)),
        EasingKind.Back => BackIn(t),
        _ => t,
    };

    private double BackIn(double t)
    {
        double c = 1.70158 * Math.Max(0, Strength);
        return (c + 1) * t * t * t - c * t * t;
    }

    /// <summary>終わりで揺れて止まる形 (ゴム・ばね・弾む)。</summary>
    private double SettleOut(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double strength = Math.Max(0.05, Strength), damping = Math.Clamp(Damping, 0, 1);
        switch (Kind)
        {
            case EasingKind.Bounce: return BounceOut(t);
            case EasingKind.Elastic:
            {
                // 揺れる回数は強さに比例、収まる速さは減衰に比例 (0.5 で標準の easeOutElastic)
                double decay = 20 * Math.Max(0.05, damping);
                double v = Math.Pow(2, -decay * t) * Math.Sin((t * 10 * strength - 0.75) * (2 * Math.PI / 3)) + 1;
                double end = Math.Pow(2, -decay) * Math.Sin((10 * strength - 0.75) * (2 * Math.PI / 3)) + 1;
                return v + (1 - end) * t;   // 終わりでちょうど 1 にする
            }
            default:
            {
                // ばね: 減衰振動。減衰比 ζ が 1 以上なら揺れずに近づく
                double omega = 2 * Math.PI * 1.5 * strength, zeta = Math.Max(0.02, damping);
                double Raw(double x)
                {
                    if (zeta >= 1) return 1 - (1 + omega * x) * Math.Exp(-omega * x);
                    double wd = omega * Math.Sqrt(1 - zeta * zeta);
                    return 1 - Math.Exp(-zeta * omega * x) * (Math.Cos(wd * x) + zeta * omega / wd * Math.Sin(wd * x));
                }
                return Raw(t) + (1 - Raw(1)) * t;
            }
        }
    }

    public static double BounceOut(double t)
    {
        const double n1 = 7.5625, d1 = 2.75;
        if (t < 1 / d1) return n1 * t * t;
        if (t < 2 / d1) { t -= 1.5 / d1; return n1 * t * t + 0.75; }
        if (t < 2.5 / d1) { t -= 2.25 / d1; return n1 * t * t + 0.9375; }
        t -= 2.625 / d1;
        return n1 * t * t + 0.984375;
    }

    /// <summary>3 次ベジェ曲線で、横 (時間) が t のときの縦 (進み具合)。</summary>
    public static double CubicBezier(double t, double x1, double y1, double x2, double y2)
    {
        x1 = Math.Clamp(x1, 0, 1); x2 = Math.Clamp(x2, 0, 1);   // 時間は戻らない
        static double B(double s, double p1, double p2) => 3 * (1 - s) * (1 - s) * s * p1 + 3 * (1 - s) * s * s * p2 + s * s * s;
        static double D(double s, double p1, double p2) => 3 * (1 - s) * (1 - s) * p1 + 6 * (1 - s) * s * (p2 - p1) + 3 * s * s * (1 - p2);
        // ニュートン法で s を求め、うまくいかなければ二分法
        double s = t;
        for (int i = 0; i < 8; i++)
        {
            double err = B(s, x1, x2) - t, d = D(s, x1, x2);
            if (Math.Abs(err) < 1e-7) return B(s, y1, y2);
            if (Math.Abs(d) < 1e-6) break;
            s = Math.Clamp(s - err / d, 0, 1);
        }
        double lo = 0, hi = 1;
        s = t;
        for (int i = 0; i < 40; i++)
        {
            double x = B(s, x1, x2);
            if (Math.Abs(x - t) < 1e-7) break;
            if (x < t) lo = s; else hi = s;
            s = (lo + hi) / 2;
        }
        return B(s, y1, y2);
    }
}
