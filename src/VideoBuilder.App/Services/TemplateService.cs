using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Rendering;

namespace VideoBuilder.App.Services;

/// <summary>テンプレートの挿入・サムネイル作り。画面 (ダイアログ) からも、ファイルのドロップからも使う。</summary>
public static class TemplateService
{
    public const int ThumbnailSide = 320;

    /// <summary>
    /// テンプレートを挿入する。mode が null なら、テンプレートに保存された既定の置き方。
    /// 置けなければエラー、置けたが足りないものがあれば注意を返す (どちらも null なら問題なし)。
    /// </summary>
    public static (bool Inserted, string? Message) Insert(TemplateEntry entry, int layer, int frame, TemplateInsertMode? mode = null)
    {
        var session = Svc.Session;
        TemplatePackage pkg;
        try { pkg = Svc.Templates.Load(entry); }
        catch (Exception ex) { return (false, $"テンプレートを読み込めませんでした。\n{ex.Message}"); }
        var useMode = mode ?? pkg.Meta.InsertAs;
        TemplateInstance inst;
        try { inst = pkg.Instantiate(session.Project, Svc.Templates.AssetDirectory, useMode); }
        catch (Exception ex) { return (false, $"テンプレートの素材を取り出せませんでした。\n{ex.Message}"); }
        if (session.InsertTemplate(inst, layer, frame) == 0) return (false, "空いているレイヤーがありません");

        var notes = new List<string>();
        var fonts = MissingFonts(pkg.Meta.Fonts);
        if (fonts.Count > 0) notes.Add("このテンプレートで使っているフォントがありません (既定のフォントで表示します):\n" + string.Join("\n", fonts.Select(f => "・" + f)));
        if (inst.MissingPlugins.Count > 0) notes.Add("このテンプレートで使っているプラグインがありません (そのエフェクトはかかりません):\n" + string.Join("\n", inst.MissingPlugins.Select(f => "・" + f)));
        // 1 つずつ訳してからつなぐ (つないだ文字列は対応表の型紙に合わない)
        return (true, notes.Count > 0 ? string.Join("\n\n", notes.Select(L.T)) : null);
    }

    public static List<string> MissingFonts(IEnumerable<string> fonts)
    {
        var installed = new HashSet<string>(Generators.FontFamilies, StringComparer.OrdinalIgnoreCase);
        return fonts.Where(f => f.Length > 0 && !installed.Contains(f)).ToList();
    }

    /// <summary>.vbtemplate のファイルをライブラリに取り込んで、挿入する (ファイルのドロップ用)。</summary>
    public static string? InsertFile(string path, int layer, int frame)
    {
        TemplateEntry? entry;
        try
        {
            var full = Path.GetFullPath(path);
            // ライブラリの中のファイルなら、取り込み直さない
            var stored = Path.GetDirectoryName(full) == Path.GetFullPath(Svc.Templates.Directory) ? full : Svc.Templates.Import(full);
            entry = Svc.Templates.List().FirstOrDefault(e => !e.Legacy && Path.GetFullPath(e.Path) == stored);
        }
        catch (Exception ex) { return $"テンプレートを取り込めませんでした: {Path.GetFileName(path)}\n{ex.Message}"; }
        if (entry == null) return $"テンプレートを取り込めませんでした: {Path.GetFileName(path)}";
        return Insert(entry, layer, frame).Message;
    }

    /// <summary>本体の中間のフレームを描いて、長辺 320px の PNG にする。描けなければ null。</summary>
    public static byte[]? RenderThumbnail(TemplatePackage pkg, ProjectSettings settings)
    {
        try
        {
            var project = pkg.PreviewProject(settings);
            var body = project.Root;
            int frame = Math.Max(0, body.Length / 2);
            using var engine = new RenderEngine();
            using var image = engine.RenderImage(project, body, frame);
            float scale = Math.Min(1f, (float)ThumbnailSide / Math.Max(image.Width, image.Height));
            int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
            using var surface = SKSurface.Create(new SKImageInfo(w, h));
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
                surface.Canvas.DrawImage(image, SKRect.Create(w, h), paint);
            using var snap = surface.Snapshot();
            using var data = snap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception)
        {
            return null;   // サムネイルがなくても保存はできる
        }
    }
}
