using System.Globalization;
using System.Text.Json;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VoicevoxPlugin;

/// <summary>作った音声 1 つ (ファイル・長さ・セリフ・話者・調整の値)。</summary>
public sealed record VoiceClip(string Path, double Seconds, string Text, VoiceStyle Style, VoiceParams Voice);

/// <summary>音声オブジェクトに残す、作ったときの情報 (TimelineObject.PluginData)。</summary>
public sealed record VoiceInfo(string Text, int StyleId, string Speaker, VoiceParams Voice)
{
    public const string KeyText = "voicevox.text";
    public const string KeyStyleId = "voicevox.styleId";
    public const string KeySpeaker = "voicevox.speaker";
    public const string KeyParams = "voicevox.params";

    public static void Write(TimelineObject audio, VoiceClip clip)
    {
        audio.PluginData ??= new Dictionary<string, string>();
        audio.PluginData[KeyText] = clip.Text;
        audio.PluginData[KeyStyleId] = clip.Style.Id.ToString(CultureInfo.InvariantCulture);
        audio.PluginData[KeySpeaker] = clip.Style.Label;
        audio.PluginData[KeyParams] = JsonSerializer.Serialize(clip.Voice);
    }

    /// <summary>このプラグインで作った音声オブジェクトなら、その情報。そうでなければ null。</summary>
    public static VoiceInfo? Read(TimelineObject o)
    {
        if (o.Main.DefinitionId != EffectRegistry.Audio || o.PluginData is not { } d) return null;
        if (!d.TryGetValue(KeyText, out var text) || !d.TryGetValue(KeyStyleId, out var sid)
            || !int.TryParse(sid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var styleId)) return null;
        VoiceParams voice;
        try { voice = d.TryGetValue(KeyParams, out var json) ? JsonSerializer.Deserialize<VoiceParams>(json) ?? new VoiceParams() : new VoiceParams(); }
        catch (JsonException) { voice = new VoiceParams(); }
        return new VoiceInfo(text, styleId, d.GetValueOrDefault(KeySpeaker, ""), voice.Clamped());
    }
}

/// <summary>作った音声 (と字幕) をタイムラインに置く・差し替える。</summary>
public static class VoicePlacer
{
    public sealed record Placed(TimelineObject Audio, TimelineObject? Subtitle);

    /// <summary>秒をフレーム数にする。終わりが切れないよう、端数は切り上げる。</summary>
    public static int Frames(IPluginCommandContext context, double seconds) =>
        Math.Max(1, (int)Math.Ceiling(seconds * context.Project.Settings.Fps.Value - 1e-6));

    /// <summary>1 つを再生位置に置く (PlaceMany の 1 つ版)。</summary>
    public static Placed? Place(IPluginCommandContext context, VoiceClip clip, bool subtitle) =>
        PlaceMany(context, new[] { clip }, 0, subtitle, "VOICEVOX の音声を置く")?[0];

    /// <summary>
    /// 再生位置から順に、gapFrames ずつ空けて並べる。字幕も置くなら、字幕を 1 つのレイヤーに、音声をそのすぐ下のレイヤーに並べ、
    /// 1 組ずつグループにする。すべてが入るレイヤー (の組) を目安のレイヤーから下に探す。
    /// 置く場所がなければ null (何も変えない)。まとめて 1 回で取り消せる。
    /// </summary>
    public static List<Placed>? PlaceMany(IPluginCommandContext context, IReadOnlyList<VoiceClip> clips, int gapFrames, bool subtitle, string description)
    {
        if (clips.Count == 0) return new List<Placed>();
        var ranges = new List<(int Start, int End)>();
        int frame = Math.Max(0, context.CurrentFrame);
        foreach (var c in clips)
        {
            int length = Frames(context, c.Seconds);
            ranges.Add((frame, frame + length - 1));
            frame += length + Math.Max(0, gapFrames);
        }

        // 置く場所を先に決める (置けないときに、空の取り消し履歴を残さないため)
        var scene = context.Scene;
        int needed = subtitle ? 2 : 1;
        int top = -1;
        for (int l = Math.Max(0, context.TargetLayer); l + needed <= Scene.LayerCount && top < 0; l++)
            if (Enumerable.Range(l, needed).All(x => ranges.All(r => scene.IsFree(x, r.Start, r.End)))) top = l;
        if (top < 0) return null;

        var result = new List<Placed>();
        context.Edit(description, e =>
        {
            for (int i = 0; i < clips.Count; i++)
            {
                var (start, end) = ranges[i];
                int length = end - start + 1;
                var caption = subtitle ? e.AddText(clips[i].Text, top, start, length) : null;
                var audio = e.AddAudioFile(clips[i].Path, subtitle ? top + 1 : top, start, length);
                if (audio == null || subtitle && caption == null) throw new InvalidOperationException("音声を置けませんでした。");
                VoiceInfo.Write(audio, clips[i]);
                if (caption != null) e.Group(new[] { caption, audio });
                result.Add(new Placed(audio, caption));
            }
            e.Select(result.SelectMany(p => p.Subtitle != null ? new[] { p.Subtitle, p.Audio } : new[] { p.Audio }));
        });
        return result;
    }

    // ---------------- 作り直す ----------------

    /// <summary>作り直す対象: このプラグインで作った音声と、同じグループの字幕 (あれば)。</summary>
    public sealed record Target(TimelineObject Audio, TimelineObject? Subtitle, VoiceInfo Info)
    {
        /// <summary>今のセリフ。字幕があれば字幕の文字 (直したあとの文字)、なければ作ったときのセリフ。</summary>
        public string CurrentText => Subtitle?.Main.Values.GetValueOrDefault("text") is { Length: > 0 } t ? t : Info.Text;
    }

    /// <summary>
    /// 選んでいるものから、作り直す対象を集める。音声を選んでいても、同じグループの字幕を選んでいても対象になる。
    /// </summary>
    public static List<Target> FindTargets(IPluginCommandContext context)
    {
        var scene = context.Scene;
        var result = new List<Target>();
        var seen = new HashSet<string>();
        foreach (var o in context.SelectedObjects)
        {
            IEnumerable<TimelineObject> candidates = o.GroupId != null
                ? scene.Objects.Where(x => x.GroupId == o.GroupId)
                : new[] { o };
            foreach (var audio in candidates)
            {
                if (seen.Contains(audio.Id) || VoiceInfo.Read(audio) is not { } info) continue;
                seen.Add(audio.Id);
                var subtitle = audio.GroupId == null ? null
                    : scene.Objects.FirstOrDefault(x => x.GroupId == audio.GroupId && x.Main.DefinitionId == EffectRegistry.Text);
                result.Add(new Target(audio, subtitle, info));
            }
        }
        return result.OrderBy(t => t.Audio.Start).ThenBy(t => t.Audio.Layer).ToList();
    }

    /// <summary>
    /// 作り直した音声に差し替える。音声のファイルと長さ、字幕の文字と長さを新しいものに合わせる (先頭の位置は変えない)。
    /// 長くなって後ろのオブジェクトにぶつかるものは差し替えず、返す一覧に入れる。まとめて 1 回で取り消せる。
    /// </summary>
    public static List<Target> Replace(IPluginCommandContext context, IReadOnlyList<(Target Target, VoiceClip Clip)> items)
    {
        var scene = context.Scene;
        var blocked = new List<Target>();
        var ok = new List<(Target Target, VoiceClip Clip, int Length)>();
        foreach (var (t, clip) in items)
        {
            int length = Frames(context, clip.Seconds);
            var ignore = new HashSet<string> { t.Audio.Id };
            if (t.Subtitle != null) ignore.Add(t.Subtitle.Id);
            bool free = scene.IsFree(t.Audio.Layer, t.Audio.Start, t.Audio.Start + length - 1, ignore)
                        && (t.Subtitle == null || scene.IsFree(t.Subtitle.Layer, t.Subtitle.Start, t.Subtitle.Start + length - 1, ignore));
            if (free) ok.Add((t, clip, length)); else blocked.Add(t);
        }
        if (ok.Count == 0) return blocked;

        context.Edit("VOICEVOX の音声を作り直す", e =>
        {
            foreach (var (t, clip, length) in ok)
            {
                // Edit の中では、シーンの今のオブジェクトを書き換える (取り消しの複製と取り違えないよう、ID で引き直す)
                var audio = e.Scene.Find(t.Audio.Id) ?? throw new InvalidOperationException("音声が見つかりません。");
                audio.Main.Values["file"] = clip.Path;
                audio.SourceOffsetFrames = 0;
                audio.SetRange(audio.Start, audio.Start + length - 1);
                VoiceInfo.Write(audio, clip);
                if (t.Subtitle != null && e.Scene.Find(t.Subtitle.Id) is { } caption)
                {
                    caption.Main.Values["text"] = clip.Text;
                    caption.SetRange(caption.Start, caption.Start + length - 1);
                }
            }
        });
        return blocked;
    }
}
