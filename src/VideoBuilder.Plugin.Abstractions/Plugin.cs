using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.Plugins;

/// <summary>
/// プラグインの入口。プラグインのアセンブリに、この実装を 1 つ以上置く (引数なしのコンストラクタが必要)。
/// </summary>
public interface IPlugin
{
    /// <summary>起動時に 1 回呼ばれる。host に効果などを登録する。</summary>
    void Initialize(IPluginHost host);
}

public interface IPluginHost
{
    /// <summary>このアプリのプラグイン API のバージョン。</summary>
    int ApiVersion { get; }

    /// <summary>映像のエフェクトを追加する (いちばん自由な形)。definition.Category は Filter にする。</summary>
    void RegisterFilter(EffectDefinition definition, IVideoEffect effect);

    /// <summary>ピクセルを直接書き換える映像のエフェクトを追加する (速い)。definition.Category は Filter にする。</summary>
    void RegisterPixelEffect(EffectDefinition definition, IPixelEffect effect);

    /// <summary>色の変換だけを行う映像のエフェクトを追加する (いちばん速い)。definition.Category は Filter にする。</summary>
    void RegisterColorMatrixEffect(EffectDefinition definition, IColorMatrixEffect effect);

    /// <summary>音声のエフェクトを追加する。definition.Category は AudioFilter にする。</summary>
    void RegisterAudioEffect(EffectDefinition definition, IAudioEffect effect);

    /// <summary>メディアオブジェクト (絵を作るもの) を追加する。definition.Category は VideoSource にする。</summary>
    void RegisterMediaObject(EffectDefinition definition, IVideoSource source);

    /// <summary>トラックバーの移動方法を追加する。</summary>
    void RegisterInterpolator(IInterpolator interpolator);

    /// <summary>ログに書く (プラグインの名前が付く)。</summary>
    void Log(string message);
}

/// <summary>メディアオブジェクトの絵を作る。</summary>
public interface IVideoSource
{
    /// <summary>
    /// 現在のフレームの絵を返す (出さないときは null)。新しく作った画像は args.Context.Own() に渡す。
    /// 作業用の描画先は args.Context.CreateSurface を使う (GPU 描画中は GPU 上に作られる)。
    /// </summary>
    SKImage? Render(EffectArgs args);

    /// <summary>
    /// 絵を使い回すための鍵。同じ鍵を返したフレームでは、エンジンが前に作った絵を使い、Render を呼ばない。
    /// 絵がパラメータだけで決まるなら、パラメータの値から作った文字列を返す (例: "40|#00FF00")。
    /// 毎フレーム作り直す必要があれば null (既定)。
    /// 使い回すのは、Render が返した画像を args.Context.Own() に渡していた場合だけ (エンジンが引き取る)。
    /// </summary>
    string? CacheKey(EffectArgs args) => null;
}

/// <summary>
/// 音声のエフェクト。オブジェクトの音声を、短い区切り (ブロック) ごとに、その場で書き換える。
/// </summary>
public interface IAudioEffect
{
    string Id { get; }

    /// <summary>
    /// block.Samples (ステレオ・float・-1〜1、左右が交互に並ぶ) をその場で書き換える。
    /// state は、このオブジェクトのこのエフェクト専用の入れ物 (最初は null)。前のブロックから続く処理
    /// (エコーの残響・フィルタの内部の値など) はここに入れる。block.Continuous が false (シークした後など) なら、
    /// 前の続きではないので、state をやり直すこと。
    /// 再生中は音声のスレッドから呼ばれるので、時間のかかる処理やメモリの確保を毎回しないこと。
    /// </summary>
    void Process(AudioBlock block, ref object? state);
}

/// <summary>IAudioEffect に渡す、音声のひと区切り。</summary>
public readonly ref struct AudioBlock
{
    /// <summary>ステレオ (左右が交互)。長さ = サンプル数 x 2。</summary>
    public Span<float> Samples { get; init; }
    public int SampleRate { get; init; }
    /// <summary>ブロックの先頭の、オブジェクトの先頭からのサンプル位置。</summary>
    public long ObjectSample { get; init; }
    /// <summary>オブジェクトの長さ (サンプル数)。フェードアウトなどに使う。</summary>
    public long ObjectLength { get; init; }
    /// <summary>前のブロックの直後から続いているか (シーク・再生開始の直後は false)。</summary>
    public bool Continuous { get; init; }
    public TimelineObject Object { get; init; }
    public Effect Effect { get; init; }
    public Project Project { get; init; }
    /// <summary>ブロックの先頭のフレーム (トラックバーの値はこのフレームで評価する)。</summary>
    public int Frame { get; init; }

    public int FrameCount => Samples.Length / 2;
    public float Track(string key) => (float)Effect.Eval(key, Object.Bounds, Frame);
    public bool Check(string key) => Effect.GetBool(key);
}

public static class PluginApi
{
    /// <summary>このアプリのプラグイン API のバージョン。</summary>
    public const int Version = 2;
    /// <summary>読み込めるいちばん古いバージョン (1 で作ったプラグインもそのまま動く)。</summary>
    public const int MinimumVersion = 1;
}
