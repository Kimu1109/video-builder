using System.Text.Json.Serialization;

namespace VideoBuilder.Core;

/// <summary>
/// シンプル編集 (ムービーメーカー風) のプロジェクトの中身。クリップを 1 列に並べたものと、背景の音楽。
/// <para>
/// 描画・出力はこれまでどおりシーン (タイムラインのオブジェクト) で行う。ストーリーボードを編集するたびに
/// <see cref="StoryboardCompiler"/> がメインのシーンのオブジェクトを作り直す。つまり、シーンはストーリーボードから
/// 作られる「結果」で、ストーリーボードが元になる。詳細編集でシーンを直接変えたかどうかは
/// <see cref="StoryboardCompiler.IsInSync"/> で調べる。
/// </para>
/// </summary>
public sealed class Storyboard
{
    public List<StoryClip> Clips { get; set; } = new();
    public List<MusicClip> Music { get; set; } = new();
    /// <summary>音楽を、映像 (クリップ) の終わりで切る。</summary>
    public bool FitMusicToVideo { get; set; } = true;
    /// <summary>最後のクリップの終わりで、黒い画面へフェードアウトする長さ (秒)。0 ならしない。</summary>
    public double EndFadeSeconds { get; set; }
    /// <summary>写真・動画の周りの余白 (画面に収めたときにできる帯) の色。空なら黒 (何も置かない)。</summary>
    public string Background { get; set; } = "";

    public StoryClip? FindClip(string id) => Clips.FirstOrDefault(c => c.Id == id);
    public MusicClip? FindMusic(string id) => Music.FirstOrDefault(m => m.Id == id);

    public Storyboard Clone() => new()
    {
        Clips = Clips.Select(c => c.Clone()).ToList(),
        Music = Music.Select(m => m.Clone()).ToList(),
        FitMusicToVideo = FitMusicToVideo,
        EndFadeSeconds = EndFadeSeconds,
        Background = Background,
    };

    internal static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

public enum StoryClipKind { Video, Photo, Title }

/// <summary>クリップの動き (写真などをゆっくり拡大・移動させる、いわゆるケン・バーンズ効果)。</summary>
/// <para><see cref="ScrollUp"/> はタイトル用 (文字を下から上へ流す、エンドロール)。</para>
public enum ClipMotion { None, ZoomIn, ZoomOut, PanLeft, PanRight, ScrollUp }

/// <summary>クリップの見た目の効果。</summary>
public enum ClipLook { None, Mono, Sepia, Blur, Bright, Dark, Vivid }

public enum CaptionPlacement { Bottom, Middle, Top }

/// <summary>ストーリーボードの 1 つのクリップ (動画・写真・タイトル)。時間はすべて秒。</summary>
public sealed class StoryClip
{
    /// <summary>写真・タイトルの最短の長さ、動画の最短の切り出し (秒)。</summary>
    public const double MinSeconds = 0.1;

    public string Id { get; set; } = Storyboard.NewId();
    public StoryClipKind Kind { get; set; }

    // ---- 素材 (動画・写真) ----
    public string File { get; set; } = "";
    /// <summary>素材の画像の大きさ (ピクセル)。画面に合わせて拡大縮小するのに使う。0 なら分からない (そのままの大きさで描く)。</summary>
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    /// <summary>動画の長さ (秒)。0 なら分からない。</summary>
    public double SourceSeconds { get; set; }
    public bool HasAudio { get; set; }

    // ---- 長さ ----
    /// <summary>動画の使う範囲の始まり (素材の先頭からの秒)。</summary>
    public double TrimStart { get; set; }
    /// <summary>動画の使う範囲の終わり (素材の先頭からの秒)。0 なら素材の最後まで。</summary>
    public double TrimEnd { get; set; }
    /// <summary>写真・タイトルの表示時間 (秒)。</summary>
    public double Duration { get; set; } = 5;
    /// <summary>動画の再生速度 (倍。1 = そのまま、2 = 2 倍速)。音声も同じ速さになる (音の高さも変わる)。</summary>
    public double Speed { get; set; } = 1;

    /// <summary>選べる再生速度。音声の再生速度の上限 (8 倍) に合わせる。</summary>
    public static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, 8 };
    public const double MinSpeed = 0.1, MaxSpeed = 8;

    /// <summary>実際に使う再生速度 (範囲に収めたもの)。動画以外は 1。</summary>
    [JsonIgnore] public double EffectiveSpeed => Kind == StoryClipKind.Video ? Math.Clamp(Speed, MinSpeed, MaxSpeed) : 1;

    // ---- 切り替え効果 (このクリップに入るとき) ----
    /// <summary>0 = なし、1 以上 = <see cref="EffectRegistry.TransitionTypes"/> の (番号 + 1)。</summary>
    public int Transition { get; set; }
    public double TransitionSeconds { get; set; } = 1;

    // ---- 見た目 ----
    public ClipMotion Motion { get; set; }
    public ClipLook Look { get; set; }
    /// <summary>回転 (0・90・180・270 度、時計回り)。</summary>
    public int Rotation { get; set; }
    /// <summary>画面いっぱいに広げる (はみ出た所は切れる)。false なら画面に収める。</summary>
    public bool Fill { get; set; }

    // ---- 音声 (動画) ----
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
    /// <summary>速度を変えても、音の高さを変えない。</summary>
    public bool KeepPitch { get; set; }

    // ---- 文字 ----
    /// <summary>クリップに重ねる字幕 (キャプション)。空なら出さない。</summary>
    public string Caption { get; set; } = "";
    public CaptionPlacement CaptionPlacement { get; set; }
    /// <summary>キャプションの後ろに半透明の帯を敷く。</summary>
    public bool CaptionBackground { get; set; }
    /// <summary>キャプションを出し始める時刻 (クリップの先頭からの秒)。</summary>
    public double CaptionStart { get; set; }
    /// <summary>キャプションを出す長さ (秒)。0 ならクリップの終わりまで。</summary>
    public double CaptionSeconds { get; set; }
    /// <summary>文字 (タイトル・キャプション) の大きさ (%、100 が標準)。</summary>
    public double TextScale { get; set; } = 100;
    /// <summary>文字のフォント。空なら既定のフォント。</summary>
    public string Font { get; set; } = "";
    /// <summary>タイトルの文字。</summary>
    public string Text { get; set; } = "";
    public string TextColor { get; set; } = "#FFFFFF";
    /// <summary>タイトルの背景の色。</summary>
    public string Background { get; set; } = "#000000";

    /// <summary>動画の使う範囲の終わり (TrimEnd が 0 なら素材の長さ)。</summary>
    [JsonIgnore] public double EffectiveTrimEnd => TrimEnd > 0 ? TrimEnd : SourceSeconds;

    /// <summary>このクリップの長さ (秒)。動画は、使う範囲を再生速度で割ったもの。</summary>
    [JsonIgnore]
    public double Seconds => Kind == StoryClipKind.Video
        ? Math.Max(MinSeconds, (EffectiveTrimEnd - TrimStart) / EffectiveSpeed)
        : Math.Max(MinSeconds, Duration);

    /// <summary>素材のファイルを使うクリップか (タイトルは使わない)。</summary>
    [JsonIgnore] public bool UsesFile => Kind != StoryClipKind.Title;

    public StoryClip Clone(bool newId = false)
    {
        var c = (StoryClip)MemberwiseClone();
        if (newId) c.Id = Storyboard.NewId();
        return c;
    }

    public static StoryClip Title(string text) => new() { Kind = StoryClipKind.Title, Text = text, Duration = 4 };
}

/// <summary>背景の音楽 (クリップとは別に、決まった時刻から鳴らす)。</summary>
public sealed class MusicClip
{
    public string Id { get; set; } = Storyboard.NewId();
    public string File { get; set; } = "";
    /// <summary>素材の長さ (秒)。0 なら分からない。</summary>
    public double SourceSeconds { get; set; }
    /// <summary>鳴らし始める時刻 (作品の先頭からの秒)。</summary>
    public double StartSeconds { get; set; }
    public double TrimStart { get; set; }
    /// <summary>0 なら素材の最後まで。</summary>
    public double TrimEnd { get; set; }
    public double Volume { get; set; } = 100;
    public double FadeIn { get; set; } = 1;
    public double FadeOut { get; set; } = 2;
    /// <summary>使う範囲を繰り返して、映像の終わりまで鳴らす。</summary>
    public bool Loop { get; set; }
    /// <summary>動画の音声が鳴っている間は、音楽を小さくする (ダッキング)。</summary>
    public bool Duck { get; set; }

    [JsonIgnore] public double EffectiveTrimEnd => TrimEnd > 0 ? TrimEnd : SourceSeconds;
    [JsonIgnore] public double Seconds => Math.Max(StoryClip.MinSeconds, EffectiveTrimEnd - TrimStart);

    public MusicClip Clone(bool newId = false)
    {
        var m = (MusicClip)MemberwiseClone();
        if (newId) m.Id = Storyboard.NewId();
        return m;
    }
}
