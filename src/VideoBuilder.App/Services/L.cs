using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;

namespace VideoBuilder.App.Services;

public enum Language { Auto, Japanese, English }

/// <summary>
/// 表示言語。ソースコード中の文言は日本語で書き、英語のときだけ対応表 (tools/I18n/en.txt) で置き換える。
/// 置き換えは TextBlock などに文字が入る瞬間に自動で行うので、画面側のコードは言語を意識しない。
/// </summary>
public static class L
{
    private static Dictionary<string, string> _exact = new();
    private static List<(Regex Pattern, string Template)> _patterns = new();
    // "{0}" を含む対応 (日本語の型紙 → 英語の型紙)。Format で使う
    private static Dictionary<string, string> _templates = new();
    private static bool _installed;
    private static readonly Dictionary<string, string> Memo = new();

    public static bool English { get; private set; }

    public static void Initialize(Language language)
    {
        English = language switch
        {
            Language.English => true,
            Language.Japanese => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ja",
        };
        lock (Memo) Memo.Clear();
        if (!English) return;
        _exact = new Dictionary<string, string>();
        _templates = new Dictionary<string, string>();
        var patterns = new List<(Regex, string, int)>();
        foreach (var (ja, en) in TranslationsEn.Pairs)
        {
            if (!ja.Contains("{0}")) { _exact[ja] = en; continue; }
            _templates[ja] = en;
            // "{0}" を含むものは、実行時の値が入る型紙として扱う
            string regex = "^" + Regex.Replace(Regex.Escape(ja), @"\\\{(\d)}", m => $"(?<g{m.Groups[1].Value}>.*?)") + "$";
            patterns.Add((new Regex(regex, RegexOptions.Singleline | RegexOptions.CultureInvariant), en, ja.Length));
        }
        // 固定部分が長いものから試す (短い型紙が何にでも当たってしまうのを防ぐ)
        _patterns = patterns.OrderByDescending(p => p.Item3).Select(p => (p.Item1, p.Item2)).ToList();
        Install();
    }

    /// <summary>s を表示言語に直す。対応がなければそのまま返す。</summary>
    public static string T(string? s)
    {
        if (!English || string.IsNullOrEmpty(s)) return s ?? "";
        if (_exact.TryGetValue(s, out var en)) return en;
        if (!HasJapanese(s)) return s;
        // 型紙との照合は重いので、結果を覚えておく (同じ文字列が毎フレーム来る)
        lock (Memo)
        {
            if (Memo.TryGetValue(s, out var known)) return known;
        }
        string result = s;
        foreach (var (pattern, template) in _patterns)
        {
            var m = pattern.Match(s);
            if (!m.Success) continue;
            result = Regex.Replace(template, @"\{(\d)}", x =>
            {
                var value = m.Groups["g" + x.Groups[1].Value].Value;
                return _exact.TryGetValue(value, out var inner) ? inner : value;
            });
            break;
        }
        lock (Memo)
        {
            if (Memo.Count > 4000) Memo.Clear();
            Memo[s] = result;
        }
        return result;
    }

    private static bool HasJapanese(string s)
    {
        foreach (char c in s)
            if (c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿') return true;
        return false;
    }

    /// <summary>
    /// 型紙 (例: "シーン [{0}]") だけを訳し、{0} {1} ... には args をそのまま入れる。
    /// 利用者が付けた名前を含む文を作るときに使う (T に渡すと、名前の部分まで訳されてしまう)。
    /// 結果を TextBlock に出すときは Verbatim を使う。
    /// </summary>
    public static string Format(string template, params string[] args)
    {
        var t = English && _templates.TryGetValue(template, out var en) ? en : template;
        return System.Text.RegularExpressions.Regex.Replace(t, @"\{(\d)}", m =>
        {
            int i = m.Groups[1].Value[0] - '0';
            return i < args.Length ? args[i] : m.Value;
        });
    }

    /// <summary>
    /// true の TextBlock は訳さない (利用者が入力した名前など)。Text より先に設定すること。Verbatim(...) で作るのが簡単。
    /// </summary>
    public static readonly AttachedProperty<bool> VerbatimProperty = AvaloniaProperty.RegisterAttached<TextBlock, bool>("Verbatim", typeof(L));

    /// <summary>訳さない TextBlock を作る (利用者が入力した文字を表示する)。</summary>
    public static TextBlock Verbatim(string text)
    {
        var tb = new TextBlock();
        tb.SetValue(VerbatimProperty, true);
        tb.Text = text;
        return tb;
    }

    private static void Install()
    {
        if (_installed) return;
        _installed = true;
        // メニュー・ボタン・ツールチップ・入力欄の説明なども、最後は TextBlock に文字が入る
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((tb, e) =>
        {
            var text = e.NewValue as string;
            if (text == null || tb.GetValue(VerbatimProperty)) return;
            var translated = T(text);
            if (!ReferenceEquals(translated, text) && translated != text) tb.SetCurrentValue(TextBlock.TextProperty, translated);
        });
        Window.TitleProperty.Changed.AddClassHandler<Window>((w, e) =>
        {
            var text = e.NewValue as string;
            if (text == null) return;
            var translated = T(text);
            if (translated != text) w.SetCurrentValue(Window.TitleProperty, translated);
        });
    }
}
