using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace VideoBuilder.App.Controls;

/// <summary>
/// ファイルをドロップして受け取る部品 (プロパティのファイルの欄など)。
/// Windows・macOS では Avalonia の DragDrop で受け取る。X11 では Avalonia がほかのアプリからのドロップを受け取れないので、
/// MainWindow の自前の受け取り口が、落とした位置で HandleExternal を呼んで振り分ける。
/// </summary>
public static class FileDropZone
{
    private sealed class Zone
    {
        public required Func<string, bool> Accepts;
        public required Action<string> Drop;
        public required Action<bool> Highlight;
    }

    private static readonly ConditionalWeakTable<Control, Zone> Zones = new();
    private static Control? _highlighted;

    /// <summary>control をドロップの受け取り口にする。accepts に合う最初のファイルを drop に渡す。</summary>
    public static void Attach(Control control, Func<string, bool> accepts, Action<string> drop, Action<bool> highlight)
    {
        Zones.AddOrUpdate(control, new Zone { Accepts = accepts, Drop = drop, Highlight = highlight });
        DragDrop.SetAllowDrop(control, true);
        control.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            bool ok = FilesOf(e).Any(accepts);
            e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            Highlight(ok ? control : null);
            e.Handled = true;
        });
        control.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(null));
        control.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            Highlight(null);
            var file = FilesOf(e).FirstOrDefault(accepts);
            if (file != null) drop(file);
            e.Handled = true;
        });
    }

    private static IEnumerable<string> FilesOf(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!) ?? Enumerable.Empty<string>();

    /// <summary>ドラッグ中の枠の表示を、control だけに付ける (null なら消す)。</summary>
    public static void Highlight(Control? control)
    {
        if (_highlighted == control) return;
        if (_highlighted != null && Zones.TryGetValue(_highlighted, out var old)) old.Highlight(false);
        _highlighted = control;
        if (control != null && Zones.TryGetValue(control, out var z)) z.Highlight(true);
    }

    /// <summary>window の point (Avalonia の座標) の下にある受け取り口。なければ null。</summary>
    public static Control? FindAt(Window window, Point point)
    {
        if (window.InputHitTest(point) is not Visual hit) return null;
        for (Visual? v = hit; v != null; v = v.GetVisualParent())
            if (v is Control c && Zones.TryGetValue(c, out _) && c.IsEffectivelyVisible && c.IsEffectivelyEnabled) return c;
        return null;
    }

    /// <summary>
    /// ほかのアプリからのドラッグ (files が null) / ドロップを処理する。受け取り口の上なら true (タイムラインには渡さない)。
    /// ドロップで合うファイルがなければ、何もせずに true を返す。
    /// </summary>
    public static bool HandleExternal(Window window, Point point, IReadOnlyList<string>? files)
    {
        var target = FindAt(window, point);
        if (target == null || !Zones.TryGetValue(target, out var zone)) { Highlight(null); return false; }
        if (files == null) { Highlight(target); return true; }
        Highlight(null);
        var file = files.FirstOrDefault(zone.Accepts);
        if (file != null) zone.Drop(file);
        return true;
    }
}
