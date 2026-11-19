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
        try { inst = pkg.Instantiate(session.Project, Svc.Templates.AssetDirectory, useMode, Svc.Settings.FitTemplateResolution); }
        catch (Exception ex) { return (false, $"テンプレートの素材を取り出せませんでした。\n{ex.Message}"); }
        if (session.InsertTemplate(inst, layer, frame) == 0) return (false, "空いているレイヤーがありません");

        var notes = new List<string>();
        var fonts = MissingFonts(pkg.Meta.Fonts);
        if (fonts.Count > 0) notes.Add("このテンプレートで使っているフォントがありません (既定のフォントで表示します):\n" + string.Join("\n", fonts.Select(f => "・" + f)));
        if (inst.MissingPlugins.Count > 0) notes.Add("このテンプレートで使っているプラグインがありません (そのエフェクトはかかりません):\n" + string.Join("\n", inst.MissingPlugins.Select(f => "・" + f)));
        // 1 つずつ訳してからつなぐ (つないだ文字列は対応表の型紙に合わない)
        return (true, notes.Count > 0 ? string.Join("\n\n", notes.Select(L.T)) : null);
    }

    // 新しい版があるかの確認は、プロパティの表示のたびに呼ばれるので、少しの間 (2 秒) 覚えておく
    private static readonly Dictionary<string, (long Time, TemplateEntry? Entry, int Version)> NewerCache = new();

    /// <summary>src (挿入したときの版) より新しい版がライブラリにあれば、その一覧の行。なければ null。</summary>
    public static TemplateEntry? NewerVersion(TemplateSource src)
    {
        long now = Environment.TickCount64;
        if (!NewerCache.TryGetValue(src.Id, out var hit) || now - hit.Time > 2000)
        {
            var latest = Svc.Templates.FindLatest(src.Id);
            hit = (now, latest?.Entry, latest?.Meta.Version ?? 0);
            NewerCache[src.Id] = hit;
        }
        return hit.Entry != null && hit.Version > src.Version ? hit.Entry : null;
    }

    /// <summary>覚えている「新しい版」の確認をやり直す (保存したあとなど)。</summary>
    public static void ForgetVersions() => NewerCache.Clear();

    /// <summary>シーンオブジェクトを、元のテンプレートのいちばん新しい版にする。更新した数か、エラーを返す。</summary>
    public static (int Count, string? Error) Update(IReadOnlyList<TimelineObject> objects)
    {
        var src = objects.Select(o => Svc.Session.TemplateOf(o)).FirstOrDefault(s => s != null);
        if (src == null) return (0, null);
        var latest = Svc.Templates.FindLatest(src.Id);
        if (latest == null) return (0, $"元のテンプレート「{L.Name(src.Name)}」がライブラリにありません");
        TemplatePackage pkg;
        try { pkg = Svc.Templates.Load(latest.Value.Entry); }
        catch (Exception ex) { return (0, $"テンプレートを読み込めませんでした。\n{ex.Message}"); }
        int n = Svc.Session.UpdateTemplateInstances(objects, pkg, Svc.Templates.AssetDirectory, Svc.Settings.FitTemplateResolution);
        return (n, null);
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
        catch (Exception ex) { return $"テンプレートを取り込めませんでした: {L.Name(Path.GetFileName(path))}\n{ex.Message}"; }
        if (entry == null) return $"テンプレートを取り込めませんでした: {L.Name(Path.GetFileName(path))}";
        return Insert(entry, layer, frame).Message;
    }

    public const int PreviewSide = 160;

    /// <summary>
    /// 本体を等間隔に TemplatePackage.PreviewFrames 枚描き、長辺 160px にして横に並べた PNG にする (ライブラリのプレビュー再生用)。
    /// 描けなければ null。
    /// </summary>
    public static byte[]? RenderPreview(TemplatePackage pkg, ProjectSettings settings)
    {
        try
        {
            var project = pkg.PreviewProject(settings);
            var body = project.Root;
            int n = TemplatePackage.PreviewFrames, length = Math.Max(1, body.Length);
            float scale = (float)PreviewSide / Math.Max(settings.Width, settings.Height);
            int w = Math.Max(1, (int)(settings.Width * scale)), h = Math.Max(1, (int)(settings.Height * scale));
            using var engine = new RenderEngine();
            using var strip = SKSurface.Create(new SKImageInfo(w * n, h));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
            for (int i = 0; i < n; i++)
            {
                int frame = (int)((long)i * (length - 1) / Math.Max(1, n - 1));
                using var image = engine.RenderImage(project, body, frame);
                strip.Canvas.DrawImage(image, SKRect.Create(i * w, 0, w, h), paint);
            }
            using var snap = strip.Snapshot();
            using var data = snap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
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
