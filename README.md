# video-builder

**レイヤーとキーフレームで組み立てる、Linux・Windows・macOS 対応の動画編集アプリ**

細かく作り込む「詳細編集」と、動画・写真を並べるだけの「シンプル編集」の 2 つの編集モードを持ち、同じプロジェクトをどちらでも開けます。
詳細編集は AviUtl に近い操作感で、シンプル編集はムービーメーカーのような手軽さで使えるようにしています。

*A layer-based video editor for Linux, Windows and macOS, with keyframes, effects, reusable templates, Lua scripting and plugins. The UI is available in Japanese and English.*

- 対応 OS: Linux (Ubuntu 22.04+ / Fedora 39+ / Arch。x64・arm64) ・ Windows 10+ ・ macOS 13+
- 表示言語: 日本語 / English
- ライセンス: [LGPL-3.0-or-later](#ライセンス)

## 目次

- [特長](#特長)
- [入手方法](#入手方法)
- [使い方](#使い方)
- [快適に動かすために](#快適に動かすために)
- [開発者向け](#開発者向け)
- [ライセンス](#ライセンス)

## 特長

### 1 つのプロジェクトを、2 つの編集モードで

| モード | こんな人に |
|---|---|
| **詳細編集** | レイヤー・オブジェクト・キーフレームで細かく作り込みたい |
| **シンプル編集** | 動画・写真・タイトルを並べ、切り替え効果・キャプション・音楽を付けるだけでよい |

シンプル編集で並べたものを、詳細編集で仕上げることもできます。モードはいつでも切り替えられます。

### video-builder ならではの機能

- **テンプレート**: テロップの帯や名前表示、アイキャッチなどを 1 つのファイル (`.vbtemplate`) にまとめて、何度でも使い回せます。
  - 作る人が「変えてよい項目」(文字や色など) を選んで公開するので、使う人はその項目だけを触れば済みます。
  - 素材も同梱されるので、別の PC にそのまま持っていけます。
  - 長さを変えても、入りと抜けの動きは崩れません。
  - 解像度の違うプロジェクト (縦長動画など) に入れると、大きさと位置を自動で合わせます。
  - ライブラリにマウスを乗せると動きをプレビューできます。チームの共有フォルダにも置けます。
  - テンプレートを直したら、置いてあるものをまとめて新しい版に更新できます。
- **ベクター素材**: SVG と Lottie (`.json` / `.lottie`) のアニメーションをそのまま読み込めます。色の置き換えや一部の非表示もできます。
- **アニメーション WebP・GIF**: 透明な部分を保ったまま、動画として読み込めます。
- **字幕**: SRT / WebVTT の字幕を読み込んでテキストに変換したり、テキストを字幕ファイルに書き出したりできます。
- **音声**: BGM に付けると、ナレーションが鳴っている間だけ自動で音量を下げるダッキング機能と、3 バンドのイコライザが使えます。
- **3D カメラ**: カメラからの奥行きに合わせて前後を並べ替え、ピントから外れたものをぼかす (深度ぼけ) こともできます。
- **文字のアニメーション**: タイプライター・1 文字ずつフェードイン・下から出す・拡大して出す、から選べます。
- **マスクとトランジション**: 図形で切り抜くマスクに加え、ワイプ・スライド・ズーム・ぼかし・時計回りワイプ・ブラインドなど 18 種類の切り替え効果と、右クリックでのクロスフェードが使えます。
- **アニメーション効果**: 振動・点滅・回転・鼓動・揺れ・浮遊・弾むなどの動きを、キーフレームを打たずに付けられます。
- **自動保存と復元**: 10 世代まで自動で保存し、もしものときも元に戻せます。
- **拡張できる**: Lua スクリプトや、C# のプラグイン ([docs/PLUGIN_API.md](docs/PLUGIN_API.md)) で、エフェクトなどを追加できます。

### 基本の機能

- レイヤー 100 層のタイムライン、キーフレームと移動方法 (直線・曲線・加減速など 9 種類) によるアニメーション
- 動画・画像・音声・テキスト・図形・シーンのオブジェクト
- 図形は矢印・吹き出し・ハート・扇形・直線など 21 種類。角の丸み・グラデーション・縁の線・破線/点線などを設定でき、中間点で動かせる
- 色調補正・ぼかし・縁取り・影・クロマキー・グロー・方向ブラー・放射ブラー・シャープ・色収差・ラスター・ノイズ・周辺減光・グラデーション・斜めクリッピング・レベル補正・ポスタリゼーションなどのエフェクト、調整レイヤー、合成モード 13 種類
- グループ制御、プリセット、BPM グリッドへのスナップ、音声波形とサムネイルの表示
- 見つからない素材の付け替え・素材の収集、ショートカットのカスタマイズ
- ライト / ダークテーマ (既定は OS のテーマとアクセントカラーに合わせる)
- 出力: H.264 / H.265 / VP9 / AV1 / ProRes / 連番 PNG / WAV / FLAC

## 入手方法

> [!NOTE]
> まだ正式なリリースはありません。今は次のどちらかの方法で試せます。

### 配布物をダウンロードする (Linux・Windows・macOS)

GitHub Actions の [Package ワークフロー](https://github.com/Kimu1109/video-builder/actions/workflows/package.yml) の実行結果 (Artifacts) から、FFmpeg を同梱した Linux (x64・arm64 の AppImage・Flatpak・tar.gz)・Windows・macOS 向けの配布物をダウンロードできます (GitHub へのログインが必要です)。

### ソースからビルドする (Linux など)

必要なもの:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- FFmpeg 9 の共有ライブラリ (作り方は[下記](#ffmpeg-9-を用意する))
- OpenAL Soft (`libopenal1`)。音声の再生に使います
- (任意) EGL / OpenGL ES (`libegl1`)。GPU 描画に使います。なければ CPU で描画します

```sh
git clone https://github.com/Kimu1109/video-builder.git
cd video-builder
dotnet run --project src/VideoBuilder.App -- --demo   # サンプルのプロジェクトを開いて起動
```

#### FFmpeg 9 を用意する

Ubuntu 24.04 の標準の FFmpeg は 6.1 なので使えません。配布物と同じ設定 (x264・x265 入り) のものを、ffmpeg.org のソースから次のように作れます (数分かかります)。

```sh
sudo apt install build-essential nasm pkg-config libx264-dev libx265-dev libvpx-dev libdav1d-dev libopus-dev libffmpeg-nvenc-dev libva-dev libdrm-dev
packaging/ffmpeg/build.sh --prefix ~/.local/ffmpeg9
export VIDEO_BUILDER_FFMPEG=~/.local/ffmpeg9/lib LD_LIBRARY_PATH=~/.local/ffmpeg9/lib
```

FFmpeg を標準以外の場所に置いている場合は、環境変数 `VIDEO_BUILDER_FFMPEG` にライブラリのあるフォルダを指定します。

## 使い方

### 起動オプション

| オプション | 内容 |
|---|---|
| `<file>.vbproj` | プロジェクトを開いて起動する |
| `--demo` | サンプルのプロジェクトを読み込んで起動する |
| `--docked` / `--separate` | ウィンドウを 1 つにまとめる (既定) / プレビュー・タイムライン・プロパティを別々のウィンドウにする |
| `--theme light\|dark\|system` | テーマを指定する |
| `--gpu` / `--cpu` | 描画方式を指定する (既定は自動) |
| `--lang ja\|en` | 表示言語を指定する (既定は OS の言語) |
| `--screenshot <png>` | ウィンドウを画像に保存して終了する (見た目の確認用) |

### 編集モードの切り替え

環境設定、または「表示」→「編集モード」で切り替えます。

### 詳細編集の基本操作

| 操作 | 内容 |
|---|---|
| タイムラインを右クリック | オブジェクトの追加・貼り付け |
| ファイルをタイムラインへドロップ | 動画・画像・音声を、落とした位置 (レイヤー・時刻) に配置 (Linux は X11 / XWayland) |
| オブジェクトをドラッグ / 端をドラッグ | 移動 / 長さ変更 (Alt でスナップ無効) |
| `Space` | 再生 / 停止 |
| `←` `→` (`Shift` で 10 フレーム) | フレーム移動 |
| `P` / `S` | キーフレームを追加 / 現在位置で分割 |
| `[` / `]` | 出力範囲の開始 / 終了を現在位置に |
| `Ctrl+Z` / `Ctrl+Y` | 元に戻す / やり直し |
| `Ctrl+ホイール` | タイムラインの拡大縮小 |
| プロパティのパラメータ名をクリック | 移動方法 (直線移動など) の選択 |
| 何もない所をドラッグ | 範囲選択 (Ctrl で追加) |

### シンプル編集の基本操作

| 操作 | 内容 |
|---|---|
| ファイルをストーリーボードへドロップ / 「ビデオと写真」 | 動画・写真を追加 (音声ファイルは音楽として現在位置から) |
| クリップをクリック / ドラッグ | 選択と現在位置の移動 / 並べ替え |
| 音楽をドラッグ | 鳴らし始める位置を変える |
| `S` / `Delete` | 現在位置で分割 / 削除 |
| `[` / `]` | 選んでいるクリップの開始 / 終了を現在位置に |
| 右側の欄 | 長さ・速度 (0.25〜8 倍)・キャプション・切り替え効果・動き・見た目・回転・音量 |

## 快適に動かすために

重い動画でも軽快に編集できるよう、次の仕組みを備えています。どれも基本は自動で働きます。

- **GPU 描画**: GPU が使えれば GPU で、なければ自動で CPU で描画します (GPU 描画は Linux (ARM のボードの GPU を含む) と Windows。macOS は今のところ CPU で描画します)。
- **ハードウェアデコード**: NVDEC / VAAPI / VideoToolbox / V4L2 (Raspberry Pi 4 など ARM のボード) などを使います。使えなければ自動でソフトウェアに戻ります。
- **ハードウェアエンコード**: NVENC / Quick Sync / VAAPI / V4L2 などが使える環境では、出力ダイアログの形式に自動で出ます。
- **プロキシ編集**: 4K などの重い動画は、プレビュー用の小さな動画を自動で作って使います。

### ARM のボードで使う

Raspberry Pi などの ARM のボードで GPU やハードウェアデコードが使えないときは、`tools/arm-check.sh` を実行すると、GPU・V4L2 の装置の構成と、アプリで確かめるところが表示されます (何も変更しません)。Flatpak 版で V4L2 のデコードを使うには、`flatpak override --user --device=all io.github.kimu1109.VideoBuilder` が必要です。

### 使う GPU を選ぶ

内蔵 GPU と外付け GPU (NVIDIA など) の両方がある場合は、環境設定の「使う GPU」で描画に使う GPU を選べます (既定は自動で、外付けを優先。動画のデコードも同じ GPU に合わせます)。環境変数 `VIDEO_BUILDER_GPU` (GPU の名前の一部) でも指定できます。

### パフォーマンスチェック

「表示」→「パフォーマンスチェック」(`Ctrl+Shift+P`、ツールバー右の (i) ボタン) でオンにすると、プレビューの左上に描画時間・表示 FPS が出ます。詳細の窓では、プレビュー・再生・ベンチマーク・出力 (エンコード) の内訳とメモリを確認できます。

## 開発者向け

- 設計書: [docs/DESIGN.md](docs/DESIGN.md) (実装状況は 22 章)
- プラグイン API: [docs/PLUGIN_API.md](docs/PLUGIN_API.md) (見本は `samples/SamplePlugin`)

### ビルドとテスト

```sh
dotnet build
dotnet run --project src/VideoBuilder.App
dotnet test
```

動画・音声のテストは `ffmpeg` コマンドで素材を作ります。FFmpeg がない環境では該当テストは何も検証せずに成功するので、CI では `VB_REQUIRE_FFMPEG=1` を付けて、FFmpeg がない場合に失敗させています。

### 配布用のビルド

```sh
packaging/appimage/build.sh        # artifacts/appimage/ に出力 (この PC のアーキテクチャ向け)
```

- x64 と arm64 に対応しています (`build.sh x64` / `build.sh arm64`)。FFmpeg を同梱するため、作れるのは実行する PC と同じアーキテクチャのものだけです。
- `appimagetool` が PATH にあれば AppImage まで作り、なければ展開した状態 (AppDir) で止まります。
- FFmpeg 9 (x264・x265 入りの GPL ビルド) は `packaging/ffmpeg/build.sh` で作って同梱します。OpenAL は同梱しないので、利用者の環境に必要です。
- Flatpak は `packaging/flatpak/build.sh` で作ります (FFmpeg もマニフェストの中で作ります。`--arch x64|arm64` で指定でき、省くとこの PC のアーキテクチャ)。
- Linux (x64・arm64)・Windows・macOS 向けは、GitHub Actions の `Package` ワークフロー (`.github/workflows/package.yml`) が、それぞれの OS で FFmpeg を作って同梱したものを作ります (手動で実行するか、`v*` のタグを付けます)。

### 翻訳

画面の文言は日本語でソースに書き、英語は `tools/I18n/en.txt` の対応表で置き換えています。文言を足したら対応表に行を追加し、`python3 tools/I18n/generate.py` を実行してください。

### アイコン

[Material Symbols](https://fonts.google.com/icons) の Sharp スタイル (Apache License 2.0) を使用しています。`tools/IconGen/icons.txt` に書いたアイコンだけを `tools/IconGen/generate.py` で `Icons.axaml` に変換しています。

## ライセンス

video-builder は [LGPL-3.0-or-later](LICENSE) で配布しています (条文は [COPYING.LESSER](COPYING.LESSER) と [COPYING](COPYING))。
配布物 (AppImage・Flatpak など) は x264・x265 を有効にした FFmpeg (GPL) を同梱するため、全体を GPL-3.0-or-later の条件で配布します。
使っているソフトウェアとライセンスは [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。
