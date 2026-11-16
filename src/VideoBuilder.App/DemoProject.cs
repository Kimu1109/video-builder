using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App;

/// <summary>見た目の確認用のサンプル (--demo で起動したときに読み込む)。</summary>
public static class DemoProject
{
    public static void Load(EditorSession session)
    {
        session.NewProject(new ProjectSettings());
        var scene = session.Scene;

        var bg = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 300);
        bg.Main.Values["type"] = "0"; bg.Main.Values["color"] = "#1B2A41";
        scene.Objects.Add(bg);

        var circle = ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 240);
        circle.Main.Values["type"] = "1"; circle.Main.Values["color"] = "#F2A900";
        circle.Main.Tracks["size"] = new AnimatedValue(320);
        circle.AddKeyFrame(120);
        var x = circle.Draw!.Tracks["x"];
        x.Mode = MovementMode.Curve; x.Decel = true;
        x.Values[0] = -600; x.Values[1] = 0; x.Values[2] = 520;
        var sh = ObjectFactory.AddFilter(circle, "filter.shadow");
        sh.Tracks["x"] = new AnimatedValue(16, circle.Sections); sh.Tracks["y"] = new AnimatedValue(16, circle.Sections);
        scene.Objects.Add(circle);

        var title = ObjectFactory.Create(EffectRegistry.Text, 2, 30, 210);
        title.Main.Values["text"] = "video-builder\nAviUtl ライクな動画編集";
        title.Main.Tracks["size"] = new AnimatedValue(96);
        title.Draw!.Tracks["y"] = new AnimatedValue(-40);
        ObjectFactory.AddFilter(title, "filter.border").Tracks["size"] = new AnimatedValue(4);
        ObjectFactory.AddFilter(title, "filter.fade");
        scene.Objects.Add(title);

        var star = ObjectFactory.Create(EffectRegistry.Shape, 3, 90, 150);
        star.Main.Values["type"] = "6"; star.Main.Values["color"] = "#E53935";
        star.Draw!.Tracks["x"] = new AnimatedValue(-640); star.Draw!.Tracks["y"] = new AnimatedValue(300);
        var rot = star.Draw!.Tracks["rotation"]; rot.Mode = MovementMode.Linear; rot.Values[1] = 360;
        scene.Objects.Add(star);

        scene.Objects.Add(ObjectFactory.Create("filter.color", 5, 150, 120));
        session.History.Clear();
        session.CurrentFrame = 100;
        session.Select(title.Id);
        session.NotifyChanged();
    }
}
