using System.Globalization;
using System.Text.Json.Serialization;

namespace VideoBuilder.Core;

/// <summary>オブジェクトに積まれる効果 1 つ分 (メディア本体・フィルタ・標準描画)。</summary>
public sealed class Effect
{
    public string DefinitionId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>トラックバー (数値・移動可能) の値。</summary>
    public Dictionary<string, AnimatedValue> Tracks { get; set; } = new();
    /// <summary>チェック・選択・テキスト・色・ファイルなどの値 (文字列で保持)。</summary>
    public Dictionary<string, string> Values { get; set; } = new();

    [JsonIgnore]
    public EffectDefinition Definition => EffectRegistry.Get(DefinitionId);

    public Effect() { }

    public Effect(EffectDefinition def, int sections = 1)
    {
        DefinitionId = def.Id;
        foreach (var p in def.Params)
        {
            switch (p.Kind)
            {
                case ParamKind.Track: Tracks[p.Key] = new AnimatedValue(p.Default, sections); break;
                case ParamKind.Check:
                case ParamKind.Choice: Values[p.Key] = ((int)p.Default).ToString(CultureInfo.InvariantCulture); break;
                default: Values[p.Key] = p.DefaultText; break;
            }
        }
    }

    public Effect Clone()
    {
        var e = new Effect { DefinitionId = DefinitionId, Enabled = Enabled, Values = new Dictionary<string, string>(Values) };
        foreach (var (k, v) in Tracks) e.Tracks[k] = v.Clone();
        return e;
    }

    public double Eval(string key, IReadOnlyList<int> bounds, int frame)
    {
        if (Tracks.TryGetValue(key, out var v)) return v.Evaluate(bounds, frame);
        return Definition.Find(key)?.Default ?? 0;
    }

    public string GetString(string key) =>
        Values.TryGetValue(key, out var s) ? s : Definition.Find(key)?.DefaultText ?? "";

    public int GetInt(string key) =>
        Values.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : (int)(Definition.Find(key)?.Default ?? 0);

    public bool GetBool(string key) => GetInt(key) != 0;
}
