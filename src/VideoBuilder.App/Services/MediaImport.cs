using VideoBuilder.Core;
using VideoBuilder.Media;

namespace VideoBuilder.App.Services;

public static class MediaImport
{
    /// <summary>ファイルをタイムラインに追加する。追加できなければ理由を返す。</summary>
    public static string? AddFile(string path, int layer, int frame)
    {
        if (path.EndsWith(TemplatePackage.Extension, StringComparison.OrdinalIgnoreCase)) return TemplateService.InsertFile(path, layer, frame);
        var session = Svc.Session;
        var project = session.Project;
        var defId = ObjectFactory.DefinitionForFile(path);
        if (defId == null) return $"対応していないファイル形式です: {Path.GetFileName(path)}";

        if (defId is EffectRegistry.Image or EffectRegistry.Vector)
        {
            double seconds = defId == EffectRegistry.Vector ? LottieSeconds(path) ?? Svc.Settings.DefaultObjectSeconds : Svc.Settings.DefaultObjectSeconds;
            int len = Math.Max(1, project.SecondsToFrames(seconds));
            return session.AddMediaFile(path, layer, frame, len, false) == null ? "空いているレイヤーがありません" : null;
        }

        if (!FFmpegLoader.EnsureLoaded()) return "FFmpeg が見つからないため、動画・音声を読み込めません。\n" + FFmpegLoader.Error;
        var info = MediaInfo.Probe(path);
        if (info == null) return $"ファイルを開けません: {Path.GetFileName(path)}";
        if (defId == EffectRegistry.Video && !info.HasVideo)
        {
            if (!info.HasAudio) return $"映像も音声も含まれていません: {Path.GetFileName(path)}";
            defId = EffectRegistry.Audio;
        }
        if (defId == EffectRegistry.Audio && !info.HasAudio) return $"音声が含まれていません: {Path.GetFileName(path)}";

        // 最初の動画を読み込むときは、プロジェクトの画像サイズとフレームレートを動画に合わせる (AviUtl と同じ)
        bool adopt = defId == EffectRegistry.Video && project.Scenes.All(s => s.Objects.Count == 0) && info.Width > 0 && info.FrameRate > 0;
        string? error = null;
        session.Edit("ファイルの追加", () =>
        {
            if (adopt)
            {
                project.Settings.Width = info.Width; project.Settings.Height = info.Height;
                project.Settings.FrameRate = Rational.FromDouble(info.FrameRate).ToString();
            }
            int len = Math.Max(1, project.SecondsToFrames(info.Duration > 0 ? info.Duration : Svc.Settings.DefaultObjectSeconds));
            var created = defId == EffectRegistry.Audio
                ? session.AddObject(EffectRegistry.Audio, layer, frame, len, o => o.Main.Values["file"] = path)
                : session.AddMediaFile(path, layer, frame, len, info.HasAudio);
            if (created == null) error = "空いているレイヤーがありません";
        });
        // 重い動画 (4K・H.265 など) は、裏でプロキシを作っておく
        if (error == null && defId == EffectRegistry.Video && Svc.Settings.AutoProxy && ProxyStore.IsHeavy(info)) Svc.Proxies.Enqueue(path);
        return error;
    }

    /// <summary>Lottie (JSON) のアニメーションの長さ (秒)。Lottie でなければ null。</summary>
    public static double? LottieSeconds(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = System.Text.Json.JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (!root.TryGetProperty("fr", out var fr) || !root.TryGetProperty("op", out var op)) return null;
            double ip = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetDouble() : 0;
            double rate = fr.GetDouble(), frames = op.GetDouble() - ip;
            return rate > 0 && frames > 0 ? frames / rate : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }
}
