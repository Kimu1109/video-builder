using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// テキストアニメーション (文字の出し方・消え方) の、文字ごとの描き方を決める。
/// <para>
/// 文字は「単位」(文字・単語・行) にまとめ、単位ごとに順番 (先頭から・ランダム・中央から・両端から、逆順) を付ける。
/// 順番 n の単位は「n × 文字の間隔」秒後に動き始め、「1 文字の長さ」秒で動き終わる。
/// 消え方は、オブジェクトの終わりにちょうど消え終わるように、同じ順番・間隔で消える。
/// </para>
/// </summary>
internal static class TextAnimation
{
    /// <summary>描くもの。DoneKey が null でなければ、この時刻の絵は動いていない (同じキーの絵を使い回してよい)。</summary>
    public readonly record struct Plan(string Text, Func<int, Generators.CharStyle> Style, string? DoneKey);

    private const int CountUp = 17, Wave = 14;

    /// <param name="t">オブジェクトの先頭からの時間 (秒)。</param>
    /// <param name="length">オブジェクトの長さ (秒)。</param>
    public static Plan Make(Effect main, IReadOnlyList<int> bounds, int startFrame, string text, double t, double length, float size, SKColor color)
    {
        int anim = main.GetInt("anim"), exit = main.GetInt("exit");
        double interval = Math.Max(0, main.Eval("interval", bounds, startFrame)), duration = Math.Max(0, main.Eval("duration", bounds, startFrame));
        bool reverse = main.GetBool("reverse");
        int unitKind = main.GetInt("unit"), order = main.GetInt("order");

        // 数字のカウントアップ: 文字そのものを、数える途中の値に書き換える
        bool counting = false;
        if (anim == CountUp)
        {
            double countTime = main.Eval("countTime", bounds, startFrame);
            double k = countTime <= 0 ? 1 : Math.Clamp(t / countTime, 0, 1);
            counting = k < 1;
            text = CountText(text, 1 - (1 - k) * (1 - k) * (1 - k));
        }

        var units = UnitsOf(text, unitKind);
        int unitCount = units.Length == 0 ? 0 : units.Max() + 1;
        var rank = Ranks(unitCount, order, reverse, text);
        int maxRank = rank.Length == 0 ? 0 : rank.Max();
        double Local(int i) => t - (i < units.Length ? rank[units[i]] : 0) * interval;

        // 消え方: オブジェクトの終わりで消え終わる
        double exitTotal = maxRank * interval + duration, exitStart = length - exitTotal;
        double inEnd = anim switch
        {
            0 or CountUp => 0,
            1 => maxRank * interval,
            Wave => double.PositiveInfinity,
            _ => maxRank * interval + duration,
        };

        float period = (float)Math.Max(0.01, main.Eval("period", bounds, startFrame));
        float waveHeight = (float)main.Eval("waveHeight", bounds, startFrame) / 100f * size;
        var fillColor = SKColor.TryParse(main.GetString("color2"), out var c2) ? c2 : new SKColor(255, 208, 64);

        Generators.CharStyle Style(int i)
        {
            double local = Local(i);
            var st = InStyle(anim, i, local, duration, t, interval, size, period, waveHeight, fillColor, text);
            if (exit > 0)
            {
                double lo = t - exitStart - (i < units.Length ? rank[units[i]] : 0) * interval;
                double q = duration <= 0 ? (lo >= 0 ? 1 : 0) : Math.Clamp(lo / duration, 0, 1);
                if (q > 0) st = ExitStyle(st, exit, i, (float)q, size);
            }
            return st;
        }

        bool moving = counting || t < inEnd || (exit > 0 && t >= exitStart);
        string? doneKey = moving ? null : string.Join('|', "A", anim, exit, reverse, unitKind, order, main.GetString("color2"),
            exit > 0 ? "x" : "", Generators.F(waveHeight));
        return new Plan(text, Style, doneKey);
    }

    private static float Smooth(double q)
    {
        q = Math.Clamp(q, 0, 1);
        return (float)(q * q * (3 - 2 * q));
    }

    private static Generators.CharStyle InStyle(int anim, int i, double local, double duration, double t, double interval,
        float size, float period, float waveHeight, SKColor fillColor, string text)
    {
        double q = duration <= 0 ? (local >= 0 ? 1 : 0) : Math.Clamp(local / duration, 0, 1);
        float e = Smooth(q);
        bool started = local >= 0;
        switch (anim)
        {
            case 0 or CountUp: return new(1, 0, 1);
            case 1: return new(started ? 1 : 0, 0, 1);                                   // 1 文字ずつ表示
            case 2: return new(e, 0, 1);                                                 // フェードイン
            case 3: return new(e, (1 - e) * size * 0.6f, 1);                             // 下から
            case 4: return new(e > 0 ? 1 : 0, 0, e);                                     // 拡大
            case 5: return new(e, -(1 - e) * size * 0.6f, 1);                            // 上から
            case 6: return new(e, 0, 1) { Dx = -(1 - e) * size * 0.8f };                 // 左から
            case 7: return new(e, 0, 1) { Dx = (1 - e) * size * 0.8f };                  // 右から
            case 8: return new(e, 0, 0.3f + 0.7f * e) { Rotation = -180 * (1 - e) };     // 回転して登場
            case 9:                                                                      // 弾んで登場: 上から落ちて弾む
            {
                if (!started) return new(0, 0, 1);
                float b = Effects.BounceOut((float)q);
                return new((float)Math.Min(1, q * 4), -(1 - b) * size * 1.2f, 1);
            }
            case 10: return new(e, 0, 1) { Blur = (1 - e) * size * 0.4f };               // ぼかしから
            case 11: return new(e, 0, 1 + 1.5f * (1 - e));                               // 縮小して登場
            case 12:                                                                     // 落ちてくる: 上から加速しながら落ちる
            {
                if (!started) return new(0, 0, 1);
                float fall = (float)(1 - q * q);
                return new((float)Math.Min(1, q * 5), -fall * size * 1.8f, 1);
            }
            case 13:                                                                     // 文字化けから表示
            {
                if (!started) return new(0, 0, 1);
                if (q >= 1) return new(1, 0, 1);
                long step = (long)Math.Floor(local / 0.05);
                return new(1, 0, 1) { Text = RandomLike(TextElementAt(text, i), (uint)i, (uint)step) };
            }
            case Wave:                                                                   // 波打つ: 単位ごとに少しずつ遅れて上下する
            {
                double phase = (t - local) / period;   // = 順番 × 間隔 / 周期 (遅れ)
                float dy = -waveHeight * (float)Math.Sin(2 * Math.PI * (t / period - phase));
                return new(1, dy, 1);
            }
            case 15: return new(1, 0, 1) { Fill = (float)(duration <= 0 ? (started ? 1 : 0) : Math.Clamp(local / duration, 0, 1)), FillColor = fillColor };   // カラオケ
            default:                                                                     // 線で描いてから塗る
            {
                if (!started) return new(0, 0, 1);
                if (q >= 1) return new(1, 0, 1);
                return new((float)Math.Clamp((q - 0.65) / 0.35, 0, 1), 0, 1) { Outline = (float)Math.Clamp(q / 0.65, 0.001, 1) };
            }
        }
    }

    private static Generators.CharStyle ExitStyle(Generators.CharStyle st, int exit, int i, float q, float size)
    {
        float e = Smooth(q);
        switch (exit)
        {
            case 1: return st with { Alpha = st.Alpha * (1 - e) };
            case 2: return st with { Alpha = st.Alpha * (1 - e), Dy = st.Dy - size * 0.6f * e };
            case 3: return st with { Alpha = st.Alpha * (1 - q * q * q), Dy = st.Dy + size * 1.8f * q * q, Rotation = st.Rotation + (i % 2 == 0 ? 12 : -12) * q };
            case 4: return st with { Scale = st.Scale * (1 - e) };
            default:   // 散らばって消える: 文字ごとにばらばらの向きへ、回りながら飛んで消える
            {
                float ang = PixelFx.Hash((uint)i, 0x5CA7, 1) / (float)uint.MaxValue * 2 * MathF.PI;
                float spin = (PixelFx.Hash((uint)i, 0x5CA7, 2) / (float)uint.MaxValue * 2 - 1) * 240;
                return st with
                {
                    Alpha = st.Alpha * (1 - e), Dx = st.Dx + MathF.Cos(ang) * size * 1.4f * e, Dy = st.Dy + MathF.Sin(ang) * size * 1.4f * e,
                    Rotation = st.Rotation + spin * e,
                };
            }
        }
    }

    // ---------------- 単位と順番 ----------------

    private enum CharClass { Space, Punct, Kanji, Hiragana, Katakana, Latin, Other }

    private static CharClass ClassOf(string el)
    {
        if (string.IsNullOrWhiteSpace(el)) return CharClass.Space;
        int cp = char.ConvertToUtf32(el, 0);
        if (cp is >= 0x3040 and <= 0x309F) return CharClass.Hiragana;
        if (cp is >= 0x30A0 and <= 0x30FF or >= 0xFF66 and <= 0xFF9F) return CharClass.Katakana;
        if (cp is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or 0x3005) return CharClass.Kanji;
        if (char.IsLetterOrDigit(el, 0) || el == "'" || el == "’") return CharClass.Latin;
        if (char.IsPunctuation(el, 0) || char.IsSymbol(el, 0)) return CharClass.Punct;
        return CharClass.Other;
    }

    /// <summary>
    /// 文字の番号 (改行も 1 文字) ごとの単位の番号。文字なら文字 (改行も) ごと、行なら行ごと。
    /// 単語は空白で区切り、日本語は「漢字 + 送り仮名」「カタカナ」「英数字」のまとまりで区切る。句読点・空白は前の単語に含める。
    /// </summary>
    internal static int[] UnitsOf(string text, int kind)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new List<int>();
        int unit = -1;
        for (int li = 0; li < lines.Length; li++)
        {
            var prev = CharClass.Space;
            var e = StringInfo.GetTextElementEnumerator(lines[li]);
            bool firstInLine = true;
            while (e.MoveNext())
            {
                string el = e.GetTextElement();
                var cls = ClassOf(el);
                bool start = kind switch
                {
                    0 => true,
                    2 => firstInLine,
                    _ => firstInLine || StartsWord(prev, cls),
                };
                if (start || unit < 0) unit++;
                result.Add(unit);
                if (cls is not (CharClass.Space or CharClass.Punct) || kind != 1) prev = cls;
                else prev = CharClass.Space;
                firstInLine = false;
            }
            if (li < lines.Length - 1)
            {
                // 改行: 文字単位なら 1 つの単位 (以前からの数え方と同じ)、それ以外は前の単位に含める
                if (kind == 0) unit++;
                result.Add(Math.Max(0, unit));
                if (firstInLine && kind == 2) unit++;   // 空の行も 1 行と数える
            }
        }
        return result.ToArray();
    }

    private static bool StartsWord(CharClass prev, CharClass cur)
    {
        if (cur is CharClass.Space or CharClass.Punct) return false;   // 前の単語に付ける
        if (prev == CharClass.Space) return true;
        if (prev == cur) return false;
        if (prev == CharClass.Kanji && cur == CharClass.Hiragana) return false;   // 送り仮名
        return true;
    }

    /// <summary>単位ごとの順番 (同じ順番の単位は同時に動く)。</summary>
    internal static int[] Ranks(int count, int order, bool reverse, string text)
    {
        var rank = new int[count];
        switch (order)
        {
            case 1:   // ランダム: テキストから決まる乱数で並べ替える (同じテキストなら毎回同じ)
            {
                uint seed = 2166136261;
                foreach (char ch in text) seed = (seed ^ ch) * 16777619;
                var idx = Enumerable.Range(0, count).OrderBy(u => PixelFx.Hash((uint)u, seed, 0x0D)).ToArray();
                for (int r = 0; r < count; r++) rank[idx[r]] = r;
                break;
            }
            case 2: for (int u = 0; u < count; u++) rank[u] = (int)Math.Floor(Math.Abs(u - (count - 1) / 2.0)); break;   // 中央から
            case 3: for (int u = 0; u < count; u++) rank[u] = Math.Min(u, count - 1 - u); break;                           // 両端から
            default: for (int u = 0; u < count; u++) rank[u] = u; break;
        }
        if (reverse && count > 0)
        {
            int max = rank.Max();
            for (int u = 0; u < count; u++) rank[u] = max - rank[u];
        }
        return rank;
    }

    // ---------------- 文字化け・カウントアップ ----------------

    private static string TextElementAt(string text, int index)
    {
        int i = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var e = StringInfo.GetTextElementEnumerator(line);
            while (e.MoveNext())
            {
                if (i == index) return e.GetTextElement();
                i++;
            }
            if (i == index) return "\n";
            i++;
        }
        return "";
    }

    private const string Kanji = "一二三四五六七八九十人日月火水木金土山川田口目耳手足上下中大小円年本出入生先学校天気雨電";

    /// <summary>元の文字と同じ種類 (英大文字・英小文字・数字・ひらがな・カタカナ・漢字・記号) の、でたらめな文字。</summary>
    private static string RandomLike(string el, uint i, uint step)
    {
        if (string.IsNullOrWhiteSpace(el)) return el;
        uint h = PixelFx.Hash(i, step, 0xDEC0);
        int cp = char.ConvertToUtf32(el, 0);
        string Pick(string set) => set[(int)(h % (uint)set.Length)].ToString();
        if (cp is >= 'A' and <= 'Z') return Pick("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        if (cp is >= 'a' and <= 'z') return Pick("abcdefghijklmnopqrstuvwxyz");
        if (cp is >= '0' and <= '9') return Pick("0123456789");
        return ClassOf(el) switch
        {
            CharClass.Hiragana => ((char)(0x3041 + h % 83)).ToString(),
            CharClass.Katakana => ((char)(0x30A1 + h % 86)).ToString(),
            CharClass.Kanji => Pick(Kanji),
            _ => Pick("#$%&*+=?@<>/"),
        };
    }

    private static readonly Regex Number = new(@"\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>テキストの中の数を、0 から k (0〜1) の割合まで数えた値に書き換える。桁区切り・小数の桁数はそのまま。</summary>
    internal static string CountText(string text, double k)
    {
        if (k >= 1) return text;
        return Number.Replace(text, m =>
        {
            string s = m.Value;
            bool comma = s.Contains(',');
            int dot = s.IndexOf('.');
            int decimals = dot < 0 ? 0 : s.Length - dot - 1;
            if (!double.TryParse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return s;
            double shown = v * Math.Clamp(k, 0, 1);
            shown = Math.Floor(shown * Math.Pow(10, decimals)) / Math.Pow(10, decimals);
            return shown.ToString((comma ? "N" : "F") + decimals, CultureInfo.InvariantCulture);
        });
    }
}
