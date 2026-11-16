# video-builder

Avalonia UI / C# 製の、AviUtl ライクな動画編集アプリ (Linux 優先・マルチプラットフォーム)。

- 設計書: [docs/DESIGN.md](docs/DESIGN.md) (実装状況は 22 章)

## 必要なもの

- .NET 10 SDK
- FFmpeg 6.1 の共有ライブラリ (`libavcodec.so.60` など)。Ubuntu 24.04 なら `sudo apt install ffmpeg`
- OpenAL Soft (`libopenal1`)。音声の再生に使う
- (任意) EGL / OpenGL ES (`libegl1`)。GPU 描画に使う。なければ CPU で描画する

FFmpeg を標準以外の場所に置いている場合は、環境変数 `VIDEO_BUILDER_FFMPEG` にライブラリのあるフォルダを指定する。

## ビルドと実行

```sh
dotnet build
dotnet run --project src/VideoBuilder.App
```

起動オプション:

| オプション | 内容 |
|---|---|
| `<file>.vbproj` | プロジェクトを開いて起動する |
| `--demo` | サンプルのプロジェクトを読み込んで起動する |
| `--docked` / `--separate` | ウィンドウを 1 つにまとめる / AviUtl のように分ける |
| `--theme light\|dark\|system` | テーマを指定する |
| `--gpu` / `--cpu` | 描画方式を指定する (既定は自動) |
| `--lang ja\|en` | 表示言語を指定する (既定は OS の言語) |
| `--screenshot <png>` | ウィンドウを画像に保存して終了する (見た目の確認用) |

## テスト

```sh
dotnet test
```

動画・音声のテストは `ffmpeg` コマンドで素材を作る。FFmpeg がない環境では該当テストは何も検証せずに成功するので、
CI では `VB_REQUIRE_FFMPEG=1` を付けて、FFmpeg がない場合に失敗させる。

## 基本操作

| 操作 | 内容 |
|---|---|
| タイムラインを右クリック | オブジェクトの追加・貼り付け |
| ファイルをタイムラインへドロップ | 動画・画像・音声を配置 |
| オブジェクトをドラッグ / 端をドラッグ | 移動 / 長さ変更 (Alt でスナップ無効) |
| `Space` | 再生 / 停止 |
| `←` `→` (`Shift` で 10 フレーム) | フレーム移動 |
| `P` / `S` | 中間点を追加 / 現在位置で分割 |
| `Ctrl+Z` / `Ctrl+Y` | 元に戻す / やり直し |
| `Ctrl+ホイール` | タイムラインの拡大縮小 |
| 設定ダイアログのパラメータ名をクリック | 移動方法 (直線移動など) の選択 |

## アイコン

[Material Symbols](https://fonts.google.com/icons) の Sharp スタイル (Apache License 2.0) を使用。
`tools/IconGen/icons.txt` に書いたアイコンだけを `tools/IconGen/generate.py` で `Icons.axaml` に変換している。
