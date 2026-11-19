using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoBuilder.Core;

/// <summary>
/// ベクター画像 (Lottie / SVG) の色の取り出しと置き換え。
/// ファイルに出てくる色を、出てきた順に重複なく並べたものを「パレット」とし、ベクター画像の「色 1」〜「色 8」が
/// パレットの 1 番目〜8 番目の色を置き換える (その色が出てくる所をすべて置き換える)。
/// Lottie: シェイプの塗り / 線 (ty が fl / st の c。キーフレームの値も含む)、平面レイヤーの色 (sc)、テキストの文字色 / 線の色 (fc / sc)。グラデーションは対象外。
/// SVG: fill / stroke / stop-color / flood-color / lighting-color / color の属性と、style 属性・&lt;style&gt; の中の同じ名前のプロパティ。
/// </summary>
public static class VectorColors
{
    /// <summary>置き換えられる色の数 (パラメータ color1〜color8)。</summary>
    public const int Slots = 8;

    public static string ParamKey(int index) => "color" + (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>key が「色 n」のパラメータなら 0 始まりの番号、違えば -1。</summary>
    public static int SlotOf(string key) =>
        key.StartsWith("color", StringComparison.Ordinal) && int.TryParse(key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Slots ? n - 1 : -1;

    /// <summary>Lottie に出てくる色 (#RRGGBB) を、出てきた順に重複なく並べる。読めなければ空。</summary>
    public static List<string> LottiePalette(string json)
    {
        var result = new List<string>();
        var root = Parse(json);
        if (root == null) return result;
        Walk(root, hex => { if (!result.Contains(hex)) result.Add(hex); return null; });
        return result;
    }

    /// <summary>map (元の色 → 新しい色。どちらも #RRGGBB) のとおりに色を置き換えた JSON を返す。透明度はそのまま。</summary>
    public static string RecolorLottie(string json, IReadOnlyDictionary<string, string> map)
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

    private static bool IsSvg(string path) => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    private static bool IsLottie(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>ファイルのパレット。ベクター画像でない・読めないときは空。</summary>
    public static IReadOnlyList<string> PaletteOfFile(string path)
    {
        if (path.Length == 0 || !(IsSvg(path) || IsLottie(path)) || !File.Exists(path)) return Array.Empty<string>();
        try
        {
            var time = File.GetLastWriteTimeUtc(path);
            lock (Cache)
                if (Cache.TryGetValue(path, out var hit) && hit.Time == time) return hit.Palette;
            var text = File.ReadAllText(path);
            var palette = IsSvg(path) ? SvgPalette(text) : LottiePalette(text);
            lock (Cache)
            {
                if (Cache.Count > 64) Cache.Clear();
                Cache[path] = (time, palette);
            }
            return palette;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>ファイルを読んで、色を置き換えた中身 (SVG / JSON の文字列) を返す。</summary>
    public static string RecolorFile(string path, IReadOnlyDictionary<string, string> map)
    {
        var text = File.ReadAllText(path);
        return IsSvg(path) ? RecolorSvg(text, map) : RecolorLottie(text, map);
    }

    // ---------------- SVG ----------------

    // 色を持つ属性 (fill="...") と CSS のプロパティ (fill: ...)。"stop-color" の中の "color" に当たらないよう、前が名前の文字でないことを確かめる
    private static readonly Regex SvgColor = new(
        @"(?<![\w-])(?:fill|stroke|stop-color|flood-color|lighting-color|color)(?:\s*=\s*(?<q>[""'])(?<a>[^""']*)\k<q>|\s*:\s*(?<c>[^;""'}<>!]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RgbFunc = new(
        @"^rgba?\(\s*(?<r>[\d.]+%?)\s*[,\s]\s*(?<g>[\d.]+%?)\s*[,\s]\s*(?<b>[\d.]+%?)\s*(?:[,/]\s*(?<a>[\d.]+%?)\s*)?\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>SVG に出てくる色 (#RRGGBB) を、出てきた順に重複なく並べる。none・currentColor・url(...) は数えない。</summary>
    public static List<string> SvgPalette(string svg)
    {
        var result = new List<string>();
        foreach (Match m in SvgColor.Matches(svg))
            if (ParseSvgColor(ValueOf(m).Value.Trim()) is { } c && !result.Contains(c.Hex)) result.Add(c.Hex);
        return result;
    }

    /// <summary>map のとおりに色を置き換えた SVG を返す。透明度 (#RRGGBBAA・rgba の 4 つ目) はそのまま。</summary>
    public static string RecolorSvg(string svg, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return svg;
        return SvgColor.Replace(svg, m =>
        {
            var g = ValueOf(m);
            var raw = g.Value;
            var trimmed = raw.Trim();
            if (ParseSvgColor(trimmed) is not { } c || !map.TryGetValue(c.Hex, out var to) || Normalize(to) is not { } n) return m.Value;
            string replaced = c.Alpha == null ? n.ToLowerInvariant()
                : c.Alpha.StartsWith('#') ? n.ToLowerInvariant() + c.Alpha[1..]
                : $"rgba({Convert.ToInt32(n[1..3], 16)},{Convert.ToInt32(n[3..5], 16)},{Convert.ToInt32(n[5..7], 16)},{c.Alpha})";
            // 値の前後の空白は残す
            int lead = raw.Length - raw.TrimStart().Length;
            var newValue = raw[..lead] + replaced + raw[(lead + trimmed.Length)..];
            return m.Value[..(g.Index - m.Index)] + newValue + m.Value[(g.Index - m.Index + g.Length)..];
        });
    }

    private static Group ValueOf(Match m) => m.Groups["a"].Success ? m.Groups["a"] : m.Groups["c"];

    /// <summary>
    /// SVG の色の値を読む。Alpha は透明度の部分 (#RRGGBBAA なら "#AA"、rgba なら 4 つ目の値)。色でなければ null。
    /// </summary>
    private static (string Hex, string? Alpha)? ParseSvgColor(string v)
    {
        if (v.Length == 0) return null;
        if (v.StartsWith('#'))
        {
            var h = v[1..];
            if (!h.All(Uri.IsHexDigit)) return null;
            if (h.Length == 3) return ("#" + string.Concat(h.Select(c => new string(c, 2))).ToUpperInvariant(), null);
            if (h.Length == 4) return ("#" + string.Concat(h[..3].Select(c => new string(c, 2))).ToUpperInvariant(), "#" + new string(h[3], 2));
            if (h.Length == 6) return ("#" + h.ToUpperInvariant(), null);
            if (h.Length == 8) return ("#" + h[..6].ToUpperInvariant(), "#" + h[6..]);
            return null;
        }
        var rgb = RgbFunc.Match(v);
        if (rgb.Success)
        {
            int Channel(string s) => s.EndsWith('%')
                ? (int)Math.Clamp(Math.Round(double.Parse(s[..^1], CultureInfo.InvariantCulture) * 2.55), 0, 255)
                : (int)Math.Clamp(Math.Round(double.Parse(s, CultureInfo.InvariantCulture)), 0, 255);
            return ($"#{Channel(rgb.Groups["r"].Value):X2}{Channel(rgb.Groups["g"].Value):X2}{Channel(rgb.Groups["b"].Value):X2}",
                    rgb.Groups["a"].Success ? rgb.Groups["a"].Value : null);
        }
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Equals("transparent", StringComparison.OrdinalIgnoreCase)
            || v.Equals("currentColor", StringComparison.OrdinalIgnoreCase) || !v.All(char.IsAsciiLetter)) return null;
        // 色の名前 (red など)
        var named = System.Drawing.Color.FromName(v);
        return named.IsKnownColor && !named.IsSystemColor ? ($"#{named.R:X2}{named.G:X2}{named.B:X2}", null) : null;
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
