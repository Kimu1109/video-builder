using Avalonia;
using Avalonia.Controls;
using VideoBuilder.App.Services;
using VideoBuilder.Core;

namespace VideoBuilder.App.Shell;

/// <summary>
/// シェル (メインウィンドウ) が編集画面に渡すもの。メニュー・ツールバー・プレビューなど、どの編集画面でも共通の部分。
/// </summary>
public interface IShell
{
    Window Window { get; }
    /// <summary>プレビューと再生バー。どちらの編集画面でも同じものを使う (有効になった編集画面が自分の中に置く)。</summary>
    PreviewArea PreviewArea { get; }
    /// <summary>選択などに応じて、使えるかどうか (IsEnabled) を切り替えるコントロールとして登録する。</summary>
    T Track<T>(T control, Func<bool> enabled) where T : Control;
    /// <summary>ほかのアプリからのドロップを、このウィンドウでも受け取れるようにする (X11)。</summary>
    void AttachDropTarget(Window window);
    /// <summary>Track で登録したコントロールの「使えるかどうか」を今の状態に合わせる (編集画面が自分の選択を変えたときなど)。</summary>
    void RefreshEnabled();
}

/// <summary>
/// 編集画面 (ワークスペース)。シェルの中央に置かれ、編集メニュー・ツールバーの中身と、編集の操作を受け持つ。
/// <list type="bullet">
/// <item>詳細編集 (<see cref="Workspaces.Advanced.AdvancedWorkspace"/>): タイムラインとプロパティ。AviUtl 風</item>
/// <item>シンプル編集 (<see cref="Workspaces.Simple.SimpleWorkspace"/>): ストーリーボード。ムービーメーカー風</item>
/// </list>
/// どちらも同じプロジェクト (<see cref="Editing.EditorSession"/>) を編集し、取り消し・保存・出力の仕組みは共通。
/// </summary>
public interface IWorkspace
{
    EditorMode Mode { get; }
    /// <summary>シェルの中央に置く中身。</summary>
    Control View { get; }

    /// <summary>この編集画面に切り替わった。共通のプレビューを自分の中に置く。</summary>
    void Activate(IShell shell);
    /// <summary>ほかの編集画面に切り替わる。共通のプレビューを外し、別ウィンドウがあれば隠す。</summary>
    void Deactivate();

    /// <summary>「編集」メニューの中身 (元に戻す・やり直しはシェルが先頭に置く)。</summary>
    IEnumerable<object> BuildEditMenu(IShell shell);
    /// <summary>「表示」メニューに足す項目。</summary>
    IEnumerable<object> BuildViewMenu(IShell shell);
    /// <summary>ツールバーの中ほど (ファイル・取り消しのボタンの後ろ) に並べるボタン。</summary>
    IEnumerable<Control> BuildToolbar(IShell shell);

    /// <summary>この編集画面が開いている別ウィンドウ (表示中のもの)。</summary>
    IEnumerable<Window> ToolWindows { get; }

    // ---- 編集の操作 (メニュー・ツールバー・ショートカットから呼ばれる) ----
    bool HasSelection { get; }
    bool CanPaste { get; }
    void Cut();
    void Copy();
    void Paste();
    void Delete();
    void Split();
    void SelectAll();
    void Deselect();

    /// <summary>ファイルを選んで読み込む (「メディアファイルを読み込む」)。</summary>
    Task ImportFilesAsync();
    /// <summary>
    /// ほかのアプリからのドラッグ (files が null) / ドロップ。point は window の中の位置。
    /// プロパティのファイルの欄などへのドロップは、シェルが先に振り分ける。
    /// </summary>
    void ExternalDrag(Window window, Point point, IReadOnlyList<string>? files);
    /// <summary>ドラッグが外れた。</summary>
    void ExternalDragLeft();
    /// <summary>現在位置が移った (シークした) ので、見える所までスクロールする。</summary>
    void RevealFrame(int frame);
    /// <summary>プロジェクト設定 (画像サイズ・フレームレート) を変える (1 回の取り消し)。</summary>
    void ChangeProjectSettings(ProjectSettings settings);
}
