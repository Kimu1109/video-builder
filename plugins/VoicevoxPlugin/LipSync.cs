using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoicevoxPlugin;

/// <summary>口の形。Closed は閉じた口 (無音・ん・っ・ま行などの唇を閉じる子音)。</summary>
public enum Mouth { Closed, A, I, U, E, O }

/// <summary>
/// 口パクの時刻表。音声の先頭からの秒と、その時刻から次の変わり目までの口の形を並べたもの。
/// 音声オブジェクトの PluginData に、"0:-;0.12:a;0.25:i" のような短い文字列で残す (- は閉じた口)。
/// </summary>
public sealed class LipSync
{
    /// <summary>これより短い口の形は、前の形にまとめる (パクパクしすぎないように)。秒。</summary>
    public const double MinSegment = 0.04;

    private readonly double[] _times;
    private readonly Mouth[] _mouths;

    public LipSync(IReadOnlyList<(double Time, Mouth Mouth)> points)
    {
        _times = points.Select(p => p.Time).ToArray();
        _mouths = points.Select(p => p.Mouth).ToArray();
    }

    public IReadOnlyList<(double Time, Mouth Mouth)> Points => _times.Zip(_mouths).ToList();

    /// <summary>音声の先頭から seconds 秒の口の形。範囲の外は閉じた口。</summary>
    public Mouth At(double seconds)
    {
        if (_times.Length == 0 || seconds < _times[0]) return Mouth.Closed;
        int i = Array.BinarySearch(_times, seconds);
        if (i < 0) i = ~i - 1;
        return _mouths[Math.Clamp(i, 0, _mouths.Length - 1)];
    }

    // ---------------- 文字列にする ----------------

    private const string Letters = "-aiueo";

    public string Encode()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _times.Length; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(_times[i].ToString("0.###", CultureInfo.InvariantCulture)).Append(':').Append(Letters[(int)_mouths[i]]);
        }
        return sb.ToString();
    }

    // 同じ文字列 (PluginData の値はプロジェクトの複製の間で同じ実体) を毎フレーム読み直さないよう、読んだ結果を覚えておく
    private static readonly ConditionalWeakTable<string, LipSync> Parsed = new();

    /// <summary>Encode した文字列を読む。読めない部分は飛ばす。</summary>
    public static LipSync Decode(string text)
    {
        if (Parsed.TryGetValue(text, out var hit)) return hit;
        var points = new List<(double, Mouth)>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0 || colon + 1 >= part.Length) continue;
            if (!double.TryParse(part[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) || !double.IsFinite(t)) continue;
            int m = Letters.IndexOf(part[colon + 1]);
            if (m < 0) continue;
            if (points.Count > 0 && t < points[^1].Item1) continue;   // 時刻は増えていくものだけ
            points.Add((t, (Mouth)m));
        }
        var lips = new LipSync(points);
        Parsed.AddOrUpdate(text, lips);
        return lips;
    }

    // ---------------- audio_query から作る ----------------

    // 唇を閉じて出す子音 (ま行・ば行・ぱ行)
    private static readonly HashSet<string> ClosingConsonants = new(StringComparer.Ordinal) { "m", "my", "b", "by", "p", "py" };

    private static Mouth VowelMouth(string? vowel) => vowel?.ToLowerInvariant() switch
    {
        "a" => Mouth.A,
        "i" => Mouth.I,
        "u" => Mouth.U,
        "e" => Mouth.E,
        "o" => Mouth.O,
        _ => Mouth.Closed,   // ん (N)・っ (cl)・無音 (pau)
    };

    /// <summary>
    /// 音声合成に使った audio_query (話速などを書き込んだもの) から、口パクの時刻表を作る。
    /// モーラごとの子音・母音の長さを足していき、全体を実際の音声の長さ (wavSeconds) に合わせて伸び縮みさせる
    /// (エンジンの丸めや、間の長さの調整のずれを吸収するため)。読めなければ null。
    /// </summary>
    public static LipSync? FromQuery(string queryJson, double wavSeconds)
    {
        JsonObject q;
        try { q = JsonNode.Parse(queryJson)?.AsObject() ?? throw new JsonException(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }

        double Num(JsonNode? n, double fallback)
        {
            try { return n?.GetValue<double>() ?? fallback; }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { return fallback; }
        }

        var raw = new List<(double Time, Mouth Mouth)>();
        double t = Math.Max(0, Num(q["prePhonemeLength"], 0.1));
        raw.Add((0, Mouth.Closed));
        foreach (var phrase in q["accent_phrases"]?.AsArray() ?? new JsonArray())
        {
            foreach (var mora in phrase?["moras"]?.AsArray() ?? new JsonArray())
            {
                var vowel = mora?["vowel"]?.GetValue<string>();
                var consonant = mora?["consonant"]?.GetValue<string>();
                double cl = Math.Max(0, Num(mora?["consonant_length"], 0));
                if (consonant != null && cl > 0)
                {
                    raw.Add((t, ClosingConsonants.Contains(consonant) ? Mouth.Closed : VowelMouth(vowel)));
                    t += cl;
                }
                raw.Add((t, VowelMouth(vowel)));
                t += Math.Max(0, Num(mora?["vowel_length"], 0));
            }
            if (phrase?["pause_mora"] is JsonObject pause)
            {
                raw.Add((t, Mouth.Closed));
                t += Math.Max(0, Num(pause["vowel_length"], 0));
            }
        }
        raw.Add((t, Mouth.Closed));
        double total = t + Math.Max(0, Num(q["postPhonemeLength"], 0.1));
        if (total <= 0 || wavSeconds <= 0) return null;

        // 話速を含め、実際の長さに合わせる
        double k = wavSeconds / total;
        var points = new List<(double Time, Mouth Mouth)>();
        for (int i = 0; i < raw.Count; i++)
        {
            double start = raw[i].Time * k;
            double end = i + 1 < raw.Count ? raw[i + 1].Time * k : wavSeconds;
            if (end - start <= 0) continue;
            // 短すぎる形は前の形にまとめる (最初と閉じた口は残す)
            if (end - start < MinSegment && points.Count > 0 && raw[i].Mouth != Mouth.Closed) continue;
            if (points.Count > 0 && points[^1].Mouth == raw[i].Mouth) continue;
            points.Add((Math.Round(start, 3), raw[i].Mouth));
        }
        return new LipSync(points);
    }
}
