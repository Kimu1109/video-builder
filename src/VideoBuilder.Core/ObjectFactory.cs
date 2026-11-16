namespace VideoBuilder.Core;

public static class ObjectFactory
{
    /// <summary>メディアオブジェクト、またはフィルタオブジェクトを作る。</summary>
    public static TimelineObject Create(string definitionId, int layer, int start, int length)
    {
        var def = EffectRegistry.Get(definitionId);
        var o = new TimelineObject { Layer = layer, Start = start, End = start + Math.Max(1, length) - 1 };
        o.Effects.Add(new Effect(def));
        switch (def.Category)
        {
            case EffectCategory.VideoSource:
                o.Effects.Add(new Effect(EffectRegistry.Get(EffectRegistry.StandardDraw)));
                break;
            case EffectCategory.AudioSource:
                o.Effects.Add(new Effect(EffectRegistry.Get(EffectRegistry.StandardPlay)));
                break;
        }
        return o;
    }

    public static string? DefinitionForFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (EffectRegistry.VideoExtensions.Contains(ext)) return EffectRegistry.Video;
        if (EffectRegistry.ImageExtensions.Contains(ext)) return EffectRegistry.Image;
        if (EffectRegistry.AudioExtensions.Contains(ext)) return EffectRegistry.Audio;
        return null;
    }

    /// <summary>フィルタをオブジェクトに追加する (標準描画の手前に入る)。</summary>
    public static Effect AddFilter(TimelineObject obj, string definitionId)
    {
        var e = new Effect(EffectRegistry.Get(definitionId), obj.Sections);
        obj.Effects.Insert(obj.FilterInsertIndex, e);
        return e;
    }
}
