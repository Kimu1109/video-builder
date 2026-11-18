using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

public sealed record PluginInfo(string Id, string Name, string Version, string Directory, string? Error)
{
    public bool Loaded => Error == null;
}

/// <summary>
/// プラグインの読み込み。plugins フォルダの下の各フォルダに plugin.json とアセンブリを置く。
/// plugin.json: { "id": "...", "name": "...", "version": "1.0.0", "apiVersion": 2, "assembly": "MyPlugin.dll" }
/// apiVersion は PluginApi.MinimumVersion 〜 PluginApi.Version の範囲なら読み込む (古いプラグインもそのまま動く)。
/// </summary>
public static class PluginLoader
{
    private sealed class Manifest
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public int ApiVersion { get; set; }
        public string Assembly { get; set; } = "";
    }

    /// <summary>プラグインごとに読み込み先を分け、プラグインが持ち込んだライブラリ同士がぶつからないようにする。</summary>
    private sealed class PluginContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public PluginContext(string mainAssembly) : base(Path.GetFileNameWithoutExtension(mainAssembly)) => _resolver = new AssemblyDependencyResolver(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            // アプリ本体と共有するもの (API・Core・SkiaSharp など) は、本体側を使う。そうしないと型が別物になる
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }

    private sealed class Host(string pluginId) : IPluginHost
    {
        public int ApiVersion => PluginApi.Version;

        private static void Check(EffectDefinition definition, string id, EffectCategory category)
        {
            if (definition.Category != category) throw new ArgumentException($"Category は {category} にしてください");
            if (definition.Id != id) throw new ArgumentException("definition.Id と effect.Id が一致しません");
        }

        public void RegisterFilter(EffectDefinition definition, IVideoEffect effect)
        {
            Check(definition, effect.Id, EffectCategory.Filter);
            EffectRegistry.Register(definition);
            Effects.Register(effect);
        }

        public void RegisterPixelEffect(EffectDefinition definition, IPixelEffect effect)
        {
            Check(definition, effect.Id, EffectCategory.Filter);
            EffectRegistry.Register(definition);
            Effects.RegisterPixel(effect);
        }

        public void RegisterColorMatrixEffect(EffectDefinition definition, IColorMatrixEffect effect)
        {
            Check(definition, effect.Id, EffectCategory.Filter);
            EffectRegistry.Register(definition);
            Effects.RegisterMatrix(effect);
        }

        public void RegisterAudioEffect(EffectDefinition definition, IAudioEffect effect)
        {
            Check(definition, effect.Id, EffectCategory.AudioFilter);
            EffectRegistry.Register(definition);
            AudioEffects.Register(effect);
        }

        public void Log(string message) => PluginLoader.Log(pluginId, message);

        public void RegisterMediaObject(EffectDefinition definition, IVideoSource source)
        {
            if (definition.Category != EffectCategory.VideoSource) throw new ArgumentException("Category は VideoSource にしてください");
            EffectRegistry.Register(definition);
            Effects.RegisterSource(definition.Id, source);
        }

        public void RegisterInterpolator(IInterpolator interpolator) => Interpolators.Register(interpolator);
    }

    private static readonly List<PluginInfo> LoadedList = new();
    private static readonly List<string> LogLines = new();

    /// <summary>プラグインが IPluginHost.Log で書いたもの (新しい 500 行まで)。</summary>
    public static IReadOnlyList<string> LogMessages { get { lock (LogLines) return LogLines.ToList(); } }

    internal static void Log(string pluginId, string message)
    {
        string line = $"[{pluginId}] {message}";
        lock (LogLines)
        {
            LogLines.Add(line);
            if (LogLines.Count > 500) LogLines.RemoveRange(0, LogLines.Count - 500);
        }
        Console.Error.WriteLine("plugin " + line);
    }
    public static IReadOnlyList<PluginInfo> Loaded { get { lock (LoadedList) return LoadedList.ToList(); } }

    /// <summary>root の下の各フォルダからプラグインを読み込む。失敗したものは Error に理由を入れて返す。</summary>
    public static List<PluginInfo> LoadAll(string root)
    {
        var result = new List<PluginInfo>();
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (File.Exists(manifestPath)) result.Add(Load(dir, manifestPath));
        }
        return result;
    }

    public static PluginInfo Load(string dir, string manifestPath)
    {
        Manifest m;
        try
        {
            m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("plugin.json が空です");
        }
        catch (Exception ex) { return Add(new PluginInfo(Path.GetFileName(dir), Path.GetFileName(dir), "", dir, "plugin.json を読めません: " + ex.Message)); }

        string name = m.Name.Length > 0 ? m.Name : m.Id;
        PluginInfo Fail(string error) => Add(new PluginInfo(m.Id, name, m.Version, dir, error));
        if (m.Id.Length == 0 || m.Assembly.Length == 0) return Fail("plugin.json に id と assembly が必要です");
        if (m.ApiVersion < PluginApi.MinimumVersion || m.ApiVersion > PluginApi.Version)
            return Fail($"API バージョンが合いません (プラグイン: {m.ApiVersion}, アプリ: {PluginApi.MinimumVersion}〜{PluginApi.Version})");
        lock (LoadedList) if (LoadedList.Any(p => p.Id == m.Id && p.Loaded)) return Fail("同じ ID のプラグインがすでに読み込まれています");
        // フォルダの外のファイルを指す指定は受け付けない
        string assemblyPath = Path.GetFullPath(Path.Combine(dir, m.Assembly));
        if (!assemblyPath.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(assemblyPath))
            return Fail($"アセンブリが見つかりません: {m.Assembly}");
        try
        {
            var asm = new PluginContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);
            var types = asm.GetTypes().Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }).ToList();
            if (types.Count == 0) return Fail("IPlugin を実装したクラスがありません");
            var host = new Host(m.Id);
            foreach (var t in types) ((IPlugin)Activator.CreateInstance(t)!).Initialize(host);
        }
        catch (ReflectionTypeLoadException ex) { return Fail("読み込みに失敗: " + (ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message)); }
        catch (Exception ex) { return Fail("読み込みに失敗: " + (ex.InnerException?.Message ?? ex.Message)); }
        return Add(new PluginInfo(m.Id, name, m.Version, dir, null));
    }

    private static PluginInfo Add(PluginInfo info)
    {
        lock (LoadedList) LoadedList.Add(info);
        return info;
    }
}
