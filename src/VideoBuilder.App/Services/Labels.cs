using VideoBuilder.Core;

namespace VideoBuilder.App.Services;

/// <summary>オブジェクト・シーンの表示名 (タイムライン・プロパティ・テンプレートのダイアログで共通)。</summary>
public static class Labels
{
    /// <summary>
    /// オブジェクトの表示名 (表示する言語にしたもの)。テキストの本文・ファイル名・シーンの名前など、
    /// 利用者が付けたものは訳さない。TextBlock に出すときは L.Verbatim を使うこと (自動の置き換えで訳されないように)。
    /// </summary>
    public static string Object(TimelineObject o)
    {
        var main = o.Main;
        string DefName() => L.T(main.Definition.Name);
        switch (main.DefinitionId)
        {
            case EffectRegistry.Text:
                var t = main.GetString("text").Split('\n')[0];
                return t.Length > 0 ? t : DefName();
            case EffectRegistry.Video or EffectRegistry.Image or EffectRegistry.Audio or EffectRegistry.Vector:
                var f = main.GetString("file");
                return f.Length > 0 ? Path.GetFileName(f) : DefName();
            case EffectRegistry.SceneRef:
                var sc = Svc.Session.Project.FindScene(main.GetString("scene"));
                return sc != null ? L.Format("シーン [{0}]", Scene(sc)) : DefName();
            case EffectRegistry.Shape:
                return L.T($"図形 [{EffectRegistry.ShapeTypes[Math.Clamp(main.GetInt("type"), 0, EffectRegistry.ShapeTypes.Length - 1)]}]");
        }
        return o.Definition.Category == EffectCategory.Filter ? L.T($"調整レイヤー [{main.Definition.Name}]") : DefName();
    }

    /// <summary>シーンの表示名。利用者が付けた名前は訳さない。アプリが付けた既定の名前 (メイン) だけは訳す。</summary>
    public static string Scene(Core.Scene scene) => scene.Name == Core.Scene.DefaultName ? L.T(scene.Name) : scene.Name;

}
