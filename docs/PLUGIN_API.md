# プラグイン API (バージョン 2)

video-builder のプラグインは .NET のクラスライブラリです。見本は `samples/SamplePlugin`。

- 参照するもの: `src/VideoBuilder.Plugin.Abstractions` (名前空間 `VideoBuilder.Plugins`) と SkiaSharp。
  どちらもアプリ本体のものを使うので、出力フォルダにコピーしない (見本の `.csproj` を参照)。
- 置き場所: `$XDG_DATA_HOME/video-builder/plugins/<名前>/` に `plugin.json` と DLL。
- `plugin.json`:

  ```json
  { "id": "my.plugin", "name": "わたしのプラグイン", "version": "1.0.0", "apiVersion": 2, "assembly": "MyPlugin.dll" }
  ```

  `apiVersion` は `PluginApi.MinimumVersion` (1) 〜 `PluginApi.Version` (2) なら読み込みます。バージョン 1 で作ったプラグインは作り直さずにそのまま動きます。

## 登録できるもの

`IPlugin.Initialize(IPluginHost host)` の中で登録します。

| メソッド | 用途 | 速さ |
|---|---|---|
| `RegisterColorMatrixEffect(定義, IColorMatrixEffect)` | 色の変換だけを行う映像エフェクト | いちばん速い |
| `RegisterPixelEffect(定義, IPixelEffect)` | ピクセルを直接書き換える映像エフェクト | 速い |
| `RegisterFilter(定義, IVideoEffect)` | 自由に描く映像エフェクト (大きさが変わる・Skia で描くなど) | 普通 |
| `RegisterMediaObject(定義, IVideoSource)` | 絵を作るメディアオブジェクト | `CacheKey` を返せば使い回し |
| `RegisterAudioEffect(定義, IAudioEffect)` | 音声エフェクト | ブロック単位でその場で処理 |
| `RegisterInterpolator(IInterpolator)` | トラックバーの移動方法 | – |
| `Log(文字列)` | ログに書く (標準エラーと `PluginLoader.LogMessages`) | – |

定義 (`EffectDefinition`) の `Category` は、映像エフェクトなら `Filter`、音声エフェクトなら `AudioFilter`、メディアオブジェクトなら `VideoSource`。
`definition.Id` と実装の `Id` は同じにします。
`Group` を付けると、「エフェクトの追加」メニューのその分類 (`EffectRegistry.FilterGroups` の「色」「ぼかし」など、または新しい名前) に入ります。付けなければ「プラグイン」に入ります。

## 速く動かすための選び方

できるだけ表の上のものを使ってください。エンジンが次のように扱います。

### IColorMatrixEffect (色の変換)

```csharp
float[]? GetMatrix(EffectArgs args);   // 4x5 のカラーマトリクス (行優先、平行移動は 0〜1)。何もしないときは null
```

- 続けて並んだ色の変換 (組み込みの「色調補正」「単色化」を含む) は、行列を掛け合わせて **1 回の処理** にまとめます。3 つ並べても画像の読み書きは 1 回だけ。
- 画面全体にかける (調整レイヤーとして置く) ときは、画面のピクセルを**複製せずにその場で**書き換えます。
- GPU 描画中は Skia のカラーフィルタとして GPU 上で処理します。
- 既定で `EffectFlags.Deterministic` (結果を使い回してよい)。行列が時刻や乱数で変わるなら `Flags` を `None` にします。

### IPixelEffect (ピクセルの書き換え)

```csharp
void Process(PixelBuffer pixels, EffectArgs args);
```

- `pixels` は BGRA・乗算済みアルファ。`Row(y)` / `RowPixels(y)` で 1 行を `Span` として読み書きします。
- `pixels.ParallelRows((y0, y1) => ...)` で、行を横帯に分けてエンジンのスレッドで並列に処理できます。
- 画面全体にかけるときは、画面のピクセルをその場で渡します (複製・描き戻しなし)。オブジェクトにかけるときは、使い回しのバッファに写してから渡します。
- 大きさは変えられません (変えたいなら `IVideoEffect`)。
- 既定で `EffectFlags.Deterministic`。

### IVideoEffect (自由な描画)

```csharp
Sprite Apply(Sprite input, EffectArgs args);
EffectFlags Flags => EffectFlags.None;   // 既定
```

- 新しく作った画像は `args.Context.Own()` に渡します (フレームの終わりにエンジンが捨てる)。作業用の描画先は `args.Context.CreateSurface` (GPU 描画中は GPU 上)。
- 何もしないときは `input` をそのまま返すと、画像の複製が起きません。
- 結果が入力とパラメータだけで決まるなら `Flags => EffectFlags.Deterministic` を返してください。オブジェクトの先頭から続く Deterministic なエフェクトの結果は、入力の絵とパラメータが前のフレームと同じ間は**計算し直さずに使い回します** (動かないテロップの縁取りなど)。

### IVideoSource.CacheKey (メディアオブジェクトの使い回し)

```csharp
SKImage? Render(EffectArgs args);
string? CacheKey(EffectArgs args) => null;   // 既定: 毎フレーム Render
```

絵がパラメータだけで決まるなら、パラメータから作った文字列を返します。同じ鍵の間は `Render` を呼ばずに前の絵を使います。
使い回すのは、`Render` が返した画像を `args.Context.Own()` に渡していた場合だけです (エンジンが引き取ります)。

### IAudioEffect (音声)

```csharp
void Process(AudioBlock block, ref object? state);
```

- `block.Samples` はステレオ・float (左右が交互)。その場で書き換えます。音量・パンはこのあとにかかります。
- `state` は、オブジェクトとエフェクトごとの入れ物です (最初は null)。残響などの続きの値を入れます。
- `block.Continuous` が false (再生開始・シークの直後) なら、前の続きではないので `state` をやり直します。
- 音声のスレッドから呼ばれるので、毎回メモリを確保したり、時間のかかる処理をしないでください。例外はエンジンが握りつぶし、その回は処理しなかったことになります。

## EffectArgs

| メンバー | 内容 |
|---|---|
| `Track(key)` / `Check(key)` / `Color(key)` | パラメータの今のフレームでの値 |
| `Frame` / `Time` | 今のフレーム (シーンの中の位置) / オブジェクトの先頭からの秒 |
| `IsDraft` | プレビューの軽い描画中 (再生中など)。品質を落として速くしてよい |
| `IsFrameBuffer` | 画面全体にかけている (調整レイヤー) |
| `Context` | 描画先の作成・画像の持ち主の登録・プロジェクト |
| `Context.LoadImage(パス)` / `Context.LoadVideoFrame(キー, パス, 秒, ループ)` | 別のファイルの絵を読む (キャッシュ付き。返った画像は破棄しない)。エンジンの外で作った `RenderContext` では null |

## スレッド

映像のエフェクトは描画のスレッドから呼ばれます。プレビューと出力が同時に動くこともあるので、実装のクラスにフレームごとの値を持たせないでください (状態が必要なら音声エフェクトの `state` のように、呼び出しごとの値を使う)。
