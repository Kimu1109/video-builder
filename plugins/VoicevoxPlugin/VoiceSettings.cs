using System.Text;
using System.Text.Json;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VoicevoxPlugin;

/// <summary>プラグインの設定と、前回使った値。DataDirectory の settings.json に保存する。</summary>
public sealed class VoiceSettings
{
    public const string FileName = "settings.json";

    public string EngineUrl { get; set; } = VoicevoxClient.DefaultUrl;
    /// <summary>前回使ったスタイル (話者 ID)。なければ一覧の先頭。</summary>
    public int? StyleId { get; set; }
    public VoiceParams Voice { get; set; } = new();
    /// <summary>字幕 (テキストオブジェクト) も置く。</summary>
    public bool AddSubtitle { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>読めなければ既定の設定を返す。</summary>
    public static VoiceSettings Load(string dir)
    {
        try
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return new VoiceSettings();
            var s = JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(path), Options) ?? new VoiceSettings();
            if (VoicevoxClient.ParseUrl(s.EngineUrl) == null) s.EngineUrl = VoicevoxClient.DefaultUrl;
            s.Voice = (s.Voice ?? new VoiceParams()).Clamped();
            return s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new VoiceSettings(); }
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        // 途中で止まっても前の設定が壊れないよう、別の名前で書いてから置き換える
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>作った音声のファイルの置き場所と名前。</summary>
public static class VoiceFiles
{
    public const string FolderName = "voices";

    /// <summary>
    /// 置き場所: プロジェクトを保存していればその隣の voices フォルダ (プロジェクトと一緒に持ち運べる)、
    /// まだなら、プラグインの設定フォルダの voices。
    /// </summary>
    public static string Directory(IPluginCommandContext context) =>
        Path.Combine(context.ProjectDirectory ?? context.DataDirectory, FolderName);

    /// <summary>"日時_話者_セリフの頭.wav" の、まだないファイルの名前。</summary>
    public static string NewPath(string dir, string speaker, string text, DateTime now)
    {
        string head = Safe(text, 16);
        string name = $"{now:yyyyMMdd-HHmmss}_{Safe(speaker, 24)}" + (head.Length > 0 ? "_" + head : "");
        var path = Path.Combine(dir, name + ".wav");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name}_{i}.wav");
        return path;
    }

    /// <summary>ファイル名に使えない文字・空白・改行を "_" にして、max 文字までにする。</summary>
    public static string Safe(string s, int max)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var sb = new StringBuilder();
        foreach (var c in s.Trim())
        {
            if (sb.Length >= max) break;
            sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c);
        }
        // 半端なサロゲートを残さない
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        return sb.ToString().Trim('_', '.');
    }
}

/// <summary>作った音声 (と字幕) をタイムラインに置く。</summary>
public static class VoicePlacer
{
    // PluginData のキー (あとで作り直すときに使う)
    public const string KeyText = "voicevox.text";
    public const string KeyStyleId = "voicevox.styleId";
    public const string KeySpeaker = "voicevox.speaker";
    public const string KeyParams = "voicevox.params";

    public sealed record Placed(TimelineObject Audio, TimelineObject? Subtitle);

    /// <summary>
    /// 再生位置に音声を置く。字幕も置くなら、字幕を目安のレイヤー (から下の空いているところ) に、音声をそのすぐ下に置いて、グループにする。
    /// 置く場所がなければ null (何も変えない)。1 回で取り消せる。
    /// </summary>
    public static Placed? Place(IPluginCommandContext context, string wavPath, double seconds, string text, VoiceStyle style, VoiceParams voice, bool subtitle)
    {
        var fps = context.Project.Settings.Fps.Value;
        // 終わりが切れないよう、端数は切り上げる
        int length = Math.Max(1, (int)Math.Ceiling(seconds * fps - 1e-6));
        int frame = Math.Max(0, context.CurrentFrame);
        int end = frame + length - 1;

        // 置く場所を先に決める (置けないときに、空の取り消し履歴を残さないため)
        var scene = context.Scene;
        int needed = subtitle ? 2 : 1;
        int top = -1;
        for (int l = Math.Max(0, context.TargetLayer); l + needed <= Scene.LayerCount && top < 0; l++)
            if (Enumerable.Range(l, needed).All(x => scene.IsFree(x, frame, end))) top = l;
        if (top < 0) return null;

        Placed? result = null;
        context.Edit("VOICEVOX の音声を置く", e =>
        {
            var caption = subtitle ? e.AddText(text, top, frame, length) : null;
            var audio = e.AddAudioFile(wavPath, subtitle ? top + 1 : top, frame, length);
            if (audio == null || subtitle && caption == null) throw new InvalidOperationException("音声を置けませんでした。");
            audio.PluginData = new Dictionary<string, string>
            {
                [KeyText] = text,
                [KeyStyleId] = style.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [KeySpeaker] = style.Label,
                [KeyParams] = JsonSerializer.Serialize(voice),
            };
            if (caption != null) e.Group(new[] { caption, audio });
            e.Select(caption != null ? new[] { caption, audio } : new[] { audio });
            result = new Placed(audio, caption);
        });
        return result;
    }
}
