using System.Globalization;

namespace VideoBuilder.Core;

/// <summary>
/// テンプレートの公開項目 (ExposedParam) の値を、文字列で読み書きする。プラグインからテンプレートの項目を埋めるときに使う。
/// 数値 (トラックバー) は不変の書式の数値、チェックは "1" / "0"、ほかはそのままの文字列。
/// </summary>
public static class ExposedValues
{
    /// <summary>公開項目が指すエフェクトとパラメータ。見つからなければ null。</summary>
    public static (Effect Effect, ParamDefinition Param)? Resolve(IEnumerable<TimelineObject> objects, ExposedParam x)
    {
        if (Find(objects, x) is not var (_, e, p)) return null;
        return (e, p);
    }

    private static (TimelineObject Object, Effect Effect, ParamDefinition Param)? Find(IEnumerable<TimelineObject> objects, ExposedParam x)
    {
        var o = objects.FirstOrDefault(o => o.Id == x.ObjectId);
        if (o == null || x.Effect < 0 || x.Effect >= o.Effects.Count) return null;
        var e = o.Effects[x.Effect];
        var p = e.Definition.Params.FirstOrDefault(p => p.Key == x.Key);
        return p == null ? null : (o, e, p);
    }

    /// <summary>文字 (1 行・複数行) の項目か。</summary>
    public static bool IsText(ParamDefinition p) => p.Kind is ParamKind.Text or ParamKind.MultilineText;

    public static string? Get(IEnumerable<TimelineObject> objects, ExposedParam x)
    {
        if (Resolve(objects, x) is not var (e, p)) return null;
        if (p.Kind == ParamKind.Track)
            return (e.Tracks.TryGetValue(p.Key, out var v) && v.Values.Count > 0 ? v.Values[0] : p.Default).ToString(CultureInfo.InvariantCulture);
        return e.Values.TryGetValue(p.Key, out var s) ? s : p.Kind == ParamKind.Check ? (p.Default != 0 ? "1" : "0") : p.DefaultText;
    }

    /// <summary>値を書き込む。数値の項目に数値でない文字を渡したときなど、書けなければ false。</summary>
    public static bool Set(IEnumerable<TimelineObject> objects, ExposedParam x, string value)
    {
        if (Find(objects, x) is not var (o, e, p)) return false;
        if (p.Kind == ParamKind.Track)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return false;
            // 動きは消して、すべての区間をその値で止める
            e.Tracks[p.Key] = new AnimatedValue(Math.Clamp(d, p.Min, p.Max), o.Sections);
            return true;
        }
        e.Values[p.Key] = value;
        return true;
    }
}
