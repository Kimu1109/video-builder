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
            await CheckMissingFilesAsync();
            if (Svc.Settings.AutoProxy) Svc.Proxies.EnqueueProject(Session.Project, heavyOnly: true);
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

    // ---------------- 範囲・グリッド・エイリアス・素材 ----------------

    public static void SetRangeStart() => Session.SetRange(Session.CurrentFrame, Session.Scene.RangeEnd);
    public static void SetRangeEnd() => Session.SetRange(Session.Scene.RangeStart, Session.CurrentFrame);
    public static void ClearRange() => Session.SetRange(null, null);

    public static void SetGridMode(GridMode mode) => Session.Edit("グリッドの切替", () => Session.Project.Grid.Mode = mode);

    public static async Task GridSettingsAsync()
    {
        if (Main != null) await new GridDialog().ShowDialog(Main);
    }

    public static async Task CreateAliasAsync()
    {
        var targets = Session.WithGroup(Session.SelectedObjects.ToList());
        if (Main == null || targets.Count == 0) return;
        var name = await PromptDialog.Show(Main, "エイリアスを作成", "エイリアスの名前", TimelineCanvasLabel(targets[0]));
        if (name == null) return;
        if (Svc.Aliases.Exists(name) &&
            await MessageDialog.Show(Main, "エイリアスを作成", $"「{AliasStore.SafeName(name)}」はすでにあります。上書きしますか?", ("キャンセル", false), ("上書き", true)) != "上書き") return;
        try { Svc.Aliases.Save(name, targets); }
        catch (Exception ex) { await ShowErrorAsync("エイリアスを保存できませんでした。\n" + ex.Message); }
    }

    private static string TimelineCanvasLabel(TimelineObject o) => Controls.TimelineCanvas.ObjectLabel(o);

    public static void InsertAlias(string name, int layer, int frame)
    {
        try
        {
            if (Session.Insert(Svc.Aliases.Load(name), layer, frame, "エイリアスの追加") == 0)
                _ = ShowErrorAsync("空いているレイヤーがありません");
        }
        catch (Exception ex) { _ = ShowErrorAsync("エイリアスを読み込めませんでした。\n" + ex.Message); }
    }

    /// <summary>見つからない素材があれば、付け替えのダイアログを出す。</summary>
    public static async Task CheckMissingFilesAsync(bool always = false)
    {
        if (Main == null) return;
        if (!always && ProjectMedia.FindMissing(Session.Project).Count == 0) return;
        await new MissingFilesDialog().ShowDialog(Main);
    }

    /// <summary>素材をプロジェクトの隣のフォルダに集めてから保存する。</summary>
    public static async Task CollectAndSaveAsync()
    {
        if (Main == null) return;
        if (Session.FilePath == null && !await SaveAsAsync()) return;
        string path = Session.FilePath!;
        string dir = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path) + "_media");
        int copied = 0;
        try
        {
            Session.Edit("素材の収集", () => copied = ProjectMedia.Collect(Session.Project, dir));
            if (!await SaveToAsync(path)) return;
        }
        catch (Exception ex) { await ShowErrorAsync("素材を収集できませんでした。\n" + ex.Message); return; }
        var missing = ProjectMedia.FindMissing(Session.Project).Count;
        await MessageDialog.Show(Main, "素材を収集して保存",
            $"{copied} 個のファイルを次のフォルダにコピーしました。\n{dir}" + (missing > 0 ? $"\n\n見つからない素材が {missing} 個あり、それらはコピーしていません。" : ""));
    }

    // ---------------- ショートカット ----------------

    public static void RegisterCommands()
    {
        Shortcuts.Register(
            new("file.new", "新規プロジェクト", "Ctrl+N", () => _ = NewAsync(), true),
            new("file.open", "開く", "Ctrl+O", () => _ = OpenAsync(), true),
            new("file.save", "保存", "Ctrl+S", () => _ = SaveAsync(), true),
            new("file.saveAs", "名前を付けて保存", "Ctrl+Shift+S", () => _ = SaveAsAsync(), true),
            new("file.export", "動画の出力", "Ctrl+E", () => _ = ExportAsync(), true),
            new("edit.undo", "元に戻す", "Ctrl+Z", () => Session.Undo()),
            new("edit.redo", "やり直し", "Ctrl+Y;Ctrl+Shift+Z", () => Session.Redo()),
            new("edit.cut", "切り取り", "Ctrl+X", () => Session.CutSelected()),
            new("edit.copy", "コピー", "Ctrl+C", () => Session.CopySelected()),
            new("edit.paste", "貼り付け", "Ctrl+V", Paste),
            new("edit.delete", "削除", "Delete", () => Session.DeleteSelected()),
            new("edit.selectAll", "すべて選択", "Ctrl+A", () => Session.SelectMany(Session.Scene.Objects.Where(o => !Session.Scene.GetLayer(o.Layer).Locked).Select(o => o.Id))),
            new("edit.deselect", "選択を解除", "Escape", () => Session.Select(null)),
            new("edit.split", "現在位置で分割", "S", Split),
            new("edit.addKey", "中間点を追加", "P", AddKeyFrame),
            new("play.toggle", "再生 / 停止", "Space", () => Svc.Preview.TogglePlay()),
            new("move.prev", "1 フレーム戻る", "Left", () => Step(-1)),
            new("move.next", "1 フレーム進む", "Right", () => Step(1)),
            new("move.prev10", "10 フレーム戻る", "Shift+Left", () => Step(-10)),
            new("move.next10", "10 フレーム進む", "Shift+Right", () => Step(10)),
            new("move.prevEdit", "前の編集点へ", "Ctrl+Left", () => Seek(Session.FindEditPoint(Session.CurrentFrame, -1))),
            new("move.nextEdit", "次の編集点へ", "Ctrl+Right", () => Seek(Session.FindEditPoint(Session.CurrentFrame, 1))),
            new("move.home", "先頭へ", "Home", () => Seek(0)),
            new("move.end", "末尾へ", "End", () => Seek(Math.Max(0, Session.SceneLength - 1))),
            new("range.start", "出力範囲の開始を現在位置に", "OemOpenBrackets", SetRangeStart),
            new("range.end", "出力範囲の終了を現在位置に", "OemCloseBrackets", SetRangeEnd),
            new("range.clear", "出力範囲を解除", "", ClearRange),
            new("alias.create", "エイリアスを作成", "", () => _ = CreateAliasAsync()));
    }

    /// <summary>キー入力を処理したら true。文字入力中 (TextBox) は編集用のキーを横取りしない。</summary>
    public static bool HandleKey(KeyEventArgs e, TopLevel top)
    {
        if (Shortcuts.All.Count == 0) RegisterCommands();
        return Shortcuts.Handle(e, top);
    }

    // ---------------- 自動保存 ----------------

    public static void StartAutoSave()
    {
        _autoSave = new DispatcherTimer(TimeSpan.FromMinutes(Math.Max(1, Svc.Settings.AutoSaveMinutes)), DispatcherPriority.Background, (_, _) => _ = AutoSaveNow());
        _autoSave.Start();
    }

    private static long _lastAutoSavedVersion = -1;
    private static bool _autoSaving;

    /// <summary>
    /// 前回の自動保存から変更があれば、別スレッドで保存する。
    /// 画面側では「今の状態の複製」を受け取るだけなので、大きなプロジェクトでも操作が止まらない。
    /// </summary>
    public static Task AutoSaveNow()
    {
        if (!Svc.Settings.AutoSave || !Session.IsDirty || Session.IsEditing || _autoSaving) return Task.CompletedTask;
        long version = Session.History.Version;
        if (version == _lastAutoSavedVersion) return Task.CompletedTask;
        var snapshot = Session.SnapshotForRead();
        // 保存処理は素材の相対パス表を書き換えるので、中身 (シーンなど) だけを共有する別の入れ物を渡す
        var copy = new Project { FormatVersion = snapshot.FormatVersion, Settings = snapshot.Settings, Scenes = snapshot.Scenes, Grid = snapshot.Grid };
        string name = Session.FilePath != null ? Path.GetFileNameWithoutExtension(Session.FilePath) : "untitled";
        string dir = AppPaths.AutoSaveDir;
        _autoSaving = true;
        return Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                ProjectSerializer.Save(copy, Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{ProjectSerializer.Extension}"));
                _lastAutoSavedVersion = version;
                // 10 世代だけ残す
                foreach (var old in new DirectoryInfo(dir).GetFiles("*" + ProjectSerializer.Extension).OrderByDescending(f => f.LastWriteTimeUtc).Skip(10))
                    old.Delete();
            }
            catch { /* 自動保存の失敗で編集を止めない */ }
            finally { _autoSaving = false; }
        });
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
