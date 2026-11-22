using Avalonia.Controls;

namespace VideoBuilder.App.Shell;

/// <summary>タイムラインや設定ダイアログを入れる、別ウィンドウ用の枠。閉じても隠すだけ。</summary>
public sealed class ToolWindow : Window
{
    public bool AllowClose { get; set; }

    public ToolWindow(string title, double width, double height)
    {
        Title = title; Width = width; Height = height;
        Icon = AppIcon.Get();
        ShowInTaskbar = false;
        KeyDown += (_, e) => { if (EditorCommands.HandleKey(e, this)) e.Handled = true; };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // このウィンドウだけを閉じようとしたとき (閉じるボタンなど) は、閉じずに隠す。
        // メインウィンドウの終了やアプリの終了に伴うときは、そのまま閉じる。
        // ここで閉じるのを取りやめると、メインウィンドウの終了処理 (保存の確認) まで届かず、終了できなくなる
        // (Avalonia は、持ち主のウィンドウを閉じる前に、子ウィンドウに「閉じてよいか」を問うため)。
        if (!AllowClose && e.CloseReason == WindowCloseReason.WindowClosing) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
