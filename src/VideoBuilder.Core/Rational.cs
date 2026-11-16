using System.Globalization;

namespace VideoBuilder.Core;

/// <summary>分数。フレームレートなどを誤差なく扱う。</summary>
public readonly record struct Rational(long Num, long Den)
{
    public double Value => Den == 0 ? 0 : (double)Num / Den;

    public static Rational Parse(string s)
    {
        var parts = s.Split('/');
        if (parts.Length == 2)
            return new Rational(long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
        var d = double.Parse(s, CultureInfo.InvariantCulture);
        return FromDouble(d);
    }

    public static Rational FromDouble(double fps)
    {
        // 29.97 / 23.976 / 59.94 は NTSC 系の分数にする
        foreach (var (n, den) in new[] { (30000L, 1001L), (24000L, 1001L), (60000L, 1001L) })
            if (Math.Abs(fps - (double)n / den) < 0.005) return new Rational(n, den);
        if (Math.Abs(fps - Math.Round(fps)) < 1e-6) return new Rational((long)Math.Round(fps), 1);
        return new Rational((long)Math.Round(fps * 1000), 1000);
    }

    public override string ToString() => Den == 1 ? Num.ToString(CultureInfo.InvariantCulture) : $"{Num}/{Den}";
}
