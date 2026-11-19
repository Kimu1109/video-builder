using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace VideoBuilder.Core;

/// <summary>テンプレートを挿入したときの置き方。</summary>
public enum TemplateInsertMode
{
    /// <summary>シーンオブジェクト 1 つとして置く (中身はシーンに入る)。</summary>
    Scene,
    /// <summary>中のオブジェクトをそのまま並べる。</summary>
    Objects,
}

/// <summary>テンプレートの meta.json。</summary>
public sealed class TemplateMeta
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>作ったときのプロジェクトの解像度とフレームレート (参考。挿入時に変換はしない)。</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public string FrameRate { get; set; } = "30";
    /// <summary>本体のシーンの長さ (フレーム)。</summary>
    public int Length { get; set; }
    /// <summary>長さを変えたときに伸縮しない先頭 / 末尾の長さ (秒)。</summary>
    public double IntroSeconds { get; set; }
    public double OutroSeconds { get; set; }
    public TemplateInsertMode InsertAs { get; set; } = TemplateInsertMode.Scene;
    /// <summary>使っているフォント (既定のフォントは含めない)。</summary>
    public List<string> Fonts { get; set; } = new();
    /// <summary>使っている、組み込みでないエフェクトの ID (プラグイン)。</summary>
    public List<string> Plugins { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>テンプレートを作るときの指定。</summary>
public sealed class TemplateOptions
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public TemplateInsertMode InsertAs { get; set; } = TemplateInsertMode.Scene;
    public double IntroSeconds { get; set; }
    public double OutroSeconds { get; set; }
    /// <summary>
    /// 公開する項目。ObjectId は保存元のオブジェクトの ID で指定する。
    /// null なら、シーンオブジェクト 1 つから作るときはそのシーンの公開項目を引き継ぎ、それ以外は公開しない。
    /// </summary>
    public List<ExposedParam>? Exposed { get; set; }
}

/// <summary>テンプレートを挿入するために作ったもの。プロジェクトに足すシーンと、タイムラインに置くオブジェクト。</summary>
public sealed class TemplateInstance
{
    public List<Scene> Scenes { get; } = new();
    public List<TimelineObject> Objects { get; } = new();
    /// <summary>テンプレートが使っているのに、読み込まれていないエフェクト (プラグイン)。</summary>
    public List<string> MissingPlugins { get; } = new();
}

/// <summary>
/// テンプレート (.vbtemplate)。ZIP の中に meta.json・content.json (シーンの並び。先頭が本体)・thumbnail.png・assets/ を入れる。
/// content.json の中で同梱した素材を指す値は "asset:&lt;assets の中のファイル名&gt;"。
/// </summary>
public sealed class TemplatePackage
{
    public const string Extension = ".vbtemplate";
    public const string AssetPrefix = "asset:";

    public TemplateMeta Meta { get; set; } = new();
    /// <summary>先頭が本体、2 つめ以降は本体から参照しているシーン。</summary>
    public List<Scene> Scenes { get; set; } = new();
    public byte[]? Thumbnail { get; set; }

    /// <summary>同梱する素材 (assets の中のファイル名 → 読み出し元)。</summary>
    private readonly Dictionary<string, Func<Stream>> _assets = new();
    /// <summary>作ったときの素材の元のパス (assets の中のファイル名 → パス)。読み込んだテンプレートでは空。</summary>
    private readonly Dictionary<string, string> _assetSources = new();
    public IReadOnlyCollection<string> AssetNames => _assets.Keys;

    public Scene Body => Scenes[0];

    private sealed class Content
    {
        public List<Scene> Scenes { get; set; } = new();
    }

    // ---------------- 作る ----------------

    /// <summary>
    /// 選んだオブジェクトからテンプレートを作る。オブジェクトは先頭を 0 フレーム・いちばん上を 0 レイヤーにそろえる。
    /// 選んだのがシーンオブジェクト 1 つだけなら、その参照先のシーンを本体にする。
    /// </summary>
    public static TemplatePackage Create(Project project, IReadOnlyList<TimelineObject> objects, TemplateOptions options)
    {
        if (objects.Count == 0) throw new ArgumentException("オブジェクトがありません", nameof(objects));
        var pkg = new TemplatePackage();
        Scene body;
        double intro = options.IntroSeconds, outro = options.OutroSeconds;
        var single = objects.Count == 1 && objects[0].Main.DefinitionId == EffectRegistry.SceneRef
            ? project.FindScene(objects[0].Main.GetString("scene")) : null;
        if (single != null)
        {
            body = single.Clone();
            if (options.Exposed != null) body.Exposed = options.Exposed.Select(x => x.Clone()).ToList();
        }
        else
        {
            body = new Scene { Id = "template", Name = options.Name };
            int minLayer = objects.Min(o => o.Layer), minStart = objects.Min(o => o.Start);
            foreach (var src in objects)
            {
                var c = src.Clone();
                int len = c.Length;
                c.Layer = src.Layer - minLayer;
                c.SetRange(src.Start - minStart, src.Start - minStart + len - 1);
                body.Objects.Add(c);
            }
            body.Exposed = options.Exposed?.Where(x => body.Find(x.ObjectId) != null).Select(x => x.Clone()).ToList();
        }
        if (body.Exposed != null)
        {
            body.Exposed.RemoveAll(x => x.Resolve(body) == null);
            if (body.Exposed.Count == 0) body.Exposed = null;
        }
        body.RangeStart = body.RangeEnd = null;
        pkg.Scenes.Add(body);

        // 本体から参照しているシーンを、入れ子の奥までたどって集める
        for (int i = 0; i < pkg.Scenes.Count; i++)
        {
            foreach (var o in pkg.Scenes[i].Objects)
            {
                if (o.Main.DefinitionId != EffectRegistry.SceneRef) continue;
                var id = o.Main.GetString("scene");
                if (pkg.Scenes.Any(s => s.Id == id)) continue;
                var nested = project.FindScene(id);
                if (nested != null) pkg.Scenes.Add(nested.Clone());
            }
        }

        // 素材を同梱する
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (effect, key) in FileParams(pkg.Scenes))
        {
            var path = effect.Values[key];
            if (path.StartsWith(AssetPrefix, StringComparison.Ordinal) || !File.Exists(path)) continue;
            if (!byPath.TryGetValue(path, out var name))
            {
                name = HashOf(path)[..8] + "_" + Path.GetFileName(path);
                byPath[path] = name;
                var source = path;
                pkg._assets[name] = () => File.OpenRead(source);
                pkg._assetSources[name] = source;
            }
            effect.Values[key] = AssetPrefix + name;
        }

        pkg.Meta = new TemplateMeta
        {
            Name = options.Name, Category = options.Category, Description = options.Description,
            Width = project.Settings.Width, Height = project.Settings.Height, FrameRate = project.Settings.FrameRate,
            Length = Math.Max(1, body.Length), IntroSeconds = Math.Max(0, intro), OutroSeconds = Math.Max(0, outro),
            InsertAs = options.InsertAs,
            Fonts = UsedFonts(pkg.Scenes), Plugins = UsedPlugins(pkg.Scenes),
        };
        return pkg;
    }

    private static IEnumerable<(Effect Effect, string Key)> Params(IEnumerable<Scene> scenes, ParamKind kind)
    {
        foreach (var s in scenes)
        foreach (var o in s.Objects)
        foreach (var e in o.Effects)
        {
            if (!EffectRegistry.Exists(e.DefinitionId)) continue;
            foreach (var d in e.Definition.Params)
                if (d.Kind == kind && e.Values.TryGetValue(d.Key, out var v) && v.Length > 0) yield return (e, d.Key);
        }
    }

    private static IEnumerable<(Effect Effect, string Key)> FileParams(IEnumerable<Scene> scenes) => Params(scenes, ParamKind.File).ToList();

    private static List<string> UsedFonts(IEnumerable<Scene> scenes) =>
        Params(scenes, ParamKind.Font).Select(f => f.Effect.Values[f.Key]).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();

    private static List<string> UsedPlugins(IEnumerable<Scene> scenes) =>
        scenes.SelectMany(s => s.Objects).SelectMany(o => o.Effects).Select(e => e.DefinitionId)
            .Where(id => !EffectRegistry.IsBuiltin(id)).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();

    private static string HashOf(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// 本体を描くためのプロジェクト (先頭のシーンが本体)。サムネイル用。
    /// 同梱した素材は、作ったばかりのテンプレートなら元のファイルを指す (読み込んだものでは描かれない)。
    /// </summary>
    public Project PreviewProject(ProjectSettings settings)
    {
        var p = new Project { Settings = settings.Clone(), Scenes = Scenes.Select(s => s.Clone()).ToList() };
        foreach (var (effect, key) in FileParams(p.Scenes))
        {
            var v = effect.Values[key];
            if (v.StartsWith(AssetPrefix, StringComparison.Ordinal) && _assetSources.TryGetValue(v[AssetPrefix.Length..], out var src))
                effect.Values[key] = src;
        }
        return p;
    }

    // ---------------- 保存・読み込み ----------------

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteText(zip, "meta.json", JsonSerializer.Serialize(Meta, ProjectSerializer.Options));
            WriteText(zip, "content.json", JsonSerializer.Serialize(new Content { Scenes = Scenes }, ProjectSerializer.Options));
            if (Thumbnail != null)
            {
                using var s = zip.CreateEntry("thumbnail.png", CompressionLevel.NoCompression).Open();
                s.Write(Thumbnail);
            }
            foreach (var (name, open) in _assets)
            {
                // 動画などはもう圧縮されているので、圧縮し直さない
                using var dst = zip.CreateEntry("assets/" + name, CompressionLevel.NoCompression).Open();
                using var src = open();
                src.CopyTo(dst);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static TemplatePackage Load(string path)
    {
        var pkg = new TemplatePackage();
        using (var fs = File.OpenRead(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            pkg.Meta = ReadMeta(zip) ?? throw new InvalidDataException("meta.json が見つかりません");
            if (pkg.Meta.FormatVersion > TemplateMeta.CurrentFormatVersion)
                throw new InvalidDataException($"このテンプレートは新しいバージョン (形式 {pkg.Meta.FormatVersion}) で作られています");
            var entry = zip.GetEntry("content.json") ?? throw new InvalidDataException("content.json が見つかりません");
            string json;
            using (var r = new StreamReader(entry.Open())) json = r.ReadToEnd();
            var content = JsonSerializer.Deserialize<Content>(json, ProjectSerializer.Options) ?? new Content();
            if (content.Scenes.Count == 0) throw new InvalidDataException("テンプレートが空です");
            // プロジェクトと同じ補正 (増えたパラメータを既定値で補うなど) をかける
            var p = new Project { Scenes = content.Scenes };
            pkg.Scenes = ProjectSerializer.FromJson(ProjectSerializer.ToJson(p)).Scenes;
            pkg.Thumbnail = ReadBytes(zip, "thumbnail.png");
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith("assets/", StringComparison.Ordinal) || e.FullName.Length <= 7) continue;
                string name = e.FullName[7..], entryName = e.FullName;
                // ZIP を開きっぱなしにしないよう、使うときに開き直す
                pkg._assets[name] = () => OpenEntry(path, entryName);
            }
        }
        return pkg;
    }

    private static Stream OpenEntry(string zipPath, string entryName)
    {
        var zip = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName);
        if (entry == null) { zip.Dispose(); throw new FileNotFoundException(entryName); }
        // 展開したものをメモリに置かず、読み終わったら ZIP も閉じる
        return new OwningStream(entry.Open(), zip);
    }

    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); owner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>meta.json だけを読む (一覧の表示用)。読めなければ null。</summary>
    public static TemplateMeta? ReadMeta(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return ReadMeta(zip);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>サムネイル (PNG) だけを読む。なければ null。</summary>
    public static byte[]? ReadThumbnail(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return ReadBytes(zip, "thumbnail.png");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    private static TemplateMeta? ReadMeta(ZipArchive zip)
    {
        var entry = zip.GetEntry("meta.json");
        if (entry == null) return null;
        using var r = new StreamReader(entry.Open());
        return JsonSerializer.Deserialize<TemplateMeta>(r.ReadToEnd(), ProjectSerializer.Options);
    }

    private static byte[]? ReadBytes(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry == null) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }

    // ---------------- 挿入 ----------------

    /// <summary>
    /// target に挿入するためのシーンとオブジェクトを作る。同梱の素材は assetRoot の下に取り出し、そのパスを参照させる。
    /// シーン・オブジェクトの ID と連結のグループは振り直す。オブジェクトは 0 レイヤー・0 フレームを左上として返す。
    /// </summary>
    public TemplateInstance Instantiate(Project target, string assetRoot, TemplateInsertMode mode)
    {
        var inst = new TemplateInstance();
        var scenes = Scenes.Select(s => s.Clone()).ToList();

        // 素材を取り出す
        var extracted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (effect, key) in FileParams(scenes))
        {
            var value = effect.Values[key];
            if (!value.StartsWith(AssetPrefix, StringComparison.Ordinal)) continue;
            var name = value[AssetPrefix.Length..];
            if (!extracted.TryGetValue(name, out var dest))
            {
                dest = ExtractAsset(name, assetRoot);
                extracted[name] = dest;
            }
            effect.Values[key] = dest;
        }

        // シーンの ID と名前を振り直す
        var usedIds = target.Scenes.Select(s => s.Id).ToHashSet();
        var usedNames = target.Scenes.Select(s => s.Name).ToHashSet();
        var sceneMap = new Dictionary<string, string>();
        for (int i = 0; i < scenes.Count; i++)
        {
            var s = scenes[i];
            string id;
            do { id = "tpl" + Guid.NewGuid().ToString("N")[..8]; } while (usedIds.Contains(id));
            usedIds.Add(id);
            sceneMap[s.Id] = id;
            s.Id = id;
            string baseName = i == 0 && Meta.Name.Length > 0 ? Meta.Name : s.Name, name = baseName;
            for (int n = 2; usedNames.Contains(name); n++) name = $"{baseName} ({n})";
            usedNames.Add(name);
            s.Name = name;
        }

        // オブジェクトの ID・グループ・シーンの参照先を付け替える
        foreach (var s in scenes)
        {
            var idMap = new Dictionary<string, string>();
            var groupMap = new Dictionary<string, string>();
            foreach (var o in s.Objects)
            {
                var newId = Guid.NewGuid().ToString("N")[..12];
                idMap[o.Id] = newId;
                o.Id = newId;
                if (o.GroupId != null)
                {
                    if (!groupMap.TryGetValue(o.GroupId, out var g)) groupMap[o.GroupId] = g = Guid.NewGuid().ToString("N")[..8];
                    o.GroupId = g;
                }
                if (o.Main.DefinitionId == EffectRegistry.SceneRef && sceneMap.TryGetValue(o.Main.GetString("scene"), out var sid))
                    o.Main.Values["scene"] = sid;
            }
            if (s.Exposed == null) continue;
            s.Exposed.RemoveAll(x => !idMap.ContainsKey(x.ObjectId));
            foreach (var x in s.Exposed) x.ObjectId = idMap[x.ObjectId];
        }

        var body = scenes[0];
        if (mode == TemplateInsertMode.Scene)
        {
            inst.Scenes.AddRange(scenes);
            var o = ObjectFactory.Create(EffectRegistry.SceneRef, 0, 0, Math.Max(1, body.Length));
            o.Main.Values["scene"] = body.Id;
            bool stretch = Meta.IntroSeconds > 0 || Meta.OutroSeconds > 0;
            o.Main.Values["stretch"] = stretch ? "1" : "0";
            o.Main.Tracks["intro"] = new AnimatedValue(Meta.IntroSeconds);
            o.Main.Tracks["outro"] = new AnimatedValue(Meta.OutroSeconds);
            inst.Objects.Add(o);
        }
        else
        {
            inst.Scenes.AddRange(scenes.Skip(1));
            inst.Objects.AddRange(body.Objects);
        }
        foreach (var id in Meta.Plugins.Concat(UsedPlugins(scenes)).Distinct())
            if (!EffectRegistry.Exists(id)) inst.MissingPlugins.Add(id);
        return inst;
    }

    /// <summary>素材を assetRoot/&lt;ハッシュ&gt;/&lt;元のファイル名&gt; に取り出す。取り出し済みならそれを使う。</summary>
    private string ExtractAsset(string name, string assetRoot)
    {
        int sep = name.IndexOf('_');
        string hash = sep > 0 ? name[..sep] : "misc", file = sep > 0 ? name[(sep + 1)..] : name;
        // ZIP の中の名前でフォルダの外に出ないようにする
        file = Path.GetFileName(file);
        if (file.Length == 0) file = "asset";
        foreach (var c in Path.GetInvalidFileNameChars()) hash = hash.Replace(c, '_');
        var dir = Path.Combine(assetRoot, hash);
        var dest = Path.Combine(dir, file);
        if (!_assets.TryGetValue(name, out var open)) return dest;   // 同梱されていない (見つからない素材として扱う)
        if (File.Exists(dest)) return dest;
        Directory.CreateDirectory(dir);
        var tmp = dest + ".tmp";
        using (var src = open())
        using (var dst = File.Create(tmp))
            src.CopyTo(dst);
        File.Move(tmp, dest, overwrite: true);
        return dest;
    }
}

/// <summary>
/// スタイル (.vbstyle)。エフェクト 1 つ分の設定。内容を表す値 (ファイル・テキストの本文・シーンの参照先) は含めない。
/// </summary>
public sealed class EffectStyle
{
    public const string Extension = ".vbstyle";

    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = new();
    /// <summary>トラックバーの値。1 区間ぶん (左の値と右の値) と移動方法を持つ。</summary>
    public Dictionary<string, AnimatedValue> Tracks { get; set; } = new();

    /// <summary>スタイルに含めない (内容を表す) パラメータか。</summary>
    public static bool IsContent(EffectDefinition def, ParamDefinition p) =>
        p.Kind == ParamKind.File
        || (def.Id == EffectRegistry.Text && p.Key == "text")
        || (def.Id == EffectRegistry.SceneRef && p.Key == "scene");

    public static EffectStyle From(Effect effect, string name)
    {
        var def = effect.Definition;
        var style = new EffectStyle { Name = name, DefinitionId = effect.DefinitionId };
        foreach (var p in def.Params)
        {
            if (IsContent(def, p)) continue;
            if (p.Kind == ParamKind.Track)
            {
                if (!effect.Tracks.TryGetValue(p.Key, out var v)) continue;
                var c = v.Clone();
                double left = c.Values.Count > 0 ? c.Values[0] : p.Default, right = c.Values.Count > 1 ? c.Values[1] : left;
                c.Values = new List<double> { left, right };
                style.Tracks[p.Key] = c;
            }
            else if (effect.Values.TryGetValue(p.Key, out var s)) style.Values[p.Key] = s;
        }
        return style;
    }

    /// <summary>effect に設定を写す。トラックバーは sections 区間ぶんを同じ値で埋める (動くものは区間ごとに左右の値を繰り返す)。</summary>
    public void ApplyTo(Effect effect, int sections)
    {
        if (effect.DefinitionId != DefinitionId) return;
        var def = effect.Definition;
        foreach (var (k, v) in Values)
        {
            var p = def.Find(k);
            if (p != null && !IsContent(def, p)) effect.Values[k] = v;
        }
        foreach (var (k, v) in Tracks)
        {
            var p = def.Find(k);
            if (p == null || p.Kind != ParamKind.Track) continue;
            var c = v.Clone();
            double left = c.Values.Count > 0 ? c.Values[0] : p.Default, right = c.Values.Count > 1 ? c.Values[1] : left;
            c.Values = Enumerable.Range(0, sections + 1).Select(i => c.IsMoving && i % 2 == 1 ? right : left).ToList();
            effect.Tracks[k] = c;
        }
    }

    /// <summary>
    /// オブジェクトにスタイルを当てる。同じエフェクトがあれば最初の 1 つに、なければ (エフェクトとして追加できるものなら) 追加して当てる。
    /// 当てられたら true。
    /// </summary>
    public bool ApplyTo(TimelineObject obj)
    {
        var target = obj.FindEffect(DefinitionId);
        if (target == null)
        {
            if (!EffectRegistry.Exists(DefinitionId)) return false;
            var def = EffectRegistry.Get(DefinitionId);
            if (!EffectRegistry.FiltersFor(obj).Contains(def)) return false;
            target = ObjectFactory.AddFilter(obj, DefinitionId);
        }
        ApplyTo(target, obj.Sections);
        return true;
    }

    public string ToJson() => JsonSerializer.Serialize(this, ProjectSerializer.Options);

    public static EffectStyle FromJson(string json) =>
        JsonSerializer.Deserialize<EffectStyle>(json, ProjectSerializer.Options) ?? throw new InvalidDataException("スタイルが空です");
}
