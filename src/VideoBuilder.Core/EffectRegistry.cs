namespace VideoBuilder.Core;

/// <summary>組み込みの効果定義。プラグインは Register で追加する。</summary>
public static class EffectRegistry
{
    private static readonly Dictionary<string, EffectDefinition> Map = new();
    private static readonly List<EffectDefinition> List = new();

    public static readonly string[] BlendModes =
    {
        "通常", "加算", "減算", "乗算", "スクリーン", "オーバーレイ", "比較(明)", "比較(暗)", "輝度", "カラー", "焼き込みカラー", "ハードライト", "差分",
    };

    public static readonly string[] ShapeTypes = { "背景", "円", "四角形", "三角形", "五角形", "六角形", "星型" };

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
                new ParamDefinition { Key = "file", Name = "ファイル", Kind = ParamKind.File, FileExtensions = VideoExtensions },
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
            },
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
            },
        });
        Register(new EffectDefinition
        {
            Id = StandardPlay, Name = "音声", Category = EffectCategory.AudioPlay, Icon = "Icon.VolumeUp",
            Params = new[] { T("volume", "音量", 0, 500, 100), T("pan", "左右", -100, 100, 0) },
        });

        // ---- フィルタ効果 ----
        void F(string id, string name, string icon, params ParamDefinition[] ps) =>
            Register(new EffectDefinition { Id = id, Name = name, Category = EffectCategory.Filter, Icon = icon, Color = "#7A7A7A", Params = ps });

        F("filter.color", "色調補正", "Icon.Tune",
            T("brightness", "明るさ", 0, 200, 100), T("contrast", "コントラスト", 0, 200, 100),
            T("hue", "色相", -360, 360, 0), T("luminance", "輝度", 0, 200, 100), T("saturation", "彩度", 0, 200, 100));
        F("filter.blur", "ぼかし", "Icon.BlurOn", T("radius", "範囲", 0, 1000, 5, 0, 0, 200), C("fixed", "サイズ固定"));
        F("filter.border", "縁取り", "Icon.BorderStyle", T("size", "サイズ", 0, 500, 3, 0), T("blur", "ぼかし", 0, 100, 0, 0), Col("color", "縁色", "#000000"));
        F("filter.shadow", "シャドー", "Icon.Layers",
            T("x", "X", -200, 200, 8, 0), T("y", "Y", -200, 200, 8, 0), T("density", "濃さ", 0, 100, 60), T("spread", "拡散", 0, 50, 10, 0), Col("color", "影色", "#000000"));
        F("filter.clip", "クロップ", "Icon.Crop", T("top", "上", 0, 4000, 0, 0, 0, 1000), T("bottom", "下", 0, 4000, 0, 0, 0, 1000), T("left", "左", 0, 4000, 0, 0, 0, 1000), T("right", "右", 0, 4000, 0, 0, 0, 1000));
        F("filter.mosaic", "モザイク", "Icon.GridOn", T("size", "サイズ", 1, 1000, 12, 0, 1, 200));
        F("filter.chromakey", "クロマキー", "Icon.Colorize", T("hue", "色相範囲", 0, 256, 24, 0), T("saturation", "彩度範囲", 0, 256, 96, 0), T("edge", "境界補正", 0, 100, 10, 0), Col("color", "キー色", "#00FF00"));
        F("filter.mono", "単色化", "Icon.InvertColors", T("strength", "強さ", 0, 100, 100), Col("color", "色", "#FFFFFF"), C("keep", "輝度を保持する", true));
        F("filter.flip", "反転", "Icon.Flip", C("vertical", "上下反転"), C("horizontal", "左右反転"), C("luma", "輝度反転"));
        F("filter.fade", "フェード", "Icon.Gradient", T("in", "イン", 0, 30, 0.5, 2), T("out", "アウト", 0, 30, 0.5, 2));
        F("filter.script", "スクリプト制御", "Icon.Code",
            new ParamDefinition { Key = "code", Name = "Lua スクリプト", Kind = ParamKind.MultilineText, DefaultText = "-- obj.ox, obj.oy : 位置のずれ   obj.zoom : 拡大率 (1 = 等倍)\n-- obj.rz : 回転   obj.alpha : 不透明度 (1 = そのまま)\nobj.oy = math.sin(obj.time * 4) * 20" });

        // ---- 音声のエフェクト ----
        void A(string id, string name, string icon, params ParamDefinition[] ps) =>
            Register(new EffectDefinition { Id = id, Name = name, Category = EffectCategory.AudioFilter, Icon = icon, Color = "#7A7A7A", Params = ps });
        A("audio.fade", "音声フェード", "Icon.VolumeUp", T("in", "イン", 0, 30, 0.5, 2), T("out", "アウト", 0, 30, 0.5, 2));
        A("audio.echo", "エコー", "Icon.MusicNote", T("delay", "遅延 (ms)", 1, 2000, 250, 0, 1, 1000), T("feedback", "繰り返し", 0, 95, 40, 0), T("mix", "混ぜる量", 0, 100, 40, 0));
        A("audio.eq", "イコライザ", "Icon.Tune", T("low", "低音 (dB)", -24, 24, 0, 1), T("mid", "中音 (dB)", -24, 24, 0, 1), T("high", "高音 (dB)", -24, 24, 0, 1));
        A("audio.duck", "自動音量調整 (ダッキング)", "Icon.VolumeUp",
            T("amount", "下げる量 (dB)", 0, 60, 12, 1), T("threshold", "反応する大きさ (dB)", -80, 0, -40, 1),
            T("attack", "下げる速さ (秒)", 0.001, 5, 0.05, 3, 0.001, 1), T("release", "戻す速さ (秒)", 0.01, 10, 0.4, 2, 0.01, 3));
        Builtin = Map.Keys.ToHashSet();
    }

    // 組み込みのエフェクトの ID (ここに入っていないものはプラグインが追加したもの)
    private static readonly HashSet<string> Builtin;

    public static bool IsBuiltin(string id) => Builtin.Contains(id);

    public static void Register(EffectDefinition def)
    {
        if (Map.TryAdd(def.Id, def)) List.Add(def);
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
