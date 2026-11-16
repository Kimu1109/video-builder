using VideoBuilder.Core;

namespace VideoBuilder.Editing;

/// <summary>
/// エイリアス (オブジェクトの設定を名前を付けて保存したもの)。1 つのエイリアスが 1 つのファイルになる。
/// </summary>
public sealed class AliasStore
{
    public const string Extension = ".vbalias";
    private readonly string _directory;

    public AliasStore(string directory) => _directory = directory;

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_directory, "*" + Extension)
            .Select(Path.GetFileNameWithoutExtension).Where(n => n != null).Select(n => n!)
            .OrderBy(n => n, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>ファイル名に使えない文字を置き換える。</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return cleaned.Length == 0 ? "alias" : cleaned;
    }

    private string PathOf(string name) => Path.Combine(_directory, SafeName(name) + Extension);

    public bool Exists(string name) => File.Exists(PathOf(name));

    /// <summary>オブジェクトを、先頭を 0 フレーム・いちばん上を 0 レイヤーにそろえて保存する。</summary>
    public void Save(string name, IReadOnlyList<TimelineObject> objects)
    {
        if (objects.Count == 0) return;
        Directory.CreateDirectory(_directory);
        int minLayer = objects.Min(o => o.Layer), minStart = objects.Min(o => o.Start);
        var copies = objects.Select(o =>
        {
            var c = o.Clone(newId: true);
            int len = c.Length;
            c.Layer = o.Layer - minLayer;
            c.SetRange(o.Start - minStart, o.Start - minStart + len - 1);
            return c;
        }).ToList();
        File.WriteAllText(PathOf(name), ProjectSerializer.ObjectsToJson(copies));
    }

    public List<TimelineObject> Load(string name) => ProjectSerializer.ObjectsFromJson(File.ReadAllText(PathOf(name)));

    public void Delete(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path)) File.Delete(path);
    }
}
