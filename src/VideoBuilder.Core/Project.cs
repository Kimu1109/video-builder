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

public sealed class LayerInfo
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public LayerInfo Clone() => (LayerInfo)MemberwiseClone();
}

public sealed class Scene
{
    public const int LayerCount = 100;

    public string Id { get; set; } = "root";
    public string Name { get; set; } = "Root";
    public List<TimelineObject> Objects { get; set; } = new();
    /// <summary>既定値と違うレイヤーだけを保持する (キー = レイヤー番号, 0 始まり)。</summary>
    public Dictionary<int, LayerInfo> Layers { get; set; } = new();

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

    public Scene Clone()
    {
        var s = new Scene { Id = Id, Name = Name };
        foreach (var o in Objects) s.Objects.Add(o.Clone());
        foreach (var (k, v) in Layers) s.Layers[k] = v.Clone();
        return s;
    }
}

public sealed class Project
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public ProjectSettings Settings { get; set; } = new();
    public List<Scene> Scenes { get; set; } = new() { new Scene() };

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

    /// <summary>レンダリングスレッドに渡すための複製。</summary>
    public Project Clone()
    {
        var p = new Project { FormatVersion = FormatVersion, Settings = Settings.Clone(), Scenes = new List<Scene>() };
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
