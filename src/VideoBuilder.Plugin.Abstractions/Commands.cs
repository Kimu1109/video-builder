using VideoBuilder.Core;

namespace VideoBuilder.Plugins;

/// <summary>
/// 「プラグイン」メニューに出すコマンド (API バージョン 3 から)。IPluginHost.RegisterCommand で登録する。
/// 選んだときに UI のスレッドで RunAsync が呼ばれる。
/// </summary>
public interface IPluginCommand
{
    /// <summary>コマンドの ID。ほかのコマンドと重ならないよう、プラグインの ID を頭に付ける (例: "my.plugin.hello")。</summary>
    string Id { get; }

    /// <summary>メニューに出す名前。</summary>
    string Name { get; }

    /// <summary>メニューの中のサブメニューの名前。同じ Group のコマンドは 1 つのサブメニューにまとまる。null ならそのまま並べる。</summary>
    string? Group => null;

    /// <summary>今選べるか。メニューを開くたびに呼ばれるので、すぐ返すこと。</summary>
    bool CanRun(IPluginCommandContext context) => true;

    /// <summary>
    /// コマンドを実行する。UI のスレッドで呼ばれる。ネットワークなどの待ちは await する (UI は止まらない)。
    /// 時間がかかる処理は context.RunWithProgressAsync で包むと、進み具合と中止のボタンが出る。
    /// 例外を投げると、エンジンがその内容をメッセージで表示する (プロジェクトは変わらない)。
    /// </summary>
    Task RunAsync(IPluginCommandContext context);
}

/// <summary>コマンドから本体を操作する窓口。RunAsync の間だけ使う (取っておいて後で使わないこと)。</summary>
public interface IPluginCommandContext
{
    /// <summary>今のプロジェクト。読むだけにする (書き換えは Edit の中で行う)。</summary>
    Project Project { get; }

    /// <summary>今開いているシーン。読むだけにする。</summary>
    Scene Scene { get; }

    /// <summary>再生位置 (シーンの中のフレーム)。</summary>
    int CurrentFrame { get; }

    /// <summary>選んでいるオブジェクト (選んだ順)。読むだけにする。</summary>
    IReadOnlyList<TimelineObject> SelectedObjects { get; }

    /// <summary>
    /// 新しいオブジェクトを置くときの目安のレイヤー。オブジェクトを選んでいればそのレイヤー、なければ 0。
    /// </summary>
    int TargetLayer { get; }

    /// <summary>プロジェクトのファイルがあるフォルダ。まだ保存していなければ null。</summary>
    string? ProjectDirectory { get; }

    /// <summary>このプラグイン専用の、設定などを置くフォルダ (作成済み)。</summary>
    string DataDirectory { get; }

    /// <summary>音声・動画ファイルの長さ (秒)。読めなければ null (FFmpeg がないときも null)。</summary>
    double? MediaDuration(string path);

    /// <summary>
    /// プロジェクトを書き換える。edit の中で行った変更は、まとめて 1 回で取り消せる。
    /// edit が例外を投げたら、変更はすべて元に戻る (例外はそのまま呼び出し元に返る)。
    /// </summary>
    void Edit(string description, Action<IPluginEditor> edit);

    /// <summary>入力フォームを出す。OK なら true を返し、入力された値を各項目の Value に書き戻す。取り消しなら false。</summary>
    Task<bool> ShowFormAsync(PluginForm form);

    /// <summary>メッセージを出す。</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>
    /// 進み具合のダイアログを出しながら work を実行し、その結果を返す。
    /// 利用者が中止すると、work に渡した CancellationToken が取り消される (work が止まったら OperationCanceledException が返る)。
    /// </summary>
    Task<T> RunWithProgressAsync<T>(string title, Func<IProgress<PluginProgress>, CancellationToken, Task<T>> work);

    // ---------------- テンプレート (API バージョン 4 から) ----------------

    /// <summary>テンプレートのライブラリ (自分のものと共有フォルダのもの) の一覧。同じテンプレートの古い版は含めない。</summary>
    IReadOnlyList<PluginTemplateInfo> ListTemplates();

    /// <summary>
    /// id のテンプレートのいちばん新しい版を読み込む。ライブラリになければ null。
    /// 読み込んだものは IPluginEditor.AddTemplate で何度でも置ける (置くたびに新しいシーンを作る)。
    /// </summary>
    IPluginTemplate? LoadTemplate(string id);

    /// <summary>
    /// テンプレートから置いたもの (シーンオブジェクト、または「複数のオブジェクト」として置いたもののどれか) の公開項目と今の値。
    /// テンプレートから置いたものでなければ空。
    /// </summary>
    IReadOnlyList<PluginTemplateField> TemplateFields(TimelineObject placed);
}

/// <summary>ライブラリのテンプレート 1 つ。Id は版が変わっても同じ。</summary>
public sealed record PluginTemplateInfo(string Id, string Name, string Category, string Description);

/// <summary>
/// テンプレートの公開項目。Label は作った人が付けた表示名で、値を入れるときの目印にする (同じ表示名の項目には同じ値が入る)。
/// IsText は文字 (1 行・複数行) の項目か。Value は今の値 (数値は不変の書式、チェックは "1" / "0")。
/// </summary>
public sealed record PluginTemplateField(string Label, string Group, bool IsText, string Value);

/// <summary>LoadTemplate で読み込んだテンプレート。</summary>
public interface IPluginTemplate
{
    string Id { get; }
    string Name { get; }
    int Version { get; }
    /// <summary>公開項目と、テンプレートに保存された値。</summary>
    IReadOnlyList<PluginTemplateField> Fields { get; }
}

/// <summary>進み具合。Fraction は 0〜1 (分からなければ null)。</summary>
public readonly record struct PluginProgress(string? Message, double? Fraction = null);

/// <summary>IPluginCommandContext.Edit の中で使う、プロジェクトの書き換え口。</summary>
public interface IPluginEditor
{
    /// <summary>今のシーン。直接書き換えてもよい (Edit の中なら、まとめて取り消せる)。</summary>
    Scene Scene { get; }

    /// <summary>layer から下で、start〜end (フレーム、end を含む) がすべて空いている最初のレイヤー。なければ -1。</summary>
    int FindFreeLayer(int layer, int start, int end);

    /// <summary>
    /// 組み込みやプラグインのオブジェクト (definitionId) を置く。layer から下で、frame から lengthFrames がすべて空いている
    /// 最初のレイヤーに置く (長さは削らない)。置けなければ null。init で値を設定できる。
    /// </summary>
    TimelineObject? AddObject(string definitionId, int layer, int frame, int lengthFrames, Action<TimelineObject>? init = null);

    /// <summary>
    /// 音声ファイルのオブジェクトを置く。lengthFrames が null なら、ファイルの長さに合わせる
    /// (ファイルを読めなければ InvalidOperationException)。置く場所がなければ null。
    /// </summary>
    TimelineObject? AddAudioFile(string path, int layer, int frame, int? lengthFrames = null);

    /// <summary>
    /// 字幕向けのテキストオブジェクトを置く。style (テキストオブジェクト) があればその見た目をひな形にし、
    /// なければ画面の下寄りに縁取り付きで置く。置く場所がなければ null。
    /// </summary>
    TimelineObject? AddText(string text, int layer, int frame, int lengthFrames, TimelineObject? style = null);

    /// <summary>
    /// テンプレートを、シーンオブジェクト 1 つとして長さ lengthFrames で置く (API バージョン 4 から)。
    /// 「長さに合わせて伸縮」をオンにするので、入りと抜けの動きはそのままで、中間が伸び縮みする。
    /// values (公開項目の表示名 → 値) で項目を埋める。置く場所は AddText などと同じ決め方。置けなければ null。
    /// </summary>
    TimelineObject? AddTemplate(IPluginTemplate template, int layer, int frame, int lengthFrames, IReadOnlyDictionary<string, string>? values = null);

    /// <summary>テンプレートから置いたものの公開項目に値を入れる (表示名 → 値)。入れた項目の数を返す (API バージョン 4 から)。</summary>
    int SetTemplateValues(TimelineObject placed, IReadOnlyDictionary<string, string> values);

    /// <summary>オブジェクトをグループにする (一緒に動くようにする)。</summary>
    void Group(IEnumerable<TimelineObject> objects);

    /// <summary>Edit が終わったら、これらのオブジェクトを選んだ状態にする。</summary>
    void Select(IEnumerable<TimelineObject> objects);
}

/// <summary>ShowFormAsync で出す入力フォーム。</summary>
public sealed class PluginForm
{
    public string Title { get; set; } = "";
    /// <summary>項目の上に出す説明 (なくてよい)。</summary>
    public string? Message { get; set; }
    public List<PluginField> Fields { get; set; } = new();
    /// <summary>決定のボタンの文字。</summary>
    public string OkText { get; set; } = "OK";
    /// <summary>フォームの幅 (ピクセル)。</summary>
    public double Width { get; set; } = 440;

    public T Get<T>(string key) where T : PluginField =>
        Fields.OfType<T>().FirstOrDefault(f => f.Key == key) ?? throw new KeyNotFoundException(key);
}

/// <summary>フォームの項目。Key はフォームの中で重ならないようにする。</summary>
public abstract class PluginField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>項目の下に出す補足 (なくてよい)。</summary>
    public string? Note { get; set; }
}

/// <summary>文字の入力。</summary>
public sealed class PluginTextField : PluginField
{
    public string Value { get; set; } = "";
    /// <summary>複数行にする (改行を入力できる)。</summary>
    public bool Multiline { get; set; }
    /// <summary>空のときに薄く出す文字。</summary>
    public string? Placeholder { get; set; }
    /// <summary>true なら、空のままでは OK を押せない。</summary>
    public bool Required { get; set; }
}

/// <summary>数値の入力。</summary>
public sealed class PluginNumberField : PluginField
{
    public double Value { get; set; }
    public double Minimum { get; set; } = double.MinValue;
    public double Maximum { get; set; } = double.MaxValue;
    /// <summary>小数点以下の桁数。</summary>
    public int Decimals { get; set; }
}

/// <summary>一覧から 1 つ選ぶ。Value は選んだ項目の Value。</summary>
public sealed class PluginChoiceField : PluginField
{
    public List<PluginChoice> Options { get; set; } = new();
    public string Value { get; set; } = "";
}

public sealed record PluginChoice(string Value, string Label);

/// <summary>チェックボックス。</summary>
public sealed class PluginCheckField : PluginField
{
    public bool Value { get; set; }
}
