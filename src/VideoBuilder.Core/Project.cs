using System.Text.Json.Serialization;

namespace VideoBuilder.Core;

public sealed class ProjectSettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    /// <summary>"30" や "30000/1001" 形式。</summary>
    public string FrameRate { get; set; } = "30";
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;

    [JsonIgnore] public Rational Fps => Rational.Parse(FrameRate);

    public ProjectSettings Clone() => (ProjectSettings)MemberwiseClone();
}

public enum GridMode { None, Time, Bpm }

/// <summary>タイムラインのグリッド。BPM グリッドは曲のテンポに合わせて線を引く。</summary>
public sealed class GridSettings
{
    public GridMode Mode { get; set; } = GridMode.Time;
    public double Bpm { get; set; } = 120;
    /// <summary>1 小節の拍数。</summary>
    public int Beats { get; set; } = 4;
    /// <summary>1 拍目の位置 (秒)。</summary>
    public double OffsetSeconds { get; set; }
    public GridSettings Clone() => (GridSettings)MemberwiseClone();
}

public sealed class LayerInfo
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public LayerInfo Clone() => (LayerInfo)MemberwiseClone();
}

/// <summary>
/// テンプレートとして外から編集できるようにした項目。シーンオブジェクトのプロパティに、参照先のシーンのこの項目が並ぶ。
/// </summary>
public sealed class ExposedParam
{
    /// <summary>シーン内のオブジェクトの ID。</summary>
    public string ObjectId { get; set; } = "";
    /// <summary>オブジェクトの何番目のエフェクトか。</summary>
    public int Effect { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>グループ名 (プロパティで小見出しを付けてまとめる)。空ならグループなし。</summary>
    public string Group { get; set; } = "";

    public ExposedParam Clone() => (ExposedParam)MemberwiseClone();

    /// <summary>
    /// 同じグループの項目を、そのグループが最初に出てくる位置にまとめる。
    /// グループの順はそれぞれの最初の項目の順、グループの中の順は元の並びのまま。
    /// </summary>
    public static List<ExposedParam> GroupTogether(IEnumerable<ExposedParam> items)
    {
        var list = items.ToList();
        var groups = list.Select(x => x.Group).Distinct().ToList();
        return groups.SelectMany(g => list.Where(x => x.Group == g)).ToList();
    }

    public bool SameTarget(ExposedParam other) => ObjectId == other.ObjectId && Effect == other.Effect && Key == other.Key;

    /// <summary>公開元のエフェクトとパラメータ定義。消えていれば null。</summary>
    public (TimelineObject Object, Effect Effect, ParamDefinition Param)? Resolve(Scene scene)
    {
        var o = scene.Find(ObjectId);
        if (o == null || Effect < 0 || Effect >= o.Effects.Count) return null;
        var e = o.Effects[Effect];
        var d = EffectRegistry.Exists(e.DefinitionId) ? e.Definition.Find(Key) : null;
        return d == null ? null : (o, e, d);
    }
}

/// <summary>挿入したテンプレートの元 (テンプレートの id・名前・版)。新しい版が出たときに更新するために覚えておく。</summary>
public sealed class TemplateSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public TemplateSource Clone() => (TemplateSource)MemberwiseClone();
}

public sealed class Scene
{
    public const int LayerCount = 100;

    public string Id { get; set; } = "root";
    /// <summary>新しいプロジェクトのメインのシーンの名前 (アプリが付けた名前なので、表示するときは訳す)。</summary>
    public const string DefaultName = "メイン";

    public string Name { get; set; } = DefaultName;
    public List<TimelineObject> Objects { get; set; } = new();
    /// <summary>既定値と違うレイヤーだけを保持する (キー = レイヤー番号, 0 始まり)。</summary>
    public Dictionary<int, LayerInfo> Layers { get; set; } = new();
    /// <summary>出力範囲 (フレーム, 両端を含む)。null なら先頭 / 末尾まで。</summary>
    public int? RangeStart { get; set; }
    public int? RangeEnd { get; set; }
    /// <summary>テンプレートとして公開する項目。ないときは null (保存しない)。</summary>
    public List<ExposedParam>? Exposed { get; set; }
    /// <summary>テンプレートを挿入してできたシーンなら、その元。ないときは null。</summary>
    public TemplateSource? Template { get; set; }

    public LayerInfo GetLayer(int index) => Layers.TryGetValue(index, out var l) ? l : DefaultLayer;
    private static readonly LayerInfo DefaultLayer = new();

    public LayerInfo EditLayer(int index)
    {
        if (!Layers.TryGetValue(index, out var l)) Layers[index] = l = new LayerInfo();
        return l;
    }

    /// <summary>最後のオブジェクトの終了フレーム + 1。空なら 0。</summary>
    [JsonIgnore] public int Length => Objects.Count == 0 ? 0 : Objects.Max(o => o.End) + 1;

    public TimelineObject? Find(string id) => Objects.FirstOrDefault(o => o.Id == id);

    public IEnumerable<TimelineObject> ObjectsAt(int frame) =>
        Objects.Where(o => o.Contains(frame)).OrderBy(o => o.Layer);

    /// <summary>レイヤー上で [start, end] が他のオブジェクトと重ならないか。</summary>
    public bool IsFree(int layer, int start, int end, ICollection<string>? ignoreIds = null)
    {
        if (layer < 0 || layer >= LayerCount || start < 0) return false;
        foreach (var o in Objects)
        {
            if (o.Layer != layer) continue;
            if (ignoreIds != null && ignoreIds.Contains(o.Id)) continue;
            if (start <= o.End && end >= o.Start) return false;
        }
        return true;
    }

    /// <summary>layer の start 位置から置ける最大の終了フレーム (次のオブジェクトの手前)。</summary>
    public int MaxEndFrom(int layer, int start, int desiredEnd, ICollection<string>? ignoreIds = null)
    {
        int end = desiredEnd;
        foreach (var o in Objects)
        {
            if (o.Layer != layer || (ignoreIds != null && ignoreIds.Contains(o.Id))) continue;
            if (o.Start > start && o.Start <= end) end = o.Start - 1;
        }
        return end;
    }

    /// <summary>
    /// 複製を作る。previous (以前に作った複製) に内容が同じオブジェクトがあれば、複製せずにそれを使い回す。
    /// 取り消し用の履歴を、変わったオブジェクトのぶんだけのメモリで持つために使う。
    /// 戻り値と previous は、どちらも書き換えてはいけない (オブジェクトを共有しているため)。
    /// </summary>
    public Scene CloneSharing(Scene? previous)
    {
        if (previous == null) return Clone();
        var s = new Scene { Id = Id, Name = Name, RangeStart = RangeStart, RangeEnd = RangeEnd };
        var old = new Dictionary<string, TimelineObject>(previous.Objects.Count);
        foreach (var o in previous.Objects) old.TryAdd(o.Id, o);
        s.Objects.Capacity = Objects.Count;
        foreach (var o in Objects)
            s.Objects.Add(old.TryGetValue(o.Id, out var prev) && TimelineObject.SameContent(o, prev) ? prev : o.Clone());
        foreach (var (k, v) in Layers) s.Layers[k] = v.Clone();
        s.Exposed = Exposed?.Select(x => x.Clone()).ToList();
        s.Template = Template?.Clone();
        return s;
    }

    public Scene Clone()
    {
        var s = new Scene { Id = Id, Name = Name, RangeStart = RangeStart, RangeEnd = RangeEnd };
        foreach (var o in Objects) s.Objects.Add(o.Clone());
        foreach (var (k, v) in Layers) s.Layers[k] = v.Clone();
        s.Exposed = Exposed?.Select(x => x.Clone()).ToList();
        s.Template = Template?.Clone();
        return s;
    }
}

public sealed class Project
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public ProjectSettings Settings { get; set; } = new();
    public List<Scene> Scenes { get; set; } = new() { new Scene() };
    public GridSettings Grid { get; set; } = new();
    /// <summary>素材の絶対パス → プロジェクトファイルから見た相対パス。保存時に作り、フォルダごと移動したときの復元に使う。</summary>
    public Dictionary<string, string> MediaPaths { get; set; } = new();

    public Scene Root => Scenes[0];
    public Scene? FindScene(string id) => Scenes.FirstOrDefault(s => s.Id == id);

    public double FrameToSeconds(int frame)
    {
        var fps = Settings.Fps;
        return fps.Num == 0 ? 0 : (double)frame * fps.Den / fps.Num;
    }

    public long FrameToSample(long frame)
    {
        var fps = Settings.Fps;
        return fps.Num == 0 ? 0 : frame * Settings.SampleRate * fps.Den / fps.Num;
    }

    public int SecondsToFrames(double seconds) => (int)Math.Round(seconds * Settings.Fps.Value);

    /// <summary>
    /// 複製を作る。previous に内容が同じオブジェクトがあれば使い回す (Scene.CloneSharing を参照)。
    /// 戻り値と previous は書き換えてはいけない。
    /// </summary>
    public Project CloneSharing(Project? previous)
    {
        if (previous == null) return Clone();
        var p = new Project { FormatVersion = FormatVersion, Settings = Settings.Clone(), Scenes = new List<Scene>(Scenes.Count), Grid = Grid.Clone(), MediaPaths = new Dictionary<string, string>(MediaPaths) };
        foreach (var s in Scenes) p.Scenes.Add(s.CloneSharing(previous.FindScene(s.Id)));
        return p;
    }

    /// <summary>レンダリングスレッドに渡すための複製。</summary>
    public Project Clone()
    {
        var p = new Project { FormatVersion = FormatVersion, Settings = Settings.Clone(), Scenes = new List<Scene>(), Grid = Grid.Clone(), MediaPaths = new Dictionary<string, string>(MediaPaths) };
        foreach (var s in Scenes) p.Scenes.Add(s.Clone());
        return p;
    }

    public string FormatTime(int frame)
    {
        double sec = FrameToSeconds(frame);
        int h = (int)(sec / 3600), m = (int)(sec / 60) % 60;
        double s = sec - h * 3600 - m * 60;
        return $"{h:00}:{m:00}:{s:00.00}";
    }
}
