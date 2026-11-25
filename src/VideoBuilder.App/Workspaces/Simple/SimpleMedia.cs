using SkiaSharp;
using VideoBuilder.App.Services;
using VideoBuilder.Core;
using VideoBuilder.Editing;
using VideoBuilder.Media;

namespace VideoBuilder.App.Workspaces.Simple;

/// <summary>シンプル編集で、ファイルからクリップ・音楽を作る (素材の大きさと長さを調べる)。</summary>
public static class SimpleMedia
{
    /// <summary>写真の表示時間の初期値 (秒)。</summary>
    public const double PhotoSeconds = 5;

    public static string[] VisualExtensions => EffectRegistry.VideoExtensions.Concat(EffectRegistry.ImageExtensions).ToArray();
    public static string[] AllExtensions => VisualExtensions.Concat(EffectRegistry.AudioExtensions).ToArray();

    // 素材があるかどうかの結果を少しのあいだ覚えておく (ストーリーボードを描くたびにファイルを調べないように)
    private static readonly Dictionary<string, (bool Exists, long Tick)> ExistsCache = new();
    private const long ExistsCacheMs = 3000;

    /// <summary>素材のファイルがあるか (3 秒まで前の結果を使う)。空のパスは「ある」とみなす。</summary>
    public static bool Exists(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        long now = Environment.TickCount64;
        lock (ExistsCache)
        {
            if (ExistsCache.TryGetValue(path, out var c) && now - c.Tick < ExistsCacheMs) return c.Exists;
            bool exists = File.Exists(path) || Directory.Exists(path);
            ExistsCache[path] = (exists, now);
            return exists;
        }
    }

    /// <summary>覚えていた結果を捨てる (付け替えたあとなど)。</summary>
    public static void ForgetExists() { lock (ExistsCache) ExistsCache.Clear(); }

    public sealed record Result(StoryClip? Clip, MusicClip? Music, MediaInfo? Info, string? Error);

    /// <summary>ファイルを調べて、映像ならクリップ、音声だけなら音楽を作る。</summary>
    public static Result FromFile(string path)
    {
        string name = L.Name(Path.GetFileName(path));
        if (Directory.Exists(path)) return new(null, null, null, $"フォルダは置けません: {name}");
        var defId = ObjectFactory.DefinitionForFile(path);
        if (defId is null or EffectRegistry.Vector) return new(null, null, null, $"シンプル編集では使えないファイル形式です: {name}");

        if (defId == EffectRegistry.Image)
        {
            var (w, h, rotation) = ImageSize(path);
            return new(new StoryClip { Kind = StoryClipKind.Photo, File = path, SourceWidth = w, SourceHeight = h, Rotation = rotation, Duration = PhotoSeconds }, null, null, null);
        }

        if (!FFmpegLoader.EnsureLoaded()) return new(null, null, null, "FFmpeg が見つからないため、動画・音声を読み込めません。\n" + FFmpegLoader.Error);
        var info = MediaInfo.Probe(path);
        if (info == null) return new(null, null, null, $"ファイルを開けません: {name}");
        if (info.HasVideo && defId == EffectRegistry.Video)
        {
            var clip = new StoryClip
            {
                Kind = StoryClipKind.Video, File = path, SourceWidth = info.Width, SourceHeight = info.Height,
                SourceSeconds = Math.Round(info.Duration, 3), HasAudio = info.HasAudio,
            };
            if (clip.SourceSeconds <= 0) clip.TrimEnd = PhotoSeconds;   // 長さが分からない動画
            return new(clip, null, info, null);
        }
        if (!info.HasAudio) return new(null, null, null, $"映像も音声も含まれていません: {name}");
        return new(null, new MusicClip { File = path, SourceSeconds = Math.Round(info.Duration, 3), TrimEnd = info.Duration > 0 ? 0 : 60 }, info, null);
    }

    /// <summary>画像の大きさと、EXIF の向きを直す回転 (時計回りの度)。</summary>
    private static (int, int, int) ImageSize(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return (0, 0, 0);
            // 描画 (MediaCache) は EXIF の向きを使わず、元の縦横のまま描く。スマートフォンの写真などが横倒しにならないよう、
            // 向きの分だけクリップの「回転」を付けておく (左右の反転は扱わない)
            return (codec.Info.Width, codec.Info.Height, RotationFor(codec.EncodedOrigin));
        }
        catch { return (0, 0, 0); }
    }

    public static int RotationFor(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.BottomRight or SKEncodedOrigin.BottomLeft => 180,
        SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftTop => 90,
        SKEncodedOrigin.LeftBottom or SKEncodedOrigin.RightBottom => 270,
        _ => 0,
    };

    /// <summary>
    /// ファイルをストーリーボードに入れる。映像は index の位置 (null なら選んでいるクリップの後ろ) に、
    /// 音声は musicAt 秒 (null なら現在位置) から鳴らす。
    /// 入れられなかったファイルの理由を返す。
    /// </summary>
    public static List<string> AddFiles(StoryboardEditor editor, IEnumerable<string> paths, int? index = null, double? musicAt = null)
    {
        var errors = new List<string>();
        var clips = new List<StoryClip>();
        MediaInfo? firstVideo = null;
        var musics = new List<MusicClip>();
        foreach (var path in paths)
        {
            var r = FromFile(path);
            if (r.Error != null) { errors.Add(r.Error); continue; }
            if (r.Clip != null)
            {
                clips.Add(r.Clip);
                if (r.Clip.Kind == StoryClipKind.Video && firstVideo == null) firstVideo = r.Info;
                if (r.Info != null && Svc.Settings.AutoProxy) Svc.Proxies.EnqueueIfNeeded(path, r.Info);
            }
            if (r.Music != null) musics.Add(r.Music);
        }

        // まだ素材のないプロジェクト (タイトルだけはあってもよい) に最初の動画を入れるときは、
        // 画像サイズとフレームレートを動画に合わせる (詳細編集と同じ)
        var project = editor.Project;
        bool adopt = firstVideo is { Width: > 0, FrameRate: > 0 } && editor.CanEdit
                     && !(editor.Board?.Clips.Any(c => c.UsesFile) ?? false) && project.Scenes.Skip(1).All(s => s.Objects.Count == 0);
        editor.AddClips(clips, index, prepare: adopt ? p =>
        {
            p.Settings.Width = firstVideo!.Width; p.Settings.Height = firstVideo.Height;
            p.Settings.FrameRate = Rational.FromDouble(firstVideo.FrameRate).ToString();
        } : null);

        // 音楽は、落とした位置 (なければ現在位置) から鳴らす
        double at = Math.Round(Math.Max(0, musicAt ?? project.FrameToSeconds(editor.Session.CurrentFrame)), 3);
        foreach (var m in musics)
        {
            m.StartSeconds = at;
            editor.AddMusic(m);
        }
        return errors;
    }
}
