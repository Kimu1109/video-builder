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

    /// <summary>フィルタ効果を追加する。definition.Category は Filter にする。</summary>
    void RegisterFilter(EffectDefinition definition, IVideoEffect effect);

    /// <summary>メディアオブジェクト (絵を作るもの) を追加する。definition.Category は VideoSource にする。</summary>
    void RegisterMediaObject(EffectDefinition definition, IVideoSource source);

    /// <summary>トラックバーの移動方法を追加する。</summary>
    void RegisterInterpolator(IInterpolator interpolator);
}

/// <summary>メディアオブジェクトの絵を作る。</summary>
public interface IVideoSource
{
    /// <summary>
    /// 現在のフレームの絵を返す (出さないときは null)。新しく作った画像は args.Context.Own() に渡して、
    /// フレームの描画が終わったら破棄されるようにする。
    /// </summary>
    SKImage? Render(EffectArgs args);
}

public static class PluginApi
{
    public const int Version = 1;
}
