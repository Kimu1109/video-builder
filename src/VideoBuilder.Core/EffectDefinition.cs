namespace VideoBuilder.Core;

public enum ParamKind { Track, Check, Choice, Text, MultilineText, Color, File, Font }

public enum EffectCategory
{
    VideoSource,   // 動画・画像・テキスト・図形など、絵を出すもの
    AudioSource,   // 音声ファイル
    Filter,        // フィルタ効果
    Draw,          // 標準描画
    AudioPlay,     // 標準再生
    Control,       // グループ制御など
}

public sealed class ParamDefinition
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public ParamKind Kind { get; init; } = ParamKind.Track;
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Default { get; init; }
    public int Decimals { get; init; } = 1;
    public string DefaultText { get; init; } = "";
    public string[] Choices { get; init; } = Array.Empty<string>();
    /// <summary>ファイル選択ダイアログ用の拡張子。</summary>
    public string[] FileExtensions { get; init; } = Array.Empty<string>();

    public double Step => Math.Pow(10, -Decimals);
}

public sealed class EffectDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required EffectCategory Category { get; init; }
    /// <summary>Icons.axaml のリソースキー ("Icon.Movie" など)。</summary>
    public string Icon { get; init; } = "Icon.Filter";
    /// <summary>タイムライン上のオブジェクト色 (#RRGGBB)。</summary>
    public string Color { get; init; } = "#7A7A7A";
    public IReadOnlyList<ParamDefinition> Params { get; init; } = Array.Empty<ParamDefinition>();

    public ParamDefinition? Find(string key) => Params.FirstOrDefault(p => p.Key == key);
}
