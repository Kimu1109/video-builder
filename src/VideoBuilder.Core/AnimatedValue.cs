namespace VideoBuilder.Core;

/// <summary>移動方法 (AviUtl のトラックバー移動方法に相当)。</summary>
public enum MovementMode
{
    None,       // 移動無し
    Linear,     // 直線移動
    Curve,      // 曲線移動
    Ease,       // 加減速移動
    Jump,       // 瞬間移動
    IgnoreMid,  // 中間点無視
    Delta,      // 移動量指定
    Random,     // ランダム移動
    Repeat,     // 反復移動
}

/// <summary>
/// 中間点で区切った区間ごとの値を持つパラメータ。
/// Values の要素数は常に「区間数 + 1」(開始・各中間点・終了)。
/// </summary>
public sealed class AnimatedValue
{
    public List<double> Values { get; set; } = new() { 0, 0 };
    public MovementMode Mode { get; set; } = MovementMode.None;
    public bool Accel { get; set; }
    public bool Decel { get; set; }
    /// <summary>ランダム移動・反復移動の間隔 (フレーム)。</summary>
    public int Interval { get; set; } = 10;

    public AnimatedValue() { }

    public AnimatedValue(double value, int sections = 1)
    {
        Values = Enumerable.Repeat(value, sections + 1).ToList();
    }

    public bool IsMoving => Mode != MovementMode.None;

    public AnimatedValue Clone() => new()
    {
        Values = new List<double>(Values), Mode = Mode, Accel = Accel, Decel = Decel, Interval = Interval,
    };

    /// <summary>区間数に合わせて Values の長さを揃える。</summary>
    public void EnsureSections(int sections)
    {
        if (Values.Count == 0) Values.Add(0);
        while (Values.Count < sections + 1) Values.Add(Values[^1]);
        while (Values.Count > sections + 1) Values.RemoveAt(Values.Count - 1);
    }

    /// <param name="bounds">区間境界 (開始, 中間点..., 終了)。要素数 = 区間数 + 1。</param>
    public double Evaluate(IReadOnlyList<int> bounds, int frame)
    {
        int sections = bounds.Count - 1;
        if (sections < 1 || Values.Count == 0) return Values.Count > 0 ? Values[0] : 0;
        frame = Math.Clamp(frame, bounds[0], bounds[^1]);

        int i = 0;
        while (i < sections - 1 && frame >= bounds[i + 1]) i++;

        double V(int k) => Values[Math.Clamp(k, 0, Values.Count - 1)];
        int len = bounds[i + 1] - bounds[i];
        double t = len <= 0 ? 0 : (double)(frame - bounds[i]) / len;

        switch (Mode)
        {
            case MovementMode.None:
            case MovementMode.Jump:
                return V(i);
            case MovementMode.Linear:
                return Lerp(V(i), V(i + 1), ApplyEase(t));
            case MovementMode.Ease:
                return Lerp(V(i), V(i + 1), SmoothStep(t));
            case MovementMode.Curve:
                return CatmullRom(V(i - 1), V(i), V(i + 1), V(i + 2), ApplyEase(t));
            case MovementMode.IgnoreMid:
            {
                int total = bounds[^1] - bounds[0];
                double g = total <= 0 ? 0 : (double)(frame - bounds[0]) / total;
                return Lerp(V(0), V(sections), ApplyEase(g));
            }
            case MovementMode.Delta:
                return V(i) + V(i + 1) * (frame - bounds[i]);
            case MovementMode.Random:
            {
                int step = Math.Max(1, Interval);
                int n = (frame - bounds[0]) / step;
                double r = Hash01(n * 7919 + i * 104729);
                return Lerp(V(i), V(i + 1), r);
            }
            case MovementMode.Repeat:
            {
                int step = Math.Max(1, Interval);
                double p = (double)(frame - bounds[i]) / step;
                double frac = p % 2.0;
                double tri = frac <= 1 ? frac : 2 - frac;
                return Lerp(V(i), V(i + 1), ApplyEase(tri));
            }
        }
        return V(i);
    }

    private double ApplyEase(double t)
    {
        if (Accel && Decel) return SmoothStep(t);
        if (Accel) return t * t;
        if (Decel) return 1 - (1 - t) * (1 - t);
        return t;
    }

    private static double SmoothStep(double t) => t * t * (3 - 2 * t);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
    {
        double t2 = t * t, t3 = t2 * t;
        return 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }

    private static double Hash01(int n)
    {
        unchecked
        {
            uint x = (uint)n;
            x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
            return x / (double)uint.MaxValue;
        }
    }
}
