# プラグイン API (バージョン 3)

video-builder のプラグインは .NET のクラスライブラリです。見本は `samples/SamplePlugin`。

- 参照するもの: `src/VideoBuilder.Plugin.Abstractions` (名前空間 `VideoBuilder.Plugins`) と SkiaSharp。
  どちらもアプリ本体のものを使うので、出力フォルダにコピーしない (見本の `.csproj` を参照)。
- 置き場所: `$XDG_DATA_HOME/video-builder/plugins/<名前>/` に `plugin.json` と DLL。
- `plugin.json`:

  ```json
  { "id": "my.plugin", "name": "わたしのプラグイン", "version": "1.0.0", "apiVersion": 3, "assembly": "MyPlugin.dll" }
  ```

  `apiVersion` は `PluginApi.MinimumVersion` (1) 〜 `PluginApi.Version` (3) なら読み込みます。バージョン 1・2 で作ったプラグインは作り直さずにそのまま動きます。
  メニューのコマンド (`RegisterCommand`) を使うときは 3 にします。古いアプリでも動かしたいなら、`host.ApiVersion >= 3` のときだけ登録します。

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
| `RegisterCommand(IPluginCommand)` | 「プラグイン」メニューのコマンド (バージョン 3 から。[下記](#メニューのコマンド-バージョン-3)) | – |
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

## メニューのコマンド (バージョン 3)

`RegisterCommand` で登録したコマンドは、メニューバーの「プラグイン」に並びます (1 つもなければメニューは出ません)。
外のアプリと通信して素材を作り、タイムラインに置く、といったプラグインに使います。見本は `samples/SamplePlugin` の `InsertCaption`。

```csharp
public sealed class InsertCaption : IPluginCommand
{
    public string Id => "my.plugin.insertCaption";     // プラグインの ID を頭に付ける
    public string Name => "再生位置にテキストを置く...";
    public string? Group => null;                       // 同じ Group はサブメニューにまとまる
    public bool CanRun(IPluginCommandContext c) => true; // メニューを開くたびに呼ばれる

    public async Task RunAsync(IPluginCommandContext context)
    {
        var form = new PluginForm { Title = "テキストを置く", Fields = { new PluginTextField { Key = "text", Label = "文字", Required = true } } };
        if (!await context.ShowFormAsync(form)) return;
        context.Edit("テキストを置く", e => e.AddText(form.Get<PluginTextField>("text").Value, context.TargetLayer, context.CurrentFrame, 90));
    }
}
```

- `RunAsync` は UI のスレッドで呼ばれます。通信などの待ちは `await` してください (画面は止まりません)。重い計算は `Task.Run` に出します。
- 例外を投げると、エンジンが内容をメッセージで表示し、ログに残します。`OperationCanceledException` は何も表示しません。

### IPluginCommandContext

| メンバー | 内容 |
|---|---|
| `Project` / `Scene` / `SelectedObjects` | 今のプロジェクト・シーン・選んでいるオブジェクト (選んだ順)。**読むだけ**にする |
| `CurrentFrame` / `TargetLayer` | 再生位置 / 新しく置くときの目安のレイヤー (選んでいるオブジェクトのレイヤー、なければ 0) |
| `ProjectDirectory` | プロジェクトのファイルがあるフォルダ (保存前は null)。作った素材はここに置くと、プロジェクトと一緒に持ち運べる |
| `DataDirectory` | このプラグイン専用の設定などの置き場所 (`$XDG_DATA_HOME/video-builder/plugin-data/<ID>/`、作成済み) |
| `MediaDuration(パス)` | 音声・動画ファイルの長さ (秒)。読めなければ null |
| `Edit(説明, e => ...)` | プロジェクトを書き換える。中の変更は 1 回で取り消せ、例外が出たらすべて元に戻る |
| `ShowFormAsync(PluginForm)` | 入力フォーム。OK なら true を返し、入力を各項目の `Value` に書き戻す |
| `ShowMessageAsync(題, 文)` | メッセージ |
| `RunWithProgressAsync(題, (progress, ct) => ...)` | 進み具合のダイアログを出しながら実行する。中止を押すと `ct` が取り消される |

### IPluginEditor (Edit の中で使う)

| メンバー | 内容 |
|---|---|
| `AddAudioFile(パス, レイヤー, フレーム, 長さ?)` | 音声ファイルを置く。長さを省くとファイルの長さ (端数は切り上げ) |
| `AddText(文字, レイヤー, フレーム, 長さ, style?)` | 字幕向けのテキストを置く。`style` (テキストオブジェクト) があれば、その見た目をひな形にする |
| `AddObject(定義の ID, レイヤー, フレーム, 長さ, init?)` | 組み込みやプラグインのオブジェクトを置く |
| `FindFreeLayer(レイヤー, 開始, 終了)` | そこから下で、範囲がすべて空いている最初のレイヤー (なければ -1) |
| `Group(オブジェクト)` / `Select(オブジェクト)` | グループにする / Edit のあとで選ぶ |
| `Scene` | 直接書き換えてもよい (Edit の中なら取り消せる) |

置くものはどれも、指定したレイヤーから下で**範囲がすべて空いている**最初のレイヤーに、長さを削らずに置きます。置けなければ null を返します。

### フォーム (PluginForm)

| 項目 | 入力 |
|---|---|
| `PluginTextField` | 文字。`Multiline` で複数行、`Required` で空のままでは OK できない |
| `PluginNumberField` | 数値。`Minimum` / `Maximum` / `Decimals` |
| `PluginChoiceField` | 一覧から 1 つ。`Options` は `PluginChoice(値, 表示名)`、`Value` は選んだものの値 |
| `PluginCheckField` | チェックボックス |

フォームの画面は本体が作るので、プラグインは UI のライブラリに依存しません。文字はプラグインが付けたまま表示します (英語表示でも訳しません)。

### オブジェクトに情報を残す (TimelineObject.PluginData)

`TimelineObject.PluginData` (文字列の辞書) に書いたものは、プロジェクトと一緒に保存され、複製・取り消しにも含まれます。
「作ったときのセリフや話者を残し、あとで作り直す」といった使い方ができます。キーは `"プラグインの ID.名前"` にしてください。

