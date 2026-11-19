using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoBuilder.Core;

/// <summary>
/// Lottie (JSON) の色の取り出しと置き換え。
/// ファイルに出てくる色を、出てきた順に重複なく並べたものを「パレット」とし、ベクター画像の「色 1」〜「色 8」が
/// パレットの 1 番目〜8 番目の色を置き換える (その色が出てくる所をすべて置き換える)。
/// 対象: シェイプの塗り / 線 (ty が fl / st の c。キーフレームの値も含む)、平面レイヤーの色 (sc)、テキストの文字色 / 線の色 (fc / sc)。
/// グラデーションは対象外。
/// </summary>
public static class LottieColors
{
    /// <summary>置き換えられる色の数 (パラメータ color1〜color8)。</summary>
    public const int Slots = 8;

    public static string ParamKey(int index) => "color" + (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>key が「色 n」のパラメータなら 0 始まりの番号、違えば -1。</summary>
    public static int SlotOf(string key) =>
        key.StartsWith("color", StringComparison.Ordinal) && int.TryParse(key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Slots ? n - 1 : -1;

    /// <summary>ファイルに出てくる色 (#RRGGBB) を、出てきた順に重複なく並べる。読めなければ空。</summary>
    public static List<string> Palette(string json)
    {
        var result = new List<string>();
        var root = Parse(json);
        if (root == null) return result;
        Walk(root, hex => { if (!result.Contains(hex)) result.Add(hex); return null; });
        return result;
    }

    /// <summary>map (元の色 → 新しい色。どちらも #RRGGBB) のとおりに色を置き換えた JSON を返す。透明度はそのまま。</summary>
    public static string Recolor(string json, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return json;
        var root = Parse(json);
        if (root == null) return json;
        Walk(root, hex => map.TryGetValue(hex, out var to) ? to : null);
        return root.ToJsonString();
    }

    /// <summary>ベクター画像のエフェクトの「色 n」の値から、置き換えの対応表を作る (空の色は置き換えない)。</summary>
    public static Dictionary<string, string> MapFor(Effect effect, IReadOnlyList<string> palette)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Slots && i < palette.Count; i++)
        {
            var hex = Normalize(effect.GetString(ParamKey(i)));
            if (hex != null && hex != palette[i]) map[palette[i]] = hex;
        }
        return map;
    }

    /// <summary>"#rgb" / "#rrggbb" / "#aarrggbb" を "#RRGGBB" にする。色でなければ null。</summary>
    public static string? Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (!s.StartsWith('#')) return null;
        var h = s[1..];
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        else if (h.Length == 8) h = h[2..];
        if (h.Length != 6 || !h.All(Uri.IsHexDigit)) return null;
        return "#" + h.ToUpperInvariant();
    }

    // ---------------- ファイルごとのパレット (更新日時が同じ間は覚えておく) ----------------

    private static readonly Dictionary<string, (DateTime Time, List<string> Palette)> Cache = new();

    /// <summary>ファイルのパレット。Lottie でない・読めないときは空。</summary>
    public static IReadOnlyList<string> PaletteOfFile(string path)
    {
        if (path.Length == 0 || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return Array.Empty<string>();
        try
        {
            var time = File.GetLastWriteTimeUtc(path);
            lock (Cache)
                if (Cache.TryGetValue(path, out var hit) && hit.Time == time) return hit.Palette;
            var palette = Palette(File.ReadAllText(path));
            lock (Cache)
            {
                if (Cache.Count > 64) Cache.Clear();
                Cache[path] = (time, palette);
            }
            return palette;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    // ---------------- JSON をたどる ----------------

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>色が出てくる所を文書の順にたどり、f に渡す。f が色を返したら、そこを置き換える。</summary>
    private static void Walk(JsonNode node, Func<string, string?> f)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var ty = obj["ty"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
                foreach (var (key, child) in obj.ToList())
                {
                    if (child == null) continue;
                    if (key == "c" && ty is "fl" or "st" && child is JsonObject prop) { ColorProperty(prop, f); continue; }
                    if (key is "fc" or "sc")
                    {
                        if (child is JsonArray arr && IsColorArray(arr)) { ColorArray(arr, f); continue; }
                        if (child is JsonValue v && v.TryGetValue<string>(out var str) && Normalize(str) is { } hex)
                        {
                            if (f(hex) is { } to) obj[key] = to.ToLowerInvariant();
                            continue;
                        }
                    }
                    Walk(child, f);
                }
                break;
            }
            case JsonArray array:
                foreach (var child in array.ToList())
                    if (child != null) Walk(child, f);
                break;
        }
    }

    /// <summary>{"a":0,"k":[r,g,b,a]} または {"a":1,"k":[{"s":[...],"e":[...]}, ...]}。</summary>
    private static void ColorProperty(JsonObject prop, Func<string, string?> f)
    {
        if (prop["k"] is not JsonArray k) return;
        if (IsColorArray(k)) { ColorArray(k, f); return; }
        foreach (var kf in k)
        {
            if (kf is not JsonObject frame) continue;
            if (frame["s"] is JsonArray s && IsColorArray(s)) ColorArray(s, f);
            if (frame["e"] is JsonArray e && IsColorArray(e)) ColorArray(e, f);
        }
    }

    private static bool IsColorArray(JsonArray a) =>
        a.Count is 3 or 4 && a.All(x => x is JsonValue v && v.TryGetValue<double>(out _));

    private static void ColorArray(JsonArray a, Func<string, string?> f)
    {
        double[] v = a.Select(x => x!.GetValue<double>()).ToArray();
        // Bodymovin は 0〜1。古いファイルには 0〜255 のものがある
        bool bytes = v.Take(3).Any(x => x > 1);
        double scale = bytes ? 1 : 255;
        int Byte(double x) => (int)Math.Clamp(Math.Round(x * scale), 0, 255);
        var hex = $"#{Byte(v[0]):X2}{Byte(v[1]):X2}{Byte(v[2]):X2}";
        if (f(hex) is not { } to || Normalize(to) is not { } n) return;
        for (int i = 0; i < 3; i++)
        {
            int b = Convert.ToInt32(n.Substring(1 + i * 2, 2), 16);
            a[i] = bytes ? b : Math.Round(b / 255.0, 4);
        }
    }
}
