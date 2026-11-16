using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoBuilder.Core;

/// <summary>
/// プロジェクトファイル (.vbproj) の読み書き。ZIP の中に project.json と meta.json を入れる。
/// </summary>
public static class ProjectSerializer
{
    public const string Extension = ".vbproj";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string ToJson(Project project) => JsonSerializer.Serialize(project, Options);

    public static Project FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<Project>(json, Options) ?? throw new InvalidDataException("project.json が空です");
        if (p.FormatVersion > Project.CurrentFormatVersion)
            throw new InvalidDataException($"このプロジェクトは新しいバージョン (形式 {p.FormatVersion}) で作られています");
        if (p.Scenes.Count == 0) p.Scenes.Add(new Scene());
        foreach (var s in p.Scenes)
        foreach (var o in s.Objects)
        foreach (var e in o.Effects)
        {
            // 新しいバージョンで増えたパラメータを、既定値で補う
            if (EffectRegistry.Exists(e.DefinitionId))
            {
                foreach (var d in e.Definition.Params)
                    if (d.Kind == ParamKind.Track && !e.Tracks.ContainsKey(d.Key)) e.Tracks[d.Key] = new AnimatedValue(d.Default, o.Sections);
            }
            foreach (var v in e.Tracks.Values) v.EnsureSections(o.Sections);
        }
        return p;
    }

    public static void Save(Project project, string path)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "project.json", ToJson(project));
            WriteEntry(zip, "meta.json", JsonSerializer.Serialize(new { app = "video-builder", formatVersion = project.FormatVersion, savedAt = DateTime.UtcNow }, Options));
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static Project Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("project.json") ?? throw new InvalidDataException("project.json が見つかりません");
        using var reader = new StreamReader(entry.Open());
        return FromJson(reader.ReadToEnd());
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }
}
