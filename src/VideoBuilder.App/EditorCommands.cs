using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoBuilder.App.Services;
using VideoBuilder.App.Shell;
using VideoBuilder.App.Views;
using VideoBuilder.Core;
using VideoBuilder.Editing;

namespace VideoBuilder.App;

/// <summary>メニュー・ツールバー・ショートカットから呼ぶ操作をまとめたもの。</summary>
public static class EditorCommands
{
    public static MainWindow? Main { get; set; }
    /// <summary>今の編集画面。</summary>
    public static IWorkspace? Workspace => Main?.Workspace;
    public static EditorMode CurrentMode => Main?.Workspace.Mode ?? Svc.Settings.EditorMode;
    private static EditorSession Session => Svc.Session;
    private static DispatcherTimer? _autoSave;

    private static readonly FilePickerFileType ProjectType = new("video-builder プロジェクト") { Patterns = new[] { "*" + ProjectSerializer.Extension } };

    // ---------------- プラグイン ----------------

    /// <summary>プラグインのコマンドに渡す窓口を作る。</summary>
    public static PluginCommandContext PluginContext(VideoBuilder.Rendering.PluginCommandEntry entry) =>
        new(Session, Main, entry.PluginId, AppPaths.PluginDataDir, Svc.Templates, Svc.Settings.FitTemplateResolution);

    /// <summary>プラグインのコマンドを実行する (「プラグイン」メニューから)。</summary>
    public static Task RunPluginCommandAsync(VideoBuilder.Rendering.PluginCommandEntry entry)
    {
        if (Session.IsEditing) return Task.CompletedTask;
        return PluginMenu.RunAsync(entry, PluginContext(entry), Main);
    }

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
            if (Main != null) await MessageDialog.Show(Main, "開けません", $"{L.Name(Path.GetFileName(path))} を開けませんでした。\n{ex.Message}");
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

    /// <summary>ファイル選択ダイアログで、メディアファイルを (いくつでも) 選ぶ。</summary>
    public static async Task<List<string>> PickMediaFilesAsync(string[] extensions, string title)
    {
        if (Main == null) return new List<string>();
        var files = await Main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title + "を選択", AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType(title) { Patterns = extensions.Select(x => "*." + x).ToArray() }, FilePickerFileTypes.All },
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
    }

    /// <summary>ファイル選択ダイアログからメディアを読み込んで、タイムラインに置く (詳細編集)。</summary>
    public static async Task ImportFilesAsync(int layer, int frame, string[]? extensions = null, string title = "メディアファイル")
    {
        if (Main == null) return;
        extensions ??= EffectRegistry.VideoExtensions.Concat(EffectRegistry.ImageExtensions).Concat(EffectRegistry.AudioExtensions).Concat(EffectRegistry.VectorExtensions).ToArray();
        var errors = new List<string>();
        foreach (var path in await PickMediaFilesAsync(extensions, title))
        {
            var error = MediaImport.AddFile(path, layer, frame);
            if (error != null) errors.Add(error);
            else if (Session.Primary is { } added) layer = Math.Min(Scene.LayerCount - 1, added.Layer + (added.GroupId != null ? 2 : 1));
        }
        if (errors.Count > 0) await ShowErrorAsync(string.Join("\n", errors));
    }

    // ---------------- 字幕 ----------------

    private static readonly FilePickerFileType SubtitleType = new("字幕 (SRT / WebVTT)") { Patterns = Subtitles.Extensions.Select(x => "*." + x).ToArray() };

    /// <summary>字幕ファイルを読み込んで、テキストオブジェクトとして並べる。テキストを選んでいれば、その見た目をひな形にする。</summary>
    public static async Task ImportSubtitlesAsync()
    {
        if (Main == null) return;
        var files = await Main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "字幕を読み込む", FileTypeFilter = new[] { SubtitleType, FilePickerFileTypes.All } });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        List<SubtitleCue> cues;
        try { cues = Subtitles.Parse(await File.ReadAllTextAsync(path)); }
        catch (Exception ex) { await ShowErrorAsync($"{L.Name(Path.GetFileName(path))} を開けませんでした。\n{ex.Message}"); return; }
        if (cues.Count == 0) { await ShowErrorAsync($"字幕が見つかりません: {L.Name(Path.GetFileName(path))}"); return; }
        var primary = Session.Primary;
        var style = primary?.Main.DefinitionId == EffectRegistry.Text ? primary : null;
        if (Session.ImportSubtitles(cues, primary?.Layer ?? 0, style) < 0) await ShowErrorAsync("空いているレイヤーがありません");
    }

    /// <summary>選んでいるオブジェクトのレイヤー (なければレイヤー 1) のテキストを、字幕ファイルに書き出す。</summary>
    public static async Task ExportSubtitlesAsync()
    {
        if (Main == null) return;
        int layer = Session.Primary?.Layer ?? 0;
        var cues = Session.ExportSubtitles(layer);
        if (cues.Count == 0) { await ShowErrorAsync($"レイヤー {layer + 1} にテキストオブジェクトがありません"); return; }
        var file = await Main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "字幕を書き出す", DefaultExtension = "srt",
            SuggestedFileName = (Session.FilePath != null ? Path.GetFileNameWithoutExtension(Session.FilePath) : "subtitles") + ".srt",
            FileTypeChoices = new[] { new FilePickerFileType("SRT") { Patterns = new[] { "*.srt" } }, new FilePickerFileType("WebVTT") { Patterns = new[] { "*.vtt" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try { await File.WriteAllTextAsync(path, Subtitles.Write(path, cues), new System.Text.UTF8Encoding(false)); }
        catch (Exception ex) { await ShowErrorAsync("字幕を書き出せませんでした。\n" + ex.Message); }
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
        Workspace?.ChangeProjectSettings(d.Result);
    }

    public static void ShowSettings() => Main?.ShowSettingsPanel();

    private static PerformanceWindow? _performanceWindow;

    /// <summary>パフォーマンスチェックモードを切り替える。オンにしたら詳細の窓も開く。</summary>
    public static void TogglePerformanceMode()
    {
        Svc.Performance.Enabled = !Svc.Performance.Enabled;
    }

    /// <summary>パフォーマンスの詳細の窓を開く (モードがオフならオンにする)。</summary>
    public static void ShowPerformanceWindow()
    {
        if (Main == null) return;
        if (!Svc.Performance.Enabled) { Svc.Performance.Enabled = true; return; }   // オンにしたときに開く (OnPerformanceModeChanged)
        _performanceWindow ??= new PerformanceWindow();
        _performanceWindow.Open(Main);
    }

    /// <summary>モードが変わった: オンなら詳細の窓を開き、オフなら閉じる。</summary>
    public static void OnPerformanceModeChanged()
    {
        if (Svc.Performance.Enabled) ShowPerformanceWindow();
        else _performanceWindow?.Hide();
    }

    /// <summary>アプリの終了時。</summary>
    public static void ClosePerformanceWindow()
    {
        if (_performanceWindow == null) return;
        _performanceWindow.AllowClose = true;
        _performanceWindow.Close();
        _performanceWindow = null;
    }

    /// <summary>パフォーマンスの詳細の窓 (開いていなければ null)。</summary>
    public static PerformanceWindow? PerformanceWindow => _performanceWindow is { IsVisible: true } w ? w : null;

    /// <summary>編集画面 (詳細 / シンプル) を切り替えて、設定に覚える。</summary>
    public static void SetEditorMode(EditorMode mode)
    {
        Svc.Settings.EditorMode = mode;
        Svc.Settings.Save();
        Main?.SwitchWorkspace(mode);
    }

    /// <summary>詳細編集のウィンドウ構成 (分ける / まとめる) を設定に合わせる。</summary>
    public static void ApplyWindowMode() => Main?.ApplyWindowMode();

    // ---------------- 編集 ----------------

    public static void AddKeyFrame() => Session.AddKeyFrameToSelected(Session.CurrentFrame);
    public static void Split() => Workspace?.Split();

    public static void Paste() => Workspace?.Paste();

    public static void Step(int frames)
    {
        Svc.Preview.Stop();
        Session.CurrentFrame = Math.Clamp(Session.CurrentFrame + frames, 0, Math.Max(0, Session.SceneLength - 1 + Math.Max(0, frames)));
        Workspace?.RevealFrame(Session.CurrentFrame);
    }

    public static void Seek(int frame)
    {
        Svc.Preview.Stop();
        Session.CurrentFrame = Math.Max(0, frame);
        Workspace?.RevealFrame(Session.CurrentFrame);
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

    /// <summary>旧「プリセットとして保存」。今はテンプレートとして保存する (ショートカットの ID は互換のため変えない)。</summary>
    public static Task CreateAliasAsync() => SaveTemplateAsync();

    // ---------------- テンプレート ----------------

    /// <summary>選んだオブジェクト (連結しているものを含む) をテンプレートとして保存する。</summary>
    public static async Task SaveTemplateAsync()
    {
        var targets = Session.WithGroup(Session.SelectedObjects.ToList());
        if (Main == null || targets.Count == 0) return;
        var d = new TemplateSaveDialog(Session.Project, targets, TimelineCanvasLabel(targets[0]), Svc.Templates.Categories());
        await d.ShowDialog(Main);
        if (d.Result is not { } options) return;
        if (Svc.Templates.Exists(options.Name) &&
            await MessageDialog.Show(Main, "テンプレートとして保存", $"「{L.Name(options.Name)}」はすでにあります。上書きしますか?", ("キャンセル", false), ("上書き", true)) != "上書き") return;
        try
        {
            var pkg = TemplatePackage.Create(Session.Project, targets, options);
            pkg.Thumbnail = TemplateService.RenderThumbnail(pkg, Session.Project.Settings);
            pkg.Preview = TemplateService.RenderPreview(pkg, Session.Project.Settings);
            Svc.Templates.Save(pkg);
            TemplateService.ForgetVersions();
            Session.NotifyChanged();   // 「新しい版があります」の表示を更新する
        }
        catch (Exception ex) { await ShowErrorAsync("テンプレートを保存できませんでした。\n" + ex.Message); }
    }

    /// <summary>テンプレートを置く。mode が null なら、テンプレートに保存された置き方。</summary>
    public static void InsertTemplate(TemplateEntry entry, int layer, int frame, TemplateInsertMode? mode = null)
    {
        var (_, message) = TemplateService.Insert(entry, layer, frame, mode);
        if (message != null) _ = ShowErrorAsync(message);
    }

    /// <summary>シーンオブジェクト (テンプレートから作ったもの) を、元のテンプレートのいちばん新しい版にする。</summary>
    public static void UpdateTemplate(IReadOnlyList<TimelineObject> objects)
    {
        var (_, error) = TemplateService.Update(objects);
        if (error != null) _ = ShowErrorAsync(error);
    }

    public static async Task ShowTemplateLibraryAsync()
    {
        if (Main != null) await new TemplateLibraryDialog().ShowDialog(Main);
    }

    // ---------------- スタイル ----------------

    /// <summary>選択中のオブジェクトの effectIndex 番目のエフェクトを、スタイルとして保存する。</summary>
    public static async Task SaveStyleAsync(int effectIndex)
    {
        var o = Session.Primary;
        if (Main == null || o == null || effectIndex >= o.Effects.Count) return;
        var effect = o.Effects[effectIndex];
        var name = await PromptDialog.Show(Main, "スタイルとして保存", $"スタイルの名前 ({L.T(effect.Definition.Name)})", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (Svc.Styles.Exists(effect.DefinitionId, name) &&
            await MessageDialog.Show(Main, "スタイルとして保存", $"「{L.Name(name)}」はすでにあります。上書きしますか?", ("キャンセル", false), ("上書き", true)) != "上書き") return;
        try { Svc.Styles.Save(EffectStyle.From(effect, name)); }
        catch (Exception ex) { await ShowErrorAsync("スタイルを保存できませんでした。\n" + ex.Message); }
    }

    /// <summary>選んだオブジェクトすべてにスタイルを当てる。</summary>
    public static void ApplyStyle(string definitionId, string name)
    {
        try
        {
            if (Session.ApplyStyle(Svc.Styles.Load(definitionId, name)) == 0)
                _ = ShowErrorAsync("選んだオブジェクトには、このスタイルを当てられません");
        }
        catch (Exception ex) { _ = ShowErrorAsync("スタイルを読み込めませんでした。\n" + ex.Message); }
    }

    private static string TimelineCanvasLabel(TimelineObject o) => Labels.Object(o);

    public static void InsertAlias(string name, int layer, int frame)
    {
        try
        {
            if (Session.Insert(Svc.Aliases.Load(name), layer, frame, "プリセットの追加") == 0)
                _ = ShowErrorAsync("空いているレイヤーがありません");
        }
        catch (Exception ex) { _ = ShowErrorAsync("プリセットを読み込めませんでした。\n" + ex.Message); }
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
            $"{copied} 個のファイルを次のフォルダにコピーしました。\n{L.Name(dir)}" + (missing > 0 ? $"\n\n見つからない素材が {missing} 個あり、それらはコピーしていません。" : ""));
    }

    // ---------------- ショートカット ----------------

    public static void RegisterCommands()
    {
        const EditorMode Adv = EditorMode.Advanced;
        Shortcuts.Register(
            new("file.new", "新規プロジェクト", "Ctrl+N", () => _ = NewAsync(), true),
            new("file.open", "開く", "Ctrl+O", () => _ = OpenAsync(), true),
            new("file.save", "保存", "Ctrl+S", () => _ = SaveAsync(), true),
            new("file.saveAs", "名前を付けて保存", "Ctrl+Shift+S", () => _ = SaveAsAsync(), true),
            new("file.export", "動画の出力", "Ctrl+E", () => _ = ExportAsync(), true),
            new("edit.undo", "元に戻す", "Ctrl+Z", () => Session.Undo()),
            new("edit.redo", "やり直し", "Ctrl+Y;Ctrl+Shift+Z", () => Session.Redo()),
            // 切り取り・削除などは、今の編集画面 (タイムラインのオブジェクト / ストーリーボードのクリップ) に対して行う
            new("edit.cut", "切り取り", "Ctrl+X", () => Workspace?.Cut()),
            new("edit.copy", "コピー", "Ctrl+C", () => Workspace?.Copy()),
            new("edit.paste", "貼り付け", "Ctrl+V", Paste),
            new("edit.delete", "削除", "Delete", () => Workspace?.Delete()),
            new("edit.selectAll", "すべて選択", "Ctrl+A", () => Workspace?.SelectAll()),
            new("edit.deselect", "選択を解除", "Escape", () => Workspace?.Deselect()),
            new("edit.split", "現在位置で分割", "S", Split),
            new("edit.addKey", "キーフレームを追加", "P", AddKeyFrame, Mode: Adv),
            new("play.toggle", "再生 / 停止", "Space", () => Svc.Preview.TogglePlay()),
            new("view.performance", "パフォーマンスチェック", "Ctrl+Shift+P", TogglePerformanceMode, true),
            new("move.prev", "1 フレーム戻る", "Left", () => Step(-1)),
            new("move.next", "1 フレーム進む", "Right", () => Step(1)),
            new("move.prev10", "10 フレーム戻る", "Shift+Left", () => Step(-10)),
            new("move.next10", "10 フレーム進む", "Shift+Right", () => Step(10)),
            new("move.prevEdit", "前の編集点へ", "Ctrl+Left", () => Seek(Session.FindEditPoint(Session.CurrentFrame, -1))),
            new("move.nextEdit", "次の編集点へ", "Ctrl+Right", () => Seek(Session.FindEditPoint(Session.CurrentFrame, 1))),
            new("move.home", "先頭へ", "Home", () => Seek(0)),
            new("move.end", "末尾へ", "End", () => Seek(Math.Max(0, Session.SceneLength - 1))),
            new("range.start", "出力範囲の開始を現在位置に", "OemOpenBrackets", SetRangeStart, Mode: Adv),
            new("range.end", "出力範囲の終了を現在位置に", "OemCloseBrackets", SetRangeEnd, Mode: Adv),
            new("range.clear", "出力範囲を解除", "", ClearRange, Mode: Adv),
            new("alias.create", "テンプレートとして保存", "", () => _ = SaveTemplateAsync(), Mode: Adv),
            new("template.library", "テンプレートライブラリ", "", () => _ = ShowTemplateLibraryAsync(), Mode: Adv),
            new("simple.trimStart", "開始位置を現在位置に (シンプル編集)", "OemOpenBrackets", () => Main?.Simple.TrimAtPlayhead(start: true), Mode: EditorMode.Simple),
            new("simple.trimEnd", "終了位置を現在位置に (シンプル編集)", "OemCloseBrackets", () => Main?.Simple.TrimAtPlayhead(start: false), Mode: EditorMode.Simple));
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
        var copy = new Project { FormatVersion = snapshot.FormatVersion, Settings = snapshot.Settings, Scenes = snapshot.Scenes, Grid = snapshot.Grid, Storyboard = snapshot.Storyboard };
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
            Session.Edit("自動保存から復元", () => { Session.Project.Scenes = project.Scenes; Session.Project.Storyboard = project.Storyboard; });
        }
        catch (Exception ex) { await ShowErrorAsync("復元できませんでした。\n" + ex.Message); }
    }
}
