using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoBuilder.App.Services;
using VideoBuilder.App.Views;
using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App;

/// <summary>メニュー・ツールバー・ショートカットから呼ぶ操作をまとめたもの。</summary>
public static class EditorCommands
{
    public static MainWindow? Main { get; set; }
    private static EditorSession Session => Svc.Session;
    private static DispatcherTimer? _autoSave;

    private static readonly FilePickerFileType ProjectType = new("video-builder プロジェクト") { Patterns = new[] { "*" + ProjectSerializer.Extension } };

    // ---------------- ファイル ----------------

    /// <summary>未保存の変更があれば確認する。続けてよければ true。</summary>
    public static async Task<bool> ConfirmDiscardAsync()
    {
        if (!Session.IsDirty || Main == null) return true;
        var r = await MessageDialog.Show(Main, "video-builder", "変更が保存されていません。保存しますか?", ("キャンセル", false), ("保存しない", false), ("保存", true));
        if (r == "保存") return await SaveAsync();
        return r == "保存しない";
    }

    public static async Task NewAsync()
    {
        if (Main == null || !await ConfirmDiscardAsync()) return;
        var d = new ProjectSettingsDialog(new ProjectSettings(), isNew: true);
        await d.ShowDialog(Main);
        if (d.Result == null) return;
        Svc.Preview.Stop();
        Session.NewProject(d.Result);
    }

    public static async Task OpenAsync()
    {
        if (Main == null || !await ConfirmDiscardAsync()) return;
        var files = await Main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "プロジェクトを開く", FileTypeFilter = new[] { ProjectType, FilePickerFileTypes.All } });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) await OpenPathAsync(path);
    }

    public static async Task OpenPathAsync(string path)
    {
        Svc.Preview.Stop();
        try
        {
            Session.Load(path);
            Svc.Settings.AddRecent(path);
        }
        catch (Exception ex)
        {
            if (Main != null) await MessageDialog.Show(Main, "開けません", $"{Path.GetFileName(path)} を開けませんでした。\n{ex.Message}");
        }
    }

    public static async Task<bool> SaveAsync()
    {
        if (Session.FilePath == null) return await SaveAsAsync();
        return await SaveToAsync(Session.FilePath);
    }

    public static async Task<bool> SaveAsAsync()
    {
        if (Main == null) return false;
        var file = await Main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "名前を付けて保存", DefaultExtension = ProjectSerializer.Extension.TrimStart('.'),
            SuggestedFileName = Session.FilePath != null ? Path.GetFileName(Session.FilePath) : "project" + ProjectSerializer.Extension,
            FileTypeChoices = new[] { ProjectType },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return false;
        if (!path.EndsWith(ProjectSerializer.Extension, StringComparison.OrdinalIgnoreCase)) path += ProjectSerializer.Extension;
        return await SaveToAsync(path);
    }

    private static async Task<bool> SaveToAsync(string path)
    {
        try
        {
            Session.Save(path);
            Svc.Settings.AddRecent(path);
            return true;
        }
        catch (Exception ex)
        {
            if (Main != null) await MessageDialog.Show(Main, "保存できません", ex.Message);
            return false;
        }
    }

    /// <summary>ファイル選択ダイアログからメディアを読み込んで、タイムラインに置く。</summary>
    public static async Task ImportFilesAsync(int layer, int frame, string[]? extensions = null, string title = "メディアファイル")
    {
        if (Main == null) return;
        extensions ??= EffectRegistry.VideoExtensions.Concat(EffectRegistry.ImageExtensions).Concat(EffectRegistry.AudioExtensions).ToArray();
        var files = await Main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title + "を選択", AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType(title) { Patterns = extensions.Select(x => "*." + x).ToArray() }, FilePickerFileTypes.All },
        });
        var errors = new List<string>();
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (path == null) continue;
            var error = MediaImport.AddFile(path, layer, frame);
            if (error != null) errors.Add(error);
            else if (Session.Primary is { } added) layer = Math.Min(Scene.LayerCount - 1, added.Layer + (added.GroupId != null ? 2 : 1));
        }
        if (errors.Count > 0) await ShowErrorAsync(string.Join("\n", errors));
    }

    public static Task ShowErrorAsync(string message) =>
        Main == null ? Task.CompletedTask : MessageDialog.Show(Main, "video-builder", message);

    // ---------------- ダイアログ ----------------

    public static async Task ExportAsync()
    {
        if (Main != null) await new ExportDialog().ShowDialog(Main);
    }

    public static async Task PreferencesAsync()
    {
        if (Main != null) await new PreferencesDialog().ShowDialog(Main);
    }

    public static async Task AboutAsync()
    {
        if (Main != null) await new AboutDialog().ShowDialog(Main);
    }

    public static async Task ProjectSettingsAsync()
    {
        if (Main == null) return;
        var d = new ProjectSettingsDialog(Session.Project.Settings, isNew: false);
        await d.ShowDialog(Main);
        if (d.Result == null) return;
        Svc.Preview.Stop();
        Session.Edit("プロジェクト設定の変更", () => Session.Project.Settings = d.Result);
    }

    public static void ShowSettings() => Main?.ShowSettingsPanel();

    // ---------------- 編集 ----------------

    public static void AddKeyFrame() => Session.AddKeyFrameToSelected(Session.CurrentFrame);
    public static void Split() => Session.SplitSelectedAt(Session.CurrentFrame);

    public static void Paste()
    {
        int layer = Main?.Timeline.LastLayer ?? 0;
        Session.Paste(layer, Session.CurrentFrame);
    }

    public static void Step(int frames)
    {
        Svc.Preview.Stop();
        Session.CurrentFrame = Math.Clamp(Session.CurrentFrame + frames, 0, Math.Max(0, Session.SceneLength - 1 + Math.Max(0, frames)));
        Main?.Timeline.Canvas.EnsureFrameVisible(Session.CurrentFrame);
    }

    public static void Seek(int frame)
    {
        Svc.Preview.Stop();
        Session.CurrentFrame = Math.Max(0, frame);
        Main?.Timeline.Canvas.EnsureFrameVisible(Session.CurrentFrame);
    }

    public static void SetTheme(ThemeMode mode)
    {
        Svc.Settings.Theme = mode;
        Svc.Settings.Save();
        Svc.Theme.Apply();
    }

    // ---------------- ショートカット ----------------

    /// <summary>キー入力を処理したら true。文字入力中 (TextBox) は編集用のキーを横取りしない。</summary>
    public static bool HandleKey(KeyEventArgs e, TopLevel top)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool typing = top.FocusManager?.GetFocusedElement() is TextBox;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.S: _ = shift ? SaveAsAsync() : SaveAsync(); return true;
                case Key.O: _ = OpenAsync(); return true;
                case Key.N: _ = NewAsync(); return true;
                case Key.E: _ = ExportAsync(); return true;
            }
            if (typing) return false;
            switch (e.Key)
            {
                case Key.Z: if (shift) Session.Redo(); else Session.Undo(); return true;
                case Key.Y: Session.Redo(); return true;
                case Key.C: Session.CopySelected(); return true;
                case Key.X: Session.CutSelected(); return true;
                case Key.V: Paste(); return true;
                case Key.A: Session.SelectMany(Session.Scene.Objects.Where(o => !Session.Scene.GetLayer(o.Layer).Locked).Select(o => o.Id)); return true;
                case Key.Left: Seek(Session.FindEditPoint(Session.CurrentFrame, -1)); return true;
                case Key.Right: Seek(Session.FindEditPoint(Session.CurrentFrame, 1)); return true;
            }
            return false;
        }
        if (typing || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        switch (e.Key)
        {
            case Key.Space: Svc.Preview.TogglePlay(); return true;
            case Key.Left: Step(shift ? -10 : -1); return true;
            case Key.Right: Step(shift ? 10 : 1); return true;
            case Key.Home: Seek(0); return true;
            case Key.End: Seek(Math.Max(0, Session.SceneLength - 1)); return true;
            case Key.Delete: Session.DeleteSelected(); return true;
            case Key.P: AddKeyFrame(); return true;
            case Key.S: Split(); return true;
            case Key.Escape: Session.Select(null); return true;
        }
        return false;
    }

    // ---------------- 自動保存 ----------------

    public static void StartAutoSave()
    {
        _autoSave = new DispatcherTimer(TimeSpan.FromMinutes(Math.Max(1, Svc.Settings.AutoSaveMinutes)), DispatcherPriority.Background, (_, _) => AutoSaveNow());
        _autoSave.Start();
    }

    private static int _lastAutoSaved = -1;

    public static void AutoSaveNow()
    {
        if (!Svc.Settings.AutoSave || !Session.IsDirty || Session.IsEditing) return;
        try
        {
            // 同じ内容を何度も書かないよう、内容のハッシュで判定する
            int hash = ProjectSerializer.ToJson(Session.Project).GetHashCode();
            if (hash == _lastAutoSaved) return;
            Directory.CreateDirectory(AppPaths.AutoSaveDir);
            string name = Session.FilePath != null ? Path.GetFileNameWithoutExtension(Session.FilePath) : "untitled";
            string path = Path.Combine(AppPaths.AutoSaveDir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{ProjectSerializer.Extension}");
            ProjectSerializer.Save(Session.Project, path);
            _lastAutoSaved = hash;
            // 10 世代だけ残す
            foreach (var old in new DirectoryInfo(AppPaths.AutoSaveDir).GetFiles("*" + ProjectSerializer.Extension).OrderByDescending(f => f.LastWriteTimeUtc).Skip(10))
                old.Delete();
        }
        catch { /* 自動保存の失敗で編集を止めない */ }
    }

    public static async Task OpenAutoSaveAsync()
    {
        if (Main == null || !await ConfirmDiscardAsync()) return;
        Directory.CreateDirectory(AppPaths.AutoSaveDir);
        var folder = await Main.StorageProvider.TryGetFolderFromPathAsync(AppPaths.AutoSaveDir);
        var files = await Main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "自動保存から復元", SuggestedStartLocation = folder, FileTypeFilter = new[] { ProjectType } });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        Svc.Preview.Stop();
        try
        {
            // 自動保存ファイルに上書きしないよう、「名前なし・未保存」の状態で開く
            var project = ProjectSerializer.Load(path);
            Session.NewProject(project.Settings);
            Session.Edit("自動保存から復元", () => Session.Project.Scenes = project.Scenes);
        }
        catch (Exception ex) { await ShowErrorAsync("復元できませんでした。\n" + ex.Message); }
    }
}
