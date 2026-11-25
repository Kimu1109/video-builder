using System.Globalization;

namespace VideoBuilder.Core;

/// <summary>ストーリーボードのクリップが、シーンのどこ (フレーム) に置かれるか。</summary>
/// <param name="FadeIn">前のクリップと重なる (切り替わる) フレーム数。先頭のクリップでは、黒からの切り替えの長さ。</param>
/// <param name="FadeOut">次のクリップと重なるフレーム数。</param>
/// <param name="Lane">0 か 1。隣り合うクリップは別のレーン (レイヤー) に置いて重ねる。</param>
public sealed record ClipSpan(StoryClip Clip, int Index, int Start, int End, int FadeIn, int FadeOut, int Lane)
{
    public int Length => End - Start + 1;
    public bool Contains(int frame) => frame >= Start && frame <= End;
}

public sealed record MusicSpan(MusicClip Music, int Start, int End, int Layer)
{
    public int Length => End - Start + 1;
}

/// <summary>ストーリーボード全体の配置。</summary>
public sealed class StoryboardLayout
{
    public List<ClipSpan> Clips { get; } = new();
    public List<MusicSpan> Music { get; } = new();
    /// <summary>映像 (クリップ) の長さ (フレーム)。</summary>
    public int VideoLength { get; internal set; }
    /// <summary>音楽も含めた全体の長さ (フレーム)。</summary>
    public int Length => Math.Max(VideoLength, Music.Count == 0 ? 0 : Music.Max(m => m.End) + 1);

    public ClipSpan? Find(string id) => Clips.FirstOrDefault(c => c.Clip.Id == id);
    public MusicSpan? FindMusic(string id) => Music.FirstOrDefault(m => m.Music.Id == id);

    /// <summary>
    /// frame の位置で見えているクリップ。切り替えの途中 (2 つが重なっている所) では、入ってくる (後ろの) クリップ。
    /// </summary>
    public ClipSpan? ClipAt(int frame)
    {
        ClipSpan? found = null;
        foreach (var c in Clips) if (c.Contains(frame)) found = c;
        return found;
    }
}

/// <summary>
/// ストーリーボード → シーンのオブジェクトへの変換。結果は入力だけで決まり (オブジェクトの ID もクリップの ID から作る)、
/// 同じストーリーボードからは何度でも同じものができる。そのため、シーンが詳細編集で変えられたかどうかを、
/// 作り直したものと比べるだけで調べられる。
/// <para>レイヤーの使い方 (0 始まり):</para>
/// <list type="bullet">
/// <item>0・1: 映像 (動画・写真・タイトルの背景)。隣り合うクリップを交互に置き、切り替えの間だけ重ねる</item>
/// <item>2・3: 文字 (タイトルの文字・キャプション)</item>
/// <item>4・5: 動画の音声</item>
/// <item>6〜9: 音楽</item>
/// </list>
/// 余白の色 (<see cref="Storyboard.Background"/>) を決めたときだけ、レイヤー 0 に背景を置き、ほかはすべて 1 つ上にずらす。
/// </summary>
public static class StoryboardCompiler
{
    public const int VisualLayer = 0, TextLayer = 2, AudioLayer = 4, MusicLayer = 6, MusicLayers = 4;
    /// <summary>作ったオブジェクトの ID の頭に付ける文字。</summary>
    public const string IdPrefix = "sb";

    /// <summary>動き (ズーム・パン) で、どれだけ拡大するか。</summary>
    private const double MotionZoom = 1.15;

    public static StoryboardLayout Layout(Storyboard board, Project project)
    {
        var layout = new StoryboardLayout();
        int end = -1;
        for (int i = 0; i < board.Clips.Count; i++)
        {
            var clip = board.Clips[i];
            int len = Math.Max(2, project.SecondsToFrames(clip.Seconds));
            int fadeIn = 0;
            if (clip.Transition > 0)
            {
                int wanted = Math.Max(1, project.SecondsToFrames(clip.TransitionSeconds));
                // 前後の切り替えが 3 つのクリップにまたがらないよう、どちらのクリップの長さの半分も超えないようにする
                int limit = (len - 1) / 2;
                if (i > 0) limit = Math.Min(limit, (layout.Clips[i - 1].Length - 1) / 2);
                fadeIn = Math.Clamp(wanted, 0, Math.Max(0, limit));
            }
            int overlap = i > 0 ? fadeIn : 0;
            int start = end + 1 - overlap;
            var span = new ClipSpan(clip, i, start, start + len - 1, fadeIn, 0, i % 2);
            if (i > 0 && overlap > 0) layout.Clips[i - 1] = layout.Clips[i - 1] with { FadeOut = overlap };
            layout.Clips.Add(span);
            end = span.End;
        }
        layout.VideoLength = end + 1;

        foreach (var music in board.Music.OrderBy(m => m.StartSeconds).ThenBy(m => board.Music.IndexOf(m)))
        {
            int start = Math.Max(0, project.SecondsToFrames(music.StartSeconds));
            int last = start + Math.Max(1, project.SecondsToFrames(music.Seconds)) - 1;
            // 繰り返す音楽は、映像の終わりまで鳴らす (映像がなければ 1 回だけ)
            if (music.Loop && layout.VideoLength > start) last = layout.VideoLength - 1;
            else if (board.FitMusicToVideo && layout.VideoLength > 0) last = Math.Min(last, layout.VideoLength - 1);
            if (last < start) continue;
            // 重なる音楽は、別のレイヤーに置く
            int layer = -1;
            for (int l = MusicLayer; l < MusicLayer + MusicLayers && layer < 0; l++)
                if (layout.Music.All(m => m.Layer != l || m.End < start || m.Start > last)) layer = l;
            if (layer < 0) continue;
            layout.Music.Add(new MusicSpan(music, start, last, layer));
        }
        return layout;
    }

    /// <summary>ストーリーボードからシーンのオブジェクトを作る。</summary>
    public static List<TimelineObject> Compile(Storyboard board, Project project) => Compile(board, project, Layout(board, project));

    public static List<TimelineObject> Compile(Storyboard board, Project project, StoryboardLayout layout)
    {
        var result = new List<TimelineObject>();
        var visuals = new List<TimelineObject>();
        int W = project.Settings.Width, H = project.Settings.Height;

        // ---- 余白の色 ----
        int off = string.IsNullOrWhiteSpace(board.Background) ? 0 : 1;
        if (off > 0 && layout.VideoLength > 0)
        {
            var bg = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, layout.VideoLength);
            bg.Id = IdPrefix + "bg";
            bg.Main.Values["type"] = "0";
            bg.Main.Values["color"] = board.Background;
            result.Add(bg);
        }

        // ---- 最後に黒へフェードアウト ----
        int endFade = 0;
        if (board.EndFadeSeconds > 0 && layout.Clips.Count > 0)
        {
            var lastSpan = layout.Clips[^1];
            endFade = Math.Clamp(project.SecondsToFrames(board.EndFadeSeconds), 1, Math.Max(1, lastSpan.Length - lastSpan.FadeIn - 1));
        }

        foreach (var span in layout.Clips)
        {
            var clip = span.Clip;
            bool isLast = span.Index == layout.Clips.Count - 1;
            double fadeIn = project.FrameToSeconds(span.FadeIn), fadeOut = project.FrameToSeconds(isLast && endFade > 0 ? endFade : span.FadeOut);

            // ---- 映像 ----
            var v = clip.Kind switch
            {
                StoryClipKind.Video => ObjectFactory.Create(EffectRegistry.Video, off + VisualLayer + span.Lane, span.Start, span.Length),
                StoryClipKind.Photo => ObjectFactory.Create(EffectRegistry.Image, off + VisualLayer + span.Lane, span.Start, span.Length),
                _ => ObjectFactory.Create(EffectRegistry.Shape, off + VisualLayer + span.Lane, span.Start, span.Length),
            };
            v.Id = ObjectId(clip, "v");
            if (clip.Kind == StoryClipKind.Title)
            {
                v.Main.Values["type"] = "0";   // 背景 (画面全体を塗る)
                v.Main.Values["color"] = clip.Background;
            }
            else
            {
                v.Main.Values["file"] = clip.File;
                if (clip.Kind == StoryClipKind.Video)
                {
                    v.Main.Tracks["position"] = new AnimatedValue(Round(clip.TrimStart));
                    if (clip.EffectiveSpeed != 1) v.Main.Tracks["speed"] = new AnimatedValue(Round(clip.EffectiveSpeed * 100));
                }
                ApplyFrame(v, clip, W, H);
            }
            ApplyLook(v, clip.Look);
            if (isLast && endFade > 0) AddFade(v, "filter.fade", 0, fadeOut);
            visuals.Add(v);
            result.Add(v);

            // ---- 文字 ----
            var text = clip.Kind == StoryClipKind.Title ? clip.Text : clip.Caption;
            if (!string.IsNullOrWhiteSpace(text))
            {
                // キャプションは、クリップの一部の時間だけ出せる
                int tStart = span.Start, tLength = span.Length;
                double tFadeIn = fadeIn, tFadeOut = fadeOut;
                if (clip.Kind != StoryClipKind.Title && (clip.CaptionStart > 0 || clip.CaptionSeconds > 0))
                {
                    int delay = Math.Clamp(project.SecondsToFrames(clip.CaptionStart), 0, span.Length - 1);
                    int length = clip.CaptionSeconds > 0 ? Math.Max(1, project.SecondsToFrames(clip.CaptionSeconds)) : span.Length;
                    length = Math.Min(length, span.Length - delay);
                    tStart = span.Start + delay; tLength = length;
                    // クリップの途中で出る・消えるときは、短くフェードする
                    double shortFade = Math.Min(CaptionFade, project.FrameToSeconds(length) / 2);
                    if (delay > 0) tFadeIn = shortFade;
                    if (delay + length < span.Length) tFadeOut = shortFade;
                }
                var t = ObjectFactory.Create(EffectRegistry.Text, off + TextLayer + span.Lane, tStart, tLength);
                t.Id = ObjectId(clip, "t");
                t.Main.Values["text"] = text;
                t.Main.Values["color"] = clip.TextColor;
                if (!string.IsNullOrWhiteSpace(clip.Font)) t.Main.Values["font"] = clip.Font;
                double scale = clip.TextScale > 0 && clip.TextScale != 100 ? Math.Clamp(clip.TextScale, 10, 1000) / 100 : 1;
                if (clip.Kind == StoryClipKind.Title)
                {
                    double size = Math.Round(H * 0.09 * scale);
                    t.Main.Tracks["size"] = new AnimatedValue(size);
                    t.Main.Values["bold"] = "1";
                    if (clip.Motion == ClipMotion.ScrollUp)
                    {
                        // 下の外から上の外へ流す (エンドロール)。文字の高さは行数から見積もる
                        int lines = text.Split('\n').Length;
                        double edge = Math.Round(H / 2.0 + lines * size * 1.3 / 2 + size);
                        t.Draw!.Tracks["y"] = Moving(edge, -edge);
                    }
                }
                else
                {
                    t.Main.Tracks["size"] = new AnimatedValue(Math.Round(H * 0.055 * scale));
                    double y = clip.CaptionPlacement switch { CaptionPlacement.Top => -0.38, CaptionPlacement.Middle => 0, _ => 0.38 } * H;
                    t.Draw!.Tracks["y"] = new AnimatedValue(Math.Round(y));
                    if (clip.CaptionBackground)
                    {
                        var plate = ObjectFactory.AddFilter(t, "filter.backplate");
                        plate.Tracks["padx"] = new AnimatedValue(Math.Round(H / 45.0));
                        plate.Tracks["pady"] = new AnimatedValue(Math.Round(H / 90.0));
                        plate.Tracks["radius"] = new AnimatedValue(Math.Round(H / 135.0));
                    }
                    else
                    {
                        var border = ObjectFactory.AddFilter(t, "filter.border");
                        border.Tracks["size"] = new AnimatedValue(Math.Max(2, Math.Round(H / 360.0)));
                    }
                }
                AddFade(t, "filter.fade", tFadeIn, tFadeOut);
                result.Add(t);
            }

            // ---- 音声 ----
            if (clip.Kind == StoryClipKind.Video && clip.HasAudio && !clip.Muted)
            {
                var a = ObjectFactory.Create(EffectRegistry.Audio, off + AudioLayer + span.Lane, span.Start, span.Length);
                a.Id = ObjectId(clip, "a");
                a.Main.Values["file"] = clip.File;
                a.Main.Tracks["position"] = new AnimatedValue(Round(clip.TrimStart));
                if (clip.EffectiveSpeed != 1) a.Main.Tracks["speed"] = new AnimatedValue(Round(clip.EffectiveSpeed * 100));
                a.Effects[^1].Tracks["volume"] = new AnimatedValue(Math.Clamp(clip.Volume, 0, 500));
                if (clip.KeepPitch && clip.EffectiveSpeed != 1)
                {
                    // 速度を変えると上がる (下がる) 音の高さを、ピッチ変更で戻す
                    var pitch = ObjectFactory.AddFilter(a, "audio.pitch");
                    pitch.Tracks["semitones"] = new AnimatedValue(Math.Round(PitchCompensation(clip.EffectiveSpeed), 2));
                }
                AddFade(a, "audio.fade", fadeIn, fadeOut);
                // 映像と音声を連結しておく (詳細編集で一緒に動かせるように)
                v.GroupId = a.GroupId = IdPrefix + clip.Id;
                result.Add(a);
            }
        }

        // ---- 切り替え効果 ----
        // 後ろのレイヤーほど手前に描かれる。入ってくるクリップが手前 (レーン 1) なら、そのクリップの入りに効果をかける。
        // 奥 (レーン 0) なら、出ていく手前のクリップの出に、向きを逆にした効果をかけて同じ見え方にする。
        foreach (var span in layout.Clips)
        {
            if (span.Clip.Transition <= 0 || span.FadeIn <= 0) continue;
            int type = span.Clip.Transition - 1;
            double seconds = project.FrameToSeconds(span.FadeIn);
            if (type >= EffectRegistry.PairedTransitionStart)
            {
                // 入りと出で別の動きをするもの: 入ってくるクリップと出ていくクリップの両方にかける (どちらが手前でも同じ見え方)
                // 奥のクリップには印を付ける (形で切り抜くだけの役なら、手前のクリップが切り抜かれるので切り抜かない。境目に線が出ない)
                bool incomingOnTop = span.Index == 0 || span.Lane > layout.Clips[span.Index - 1].Lane;
                AddTransition(visuals[span.Index], type, seconds, 0, true, under: !incomingOnTop);
                if (span.Index > 0) AddTransition(visuals[span.Index - 1], type, 0, seconds, true, under: incomingOnTop);
            }
            else if (span.Index == 0 || span.Lane == 1) AddTransition(visuals[span.Index], type, seconds, 0);
            else AddTransition(visuals[span.Index - 1], Mirror(type), 0, seconds);
        }

        // ---- 音楽 ----
        foreach (var m in layout.Music)
        {
            // 繰り返す音楽は、使う範囲の長さごとに並べる (フェードは最初の入りと最後の出だけ)
            int cycle = m.Music.Loop ? Math.Max(1, project.SecondsToFrames(m.Music.Seconds)) : m.Length;
            for (int at = m.Start, k = 0; at <= m.End; at += cycle, k++)
            {
                int length = Math.Min(cycle, m.End - at + 1);
                var a = ObjectFactory.Create(EffectRegistry.Audio, off + m.Layer, at, length);
                a.Id = IdPrefix + m.Music.Id + "m" + (k == 0 ? "" : k.ToString(CultureInfo.InvariantCulture));
                a.Main.Values["file"] = m.Music.File;
                a.Main.Tracks["position"] = new AnimatedValue(Round(m.Music.TrimStart));
                a.Effects[^1].Tracks["volume"] = new AnimatedValue(Math.Clamp(m.Music.Volume, 0, 500));
                double seconds = project.FrameToSeconds(length);
                bool first = at == m.Start, last = at + length > m.End;
                AddFade(a, "audio.fade", first ? Math.Min(m.Music.FadeIn, seconds / 2) : 0, last ? Math.Min(m.Music.FadeOut, seconds / 2) : 0);
                if (m.Music.Duck) ObjectFactory.AddFilter(a, AudioDuckId);
                result.Add(a);
            }
        }
        return result;
    }

    /// <summary>ストーリーボードから作り直したオブジェクトで、メインのシーンを置き換える。ストーリーボードがなければ何もしない。</summary>
    public static void Apply(Project project)
    {
        if (project.Storyboard == null) return;
        project.Root.Objects = Compile(project.Storyboard, project);
    }

    /// <summary>
    /// メインのシーンが、ストーリーボードから作ったままか (詳細編集で変えられていないか)。
    /// ストーリーボードがなければ、シーンが空のときだけ true (まだ何も作っていない)。
    /// </summary>
    public static bool IsInSync(Project project)
    {
        var objects = project.Root.Objects;
        if (project.Storyboard == null) return objects.Count == 0;
        var compiled = Compile(project.Storyboard, project);
        if (compiled.Count != objects.Count) return false;
        var byId = new Dictionary<string, TimelineObject>(objects.Count);
        foreach (var o in objects) byId.TryAdd(o.Id, o);
        return compiled.All(c => byId.TryGetValue(c.Id, out var o) && TimelineObject.SameContent(c, o));
    }

    // ---------------- 部品 ----------------

    private static string ObjectId(StoryClip clip, string part) => IdPrefix + clip.Id + part;

    /// <summary>キャプションを途中で出す・消すときのフェード (秒)。</summary>
    private const double CaptionFade = 0.25;
    private const string AudioDuckId = "audio.duck";

    /// <summary>速度 speed 倍で上がる音の高さを戻す半音の数 (ピッチ変更の範囲 ±24 に収める)。</summary>
    public static double PitchCompensation(double speed) => Math.Clamp(-12 * Math.Log2(speed), -24, 24);

    private static double Round(double v) => Math.Round(v, 3);

    /// <summary>素材を画面に収まる大きさにして、回転と動きを付ける。</summary>
    private static void ApplyFrame(TimelineObject o, StoryClip clip, int W, int H)
    {
        var draw = o.Draw!;
        int rotation = ((clip.Rotation % 360) + 360) % 360;
        double sw = clip.SourceWidth, sh = clip.SourceHeight;
        if (rotation is 90 or 270) (sw, sh) = (sh, sw);
        double fit = sw > 0 && sh > 0 ? (clip.Fill ? Math.Max(W / sw, H / sh) : Math.Min(W / sw, H / sh)) * 100 : 100;
        fit = Math.Round(fit, 2);
        if (rotation != 0) draw.Tracks["rotation"] = new AnimatedValue(rotation);
        switch (clip.Motion)
        {
            case ClipMotion.ZoomIn:
                draw.Tracks["scale"] = Moving(fit, Math.Round(fit * MotionZoom, 2));
                break;
            case ClipMotion.ZoomOut:
                draw.Tracks["scale"] = Moving(Math.Round(fit * MotionZoom, 2), fit);
                break;
            case ClipMotion.PanLeft:
            case ClipMotion.PanRight:
            {
                // 拡大して余った幅の中で、横に動かす (端が見えないように)
                draw.Tracks["scale"] = new AnimatedValue(Math.Round(fit * MotionZoom, 2));
                double dx = Math.Round(W * (MotionZoom - 1) / 2 * 0.9);
                draw.Tracks["x"] = clip.Motion == ClipMotion.PanLeft ? Moving(dx, -dx) : Moving(-dx, dx);
                break;
            }
            default:
                draw.Tracks["scale"] = new AnimatedValue(fit);
                break;
        }
    }

    private static AnimatedValue Moving(double from, double to) => new() { Values = new List<double> { from, to }, Mode = MovementMode.Linear };

    private static void ApplyLook(TimelineObject o, ClipLook look)
    {
        switch (look)
        {
            case ClipLook.Mono:
            {
                var e = ObjectFactory.AddFilter(o, "filter.mono");
                e.Values["color"] = "#FFFFFF";
                break;
            }
            case ClipLook.Sepia:
            {
                var e = ObjectFactory.AddFilter(o, "filter.mono");
                e.Values["color"] = "#E8C89A";
                break;
            }
            case ClipLook.Blur:
                ObjectFactory.AddFilter(o, "filter.blur").Tracks["radius"] = new AnimatedValue(8);
                break;
            case ClipLook.Bright:
                ObjectFactory.AddFilter(o, "filter.color").Tracks["brightness"] = new AnimatedValue(125);
                break;
            case ClipLook.Dark:
                ObjectFactory.AddFilter(o, "filter.color").Tracks["brightness"] = new AnimatedValue(75);
                break;
            case ClipLook.Vivid:
            {
                var e = ObjectFactory.AddFilter(o, "filter.color");
                e.Tracks["saturation"] = new AnimatedValue(150);
                e.Tracks["contrast"] = new AnimatedValue(110);
                break;
            }
        }
    }

    private static void AddTransition(TimelineObject o, int type, double fadeIn, double fadeOut, bool screen = false, bool under = false)
    {
        var e = ObjectFactory.AddFilter(o, "filter.transition");
        if (screen) e.Values["screen"] = "1";
        if (under) e.Values["under"] = "1";
        e.Values["type"] = type.ToString(CultureInfo.InvariantCulture);
        e.Tracks["in"] = new AnimatedValue(Round(fadeIn));
        e.Tracks["out"] = new AnimatedValue(Round(fadeOut));
    }

    /// <summary>
    /// 入りの効果を、出ていく側の出にかけたときに同じ向きに見える種類。
    /// 例: 「ワイプ (左から)」で入ってくるのは、出ていく側が「右から」の形で消えていくのと同じ見え方になる。
    /// </summary>
    public static int Mirror(int type) => type switch
    {
        1 => 2, 2 => 1, 3 => 4, 4 => 3,     // ワイプ
        6 => 7, 7 => 6, 8 => 9, 9 => 8,     // スライド
        _ => type,                          // フェード・円形ワイプ・ズーム
    };

    private static void AddFade(TimelineObject o, string definitionId, double fadeIn, double fadeOut)
    {
        if (fadeIn <= 0 && fadeOut <= 0) return;
        var e = ObjectFactory.AddFilter(o, definitionId);
        e.Tracks["in"] = new AnimatedValue(Round(fadeIn));
        e.Tracks["out"] = new AnimatedValue(Round(fadeOut));
    }
}
