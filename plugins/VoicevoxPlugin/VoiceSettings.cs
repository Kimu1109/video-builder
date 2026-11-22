using System.Text;
using System.Text.Json;
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
    /// <summary>台本から一括で作るときの、セリフとセリフの間 (秒)。</summary>
    public double ScriptGap { get; set; } = 0.3;

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
            s.ScriptGap = double.IsFinite(s.ScriptGap) ? Math.Clamp(s.ScriptGap, 0, 10) : 0.3;
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
