namespace VideoBuilder.Core;

/// <summary>素材ファイルを指している 1 か所 (エフェクトのファイルの欄、ストーリーボードのクリップなど)。</summary>
public sealed class MediaRef
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    public MediaRef(Func<string> get, Action<string> set) { _get = get; _set = set; }
    public string Path { get => _get(); set => _set(value); }
}

/// <summary>プロジェクトが参照している素材ファイルの調査・付け替え・収集。</summary>
public static class ProjectMedia
{
    /// <summary>ファイルを指しているパラメータを列挙する。</summary>
    public static IEnumerable<(Effect Effect, string Key)> FileParams(Project project)
    {
        foreach (var s in project.Scenes)
        foreach (var o in s.Objects)
        foreach (var e in o.Effects)
        {
            if (!EffectRegistry.Exists(e.DefinitionId)) continue;
            foreach (var d in e.Definition.Params)
                if (d.Kind == ParamKind.File && e.Values.TryGetValue(d.Key, out var v) && v.Length > 0) yield return (e, d.Key);
        }
    }

    /// <summary>
    /// ファイルを指している所をすべて列挙する。シーンのほか、ストーリーボード (シンプル編集) のクリップと音楽も含む。
    /// シーンとストーリーボードを一緒に書き換えるので、付け替えたあとも、シーンはストーリーボードから作ったままになる。
    /// </summary>
    public static IEnumerable<MediaRef> References(Project project)
    {
        foreach (var (e, key) in FileParams(project))
            yield return new MediaRef(() => e.Values[key], v => e.Values[key] = v);
        if (project.Storyboard is not { } board) yield break;
        foreach (var c in board.Clips.Where(c => c.UsesFile && c.File.Length > 0))
            yield return new MediaRef(() => c.File, v => c.File = v);
        foreach (var m in board.Music.Where(m => m.File.Length > 0))
            yield return new MediaRef(() => m.File, v => m.File = v);
    }

    public static List<string> UsedFiles(Project project) =>
        References(project).Select(r => r.Path).Distinct().ToList();

    public static List<string> FindMissing(Project project) =>
        UsedFiles(project).Where(f => !File.Exists(f)).ToList();

    /// <summary>oldPath を参照している箇所をすべて newPath に付け替える。変えた数を返す。</summary>
    public static int Relink(Project project, string oldPath, string newPath)
    {
        int n = 0;
        foreach (var r in References(project).ToList())
            if (r.Path == oldPath) { r.Path = newPath; n++; }
        return n;
    }

    /// <summary>見つからない素材を、folder の中 (下位フォルダも含む) から同じファイル名で探して付け替える。</summary>
    public static int RelinkFromFolder(Project project, string folder)
    {
        var missing = FindMissing(project);
        if (missing.Count == 0 || !Directory.Exists(folder)) return 0;
        var wanted = missing.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            var name = Path.GetFileName(file);
            if (wanted.Contains(name)) found.TryAdd(name, file);
            if (found.Count == wanted.Count) break;
        }
        int fixedCount = 0;
        foreach (var old in missing)
            if (found.TryGetValue(Path.GetFileName(old), out var now)) { Relink(project, old, now); fixedCount++; }
        return fixedCount;
    }

    /// <summary>保存先から見た相対パスを記録する。</summary>
    public static void UpdateRelativePaths(Project project, string projectPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
        project.MediaPaths = UsedFiles(project).Where(Path.IsPathRooted).ToDictionary(f => f, f => Path.GetRelativePath(dir, f));
    }

    /// <summary>絶対パスにファイルがなく、相対パスの位置にあるなら (フォルダごと移動した場合)、そちらに付け替える。</summary>
    public static void ResolveMovedFiles(Project project, string projectPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
        foreach (var old in FindMissing(project))
        {
            if (!project.MediaPaths.TryGetValue(old, out var rel)) continue;
            var candidate = Path.GetFullPath(Path.Combine(dir, rel));
            if (File.Exists(candidate)) Relink(project, old, candidate);
        }
    }

    /// <summary>参照している素材を targetDir にコピーして、参照先をコピーに付け替える。コピーした数を返す。</summary>
    public static int Collect(Project project, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        string full = Path.GetFullPath(targetDir);
        int copied = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in UsedFiles(project))
        {
            if (!File.Exists(file)) continue;
            if (Path.GetDirectoryName(Path.GetFullPath(file)) == full) { used.Add(Path.GetFileName(file)); continue; }   // すでに収集済み
            string name = Path.GetFileName(file), stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
            // 別のフォルダにある同名ファイルを上書きしないよう、番号を付ける
            for (int i = 2; used.Contains(name) || (File.Exists(Path.Combine(full, name)) && !SameFile(file, Path.Combine(full, name))); i++)
                name = $"{stem}_{i}{ext}";
            used.Add(name);
            string dest = Path.Combine(full, name);
            if (!File.Exists(dest)) { File.Copy(file, dest); copied++; }
            Relink(project, file, dest);
        }
        return copied;
    }

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
    }
}
