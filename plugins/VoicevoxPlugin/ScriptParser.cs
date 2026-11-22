using System.Text;

namespace VoicevoxPlugin;

/// <summary>台本の 1 行 (1 つのセリフ)。LineNumber は 1 から。</summary>
public sealed record ScriptLine(int LineNumber, string Text, VoiceStyle Style);

/// <summary>
/// 台本を 1 行 1 セリフに分ける。
/// <list type="bullet">
/// <item>「話者: セリフ」と書くと、その話者で読む (コロンは全角でもよい)。話者は「ずんだもん」のようなキャラクター名か、
///   「ずんだもん(あまあま)」のようなスタイル付きの名前。キャラクター名だけなら、既定の話者と同じキャラクターなら既定のスタイル、
///   ほかのキャラクターならそのキャラクターの最初のスタイル。</item>
/// <item>話者を書かない行や、コロンの前が話者の名前でない行 (「時刻は 10:30」など) は、行全体を既定の話者で読む。</item>
/// <item>空の行と、# で始まる行 (コメント) は飛ばす。</item>
/// </list>
/// </summary>
public static class ScriptParser
{
    public static List<ScriptLine> Parse(string script, IReadOnlyList<VoiceStyle> styles, VoiceStyle defaultStyle)
    {
        var names = Names(styles, defaultStyle);
        var result = new List<ScriptLine>();
        var lines = (script ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('＃')) continue;
            var style = defaultStyle;
            var text = line;
            int colon = line.IndexOfAny(new[] { ':', '：' });
            if (colon > 0 && names.TryGetValue(Normalize(line[..colon]), out var named))
            {
                style = named;
                text = line[(colon + 1)..].Trim();
            }
            if (text.Length > 0) result.Add(new ScriptLine(i + 1, text, style));
        }
        return result;
    }

    /// <summary>話者の名前 (正規化したもの) → スタイル。</summary>
    private static Dictionary<string, VoiceStyle> Names(IReadOnlyList<VoiceStyle> styles, VoiceStyle defaultStyle)
    {
        var map = new Dictionary<string, VoiceStyle>(StringComparer.Ordinal);
        foreach (var s in styles)
        {
            map.TryAdd(Normalize($"{s.Speaker}({s.Style})"), s);
            // キャラクター名だけ: 既定の話者と同じキャラクターなら既定のスタイル、ほかは最初のスタイル
            var speaker = Normalize(s.Speaker);
            if (s.Speaker == defaultStyle.Speaker) map[speaker] = defaultStyle;
            else map.TryAdd(speaker, s);
        }
        return map;
    }

    /// <summary>全角・半角、空白の違いを無視して比べるための形。</summary>
    public static string Normalize(string s)
    {
        var n = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }
}
