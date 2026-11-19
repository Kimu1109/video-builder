using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace VideoBuilder.Core;

/// <summary>ベクター画像のファイル (SVG / Lottie の JSON / dotLottie) の読み込みと、読み込む前の書き換え。</summary>
public static class VectorFiles
{
    public static bool IsSvg(string path) => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    public static bool IsDotLottie(string path) => path.EndsWith(".lottie", StringComparison.OrdinalIgnoreCase);
    public static bool IsLottie(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || IsDotLottie(path);

    /// <summary>
    /// 中身の文字列 (SVG / Lottie の JSON)。dotLottie なら、最初のアニメーションの JSON に画像を埋め込んだもの。
    /// </summary>
    public static string ReadText(string path) => IsDotLottie(path) ? ReadDotLottie(path) : File.ReadAllText(path);

    // ---------------- dotLottie ----------------

    /// <summary>
    /// dotLottie (ZIP) の最初のアニメーションの JSON を取り出す。manifest.json の animations[0].id を使い、
    /// animations/ (版 1) か a/ (版 2) の下から探す。画像 (images/ か i/) は data URI にして JSON に埋め込む。
    /// </summary>
    public static string ReadDotLottie(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string? id = null;
        if (zip.GetEntry("manifest.json") is { } manifestEntry)
        {
            try
            {
                using var r = new StreamReader(manifestEntry.Open());
                id = JsonNode.Parse(r.ReadToEnd())?["animations"]?[0]?["id"]?.GetValue<string>();
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
        var entry = (id != null ? zip.GetEntry($"animations/{id}.json") ?? zip.GetEntry($"a/{id}.json") : null)
            ?? zip.Entries.FirstOrDefault(e => (e.FullName.StartsWith("animations/", StringComparison.Ordinal) || e.FullName.StartsWith("a/", StringComparison.Ordinal))
                                               && e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("dotLottie の中にアニメーションがありません");
        string json;
        using (var r = new StreamReader(entry.Open())) json = r.ReadToEnd();
        return EmbedImages(json, zip);
    }

    private static string EmbedImages(string json, ZipArchive zip)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root?["assets"] is not JsonArray assets) return json;
        bool changed = false;
        foreach (var asset in assets.OfType<JsonObject>())
        {
            if (asset["p"] is not JsonValue pv || !pv.TryGetValue<string>(out var file) || file.StartsWith("data:", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            var img = zip.GetEntry("images/" + name) ?? zip.GetEntry("i/" + name);
            if (img == null) continue;
            using var s = img.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            string mime = Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/png",
            };
            asset["p"] = $"data:{mime};base64,{Convert.ToBase64String(ms.ToArray())}";
            asset["u"] = "";
            asset["e"] = 1;
            changed = true;
        }
        return changed ? root.ToJsonString() : json;
    }

    // ---------------- SVG の部品の非表示 ----------------

    /// <summary>"a, b c" のような id の並びを分ける (カンマ・空白区切り)。</summary>
    public static HashSet<string> ParseIds(string? text) =>
        (text ?? "").Split(new[] { ',', ' ', '\t', '\n', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>SVG の id が ids に入っている要素を表示しないようにする。SVG として読めなければそのまま返す。</summary>
    public static string HideSvgElements(string svg, IReadOnlySet<string> ids)
    {
        if (ids.Count == 0) return svg;
        XDocument doc;
        try { doc = XDocument.Parse(svg, LoadOptions.PreserveWhitespace); }
        catch (XmlException) { return svg; }
        bool changed = false;
        foreach (var el in doc.Descendants())
        {
            if (el.Attribute("id")?.Value is not { } id || !ids.Contains(id)) continue;
            el.SetAttributeValue("display", "none");
            // style の指定は display の属性より強いので、style にも書く
            var style = el.Attribute("style")?.Value;
            el.SetAttributeValue("style", "display:none" + (string.IsNullOrWhiteSpace(style) ? "" : ";" + style));
            changed = true;
        }
        return changed ? doc.Declaration + doc.ToString(SaveOptions.DisableFormatting) : svg;
    }

    /// <summary>SVG の要素の id を並べる (プロパティでの候補の表示用)。</summary>
    public static List<string> SvgIds(string svg)
    {
        try { return XDocument.Parse(svg).Descendants().Select(e => e.Attribute("id")?.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Distinct().ToList(); }
        catch (XmlException) { return new List<string>(); }
    }
}
