using VideoBuilder.Core;

namespace VideoBuilder.Editing;

/// <summary>テンプレートの一覧の 1 行。Legacy は旧形式 (.vbalias) のプリセット。</summary>
public sealed record TemplateEntry(string Path, string Name, string Category, string Description, bool Legacy, TemplateInsertMode InsertAs);

/// <summary>
/// テンプレート (.vbtemplate) の置き場所。1 つのテンプレートが 1 つのファイルになる。
/// 旧形式のプリセット (aliases/*.vbalias) も一覧に出し、「展開して置く」テンプレートとして読む。
/// </summary>
public sealed class TemplateStore
{
    public const string LegacyCategory = "プリセット (旧形式)";

    private readonly string _directory;
    private readonly AliasStore? _legacy;
    private readonly string? _legacyDirectory;
    // 一覧のたびに ZIP を開かないよう、更新日時が同じ間は読んだ meta.json を使い回す
    private readonly Dictionary<string, (DateTime Time, TemplateMeta? Meta)> _metaCache = new();

    /// <summary>取り出した素材を置く場所。</summary>
    public string AssetDirectory { get; }
    public string Directory => _directory;

    public TemplateStore(string directory, string assetDirectory, string? legacyDirectory = null)
    {
        _directory = directory;
        AssetDirectory = assetDirectory;
        _legacyDirectory = legacyDirectory;
        if (legacyDirectory != null) _legacy = new AliasStore(legacyDirectory);
    }

    public IReadOnlyList<TemplateEntry> List()
    {
        var result = new List<TemplateEntry>();
        if (System.IO.Directory.Exists(_directory))
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*" + TemplatePackage.Extension))
            {
                var meta = ReadMeta(path);
                if (meta == null) continue;
                var name = meta.Name.Length > 0 ? meta.Name : System.IO.Path.GetFileNameWithoutExtension(path);
                result.Add(new TemplateEntry(path, name, meta.Category, meta.Description, false, meta.InsertAs));
            }
        }
        if (_legacy != null && _legacyDirectory != null)
        {
            foreach (var name in _legacy.List())
                result.Add(new TemplateEntry(System.IO.Path.Combine(_legacyDirectory, AliasStore.SafeName(name) + AliasStore.Extension),
                    name, LegacyCategory, "", true, TemplateInsertMode.Objects));
        }
        return result.OrderBy(e => e.Legacy).ThenBy(e => e.Category, StringComparer.CurrentCulture)
            .ThenBy(e => e.Name, StringComparer.CurrentCulture).ToList();
    }

    public IReadOnlyList<string> Categories() =>
        List().Select(e => e.Category).Where(c => c.Length > 0).Distinct().OrderBy(c => c, StringComparer.CurrentCulture).ToList();

    private TemplateMeta? ReadMeta(string path)
    {
        var time = File.GetLastWriteTimeUtc(path);
        if (_metaCache.TryGetValue(path, out var hit) && hit.Time == time) return hit.Meta;
        var meta = TemplatePackage.ReadMeta(path);
        _metaCache[path] = (time, meta);
        return meta;
    }

    public string PathFor(string name) => System.IO.Path.Combine(_directory, AliasStore.SafeName(name) + TemplatePackage.Extension);

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>テンプレートを保存して、保存先のパスを返す。同じ名前のものは上書きする。</summary>
    public string Save(TemplatePackage package)
    {
        var path = PathFor(package.Meta.Name);
        package.Save(path);
        _metaCache.Remove(path);
        return path;
    }

    public TemplatePackage Load(TemplateEntry entry)
    {
        if (!entry.Legacy) return TemplatePackage.Load(entry.Path);
        // 旧形式: オブジェクトの並びを本体のシーンに入れたテンプレートとして扱う
        var objects = _legacy!.Load(entry.Name);
        var body = new Scene { Id = "template", Name = entry.Name };
        body.Objects.AddRange(objects);
        return new TemplatePackage
        {
            Meta = new TemplateMeta { Name = entry.Name, Category = LegacyCategory, InsertAs = TemplateInsertMode.Objects, Length = Math.Max(1, body.Length) },
            Scenes = { body },
        };
    }

    public byte[]? Thumbnail(TemplateEntry entry) => entry.Legacy ? null : TemplatePackage.ReadThumbnail(entry.Path);

    public void Delete(TemplateEntry entry)
    {
        if (entry.Legacy) { _legacy?.Delete(entry.Name); return; }
        if (File.Exists(entry.Path)) File.Delete(entry.Path);
        _metaCache.Remove(entry.Path);
    }

    /// <summary>ほかの場所の .vbtemplate を取り込む。同じ名前があれば番号を付ける。取り込んだ先のパスを返す。</summary>
    public string Import(string file)
    {
        var meta = TemplatePackage.ReadMeta(file) ?? throw new InvalidDataException("テンプレートのファイルではありません");
        System.IO.Directory.CreateDirectory(_directory);
        var baseName = meta.Name.Length > 0 ? meta.Name : System.IO.Path.GetFileNameWithoutExtension(file);
        var name = baseName;
        for (int n = 2; Exists(name); n++) name = $"{baseName} ({n})";
        var dest = PathFor(name);
        if (name == baseName) File.Copy(file, dest);
        else
        {
            // 名前を変えて取り込む (一覧の名前は meta.json の名前なので、書き直す)
            var pkg = TemplatePackage.Load(file);
            pkg.Meta.Name = name;
            pkg.Save(dest);
        }
        return dest;
    }

    /// <summary>project に挿入するためのシーンとオブジェクトを作る。</summary>
    public TemplateInstance Instantiate(TemplateEntry entry, Project project, TemplateInsertMode mode) =>
        Load(entry).Instantiate(project, AssetDirectory, mode);
}

/// <summary>スタイル (.vbstyle) の置き場所。</summary>
public sealed class StyleStore
{
    private readonly string _directory;

    public StyleStore(string directory) => _directory = directory;

    private string PathFor(string definitionId, string name) =>
        Path.Combine(_directory, AliasStore.SafeName(definitionId), AliasStore.SafeName(name) + EffectStyle.Extension);

    /// <summary>そのエフェクトのスタイルの名前。</summary>
    public IReadOnlyList<string> List(string definitionId)
    {
        var dir = Path.Combine(_directory, AliasStore.SafeName(definitionId));
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*" + EffectStyle.Extension)
            .Select(Path.GetFileNameWithoutExtension).Where(n => n != null).Select(n => n!)
            .OrderBy(n => n, StringComparer.CurrentCulture).ToList();
    }

    public bool Exists(string definitionId, string name) => File.Exists(PathFor(definitionId, name));

    public void Save(EffectStyle style)
    {
        var path = PathFor(style.DefinitionId, style.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, style.ToJson());
    }

    public EffectStyle Load(string definitionId, string name) => EffectStyle.FromJson(File.ReadAllText(PathFor(definitionId, name)));

    public void Delete(string definitionId, string name)
    {
        var path = PathFor(definitionId, name);
        if (File.Exists(path)) File.Delete(path);
    }
}
