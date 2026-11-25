using System.Text.RegularExpressions;

namespace VideoBuilder.Core;

/// <summary>
/// 詳細編集で作ったシーンから、ストーリーボードを作る (シンプル編集に取り込む)。
/// 置けるのは「並べた動画・写真・タイトル」「それに重なる文字 (キャプション)」と「音楽」だけなので、
/// 重なり・レイヤー・エフェクト・キーフレームなどは失われる。
/// <para>
/// 前のストーリーボード (previous) を渡すと、シンプル編集で作ったオブジェクト (ID が <see cref="StoryboardCompiler.IdPrefix"/> で始まるもの) は
/// 元のクリップ・音楽の設定 (キャプション・タイトル・切り替え効果・繰り返しなど) を引き継ぎ、詳細編集で変えた長さ・位置・文字だけを取り込む。
/// </para>
/// </summary>
public static partial class StoryboardImport
{
    public static Storyboard FromScene(Project project, Scene scene, Storyboard? previous = null)
    {
        var board = new Storyboard();
        var visible = scene.Objects.Where(o => o.Enabled && scene.GetLayer(o.Layer).Visible).ToList();

        // 映像: 動画・画像 (とシンプル編集で作ったタイトルの背景) を、始まりの順に並べる
        var visuals = visible.Where(o => o.Main.DefinitionId is EffectRegistry.Video or EffectRegistry.Image || PreviousTitle(o) != null)
            .OrderBy(o => o.Start).ThenBy(o => o.Layer).ToList();
        var linkedAudio = new HashSet<string>();
        var placed = new List<(StoryClip Clip, TimelineObject Object)>();
        foreach (var o in visuals)
        {
            double seconds = project.FrameToSeconds(o.Length);
            var old = OwnClip(o.Id, "v");
            StoryClip clip;
            if (old is { Kind: StoryClipKind.Title } title)
            {
                clip = title.Clone();
                clip.Duration = Math.Round(seconds, 3);
                clip.Background = o.Main.GetString("color") is { Length: > 0 } bg ? bg : clip.Background;
                clip.Text = "";   // 文字は、あとで重なる文字から取り込む
                placed.Add((clip, o));
                board.Clips.Add(clip);
                continue;
            }
            clip = old is { Kind: not StoryClipKind.Title } ? old.Clone() : new StoryClip();
            if (old != null) clip.Caption = "";   // キャプションも、あとで重なる文字から取り込む
            clip.File = o.Main.GetString("file");
            if (o.Main.DefinitionId == EffectRegistry.Video)
            {
                clip.Kind = StoryClipKind.Video;
                double speed = Math.Max(0.01, o.Main.Eval("speed", o.Bounds, o.Start) / 100);
                clip.TrimStart = Math.Round(o.Main.Eval("position", o.Bounds, o.Start) + project.FrameToSeconds(o.SourceOffsetFrames) * speed, 3);
                clip.TrimEnd = Math.Round(clip.TrimStart + seconds * speed, 3);
                clip.Speed = Math.Round(Math.Clamp(speed, StoryClip.MinSpeed, StoryClip.MaxSpeed), 3);
                var audio = o.GroupId == null ? null : visible.FirstOrDefault(a => a.GroupId == o.GroupId && a.Main.DefinitionId == EffectRegistry.Audio);
                if (audio != null)
                {
                    clip.HasAudio = true;
                    clip.Muted = false;
                    clip.Volume = audio.Effects[^1].Eval("volume", audio.Bounds, audio.Start);
                    linkedAudio.Add(audio.Id);
                }
                else if (old is { HasAudio: true }) clip.Muted = true;   // 音声だけ消してあれば、音を消したことにする
            }
            else
            {
                clip.Kind = StoryClipKind.Photo;
                clip.Duration = Math.Round(seconds, 3);
            }
            placed.Add((clip, o));
            board.Clips.Add(clip);
        }

        // 文字: いちばん長く重なっているクリップのキャプション (タイトルなら、その文字) にする
        foreach (var t in visible.Where(o => o.Main.DefinitionId == EffectRegistry.Text).OrderBy(o => o.Start))
        {
            string text = t.Main.GetString("text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var own = OwnClip(t.Id, "t");
            // まだ文字のないクリップのうち、いちばん長く重なっているもの (同じなら後ろ = 手前に見えているもの)
            bool Free(StoryClip c) => (c.Kind == StoryClipKind.Title ? c.Text : c.Caption).Length == 0;
            var target = (own == null ? null : placed.FirstOrDefault(p => p.Clip.Id == own.Id && Free(p.Clip)).Clip)
                         ?? placed.Select(p => (p.Clip, p.Object, Overlap: Math.Min(p.Object.End, t.End) - Math.Max(p.Object.Start, t.Start) + 1))
                             .Where(p => p.Overlap > 0 && Free(p.Clip))
                             .OrderByDescending(p => p.Overlap).ThenByDescending(p => p.Object.Start).Select(p => p.Clip).FirstOrDefault();
            if (target == null) continue;
            if (target.Kind == StoryClipKind.Title) target.Text = text;
            else
            {
                target.Caption = text;
                if (own == null)
                {
                    // 詳細編集で置いた文字は、クリップの中で出ている時間をそのまま使う
                    var span = placed.First(p => p.Clip == target).Object;
                    if (t.Start > span.Start) target.CaptionStart = Math.Round(project.FrameToSeconds(t.Start - span.Start), 3);
                    if (t.End < span.End) target.CaptionSeconds = Math.Round(project.FrameToSeconds(Math.Min(t.End, span.End) - Math.Max(t.Start, span.Start) + 1), 3);
                }
            }
            if (t.Main.GetString("color") is { Length: > 0 } color) target.TextColor = color;
        }

        // 音楽: 映像と連結していない音声。繰り返す音楽の続き (ID の末尾が番号) は、最初の 1 つにまとめる
        foreach (var a in visible.Where(o => o.Main.DefinitionId == EffectRegistry.Audio && !linkedAudio.Contains(o.Id)).OrderBy(o => o.Start))
        {
            var oldMusic = OwnMusic(a.Id, out bool continuation);
            if (continuation) continue;
            double start = Math.Round(a.Main.Eval("position", a.Bounds, a.Start) + project.FrameToSeconds(a.SourceOffsetFrames), 3);
            var music = oldMusic?.Clone() ?? new MusicClip { FadeIn = 0, FadeOut = 0 };
            music.File = a.Main.GetString("file");
            music.StartSeconds = Math.Round(project.FrameToSeconds(a.Start), 3);
            music.TrimStart = start;
            music.Volume = a.Effects[^1].Eval("volume", a.Bounds, a.Start);
            // 繰り返す音楽は、1 回分の長さをそのまま使う (並べたものの長さは、映像の終わりまでで決まる)
            if (oldMusic is not { Loop: true }) music.TrimEnd = Math.Round(start + project.FrameToSeconds(a.Length), 3);
            board.Music.Add(music);
        }
        board.FitMusicToVideo = false;   // 元の長さのまま鳴らす
        if (previous != null)
        {
            board.EndFadeSeconds = previous.EndFadeSeconds;
            board.Background = previous.Background;
        }
        return board;

        StoryClip? OwnClip(string id, string part)
        {
            if (previous == null || !id.StartsWith(StoryboardCompiler.IdPrefix, StringComparison.Ordinal) || !id.EndsWith(part, StringComparison.Ordinal)) return null;
            return previous.FindClip(id[StoryboardCompiler.IdPrefix.Length..^part.Length]);
        }

        StoryClip? PreviousTitle(TimelineObject o) =>
            o.Main.DefinitionId == EffectRegistry.Shape && OwnClip(o.Id, "v") is { Kind: StoryClipKind.Title } t ? t : null;

        MusicClip? OwnMusic(string id, out bool continuation)
        {
            continuation = false;
            if (previous == null || !id.StartsWith(StoryboardCompiler.IdPrefix, StringComparison.Ordinal)) return null;
            var m = MusicId().Match(id[StoryboardCompiler.IdPrefix.Length..]);
            if (!m.Success || previous.FindMusic(m.Groups[1].Value) is not { } music) return null;
            continuation = m.Groups[2].Length > 0 && music.Loop;
            return music;
        }
    }

    /// <summary>音楽のオブジェクトの ID (頭の印を除く): 音楽の ID、"m"、繰り返しの番号 (2 回目から)。</summary>
    [GeneratedRegex("^(.+)m([0-9]*)$")]
    private static partial Regex MusicId();
}
