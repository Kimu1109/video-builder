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

/// <summary>プラグインが追加する移動方法。</summary>
public interface IInterpolator
{
    /// <summary>プロジェクトファイルに保存される ID。</summary>
    string Id { get; }
    string DisplayName { get; }
    /// <param name="t">区間の中での進み具合 (0〜1)。</param>
    double Evaluate(double start, double end, double t);
}

public static class Interpolators
{
    private static readonly Dictionary<string, IInterpolator> Map = new();
    public static void Register(IInterpolator i) { lock (Map) Map[i.Id] = i; }
    public static IInterpolator? Find(string id) { lock (Map) return Map.GetValueOrDefault(id); }
    public static IReadOnlyList<IInterpolator> All { get { lock (Map) return Map.Values.ToList(); } }
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
    /// <summary>プラグインの移動方法の ID。設定されていれば Mode より優先する。</summary>
    public string? Custom { get; set; }
    /// <summary>動き方 (イージング)。null なら加速・減速のチェックで決める (以前からの動き)。</summary>
    public Easing? Ease { get; set; }
    /// <summary>
    /// 区間ごとの動き方。null の要素は Ease (なければ加速・減速) を使う。要素数は区間数 (足りない分は null とみなす)。
    /// 中間点の追加・削除・分割に合わせて、TimelineObject が並びを保つ。
    /// </summary>
    public List<Easing?>? SectionEases { get; set; }

    public AnimatedValue() { }

    public AnimatedValue(double value, int sections = 1)
    {
        Values = Enumerable.Repeat(value, sections + 1).ToList();
    }

    public bool IsMoving => Mode != MovementMode.None || Custom != null;

    public static bool SameContent(AnimatedValue a, AnimatedValue b)
    {
        if (a.Mode != b.Mode || a.Accel != b.Accel || a.Decel != b.Decel || a.Interval != b.Interval || a.Custom != b.Custom || a.Values.Count != b.Values.Count) return false;
        for (int i = 0; i < a.Values.Count; i++) if (!a.Values[i].Equals(b.Values[i])) return false;
        if (!Easing.Same(a.Ease, b.Ease)) return false;
        int n = Math.Max(a.SectionEases?.Count ?? 0, b.SectionEases?.Count ?? 0);
        for (int i = 0; i < n; i++) if (!Easing.Same(a.SectionEaseAt(i), b.SectionEaseAt(i))) return false;
        return true;
    }

    public AnimatedValue Clone() => new()
    {
        Values = new List<double>(Values), Mode = Mode, Accel = Accel, Decel = Decel, Interval = Interval, Custom = Custom,
        Ease = Ease?.Clone(), SectionEases = SectionEases?.Select(e => e?.Clone()).ToList(),
    };

    /// <summary>区間 section だけの動き方 (なければ null)。</summary>
    public Easing? SectionEaseAt(int section) =>
        SectionEases != null && section >= 0 && section < SectionEases.Count ? SectionEases[section] : null;

    /// <summary>区間 section の動き方を決める (null で区間ごとの設定を消す)。</summary>
    public void SetSectionEase(int section, Easing? ease)
    {
        if (section < 0) return;
        SectionEases ??= new List<Easing?>();
        while (SectionEases.Count <= section) SectionEases.Add(null);
        SectionEases[section] = ease;
        if (SectionEases.All(e => e == null)) SectionEases = null;
    }

    /// <summary>区間 section を 2 つに分けた (中間点を足した)。後ろの区間も同じ動き方にする。</summary>
    public void SplitSection(int section)
    {
        if (SectionEases == null || section < 0 || section >= SectionEases.Count) return;
        SectionEases.Insert(section + 1, SectionEases[section]?.Clone());
    }

    /// <summary>区間 section と次の区間を 1 つにした (中間点を消した)。前の区間の動き方を残す。</summary>
    public void MergeSections(int section)
    {
        if (SectionEases == null || section + 1 >= SectionEases.Count) return;
        SectionEases.RemoveAt(section + 1);
        if (SectionEases.All(e => e == null)) SectionEases = null;
    }

    /// <summary>区間 first から count 個だけを残す (オブジェクトの分割)。</summary>
    public void KeepSections(int first, int count)
    {
        if (SectionEases == null) return;
        SectionEases = SectionEases.Skip(first).Take(count).ToList();
        if (SectionEases.All(e => e == null)) SectionEases = null;
    }

    /// <summary>区間数に合わせて Values の長さを揃える。</summary>
    public void EnsureSections(int sections)
    {
        if (Values.Count == 0) Values.Add(0);
        while (Values.Count < sections + 1) Values.Add(Values[^1]);
        while (Values.Count > sections + 1) Values.RemoveAt(Values.Count - 1);
        if (SectionEases != null && SectionEases.Count > sections) SectionEases.RemoveRange(sections, SectionEases.Count - sections);
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

        if (Custom != null)
        {
            // プラグインが入っていない環境では直線移動で代用する
            var custom = Interpolators.Find(Custom);
            return custom != null ? custom.Evaluate(V(i), V(i + 1), ApplyEase(t, i)) : Lerp(V(i), V(i + 1), ApplyEase(t, i));
        }
        switch (Mode)
        {
            case MovementMode.None:
            case MovementMode.Jump:
                return V(i);
            case MovementMode.Linear:
                return Lerp(V(i), V(i + 1), ApplyEase(t, i));
            case MovementMode.Ease:
                return Lerp(V(i), V(i + 1), SmoothStep(t));
            case MovementMode.Curve:
                return CatmullRom(V(i - 1), V(i), V(i + 1), V(i + 2), ApplyEase(t, i));
            case MovementMode.IgnoreMid:
            {
                int total = bounds[^1] - bounds[0];
                double g = total <= 0 ? 0 : (double)(frame - bounds[0]) / total;
                return Lerp(V(0), V(sections), ApplyEase(g, -1));
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
                return Lerp(V(i), V(i + 1), ApplyEase(tri, i));
            }
        }
        return V(i);
    }

    /// <summary>区間 section (-1 なら区間によらない) の動き方で、進み具合 t を変える。</summary>
    private double ApplyEase(double t, int section)
    {
        var ease = SectionEaseAt(section) ?? Ease;
        if (ease != null) return ease.Apply(t);
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
