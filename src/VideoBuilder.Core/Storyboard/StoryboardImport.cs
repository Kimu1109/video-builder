namespace VideoBuilder.Core;

/// <summary>
/// 詳細編集で作ったシーンから、ストーリーボードを作る (シンプル編集に取り込む)。
/// 置けるのは「並べた動画・写真」と「音楽」だけなので、重なり・レイヤー・エフェクト・キーフレームなどは失われる。
/// </summary>
public static class StoryboardImport
{
    public static Storyboard FromScene(Project project, Scene scene)
    {
        var board = new Storyboard();
        var visible = scene.Objects.Where(o => o.Enabled && scene.GetLayer(o.Layer).Visible).ToList();

        // 映像: 動画・画像を、始まりの順に並べる (同じ時刻に重なっているものは、後ろのものだけを使う)
        var visuals = visible.Where(o => o.Main.DefinitionId is EffectRegistry.Video or EffectRegistry.Image)
            .OrderBy(o => o.Start).ThenBy(o => o.Layer).ToList();
        var linkedAudio = new HashSet<string>();
        foreach (var o in visuals)
        {
            double seconds = project.FrameToSeconds(o.Length);
            var clip = new StoryClip { File = o.Main.GetString("file") };
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
                    clip.Volume = audio.Effects[^1].Eval("volume", audio.Bounds, audio.Start);
                    linkedAudio.Add(audio.Id);
                }
            }
            else
            {
                clip.Kind = StoryClipKind.Photo;
                clip.Duration = Math.Round(seconds, 3);
            }
            board.Clips.Add(clip);
        }

        // 音楽: 映像と連結していない音声
        foreach (var a in visible.Where(o => o.Main.DefinitionId == EffectRegistry.Audio && !linkedAudio.Contains(o.Id)).OrderBy(o => o.Start))
        {
            double start = Math.Round(a.Main.Eval("position", a.Bounds, a.Start) + project.FrameToSeconds(a.SourceOffsetFrames), 3);
            board.Music.Add(new MusicClip
            {
                File = a.Main.GetString("file"),
                StartSeconds = Math.Round(project.FrameToSeconds(a.Start), 3),
                TrimStart = start,
                TrimEnd = Math.Round(start + project.FrameToSeconds(a.Length), 3),
                Volume = a.Effects[^1].Eval("volume", a.Bounds, a.Start),
                FadeIn = 0, FadeOut = 0,
            });
        }
        board.FitMusicToVideo = false;   // 元の長さのまま鳴らす
        return board;
    }
}
