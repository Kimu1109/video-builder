using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App.Tests;

/// <summary>テスト用のテンプレートのライブラリ (一時フォルダ)。</summary>
public sealed class TemplateFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"vb-tpl-{Guid.NewGuid():N}");
    public TemplateStore Store { get; }

    public TemplateFixture()
    {
        Store = new TemplateStore(Path.Combine(Root, "templates"), Path.Combine(Root, "assets"));
    }

    /// <summary>
    /// 字幕のテンプレート: 帯 (図形、レイヤー 0) とテキスト (レイヤー 1)。長さ 60 フレーム。
    /// textLabel が null でなければテキストの「テキスト」を、colorLabel が null でなければ帯の色を公開する。
    /// 置き方の既定は「複数のオブジェクト」(字幕ではシーンとして置かれることを確かめるため)。
    /// </summary>
    public string Add(string name, string? textLabel = "セリフ", string? colorLabel = "帯の色", string category = "字幕")
    {
        var session = new EditorSession();
        var band = session.AddObject(EffectRegistry.Shape, 0, 0, 60)!;
        band.Main.Values["color"] = "#203040";
        var text = session.AddObject(EffectRegistry.Text, 1, 0, 60)!;
        text.Main.Values["text"] = "見本の文字";
        var exposed = new List<ExposedParam>();
        if (textLabel != null) exposed.Add(new ExposedParam { ObjectId = text.Id, Effect = 0, Key = "text", Label = textLabel });
        if (colorLabel != null) exposed.Add(new ExposedParam { ObjectId = band.Id, Effect = 0, Key = "color", Label = colorLabel });
        var pkg = TemplatePackage.Create(session.Project, new[] { band, text }, new TemplateOptions
        {
            Name = name, Category = category, IntroSeconds = 0.2, OutroSeconds = 0.2,
            InsertAs = TemplateInsertMode.Objects, Exposed = exposed,
        });
        Store.Save(pkg);
        return TemplatePackage.ReadMeta(Store.PathFor(name))!.Id;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}
