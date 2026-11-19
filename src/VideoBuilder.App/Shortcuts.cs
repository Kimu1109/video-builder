using Avalonia.Controls;
using Avalonia.Input;
using VideoBuilder.App.Services;

namespace VideoBuilder.App;

/// <summary>ショートカットを割り当てられる操作。</summary>
/// <param name="DefaultGestures">既定のキー。複数あるときは ";" で区切る。</param>
/// <param name="WhileTyping">文字入力中 (TextBox にフォーカスがあるとき) も効くか。</param>
/// <param name="Mode">この編集画面のときだけ効く。null ならどちらでも効く。</param>
public sealed record CommandInfo(string Id, string Name, string DefaultGestures, Action Run, bool WhileTyping = false, EditorMode? Mode = null);

/// <summary>操作とキーの対応表。利用者が変えた分は設定ファイルに保存する。</summary>
public static class Shortcuts
{
    private static readonly List<CommandInfo> Commands = new();
    private static Dictionary<string, List<KeyGesture>>? _resolved;

    public static IReadOnlyList<CommandInfo> All => Commands;

    public static void Register(params CommandInfo[] commands)
    {
        foreach (var c in commands)
            if (Commands.All(x => x.Id != c.Id)) Commands.Add(c);
        _resolved = null;
    }

    /// <summary>今の割り当て (";" 区切り)。空文字なら割り当てなし。</summary>
    public static string GesturesOf(string id)
    {
        if (Svc.Settings.Shortcuts.TryGetValue(id, out var custom)) return custom;
        return Commands.FirstOrDefault(c => c.Id == id)?.DefaultGestures ?? "";
    }

    /// <summary>メニューに表示するキー (最初の 1 つ)。</summary>
    public static string? First(string id)
    {
        var g = GesturesOf(id).Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(g) ? null : g;
    }

    public static string Display(string id) =>
        string.Join(" / ", Parse(GesturesOf(id)).Select(DisplayOf));

    public static string DisplayOf(KeyGesture g)
    {
        string key = g.Key switch
        {
            Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemComma => ",", Key.OemPeriod => ".",
            Key.OemMinus => "-", Key.OemPlus => "+", Key.OemSemicolon => ";", Key.OemQuestion => "/",
            Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
            _ => g.Key.ToString(),
        };
        var parts = new List<string>();
        if (g.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(key);
        return string.Join("+", parts);
    }

    private static List<KeyGesture> Parse(string gestures)
    {
        var list = new List<KeyGesture>();
        foreach (var g in gestures.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { list.Add(KeyGesture.Parse(g)); }
            catch { /* 壊れた設定は無視 */ }
        }
        return list;
    }

    /// <summary>id のキーを gesture に変える (null で割り当てなし)。同じキーを使っていた別の操作からは外す。外した操作の名前を返す。</summary>
    public static string? Assign(string id, KeyGesture? gesture)
    {
        string? stolen = null;
        if (gesture != null)
        {
            var mode = Commands.FirstOrDefault(c => c.Id == id)?.Mode;
            foreach (var c in Commands)
            {
                if (c.Id == id) continue;
                // 別の編集画面だけで効く操作とは、同じキーを使ってよい
                if (mode != null && c.Mode != null && c.Mode != mode) continue;
                var others = Parse(GesturesOf(c.Id));
                if (!others.Any(o => o.Key == gesture.Key && o.KeyModifiers == gesture.KeyModifiers)) continue;
                Svc.Settings.Shortcuts[c.Id] = string.Join(";", others.Where(o => !(o.Key == gesture.Key && o.KeyModifiers == gesture.KeyModifiers)).Select(o => o.ToString()));
                stolen = c.Name;
            }
        }
        Svc.Settings.Shortcuts[id] = gesture?.ToString() ?? "";
        _resolved = null;
        Svc.Settings.Save();
        return stolen;
    }

    public static void Reset(string? id = null)
    {
        if (id == null) Svc.Settings.Shortcuts.Clear(); else Svc.Settings.Shortcuts.Remove(id);
        _resolved = null;
        Svc.Settings.Save();
    }

    /// <summary>キー入力に対応する操作があれば実行して true を返す。</summary>
    public static bool Handle(KeyEventArgs e, TopLevel top)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return false;
        _resolved ??= Commands.ToDictionary(c => c.Id, c => Parse(GesturesOf(c.Id)));
        bool typing = top.FocusManager?.GetFocusedElement() is TextBox;
        var mods = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        foreach (var c in Commands)
        {
            if (typing && !c.WhileTyping) continue;
            if (c.Mode != null && c.Mode != EditorCommands.CurrentMode) continue;
            foreach (var g in _resolved[c.Id])
            {
                if (g.Key != e.Key || g.KeyModifiers != mods) continue;
                c.Run();
                return true;
            }
        }
        return false;
    }
}
