namespace VideoBuilder.Core;

/// <summary>組み込みの効果定義。プラグインは Register で追加する。</summary>
public static class EffectRegistry
{
    // プラグインは別のスレッド (テストの並列実行・読み込み) から Register することがあるので、
    // 一覧は書き換えるたびに作り直して差し替え (読む側は差し替え前の一覧をそのまま読める)、辞書は並列に使えるものにする
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, EffectDefinition> Map = new();
    private static readonly object RegisterGate = new();
    private static volatile EffectDefinition[] _list = Array.Empty<EffectDefinition>();
    private static EffectDefinition[] List => _list;

    public static readonly string[] BlendModes =
    {
        "通常", "加算", "減算", "乗算", "スクリーン", "オーバーレイ", "比較(明)", "比較(暗)", "輝度", "カラー", "焼き込みカラー", "ハードライト", "差分",
    };

    /// <summary>図形の種類。番号はプロジェクトファイルに残るので、増やすときは末尾に足す (ShapeType と同じ並び)。</summary>
    public static readonly string[] ShapeTypes =
    {
        "背景", "円", "四角形", "三角形", "五角形", "六角形", "星型",
        "ひし形", "台形", "平行四辺形", "多角形", "矢印", "両矢印", "山形矢印", "十字", "ハート",
        "吹き出し (四角)", "吹き出し (丸)", "扇形", "角括弧", "直線",
    };
    public static readonly string[] ShapeFills = { "単色", "線形グラデーション", "円形グラデーション" };
    public static readonly string[] LineStyles = { "実線", "破線", "点線" };
    public static readonly string[] LineJoins = { "とがらせる", "丸める", "面取り" };
    public static readonly string[] LineEnds = { "なし", "矢印", "三角", "円", "四角" };
    public static readonly string[] MaskShapes = { "四角形", "円", "角丸四角形" };
    /// <summary>テキストの「文字の出し方」。番号はプロジェクトファイルに残るので、増やすときは末尾に足す。</summary>
    public static readonly string[] TextAnimations =
    {
        "なし", "1 文字ずつ表示", "1 文字ずつフェードイン", "1 文字ずつ下から", "1 文字ずつ拡大",
        "1 文字ずつ上から", "1 文字ずつ左から", "1 文字ずつ右から", "1 文字ずつ回転して登場", "1 文字ずつ弾んで登場",
        "1 文字ずつぼかしから", "1 文字ずつ縮小して登場", "1 文字ずつ落ちてくる", "文字化けから表示 (デコード)",
        "波打つ (繰り返し)", "カラオケ (色が変わる)", "線で描いてから塗る", "数字のカウントアップ",
    };
    /// <summary>テキストの「消え方」(オブジェクトの終わりに、文字の出し方と同じ順番・間隔で消える)。</summary>
    public static readonly string[] TextExits = { "なし", "1 文字ずつフェードアウト", "1 文字ずつ上へ消える", "1 文字ずつ下へ落ちる", "1 文字ずつ縮んで消える", "散らばって消える" };
    /// <summary>テキストアニメーションの単位と順番。</summary>
    public static readonly string[] TextAnimUnits = { "文字", "単語", "行" };
    public static readonly string[] TextAnimOrders = { "先頭から", "ランダム", "中央から", "両端から" };
    public static readonly string[] TransitionTypes =
    {
        "フェード", "ワイプ (左から)", "ワイプ (右から)", "ワイプ (上から)", "ワイプ (下から)", "円形ワイプ",
        "スライド (左から)", "スライド (右から)", "スライド (上から)", "スライド (下から)", "ズーム",
        // 番号はプロジェクトファイルに残るので、増やすときは末尾に足す
        "ぼかし", "回転ズーム", "ポップ", "時計回りワイプ", "ブラインド (横)", "ブラインド (縦)", "ひし形ワイプ",
        "ランダム方向から", "縮小して登場", "落ちて登場 (ランダムな間隔)", "落ちて弾む", "広がって登場", "起き上がって登場",
        "集まって登場", "点滅して登場", "弾んで登場", "何処からともなく登場",
        // 28〜: 入り (イン) と出 (アウト) で別の動きをする。ストーリーボードでは、入ってくるクリップと出ていくクリップの両方にかける
        "プッシュ (左から)", "プッシュ (右から)", "プッシュ (上から)", "プッシュ (下から)",
        "カバー (左から)", "カバー (右から)", "カバー (上から)", "カバー (下から)",
        "アンカバー (左へ)", "アンカバー (右へ)", "アンカバー (上へ)", "アンカバー (下へ)",
        "ホイップパン (左から)", "ホイップパン (右から)", "ホイップパン (上から)", "ホイップパン (下から)",
        "クロスズーム", "白で切り替え (フラッシュ)", "黒で切り替え (暗転)",
        "ワイプ (左上から)", "ワイプ (右上から)", "ワイプ (左下から)", "ワイプ (右下から)", "ワイプ (角度指定)",
        "中央から開く (横)", "中央から開く (縦)", "中央へ閉じる (横)", "中央へ閉じる (縦)",
        "四角形ワイプ (中心から)", "四角形ワイプ (外から)", "円形ワイプ (外から)", "星型ワイプ", "ハート型ワイプ",
        "反時計回りワイプ", "扇形ワイプ", "チェッカーボード", "ランダムブロック", "ディゾルブ (粒)", "ルールワイプ (画像)",
        "扉 (観音開き)", "ドア (片開き)",
        "キューブ回転 (右から)", "キューブ回転 (左から)", "キューブ回転 (上から)", "キューブ回転 (下から)",
        "カードを裏返す (横)", "カードを裏返す (縦)", "ページめくり",
        "グリッチ", "色収差", "光漏れ", "ピクセル化", "波紋", "渦巻き", "焼けて消える", "フィルム焼け",
        "重力で落下", "飛び去る", "溶ける",
    };
    /// <summary>トランジションの「動き方」(進み具合のかけ方)。0 (加減速) が以前からの動き。</summary>
    public static readonly string[] TransitionEasings = { "加減速", "直線", "加速", "減速", "行き過ぎて戻る", "弾む", "ばね" };
    /// <summary>入りと出で別の動きをする (ストーリーボードで両方のクリップにかける) トランジションの最初の番号。</summary>
    public const int PairedTransitionStart = 28;
    /// <summary>境界線を引ける (形で切り替える) トランジション。</summary>
    public static readonly int[] TransitionEdgeTypes = Enumerable.Range(47, 18).ToArray();
    /// <summary>「画面全体を基準にする」が効くトランジション (形・位置を絵ではなく画面の大きさで決めるもの)。</summary>
    public static readonly int[] TransitionScreenTypes =
        Enumerable.Range(32, 8).Concat(Enumerable.Range(47, 22)).Concat(new[] { 75, 82, 86 }).ToArray();
    /// <summary>エフェクト「アニメーション効果」の種類。番号はプロジェクトファイルに残るので、増やすときは末尾に足す。</summary>
    public static readonly string[] AnimationTypes =
    {
        "震える", "点滅", "回転", "鼓動 (拡大縮小)", "揺れ (傾く)", "浮遊 (上下)", "弾む", "横揺れ",
        "振り子", "反復移動", "回転 (X軸)", "回転 (Y軸)", "立方体の面", "リール回転",
        "円運動", "楕円軌道", "8 の字", "ぷるぷる (ゼリー)", "呼吸", "色相が回る", "虹色が流れる", "明るさの脈動",
        "心臓の鼓動 (2 拍)", "首振り", "旗のようになびく", "跳ねながら往復", "手ぶれ", "ゆっくりズーム・パン", "カーソル点滅",
    };
    /// <summary>エフェクト「アニメーション効果」の立方体の面。</summary>
    public static readonly string[] CubeFaces = { "前", "後ろ", "右", "左", "上", "下" };
    /// <summary>画像ファイル合成・動画ファイル合成の合成モード。</summary>
    public static readonly string[] FileBlendModes =
    {
        "通常", "加算", "乗算", "スクリーン", "オーバーレイ", "比較(明)", "比較(暗)", "輝度", "カラー", "ハードライト", "差分",
    };

    public static readonly string[] VideoExtensions = { "mp4", "mkv", "mov", "webm", "avi", "m4v", "ts", "flv", "wmv", "mpg" };
    public static readonly string[] ImageExtensions = { "png", "jpg", "jpeg", "bmp", "webp", "gif" };
    public static readonly string[] AudioExtensions = { "wav", "mp3", "flac", "ogg", "m4a", "aac", "opus" };
    /// <summary>ベクター画像: SVG と Lottie (JSON)。</summary>
    public static readonly string[] VectorExtensions = { "svg", "json", "lottie" };

    public const string Video = "builtin.video";
    public const string Image = "builtin.image";
    public const string Audio = "builtin.audio";
    public const string Text = "builtin.text";
    public const string Shape = "builtin.shape";
    public const string Vector = "builtin.vector";
    public const string SceneRef = "builtin.scene";
    public const string GroupControl = "builtin.group";
    public const string CameraControl = "builtin.camera";
    public const string StandardDraw = "builtin.draw";
    public const string StandardPlay = "builtin.play";

    static EffectRegistry()
    {
        static ParamDefinition T(string key, string name, double min, double max, double def, int dec = 1, double? softMin = null, double? softMax = null) =>
            new() { Key = key, Name = name, Min = min, Max = max, Default = def, Decimals = dec, SoftMin = softMin, SoftMax = softMax };
        static ParamDefinition C(string key, string name, bool def = false) =>
            new() { Key = key, Name = name, Kind = ParamKind.Check, Default = def ? 1 : 0 };
        static ParamDefinition Col(string key, string name, string def) =>
            new() { Key = key, Name = name, Kind = ParamKind.Color, DefaultText = def };

        // ---- メディアオブジェクト ----
        Register(new EffectDefinition
        {
            Id = Video, Name = "動画ファイル", Category = EffectCategory.VideoSource, Icon = "Icon.Movie", Color = "#3A6EA5",
            Params = new[]
            {
                T("position", "開始位置", 0, 36000, 0, 2, 0, 600),
                T("speed", "再生速度", 0, 2000, 100, 1, 0, 400),
                C("loop", "ループ再生"),
                C("reverse", "逆再生"),
                // アニメーション WebP・GIF も動画として選べる
                new ParamDefinition { Key = "file", Name = "ファイル", Kind = ParamKind.File, FileExtensions = VideoExtensions.Concat(AnimatedImageFiles.Extensions).ToArray() },
            },
        });
        Register(new EffectDefinition
        {
            Id = Image, Name = "画像ファイル", Category = EffectCategory.VideoSource, Icon = "Icon.Image", Color = "#3A8F6E",
            Params = new[] { new ParamDefinition { Key = "file", Name = "ファイル", Kind = ParamKind.File, FileExtensions = ImageExtensions } },
        });
        Register(new EffectDefinition
        {
            Id = Audio, Name = "音声ファイル", Category = EffectCategory.AudioSource, Icon = "Icon.MusicNote", Color = "#A5503A",
            Params = new[]
            {
                T("position", "開始位置", 0, 36000, 0, 2, 0, 600),
                T("speed", "再生速度", 10, 800, 100, 1),
                C("loop", "ループ再生"),
                new ParamDefinition { Key = "file", Name = "ファイル", Kind = ParamKind.File, FileExtensions = AudioExtensions.Concat(VideoExtensions).ToArray() },
            },
        });
        Register(new EffectDefinition
        {
            Id = Text, Name = "テキスト", Category = EffectCategory.VideoSource, Icon = "Icon.Title", Color = "#6E5AA5",
            Params = new[]
            {
                T("size", "サイズ", 1, 1000, 64, 0, 1, 256),
                T("spacing", "字間", -100, 500, 0, 0),
                T("leading", "行間", -100, 500, 0, 0),
                new ParamDefinition { Key = "font", Name = "フォント", Kind = ParamKind.Font, DefaultText = "" },
                Col("color", "文字色", "#FFFFFF"),
                C("bold", "太字"), C("italic", "イタリック"),
                new ParamDefinition { Key = "align", Name = "揃え", Kind = ParamKind.Choice, Choices = new[] { "左寄せ", "中央揃え", "右寄せ" }, Default = 1 },
                // 縦書きでは、揃えは上揃え・中央・下揃えになる
                C("vertical", "縦書き"), C("underline", "下線"), C("strike", "取り消し線"),
                // テキストアニメーション (1 文字ずつ)。時間はオブジェクトの先頭から数える
                new ParamDefinition { Key = "anim", Name = "文字の出し方", Kind = ParamKind.Choice, Choices = TextAnimations },
                T("interval", "文字の間隔 (秒)", 0, 10, 0.05, 3, 0, 0.5), T("duration", "1 文字の長さ (秒)", 0, 10, 0.3, 2, 0, 2),
                C("reverse", "逆順 (最後の文字から)"),
                new ParamDefinition { Key = "unit", Name = "単位", Kind = ParamKind.Choice, Choices = TextAnimUnits },
                new ParamDefinition { Key = "order", Name = "順番", Kind = ParamKind.Choice, Choices = TextAnimOrders },
                new ParamDefinition { Key = "exit", Name = "消え方", Kind = ParamKind.Choice, Choices = TextExits },
                // 波打つ (14): 1 回の周期・高さ / カラオケ (15): 塗る色 / カウントアップ (17): 数える時間
                new() { Key = "period", Name = "波の周期 (秒)", Min = 0.05, Max = 60, Default = 1, Decimals = 2, SoftMin = 0.1, SoftMax = 5, ShowWhenKey = "anim", ShowWhenValues = new[] { 14 } },
                new() { Key = "waveHeight", Name = "波の高さ (%)", Min = 0, Max = 200, Default = 25, Decimals = 1, ShowWhenKey = "anim", ShowWhenValues = new[] { 14 } },
                new ParamDefinition { Key = "color2", Name = "塗る色", Kind = ParamKind.Color, DefaultText = "#FFD040", ShowWhenKey = "anim", ShowWhenValues = new[] { 15 } },
                new() { Key = "countTime", Name = "数える時間 (秒)", Min = 0, Max = 3600, Default = 1.5, Decimals = 2, SoftMin = 0, SoftMax = 10, ShowWhenKey = "anim", ShowWhenValues = new[] { 17 } },
                new ParamDefinition { Key = "text", Name = "テキスト", Kind = ParamKind.MultilineText, DefaultText = "テキスト" },
            },
        });
        Register(new EffectDefinition
        {
            Id = Shape, Name = "図形", Category = EffectCategory.VideoSource, Icon = "Icon.Category", Color = "#5A7F3A",
            Params = new[]
            {
                T("size", "サイズ", 0, 4000, 200, 0, 0, 1000),
                T("aspect", "縦横比", -100, 100, 0, 1),
                T("line", "ライン幅", 0, 4000, 4000, 0, 0, 500),
                new ParamDefinition { Key = "type", Name = "図形の種類", Kind = ParamKind.Choice, Choices = ShapeTypes, Default = 2 },
                Col("color", "色", "#FFFFFF"),
            }.Concat(ShapeStyleParams()).ToArray(),
        });
        Register(new EffectDefinition
        {
            Id = Vector, Name = "ベクター画像", Category = EffectCategory.VideoSource, Icon = "Icon.Vector", Color = "#3A7F8F",
            Params = new[]
            {
                T("size", "サイズ", 0, 8000, 0, 0, 0, 2000),
                T("position", "開始位置", 0, 36000, 0, 2, 0, 600),
                T("speed", "再生速度", 0, 2000, 100, 1, 0, 400),
                C("loop", "ループ再生", true),
                new ParamDefinition { Key = "file", Name = "ファイル", Kind = ParamKind.File, FileExtensions = VectorExtensions },
                // SVG の要素の id (カンマ区切り)。その部品を描かない
                new ParamDefinition { Key = "hide", Name = "非表示にする部品 (id)", Kind = ParamKind.Text, DefaultText = "" },
            }.Concat(Enumerable.Range(0, VectorColors.Slots).Select(i =>
                // Lottie の色の置き換え。空ならファイルの色のまま
                new ParamDefinition { Key = VectorColors.ParamKey(i), Name = $"色 {i + 1}", Kind = ParamKind.Color, DefaultText = "" })).ToArray(),
        });
        Register(new EffectDefinition
        {
            Id = SceneRef, Name = "シーン", Category = EffectCategory.VideoSource, Icon = "Icon.MovieEdit", Color = "#A58A3A",
            Params = new[]
            {
                T("position", "開始位置", 0, 36000, 0, 2, 0, 600),
                new ParamDefinition { Key = "scene", Name = "シーン", Kind = ParamKind.Text, DefaultText = "" },
                C("stretch", "長さに合わせて伸縮"),
                T("intro", "先頭の固定 (秒)", 0, 3600, 0, 2, 0, 10),
                T("outro", "末尾の固定 (秒)", 0, 3600, 0, 2, 0, 10),
            },
        });
        Register(new EffectDefinition
        {
            Id = GroupControl, Name = "グループ制御", Category = EffectCategory.Control, Icon = "Icon.SelectAll", Color = "#4F6070",
            Params = new[]
            {
                T("x", "X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
                T("scale", "拡大率", 0, 5000, 100, 2, 0, 800), T("rotation", "回転", -3600, 3600, 0, 2, -360, 360),
                T("alpha", "透明度", 0, 100, 0),
                T("layers", "対象レイヤー数", 0, 100, 0, 0),
            },
        });

        Register(new EffectDefinition
        {
            Id = CameraControl, Name = "カメラ制御", Category = EffectCategory.Control, Icon = "Icon.Videocam", Color = "#704F60",
            Params = new[]
            {
                T("x", "X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
                T("z", "Z", -99999.9, 99999.9, -1024, 1, -4000, 0),
                T("tx", "目標X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("ty", "目標Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
                T("tz", "目標Z", -99999.9, 99999.9, 0, 1, -2000, 2000),
                T("roll", "傾き", -3600, 3600, 0, 2, -360, 360),
                T("fov", "視野角", 0, 170, 0, 1),
                T("layers", "対象レイヤー数", 0, 100, 0, 0),
                C("zsort", "奥行きで並べ替える", true),
                T("dof", "深度ぼけ", 0, 200, 0, 1, 0, 50),
                T("focus", "ピントの距離", 0, 99999.9, 0, 1, 0, 4000),
            },
        });

        // ---- 標準描画 / 標準再生 ----
        Register(new EffectDefinition
        {
            Id = StandardDraw, Name = "変形", Category = EffectCategory.Draw, Icon = "Icon.Transform",
            Params = new[]
            {
                T("x", "X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "Y", -99999.9, 99999.9, 0, 1, -2000, 2000), T("z", "Z", -99999.9, 99999.9, 0, 1, -2000, 2000),
                T("scale", "拡大率", 0, 5000, 100, 2, 0, 800), T("alpha", "透明度", 0, 100, 0), T("rotation", "回転", -3600, 3600, 0, 2, -360, 360),
                T("rx", "X軸回転", -3600, 3600, 0, 2, -360, 360), T("ry", "Y軸回転", -3600, 3600, 0, 2, -360, 360),
                new ParamDefinition { Key = "blend", Name = "合成モード", Kind = ParamKind.Choice, Choices = BlendModes },
                // 動いている向き (X・Y の動き) に合わせて回す。回転の値はそこに足す
                C("orient", "進む向きに回転"),
                // 親: そのレイヤー (表示の番号) にあるオブジェクトの位置に付いていく。遅れを付けると、少し前の親の位置を追いかける
                T("parent", "親のレイヤー (0 でなし)", 0, 1000, 0, 0, 0, 30),
                T("parentDelay", "親を追いかける遅れ (秒)", 0, 60, 0, 2, 0, 3),
                C("parentInherit", "親の回転・拡大も受け継ぐ"),
            },
        });
        Register(new EffectDefinition
        {
            Id = StandardPlay, Name = "音声", Category = EffectCategory.AudioPlay, Icon = "Icon.VolumeUp",
            Params = new[] { T("volume", "音量", 0, 500, 100), T("pan", "左右", -100, 100, 0) },
        });

        // ---- フィルタ効果 ----
        void F(string id, string name, string icon, params ParamDefinition[] ps) =>
            Register(new EffectDefinition { Id = id, Name = name, Category = EffectCategory.Filter, Icon = icon, Color = "#7A7A7A", Params = ps, Group = FilterGroupOf(id) });

        F("filter.color", "色調補正", "Icon.Tune",
            T("brightness", "明るさ", 0, 200, 100), T("contrast", "コントラスト", 0, 200, 100),
            T("hue", "色相", -360, 360, 0), T("luminance", "輝度", 0, 200, 100), T("saturation", "彩度", 0, 200, 100));
        F("filter.blur", "ぼかし", "Icon.BlurOn", T("radius", "範囲", 0, 1000, 5, 0, 0, 200), C("fixed", "サイズ固定"));
        F("filter.border", "縁取り", "Icon.BorderStyle", T("size", "サイズ", 0, 500, 3, 0), T("blur", "ぼかし", 0, 100, 0, 0), Col("color", "縁色", "#000000"),
            C("only", "縁だけにする (中を抜く)"));
        // ---- 文字を飾る効果 (どのオブジェクトにも使える) ----
        F("filter.backplate", "背景の帯", "Icon.Crop",
            T("padx", "余白 (横)", 0, 2000, 24, 0, 0, 200), T("pady", "余白 (縦)", 0, 2000, 12, 0, 0, 200), T("radius", "角の丸み", 0, 2000, 8, 0, 0, 100),
            Col("color", "帯の色", "#000000"), T("alpha", "透明度", 0, 100, 30, 0),
            T("line", "枠線の幅", 0, 200, 0, 0, 0, 20), Col("linecolor", "枠線の色", "#FFFFFF"),
            new ParamDefinition { Key = "fit", Name = "帯の大きさ", Kind = ParamKind.Choice, Choices = new[] { "文字に合わせる", "絵の大きさ" } });
        F("filter.innershadow", "内側シャドー", "Icon.Layers",
            T("x", "X", -200, 200, 4, 0), T("y", "Y", -200, 200, 4, 0), T("density", "濃さ", 0, 100, 60), T("spread", "拡散", 0, 50, 4, 0), Col("color", "影色", "#000000"));
        F("filter.extrude", "立体 (押し出し)", "Icon.Layers",
            T("depth", "奥行き", 0, 200, 12, 0, 0, 60), T("angle", "向き", -3600, 3600, 45, 1, -180, 180),
            Col("color", "手前の色", "#606060"), Col("color2", "奥の色", "#202020"));
        F("filter.overlapinvert", "背景と被る色を反転", "Icon.InvertColors",
            T("range", "判定の幅", 0, 100, 20, 0), T("soft", "なめらかさ", 0, 100, 10, 0), T("strength", "強さ", 0, 100, 100, 0),
            new ParamDefinition { Key = "mode", Name = "変える色", Kind = ParamKind.Choice, Choices = new[] { "背景の反転色", "白か黒 (見やすい方)" } });
        F("filter.shadow", "シャドー", "Icon.Layers",
            T("x", "X", -200, 200, 8, 0), T("y", "Y", -200, 200, 8, 0), T("density", "濃さ", 0, 100, 60), T("spread", "拡散", 0, 50, 10, 0), Col("color", "影色", "#000000"));
        F("filter.clip", "クロップ", "Icon.Crop", T("top", "上", 0, 4000, 0, 0, 0, 1000), T("bottom", "下", 0, 4000, 0, 0, 0, 1000), T("left", "左", 0, 4000, 0, 0, 0, 1000), T("right", "右", 0, 4000, 0, 0, 0, 1000));
        F("filter.mosaic", "モザイク", "Icon.GridOn", T("size", "サイズ", 1, 1000, 12, 0, 1, 200));
        F("filter.chromakey", "クロマキー", "Icon.Colorize", T("hue", "色相範囲", 0, 256, 24, 0), T("saturation", "彩度範囲", 0, 256, 96, 0), T("edge", "境界補正", 0, 100, 10, 0), Col("color", "キー色", "#00FF00"));
        F("filter.mono", "単色化", "Icon.InvertColors", T("strength", "強さ", 0, 100, 100), Col("color", "色", "#FFFFFF"), C("keep", "輝度を保持する", true));
        F("filter.flip", "反転", "Icon.Flip", C("vertical", "上下反転"), C("horizontal", "左右反転"), C("luma", "輝度反転"));
        F("filter.fade", "フェード", "Icon.Gradient", T("in", "イン", 0, 30, 0.5, 2), T("out", "アウト", 0, 30, 0.5, 2));
        F("filter.mask", "マスク", "Icon.Crop",
            new ParamDefinition { Key = "shape", Name = "形", Kind = ParamKind.Choice, Choices = MaskShapes },
            T("x", "X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
            T("width", "幅", 0, 16384, 400, 0, 0, 4000), T("height", "高さ", 0, 16384, 300, 0, 0, 4000),
            T("rotation", "回転", -3600, 3600, 0, 2, -360, 360), T("radius", "角の丸み", 0, 4000, 40, 0, 0, 500),
            T("feather", "ぼかし", 0, 1000, 0, 0, 0, 200), C("invert", "反転 (図形の外側を残す)"));
        F("filter.transition", "トランジション", "Icon.Gradient",
            new ParamDefinition { Key = "type", Name = "種類", Kind = ParamKind.Choice, Choices = TransitionTypes },
            T("in", "イン (秒)", 0, 60, 0.5, 2, 0, 5), T("out", "アウト (秒)", 0, 60, 0, 2, 0, 5), T("feather", "ぼかし", 0, 1000, 20, 0, 0, 200),
            // 弾んで登場 (26)
            new() { Key = "height", Name = "高さ", Min = -1000, Max = 1000, Default = 200, Decimals = 0, ShowWhenKey = "type", ShowWhenValues = new[] { 26 } },
            new() { Key = "bounces", Name = "回数", Min = 1, Max = 20, Default = 4, Decimals = 1, ShowWhenKey = "type", ShowWhenValues = new[] { 26 } },
            // 何処からともなく登場 (27)
            new() { Key = "range", Name = "位置", Min = 0, Max = 5, Default = 1.5, Decimals = 2, ShowWhenKey = "type", ShowWhenValues = new[] { 27 } },
            new() { Key = "jumpBlur", Name = "移動のぼかし", Min = 0, Max = 5, Default = 1, Decimals = 2, ShowWhenKey = "type", ShowWhenValues = new[] { 27 } },
            // ワイプ (角度指定) (51)
            new() { Key = "angle", Name = "角度", Min = -3600, Max = 3600, Default = 45, Decimals = 1, SoftMin = -180, SoftMax = 180, ShowWhenKey = "type", ShowWhenValues = new[] { 51 } },
            // ルールワイプ (66): 画像の暗い所から切り替わる
            new ParamDefinition { Key = "rule", Name = "ルール画像", Kind = ParamKind.File, FileExtensions = ImageExtensions, ShowWhenKey = "type", ShowWhenValues = new[] { 66 } },
            new ParamDefinition { Key = "ruleInvert", Name = "明るい所から切り替える", Kind = ParamKind.Check, ShowWhenKey = "type", ShowWhenValues = new[] { 66 } },
            // 形で切り替えるもの: 境界線
            new() { Key = "edge", Name = "境界線の幅", Min = 0, Max = 1000, Default = 0, Decimals = 0, SoftMin = 0, SoftMax = 50, ShowWhenKey = "type", ShowWhenValues = TransitionEdgeTypes },
            new ParamDefinition { Key = "edgeColor", Name = "境界線の色", Kind = ParamKind.Color, DefaultText = "#FFFFFF", ShowWhenKey = "type", ShowWhenValues = TransitionEdgeTypes },
            new ParamDefinition { Key = "screen", Name = "画面全体を基準にする", Kind = ParamKind.Check, ShowWhenKey = "type", ShowWhenValues = TransitionScreenTypes },
            // ストーリーボードが、重なった 2 つのクリップの奥の方に付ける (切り抜くだけの役なら、手前の絵が切り抜かれるので何もしない)。設定画面には出さない
            new ParamDefinition { Key = "under", Name = "奥のクリップ", Kind = ParamKind.Check, ShowWhenKey = "type", ShowWhenValues = Array.Empty<int>() },
            new ParamDefinition { Key = "ease", Name = "動き方", Kind = ParamKind.Choice, Choices = TransitionEasings },
            new ParamDefinition { Key = "outType", Name = "アウトの種類", Kind = ParamKind.Choice, Choices = new[] { "インと同じ" }.Concat(TransitionTypes).ToArray() });
        // ---- ぼかし・光 (重い処理は複数スレッド・縮小計算で速くしている) ----
        F("filter.glow", "グロー", "Icon.LightMode",
            T("strength", "強さ", 0, 400, 100, 1, 0, 300), T("radius", "拡散", 0, 1000, 20, 0, 0, 200),
            T("threshold", "しきい値", 0, 100, 60, 1), Col("color", "光の色 (空欄で元の色)", ""), C("only", "光だけを描く"),
            new ParamDefinition { Key = "shape", Name = "光の形", Kind = ParamKind.Choice, Choices = new[] { "通常", "十字", "横", "縦" } });
        F("filter.emit", "発光", "Icon.LightMode",
            T("strength", "強さ", 0, 400, 100, 1, 0, 300), T("radius", "拡散", 0, 1000, 30, 0, 0, 200),
            T("threshold", "しきい値", 0, 100, 80, 1), Col("color", "光の色 (空欄で元の色)", ""), C("only", "光だけを描く"));
        F("filter.diffuse", "拡散光", "Icon.LightMode", T("strength", "強さ", 0, 100, 60, 1), T("radius", "拡散", 0, 1000, 30, 0, 0, 200));
        F("filter.flash", "閃光", "Icon.LightMode",
            T("strength", "強さ", 0, 400, 100, 1, 0, 300), T("length", "長さ (%)", 0, 400, 50, 1, 0, 200), T("threshold", "しきい値", 0, 100, 60, 1),
            T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000), C("only", "光だけを描く"));
        F("filter.light", "ライト", "Icon.LightMode",
            T("strength", "強さ", 0, 800, 100, 1, 0, 300), T("width", "幅", 0, 500, 20, 0, 0, 100),
            T("angle", "光の向き", -3600, 3600, -45, 1, -360, 360), T("direction", "向きの強さ (%)", 0, 100, 50, 1), Col("color", "光の色", "#FFFFFF"));
        F("filter.dirblur", "方向ブラー", "Icon.BlurOn",
            T("length", "長さ", 0, 2000, 30, 0, 0, 300), T("angle", "角度", -3600, 3600, 0, 1, -360, 360), C("fixed", "サイズ固定"));
        F("filter.radialblur", "放射ブラー", "Icon.BlurOn",
            T("amount", "強さ", 0, 400, 20, 1, 0, 100),
            T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000));
        F("filter.sharpen", "シャープ", "Icon.Tune", T("amount", "強さ", 0, 500, 100, 1, 0, 300), T("radius", "範囲", 0, 100, 2, 1, 0, 20));
        F("filter.chromatic", "色ずれ (色収差)", "Icon.Style",
            T("amount", "ずれ", -500, 500, 6, 1, -50, 50), T("angle", "角度", -3600, 3600, 0, 1, -360, 360), C("radial", "中心から外へずらす"),
            new ParamDefinition { Key = "type", Name = "ずらす色", Kind = ParamKind.Choice, Choices = new[] { "赤と青", "赤と緑", "緑と青" } });
        F("filter.raster", "ラスター (波)", "Icon.Gradient",
            T("amplitude", "振幅", -2000, 2000, 20, 1, -200, 200), T("wavelength", "波長", 1, 10000, 120, 0, 1, 1000),
            T("speed", "速さ (回/秒)", -100, 100, 1, 2, -10, 10), C("vertical", "縦方向"));
        F("filter.noise", "ノイズ", "Icon.GridOn",
            T("amount", "強さ", 0, 100, 20, 1), T("size", "粒の大きさ", 1, 200, 1, 0, 1, 20),
            C("color", "カラーノイズ"), C("animate", "毎フレーム変える", true), T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        // ---- 色・形 ----
        F("filter.vignette", "周辺減光", "Icon.Filter",
            T("strength", "強さ", 0, 100, 60, 1), T("size", "大きさ (%)", 0, 200, 50, 1), T("softness", "ぼかし (%)", 0, 200, 60, 1),
            Col("color", "色", "#000000"));
        F("filter.gradient", "グラデーション", "Icon.Gradient",
            T("strength", "強さ", 0, 100, 100, 1), T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
            T("angle", "角度", -3600, 3600, 0, 1, -360, 360), T("width", "幅", 0, 16384, 200, 0, 0, 2000),
            Col("color1", "色 1", "#FFFFFF"), Col("color2", "色 2", "#000000"));
        F("filter.diagclip", "斜めクリッピング", "Icon.Crop",
            T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
            T("angle", "角度", -3600, 3600, 0, 1, -360, 360), T("feather", "ぼかし", 0, 1000, 0, 0, 0, 200));
        F("filter.levels", "レベル補正", "Icon.Tune",
            T("inBlack", "入力の黒", 0, 255, 0, 0), T("inWhite", "入力の白", 0, 255, 255, 0), T("gamma", "ガンマ", 0.1, 10, 1, 2, 0.1, 3),
            T("outBlack", "出力の黒", 0, 255, 0, 0), T("outWhite", "出力の白", 0, 255, 255, 0));
        F("filter.posterize", "ポスタリゼーション", "Icon.Palette", T("levels", "段階数", 2, 256, 6, 0, 2, 32));
        F("filter.extcolor", "拡張色設定", "Icon.Palette",
            T("r", "赤 (R)", -255, 255, 0, 0), T("g", "緑 (G)", -255, 255, 0, 0), T("b", "青 (B)", -255, 255, 0, 0));
        F("filter.hueconvert", "特定色域変換", "Icon.Palette",
            Col("from", "変える色", "#FF0000"), Col("to", "変えた後の色", "#0000FF"), T("hue", "色相のずらし", -360, 360, 0, 1),
            T("range", "色相の範囲", 0, 180, 20, 1), T("edge", "境界補正", 0, 180, 15, 1),
            T("saturation", "彩度 (%)", 0, 400, 100, 1, 0, 200), T("value", "明るさ (%)", 0, 400, 100, 1, 0, 200));
        // ---- キー ----
        F("filter.colorkey", "カラーキー", "Icon.Colorize",
            Col("color", "キー色", "#FFFFFF"), T("luma", "輝度範囲", 0, 255, 16, 0), T("chroma", "色差範囲", 0, 255, 16, 0), T("edge", "境界補正", 0, 100, 10, 0));
        F("filter.lumakey", "ルミナンスキー", "Icon.Colorize",
            T("level", "基準輝度", 0, 255, 64, 0), T("soft", "ぼかし", 0, 255, 32, 0),
            new ParamDefinition { Key = "type", Name = "透明にする所", Kind = ParamKind.Choice, Choices = new[] { "暗い部分", "明るい部分" } });
        // ---- 縁・輪郭 ----
        F("filter.bevel", "凸エッジ", "Icon.BorderStyle",
            T("width", "幅", 0, 500, 8, 0, 0, 100), T("height", "高さ", 0, 400, 100, 1, 0, 300), T("angle", "光の向き", -3600, 3600, -45, 1, -360, 360));
        F("filter.edge", "エッジ抽出", "Icon.BorderStyle",
            T("strength", "強さ", 0, 1000, 100, 1, 0, 400), T("threshold", "しきい値", 0, 100, 10, 1),
            new ParamDefinition { Key = "type", Name = "種類", Kind = ParamKind.Choice, Choices = new[] { "明るさのエッジ", "透明度のエッジ" } },
            Col("color", "線の色", "#FFFFFF"), C("overlay", "元の絵に重ねる"));
        F("filter.edgeblur", "境界ぼかし", "Icon.BlurOn", T("radius", "範囲", 0, 1000, 10, 0, 0, 200));
        // ---- ぼかし (追加) ----
        F("filter.lensblur", "レンズブラー", "Icon.BlurOn",
            T("radius", "範囲", 0, 1000, 10, 1, 0, 100), T("boost", "光の強さ", 0, 100, 0, 1), C("fixed", "サイズ固定"));
        F("filter.motionblur", "モーションブラー", "Icon.BlurOn", T("shutter", "間隔 (%)", 0, 1000, 100, 1, 0, 400));
        // ---- 変形 ----
        F("filter.ripple", "波紋", "Icon.Gradient",
            T("amplitude", "振幅", -1000, 1000, 10, 1, -100, 100), T("wavelength", "波長", 1, 10000, 60, 0, 1, 500),
            T("speed", "速さ (回/秒)", -100, 100, 1, 2, -10, 10), T("decay", "減衰 (0 でなし)", 0, 10000, 0, 0, 0, 1000),
            T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000));
        F("filter.polar", "極座標変換", "Icon.Gradient",
            T("zoom", "拡大率", 1, 1000, 100, 1, 1, 300), T("hole", "中心の穴", 0, 4000, 0, 0, 0, 500),
            T("rotation", "回転", -3600, 3600, 0, 1, -360, 360), T("swirl", "渦巻", -1000, 1000, 0, 1, -300, 300));
        {
            static ParamDefinition DS(ParamDefinition d, params int[] shapes) => new()
            {
                Key = d.Key, Name = d.Name, Min = d.Min, Max = d.Max, Default = d.Default, Decimals = d.Decimals, SoftMin = d.SoftMin, SoftMax = d.SoftMax,
                ShowWhenKey = "shape", ShowWhenValues = shapes,
            };
            F("filter.displace", "ディスプレイスメントマップ", "Icon.Gradient",
                new ParamDefinition { Key = "shape", Name = "形", Kind = ParamKind.Choice, Choices = new[] { "円", "四角形", "画像ファイル" } },
                new ParamDefinition { Key = "mode", Name = "変形方法", Kind = ParamKind.Choice, Choices = new[] { "移動", "拡大", "回転" }, ShowWhenKey = "shape", ShowWhenValues = new[] { 0, 1 } },
                T("param1", "変形量 (移動 X・拡大 %・角度)", -10000, 10000, 50, 1, -500, 500),
                T("param2", "変形量 (移動 Y)", -10000, 10000, 0, 1, -500, 500),
                DS(T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), 0, 1), DS(T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000), 0, 1),
                DS(T("size", "サイズ", 1, 16384, 200, 0, 1, 2000), 0, 1), DS(T("aspect", "縦横比", -100, 100, 0, 1), 0, 1),
                DS(T("feather", "ぼかし (%)", 0, 100, 50, 1), 0, 1),
                new ParamDefinition { Key = "file", Name = "画像ファイル", Kind = ParamKind.File, FileExtensions = ImageExtensions, ShowWhenKey = "shape", ShowWhenValues = new[] { 2 } });
        }
        F("filter.mirror", "ミラー", "Icon.Flip",
            new ParamDefinition { Key = "direction", Name = "向き", Kind = ParamKind.Choice, Choices = new[] { "下", "上", "右", "左" } },
            T("alpha", "透明度", 0, 100, 50, 1), T("fade", "減衰 (%)", 0, 100, 60, 1), T("gap", "境目の間隔", -2000, 2000, 0, 0, -100, 200));
        F("filter.kaleido", "万華鏡", "Icon.Category",
            T("count", "分割数", 1, 180, 4, 0, 1, 24), T("rotation", "回転", -3600, 3600, 0, 1, -360, 360),
            T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000), C("clamp", "端を伸ばす"));
        F("filter.sphere", "球体", "Icon.Category",
            T("size", "大きさ (0 で自動)", 0, 8192, 0, 0, 0, 2000), T("rx", "X軸回転", -3600, 3600, 0, 1, -360, 360),
            T("ry", "Y軸回転", -3600, 3600, 0, 1, -360, 360), T("rz", "Z軸回転", -3600, 3600, 0, 1, -360, 360),
            T("spin", "自転の速さ (回/秒)", -100, 100, 0, 2, -5, 5), T("shade", "陰影", 0, 100, 40, 1));
        F("filter.freeform", "簡易変形", "Icon.Transform",
            T("x1", "左上X", -99999.9, 99999.9, 0, 1, -1000, 1000), T("y1", "左上Y", -99999.9, 99999.9, 0, 1, -1000, 1000),
            T("x2", "右上X", -99999.9, 99999.9, 0, 1, -1000, 1000), T("y2", "右上Y", -99999.9, 99999.9, 0, 1, -1000, 1000),
            T("x3", "右下X", -99999.9, 99999.9, 0, 1, -1000, 1000), T("y3", "右下Y", -99999.9, 99999.9, 0, 1, -1000, 1000),
            T("x4", "左下X", -99999.9, 99999.9, 0, 1, -1000, 1000), T("y4", "左下Y", -99999.9, 99999.9, 0, 1, -1000, 1000));
        // ---- その他 ----
        F("filter.basic", "基本効果", "Icon.Transform",
            T("x", "X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
            T("rotation", "回転", -3600, 3600, 0, 2, -360, 360), T("scale", "拡大率", 0, 5000, 100, 2, 0, 800),
            T("alpha", "透明度", 0, 100, 0), T("aspect", "縦横比", -100, 100, 0, 1));
        F("filter.shake", "振動", "Icon.Transform",
            T("x", "X", 0, 4000, 10, 1, 0, 200), T("y", "Y", 0, 4000, 10, 1, 0, 200), T("z", "Z", 0, 4000, 0, 1, 0, 200),
            T("rotation", "回転", 0, 360, 0, 1, 0, 90), T("period", "周期 (フレーム)", 1, 1000, 1, 0, 1, 30),
            T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100), C("smooth", "なめらかに揺らす"), C("perObject", "オブジェクトごとに揺れ方を変える", true));
        F("filter.loop", "画像ループ", "Icon.GridOn",
            T("countX", "横の数", 1, 100, 2, 0, 1, 20), T("countY", "縦の数", 1, 100, 2, 0, 1, 20),
            T("speedX", "横の速さ (px/秒)", -100000, 100000, 0, 1, -2000, 2000), T("speedY", "縦の速さ (px/秒)", -100000, 100000, 0, 1, -2000, 2000));
        F("filter.imageblend", "画像ファイル合成", "Icon.Image",
            new ParamDefinition { Key = "file", Name = "ファイル", Kind = ParamKind.File, FileExtensions = ImageExtensions },
            T("x", "X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
            T("scale", "拡大率", 0, 5000, 100, 2, 0, 800), T("alpha", "透明度", 0, 100, 0),
            new ParamDefinition { Key = "blend", Name = "合成モード", Kind = ParamKind.Choice, Choices = FileBlendModes }, C("tile", "ループ画像 (敷き詰める)"));
        F("filter.videoblend", "動画ファイル合成", "Icon.Movie",
            new ParamDefinition { Key = "file", Name = "ファイル", Kind = ParamKind.File, FileExtensions = VideoExtensions.Concat(AnimatedImageFiles.Extensions).ToArray() },
            T("position", "開始位置", 0, 36000, 0, 2, 0, 600), T("speed", "再生速度", 0, 2000, 100, 1, 0, 400), C("loopVideo", "ループ再生", true),
            T("x", "X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
            T("scale", "拡大率", 0, 5000, 100, 2, 0, 800), T("alpha", "透明度", 0, 100, 0),
            new ParamDefinition { Key = "blend", Name = "合成モード", Kind = ParamKind.Choice, Choices = FileBlendModes }, C("tile", "ループ画像 (敷き詰める)"));
        F("filter.deinterlace", "インターレース解除", "Icon.Tune",
            new ParamDefinition { Key = "mode", Name = "方式", Kind = ParamKind.Choice, Choices = new[] { "奇数の行", "偶数の行", "二重化", "平均" } });
        // ---- 配置・動き ----
        F("filter.shatter", "砕け散る", "Icon.Category",
            T("start", "開始時間 (秒)", -100, 100, 0, 2, -5, 10),
            T("x", "中心X", -1000, 1000, 0, 1), T("y", "中心Y", -1000, 1000, 0, 1),
            T("size", "破片サイズ", 10, 1000, 50, 0, 10, 300), T("speed", "速度", 0, 10000, 100, 0, 0, 1000),
            T("gravity", "重力", -10000, 10000, 100, 0, -500, 1000), T("delay", "時間差", 0, 10000, 100, 0, 0, 500),
            T("distance", "距離影響", 0, 10000, 100, 0, 0, 500), T("rotation", "ランダム回転", 0, 10000, 100, 0, 0, 500),
            T("direction", "ランダム方向", 0, 10000, 100, 0, 0, 500), T("playback", "再生速度", -10, 10, 1, 2, -4, 4),
            T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        // 以前の「砕け散る」(四角い破片が一度に飛び散って消える)
        F("filter.scatter", "飛び散る", "Icon.Category",
            T("start", "開始 (秒)", 0, 3600, 0, 2, 0, 10), T("speed", "速さ", 0, 10000, 400, 0, 0, 2000), T("gravity", "重力", -10000, 10000, 600, 0, -2000, 3000),
            T("spin", "回転の速さ", 0, 3600, 360, 0, 0, 1080), T("size", "破片の大きさ", 2, 1000, 40, 0, 2, 200),
            T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000),
            T("fade", "消えるまで (秒, 0 で消えない)", 0, 3600, 1.5, 2, 0, 10), T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        F("filter.circle", "円形配置", "Icon.Category",
            T("count", "個数", 1, 1000, 6, 0, 1, 36), T("radius", "半径", -10000, 10000, 200, 1, 0, 1000),
            T("rotation", "回転", -3600, 3600, 0, 1, -360, 360), T("spin", "回る速さ (回/秒)", -100, 100, 0, 2, -5, 5),
            T("scale", "拡大率", 0, 5000, 100, 1, 0, 400), C("face", "外側に向ける"));
        F("filter.random", "ランダム配置", "Icon.Category",
            T("count", "個数", 1, 2000, 10, 0, 1, 200), T("width", "範囲の幅", 0, 20000, 1000, 0, 0, 4000), T("height", "範囲の高さ", 0, 20000, 600, 0, 0, 4000),
            T("sizeVar", "大きさのばらつき (%)", 0, 100, 0, 1), T("rotVar", "回転のばらつき", 0, 360, 0, 1), T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        // ---- 動き (キーフレームを打たずに動かす) ----
        static ParamDefinition AT(string key, string name, double min, double max, double def, int dec, double? softMin, double? softMax, params int[] types) =>
            new() { Key = key, Name = name, Min = min, Max = max, Default = def, Decimals = dec, SoftMin = softMin, SoftMax = softMax, ShowWhenKey = "type", ShowWhenValues = types };
        F("filter.anim", "アニメーション効果", "Icon.Play",
            new ParamDefinition { Key = "type", Name = "種類", Kind = ParamKind.Choice, Choices = AnimationTypes },
            AT("strength", "強さ", -99999, 99999, 20, 1, 0, 200, 0, 3, 4, 5, 6, 7, 8, 14, 17, 18, 21, 22, 23, 24, 25, 26, 27),
            AT("speed", "速さ (回/秒)", -100, 100, 1, 2, -10, 10, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 28),
            AT("dx", "移動量X", -99999, 99999, 200, 1, -2000, 2000, 9, 15, 16, 25, 27), AT("dy", "移動量Y", -99999, 99999, 0, 1, -2000, 2000, 9, 15, 16, 25, 27),
            new ParamDefinition { Key = "face", Name = "面", Kind = ParamKind.Choice, Choices = CubeFaces, ShowWhenKey = "type", ShowWhenValues = new[] { 12 } },
            AT("cube", "立方体の大きさ (0 で幅)", 0, 99999, 0, 0, 0, 2000, 12),
            AT("turns", "回転数", -50, 50, 1, 1, -10, 10, 13), AT("reelBlur", "ブラー", 0, 200, 100, 0, null, null, 13),
            new ParamDefinition { Key = "reelHorizontal", Name = "横回転", Kind = ParamKind.Check, ShowWhenKey = "type", ShowWhenValues = new[] { 13 } },
            AT("rate", "揺れる回数 (回/秒)", 0.1, 240, 30, 1, 0.1, 60, 0),
            AT("rotation", "回転の揺れ", 0, 360, 0, 1, 0, 45, 0, 26),
            AT("seed", "乱数シード", 0, 99999, 0, 0, 0, 100, 0, 26),
            AT("depth", "消える濃さ (%)", 0, 100, 100, 1, null, null, 1, 28),
            new ParamDefinition { Key = "smooth", Name = "なめらかに揺らす", Kind = ParamKind.Check, ShowWhenKey = "type", ShowWhenValues = new[] { 0 } },
            // 呼吸: 明るさの変化 / 旗: 波長 / 跳ねながら往復: 向きを変える / カーソル点滅: 色・太さ
            AT("glow", "明るさの変化 (%)", -100, 100, 15, 1, -50, 50, 18),
            AT("wavelength", "波長", 1, 10000, 240, 0, 10, 1000, 24),
            new ParamDefinition { Key = "turn", Name = "戻るときに向きを変える", Kind = ParamKind.Check, Default = 1, ShowWhenKey = "type", ShowWhenValues = new[] { 25 } },
            new ParamDefinition { Key = "color", Name = "色", Kind = ParamKind.Color, DefaultText = "#FFFFFF", ShowWhenKey = "type", ShowWhenValues = new[] { 28 } },
            AT("width", "太さ (%)", 1, 100, 8, 1, 1, 30, 28),
            // どの種類にも効く
            T("delay", "開始の遅延 (秒)", 0, 3600, 0, 2, 0, 10),
            T("phase", "位相 (度)", -3600, 3600, 0, 1, -360, 360),
            T("count", "繰り返す回数 (0 で無限)", 0, 10000, 0, 0, 0, 20),
            T("ax", "中心X (回転・拡大の中心)", -99999.9, 99999.9, 0, 1, -1000, 1000),
            T("ay", "中心Y (回転・拡大の中心)", -99999.9, 99999.9, 0, 1, -1000, 1000));
        F("filter.script", "スクリプト制御", "Icon.Code",
            new ParamDefinition { Key = "code", Name = "Lua スクリプト", Kind = ParamKind.MultilineText, DefaultText = "-- obj.ox, obj.oy : 位置のずれ   obj.zoom : 拡大率 (1 = 等倍)\n-- obj.rz : 回転   obj.alpha : 不透明度 (1 = そのまま)\nobj.oy = math.sin(obj.time * 4) * 20" });

        // ---- 色 (追加) ----
        static ParamDefinition Ch(string key, string name, string[] choices, int def = 0) => new() { Key = key, Name = name, Kind = ParamKind.Choice, Choices = choices, Default = def };
        F("filter.lut", "LUT (カラーグレーディング)", "Icon.Palette",
            new ParamDefinition { Key = "file", Name = "LUT ファイル (.cube)", Kind = ParamKind.File, FileExtensions = new[] { "cube" } },
            T("strength", "強さ (%)", 0, 100, 100, 1));
        F("filter.curves", "トーンカーブ", "Icon.Tune",
            Ch("channel", "チャンネル", new[] { "RGB", "赤", "緑", "青" }),
            T("p0", "黒 (入力 0)", 0, 255, 0, 0), T("p1", "暗部 (入力 64)", 0, 255, 64, 0), T("p2", "中間 (入力 128)", 0, 255, 128, 0),
            T("p3", "明部 (入力 192)", 0, 255, 192, 0), T("p4", "白 (入力 255)", 0, 255, 255, 0));
        F("filter.whitebalance", "色温度・色かぶり", "Icon.Palette",
            T("temperature", "色温度 (− 青 / + 黄)", -100, 100, 0, 1), T("tint", "色かぶり (− 緑 / + 赤紫)", -100, 100, 0, 1));
        F("filter.shadowhighlight", "シャドウ・ハイライト", "Icon.Tune",
            T("shadows", "暗部を明るく", -100, 100, 30, 1), T("highlights", "明部を暗く", -100, 100, 30, 1));
        F("filter.gradmap", "グラデーションマップ", "Icon.Gradient",
            Col("dark", "暗い所の色", "#1B2A6B"), Col("mid", "中間の色", "#D4507A"), C("useMid", "中間の色を使う (3 色)", true), Col("light", "明るい所の色", "#FFE29A"),
            T("strength", "強さ (%)", 0, 100, 100, 1));
        F("filter.look", "カラープリセット", "Icon.Palette",
            Ch("preset", "プリセット", LookPresets), T("strength", "強さ (%)", 0, 100, 100, 1));
        // ---- 質感 ----
        F("filter.grain", "フィルム粒子", "Icon.GridOn",
            T("amount", "強さ", 0, 100, 30, 1), T("size", "粒の大きさ", 0.5, 10, 1.5, 1), C("color", "色の粒"), C("animate", "毎フレーム変える", true), T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        F("filter.vhs", "VHS", "Icon.Movie",
            T("strength", "強さ", 0, 200, 100, 1), T("noise", "ノイズ", 0, 100, 30, 1), T("wobble", "揺れ", 0, 100, 30, 1), T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        F("filter.crt", "ブラウン管", "Icon.Movie",
            T("scanline", "走査線", 0, 100, 50, 1), T("mask", "RGB の格子", 0, 100, 40, 1), T("curvature", "画面の丸み", 0, 100, 20, 1), T("vignette", "周辺減光", 0, 100, 30, 1));
        F("filter.glitch", "グリッチ", "Icon.GridOn",
            T("strength", "強さ", 0, 100, 50, 1), T("frequency", "頻度 (%)", 0, 100, 30, 1), T("block", "ブロックの大きさ", 1, 500, 30, 0, 1, 200), T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        F("filter.halftone", "ハーフトーン (網点)", "Icon.GridOn",
            T("size", "網点の間隔", 2, 200, 8, 1, 2, 50), T("angle", "角度", -3600, 3600, 45, 1, -180, 180),
            Ch("mode", "色", new[] { "白黒", "元の色", "CMY (4 色刷り風)" }));
        F("filter.paint", "油絵・水彩", "Icon.Palette",
            Ch("type", "種類", new[] { "油絵", "水彩" }), T("radius", "筆の大きさ", 1, 30, 8, 0));
        F("filter.sketch", "スケッチ (鉛筆画)", "Icon.BorderStyle",
            T("strength", "線の濃さ", 0, 400, 100, 1, 0, 200), C("color", "色を残す"), Col("paper", "紙の色", "#F6F1E7"));
        F("filter.pixelart", "ドット絵化", "Icon.GridOn",
            T("size", "ドットの大きさ", 1, 200, 6, 0, 1, 40), T("levels", "色の段階", 2, 32, 5, 0), C("dither", "ディザ (網目で中間色を表す)", true));
        F("filter.ascii", "ASCII アート", "Icon.Title",
            T("size", "文字の大きさ", 4, 100, 10, 0, 4, 40), C("color", "元の色で描く", true), Col("background", "背景色", "#000000"),
            Ch("chars", "文字", new[] { "記号", "ブロック", "数字" }));
        // ---- 光 (追加) ----
        F("filter.lensflare", "レンズフレア", "Icon.LightMode",
            T("x", "光源X", -99999.9, 99999.9, -200, 1, -2000, 2000), T("y", "光源Y", -99999.9, 99999.9, -150, 1, -2000, 2000),
            T("strength", "強さ (%)", 0, 400, 100, 1, 0, 200), T("size", "大きさ (%)", 1, 1000, 100, 1, 10, 300), Col("color", "光の色", "#FFD8A0"));
        F("filter.lightleak", "光漏れ", "Icon.LightMode",
            T("strength", "強さ (%)", 0, 200, 60, 1), T("speed", "動く速さ", 0, 10, 0.3, 2), T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        // ---- 変形 (追加) ----
        F("filter.swirl", "渦巻き", "Icon.Gradient",
            T("angle", "ねじれ (度)", -3600, 3600, 180, 1, -720, 720), T("radius", "範囲", 1, 16384, 300, 0, 1, 2000),
            T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000));
        F("filter.bulge", "膨張・収縮", "Icon.Gradient",
            T("amount", "強さ (− 縮む / + ふくらむ)", -100, 100, 50, 1), T("radius", "範囲", 1, 16384, 300, 0, 1, 2000),
            T("x", "中心X", -99999.9, 99999.9, 0, 1, -2000, 2000), T("y", "中心Y", -99999.9, 99999.9, 0, 1, -2000, 2000));
        F("filter.fisheye", "魚眼レンズ", "Icon.Gradient",
            T("strength", "強さ (− 糸巻き・補正 / + 魚眼)", -100, 100, 50, 1));
        F("filter.mirrortile", "ミラータイル", "Icon.GridOn",
            T("countX", "横の数", 1, 50, 2, 0, 1, 10), T("countY", "縦の数", 1, 50, 2, 0, 1, 10), C("mirror", "交互に反転する", true));
        // ---- 時間・合成 ----
        F("filter.strobe", "コマ落とし", "Icon.Movie", T("step", "何フレームごとに動かすか", 1, 600, 3, 0, 1, 30));
        F("filter.trail", "残像", "Icon.Movie",
            T("count", "残像の数", 1, 30, 5, 0), T("interval", "間隔 (フレーム)", 1, 60, 2, 0, 1, 10), T("fade", "薄くなる割合 (%)", 0, 100, 70, 1));
        F("filter.trackmatte", "トラックマット", "Icon.Image",
            T("layer", "マットのレイヤー (0 で 1 つ上)", 0, 1000, 0, 0, 0, 30),
            Ch("mode", "使い方", new[] { "アルファ (形で切り抜く)", "アルファ反転", "ルミナンス (明るさで切り抜く)", "ルミナンス反転" }));
        // ---- 生成 ----
        F("filter.particles", "パーティクル", "Icon.Category",
            Ch("type", "種類", ParticleTypes), T("count", "数", 1, 2000, 80, 0, 1, 500), T("speed", "速さ (%)", 0, 1000, 100, 1, 0, 300),
            T("size", "大きさ (%)", 1, 1000, 100, 1, 10, 300), T("wind", "風 (横の流れ)", -1000, 1000, 0, 1, -300, 300),
            Col("color", "色 (空欄で種類ごとの色)", ""), T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        F("filter.speedlines", "集中線・スピード線", "Icon.Category",
            Ch("type", "種類", new[] { "集中線", "横のスピード線", "縦のスピード線" }), T("count", "本数", 1, 1000, 80, 0, 1, 300),
            T("inner", "空ける範囲 (%)", 0, 100, 45, 1), T("thickness", "太さ (%)", 1, 1000, 100, 1, 10, 300), Col("color", "線の色", "#FFFFFF"),
            C("animate", "毎フレーム変える", true), T("seed", "乱数シード", 0, 99999, 0, 0, 0, 100));
        F("filter.frame", "枠", "Icon.BorderStyle",
            Ch("type", "種類", new[] { "額縁", "ポラロイド", "映画の黒帯", "角丸" }), T("width", "幅", 0, 4000, 30, 0, 0, 300),
            Col("color", "色", "#FFFFFF"), T("radius", "角の丸み", 0, 4000, 24, 0, 0, 300), C("shadow", "影を付ける", true));

        // ---- 音声のエフェクト ----
        void A(string id, string name, string icon, params ParamDefinition[] ps) =>
            Register(new EffectDefinition { Id = id, Name = name, Category = EffectCategory.AudioFilter, Icon = icon, Color = "#7A7A7A", Params = ps });
        A("audio.fade", "音声フェード", "Icon.VolumeUp", T("in", "イン", 0, 30, 0.5, 2), T("out", "アウト", 0, 30, 0.5, 2),
            // 等パワーは、2 つの音を重ねて切り替える (クロスフェード) ときに、つなぎ目で音量が下がらない
            new ParamDefinition { Key = "curve", Name = "カーブ", Kind = ParamKind.Choice, Choices = new[] { "直線", "等パワー (クロスフェード向け)", "なめらか (S 字)" } });
        A("audio.echo", "エコー", "Icon.MusicNote", T("delay", "遅延 (ms)", 1, 2000, 250, 0, 1, 1000), T("feedback", "繰り返し", 0, 95, 40, 0), T("mix", "混ぜる量", 0, 100, 40, 0));
        A("audio.eq", "イコライザ", "Icon.Tune", T("low", "低音 (dB)", -24, 24, 0, 1), T("mid", "中音 (dB)", -24, 24, 0, 1), T("high", "高音 (dB)", -24, 24, 0, 1));
        A("audio.duck", "自動音量調整 (ダッキング)", "Icon.VolumeUp",
            T("amount", "下げる量 (dB)", 0, 60, 12, 1), T("threshold", "反応する大きさ (dB)", -80, 0, -40, 1),
            T("attack", "下げる速さ (秒)", 0.001, 5, 0.05, 3, 0.001, 1), T("release", "戻す速さ (秒)", 0.01, 10, 0.4, 2, 0.01, 3));
        A("audio.reverb", "リバーブ", "Icon.MusicNote",
            T("room", "部屋の大きさ (%)", 0, 100, 60, 0), T("damping", "高音の吸収 (%)", 0, 100, 50, 0), T("mix", "混ぜる量 (%)", 0, 100, 30, 0),
            T("width", "広がり (%)", 0, 100, 100, 0), T("predelay", "初期反射までの時間 (ms)", 0, 200, 20, 0));
        A("audio.compressor", "コンプレッサー", "Icon.VolumeUp",
            T("threshold", "しきい値 (dB)", -60, 0, -20, 1), T("ratio", "圧縮比 (:1)", 1, 20, 4, 1), T("attack", "アタック (ms)", 0.1, 200, 10, 1),
            T("release", "リリース (ms)", 5, 2000, 150, 0), T("knee", "ニー (dB)", 0, 24, 6, 1), T("makeup", "音量の持ち上げ (dB)", 0, 30, 0, 1));
        A("audio.limiter", "リミッター", "Icon.VolumeUp",
            T("ceiling", "上限 (dB)", -30, 0, -1, 1), T("gain", "入力の音量 (dB)", 0, 30, 0, 1), T("release", "戻す速さ (ms)", 5, 2000, 80, 0));
        A("audio.gate", "ノイズゲート", "Icon.VolumeUp",
            T("threshold", "しきい値 (dB)", -90, 0, -45, 1), T("reduction", "下げる量 (dB)", 0, 90, 30, 1), T("attack", "開く速さ (ms)", 0.1, 100, 2, 1),
            T("hold", "保持 (ms)", 0, 1000, 60, 0), T("release", "閉じる速さ (ms)", 5, 2000, 150, 0));
        A("audio.denoise", "ノイズ除去", "Icon.Tune",
            T("amount", "強さ (%)", 0, 100, 60, 0), T("floor", "最大で下げる量 (dB)", 0, 60, 24, 1));
        A("audio.pitch", "ピッチ変更", "Icon.MusicNote",
            T("semitones", "半音", -24, 24, 0, 1, -12, 12), T("cents", "セント (半音の 1/100)", -100, 100, 0, 0), T("mix", "混ぜる量 (%)", 0, 100, 100, 0));
        A("audio.filter", "フィルタ (ローパス・ハイパス)", "Icon.Tune",
            new ParamDefinition { Key = "type", Name = "種類", Kind = ParamKind.Choice, Choices = new[] { "ローパス (高音を切る)", "ハイパス (低音を切る)", "バンドパス", "ノッチ (1 つの音を消す)" } },
            T("frequency", "周波数 (Hz)", 20, 20000, 1000, 0, 20, 10000), T("q", "鋭さ (Q)", 0.1, 20, 0.71, 2, 0.1, 10), C("steep", "急な傾き (24 dB/oct)"));
        A("audio.loudness", "ラウドネスの正規化", "Icon.VolumeUp",
            T("target", "目標 (LUFS)", -60, 0, -14, 1, -30, -5), T("maxGain", "最大で上げる量 (dB)", 0, 40, 20, 1));
        Builtin = Map.Keys.ToHashSet();
    }

    /// <summary>図形の形ごとの設定と、塗り・線の設定。図形の種類で使わないものは設定画面に出さない。</summary>
    private static IEnumerable<ParamDefinition> ShapeStyleParams()
    {
        static ParamDefinition S(string key, string name, double min, double max, double def, int dec, double? softMin, double? softMax, params ShapeType[] types) =>
            new()
            {
                Key = key, Name = name, Min = min, Max = max, Default = def, Decimals = dec, SoftMin = softMin, SoftMax = softMax,
                ShowWhenKey = types.Length == 0 ? null : "type", ShowWhenValues = types.Select(t => (int)t).ToArray(),
            };
        var all = Enum.GetValues<ShapeType>();
        // 角を丸められるのは、直線でできた形
        var cornered = all.Except(new[] { ShapeType.Background, ShapeType.Circle, ShapeType.Heart, ShapeType.BubbleOval, ShapeType.Line }).ToArray();
        var arrows = new[] { ShapeType.Arrow, ShapeType.DoubleArrow };

        // ---- 形 ----
        yield return S("corner", "角の丸み", 0, 4000, 0, 0, 0, 200, cornered);
        yield return S("points", "頂点の数", 3, 100, 5, 0, 3, 24, ShapeType.Star, ShapeType.Polygon);
        yield return S("inner", "へこみ (%)", 1, 100, 38.2, 1, null, null, ShapeType.Star);
        yield return S("slant", "傾き (%)", -100, 100, 25, 1, null, null, ShapeType.Trapezoid, ShapeType.Parallelogram);
        yield return S("head", "矢じりの長さ (%)", 0, 100, 40, 1, null, null, ShapeType.Arrow, ShapeType.DoubleArrow, ShapeType.Chevron);
        yield return S("headWidth", "矢じりの幅 (%)", 0, 100, 100, 1, null, null, arrows);
        yield return S("thickness", "太さ (%)", 1, 100, 40, 1, null, null, ShapeType.Arrow, ShapeType.DoubleArrow, ShapeType.Cross, ShapeType.Bracket, ShapeType.Line);
        yield return S("tailX", "しっぽの位置 (%)", -100, 100, -40, 1, null, null, ShapeType.BubbleRect, ShapeType.BubbleOval);
        yield return S("tailLength", "しっぽの長さ (%)", 0, 90, 25, 1, null, null, ShapeType.BubbleRect, ShapeType.BubbleOval);
        yield return S("tailWidth", "しっぽの幅 (%)", 1, 100, 20, 1, null, null, ShapeType.BubbleRect, ShapeType.BubbleOval);
        yield return S("startAngle", "開始角度", -3600, 3600, 0, 1, -360, 360, ShapeType.Pie);
        yield return S("sweep", "中心角", 0, 360, 270, 1, null, null, ShapeType.Pie);
        yield return S("hole", "穴の大きさ (%)", 0, 99, 0, 1, null, null, ShapeType.Pie, ShapeType.Circle);
        yield return new ParamDefinition { Key = "startCap", Name = "始点の形", Kind = ParamKind.Choice, Choices = LineEnds, Default = 0, ShowWhenKey = "type", ShowWhenValues = new[] { (int)ShapeType.Line } };
        yield return new ParamDefinition { Key = "endCap", Name = "終点の形", Kind = ParamKind.Choice, Choices = LineEnds, Default = 1, ShowWhenKey = "type", ShowWhenValues = new[] { (int)ShapeType.Line } };

        // ---- 塗り ----
        yield return new ParamDefinition { Key = "fill", Name = "塗り", Kind = ParamKind.Choice, Choices = ShapeFills, Default = 0 };
        yield return new ParamDefinition { Key = "color2", Name = "色 2", Kind = ParamKind.Color, DefaultText = "#000000", ShowWhenKey = "fill", ShowWhenValues = new[] { 1, 2 } };
        yield return new() { Key = "gradAngle", Name = "グラデーションの角度", Min = -3600, Max = 3600, Default = 90, Decimals = 1, SoftMin = -360, SoftMax = 360, ShowWhenKey = "fill", ShowWhenValues = new[] { 1 } };
        yield return new() { Key = "gradCenter", Name = "グラデーションの中心 (%)", Min = 1, Max = 99, Default = 50, Decimals = 1, ShowWhenKey = "fill", ShowWhenValues = new[] { 1, 2 } };

        // ---- 縁の線 (塗りの上に重ねる) ----
        yield return new() { Key = "border", Name = "縁の幅", Min = 0, Max = 1000, Default = 0, Decimals = 1, SoftMin = 0, SoftMax = 50 };
        yield return new ParamDefinition { Key = "borderColor", Name = "縁の色", Kind = ParamKind.Color, DefaultText = "#000000" };

        // ---- 線の描き方 (ライン幅で線にしたとき・縁の線・直線) ----
        yield return new ParamDefinition { Key = "dash", Name = "線の種類", Kind = ParamKind.Choice, Choices = LineStyles, Default = 0 };
        yield return new() { Key = "dashLength", Name = "破線の長さ", Min = 0, Max = 2000, Default = 20, Decimals = 1, SoftMin = 0, SoftMax = 200, ShowWhenKey = "dash", ShowWhenValues = new[] { 1 } };
        yield return new() { Key = "dashGap", Name = "破線の間隔", Min = 0, Max = 2000, Default = 12, Decimals = 1, SoftMin = 0, SoftMax = 200, ShowWhenKey = "dash", ShowWhenValues = new[] { 1, 2 } };
        yield return new() { Key = "dashOffset", Name = "破線のずらし", Min = -100000, Max = 100000, Default = 0, Decimals = 1, SoftMin = -200, SoftMax = 200, ShowWhenKey = "dash", ShowWhenValues = new[] { 1, 2 } };
        yield return new ParamDefinition { Key = "join", Name = "角の形", Kind = ParamKind.Choice, Choices = LineJoins, Default = 0 };
    }

    /// <summary>エフェクトを追加するメニューの分類 (この順に並べる)。</summary>
    public static readonly string[] FilterGroups =
    {
        "色", "ぼかし", "光", "縁・輪郭", "キー (透過)", "変形", "切り抜き・マスク", "動き・配置", "質感", "時間・合成", "生成", "切り替え・その他",
    };
    /// <summary>カラープリセットの種類。番号はプロジェクトファイルに残るので、増やすときは末尾に足す。</summary>
    public static readonly string[] LookPresets =
    {
        "セピア", "ティール & オレンジ (映画風)", "褪色 (ノスタルジー)", "クール", "ウォーム", "白黒 (高コントラスト)", "ビビッド", "ブリーチバイパス", "夕焼け", "月明かり",
    };
    /// <summary>パーティクルの種類。</summary>
    public static readonly string[] ParticleTypes = { "紙吹雪", "雪", "雨", "火花", "泡", "きらめき" };

    private static string FilterGroupOf(string id) => id switch
    {
        "filter.color" or "filter.mono" or "filter.flip" or "filter.levels" or "filter.posterize" or "filter.extcolor" or "filter.hueconvert"
            or "filter.gradient" or "filter.vignette" or "filter.noise" or "filter.chromatic" or "filter.overlapinvert" => "色",
        "filter.blur" or "filter.dirblur" or "filter.radialblur" or "filter.lensblur" or "filter.motionblur" or "filter.edgeblur"
            or "filter.mosaic" or "filter.sharpen" => "ぼかし",
        "filter.glow" or "filter.emit" or "filter.diffuse" or "filter.flash" or "filter.light" or "filter.shadow" or "filter.innershadow" => "光",
        "filter.border" or "filter.bevel" or "filter.edge" or "filter.backplate" or "filter.extrude" => "縁・輪郭",
        "filter.chromakey" or "filter.colorkey" or "filter.lumakey" => "キー (透過)",
        "filter.raster" or "filter.ripple" or "filter.polar" or "filter.displace" or "filter.mirror" or "filter.kaleido" or "filter.sphere"
            or "filter.freeform" => "変形",
        "filter.clip" or "filter.diagclip" or "filter.mask" => "切り抜き・マスク",
        "filter.anim" or "filter.basic" or "filter.shake" or "filter.loop" or "filter.shatter" or "filter.scatter" or "filter.circle" or "filter.random" => "動き・配置",
        "filter.lut" or "filter.curves" or "filter.whitebalance" or "filter.shadowhighlight" or "filter.gradmap" or "filter.look" => "色",
        "filter.grain" or "filter.vhs" or "filter.crt" or "filter.glitch" or "filter.halftone" or "filter.paint" or "filter.sketch" or "filter.pixelart" or "filter.ascii" => "質感",
        "filter.lensflare" or "filter.lightleak" => "光",
        "filter.swirl" or "filter.bulge" or "filter.fisheye" or "filter.mirrortile" => "変形",
        "filter.strobe" or "filter.trail" or "filter.trackmatte" => "時間・合成",
        "filter.particles" or "filter.speedlines" or "filter.frame" => "生成",
        _ => "切り替え・その他",
    };

    // 組み込みのエフェクトの ID (ここに入っていないものはプラグインが追加したもの)
    private static readonly HashSet<string> Builtin;

    public static bool IsBuiltin(string id) => Builtin.Contains(id);

    public static void Register(EffectDefinition def)
    {
        lock (RegisterGate)
        {
            if (Map.TryAdd(def.Id, def)) _list = [.. _list, def];
        }
    }

    public static EffectDefinition Get(string id) =>
        Map.TryGetValue(id, out var d) ? d : new EffectDefinition { Id = id, Name = $"(不明: {id})", Category = EffectCategory.Filter };

    public static bool Exists(string id) => Map.ContainsKey(id);
    public static IReadOnlyList<EffectDefinition> All => List;
    public static IEnumerable<EffectDefinition> Filters => List.Where(d => d.Category == EffectCategory.Filter);
    public static IEnumerable<EffectDefinition> AudioFilters => List.Where(d => d.Category == EffectCategory.AudioFilter);

    /// <summary>このオブジェクトに追加できるエフェクト (映像のオブジェクトには映像、音声のオブジェクトには音声のもの)。</summary>
    public static IEnumerable<EffectDefinition> FiltersFor(TimelineObject o) =>
        o.Definition.Category == EffectCategory.AudioSource ? AudioFilters : Filters;
    public static IEnumerable<EffectDefinition> MediaObjects =>
        List.Where(d => d.Category is EffectCategory.VideoSource or EffectCategory.AudioSource or EffectCategory.Control);
}
