# video-builder 設計書

| 項目 | 内容 |
|---|---|
| 文書バージョン | 0.16 |
| 作成日 | 2026-11-16 |
| 対象 | video-builder 初期実装 〜 v1.0 |

---

## 目次

1. [概要](#1-概要)
2. [要件](#2-要件)
3. [技術スタック](#3-技術スタック)
4. [全体アーキテクチャ](#4-全体アーキテクチャ)
5. [ドメインモデル](#5-ドメインモデル)
6. [UI 設計](#6-ui-設計)
7. [デザインシステム](#7-デザインシステム)
8. [アイコン](#8-アイコン)
9. [操作仕様 (AviUtl 準拠)](#9-操作仕様-aviutl-準拠)
10. [レンダリングエンジン](#10-レンダリングエンジン)
11. [メディア入出力](#11-メディア入出力)
12. [プロジェクトファイル](#12-プロジェクトファイル)
13. [Undo / Redo](#13-undo--redo)
14. [プラグイン / 拡張](#14-プラグイン--拡張)
15. [設定・永続化](#15-設定永続化)
16. [マルチプラットフォーム対応 (Linux 優先)](#16-マルチプラットフォーム対応-linux-優先)
17. [パフォーマンス目標](#17-パフォーマンス目標)
18. [テスト方針](#18-テスト方針)
19. [ソリューション構成](#19-ソリューション構成)
20. [開発ロードマップ](#20-開発ロードマップ)
21. [決定事項](#21-決定事項)
22. [実装状況](#22-実装状況)

---

## 1. 概要

video-builder は C# と Avalonia UI で作るデスクトップ向け動画編集アプリケーションである。
Linux を主対象とし、Windows / macOS でも動かせるようにする。

操作感は **AviUtl (拡張編集)** に合わせる。具体的には次のとおり。

- レイヤーとオブジェクトを並べるタイムライン
- オブジェクトごとの「設定ダイアログ」とトラックバーで値を調整する
- 中間点で区切り、移動方法を選んでアニメーションさせる
- 右クリックメニューを中心にした操作

AviUtl ユーザーが迷わず使えることを目指しつつ、操作を Linux 環境に合わせて整理する。

### 1.1 設計方針

| 方針 | 内容 |
|---|---|
| AviUtl ライク | 用語・画面構成・ショートカットはできる限り AviUtl 拡張編集に合わせる |
| シンプル & シャープ | 角丸を使わない (CornerRadius = 0)。装飾を減らし、情報密度の高いフラットな UI にする |
| アイコン併用 | ツールバー・メニュー・オブジェクト種別にアイコンを付け、テキストと並べて表示する |
| Linux ファースト | X11 / Wayland(XWayland) の両方で動作確認し、配布は AppImage / Flatpak を基本にする |
| 疎結合 | UI・ドメイン・レンダリング・メディア I/O を別アセンブリに分け、ヘッドレスでレンダリングできるようにする |

---

## 2. 要件

### 2.1 機能要件 (v1.0)

| ID | 機能 | 優先度 |
|---|---|---|
| F-01 | 新規プロジェクト作成 (解像度・FPS・サンプルレート指定) | 必須 |
| F-02 | メディアの読み込み (動画 / 画像 / 音声)。D&D 対応 | 必須 |
| F-03 | タイムライン (レイヤー 100 層・複数シーン) | 必須 |
| F-04 | オブジェクト: 動画 / 画像 / 音声 / テキスト / 図形 / 背景 | 必須 |
| F-05 | フィルタ効果: 色調補正 / ぼかし / 縁取り / 影 / クリッピング / モザイク / クロマキー | 必須 |
| F-06 | フィルタオブジェクト (下位レイヤーにまとめて効果をかける) | 必須 |
| F-07 | 中間点と移動方法 (直線移動・加減速・曲線移動・瞬間移動・ランダム移動 等) | 必須 |
| F-08 | グループ制御 / カメラ制御 (3D) | 推奨 |
| F-09 | シーンオブジェクト (別シーンをオブジェクトとして配置) | 推奨 |
| F-10 | プレビュー再生 (音声同期あり) | 必須 |
| F-11 | 出力 (FFmpeg 経由。H.264 / H.265 / VP9 / AV1 / ProRes / 連番 PNG) | 必須 |
| F-12 | Undo / Redo | 必須 |
| F-13 | プロジェクト保存 / 読み込み / 自動保存 | 必須 |
| F-14 | テンプレート (テキストなどのスタイル、複数オブジェクトの組み合わせ、アイキャッチ。素材の同梱と編集項目の公開) ([22.19](#2219-テンプレート)) | 推奨 |
| F-15 | スクリプト (Lua 互換レイヤー or C# スクリプト) | 将来 |
| F-16 | プラグイン (入力・出力・フィルタ) | 将来 |
| F-17 | ライト / ダークテーマ切替 (既定はシステムのテーマ・アクセントカラーに従う) | 必須 |
| F-18 | ベクター画像 (SVG) とベクターアニメーション (Lottie) のオブジェクト ([22.19.8](#22198-ベクター画像-svg--lottie)) | 推奨 |

### 2.2 非機能要件

| 項目 | 目標 |
|---|---|
| 対応 OS | Linux (Ubuntu 22.04+, Fedora 39+, Arch) / Windows 10+ / macOS 13+ |
| 起動時間 | 2 秒以内 (SSD, 一般的な PC) |
| プレビュー | 1080p30 の単純な構成でリアルタイム再生 |
| メモリ | 空プロジェクトで 300MB 以下 |
| 安定性 | クラッシュ時に自動保存から復旧できる |
| 国際化 | 日本語 / 英語 (リソース切替) |

---

## 3. 技術スタック

| 領域 | 採用技術 | 理由 |
|---|---|---|
| 言語 / ランタイム | C# 14 / .NET 10 (LTS) | 指定どおり。長期サポート版を使う |
| UI | Avalonia UI 11.x | クロスプラットフォームの XAML UI。Linux で安定して動く |
| MVVM | CommunityToolkit.Mvvm (画面が増えた時点で導入。[22.2](#222-設計書との差分)) | Source Generator によりボイラープレートが少ない |
| DI | Microsoft.Extensions.DependencyInjection | 標準的で軽量 |
| 2D 描画 | SkiaSharp (Avalonia が内部で使うものと共通) | GPU (OpenGL / Vulkan) 対応。テキスト描画も強い |
| GPU エフェクト | SkiaSharp `SKRuntimeEffect` (SkSL シェーダ) | 追加依存なしでフィルタを GPU 実行できる |
| 動画デコード / エンコード | FFmpeg + `FFmpeg.AutoGen` (現在は 9.0。[22.31](#2231-ffmpeg-9-への移行)) | 対応コーデックが最も多い。ハードウェアデコードも使える |
| 音声出力 | Silk.NET.OpenAL (OpenAL Soft) | Linux (PulseAudio / PipeWire / ALSA) を一通りカバーできる |
| アイコン | Material Symbols **Sharp** (SVG → `StreamGeometry`) | 角ばったデザインに合う。Apache 2.0 |
| シリアライズ | System.Text.Json (Source Generator) | AOT 対応・高速 |
| ログ | Serilog | ファイル・コンソール出力 |
| テスト | xUnit + Avalonia.Headless + Verify | UI 込みのヘッドレステストができる |

---

## 4. 全体アーキテクチャ

### 4.1 レイヤー構成

```
┌──────────────────────────────────────────────────────────┐
│ VideoBuilder.App  (Avalonia: View / ViewModel / Controls)│
├──────────────────────────────────────────────────────────┤
│ VideoBuilder.Editing  (編集セッション / Command / Undo)   │
├───────────────────────────┬──────────────────────────────┤
│ VideoBuilder.Core         │ VideoBuilder.Rendering       │
│ (ドメインモデル・補間・時間) │ (合成パイプライン・エフェクト)   │
├───────────────────────────┴──────────────────────────────┤
│ VideoBuilder.Media  (FFmpeg デコード/エンコード, 音声)       │
├──────────────────────────────────────────────────────────┤
│ VideoBuilder.Plugin.Abstractions  (拡張用インターフェース)    │
└──────────────────────────────────────────────────────────┘
```

- **Core** は UI・FFmpeg に依存しない純粋なモデルにする。単体テストの主な対象。
- **Rendering** は `Project` と時刻 `t` を受け取ってフレーム (`SKImage`) を返す純関数的な API にする。
- **Application** は UI 操作をコマンドに変換し、Undo スタックを管理する。
- **App** は表示と入力だけを担う。

### 4.2 主要なデータの流れ

```
ユーザー操作 ─▶ ViewModel ─▶ ICommand(EditCommand) ─▶ Project(Core) 更新
                                                     │
                                       ProjectChanged イベント
                                                     ▼
            PreviewController ─▶ RenderEngine.RenderFrame(t) ─▶ PreviewSurface
                    │
                    └─▶ AudioMixer.Mix(t..t+Δ) ─▶ OpenAL
```

### 4.3 スレッドモデル

| スレッド | 役割 |
|---|---|
| UI スレッド | Avalonia Dispatcher。モデル変更はここだけで行う |
| Render スレッド | プレビューフレームの合成 (1 本、GPU コンテキストを専有) |
| Decode ワーカー | ソースごとに先読みデコード (スレッドプール) |
| Audio スレッド | OpenAL バッファへの供給 |
| Export ワーカー | 出力時は別インスタンスの RenderEngine で処理する |

レンダリング時はモデルの **イミュータブルなスナップショット** (`ProjectSnapshot`) を渡し、UI スレッドの編集と衝突しないようにする。

---

## 5. ドメインモデル

### 5.1 クラス図 (概要)

```
Project
 ├─ ProjectSettings (Width, Height, FrameRate, SampleRate, Channels)
 ├─ MediaPool : List<MediaAsset>
 └─ Scenes : List<Scene>
      └─ Scene
          ├─ Id, Name, Width, Height, Length
          ├─ Exposed : List<ExposedParam>   ※テンプレートとして外から編集できる項目
          └─ Layers : Layer[100]
               └─ Layer (Index, Name, IsVisible, IsLocked, IsCoordinateLinked)
                    └─ Objects : SortedList<Frame, TimelineObject>

TimelineObject (abstract)
 ├─ Id, Layer, StartFrame, EndFrame
 ├─ Segments  : 中間点で区切られた区間 (KeyFrame 列)
 ├─ Effects   : List<Effect>   ※先頭がメイン効果 (例: 動画ファイル)
 ├─ GroupId?  : 連結オブジェクト (映像と音声のペア等)
 └─ 派生:
     VideoObject / ImageObject / AudioObject / TextObject / ShapeObject
     SceneObject / FilterObject / GroupControlObject / CameraControlObject
     FrameBufferObject / BackgroundObject

Effect (abstract)
 ├─ Definition : EffectDefinition (Id, 表示名, アイコン, パラメータ定義)
 ├─ IsEnabled
 └─ Parameters : Dictionary<string, AnimatedValue>

AnimatedValue
 ├─ Values[]  : 中間点ごとの値 (区間数 + 1)
 ├─ Mode      : MovementMode (Constant / Linear / Ease / Curve / Jump / Random / Repeat ...)
 └─ ModeArgs  : 移動方法ごとのパラメータ (加速・減速フラグ、移動フレーム間隔 等)
```

### 5.2 時間の扱い

- 内部の時刻は **フレーム番号 (long)** で持つ。AviUtl と同じ。
- 可変フレームレートの素材は、読み込み時に PTS からプロジェクトのフレームへ写像する。
- 音声はサンプル単位 (`long sample`) で管理し、`frame ↔ sample` は `Rational` (分数) で正確に変換する。

### 5.3 標準パラメータ (描画系共通)

AviUtl の「標準描画」に合わせる。

| パラメータ | 範囲 | 既定値 |
|---|---|---|
| X / Y / Z | -99999.9 〜 99999.9 | 0 |
| 拡大率 | 0 〜 5000 % | 100 |
| 透明度 | 0 〜 100 % | 0 |
| 回転 | -3600 〜 3600 ° | 0 |
| 合成モード | 通常 / 加算 / 減算 / 乗算 / スクリーン / オーバーレイ / 比較(明) / 比較(暗) / 輝度 / 色差 / 陰影 / 明暗 / 差分 | 通常 |

拡張描画 (3D) では 縦横比・X 軸回転・Y 軸回転・Z 軸回転・中心 X/Y/Z・裏面を表示しない を追加する。

---

## 6. UI 設計

### 6.1 ウィンドウ構成

AviUtl と同じく **複数ウィンドウ構成** を基本にする。ただしタイル型 WM やシングルモニタ環境向けに、**ドッキングモード** (1 ウィンドウに統合) も用意し、設定で切り替えられるようにする。

| ウィンドウ | AviUtl での対応 | 内容 |
|---|---|---|
| メインウィンドウ | 本体ウィンドウ | メニュー・プレビュー・再生コントロール・シークバー |
| タイムラインウィンドウ | 拡張編集 | レイヤー・オブジェクト・シーン切替・グリッド |
| 設定ダイアログ | 設定ダイアログ | 選択中オブジェクトのトラックバー・フィルタ一覧 |
| メディアプール | (なし。追加機能) | 読み込んだ素材の一覧。初期設定は非表示 |
| 出力ダイアログ | プラグイン出力 | 出力設定と進捗 |

### 6.2 ドッキングモードのレイアウト

```
┌───────────────────────────────────────────────────────────────────────────┐
│ [≡] ファイル  編集  表示  設定  出力  ヘルプ                              │ ← メニューバー (24px)
├───────────────────────────────────────────────────────────────────────────┤
│ [📄][📂][💾] │ [↶][↷] │ [✂][⧉][🗑] │ [⊞ grid][🧲 snap] │       1920x1080 30fps │ ← ツールバー (28px)
├──────────────────────────────────────────────┬────────────────────────────┤
│                                              │ 設定ダイアログ              │
│                                              │ ┌────────────────────────┐ │
│                                              │ │[▣] 動画ファイル [x][...]│ │
│              プレビュー                       │ ├────────────────────────┤ │
│              (16:9, 黒背景)                   │ │◀ 0.0 [====|===] 0.0  ▶ │ │
│                                              │ │   X     [直線移動 ▼]    │ │
│                                              │ │◀ 0.0 [====|===] 0.0  ▶ │ │
│                                              │ │   Y                     │ │
│                                              │ │◀100.0[==|=====]100.0 ▶ │ │
│                                              │ │   拡大率                 │ │
│                                              │ ├────────────────────────┤ │
│                                              │ │[✓] 色調補正          [▾]│ │
│                                              │ │[✓] ぼかし            [▾]│ │
│                                              │ └────────────────────────┘ │
├──────────────────────────────────────────────┴────────────────────────────┤
│ [⏮][◀][▶ ][▶|][⏭]  00:00:12.15 / 00:03:00.00   [━━━━━━●━━━━━━━━━━]  F 375 │ ← 再生バー (28px)
├───────────────────────────────────────────────────────────────────────────┤
│ [Root ▼] 00:00:00 ┃00:00:05   ┃00:00:10   ┃00:00:15   ┃00:00:20         │ ← 時間ルーラー
│───────────────────┼────────────────┃──────────────────────────────────────│
│ Layer 1  [👁][🔒] │ ███ テキスト ███┃                                     │
│ Layer 2  [👁][🔒] │      ████████ 動画ファイル █████◆████████              │ ◆=中間点
│ Layer 3  [👁][🔒] │      ████████ 音声ファイル ███████████████              │
│ Layer 4  [👁][🔒] │                   ▓▓▓ ぼかし(フィルタ) ▓▓▓              │
│ Layer 5  [👁][🔒] │                  ┃                                     │
│        ...        │                  ┃ ← 現在位置カーソル (赤 1px)          │
└───────────────────────────────────────────────────────────────────────────┘
```

### 6.3 タイムラインウィンドウ

| 要素 | 仕様 |
|---|---|
| レイヤーヘッダ | 幅 96px。「Layer N」+ 表示切替 (eye) / ロック (lock) / 座標リンク (link) アイコン。クリックで表示切替 (AviUtl 同様) |
| オブジェクト | 高さ = レイヤー高さ (既定 24px、大/中/小の 3 段階)。色はオブジェクト種別ごと。角丸なし・1px の濃い枠線 |
| 中間点 | オブジェクト内に縦線と小さな ◆ (6px、角ばったひし形) で表示 |
| 選択状態 | 枠線をアクセントカラー 2px にする。複数選択時も同じ |
| 時間ルーラー | 上端に表示。ズームに応じて目盛を 1f / 10f / 1s / 10s / 1min に切り替える |
| グリッド | BPM グリッド / 時間グリッドを設定で切り替え |
| シーン切替 | 左上のドロップダウン (Root, Scene1, ...) |
| ズーム | Ctrl + ホイール、またはタイムライン右上のスライダ (AviUtl の拡大率バーに相当) |
| スクロール | ホイール = 縦、Shift + ホイール = 横 |

#### オブジェクト配色 (種別ごと)

角ばったフラットな色帯にする。文字は白、上端に 1px のハイライト線を引く。

| 種別 | 色 (Hex) | アイコン |
|---|---|---|
| 動画ファイル | `#3A6EA5` | `movie` |
| 画像ファイル | `#3A8F6E` | `image` |
| 音声ファイル | `#A5503A` | `music_note` |
| テキスト | `#6E5AA5` | `title` |
| 図形 | `#5A7F3A` | `category` |
| フィルタ効果 / フィルタオブジェクト | `#7A7A7A` | `filter_alt` |
| シーン | `#A58A3A` | `movie_edit` |
| グループ制御 | `#4F6070` | `select_all` |
| カメラ制御 | `#704F60` | `videocam` |

### 6.4 設定ダイアログ

AviUtl の設定ダイアログをもとにする。

```
┌ 設定ダイアログ ───────────────────────────── [−][×] ┐
│ [▣] 動画ファイル [00:00:01.00 - 00:00:05.00]  [+][⋯] │ ← ヘッダ: 種別アイコン/名前/区間/効果追加/メニュー
├──────────────────────────────────────────────────────┤
│ ▼ 動画ファイル                              [✓][⋯]   │
│   [◀] [   0.00 ] ━━━━━━━━●━━━━━ [   0.00 ] [▶]  X    │
│   [◀] [   0.00 ] ━━━━━━━━●━━━━━ [   0.00 ] [▶]  Y    │
│   [◀] [ 100.00 ] ━━━━●━━━━━━━━━ [ 100.00 ] [▶]  拡大率│
│   [◀] [   0.0  ] ●━━━━━━━━━━━━━ [   0.0  ] [▶]  透明度│
│   合成モード [通常           ▼]                       │
│   ファイル   [ /home/user/clip.mp4        ] [📂]     │
│   [ ] ループ再生   [✓] 音声も読み込む                  │
├──────────────────────────────────────────────────────┤
│ ▶ 色調補正                                 [✓][⋯]    │ ← 折りたたみ可能
│ ▶ ぼかし                                   [✓][⋯]    │
└──────────────────────────────────────────────────────┘
```

#### トラックバー仕様

| 操作 | 動作 |
|---|---|
| 左の値 | 区間の開始値。右の値は終了値 |
| 移動方法ボタン (パラメータ名部分) | クリックで移動方法メニュー (直線移動 / 曲線移動 / 加減速 / 瞬間移動 / ランダム移動 / 反復移動 / 補間移動 …) |
| 移動方法なし | 右の値はグレーアウト (固定値) |
| `◀` / `▶` | ステップ単位で増減。Shift で 10 倍、Ctrl で 1/10 |
| 数値ボックス | 直接入力。式 (`100*2`) も受け付ける |
| スライダドラッグ | Shift で微調整 |
| ダブルクリック | 既定値に戻す |
| プレビュー上ドラッグ | X/Y を直接編集 (Ctrl で拡大率、Alt で回転) |

### 6.5 メインウィンドウ (プレビュー)

- プレビューは `PreviewSurface` (OpenGL コンテキストを持つ Avalonia `Control`) に描画する。
- 表示倍率: 25 / 50 / 100 / 200 % / ウィンドウに合わせる。
- プレビュー品質: フル / 1/2 / 1/4 (プロキシ解像度)。
- 選択オブジェクトに **バウンディングボックス (角ばった 1px 枠 + 四隅の 6px 正方形ハンドル)** を表示する。
- 中心線・セーフエリア・グリッドの表示を切り替えられる。

### 6.6 右クリックメニュー (タイムライン空白部)

```
┌───────────────────────────────┐
│ [▣] メディアオブジェクトの追加 ▶│─┬ [movie] 動画ファイル
│ [⚙] フィルタオブジェクトの追加 ▶│ ├ [image] 画像ファイル
│ ───────────────────────────── │ ├ [music] 音声ファイル
│ [📄] 貼り付け        Ctrl+V   │ ├ [title] テキスト
│ [⊟] 範囲設定                 ▶│ ├ [shape] 図形
│ [⊞] グリッド                 ▶│ ├ [scene] シーン
│ ───────────────────────────── │ ├ ──────────────
│ [⚙] 環境設定                  │ ├ [group] グループ制御
└───────────────────────────────┘ └ [cam]   カメラ制御
```

オブジェクトを右クリックしたときのメニュー: 中間点を追加 / 中間点を削除 / 分割 / 切り取り / コピー / 削除 / オブジェクトの有効化・無効化 / 上のオブジェクトでクリッピング / カメラ制御の対象 / エイリアスを作成 / 長さを変更 / 設定ダイアログを表示。

---

## 7. デザインシステム

### 7.1 基本原則

1. **角ばったデザイン**: すべてのコントロールで `CornerRadius="0"`。丸いのはラジオボタンの中身だけにする (チェックボックスも角型)。
2. **フラット**: グラデーション・影は原則使わない。ポップアップやメニューの境界は 1px の枠線で示す。
3. **高密度**: AviUtl のように一画面に多くの情報を載せる。標準の行高さは 22〜24px。
4. **ライト / ダーク切替**: ライトテーマとダークテーマを用意し、実行中に切り替えられる。既定は **OS のテーマとアクセントカラーに従う** ([7.3](#73-テーマ切替とシステムカラー))。

### 7.2 カラートークン (ダークテーマ)

| トークン | 値 | 用途 |
|---|---|---|
| `Bg.Base` | `#1E1E1E` | ウィンドウ背景 |
| `Bg.Panel` | `#252526` | パネル・ダイアログ |
| `Bg.Elevated` | `#2D2D30` | メニュー・ポップアップ |
| `Bg.Input` | `#1A1A1A` | テキストボックス・数値入力 |
| `Bg.Hover` | `#3A3A3D` | ホバー |
| `Bg.Pressed` | `#45454A` | 押下 |
| `Border.Default` | `#3F3F46` | 標準の枠線 |
| `Border.Strong` | `#5A5A60` | 強調枠線・区切り |
| `Fg.Primary` | `#E6E6E6` | 本文 |
| `Fg.Secondary` | `#A0A0A0` | 補助テキスト・ラベル |
| `Fg.Disabled` | `#606060` | 無効 |
| `Accent` | `#2F80ED` | 選択・フォーカス・スライダつまみ |
| `Accent.Hover` | `#4A93F0` | アクセントのホバー |
| `Playhead` | `#E53935` | 現在位置カーソル |
| `Warning` | `#F2A900` | 警告 |
| `Error` | `#D9433A` | エラー |

`Accent` / `Accent.Hover` はアプリ既定の値。システムのアクセントカラーを使う設定では、[7.3.3](#733-システムアクセントカラー) の手順で置き換える。

### 7.2.1 カラートークン (ライトテーマ)

トークン名はダークテーマと共通にし、値だけを差し替える。

| トークン | 値 | 用途 |
|---|---|---|
| `Bg.Base` | `#F3F3F3` | ウィンドウ背景 |
| `Bg.Panel` | `#FFFFFF` | パネル・ダイアログ |
| `Bg.Elevated` | `#FFFFFF` | メニュー・ポップアップ (枠線で区別) |
| `Bg.Input` | `#FFFFFF` | テキストボックス・数値入力 |
| `Bg.Hover` | `#E5E5E5` | ホバー |
| `Bg.Pressed` | `#D6D6D6` | 押下 |
| `Border.Default` | `#C8C8C8` | 標準の枠線 |
| `Border.Strong` | `#A0A0A0` | 強調枠線・区切り |
| `Fg.Primary` | `#1E1E1E` | 本文 |
| `Fg.Secondary` | `#5F5F5F` | 補助テキスト・ラベル |
| `Fg.Disabled` | `#A8A8A8` | 無効 |
| `Accent` | `#1F6FD9` | 選択・フォーカス・スライダつまみ |
| `Accent.Hover` | `#1A5FBA` | アクセントのホバー |
| `Playhead` | `#D32F2F` | 現在位置カーソル |
| `Warning` | `#B37A00` | 警告 |
| `Error` | `#C62828` | エラー |

### 7.3 テーマ切替とシステムカラー

#### 7.3.1 設定項目

「環境設定 > 外観」に次の 2 項目を置く。メニュー「表示 > テーマ」からも切り替えられる。

| 設定 | 選択肢 | 既定値 |
|---|---|---|
| テーマ | **システムに従う** / ライト / ダーク | システムに従う |
| アクセントカラー | **システムに従う** / アプリ既定 (青) / カスタム (カラーピッカー) | システムに従う |

- 切り替えは **即時反映** する (再起動不要)。開いているすべてのウィンドウ・ダイアログに反映する。
- 「システムに従う」を選んでいるときは、アプリ実行中に OS 側でテーマを変えた場合も追従する。

#### 7.3.2 システムテーマの取得

Avalonia の `IPlatformSettings` を使い、OS ごとの差を吸収する。

| OS | 取得元 |
|---|---|
| Linux | xdg-desktop-portal の `org.freedesktop.appearance` (`color-scheme`, `accent-color`)。GNOME / KDE Plasma / Flatpak 環境で動作する |
| Windows | レジストリ `AppsUseLightTheme` / `UISettings` のアクセントカラー (Avalonia が対応済み) |
| macOS | `NSAppearance` / `controlAccentColor` (Avalonia が対応済み) |

**フォールバック** (portal がない sway / i3 などの環境):

1. 環境変数 `GTK_THEME` の値が `:dark` で終わる、またはテーマ名に `dark` を含む → ダーク
2. それ以外で判定できない → **ダーク** (映像編集では暗い背景の方が見やすいため)
3. アクセントカラーが取得できない → アプリ既定の青

#### 7.3.3 システムアクセントカラー

OS から得た色をそのまま使うと、背景とのコントラストが足りないことがある。そのため次の手順で調整してから `Accent` 系トークンに入れる。

1. OS のアクセントカラーを OKLCH 色空間に変換する。
2. 現在のテーマの `Bg.Panel` に対して、コントラスト比 **3:1 以上** (WCAG の非テキスト要素の基準) になるまで明度 L を調整する (ダークでは明るく、ライトでは暗く)。
3. `Accent.Hover` は、ダークでは L を +0.06、ライトでは L を -0.06 した色にする。
4. アクセント上の文字色 (`Fg.OnAccent`) は、白と黒のうちコントラスト比が高い方を選ぶ。

#### 7.3.4 テーマに左右されない色

映像の見え方や識別性を保つため、次はテーマを切り替えても変えない。

| 対象 | 方針 |
|---|---|
| プレビュー内の映像 | テーマの影響を受けない。フレーム外 (余白) だけテーマの `Bg.Base` を使う |
| プレビューの透明部分 | 市松模様 (グレー 2 色) で表示。テーマに関係なく同じ |
| オブジェクト種別の色 ([6.3](#63-タイムラインウィンドウ)) | 色相は固定。ライトテーマでは枠線を `#00000040` にして背景から浮かせる |
| 現在位置カーソル `Playhead` | 赤系で固定 (明度のみテーマごとに調整) |
| アクセントカラー | オブジェクトの選択枠にだけ使う。オブジェクト本体の色には混ぜない |

#### 7.3.5 実装

- 色はすべて `DynamicResource` で参照する (`StaticResource` は禁止)。これにより実行中の切り替えが反映される。
- `ThemeService` が設定と OS の状態を監視し、`Application.RequestedThemeVariant` とアクセント系リソースを更新する。

```csharp
public enum ThemeMode { System, Light, Dark }
public enum AccentMode { System, AppDefault, Custom }

public sealed class ThemeService
{
    private readonly Application _app;
    private readonly IPlatformSettings? _platform;

    public ThemeService(Application app)
    {
        _app = app;
        _platform = app.PlatformSettings;
        if (_platform is not null)
            _platform.ColorValuesChanged += (_, _) => Apply();   // OS 側の変更に追従
    }

    public ThemeMode Mode { get; set; } = ThemeMode.System;
    public AccentMode Accent { get; set; } = AccentMode.System;
    public Color CustomAccent { get; set; } = Color.Parse("#2F80ED");

    public void Apply()
    {
        _app.RequestedThemeVariant = Mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark  => ThemeVariant.Dark,
            _               => ResolveSystemVariant(),          // portal → GTK_THEME → Dark
        };

        var isDark = _app.ActualThemeVariant == ThemeVariant.Dark;
        var baseAccent = Accent switch
        {
            AccentMode.Custom => CustomAccent,
            AccentMode.System when TryGetSystemAccent(out var c) => c,
            _ => isDark ? Color.Parse("#2F80ED") : Color.Parse("#1F6FD9"),
        };

        var palette = AccentPalette.Create(baseAccent, isDark);  // 7.3.3 のコントラスト調整
        _app.Resources["Accent"]       = new SolidColorBrush(palette.Accent);
        _app.Resources["Accent.Hover"] = new SolidColorBrush(palette.Hover);
        _app.Resources["Fg.OnAccent"]  = new SolidColorBrush(palette.OnAccent);
    }
    // ResolveSystemVariant / TryGetSystemAccent は IPlatformSettings.GetColorValues() を使い、
    // 取得できない場合は 7.3.2 のフォールバックに従う
}
```

### 7.4 タイポグラフィ

| 用途 | フォント | サイズ |
|---|---|---|
| UI 全般 | `Noto Sans CJK JP`, `Noto Sans JP`, `Inter` (フォールバック: システム既定) | 12px |
| 数値・タイムコード | `JetBrains Mono`, `Noto Sans Mono` | 12px (tabular) |
| ダイアログ見出し | UI フォント SemiBold | 13px |
| タイムライン内ラベル | UI フォント | 11px |

フォントはアプリに同梱し (OFL ライセンス)、Linux 環境ごとの差を減らす。

### 7.5 サイズ / 余白

| 項目 | 値 |
|---|---|
| 基本グリッド | 4px |
| コントロール高さ | 22px (小) / 24px (標準) |
| ボタン内余白 | 水平 8px / 垂直 2px |
| アイコンサイズ | 16px (標準) / 14px (タイムライン内) / 20px (ツールバー大) |
| 枠線 | 1px |
| フォーカスリング | 1px `Accent` (内側にオフセット 0) |
| CornerRadius | **0 (全コントロール)** |

### 7.6 Avalonia スタイル実装方針

- `FluentTheme` をベースにし、`ResourceDictionary` で `ControlCornerRadius` / `OverlayCornerRadius` を 0 に上書きする。
- 色は `ThemeVariant` (Dark / Light) ごとの `ResourceDictionary.ThemeDictionaries` で定義する。アクセント系だけは `ThemeService` が実行時に上書きする ([7.3.5](#735-実装))。
- 独自コントロール (`TrackBar`, `TimelineCanvas`, `LayerHeader`, `IconButton`) は `ControlTheme` で見た目を定義する。

```xml
<!-- Themes/Base.axaml (抜粋) -->
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <FluentTheme />

  <Styles.Resources>
    <ResourceDictionary>
      <CornerRadius x:Key="ControlCornerRadius">0</CornerRadius>
      <CornerRadius x:Key="OverlayCornerRadius">0</CornerRadius>
      <x:Double x:Key="ControlContentThemeFontSize">12</x:Double>

      <ResourceDictionary.ThemeDictionaries>
        <ResourceDictionary x:Key="Dark">
          <SolidColorBrush x:Key="Bg.Base"        Color="#1E1E1E"/>
          <SolidColorBrush x:Key="Bg.Panel"       Color="#252526"/>
          <SolidColorBrush x:Key="Border.Default" Color="#3F3F46"/>
          <SolidColorBrush x:Key="Fg.Primary"     Color="#E6E6E6"/>
          <SolidColorBrush x:Key="Accent"         Color="#2F80ED"/>
        </ResourceDictionary>
        <ResourceDictionary x:Key="Light">
          <SolidColorBrush x:Key="Bg.Base"        Color="#F3F3F3"/>
          <SolidColorBrush x:Key="Bg.Panel"       Color="#FFFFFF"/>
          <SolidColorBrush x:Key="Border.Default" Color="#C8C8C8"/>
          <SolidColorBrush x:Key="Fg.Primary"     Color="#1E1E1E"/>
          <SolidColorBrush x:Key="Accent"         Color="#1F6FD9"/>
        </ResourceDictionary>
      </ResourceDictionary.ThemeDictionaries>
    </ResourceDictionary>
  </Styles.Resources>

  <!-- ボタン: 角型・フラット -->
  <Style Selector="Button">
    <Setter Property="CornerRadius" Value="0"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="BorderBrush" Value="{DynamicResource Border.Default}"/>
    <Setter Property="Padding" Value="8,2"/>
    <Setter Property="MinHeight" Value="24"/>
  </Style>
</Styles>
```

---

## 8. アイコン

### 8.1 採用アイコンセット

**Material Symbols — Sharp スタイル** (Google, Apache License 2.0) を使う。

- 角が直角の "Sharp" バリエーションは、本アプリの角ばったデザインに合う。
- 線幅 (weight) は 300〜400、`FILL=0` (アウトライン) を基本にし、トグルの ON 状態だけ `FILL=1` にする。
- ビルド時に必要なアイコンだけ SVG から `StreamGeometry` に変換し、`Icons.axaml` に埋め込む (フォント全体は同梱しない)。

> 代替案: Lucide (ISC) は線が丸端 (round cap) のため今回は採用しない。

### 8.2 実装

```xml
<!-- Assets/Icons.axaml (自動生成) -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <StreamGeometry x:Key="Icon.Play">M320-200v-560l440 280-440 280Z</StreamGeometry>
  <StreamGeometry x:Key="Icon.Pause">M520-200v-560h240v560H520Zm-320 0v-560h240v560H200Z</StreamGeometry>
  <!-- ... -->
</ResourceDictionary>
```

```xml
<!-- 使用例: IconButton (独自コントロール) -->
<ctl:IconButton Icon="{StaticResource Icon.Play}"
                ToolTip.Tip="再生 (Space)"
                Command="{Binding PlayCommand}"/>
```

`IconButton` は `PathIcon` を内包する `Button` の派生で、次のプロパティを持つ。

| プロパティ | 型 | 説明 |
|---|---|---|
| `Icon` | `Geometry` | 表示するアイコン |
| `Text` | `string?` | 指定時はアイコンの右にラベルを表示 |
| `IconSize` | `double` | 既定 16 |
| `IsToggle` / `IsChecked` | `bool` | トグルボタンとして使う |

### 8.3 アイコン割り当て (主要)

| 機能 | Material Symbols 名 |
|---|---|
| 新規 / 開く / 保存 | `note_add` / `folder_open` / `save` |
| 元に戻す / やり直し | `undo` / `redo` |
| 切り取り / コピー / 貼り付け / 削除 | `content_cut` / `content_copy` / `content_paste` / `delete` |
| 再生 / 一時停止 / 停止 | `play_arrow` / `pause` / `stop` |
| 先頭 / 末尾 / 前フレーム / 次フレーム | `first_page` / `last_page` / `chevron_left` / `chevron_right` |
| 分割 | `splitscreen_vertical_add` (または `content_cut`) |
| 中間点追加 | `add_diamond` |
| スナップ / グリッド | `straighten` / `grid_on` |
| 表示 / 非表示 | `visibility` / `visibility_off` |
| ロック | `lock` / `lock_open` |
| 座標リンク | `link` |
| 出力 | `output` (または `movie`) |
| 設定 | `settings` |
| エフェクト追加 | `add_box` |

---

## 9. 操作仕様 (AviUtl 準拠)

### 9.1 タイムライン操作

| 操作 | 動作 |
|---|---|
| 空白部ダブルクリック / 右クリック | オブジェクト追加メニュー |
| ファイルを D&D | ドロップ位置のレイヤー・時刻にオブジェクトを作成 (動画は音声と連結) |
| オブジェクトをドラッグ | 移動 (レイヤー間の移動もできる) |
| オブジェクトの端をドラッグ | 長さ変更 |
| Ctrl + ドラッグ (空白部) | 範囲選択 |
| Ctrl + クリック | 複数選択の追加・解除 |
| Alt + ドラッグ | スナップ一時無効 |
| オブジェクトをダブルクリック | 設定ダイアログを前面に出す |
| 中間点をドラッグ | 中間点の位置を移動 |
| ルーラーをクリック / ドラッグ | 現在位置のシーク |
| レイヤーヘッダクリック | レイヤーの表示 / 非表示 |
| レイヤーヘッダ右クリック | レイヤー挿入・削除・ロック・座標リンク |

### 9.2 キーボードショートカット (既定)

AviUtl の既定に合わせ、Linux 環境でぶつかりやすいものは代わりのキーを用意する。すべて「環境設定 > ショートカット」から変更できる。

| キー | 動作 |
|---|---|
| `Space` | 再生 / 停止 |
| `←` / `→` | 1 フレーム戻る / 進む |
| `Shift + ←/→` | 10 フレーム (設定値) 戻る / 進む |
| `Ctrl + ←/→` | 選択オブジェクトの境界 / 中間点にジャンプ |
| `Home` / `End` | 先頭 / 末尾 |
| `P` | 中間点を追加 |
| `S` | 現在位置で分割 |
| `Delete` | 削除 |
| `Ctrl + Z` / `Ctrl + Y` (`Ctrl+Shift+Z`) | 元に戻す / やり直し |
| `Ctrl + C` / `Ctrl + X` / `Ctrl + V` | コピー / 切り取り / 貼り付け |
| `Ctrl + S` / `Ctrl + Shift + S` | 保存 / 名前を付けて保存 |
| `Ctrl + E` | 出力ダイアログ |
| `Ctrl + ホイール` | タイムラインズーム |
| `[` / `]` | 選択範囲の開始 / 終了を設定 |

### 9.3 移動方法 (補間)

| 移動方法 | 内容 | 追加パラメータ |
|---|---|---|
| 移動無し | 開始値で固定 | – |
| 直線移動 | 線形補間 | 加速 / 減速 |
| 曲線移動 | Catmull-Rom (前後の中間点を参照) | 加速 / 減速 |
| 加減速移動 | Ease in-out (3 次ベジェ) | 加速 / 減速 |
| 瞬間移動 | 区間の終わりで切り替え | – |
| 中間点無視 | オブジェクト全体を 1 区間とみなし直線補間 | 加速 / 減速 |
| 移動量指定 | 開始値 + 1 フレームあたりの移動量 × 経過 | – |
| ランダム移動 | 指定間隔でランダムな値へ | 移動フレーム間隔 |
| 反復移動 | 開始値と終了値を往復 | 往復フレーム間隔 |
| 回転 | 終了値 = 回転数 (回転パラメータ専用) | – |
| ベジェ (拡張) | 任意の 3 次ベジェカーブをエディタで指定 | 制御点 2 つ |

補間は `IInterpolator` インターフェースで抽象化し、プラグインで追加できるようにする。

```csharp
public interface IInterpolator
{
    string Id { get; }                 // "linear", "curve", ...
    string DisplayName { get; }
    double Evaluate(InterpolationContext ctx); // ctx: 区間内進捗, 前後の値, フレーム情報, 乱数シード
}
```

---

## 10. レンダリングエンジン

### 10.1 フレーム合成パイプライン

`RenderEngine.RenderFrame(sceneId, frame)` の流れは次のとおり。

```
1. 対象時刻にアクティブなオブジェクトをレイヤー昇順に列挙
2. for each layer (1 → 100):
     a. オブジェクトのソース画像を取得
        - 動画: DecoderPool から frame に対応する画像 (キャッシュ優先)
        - テキスト: TextRenderer (SKTextBlob) で描画
        - 図形: SKPath
        - シーン: 再帰的に RenderFrame (循環参照は検出して禁止)
     b. 個別フィルタ (Effects[1..]) を順に適用 → SKImage
     c. グループ制御 / カメラ制御の変換行列を合成
     d. 標準描画パラメータ (X, Y, 拡大率, 回転, 透明度) で変換行列を作成
     e. 合成モード (SKBlendMode またはカスタム SkSL) でフレームバッファへ描画
        - 「奥行きで並べ替える」カメラ制御の対象なら、すぐには描かずに待たせ、
          同じカメラの対象が続く間まとめて奥 (カメラから遠い) から順に描く。深度ぼけはこのときかける
     f. フィルタオブジェクトの場合: 待たせていたものを描き終えてから、現在のフレームバッファ全体に効果を適用
3. 完成フレームを返す
```

### 10.2 エフェクトの実装モデル

```csharp
public interface IVideoEffect
{
    EffectDefinition Definition { get; }
    // 入力画像とパラメータから出力画像を作る。GPU コンテキストは ctx から取得する
    SKImage Apply(SKImage input, EffectParameters p, RenderContext ctx);
}
```

- 単純な色処理は `SKColorFilter`、ぼかしなどは `SKImageFilter`、それ以外は `SKRuntimeEffect` (SkSL) で実装する。
- GPU が使えない環境 (VM など) ではソフトウェアラスタライザにフォールバックする。

### 10.3 キャッシュ

| キャッシュ | 内容 | 上限 (既定) |
|---|---|---|
| デコードキャッシュ | ソースごとに直近 N フレーム (GPU テクスチャ or CPU ビットマップ) | 1GB |
| テキストキャッシュ | パラメータが変わらないテキストのラスタ結果 | 128MB |
| 波形キャッシュ | タイムライン表示用の音声ピーク値 (ディスクにも保存) | 無制限 (ディスク) |
| サムネイルキャッシュ | タイムライン上の動画サムネイル | 256MB |

LRU で破棄する。キャッシュのキーは `(SourceId, Frame, Quality)`。

### 10.4 プレビュー再生

- 再生時は Render スレッドが先行して 3〜5 フレームを `FrameQueue` に用意する。
- 音声をマスタークロックとし、映像は音声の再生位置に合わせて表示・ドロップする。
- 間に合わない場合はフレームを落として音声を優先する (AviUtl の「フレームスキップ」相当)。

---

## 11. メディア入出力

### 11.1 入力

- `FFmpeg.AutoGen` で `libavformat` / `libavcodec` / `libswscale` / `libswresample` をラップした `MediaReader` を作る。
- ハードウェアデコード: Linux では VA-API / VDPAU / NVDEC (CUDA)、Windows では D3D11VA、macOS では VideoToolbox。使えない場合はソフトウェアデコードにフォールバックする。
- シークを速くするため、初回読み込み時にキーフレームインデックスを作り、`~/.cache/video-builder/index/` に保存する。
- 重い素材 (4K / 10bit) には **プロキシ生成** (1/2・1/4 解像度の中間ファイル) を用意する。

### 11.2 出力

| 形式 | エンコーダ |
|---|---|
| MP4 (H.264 / H.265) | libx264 / libx265 / h264_vaapi / h264_nvenc |
| WebM (VP9 / AV1) | libvpx-vp9 / libsvtav1 |
| MOV (ProRes) | prores_ks |
| 連番画像 | PNG / JPEG / WebP |
| 音声のみ | WAV / FLAC / AAC / Opus |

出力は RenderEngine から得たフレームを `libavcodec` に直接渡す (プロセス間パイプは使わない)。
出力ダイアログには進捗バー・残り時間・プレビューの有無・キャンセルボタンを置く。

### 11.3 FFmpeg の同梱

- Linux: AppImage / Flatpak には FFmpeg 共有ライブラリを同梱する。**x264 / x265 を有効にした GPL ビルド**にし、配布物全体を GPL-3.0-or-later の条件で配る (本体は LGPL-3.0-or-later)。詳しくは [22.33](#2233-ライセンスと-ffmpeg-の同梱)。
- ライセンスは「ヘルプ > バージョン情報」と `THIRD_PARTY_NOTICES.md` に表示する。

---

## 12. プロジェクトファイル

### 12.1 形式

- 拡張子: `.vbproj` (ZIP コンテナ)
  - `project.json` … 本体 (System.Text.Json)
  - `thumbnail.png` … プロジェクトのサムネイル
  - `meta.json` … バージョン・作成アプリ情報
- 素材は **パス参照** (絶対パス + プロジェクトからの相対パス)。リンクが切れたときは再リンクダイアログを出す。
- 「素材を収集して保存」で素材を ZIP 内または隣のフォルダにコピーできる。

### 12.2 `project.json` 例

```json
{
  "formatVersion": 1,
  "settings": { "width": 1920, "height": 1080, "frameRate": "30000/1001", "sampleRate": 48000, "channels": 2 },
  "media": [
    { "id": "m1", "path": "/home/user/clip.mp4", "relativePath": "../clip.mp4", "hash": "xxh3:..." }
  ],
  "scenes": [
    {
      "id": "root", "name": "Root",
      "objects": [
        {
          "id": "o1", "type": "video", "layer": 2, "start": 30, "end": 300,
          "keyframes": [30, 120, 300],
          "effects": [
            {
              "id": "builtin.video", "enabled": true,
              "params": {
                "x":     { "values": [0, 200, 200], "mode": "linear", "args": { "accel": false, "decel": true } },
                "scale": { "values": [100], "mode": "none" },
                "file":  { "value": "m1" }
              }
            },
            { "id": "builtin.blur", "enabled": true, "params": { "radius": { "values": [5], "mode": "none" } } }
          ]
        }
      ]
    }
  ]
}
```

### 12.3 互換性

- `formatVersion` を上げるときはマイグレーション関数 (`IProjectMigration`) を順に適用する。

---

## 13. Undo / Redo

- **コマンドパターン**。すべての編集は `IEditCommand` を通して行う。

```csharp
public interface IEditCommand
{
    string Description { get; }           // "オブジェクトの移動" など (履歴表示用)
    void Execute(Project project);
    void Undo(Project project);
    bool TryMerge(IEditCommand next);     // トラックバー連続ドラッグなどを 1 つにまとめる
}
```

- トラックバーのドラッグは、マウスを押してから離すまでを 1 つのコマンドにまとめる (`TryMerge`)。
- 複数オブジェクトの同時操作は `CompositeCommand` で 1 つにまとめる。
- 履歴の上限は既定 200 件 (設定で変更可能)。
- 保存時点にマーカーを置き、タイトルバーの未保存表示 (`*`) に使う。

---

## 14. プラグイン / 拡張

v1.0 以降に段階的に実装する。インターフェースは `VideoBuilder.Plugin.Abstractions` として先に公開する。

| 種類 | インターフェース | 例 |
|---|---|---|
| 映像エフェクト | `IVideoEffect` | 独自フィルタ |
| 音声エフェクト | `IAudioEffect` | イコライザ・リバーブ |
| メディアオブジェクト | `IMediaObjectProvider` | 独自の図形・パーティクル |
| 補間 | `IInterpolator` | 独自イージング |
| 入力 | `IMediaReaderProvider` | 特殊フォーマット |
| 出力 | `IExporter` | 独自コンテナ・外部ツール連携 |

- 読み込み: `~/.local/share/video-builder/plugins/*/` の .NET アセンブリを `AssemblyLoadContext` で分離して読み込む。
- マニフェスト `plugin.json` (Id, Version, 対応 API バージョン) を必須にする。
- スクリプト: AviUtl スクリプトとの互換を重視し、**Lua (NLua / MoonSharp)** で `obj.*` API のサブセットを提供する ([決定事項](#21-決定事項))。

---

## 15. 設定・永続化

XDG Base Directory に従う (Windows / macOS では各 OS の標準パス)。

| 種別 | Linux のパス |
|---|---|
| 設定 | `$XDG_CONFIG_HOME/video-builder/settings.json` |
| ウィンドウ配置 | `$XDG_CONFIG_HOME/video-builder/layout.json` |
| テンプレート | `$XDG_DATA_HOME/video-builder/templates/` (`*.vbtemplate`。旧形式の `aliases/*.vbalias` も読む) |
| スタイル | `$XDG_DATA_HOME/video-builder/styles/` (`*.vbstyle`) |
| テンプレートから取り出した素材 | `$XDG_DATA_HOME/video-builder/template-assets/<内容のハッシュ>/` |
| プラグイン | `$XDG_DATA_HOME/video-builder/plugins/` |
| キャッシュ | `$XDG_CACHE_HOME/video-builder/` |
| 自動保存 | `$XDG_DATA_HOME/video-builder/autosave/` (既定 5 分間隔、10 世代) |
| ログ | `$XDG_STATE_HOME/video-builder/logs/` |

主な環境設定の項目: 最大画像サイズ、キャッシュサイズ、レイヤー高さ、ウィンドウモード (分離 / ドッキング)、テーマ (システムに従う / ライト / ダーク)、アクセントカラー (システムに従う / アプリ既定 / カスタム)、言語、ショートカット、ハードウェアデコードの有効化、プレビュー品質。

---

## 16. マルチプラットフォーム対応 (Linux 優先)

### 16.1 Linux 固有の対応

| 課題 | 対応 |
|---|---|
| Wayland | Avalonia 11 は XWayland 上で動く。複数ウィンドウの位置復元は Wayland では制限があるため、**Wayland ではドッキングモードを既定にする** |
| タイル型 WM (i3 / sway / Hyprland) | 設定ダイアログ等はツールウィンドウ (`ShowInTaskbar=false`, 親ウィンドウ指定) にし、浮動表示になるようヒントを与える |
| 日本語入力 (IME) | Fcitx5 / IBus。テキストオブジェクトの入力欄で確認する |
| オーディオ | OpenAL Soft で PipeWire / PulseAudio / ALSA を自動選択する |
| GPU | Avalonia の OpenGL / Vulkan バックエンド。Mesa / NVIDIA プロプライエタリ両方で確認する |
| ファイルダイアログ | Avalonia `StorageProvider` (xdg-desktop-portal 経由)。Flatpak でも動く |
| フォント | アプリ同梱フォントを優先し、fontconfig によるフォールバックも使う |

### 16.2 配布

| OS | 形式 |
|---|---|
| Linux | **AppImage** (主)、**Flatpak** (Flathub)、AUR、`.deb` / `.rpm` (将来) |
| Windows | ZIP (ポータブル) / MSIX |
| macOS | `.app` (dmg)、Apple Silicon / Intel |

- `dotnet publish -r linux-x64 --self-contained` を基本に、`linux-arm64` もビルドする。
- CI は GitHub Actions で、Ubuntu / Windows / macOS のマトリクスビルドを行う。

---

## 17. パフォーマンス目標

| シナリオ | 目標 |
|---|---|
| 1080p30 H.264 素材 1 本 + テキスト 3 つ のプレビュー | 30fps 維持 |
| 1080p30 素材 3 本の重ね合わせ + ぼかし | 24fps 以上 (GPU 有効時) |
| タイムライン 1,000 オブジェクトのスクロール | 60fps (仮想化描画) |
| トラックバー操作からプレビュー更新まで | 50ms 以内 (停止中) |
| 1080p 1 分の H.264 出力 | 実時間の 1.0 倍以下 (8 コア CPU, libx264 medium) |

タイムラインは `ItemsControl` を使わず、**独自の `TimelineCanvas` コントロール** で表示範囲のオブジェクトだけを `DrawingContext` に直接描画する (仮想化)。

---

## 18. テスト方針

| レベル | 対象 | ツール |
|---|---|---|
| 単体 | Core (補間・時間変換・モデル操作・Undo) | xUnit |
| スナップショット | RenderEngine の出力フレーム (許容差付きの画像比較) | Verify + ImageSharp 比較 |
| UI | ViewModel・主要ダイアログ・ショートカット | Avalonia.Headless.XUnit |
| メディア | 各コーデックの読み込み / 出力の往復 | 小さなテスト素材を FFmpeg で生成 |
| 手動 | Linux (GNOME/Wayland, KDE/X11, sway) ・ Windows ・ macOS | チェックリスト |

---

## 19. ソリューション構成

```
video-builder/
├─ VideoBuilder.sln
├─ Directory.Build.props          # 共通設定 (Nullable, LangVersion, unsafe)
├─ Directory.Packages.props       # 中央パッケージ管理
├─ src/
│  ├─ VideoBuilder.App/           # Avalonia エントリポイント (実行ファイル名: video-builder)
│  │  ├─ Shell/                   # MainWindow (メニュー・ツールバー・保存の確認・ドロップ), IWorkspace, PreviewArea, ToolWindow
│  │  ├─ Workspaces/
│  │  │  ├─ Advanced/             # 詳細編集 (AviUtl 風): AdvancedWorkspace, TimelineView, TimelineCanvas, ObjectSettingsView
│  │  │  └─ Simple/               # シンプル編集 (ムービーメーカー風): SimpleWorkspace, StoryboardStrip, ClipPanel, SimpleMedia
│  │  ├─ Views/                   # 共通のダイアログ (環境設定・出力・テンプレートなど)
│  │  ├─ Controls/                # PreviewView, Icon, IconButton, NumberBox, MiniSlider, FileDropZone
│  │  ├─ Services/                # PreviewService, ThemeService, AppSettings, MediaImport, Labels
│  │  └─ Themes/                  # Tokens.axaml (色), Base.axaml (スタイル), Icons.axaml (生成)
│  ├─ VideoBuilder.Editing/       # EditorSession, UndoStack, IEditCommand, StoryboardEditor
│  ├─ VideoBuilder.Core/          # Project, Scene, TimelineObject, Effect, AnimatedValue, EffectRegistry
│  │  └─ Storyboard/              # Storyboard (シンプル編集のモデル), StoryboardCompiler, StoryboardImport
│  ├─ VideoBuilder.Rendering/     # RenderEngine, Effects, Generators (テキスト・図形), AudioMixer, Exporter
│  ├─ VideoBuilder.Media/         # VideoReader, AudioReader, MediaWriter, AudioOutput
│  └─ VideoBuilder.Plugin.Abstractions/   # プラグイン用の公開インターフェース
├─ samples/
│  └─ SamplePlugin/               # プラグインの見本
├─ tests/
│  ├─ VideoBuilder.Tests/         # Core / Editing / Media / Rendering の単体テスト
│  └─ VideoBuilder.App.Tests/     # Avalonia.Headless による UI テスト
├─ tools/
│  ├─ IconGen/                    # Material Symbols SVG → Icons.axaml 変換
│  └─ I18n/                       # 日本語 → 英語の対応表 (en.txt) と生成スクリプト
├─ packaging/                     # .desktop・アイコン・AppImage 用スクリプト
└─ docs/
   └─ DESIGN.md                   # 本書
```

`VideoBuilder.Application` という名前は Avalonia の `Application` クラスと衝突するため、プロジェクト名を `VideoBuilder.Editing` にした。

---

## 20. 開発ロードマップ

| マイルストーン | 内容 | 完了条件 |
|---|---|---|
| **M0: 基盤** | ソリューション作成、テーマ (角型・ライト / ダーク切替・システムカラー追従)、アイコン生成、ウィンドウ骨格 | 空のメイン / タイムライン / 設定ダイアログが表示される |
| **M1: タイムライン** | Core モデル、TimelineCanvas、オブジェクトの追加・移動・長さ変更、Undo | 図形オブジェクトをタイムラインで編集できる |
| **M2: プレビュー** | RenderEngine (画像・図形・テキスト)、標準描画、プレビュー表示 | 静止画とテキストの合成がプレビューに出る |
| **M3: メディア** | FFmpeg 入力、動画・音声オブジェクト、音声再生、A/V 同期 | 動画を読み込んで音声付きで再生できる |
| **M4: アニメーション** | 中間点、移動方法、トラックバー完成 | AviUtl と同じ手順で移動アニメーションが作れる |
| **M5: エフェクト** | 基本フィルタ、フィルタオブジェクト、合成モード、クリッピング | 主要フィルタ 7 種が動く |
| **M6: 出力** | 出力ダイアログ、各エンコーダ | MP4 / WebM を出力できる |
| **M7: 仕上げ (v1.0)** | シーン、グループ / カメラ制御、エイリアス、自動保存、AppImage / Flatpak | リリース |
| M8 以降 | プラグイン API 公開、スクリプト、プロキシ | – |

---

## 21. 決定事項

初版で未決だった項目は、次のとおり決定した。

| # | 項目 | 決定 | 補足 |
|---|---|---|---|
| 1 | スクリプト言語 | **Lua** | AviUtl スクリプトとの互換性を優先する。C# スクリプトは採用しない |
| 2 | 既定のウィンドウモード | **1 つのウィンドウにまとめる** (0.13 で変更。当初は X11 で分離) | 環境設定で「ウィンドウを分ける」も選べる ([22.15](#2215-aviutl-特有で不自然だった点の見直し)) |
| 3 | GPU バックエンド | **まず Skia GL** | Vulkan は v1.0 以降に対応する |
| 4 | 3D (カメラ制御) の実装方式 | **`SKMatrix44`** | 性能が足りない場合に独自 OpenGL を検討する |
| 5 | 色空間 | **v1.0 は sRGB 8bit** | 内部設計は 16bit float (リニア) に拡張できる形にする |
| 6 | アプリ名 | **video-builder** | リポジトリ名と同じ。C# の名前空間は `VideoBuilder` |
| 7 | テーマ | **ライト / ダーク切替。既定はシステムに従う** | アクセントカラーもシステムに従う ([7.3](#73-テーマ切替とシステムカラー)) |
| 8 | ランタイム | **.NET 10 (LTS)** | サポート期間が長いため |
| 9 | FFmpeg | **9.0 に移行した** ([22.31](#2231-ffmpeg-9-への移行)) | アニメーション WebP の読み込みなど。x264・x265 入りの GPL ビルドを配布物に同梱する ([22.33](#2233-ライセンスと-ffmpeg-の同梱)) |
| 10 | フォント | **Noto Sans JP を同梱** | 画面の日本語表示と、テキストオブジェクトの既定フォントに使う |

## 22. 実装状況

2026-11-16 時点。ロードマップ ([20 章](#20-開発ロードマップ)) の M0〜M7 と、M8 のうちプラグイン API・スクリプトを実装した。

### 22.1 実装済み

| 区分 | 内容 |
|---|---|
| 基盤 (M0) | 角型テーマ、ライト / ダーク切替、システムのテーマ・アクセントカラー追従、Material Symbols Sharp のアイコン生成、分離 / ドッキングのウィンドウ切替 |
| タイムライン (M1) | レイヤー 100 層、オブジェクトの追加・移動・長さ変更・分割・コピー / 貼り付け・削除、複数選択、範囲選択、スナップ、レイヤーの表示 / ロック、ズーム、ファイルの D&D、Undo / Redo |
| プレビュー (M2) | 別スレッドでの合成、プレビュー上の選択枠とドラッグ移動、プレビュー品質 (フル・1/2・1/4) |
| メディア (M3) | 動画・画像・音声の読み込み、音声付き再生 (音声を時計にした同期)、動画と音声の連結 |
| アニメーション (M4) | 中間点、移動方法 9 種 (移動無し・直線・曲線・加減速・瞬間・中間点無視・移動量指定・ランダム・反復)、加速 / 減速、トラックバー (左右の値・スライダー・数式入力) |
| エフェクト (M5) | 色調補正・ぼかし・縁取り・シャドー・クリッピング・モザイク・クロマキー・単色化・反転・フェード、フィルタオブジェクト、合成モード 13 種 (追加分は [22.39](#2239-組み込みのエフェクトアニメーションの追加)) |
| 出力 (M6) | H.264 / H.265 / VP9 / AV1 / ProRes / 連番 PNG / WAV / FLAC (その環境の FFmpeg にあるエンコーダだけを表示) |
| 仕上げ (M7) | シーンとシーンオブジェクト、グループ制御、カメラ制御 (3D: Z・X/Y 軸回転・視点・視野角)、エイリアス、自動保存 (10 世代) と復元 |
| タイムライン表示 | 音声波形 (ディスクにキャッシュ)、動画・画像のサムネイル、BPM グリッドと拍へのスナップ、出力範囲 (`[` `]`) |
| 素材 | 見つからない素材の付け替え (フォルダから同名ファイルを検索)、相対パスによる自動復元、素材を収集して保存 |
| 操作 | ショートカットのカスタマイズ、日本語 / 英語の切替 |
| 拡張 (M8) | プラグイン API (フィルタ効果・メディアオブジェクト・移動方法) とサンプル、Lua によるスクリプト制御 |
| テンプレート | スタイル、テンプレート (まとめて置く / 展開して置く、公開項目、素材の同梱、長さの伸縮)、テンプレートライブラリ、ベクター画像 (SVG / Lottie) ([22.19](#2219-テンプレート)) |
| 配布 | AppImage 用のビルドスクリプト (`packaging/appimage/build.sh`) |

### 22.2 設計書との差分

| 項目 | 設計 | 現在の実装 | 理由 |
|---|---|---|---|
| FFmpeg のバージョン | 7.x | **9.0** (`FFmpeg.AutoGen 9.0.1.1`) | 以前は Ubuntu 24.04 LTS 標準に合わせて 6.1 だった。`FFmpeg.AutoGen` はライブラリのメジャーバージョンと一致している必要がある。[22.31](#2231-ffmpeg-9-への移行) |
| 描画バックエンド | Skia GL | **GPU (EGL + OpenGL ES。Windows は ANGLE) と CPU の切替**。既定は自動 | 下の [22.5](#225-gpu-描画) と [22.35](#2235-windows-の-gpu-描画-angle) を参照 |
| クロマキー | SkSL シェーダ | C# のピクセル処理 (GPU 描画中も、この効果だけ CPU) | SkiaSharp 2.88 の `SKRuntimeEffect` は描画時にネイティブ側でクラッシュするため |
| MVVM / DI / ログ | CommunityToolkit.Mvvm / MS.DI / Serilog | 未使用 (コードビハインド + 静的な `Svc`) | 画面数が少ない間は依存を増やさない。画面が増えたら導入する |
| フォント同梱 | Noto Sans JP などを同梱 | Inter と Noto Sans JP (Regular / Bold) を同梱。等幅フォントはシステムのものを使用 | – |
| メディアプール | 素材を ID で参照 | オブジェクトがファイルパスを直接持つ | 単純化。素材の再リンク機能を作るときに導入する |
| 再生位置の単位 | (未定義) | 秒 | AviUtl はフレームだが、フレームレートを変えても意味が変わらない秒にした |

### 22.3 未実装

| 項目 | 状況 |
|---|---|
| プラグインの入力・出力 | API が未定義 (映像・音声のエフェクト、メディアオブジェクト、移動方法は追加できる) |
| Flatpak | マニフェストと作成スクリプトあり (`packaging/flatpak/`)。仮想ディスプレイで起動を確認 ([22.20.11](#222011-配布-flatpakwindows--macos-向けのビルド))。Flathub への登録は未着手 |
| AppImage | AppDir を作って起動するところまで確認。`appimagetool` による AppImage 化は未確認 |
| Windows / macOS | 発行 (`dotnet publish`) が通り、必要なネイティブライブラリが入ることを確認。Windows は実機で GPU 描画・ハードウェアエンコードまで確認した ([22.35](#2235-windows-の-gpu-描画-angle))。macOS は実機での動作が未確認で、CPU 描画 |

### 22.4 確認できていないこと

- **GPU 描画の速度**: 開発環境に GPU がなく、ソフトウェア実装の OpenGL (llvmpipe) でしか動かしていない。絵が CPU 描画と一致することは確認したが、実際の GPU でどれだけ速くなるかは未計測。 GPU と CPU の間の受け渡しを減らす変更 ([22.52](#2252-gpu-と-cpu-の間の受け渡しを減らす-linux-の-gpu-描画)) も同じ。
- **音声の再生**: 開発環境に音声デバイスがないため、OpenAL での実際の出力は確認していない (デバイスがない場合に無音で再生が進むことは確認済み)。
- **波形・サムネイル・BPM グリッド**: テスト用の動画で表示を確認した。長時間の素材での速度は未計測。
- **英語表示**: 主要な画面を確認した。エラーメッセージの一部 (FFmpeg やプラグインの読み込みエラーの詳細) は日本語のまま。
- **実機での操作感**: 画面は仮想ディスプレイ上のスクリーンショットと UI 自動テストで確認した。Wayland・タイル型 WM・IME での動作は未確認。
- **Linux の arm64**: CI の arm64 ランナーでテストと配布物の作成を回すだけで、実機 (Raspberry Pi 5 など) では動かしていない。ARM の GPU 描画・V4L2 でのデコード・エンコードも、実機では未確認 ([22.36](#2236-linux-の-arm64)・[22.37](#2237-arm-の-gpu-描画と-v4l2-でのデコード))。

---

### 22.5 GPU 描画

| 項目 | 内容 |
|---|---|
| 方式 | 描画スレッドが画面に出さない OpenGL ES コンテキスト (EGL) を作り、その上の Skia GPU サーフェスに合成する。結果は読み戻して、プレビュー表示と出力に使う |
| GPU で処理するもの | オブジェクトの合成 (移動・拡大・回転・合成モード)、ぼかし・縁取り・シャドー・色調補正などのフィルタ、フィルタオブジェクト、シーンの入れ子 |
| CPU のままのもの | 動画のデコード、テキスト・図形の生成 (結果はキャッシュ)、クロマキー。動画の YUV → RGB の変換は、OpenGL ES 3 なら GPU で行う ([22.52](#2252-gpu-と-cpu-の間の受け渡しを減らす-linux-の-gpu-描画)) |
| 設定 | 環境設定 > 描画方式: **自動** / GPU / CPU。自動では、本物の GPU があれば GPU、なければ CPU を使う (ソフトウェア実装の OpenGL は CPU 描画より遅いため選ばない) |
| フォールバック | GPU コンテキストを作れない場合や読み戻しに失敗した場合は、自動的に CPU 描画になる |
| 出力 | プレビューと同じ方式で描く。出力用スレッドが自分の GPU コンテキストを作る |
| 対応 OS | Linux (`libEGL.so.1`)・Windows (Avalonia が同梱する ANGLE の `av_libglesv2.dll`。[22.35](#2235-windows-の-gpu-描画-angle))。macOS は未対応で、CPU 描画になる |
| 確認方法 | 同じシーンを CPU と GPU で描いて比較するテスト (平均の差 0.27 / 255) |

---

### 22.6 プラグインの作り方

`samples/SamplePlugin` が見本。

1. クラスライブラリを作り、`VideoBuilder.Plugin.Abstractions` を参照する。
2. `IPlugin` を実装し、`Initialize(IPluginHost host)` で登録する。
   - `host.RegisterColorMatrixEffect(定義, IColorMatrixEffect)` … 色の変換だけのエフェクト (いちばん速い)
   - `host.RegisterPixelEffect(定義, IPixelEffect)` … ピクセルを直接書き換えるエフェクト
   - `host.RegisterFilter(定義, IVideoEffect)` … 自由に描くエフェクト
   - `host.RegisterAudioEffect(定義, IAudioEffect)` … 音声のエフェクト
   - `host.RegisterMediaObject(定義, IVideoSource)` … メディアオブジェクト
   - `host.RegisterInterpolator(IInterpolator)` … トラックバーの移動方法
   - `host.RegisterCommand(IPluginCommand)` … 「プラグイン」メニューのコマンド (API バージョン 3 から。テンプレートを使う API は 4 から)
3. `plugin.json` (id / name / version / apiVersion / assembly) と DLL を、`$XDG_DATA_HOME/video-builder/plugins/<名前>/` に置く。

詳しくは [PLUGIN_API.md](PLUGIN_API.md)。
読み込みの結果 (失敗した場合は理由) は、環境設定の「プラグイン」に表示される。
プラグインはアプリと同じ権限で動くので、信頼できるものだけを入れること。

### 22.7 スクリプト制御 (Lua)

フィルタ効果「スクリプト制御」に書いた Lua が、フレームごとに実行される。

| 変数 | 内容 |
|---|---|
| `obj.ox` `obj.oy` `obj.oz` | 位置に足す値 (書き込み可) |
| `obj.rx` `obj.ry` `obj.rz` | 回転に足す値 (度, 書き込み可) |
| `obj.zoom` | 拡大率に掛ける値 (1 = 等倍, 書き込み可) |
| `obj.alpha` | 不透明度に掛ける値 (0〜1, 書き込み可) |
| `obj.x` `obj.y` `obj.z` | 標準描画の値 (読み取り) |
| `obj.time` `obj.frame` `obj.totaltime` `obj.totalframe` `obj.framerate` | オブジェクト内の時刻 |
| `obj.w` `obj.h` `obj.layer` `obj.screen_w` `obj.screen_h` | 大きさ・レイヤー・画面サイズ |
| `obj.rand(min, max [, seed, frame])` | 再現性のある乱数 (整数) |

- AviUtl の `obj.*` のうち、上の表のものだけに対応する (`obj.draw` や `obj.effect` などは使えない)。
- ファイルや OS には触れない (`io` `os` は使えない)。他人のプロジェクトを開いても、スクリプトが勝手にファイルを操作することはない。
- 命令数に上限があり、無限ループは中断してエラーを表示する。

---

### 22.8 CPU 描画の高速化 (ハードウェアに依存しないもの)

| 手法 | 内容 |
|---|---|
| フィルタ結果のキャッシュ | 元の絵とパラメータが前のフレームと同じなら、フィルタの結果を使い回す (動かないテロップの縁取りや、位置だけ動く図形の影など)。時刻で結果が変わる効果 (フェード・スクリプト制御) と、`EffectFlags.Deterministic` を宣言していないプラグインは対象外 |
| 帯に分けた並列描画 | Skia の CPU 描画は 1 回の描画が 1 スレッドでしか動かないので、画像を横帯に分けて複数スレッドで同時に描く (`PixelOps.DrawParallel`) |
| ピクセル処理の並列化 | 色調補正・単色化・クロマキーは、ピクセルを直接書き換える処理を複数スレッドで実行する。色調補正はチャンネルが混ざらない場合は表引きにする |
| 画面全体の色変換をその場で処理 | フィルタオブジェクトの色調補正・単色化は、画面を複製せずに直接書き換える |
| ぼかしの縮小処理 | 大きくぼかすときは、縮小 → ぼかし → 拡大にする (縮小後の強さが十分残る範囲に限る) |
| 補間の選び分け | 等倍で動かすだけなら補間なし。プレビューでは軽い補間、出力では常に高品質 |
| プレビュー用の近似 | クロマキーは、プレビューでは色を 6bit に丸めた表を引く (出力では正確な計算) |
| 動画フレームの複製削減 | デコーダのバッファを直接指す画像を使い、1 フレームごとの複製をなくす (CPU 描画のみ) |
| 出力の並行化 | 描画とエンコードを別スレッドで同時に進める |

計測結果 (1920x1080, 4 コアの VM, 1 フレームあたり。前 → 後):

| 場面 | 前 | 後 |
|---|---|---|
| サンプル (図形 + 影・テロップ + 縁取り・回転する図形・画面全体の色調補正) | 35.6 ms | 5.8 ms |
| 同上、1/2 プレビュー | 10.9 ms | 2.5 ms |
| 動画 + テロップ (縁取り・影) | 4.8 ms | 2.9 ms |
| オブジェクト 60 個 | 11.6 ms | 8.7 ms |
| 画面全体の色調補正 (値が毎フレーム変わる) | 26.3 ms | 7.1 ms |
| 画面全体の単色化 | 23.9 ms | 5.6 ms |
| 画面全体のぼかし | 29.4 ms | 17.8 ms |
| 動画に色調補正 | 17.2 ms | 7.4 ms |
| 動画にクロマキー (出力 / プレビュー) | 40.0 ms | 19.9 ms / 9.2 ms |
| 動画にぼかし | 41.0 ms | 18.0 ms |
| 画面全体のモザイク | 8.2 ms | 5.2 ms (マスごとの平均を直接計算する方式に変更) |
| 動画 + テロップの出力 (H.264 medium, 150 フレーム) | 34.9 fps | 40.3 fps |

出力はエンコーダ (x264) の処理時間が大半を占めるため、伸びは小さい。

モザイクは、GPU 描画でも同じ計算 (画像を読み戻してマスごとに平均) を使う。以前の「縮小 → 拡大」より色が正確で、CPU と GPU で同じ絵になる。

### 22.9 再生中の画面側の負荷

| 手法 | 内容 |
|---|---|
| 現在位置カーソルの分離 | 赤い線を別のコントロール (`PlayheadOverlay`) にして、位置を動かすだけにした。現在位置が動いても、タイムライン本体 (レイヤー・オブジェクト・文字) は描き直さない |
| 設定ダイアログの更新を間引く | 現在位置の変化による値の更新を 1 秒に 15 回程度までにした (止まったときの値は必ず反映する)。編集や選択の変更はすぐ反映する |
| タイムライン描画のキャッシュ | 文字の組版結果・ペン・中間点の図形・音声波形の図形を使い回す |
| プレビューの補間 | 再生中は軽い補間、停止中は高品質 |
| 翻訳結果の記憶 | 英語表示のとき、同じ文字列の照合を繰り返さない |

計測結果 (オブジェクト 40 個、動きのあるオブジェクトを選択した状態で現在位置を動かす):

| 項目 | 前 | 後 |
|---|---|---|
| 1 フレームぶんの更新処理 (イベント処理とレイアウト) | 約 11〜12 ms | 約 2〜3 ms |
| 現在位置を 120 フレーム動かす間の、タイムライン本体の描き直し | 120 回 | 0 回 |
| タイムライン本体を 1 回描き直すときの処理 (描画命令の組み立て) | – | 約 2〜3 ms |

画面に実際に描く時間 (GPU での描画) は、開発環境に画面がないため計測できていない。

---

### 22.10 高速化 (その 2)

| 手法 | 内容 |
|---|---|
| 大きな絵の合成を並列化 | 拡大・縮小・回転して広い範囲に描く絵は、描画先を横帯に分けて複数スレッドで描く |
| 変換行列の正規化 | 遠近のない描画で Skia の速い経路 (平行移動・拡大・回転) が使われるよう、行列をそろえる |
| フィルタ結果キャッシュの上限をメモリ量に | 個数 (48 個) で制限していたため、画面上のオブジェクトが多いとキャッシュが効かなくなっていた |
| 作業用バッファの使い回し | フレーム大のバッファ (1080p で約 8MB) を毎フレーム確保・解放せず、取っておいて再利用する (`PixelPool`) |
| 半分ずつの縮小 | 毎フレーム変わる絵 (動画・シーン) を半分より小さく描くときと、ぼかしの縮小処理で、2x2 の平均を並列に繰り返す。Skia に縮小用の中間画像を毎フレーム作らせない |
| 縮小時の補間 | 半分〜等倍の縮小は 4 点補間にする (中間画像が不要) |
| クロマキーの早期判定 | 彩度だけで範囲外と分かるピクセルは色相の計算を省く (結果は同じ) |
| 複数動画の同時デコード | 同じフレームに動画が複数あるとき、動画ごとに別スレッドでデコードする |
| 編集時の複製を削減 | 取り消し用の「編集後」の複製を、次の編集の「編集前」とプレビュー描画に使い回す。ドラッグ中のプレビュー更新は画面更新 1 回につき 1 度にまとめる |
| 自動保存 | 変更があったときだけ、別スレッドで保存する (以前は 5 分ごとに全体を文字列化して比較していた) |
| プロジェクトファイル | 字下げなしで書く |
| 起動 | 配布用ビルドで事前コンパイル (ReadyToRun) を有効にする |

計測結果 (1920x1080, 4 コアの VM。前 → 後):

| 場面 | 前 | 後 |
|---|---|---|
| 動画を 150% に拡大して 10° 回転 (出力品質) | 29.4 ms | 約 12〜15 ms |
| 同上 (プレビュー) | 8.9 ms | 約 5〜6 ms |
| 動画を 60% に縮小 | 3.9 ms | 約 2.2 ms |
| 4K 動画を 1080p に 50% で配置 | 9.2 ms | 約 7 ms |
| 縁取り付きの図形 50 個 | 27.5 ms | 2.2 ms |
| 縁取り付きのテロップ 50 個 | 13.0 ms | 2.1 ms |
| オブジェクト 2000 個のプロジェクトを描く | 20.3 ms | 2.0 ms |
| 同上、1 回の編集 (取り消し履歴への記録まで) | 68.0 ms | 20.3 ms |
| 同上、プロジェクトの文字列化 (保存) | 591 ms / 20.4 MB | 170 ms / 6.7 MB |
| 画面全体のぼかし | 17.8 ms | 11.4 ms |
| 動画にぼかし | 18.0 ms | 13.4 ms |
| 起動 (プロセス開始 → ウィンドウ表示, 配布用ビルド) | 約 1.75 秒 | 約 1.0 秒 |

試して採用しなかったもの:

- 動画の色変換 (YUV → BGRA) のマルチスレッド化: 1080p で 2.5 → 2.4 ms とほぼ変わらなかった。
- メモリ確保の調整 (malloc のしきい値): 一貫した効果がなかった。
- 出力時の色変換の並列化: エンコーダ (x264) が全コアを使い切っているため、合計の処理量が減らない限り速くならない。

---

### 22.11 ハードウェアエンコード

出力ダイアログの「形式」に、そのマシンで使えるハードウェアエンコーダが自動で加わる
(例: 「MP4 (H.264 + AAC) - ハードウェア (NVENC)」)。使えるものがなければ何も増えない。

| 仕組み | エンコーダ | 対象 | フレームの渡し方 | 品質の指定 |
|---|---|---|---|---|
| NVENC | `h264_nvenc` `hevc_nvenc` `av1_nvenc` | NVIDIA | メモリ上のフレームをそのまま渡す | 品質一定 (`rc=vbr`, `cq`)、速度は `p1`〜`p7` |
| Quick Sync | `h264_qsv` `hevc_qsv` `av1_qsv` | Intel | 同上 | 品質一定 (ICQ, `global_quality`) |
| VAAPI | `h264_vaapi` `hevc_vaapi` `av1_vaapi` | Linux の Intel / AMD | GPU 上のフレームへ転送してから渡す | 量子化値を固定 (`rc_mode=CQP`, `qp`) |
| AMF | `h264_amf` `hevc_amf` `av1_amf` | Windows の AMD | メモリ上のフレームをそのまま渡す | 量子化値を固定 (`rc=cqp`) |
| VideoToolbox | `h264_videotoolbox` `hevc_videotoolbox` | macOS | 同上 | ビットレート (品質の値から目安を計算) |
| V4L2 | `h264_v4l2m2m` | ARM ボードなど | 同上 | ビットレート (同上) |

- **検出**: FFmpeg に組み込まれているだけでは使えるとは限らない (GPU やドライバが必要)。
  そこで、小さな設定で実際にエンコーダを開いてみて、開けたものだけを選択肢に出す。結果は覚えておく。
- **品質の目盛り**: 出力ダイアログの「品質」は x264 の CRF と同じ 0〜51 のまま使い、エンコーダごとの指定に置き換える。
  同じ数値でも、ソフトウェア (x264) とはファイルの大きさや見た目が一致しない。
- **色変換**: BGRA → NV12 / YUV420P の変換は CPU で行う (ソフトウェアエンコードと同じ)。

**実機での確認**: NVIDIA GeForce RTX 4070 Laptop GPU (NVENC) で、問題なく高速に出力できることを確認した (2026-11-18)。
Intel / AMD (VAAPI)・Apple (VideoToolbox) などほかのエンコーダは未確認。
確認したのは、(1) 使えない環境で検出が「なし」になり、出力を試みても安全に失敗すること、(2) エンコーダごとの設定の組み立て、
(3) ソフトウェアエンコードが従来どおり動くこと、の 3 点。GPU のある環境でテストを実行すると、使えるエンコーダそれぞれで
短い動画を書き出して読み戻すテスト (`Usable_hardware_encoders_produce_a_readable_file`) が動く。

---

### 22.12 ハードウェアデコード

動画を開くとき、GPU でデコードできれば GPU を使い、できなければソフトウェアでデコードする。

| OS | 試す順番 |
|---|---|
| Linux | NVDEC (CUDA) → VAAPI (Intel / AMD) |
| Windows | NVDEC (CUDA) → D3D11VA → DXVA2 (描画に Intel / AMD の GPU を選んでいれば D3D11VA を先に。D3D11VA はその GPU で開く) |
| macOS | VideoToolbox |

- **仕組み**: FFmpeg の hwaccel を使う。デコーダに GPU のデバイスを渡し、出力形式を GPU 上の形式にする。
  表示するフレームだけをメモリへ写して (`av_hwframe_transfer_data`)、あとは従来どおり BGRA に変換する。
  シークの途中で読み飛ばすフレームは写さない。
- **デバイスの共有**: デバイスは種類ごとに 1 つだけ作り、すべての動画で共有する。開けなかった種類は覚えておき、2 回目からは試さない。
- **自動で戻る**: 次のどの場合も、ソフトウェアのデコーダに切り替えて同じ位置を取り出し直す。
  デバイスがない / その動画の形式に GPU が対応していない (開くとき) / デコードが途中で失敗した / GPU からフレームを写せない。
- **設定**: 環境設定の「動画のデコード」(自動 / ソフトウェアのみ)。変えると開いている動画を開き直す。
  環境変数 `VIDEO_BUILDER_HWDEC` で、試すデバイスを 1 つに絞れる (`vaapi` `cuda` など。`off` で使わない)。
- **確認方法**: テストでは、ソフトウェアでデコードしながら「ハードウェアで開いた」扱いにして途中で失敗させ、
  切り替え後も前後へのシークを含めてソフトウェアと同じフレームを返すことを確かめている
  (この確認で、切り替え直後に 1 つ前のフレームを返す不具合を見つけて直した)。

**実機での確認**: NVIDIA GeForce RTX 4070 Laptop GPU (NVDEC) で、問題なく高速にデコードできることを確認した (2026-11-18)。
Intel / AMD (VAAPI) などほかの方式は未確認。GPU のない環境では、自動的にソフトウェアになることを確認している。
また、GPU でデコードしても、フレームをメモリへ写す時間がかかるため、1080p 程度では速くならないこともある。
4K 以上や H.265 / AV1 など、CPU でのデコードが重い素材で効果が出るはず。

---

### 22.13 プロキシ

重い動画 (4K・H.265 など) のかわりに、プレビューでだけ使う小さな動画 (プロキシ) を作る。出力には使わない。

| 項目 | 内容 |
|---|---|
| 形式 | MJPEG (すべてのフレームが単独で読める形式) を Matroska (.mkv) に入れる。どの位置へもすぐ移動できる。音声は入れない (元のファイルから読む) |
| 大きさ | 縦横 1/2 または 1/4 (環境設定)。画質は MJPEG の量子化 4 |
| 時刻 | 元の動画と同じ時刻の目盛りでフレームを書くので、プロキシと元の動画で同じ時刻に同じフレームが出る |
| 置き場所 | `$XDG_CACHE_HOME/video-builder/proxy/`。フォルダ名は「元のパス・大きさ・更新日時・縮小率」から決めるので、元の動画を書き換えると古いプロキシは使われない |
| 区切り | 元の動画を 20 秒ごとに区切り、別々のファイル (`00000.mkv`, `00001.mkv`, …) にする (22.25)。すべて書き終えたら印のファイル (`complete`) を置く。以前の版の 1 ファイルのプロキシ (`フォルダ名.mkv`) も、そのまま使う |
| 作るとき | 再生が重くなりそうな動画を読み込んだとき・それを含むプロジェクトを開いたときに自動で (環境設定で切れる。22.25)。タイムラインの右クリック「プロキシを作成」、ファイルメニュー「プロキシ > すべての動画のプロキシを作成」でも作れる |
| 作っている間 | 裏で 1 本ずつ作る。ツールバーに進み具合を出す。区切りができるたびに、その時刻からプレビューがプロキシに切り替わる |
| プレビューでの使い方 | 小さな絵を、元の動画と同じ大きさで描く (`Sprite.Scale`)。そのオブジェクトにフィルタがあるときは、ぼかしの範囲などがずれないよう、元の大きさに戻してからフィルタをかける |
| 壊れたプロキシ | 開けなければ元の動画を読む |

計測結果 (3840x2160・30fps・10 秒の H.264、1/2 プレビュー、4 コアの VM):

| | 再生 (1 フレーム) | 任意の位置へ移動 | 作成時間 | 大きさ |
|---|---|---|---|---|
| 元の動画 | 16.4 ms | 163.8 ms | – | 23.9 MB |
| 1/2 プロキシ | 4.4 ms | 18.0 ms | 2.6 秒 | 30.9 MB |
| 1/4 プロキシ | 1.6 ms | 4.7 ms | 2.2 秒 | 9.9 MB |

プラグイン API の互換性: `Sprite` に `Scale` を足したが、以前のコンストラクタ `Sprite(image, cx, cy)` も残しているので、作成済みのプラグインはそのまま動く。

---

### 22.14 ほかのアプリからのファイルのドロップ

ファイルマネージャなどからタイムラインへファイルをドラッグ&ドロップすると、落とした位置 (レイヤー・時刻) に置く。
複数のファイルは下のレイヤーへ順に置き、動画は音声と連結する。ドラッグ中は、置かれる行と時刻を色で示す。
タイムラインの外 (プレビューなど) に落としたときは、現在位置に置く。

| OS | 仕組み |
|---|---|
| Windows / macOS | Avalonia の DragDrop |
| Linux (X11・XWayland) | 自前の XDND 受け取り口 (`Platform/XdndDropTarget`) |

**Linux で自前にした理由**: 使っている Avalonia 11.3 の X11 版は、ほかのアプリからのドロップ (XDND) の受け取りに対応していない
(ドロップしても何も起きなかった)。そこで Avalonia とは別に X サーバーへつなぎ、見えない「代理ウィンドウ」を作って、
アプリのウィンドウに `XdndProxy` (ドロップのやり取りは代理と行ってほしいという目印) を付ける。
ドラッグ元 (GTK・Qt などのファイルマネージャ) は代理ウィンドウとやり取りするので、Avalonia を通さずに受け取れる。

- 受け付けるのは `text/uri-list` (ファイルの一覧) だけ。文字だけのドラッグは断る。
- X のエラー (ドラッグ元が途中で閉じられたなど) でアプリが終了しないよう、エラーの処理を差し替える。
  Avalonia 側の接続のエラーは、元の処理に回す。
- **確認方法**: テストの中で XDND の手順どおりに動く「ドラッグ元」を作り、仮想の X サーバー (Xvfb) 上でやり取りして、
  ファイルの一覧と位置を受け取れることを確かめている。また、実際のアプリを Xvfb 上で動かし、同じ方法でドロップして
  タイムラインに置かれることを画面で確認した。
- **確認できていないこと**: 実際のファイルマネージャ (Nautilus・Dolphin など) からのドロップと、Wayland 環境 (XWayland 経由) での動作。

---

### 22.15 AviUtl 特有で不自然だった点の見直し

操作感は AviUtl に寄せたまま、AviUtl の成り立ち (プラグインを前提にした作り) に由来する言葉や動きのうち、
動画編集アプリとして不自然なものを一般的なものに改めた。内部の ID・プロジェクトファイルの形式は変えていないので、
以前のプロジェクトはそのまま開ける。

**言葉**

| 以前 (AviUtl の言葉) | 今 | 理由 |
|---|---|---|
| 拡張編集 | タイムライン | 「拡張編集」は AviUtl のプラグイン名 |
| 設定ダイアログ | プロパティ | 1 つのウィンドウにまとめたときはダイアログではない |
| 中間点 | キーフレーム | 動画編集で一般的な呼び方 |
| フィルタ効果 | エフェクト | 同上 |
| フィルタオブジェクト | 調整レイヤー | 下のレイヤー全体にかかるものの一般的な呼び方 (タイムライン上は「調整レイヤー [色調補正]」) |
| メディアオブジェクトの追加 | オブジェクトを追加 | 「メディアオブジェクト」は AviUtl の分類名 |
| エイリアス | プリセット | 設定を保存して使い回すものの一般的な呼び方 |
| 標準描画 / 標準再生 | 変形 / 音声 | 何を設定する欄かが分かる名前に |
| 参照ファイル / 再生位置 | ファイル / 開始位置 | 同上 (再生位置は「素材のどこから使うか」) |
| クリッピング | クロップ | 「クリッピング」は合成の意味と紛らわしい |
| 合成モードの「色差」「陰影」「明暗」 | カラー / 焼き込みカラー / ハードライト | 画像編集アプリで一般的な名前 |
| シーン「Root」 | メイン | 新しく作るプロジェクトから |
| Layer 1 … | レイヤー 1 … | 日本語表示で英語が混ざっていた |

**動き**

| 以前 | 今 |
|---|---|
| 既定でプレビュー・タイムライン・設定ダイアログが別々のウィンドウ (X11) | 既定で 1 つのウィンドウ。分けることもできる |
| レイヤー名のどこをクリックしても表示 / 非表示が切り替わる (うっかり隠しやすい) | 目のアイコンで表示、鍵のアイコンでロックを切り替える。名前をクリックすると、そのレイヤーのオブジェクトを選ぶ |
| 範囲選択は Ctrl を押しながらドラッグ。何もない所をドラッグすると現在位置が動く | 何もない所をドラッグすると範囲選択 (Ctrl で今の選択に追加)。クリックだけなら選択を外して現在位置を移す。現在位置をドラッグで動かすのはルーラーで行う |

AviUtl の操作感の中心 (レイヤーとオブジェクト、キーフレームで区切った区間ごとの左右の値、パラメータ名からの移動方法の選択、
右クリック中心の操作、ショートカット) はそのまま残している。

---

### 22.16 高速化 (その 3)

| 手法 | 内容 | 効果 (4 コアの VM) |
|---|---|---|
| プレビューを表示の大きさで描く | プレビュー品質の既定を「自動」にし、画面に表示される大きさ (物理ピクセル) で描く。以前は表示が小さくても常にプロジェクトの大きさ (1920x1080 など) で描いていた | 表示が 870x490 なら、描くピクセル数は約 1/5 |
| 取り消し履歴の共有 | 取り消し用の複製で、変わっていないオブジェクトは前の複製と同じものを使う (変わったものだけ複製する) | オブジェクト 2000 個で 100 回編集: 履歴のメモリ 722 MB → 1.9 MB、1 回の編集 34 ms → 5 ms |
| ドラッグ中のプレビュー用の複製 | 同じく、変わっていないオブジェクトを「編集前」の複製と共有する | オブジェクト 2000 個: 9.7 ms → 5.3 ms |
| プロパティ欄の使い回し | エフェクトの並びが同じオブジェクトなら、欄を作り直さずに値だけ入れ直す。作った欄はすべて付けたままにして、表示 / 非表示を切り替える (付け外しのたびにスタイルを当て直すと遅いため) | 選択の切り替え 約 160 ms → 約 10 ms |
| 動画デコーダの貸し借り | デコーダをファイルごとに持ち、描画のたびに「続きから読めるもの」をオブジェクトに貸す。分割したクリップのつなぎ目でシークし直さない | 4K を 1 秒ごとに分割した 10 秒の再生: いちばん遅いフレーム 330 ms → 49 ms、平均 17.4 → 12.4 ms、デコーダ 10 個 → 1 個 |
| 音声の読み手の貸し借り | 音声も同じ。消したオブジェクトの読み手が残り続ける問題も解消 | 分割した音声が 1 つの読み手で続けて読める |
| サムネイル | デコーダを開き直さずに使い回し、プロキシがあればプロキシから作る | – |
| 出力の音声 | 1 フレームごとの音声の入れ物を使い回す | – |
| 出力の色変換を描画側へ | BGRA → YUV の変換を、エンコード側ではなく描画側のスレッドで行う。エンコーダへ次のフレームを渡すまでの待ちがなくなる | 1080p 動画 + テロップを H.264 (medium) で出力: 約 40 fps → 約 49 fps |

試して採用しなかったもの:

- プロジェクトの JSON をビルド時生成の手順で読み書きする: 速くならなかった (保存はファイル全体で 2000 個 40 ms ほどで、十分速い)。
- 色選択の部品のスタイルを起動時に読み込まない / JIT の設定を変える: 起動時間 (約 0.8 秒、配布用ビルド) に差が出なかった。
- 描画 1 フレームあたりのメモリ確保: 約 70 KB で、100 フレームでガベージコレクションが 0 回だったので、手を入れていない。

### 22.17 プラグイン API (バージョン 2)

速く動くプラグインを書きやすくするために、API を整理した ([PLUGIN_API.md](PLUGIN_API.md))。バージョン 1 のプラグインはそのまま読み込める (バージョン 2 の時点では `apiVersion` 1〜2 を受け付けた。今は 1〜3。22.40)。

| 追加したもの | 内容 | 速くなる理由 |
|---|---|---|
| `IColorMatrixEffect` | 色の変換をカラーマトリクスで返すだけのエフェクト | 続けて並んだ色の変換を 1 つの行列にまとめて 1 回で処理する (組み込みの色調補正・単色化とも)。調整レイヤーではその場で書き換え、GPU 描画中は GPU 上で処理 |
| `IPixelEffect` / `PixelBuffer` | BGRA のピクセルを直接書き換えるエフェクト。`ParallelRows` でエンジンのスレッドを使って並列化できる | 描画を経由しない。調整レイヤーでは画面を複製しない。バッファは使い回し |
| `EffectFlags.Deterministic` | 結果が入力とパラメータだけで決まることを宣言する | フィルタ結果のキャッシュの対象になる (以前は組み込みの効果だけ) |
| `IVideoSource.CacheKey` | 絵を使い回すための鍵を返す | 鍵が同じ間は `Render` を呼ばない |
| `IAudioEffect` / `AudioBlock` | 音声のエフェクト。ブロックをその場で書き換え、続きの値は `state` に持つ | 確保なしで処理できる。シーク後は `Continuous = false` |
| `EffectArgs.Frame` / `Time` / `IsDraft` | 今のフレーム・オブジェクトの先頭からの秒と、プレビューの軽い描画中か | 再生中は品質を落とす、などができる |
| `IPluginHost.Log` | プラグインのログ | – |

あわせて、組み込みの音声エフェクト「音声フェード」「エコー」を追加し、音声オブジェクトの「エフェクトを追加」には音声のエフェクトだけが出るようにした。
サンプルプラグインには、セピア (色の変換)・ポスタリゼーション (ピクセル)・こもった音 (音声) と、市松模様の `CacheKey` を追加した。

### 22.18 カメラ制御の奥行きによる並べ替え・深度ぼけ

カメラ制御に 3 つの設定を追加した。

| 設定 | 既定 | 内容 |
|---|---|---|
| 奥行きで並べ替える | オン | 対象のオブジェクトを、カメラから遠いものから順に描く (手前のものが上に重なる)。距離が同じならレイヤー順。オフにするとレイヤー順 |
| 深度ぼけ | 0 | ピントの位置から離れたオブジェクトをぼかす強さ (画面上のぼけの半径、ピクセル)。ぼけの半径は「強さ × (距離とピントの距離の差) ÷ 距離」で、遠くでは強さの値に近づき、手前ではさらに大きくなる |
| ピントの距離 | 0 | カメラからピントの合う位置までの距離。0 なら目標の位置にピントを合わせる |

- 並べ替えるのは、同じカメラ制御の対象が続く範囲だけ。対象外のオブジェクト・フィルタオブジェクト (調整レイヤー)・次のカメラ制御が来たら、それまでのものを描き終えてから進む (フィルタオブジェクトが「それより上のレイヤー」にかかる意味は変わらない)。
- 距離はオブジェクトの中心で測る。大きく傾いた板同士が交差する場合の、ピクセル単位の前後関係 (Z バッファ) は扱わない。
- 深度ぼけは、絵の 1 ピクセルが画面で何ピクセルになるかを中心で測り、画面上で同じぼけになるように絵の上でぼかす (プレビューの大きさによらず同じ見た目)。大きくぼかすときは縮小してから計算する (ぼかしフィルタと同じ仕組み)。
- 元の絵とぼけの半径が前のフレームと同じなら、ぼかした結果を使い回す (止まっている背景を毎フレームぼかさない)。
- 選択枠は、ぼけて広がる前の大きさで表示する。


### 22.19 テンプレート

「プリセット」(旧エイリアス。選んだオブジェクトを `.vbalias` に保存して挿入し直すもの) を広げて、次のものを保存し、いつでも複製できるようにする。

- テキストの設定 (フォント・色・縁取り・影など)
- 複数のオブジェクトを組み合わせたもの (テロップの帯、下三分の一の名前表示など)
- アイキャッチ (長さを変えても入りと抜けの動きが崩れないもの)
- SVG のようなベクターアニメーション

#### 22.19.1 ほかのアプリの方式

| アプリ | 仕組み | 特徴 |
|---|---|---|
| Premiere Pro | エフェクトプリセット (`.prfpst`) / モーショングラフィックステンプレート (`.mogrt`) | `.mogrt` は ZIP。中身は入れ子のコンポジション・公開する項目・サムネイル・プレビュー動画。タイムラインには 1 つのクリップとして置き、公開された項目だけを編集する。「レスポンシブデザイン - 時間」で入りと抜けを固定して中間だけ伸ばせる |
| After Effects | アニメーションプリセット (`.ffx`) / エッセンシャルグラフィックス | プリコンポーズした中から外に出す項目 (マスタープロパティ) を選ぶ |
| DaVinci Resolve | Fusion のマクロ / テンプレート (`.setting`) | ノードの組み合わせと外に出す入力を 1 ファイルにする。Templates フォルダに置くとタイトル・ジェネレーターとして一覧に出る |
| Final Cut Pro | Motion テンプレート | Motion で「公開」したパラメータだけを編集できる |
| AviUtl | エイリアス (`.exa`) / オブジェクトファイル (`.exo`) | 前者は 1 オブジェクトの設定、後者は複数オブジェクトの配置。素材はパス参照 |
| YMM4 | アイテムテンプレート | 複数アイテムをまとめて保存し、文字などを差し替える |

共通する考え方は次のとおり。video-builder もこれに合わせる。

1. **設定だけを当てるもの (スタイル)** と **部品として置くもの (テンプレート)** を分ける。
2. 部品は **入れ子 (シーン)** にして、作る人が **公開する項目** を選ぶ。使う人は 1 つのオブジェクトとして扱い、公開された項目だけを触る。
3. 挿入すると **複製** される。元のテンプレートを直しても、置いたものは変わらない。
4. 素材・サムネイル・必要なフォントやプラグインの情報を **1 つのファイル (ZIP)** にまとめ、別の PC に持っていける。
5. 長さを変えたとき、**入りと抜けの動きは固定し、中間だけを伸び縮み** させられる。

#### 22.19.2 種類

| 種類 | 保存するもの | 使い方 | ファイル |
|---|---|---|---|
| スタイル | エフェクト 1 つ分の設定 (例: テキストの「サイズ・フォント・色」、縁取りの「サイズ・色」) | 選んだオブジェクトの同じエフェクトに上書きする。調整用のエフェクト (縁取りなど) で、オブジェクトにそのエフェクトがなければ追加する | `.vbstyle` (JSON) |
| テンプレート (まとめて置く) | 複数のオブジェクトを入れたシーンと、公開する項目 | シーンオブジェクト 1 つとして置く。プロパティには公開した項目だけが出る | `.vbtemplate` (ZIP) |
| テンプレート (展開して置く) | 同上 | 中のオブジェクトをそのままタイムラインに並べる (今までのプリセットと同じ) | 同上 |

- 同じ `.vbtemplate` を、挿入するときに「まとめて置く」か「展開して置く」かを選べる。既定は保存したときの選択 (`meta.json` の `insertAs`)。
- スタイルには **内容を表す値を含めない**: ファイル、テキストオブジェクトの本文、シーンオブジェクトの参照先。こうすると「文字の見た目」だけを使い回せる。
- スタイルのトラックバーの値は、保存元の 1 区間目の左右の値と移動方法を保存する。当てるときは、相手の区間数に合わせて埋める: 動かない値はすべて左の値、動く値は左・右・左…と交互に入れる (キーフレームの数が違うオブジェクトにも当てられる)。

#### 22.19.3 テンプレートのファイル形式 (`.vbtemplate`)

ZIP の中身:

```
meta.json        名前・カテゴリ・説明・作った解像度とフレームレート・長さ・入りと抜けの固定 (秒)・既定の挿入方法・必要なフォントとプラグイン
content.json     シーンの並び。先頭が本体、2 つめ以降は本体から参照しているシーン (入れ子)
thumbnail.png    一覧に出す絵 (本体の中間のフレーム。長辺 320px)
assets/          同梱した素材。ファイル名は「内容のハッシュ 8 桁_元のファイル名」
```

`content.json` の中のファイルの値は `asset:<assets の中のファイル名>` と書く。同梱しなかった (見つからなかった) ファイルは元の絶対パスのまま残す。

`meta.json` の例:

```json
{
  "formatVersion": 1, "id": "3f2a9c1b7d0e", "name": "名前テロップ", "category": "テロップ", "description": "",
  "width": 1920, "height": 1080, "frameRate": "30", "length": 150,
  "introSeconds": 0.5, "outroSeconds": 0.5, "insertAs": "scene",
  "fonts": ["Noto Sans JP"], "plugins": [], "createdAt": "2026-11-19T00:00:00Z"
}
```

- `fonts` は使っているフォント名 (既定のフォントは書かない)。`plugins` は組み込みでないエフェクトの ID。
- 解像度が違うプロジェクトにも挿入できる。座標はそのまま (中心からのピクセル) で、変換はしない。

#### 22.19.4 公開する項目

シーンに `Exposed` (公開する項目の並び) を持たせる。

```
ExposedParam
 ├─ ObjectId : シーン内のオブジェクトの ID
 ├─ Effect   : そのオブジェクトの何番目のエフェクトか
 ├─ Key      : パラメータのキー ("text"・"color" など)
 ├─ Label    : 表示名 (例: 「名前」「帯の色」)
 └─ Group    : グループ名 (例: 「文字」「色」)。空ならグループなし
```

並び順は `Exposed` の並びのとおり。同じグループの項目は続けて並べる (編集のダイアログで確定するときにそろえる)。

- シーンオブジェクトを選ぶと、プロパティの先頭に「テンプレートの項目」欄が出て、参照先のシーンの公開項目を並べる。値を変えると、参照先のシーンの中のオブジェクトの値が変わる (取り消しもできる)。
- 挿入するたびにシーンを複製するので、同じテンプレートを 2 回置いても、それぞれ別の文字にできる。
- トラックバーの項目は、現在位置をシーンの中の時間に直した位置の区間を編集する。
- 公開する項目は、保存のダイアログで選ぶ。すべてのエフェクトのすべてのパラメータ (変形の X・Y・拡大率・透明度・回転、フィルタのトラックバー・色なども) を選べる。一覧はオブジェクトごとの開閉できる欄に分け、その中をエフェクトごとの小見出しで区切る。上の欄にパラメータ名を入れると絞り込める (オブジェクト・エフェクト・パラメータの名前で探す)。最初から選んでおくのは、テキストの本文・文字色、図形の色、音声以外のファイル、ベクター画像の色。選んだ項目のあるオブジェクトの欄は開いておく。
- 保存したあとも、メイン以外のシーンを開いてプロパティのパラメータの行を右クリックし「テンプレートの項目として公開」で追加・削除・名前の変更ができる。

**展開して置いたテンプレートの項目**

- 「展開して置く」(オブジェクトをそのまま並べる) ときも、公開項目を使えるようにする。シーンを作らないので、置いた先のシーンに `TemplateGroups` (`TemplateGroup` の並び: Id・元のテンプレート・公開項目) を持たせ、置いたオブジェクトには `TemplateGroup` (組の Id) を持たせる。公開項目の `ObjectId` は、置いたオブジェクトの新しい ID に付け替える。
- 置くたびに新しい組になる (同じテンプレートを 2 回置いても、別々に編集できる)。
- 組のオブジェクトのどれかを選ぶと、プロパティの先頭に「テンプレートの項目」欄が出て、組の公開項目を並べる (別のオブジェクトの項目も、そこから編集できる)。トラックバーの項目は、現在位置をそのオブジェクトの範囲に収めた位置の区間を編集する。
- 組のオブジェクトなら、メインのシーンでもパラメータの行の右クリックで、組の項目の追加・削除・名前の変更・「テンプレートの項目を編集」ができる (メインのシーン自体の公開項目にはしない)。
- 貼り付けなど、テンプレートの挿入以外で置いたオブジェクトは組とのつながりを持たない。置いたオブジェクトからテンプレートを作り直すときは、組とのつながりを消し (`TemplateGroup` = null)、組の項目を保存のダイアログで最初から選んでおく。
- 公開元のオブジェクトやエフェクトがなくなった項目は表示しない。

**項目の並べ替え・グループ分け**

- 「テンプレートの項目」欄の見出しの右に「編集」ボタンを置き、「テンプレートの項目を編集」ダイアログを開く。シーンの中では、パラメータの行の右クリックメニューからも開ける。
- ダイアログは項目を 1 行ずつ並べ、行ごとに 表示名・グループ (入力済みのグループ名から選べる)・上へ / 下へ・削除 を置く。どのオブジェクトのどのパラメータかも表示する。
- OK で、同じグループの項目を、最初に出てくる位置にまとめてから保存する (グループの順はそれぞれの最初の項目の順、グループの中の順は並べたとおり)。1 回の取り消しで戻せる。
- プロパティでは、グループが変わる所にグループ名の小見出しを出す。グループなしの項目には小見出しを出さない。
- テンプレートとして保存し直すと (シーンオブジェクト 1 つを選んで保存)、並び順とグループもそのまま保存される。

**利用者が付けた名前は訳さない**

画面の文字は、TextBlock に入った時点で自動的に英語に置き換えている (日本語のソースの文言を対応表で引く)。利用者が付けた名前がたまたま対応表の言葉と同じだと (「名前」→ Name など) 置き換わってしまうので、次の表示は訳さない (`L.Verbatim` で作った TextBlock は置き換えの対象外)。

- テンプレートの項目の表示名とグループ名 (プロパティ)
- テンプレートの名前・説明・カテゴリ (ライブラリ・右クリックのメニュー・保存のダイアログのカテゴリの候補)
- スタイルの名前 (右クリックのメニュー)
- 編集のダイアログのグループの候補
- オブジェクトの表示 (タイムラインのラベル・テンプレートのダイアログの「どこの項目か」): テキストの本文・ファイル名・シーンの名前
- シーンの名前 (タイムラインのシーンの一覧・シーンオブジェクトの「シーン」の選択)

アプリが用意した名前 (「すべてのカテゴリ」「プリセット (旧形式)」・新しいプロジェクトのシーン「メイン」・オブジェクトの種類の名前など) と、パラメータ本来の名前は訳す。
名前を含む文 (「シーン [名前]」など) は `L.Format` で型紙だけを訳し、名前はそのまま入れる (`L.T` に渡すと、型紙の中身まで対応表で引いてしまう)。

ファイル名など、メッセージの中に入る名前は `L.Name(名前)` で囲む。

- 目印に Unicode の分離文字 (U+2068 FSI / U+2069 PDI) を使う。訳すときは、目印の付いた部分を対応表で引かない (型紙の `{0}` に入っても訳さない)。
- 目印は、TextBlock とウィンドウのタイトルに入る時点で取り除く (日本語の表示でも)。取り除いた文字をもう一度訳さないよう、受け口が入れ直した文字は覚えておいて処理しない。
- 囲んでいるもの: ウィンドウのタイトルのプロジェクトのファイル名、読み込み・保存・取り込みのエラーのファイル名、素材を収集したフォルダ、プロキシ作成中の表示とエラー、「〜はすでにあります」「〜を削除しますか?」のテンプレート・スタイルの名前、公開をやめるメニューの表示名。最近使ったファイルのメニューは `verbatim` で訳さない。

**ファイルのドロップ**

- プロパティのファイルの欄 (テンプレートの項目を含む) に、ほかのアプリからファイルをドロップすると、その欄のファイルを差し替える。
- 受け取るのは、その欄の拡張子 (`FileExtensions`) に合うファイルだけ。複数のファイルを落としたときは、合う最初の 1 つを使う。合うものがなければ何もしない (タイムラインにも置かない)。
- ドラッグ中は、落とせる欄の枠をアクセントカラーにする。
- X11 では Avalonia がほかのアプリからのドロップを受け取れないので、自前の受け取り口 (`XdndDropTarget`) が受け取った位置で画面の部品を探し (`InputHitTest`)、ファイルの欄の上ならその欄に、そうでなければこれまでどおりタイムラインに渡す。Windows・macOS では Avalonia の `DragDrop` をそのまま使う。

#### 22.19.5 長さの伸縮 (入りと抜けの固定)

シーンオブジェクトに 3 つの設定を追加する。

| 設定 | 既定 | 内容 |
|---|---|---|
| 長さに合わせて伸縮 | オフ | オブジェクトの長さが参照先のシーンと違うとき、中間の時間を伸び縮みさせて、最後にシーンの最後が来るようにする |
| 先頭の固定 (秒) | 0 | 伸縮しない先頭の長さ (入りのアニメーション) |
| 末尾の固定 (秒) | 0 | 伸縮しない末尾の長さ (抜けのアニメーション) |

オブジェクトの中の位置 `t` (0 始まり)、オブジェクトの長さ `Lo`、シーンの長さ `Ls`、先頭 `I`・末尾 `O` (フレーム。`I + O` が `Lo` と `Ls` の小さいほうを超えないよう縮める) として、シーンの中の位置は次のとおり。

```
t < I            → t
t ≥ Lo − O       → Ls − (Lo − t)
それ以外          → I + (t − I) × (Ls − I − O) ÷ (Lo − I − O)
```

これに「開始位置」を足す。テンプレートを挿入したときは、`meta.json` の `introSeconds` / `outroSeconds` を入れ、伸縮をオンにする。分割したオブジェクトでは、それぞれの長さを基準にする (分割前と同じ時間にはならない)。

#### 22.19.6 保存と挿入の処理

保存 (`TemplatePackage.Create`):

1. 選んだオブジェクト (連結しているものを含む) を、先頭を 0 フレーム・いちばん上を 0 レイヤーにそろえて新しいシーンに入れる。ただし選んだのがシーンオブジェクト 1 つだけなら、その参照先のシーンをそのまま本体にする (公開項目と伸縮の設定も引き継ぐ)。
2. 本体から参照しているシーンを、入れ子の奥までたどって集める。
3. ファイルの値を集め、存在するものを `assets/` に入れて `asset:` に書き換える。
4. 使っているフォント・組み込みでないエフェクトを `meta.json` に書く。
5. 本体の中間のフレームを描いてサムネイルにする (App 側で `RenderEngine` を使う)。

挿入 (`TemplatePackage.Instantiate` → `EditorSession.InsertTemplate`):

1. 同梱の素材を `template-assets/<内容のハッシュ>/` に取り出す (同じ内容なら取り出し済みのものを使う)。プロジェクトはそのパスを参照する。「素材を収集して保存」でプロジェクトの隣に集められる。
2. シーンの ID・オブジェクトの ID・連結のグループを振り直し、シーンオブジェクトの参照先と公開項目の `ObjectId` を新しい ID に付け替える。
3. シーンの名前は「テンプレート名」。同じ名前があれば「テンプレート名 (2)」のように番号を付ける。
4. 「まとめて置く」なら、本体のシーンを参照するシーンオブジェクトを 1 つ作る (長さは本体の長さ、伸縮の設定は `meta.json` から)。「展開して置く」なら、本体のオブジェクトを並べ、入れ子のシーンだけをプロジェクトに足す。
5. シーンの追加とオブジェクトの追加は 1 回の取り消しにまとめる。
6. 足りないフォント・プラグインがあれば、挿入したあとで一覧を出して知らせる (挿入は止めない。フォントは既定のものに置き換わって描かれる)。

#### 22.19.7 画面

- タイムラインのオブジェクトの右クリック: 「テンプレートとして保存...」(旧「プリセットとして保存」)。保存のダイアログで、名前・カテゴリ・説明・既定の挿入方法・先頭 / 末尾の固定 (秒)・公開する項目を決める。
- タイムラインの空白の右クリック: 「テンプレートから追加」(カテゴリごとのサブメニュー)、「テンプレートライブラリ...」。
- メニューの「編集」: 「テンプレートライブラリ...」。ライブラリは、サムネイル付きの一覧・カテゴリの絞り込み・名前の検索・挿入 (まとめて / 展開して)・削除・読み込み (ほかの PC で作った `.vbtemplate` を取り込む)・書き出しができる。挿入先は現在位置で、選択中のオブジェクトがあればそのレイヤー、なければいちばん上の空いているレイヤー。
- プロパティのエフェクトの見出しの右クリック: 「スタイルとして保存...」「スタイルを適用」(同じエフェクトのスタイルだけを出す)。

#### 22.19.8 ベクター画像 (SVG / Lottie)

オブジェクトの種類に「ベクター画像」を追加する。

| 形式 | 読み込み | 動き |
|---|---|---|
| SVG (`.svg`) | Svg.Skia 2.0.0.8 で `SKPicture` にする | 動かない (SMIL・CSS のアニメーションは使えない)。動きは変形・エフェクト・移動方法で付ける。色は置き換えられる |
| Lottie (`.json`) | SkiaSharp.Skottie 2.88.9 | ファイルの中のアニメーションをそのまま再生する。After Effects (Bodymovin) や Lottie 対応のツールで作れる |

| パラメータ | 内容 |
|---|---|
| サイズ | 長辺を何ピクセルで描くか。0 ならファイルに書かれた大きさ。ベクターなので、大きくしてもぼやけない (変形の「拡大率」で拡大すると、描いたあとの絵を拡大するのでぼやける) |
| 開始位置 / 再生速度 / ループ再生 | Lottie のときだけ使う。動画ファイルと同じ意味 |
| ファイル | `.svg` / `.json` |
| 色 1 〜 色 8 | 色の置き換え (SVG / Lottie)。空ならファイルの色のまま |

- どちらも SkiaSharp 2.88.9 と同じ版のネイティブライブラリで動く (追加のネイティブライブラリは要らない)。
- SVG は 1 回描いた絵をサイズごとに覚えておく。Lottie は毎フレーム描く (読み込んだアニメーションはファイルごとに覚えておく)。
- テンプレートに入れると、ファイルは他の素材と同じように同梱される。
- `.lottie` (dotLottie, ZIP) は対応しない。

**色の置き換え (SVG / Lottie)**

SVG にも Lottie にも「名前の付いた色の欄」がないので、ファイルに出てくる色を、出てきた順に重複なく並べたもの (パレット) を使う。「色 n」はパレットの n 番目の色を置き換え、その色が出てくる所をすべて新しい色にする (透明度はそのまま)。

- Lottie の対象: シェイプの塗り・線 (`ty` が `fl` / `st` の `c`。キーフレームの値 `s` / `e` も含む)、平面レイヤーの色 (`sc`)、テキストの文字色・線の色 (`fc` / `sc`)。グラデーションは対象外。
- SVG の対象: `fill` / `stroke` / `stop-color` / `flood-color` / `lighting-color` / `color` の属性と、`style` 属性・`<style>` の中の同じ名前のプロパティ。値は `#rgb` / `#rgba` / `#rrggbb` / `#rrggbbaa` / `rgb()` / `rgba()` / 色の名前 (`red` など)。`none`・`currentColor`・`url(...)` は数えない。透明度の部分はそのまま残す。指定のない部分 (既定の黒い塗り) は置き換えられない。
- 読み込む前に SVG / JSON の文字列を書き換えてから Svg.Skia / Skottie に渡す (どちらにも、読み込んだあとに色を変える API がない)。色を置き換えた SVG は文字列から読むので、SVG の中から相対パスで参照している画像は表示されない (置き換えていないときは表示される)。書き換えたものは、置き換えの組み合わせごとに 16 個まで覚えておく。
- プロパティには、ファイルにある色の数だけ「色 n」を出す (最大 8 個)。空の欄は「#RRGGBB (ファイルの色)」と表示し、置き換えた欄には「ファイルの色に戻す」ボタンを出す。
- ふつうの色のパラメータなので、テンプレートの公開項目 (保存のダイアログでも、ファイルにある色の数だけ候補に出す) やスタイルにそのまま使える。
- 同じ色を別々の色に変えることはできない (同じ色の部分はまとめて変わる)。別々に変えたいときは、Lottie 側で少しだけ違う色にしておく。
- Lottie のファイルをタイムラインに置くと、オブジェクトの長さはアニメーションの長さ (`(op − ip) ÷ fr`) になる。

#### 22.19.9 旧形式 (`.vbalias`) との互換

- `aliases/*.vbalias` はそのまま一覧に出し (カテゴリ「プリセット (旧形式)」)、「展開して置く」で挿入する。サムネイル・公開項目はない。
- 旧形式のものを「テンプレートとして保存」し直すと `.vbtemplate` になる。旧形式のファイルは自動では消さない。
- ショートカットの ID (`alias.create`) は変えない (設定ファイルとの互換のため)。表示名は「テンプレートとして保存」にした。

#### 22.19.10 実装

| 場所 | 内容 |
|---|---|
| `Core/Templates.cs` | `TemplatePackage` (作る・保存・読み込み・挿入用の複製)、`TemplateMeta`、`EffectStyle` |
| `Core/SceneTime.cs` | シーンオブジェクトの時間の写像 (伸縮) |
| `Core/Project.cs` | `ExposedParam`、`Scene.Exposed` |
| `Editing/TemplateStore.cs` | `TemplateStore` (旧形式の読み込みを含む)、`StyleStore` |
| `Editing/EditorSession.cs` | `InsertTemplate`・`SetExposed`・`ApplyStyle` (どれも 1 回で取り消せる) |
| `Rendering/VectorSources.cs` | SVG / Lottie の読み込みと描画 |
| `App/Services/TemplateService.cs` | 挿入 (足りないフォント・プラグインの確認)、サムネイル、`.vbtemplate` のドロップ |
| `App/Views/TemplateDialogs.cs` | 保存のダイアログ、テンプレートライブラリ |
| `App/Views/ObjectSettingsView.cs` | 「テンプレートの項目」欄、公開の右クリックメニュー、スタイルの右クリックメニュー (エフェクトの見出し) |

- プロパティの入力欄は「どのオブジェクトのどのエフェクトを編集するか」を `Slot` で受け取るようにした。テンプレートの項目の欄は、参照先のシーンの中のオブジェクトを編集する。トラックバーは、現在位置をシーンの中の時間に直した位置の区間を編集する。
- `.vbtemplate` をタイムラインにドロップすると、ライブラリに取り込んでから挿入する。
- 確認: 単体テスト (保存と読み込み・素材の同梱と取り出し・ID の振り直し・入れ子のシーン・旧形式・取り込み時の名前の重複・足りないプラグイン・スタイル・SVG / Lottie の描画・伸縮) と、UI テスト (挿入してプロパティから公開項目を編集・取り消し、保存のダイアログ、ライブラリからの挿入、ドロップ、スタイルの適用)。画面は仮想ディスプレイのスクリーンショットで確認した。


### 22.20 追加機能 (2)

テンプレート機能 ([22.19](#2219-テンプレート)) のあとに挙げた追加機能。実装しやすいものから順に並べ、この順に実装する。

| # | 機能 | 22.20 の節 |
|---|---|---|
| 1 | 共有テンプレートフォルダ | 22.20.1 |
| 2 | 解像度に合わせてテンプレートを挿入 | 22.20.2 |
| 3 | 字幕の読み込み・書き出し (SRT / VTT) | 22.20.3 |
| 4 | ベクター画像の細かい制御 (SVG の部品の非表示・dotLottie) | 22.20.4 |
| 5 | テンプレートライブラリでのプレビュー再生 | 22.20.5 |
| 6 | 音声: イコライザ・自動音量調整 (ダッキング) | 22.20.6 |
| 7 | マスク | 22.20.7 |
| 8 | トランジション | 22.20.8 |
| 9 | テキストアニメーション (1 文字ずつ) | 22.20.9 |
| 10 | テンプレートの更新の反映 | 22.20.10 |
| 11 | 配布: Flatpak・Windows / macOS 向けのビルド | 22.20.11 |

#### 22.20.1 共有テンプレートフォルダ

- 環境設定に「共有テンプレートフォルダ」を 1 つ指定できる (チームの共有ドライブなど)。
- ライブラリと「テンプレートから追加」は、自分のテンプレートと共有フォルダの `*.vbtemplate` を合わせて並べる。共有のものには「共有」と表示する。
- 共有フォルダのテンプレートは読むだけ。削除はできず、書き出し・挿入はできる。保存・読み込み (取り込み) は自分のフォルダに入れる。「共有フォルダにコピー」で自分のテンプレートを共有フォルダに置ける。
- 共有フォルダが見つからない (ネットワークが切れているなど) ときは、自分のテンプレートだけを並べる。

#### 22.20.2 解像度に合わせてテンプレートを挿入

テンプレートは座標を画面の中心からのピクセルで持つので、作った解像度と違うプロジェクトに入れると、大きさと位置がずれる。

- 挿入するとき、テンプレートの解像度 (`meta.json` の `width` / `height`) とプロジェクトの解像度が違えば、倍率 `k = min(プロジェクトの幅 ÷ テンプレートの幅, プロジェクトの高さ ÷ テンプレートの高さ)` で合わせる。
- 合わせ方: テンプレートの中のすべてのオブジェクト (入れ子のシーンを含む) の「変形」の X・Y・Z と拡大率、グループ制御の X・Y と拡大率、カメラ制御の位置を、キーフレームの値ごと `k` 倍する。図形の「背景」は画面の大きさで描くので変えない。
- 縦長 (9:16) に横長 (16:9) のテンプレートを入れると、横幅に合わせて小さくなる (はみ出さない)。
- ライブラリに「解像度に合わせる」のチェック (既定はオン。環境設定に覚える) を置く。オフなら座標をそのまま使う (これまでと同じ)。

#### 22.20.3 字幕の読み込み・書き出し (SRT / VTT)

- 「ファイル」メニューに「字幕を読み込む...」「字幕を書き出す...」を追加する。
- 読み込み: SRT (`.srt`) と WebVTT (`.vtt`) を読み、字幕 1 つにつきテキストオブジェクト 1 つを作る。開始・終了の時刻はプロジェクトのフレームに直す (終了は含まないので、終了フレーム − 1 までにする)。
  - 置くレイヤー: 選択中のオブジェクトのレイヤー、なければ現在位置の下から、すべての字幕が重ならずに入る最初のレイヤー。
  - 見た目: テキストオブジェクトを選んでいれば、その設定 (フォント・大きさ・色・位置・エフェクト) をひな形にする。なければ、下寄せ (Y = 画面の高さ × 0.4)・大きさ 48・縁取り付き。
  - 1 回の取り消しで消せる。タグ (`<i>`・`<b>`・`{\an8}` など) は取り除く。
- 書き出し: 選択中のオブジェクトのレイヤー (なければレイヤー 1) のテキストオブジェクトを、時間順に SRT / VTT で書く。拡張子で形式を決める。
- 文字コードは UTF-8 (BOM があってもよい)。SRT の時刻は `00:00:01,000`、VTT は `00:00:01.000`。

#### 22.20.4 ベクター画像の細かい制御

- **SVG の部品の非表示**: ベクター画像に「非表示にする部品 (id)」を追加する。カンマ区切りで、SVG の要素の `id` を書く。その要素に `display="none"` を付けてから読み込む。色の置き換えと同じく文字列を書き換えて読み込むので、置き換えた SVG の中の相対パスの画像は表示されない。
- **dotLottie (`.lottie`)**: ZIP の中の `manifest.json` の最初のアニメーション (`animations/<id>.json`) を使う。`images/` の画像は、JSON の中に埋め込み (data URI) にしてから Skottie に渡す。色の置き換えもできる。

#### 22.20.5 テンプレートライブラリでのプレビュー再生

- 保存するとき、本体を等間隔に 12 フレーム描いて、長辺 160px の絵を横に並べた 1 枚の PNG (`preview.png`) をテンプレートに入れる。
- ライブラリでサムネイルにマウスを乗せると、この 12 枚を 1 秒に 8 枚の速さで繰り返し表示する。離すとサムネイルに戻す。
- `preview.png` のない (古い) テンプレートは、サムネイルのままにする。

#### 22.20.6 音声: イコライザ・自動音量調整 (ダッキング)

- **イコライザ** (音声のエフェクト): 低音 (100 Hz・シェルフ)・中音 (1 kHz・ピーク)・高音 (8 kHz・シェルフ) をそれぞれ −24〜+24 dB。双 2 次フィルタ (RBJ の式) を左右別に持ち、ブロックをまたいで状態を続ける。
- **自動音量調整 (ダッキング)** (音声のエフェクト): BGM に付けると、ほかの音声 (ナレーションなど) が鳴っている間だけ、BGM の音量を下げる。
  - パラメータ: 下げる量 (dB, 既定 12)、反応する大きさ (dB, 既定 −40)、下げる速さ (秒, 既定 0.05)、戻す速さ (秒, 既定 0.4)。
  - 合成のしかた: まずダッキングの付いていない音声を混ぜ、その音の大きさ (絶対値の包絡線) を求める。次にダッキングの付いた音声を、包絡線が「反応する大きさ」を超えている間は「下げる量」だけ小さくして混ぜる。音量の変化は、下げる速さ・戻す速さでなめらかにする。
  - ダッキングの付いた音声同士は、お互いに反応しない。
  - 下げる速さ・戻す速さは時定数 (その時間で変化の約 63% 進む)。包絡線は立ち上がりを即座に、下がりを 50ms で追う。
  - イコライザのシェルフは、角の周波数 (100 Hz / 8 kHz) でつまみの半分 (dB) の効き方になる。

#### 22.20.7 マスク

- エフェクト「マスク」: オブジェクトの絵を図形で切り抜く。
- パラメータ: 形 (四角形・円・角丸四角形)、X・Y (絵の中心から)、幅・高さ、回転、角の丸み、ぼかし (境界のぼかしの幅)、反転 (図形の外側を残す)。
- 絵の大きさは変えない。ぼかしは Skia の `MaskFilter` (正規分布) で描く。
- 結果は入力とパラメータだけで決まるので、結果を使い回す (フィルタの結果のキャッシュの対象)。

#### 22.20.8 トランジション

- エフェクト「トランジション」: クリップの入り (イン) と出 (アウト) にかける。同じエフェクトをスタイルとして保存すれば、いつでも同じ切り替えを使える。
- 種類: フェード・ワイプ (左から / 右から / 上から / 下から)・円形ワイプ・スライド (左から / 右から / 上から / 下から)・ズーム。
- パラメータ: 種類、イン (秒)、アウト (秒)、ぼかし (ワイプの境界の幅, px)。
- ワイプと円形ワイプはマスクで描く (進み具合に合わせて図形を大きくする)。スライドは画面の幅・高さぶん外から動かし、ズームは拡大率 0 から、それぞれ描く位置 (`DrawModifiers`) で動かす。動きは加減速 (イーズ) を付ける。
- 2 つのクリップのクロスフェード: 次のクリップを下のレイヤー (手前に描かれる) に、前のクリップと重ねて置き、イン を付ける。タイムラインの右クリック「次のクリップとつなぐ (クロスフェード)」で、選んだクリップのすぐ後ろのクリップ (同じレイヤー。連結している音声も一緒) を、下のレイヤーのまとめて入るいちばん近い所 (映像と音声の組なら 2 つ下) に移して重ね、映像にトランジション (フェード) のイン、音声にフェードイン、前のクリップと連結した音声にフェードアウトを付ける (重ねる長さは環境設定、既定 0.5 秒)。

#### 22.20.9 テキストアニメーション (1 文字ずつ)

- テキストオブジェクトに「文字の出し方」を追加する: なし・1 文字ずつ表示 (タイプライター)・1 文字ずつフェードイン・1 文字ずつ下から・1 文字ずつ拡大。
- パラメータ: 文字の間隔 (秒, 1 文字ごとに遅らせる時間)、1 文字の長さ (秒, フェードなどにかける時間)、逆順 (最後の文字から)。
- 時間はオブジェクトの先頭から数える。改行と空白も 1 文字として数える。
- 描き方: 文字ごと (テキストの要素ごと) に、透明度・上下のずれ・拡大率を決めて描く。字の並び (配置) はアニメーションがないときと同じ。
- 文字の出し方が「なし」以外のときは、毎フレーム描き直す (アニメーションが終わったあとは、同じ絵を使い回す)。

#### 22.20.10 テンプレートの更新の反映

- テンプレートに版を持たせる (`meta.json` の `version`。新しく作ると 1、同じ名前で上書き保存すると、それまでの `id` を引き継いで 1 つ増やす)。
- 挿入したシーンに、元のテンプレートの `id`・名前・`version` を覚えさせる (`Scene.Template`)。
- シーンオブジェクトのプロパティの「テンプレートの項目」欄に、ライブラリにもっと新しい版があれば「新しい版があります」と「更新」ボタンを出す。右クリックメニューにも「テンプレートの更新を反映」を置く。
- 更新のしかた: 新しい版を挿入するのと同じようにシーンを作り、古いシーンの公開項目の値を、同じパラメータ (キー) で同じ表示名の項目に写す。シーンオブジェクトの参照先を新しいシーンにして、ほかから参照されていない古いシーンを消す。1 回の取り消しで戻せる。
- 「このテンプレートのものをすべて更新」で、プロジェクトの中の同じテンプレートのものをまとめて更新できる。

#### 22.20.11 配布: Flatpak・Windows / macOS 向けのビルド

- Flatpak のマニフェスト (`packaging/flatpak/`) を用意する。ランタイムは Freedesktop、.NET はアプリと一緒に入れる (self-contained で発行したものを入れる)。FFmpeg は Freedesktop ランタイムの ffmpeg-full 拡張を使う。
- Windows (`win-x64`)・macOS (`osx-arm64`, `osx-x64`) 向けに `dotnet publish` が通ることを確かめるスクリプトを置く (`packaging/publish-all.sh`)。実機での動作確認はできていない (22.4)。

確認したこと (2026-11-19):

| 対象 | 結果 |
|---|---|
| `packaging/publish-all.sh` | linux-x64・linux-arm64・win-x64・osx-x64・osx-arm64 のすべてで発行が通り、実行ファイルと libSkiaSharp・libHarfBuzzSharp が入る (1 つ 120MB 前後) |
| `packaging/flatpak/build.sh --bundle` | 組み立て・インストール・1 ファイルのバンドル (約 40MB) まで通る。サンドボックスの中に `/app/lib/ffmpeg/libavcodec.so.60` (ffmpeg-full 拡張) と `libopenal.so.1` がある |
| Flatpak の起動 | 仮想ディスプレイ (Xvfb) の上で、サンドボックスの中のアプリが起動して画面を描く。音の出力・素材の読み込みは確かめていない |

- ランタイムは 23.08 (サポート終了) にしている。24.08 以降の FFmpeg は 7.x (libavcodec.so.61) で、今の FFmpeg 6.1 用のバインディングでは読み込めないため。FFmpeg 7 に移るときに、ランタイムも上げる。
- OpenAL はランタイムにないので、openal-soft 1.23.1 を一緒に作る (openal-soft.org から取得)。
- FFmpeg を探す場所に `/app/lib/ffmpeg` (Flatpak の拡張の置き場所) を足した。
- Wayland では XWayland で動く (`--socket=x11`)。


### 22.21 シンプル編集 (ムービーメーカー風)

AviUtl 風の詳細編集とは別に、動画・写真を並べるだけで作れる簡単な編集画面を用意した。
環境設定 (または「表示」→「編集モード」) の「編集モード」で切り替える。既定は詳細編集のまま。

#### 22.21.1 できること

| 操作 | 内容 |
|---|---|
| ビデオと写真 | 動画・写真をストーリーボードに追加 (選んでいるクリップの後ろ、ドロップなら落とした位置)。写真は 5 秒 |
| タイトル | 色の背景に文字を出すクリップ (選んでいるクリップの前) |
| キャプション | クリップに重ねる文字。位置 (下・中央・上) と文字色 |
| 音楽 | 現在位置から鳴らす音声。使う範囲・音量・フェードイン / アウト、「映像の終わりで止める」 |
| 長さ | 動画は使う範囲 (開始・終了)、写真・タイトルは表示時間。`[` / `]` で現在位置を開始 / 終了にする |
| 速度 | 動画の再生速度 (0.25〜8 倍から選ぶ)。クリップの長さは「使う範囲 ÷ 速度」。音声も同じ速さで、音の高さも変わる |
| 切り替え効果 | クリップに入るときの効果 (フェード・ワイプ・スライド・ズームなど) と長さ。先頭のクリップは黒から |
| 動き・見た目・回転 | ズームイン / アウト・左右へのパン、白黒・セピア・ぼかしなど、90° ずつの回転 |
| 音声 | 動画の音量・音を消す |
| 並べ替え・分割・削除 | ドラッグで並べ替え、`S` で現在位置で分割、`Delete` で削除、切り取り・コピー・貼り付け |
| すべてに付ける | 切り替え効果・見た目 (すべてのクリップ)、動き (動画・写真)、表示時間 (同じ種類の写真・タイトル) を、選んでいるクリップと同じにする (`StoryboardEditor.ApplyToAll`、1 回の取り消し) |
| 音楽の右クリック | ここから・現在位置から・先頭から鳴らす、鳴らし始めへ移動。「映像の終わりで音楽を止める」はすべての音楽に共通の設定なので、何も選んでいないときの欄にも出す |
| 全体を表示 | 「表示」→「ストーリーボード全体を表示」で、作品全体が横幅に収まる倍率にする |
| 端のドラッグ | クリップ・音楽の端をドラッグして長さを変える (`StoryboardEditor.ResizeClip` / `ResizeMusic`)。いつもドラッグ前の設定から計算し、1 回のドラッグは mergeKey で 1 回の取り消しにまとめる。クリップの始まりを削ると動画の使う範囲の始まりが、音楽の始まりを削ると鳴らし始めも同じだけずれる (残りは同じ時刻に鳴る)。繰り返す音楽は端で変えない |
| 複数選択 | `Ctrl`+クリックで加える / 外す、`Shift`+クリックで範囲、`Ctrl+A` ですべて。削除・切り取り・コピー・前後への移動 (順を保って 1 つずつずらす) はまとめて行う。右の欄は、切り替え効果・動き・見た目・画面いっぱい・表示時間を選んでいるすべてに付ける (`UpdateSelectedClips`) |
| 音楽の分割・コピー | `S` / 右クリックで分割 (前半の繰り返しは外す)、コピーした音楽は現在位置から鳴らして貼り付ける |
| 最後に黒へフェードアウト | `Storyboard.EndFadeSeconds`。最後のクリップの映像・文字・音声にフェードアウトを付ける |
| 余白の色 | `Storyboard.Background`。決めたときだけレイヤー 0 に背景の図形を置き、ほかのレイヤーを 1 つずつ上げる (決めていないプロジェクトの変換結果は変わらない) |
| 画面いっぱい | `StoryClip.Fill`。画面に収める代わりに、はみ出た所を切って画面を埋める |
| キャプション・タイトルの文字 | 大きさ (`TextScale` %)・フォント・帯 (`filter.backplate`、縁取りの代わり)。キャプションは出し始め・出す長さを決められ、途中で出る / 消えるときは 0.25 秒でフェードする |
| エンドロール | タイトルの動き「下から上へ流す」(`ClipMotion.ScrollUp`)。「終わりのタイトルの追加」は最後に入れる |
| 音楽の繰り返し | `MusicClip.Loop`。映像の終わりまで鳴らし、使う範囲の長さごとに音声オブジェクトを並べる (音声オブジェクトの「ループ再生」は素材の全体を繰り返すので使わない)。フェードは最初の入りと最後の出だけ |
| ダッキング | `MusicClip.Duck`。音楽に「自動音量調整」(`audio.duck`) を付け、動画の音声が鳴っている間は小さくする |
| 音の高さを保つ | `StoryClip.KeepPitch`。速度を変えた動画の音声に「ピッチ変更」で `-12 log2(速度)` 半音を付けて戻す (±24 半音まで。0.25〜4 倍なら元どおり) |
| 写真の向き | 読み込むときに EXIF の向きを見て「回転」を付ける (描画は EXIF を使わないため) |
| 落とした位置 | 音声ファイルをストーリーボードに落とすと、落とした位置の時刻から鳴らす (`StoryboardStrip.SecondsAt`)。メニューから追加したときは現在位置から |
| 音楽の吸着 | 音楽をドラッグすると、始まりか終わりが作品の先頭・クリップの境目・映像の終わり・現在位置の 8 ピクセル以内なら、そこに合わせる (点線で示す)。`Alt` を押している間は合わせない |
| 前後のクリップ | `Alt+←` / `Alt+→` で前 / 次のクリップを選び、現在位置をその始まりに移す (`StoryboardEditor.SelectAdjacent`)。何も選んでいなければ、現在位置のクリップから |
| 時間の入力 | 時刻・長さの欄 (`NumberBox.TimeFormat`) は「分:秒」(1 時間以上は「時:分:秒」) で表示し、「1:23.5」でも「83.5」でも入れられる。切り替え・フェードの長さなど短いものは秒のまま |
| 取り込み直し | 詳細編集で変えたあと「今の内容から取り込み直す」と、シンプル編集で作ったオブジェクト (ID が `sb` で始まる) は前のストーリーボードのクリップ・音楽の設定 (キャプション・タイトル・切り替え効果・繰り返しなど) を引き継ぎ、長さ・位置・文字だけを取り込む。文字のオブジェクトは、まだ文字のないクリップのうち最も長く重なるもののキャプション (タイトルならその文字) にする。繰り返す音楽の続きは 1 つにまとめる |
| 切り替えが短くなったとき | 前後のクリップの長さの半分までに抑えて指定より短くなったときは、右の欄に実際の長さを出す |
| 見つからない素材 | ストーリーボードのクリップ・音楽に赤い斜線と「ファイルが見つかりません」を出し、右の欄に「素材の付け替え...」を出す。ファイルがあるかどうかは 3 秒まで覚えておく (`SimpleMedia.Exists`) |

写真・動画は、画面に収まる大きさに自動で合わせる。何もないプロジェクトに最初の動画を入れると、
プロジェクトの画像サイズとフレームレートを動画に合わせる (詳細編集と同じ)。

#### 22.21.2 しくみ

- モデル: `Project.Storyboard` (`Core/Storyboard/Storyboard.cs`)。クリップ (`StoryClip`) の並びと音楽 (`MusicClip`)。
  詳細編集だけのプロジェクトでは `null` (保存もしない)。
- 描画・出力・保存・取り消しは、これまでと同じシーン (タイムラインのオブジェクト) の仕組みをそのまま使う。
  シンプル編集の操作 (`Editing/StoryboardEditor.cs`) は、ストーリーボードを書き換えたあと
  `StoryboardCompiler.Apply` でメインのシーンのオブジェクトを作り直し、これを `EditorSession.Edit` の 1 回にまとめる。
  ストーリーボードもプロジェクトの複製・取り消しの対象に入れた。
- 変換 (`StoryboardCompiler`) は入力だけで決まる。オブジェクトの ID もクリップの ID から作るので、
  同じストーリーボードからは同じオブジェクトができる。

  | レイヤー | 置くもの |
  |---|---|
  | 1・2 | 映像 (動画・写真・タイトルの背景)。隣り合うクリップを交互に置き、切り替えの間だけ重ねる |
  | 3・4 | 文字 (タイトル・キャプション)。切り替えに合わせてフェード |
  | 5・6 | 動画の音声 (映像と連結)。切り替えに合わせてフェード |
  | 7〜10 | 音楽 (重なるものは別のレイヤー) |

- 速度: `StoryClip.Speed` (倍)。動画オブジェクトと音声オブジェクトの「再生速度」(%) にそのまま入れる。
  上限は音声の再生速度の上限に合わせて 8 倍。分割・開始 / 終了の設定では、画面の上の時間に速度を掛けて素材の時間に直す
  (同じ絵の所で切れる)。詳細編集から取り込むときは、動画の再生速度を引き継ぐ。
- 切り替え効果: 重なる長さは、どちらのクリップの長さの半分も超えないようにする (3 つのクリップにまたがらない)。
  後ろのレイヤーほど手前に描かれるので、入ってくるクリップが手前 (レイヤー 2) ならその入りに「トランジション」をかける。
  奥 (レイヤー 1) なら、出ていく手前のクリップの出に、向きを逆にした種類 (ワイプ・スライドの左右・上下) をかけて同じ見え方にする。
- 詳細編集との行き来: 詳細編集に切り替えると、作られたシーンをそのまま編集できる。シンプル編集に戻ったとき、
  シーンがストーリーボードから作ったものと違えば (`StoryboardCompiler.IsInSync` が、作り直したものと比べる)、
  編集画面の代わりに案内を出し、「シンプル編集の内容に戻す」「今の内容から取り込み直す」「詳細編集に切り替える」を選ばせる。
  詳細編集で作ったプロジェクトは「シンプル編集に取り込む」(`StoryboardImport`) で、並んだ動画・写真と音楽だけを取り込める。
- 素材の付け替え・収集 (`ProjectMedia`) は、シーンとストーリーボードの両方のファイルを一緒に書き換える
  (付け替えたあとも、シーンはストーリーボードから作ったままになる)。

#### 22.21.3 画面の構成 (App)

- `Shell/MainWindow` はメニュー・ツールバー・保存の確認・ほかのアプリからのドロップなど、編集画面によらない部分だけを持ち、
  中央に今の編集画面 (`IWorkspace`) を置く。プレビューと再生バー (`PreviewArea`) は共通で、有効な編集画面が自分の中に置く。
- `IWorkspace` は「編集」メニュー・ツールバーの中身と、切り取り・削除・分割・ファイルの読み込み・ドロップなどの操作を受け持つ。
  ショートカット (`Shortcuts`) の切り取り・削除などは今の編集画面に渡す。キーフレーム・出力範囲・テンプレートなど
  詳細編集だけの操作は `CommandInfo.Mode` で詳細編集のときだけ効く (同じキーを、もう一方の編集画面の別の操作に割り当ててよい)。
- `Workspaces/Advanced` は従来の画面 (タイムライン・プロパティ・ウィンドウの分離 / ドッキング)、
  `Workspaces/Simple` はストーリーボード (`StoryboardStrip`)・クリップの設定 (`ClipPanel`)・素材の読み込み (`SimpleMedia`)。


### 22.22 パフォーマンスチェックモード

プレビューの速さや出力 (エンコード) にかかる時間を、画面からかんたんに確かめるためのモード。
詳細編集・シンプル編集のどちらでも使える (プレビューと数値の集め方は共通)。

- オン / オフ: 「表示」→「パフォーマンスチェック」、ツールバー右の (i) ボタン、`Ctrl+Shift+P`。設定に覚える。
- オンの間は、プレビューの左上に小さな表示を重ねる: 直近の 1 フレームの描画時間と 95 パーセンタイル、
  表示 FPS (再生中は目標の FPS と、飛ばしたフレームの割合)、描画の大きさと CPU / GPU。
  描画時間は、1 フレームに使える時間 (1000 ÷ fps) の 70% 以下なら緑、以下なら黄、超えたら赤。クリックで詳細を開く。
- 詳細の窓 (`Views/PerformanceWindow`): 閉じても隠すだけ (モードはオンのまま)。
  | 欄 | 内容 |
  |---|---|
  | プレビュー | 描画方式・描画の大きさ・1 フレームの描画時間・平均 / 95% / 最大・表示 FPS・直近 240 フレームのグラフ (点線は 1 フレームに使える時間) |
  | 再生 | 飛ばしたフレームの割合 (絵を出すたびに、前に出したフレームからいくつ飛んだかを数える)・音声 1 回ぶんのミックス時間と、その長さ |
  | ベンチマーク | 今のシーンを現在位置から、プレビューと同じ大きさ・描画方式で決めた数だけ続けて描いて測る (`RenderBenchmark`)。最初の 1 フレームは準備を含むので数えない |
  | 出力 | 直近 3 回の出力 (出力中なら途中経過も): 合計時間・速さ (fps)・実時間との比、描画・変換 (YUV)・音声のミックス・エンコード・エンコード待ちの内訳 |
  | メモリ | プロセスの使用量と .NET の管理メモリ |

  「結果をコピー」で、環境 (OS・CPU 数・描画方式) と数値をまとめた文字をクリップボードに入れる (不具合の報告などに使う)。
- 出力の内訳 (`ExportStats`) は `Exporter.Run` が `ExportRequest.Stats` に書く。描画・変換・音声は描画のスレッド、
  エンコードはエンコードのスレッドで同時に進むので、割合を足すと 100% を超えることがある。
  「エンコード待ち」(描画したフレームを渡すときに待った時間) が大きければ、エンコードが遅い。
  モードがオンなら、出力ダイアログの完了の表示にも内訳を出す。
- 集めるのはモードがオンのときだけ (`PerformanceMonitor`)。プレビューの描画のスレッド・音声のスレッドからは、
  時間を測って直近の値を覚える (`RollingStats`) だけにして、画面の更新は 1 秒に 4 回にまとめる。


### 22.23 使う GPU の選択 (CPU 内蔵 + 外付け)

ノート PC などで CPU 内蔵の GPU と外付けの GPU (NVIDIA など) の両方があると、これまでは EGL の既定のディスプレイ
(デスクトップの表示に使っている GPU。多くは CPU 内蔵) で描いていたため、遅い GPU が使われていた。

- GPU の一覧: EGL のデバイス列挙 (`EGL_EXT_device_enumeration` + `EGL_EXT_platform_device`) で GPU を 1 つずつ開き、
  `GL_RENDERER` の名前・DRM のデバイスファイル (`EGL_EXT_device_drm`)・レンダーノードを調べる (`GpuContext.Adapters`)。結果は覚えておく。
  ソフトウェアのもの (llvmpipe・`EGL_MESA_device_software`) も一覧に入るが、自動では選ばない。
- 環境設定の「使う GPU」: 自動 (外付けの GPU を優先) か、一覧の GPU を選ぶ。設定には `名前|デバイスファイル` を覚え、
  デバイスファイルの番号が変わっても同じ名前の GPU を選ぶ。見つからなければ自動。環境変数 `VIDEO_BUILDER_GPU` (名前の一部でもよい) が優先。
- 自動の優先度 (`GpuAdapter.Score`): NVIDIA (プロプライエタリ) > AMD Radeon RX / Pro・Intel Arc > そのほか > nouveau の NVIDIA は少し下 >
  CPU 内蔵 (Intel の UHD / Iris・AMD Radeon Graphics) > ソフトウェア。
- 開き方: 選んだ GPU のデバイスのディスプレイで、pbuffer → サーフェスなし (`EGL_KHR_surfaceless_context`) の順に試す。
  開けなければ、これまでどおりデスクトップのディスプレイ → 画面なしの Mesa。デバイス列挙がない環境・ソフトウェアしかない環境もこれまでどおり。
- プレビュー・出力・ベンチマークは、どれも `GpuContext.TryCreate` を通るので、同じ GPU を使う。
- 動画のデコードも合わせる (`HardwareDecoding.DeviceHint`): NVIDIA を選んだら NVDEC (CUDA) を先に、Intel / AMD を選んだら
  VAAPI を先に、その GPU のレンダーノードで開く (指定しないと VAAPI は既定のデバイス = 多くは CPU 内蔵になる)。
  選び直したら、デコードのデバイスを作り直す (`HardwareDecoding.ResetDevices`)。
- 実機 (内蔵 + NVIDIA) では確かめていない。この環境では Mesa のソフトウェアの GPU で、列挙と、ID を指定して開けることを確かめた。

### 22.24 高速化 (その 4): 縮めたデコード・先読み・シーンオブジェクト

| 手法 | 内容 |
|---|---|
| 縮めたデコード | プレビュー (Draft) では、動画を画面に出る大きさに足りる範囲で縦横 1/n (n = 1〜8) に縮めて BGRA へ変換する (`VideoReader.OutputDivisor`)。要る細かさ (`RenderEngine.Detail`) は、プレビューの縮小率 × 拡大率 × グループ制御の拡大率 × 手前に出した分 (Z)。カメラ制御があるとき・ピクセル単位で動くフィルタ (ぼかしなど) があるときは縮めない。色だけ・不透明度や位置だけを変えるフィルタ (色調補正・単色化・フェード・トランジションのフェード / スライド / ズーム) は、縮めた絵にそのままかける (プロキシの絵も同じ) |
| 次のフレームの先読み | 再生中 (`PreviewService`) と出力 (`Exporter`) では `RenderEngine.Lookahead` を true にする。描画のはじめに今のフレームの動画をすべて取り出してから、同じデコーダで次のフレームのデコードと変換を裏で始め、合成と同時に進める。次の描画のはじめ (`MediaCache.BeginFrame`) で終わるのを待つ。入れ子のシーンの中の動画は、次に描くフレームが 1 つ先とは限らないので先読みしない |
| フレームの置き場を 2 つ | `VideoReader` は変換済みのフレームの置き場を 2 つ持ち、交互に使う。先読みの間は今のフレームの置き場を押さえる (`PinCurrentFrame`) ので、CPU 描画で複製せずに使っている絵 (zeroCopy) が合成中に書き換わらない |
| シーンオブジェクトを表示の大きさで | 入れ子のシーンを、プロジェクトの大きさではなく Detail (1/8 刻みに切り上げ) の大きさで描く。中の動画の細かさも、その大きさで決める |
| シーンオブジェクトの使い回し | 中身が前と同じなら描き直さない。鍵はシーン・大きさ・その時刻に出ているオブジェクト (の実体。編集すると別の実体になる)・レイヤーの表示・画像ファイルの更新日時。中がすべて止まっているもの (画像・図形・アニメーションなしのテキスト・制御・結果が入力だけで決まるフィルタで、動かないパラメータだけ) ならフレームを問わず、そうでなければ同じフレームのときだけ使い回す |

測った結果 (4 コアの VM、GPU なし、4K の H.264 をソフトウェアでデコード):

- この環境では 4K のデコードそのものに 1 フレーム約 26 ms かかり (デコーダが 4 コアを使い切る)、BGRA への変換は 1 ms 未満だった。そのため、プレビュー (960x540) の速さは 1 フレーム約 28〜32 ms → 約 25〜27 ms の改善にとどまる (縮めた絵を合成する分が軽くなる)。
- 軽い 4K 素材 (デコードが速いもの) では、出力が 9.3 → 6.6 ms / フレーム、テキスト 6 つ (縁取り付き) のシーンオブジェクトを重ねると 15.2 → 10.6 ms / フレーム (先読みで、デコードと合成が重なる)。
- 先読みが効くのは合成 (フィルタなど) に時間がかかり、デコードにコアが余っているとき。ハードウェアデコード (GPU からの写し取り + 変換) のときも、裏で進む。

### 22.25 プロキシの作成を速く・使い始めを早く、GPU 描画の複製を減らす

実機で、1 時間ほどの 2K の動画のプロキシを作るのに 10〜20 分かかっていた。4 コアの VM で 60 秒の 2560x1440 H.264 を測ると、
作成に 18.1 秒、デコードだけで 12.7 秒 (ソフトウェア) かかっていた。デコードが作成時間の大半なので、ソフトウェアではこれ以上大きくは縮まない。
そこで「作り終わるのを待たずに使える」ようにし、デコードを GPU でもできるようにした。

| 手法 | 内容 |
|---|---|
| 区切りごとのプロキシ | 20 秒ごとのファイルにして、書き終えた区切りからプレビューで使う (`ProxyStore.FindSegment`)。途中でやめても (アプリを閉じても) できた区切りは残り、次は残りだけを作る |
| 今見ている位置から作る | 次に作る区切りを決めるたびに、現在位置での再生位置を尋ね (`ProxyService.UpdateFocus`)、その区切り (と、その先 2 つ) がまだなら先に作る。順番待ちの動画のうち、現在位置に置いてあるものを先に作る |
| 区切りの境目 | 区切りには、始まりより前の最後のフレームを頭に入れる (境目ちょうどにフレームがない動画でも、区切りの始まりの時刻を読める)。区切りは元の動画と同じ時刻で書き、`VideoReader(path, absoluteTime: true)` で読む。ループの長さや縮小率は元の動画の情報で決める |
| 同時に進める | デコード (呼び出したスレッド) と、縮小・MJPEG のエンコード・書き込み (別のスレッド) を、待ち行列 (8 フレーム) でつないで同時に進める |
| ハードウェアのデコード | 再生と同じ設定で、使える GPU があれば GPU でデコードする (`HardwareDecoding.Open` を `VideoReader` と共用)。途中で失敗したら、その区切りをソフトウェアで作り直す |
| デブロッキングの省略 | ソフトウェアのデコードでは、参照されないフレーム (B フレームなど) のデブロッキングを省く。後のフレームには響かない (画質のずれが積み重ならない) |
| 自動作成の判断 | 4K・H.265 など (`IsHeavy`) はすぐ作る。それ以外は裏で約 0.6 秒デコードして測り (`ProxyStore.MeasureDecode`、ハードウェアのデコードが使えればそれで)、1 秒にデコードできるフレームが動画の fps の 1.5 倍に足りないか、キーフレームの間隔が 4 秒を超える 720p 以上の動画なら作る (`NeedsProxy`) |
| GPU 描画で動画を直接送る | GPU 描画では、デコーダの置き場から直接 GPU のテクスチャへ送る (`MediaCache.Upload`。以前はメモリ上で複製してから送っていた)。送るのは `Flush` の中で終わるので、そのあと置き場が書き換わってもよい。並列にデコードするときも、送るのは描画のスレッドで行う |
| プレビューの表示 | 描画のスレッドが描いたフレームを、画面の描画 (`ICustomDrawOperation`) でそのまま描く (`PreviewService.DrawFrame`)。以前は UI スレッドで毎フレーム `WriteableBitmap` に写していた。GPU の画面なら、ロックを放す前に GPU へ送り終える |

測った結果 (4 コアの VM、GPU なし、60 秒の 2560x1440 H.264):

| | 時間 |
|---|---|
| 以前のプロキシ作成 | 18.1 秒 |
| 新しいプロキシ作成 (ソフトウェア) | 15.8 秒 (ffmpeg コマンドで同じものを作るのと同じ 15.5 秒) |
| 最初の区切り (20 秒ぶん) が使えるまで | 5.8 秒 |
| デコードだけ (参考) | 12.7 秒 |

- GPU (NVDEC・VAAPI など) でデコードできる PC では、デコードの時間が大きく縮むはず。この環境には GPU がないので測れていない
  (ハードウェアでの失敗からの作り直しは、テスト用の切り替えで確かめた)。
- GPU 描画での複製の削減は、Mesa のソフトウェアの GPU で、CPU 描画と同じ絵になることを確かめた。実際の GPU での速さは測れていない。
- 描いたフレームを GPU から読み戻す処理 (`ReadPixels`) と、Avalonia が画面へ送る処理は残っている。なくすには、描画と画面で GPU のテクスチャを共有する必要がある。

### 22.26 高速化 (その 5): フィルタオブジェクト・出力・テキストアニメーション

| 手法 | 内容 |
|---|---|
| フィルタオブジェクトで画面を複製しない | CPU 描画では、効果に渡す画面を、複製せずに画面のピクセルを指す画像にする (効果は渡された絵を書き換えず、新しい絵を返す)。効果が何も変えなければ描き戻さない |
| 描き戻しを並列に | 画面と同じ大きさ・同じ位置の結果は、Skia の描画 (1 スレッド) ではなく行ごとの並列コピーで描き戻す (`CopyOver`) |
| 出力の色変換を複数スレッドで | BGRA → YUV の変換を、FFmpeg の中で帯に分けて複数スレッドで行う (`sws_scale_frame` + `threads`)。描画したフレームは複製せず、解放しないバッファとして包んで渡す。1 スレッドのときとまったく同じ結果になる |
| 連番 PNG を並列に | 描画はこのスレッドで順に、PNG のエンコードと書き込みは複数のスレッドで同時に行う |
| テキストの字の並びを使い回す | 使う書体 (システムのフォントの検索・足りない字の代わりの書体) と字ごとの位置を、文字と書式ごとに覚えておく (`Generators.TextLayout`、64 個まで)。テキストアニメーション中は毎フレーム字を描くだけにする。描く先は使い回しのバッファ。結果は以前とピクセル単位で同じ |
| Lottie の同じフレームを描き直さない | オブジェクトごとに直前の絵を覚えておき、同じ時刻・大きさなら使い回す (止まって編集している間)。絵はほかと同じ BGRA で作る |

測った結果 (4 コアの VM):

| | 以前 | 今回 |
|---|---|---|
| 4K・左右反転のフィルタオブジェクト | 13.1 ms | 9.7 ms |
| 4K・ぼかしのフィルタオブジェクト 2 つ | 95.1 ms | 77.1 ms |
| 4K 出力の色変換 (1 フレーム) | 28.4 ms | 10.1 ms |
| 1080p 連番 PNG | 8.6 fps | 14.8 fps |
| テキストアニメーション 3 つ (1080p) | 8.49 ms | 1.10 ms |

その後の追加:

| 手法 | 内容 |
|---|---|
| 自前のぼかし | CPU 描画のガウスぼかしを、横 → 縦の 2 回に分けて、行ごとに複数スレッド・SIMD で計算する (`PixelBlur`)。重みは合計 256 の 8 ビット整数 (端数の大きいものから足して形を崩さない) なので、16 ビットで足し合わせられ、16/32 バイトずつまとめて計算できる。画面全体のぼかしでは端のピクセルを延ばす (以前の Skia の結果は四隅が薄くなっていた)。GPU 描画は Skia のまま |
| プレビューの拡大 | 描画の中の拡大は、プレビュー (Draft) では以前からバイリニア (出力はバイキュービック)。画面に表示するときの拡大も、止まっているときを含めていつもバイリニアにした (縮小して表示するときだけ、止まっている間はなめらかに縮める) |

| | 以前 | 今回 |
|---|---|---|
| 4K・ぼかしのフィルタオブジェクト | 57 ms | 28 ms |
| 4K・ぼかしのフィルタオブジェクト 2 つ | 77 ms | 34 ms |
| 1080p の動画を 200% で映す 4K: プレビュー (表示の大きさ / 4K のまま) | – | 3.6 ms / 14.7 ms |
| 同じもの: 出力 (バイキュービック) | – | 87.7 ms |

### 22.27 高速化 (その 6): 縁取り・ドラッグ・出力の拡大

| 手法 | 内容 |
|---|---|
| 縁取りをアルファだけで | CPU 描画では、元の絵のアルファ (1 チャンネル) を太らせ (正方形の範囲の最大値。横 → 縦、SIMD)、ぼかして (`PixelBlur.GaussianAlpha`)、縁取りの色で塗った上に元の絵を重ねる (`Effects.BorderOnCpu`)。Skia の画像フィルタは、色の付いた絵全体を太らせてぼかしていた。ぼかしなしは Skia と同じ絵 |
| ドラッグ中は動かすものだけ比べる | `EditorSession.NotifyChanged(changed)` で、書き換えたオブジェクトを知らせる。プレビュー用の複製では、知らされたものだけを比べ、ほかは「編集前」の複製を使う。知らせのない `NotifyChanged()` があればすべて比べる。確定ではいつもすべて比べるので、取り消しの履歴には影響しない。プレビューとタイムラインのドラッグで使う |
| 出力の拡大を自前で | 出力 (CPU) で、回転も遠近もない拡大を高品質で描くときは、自前のバイキュービック (Mitchell、Skia の高品質と同じ種類) で、画面にかかる範囲だけを作ってから等倍で重ねる (`PixelResample`)。横 → 縦の 2 回、1 ピクセルの 4 成分をまとめて SIMD、行ごとに並列。端のピクセルはかかっている割合だけ薄くする (アンチエイリアス)。不透明度もここで掛ける (丸めを 1 回にして、暗くならないように)。小さいもの (10 万ピクセル未満)・回転などは Skia のまま |

測った結果 (4 コアの VM。どれも準備運転のあとで測った):

| | 以前 | 今回 |
|---|---|---|
| 大きさが動く 1080p のテロップ + 縁取り (太さ 12・ぼかし 8) | 22.0 ms | 8.3 ms |
| オブジェクト 3000 個・ドラッグ中のマウスの移動 1 回 | 2.5 ms | 0.2 ms |
| 1080p の動画を 200% で映す 4K の出力 (1 フレーム) | 77.3 ms | 36.8 ms |

- 出力の拡大は、Skia の結果と平均 0.000・最大 1 の差 (不透明度 100% のとき)。不透明度を下げたときも、全体の明るさのずれは 0.05 未満。
- 測るときの注意: .NET は最初に動かすコードを簡単にしか最適化しないので、数十フレームだけ測ると新しい処理ほど遅く見える (22.24 以前の一部の数値にもこの影響がある)。

### 22.28 GPU 描画のプレビューを読み戻さずに表示する

以前は GPU で描いた絵を毎フレーム読み戻し (`ReadPixels`。GPU の処理が終わるのを待ち、1080p で 8 MB を写す)、
それを Avalonia が画面の描画のためにまた GPU へ送っていた。

| 項目 | 内容 |
|---|---|
| 仕組み | Avalonia の画面の描画 (コンポジタ) が OpenGL でテクスチャの共有に対応していれば (`IOpenGlTextureSharingRenderInterfaceContextFeature`)、それと共有する GL コンテキストを作り (`SharedPreviewTarget`)、描画のスレッドの Skia をそのコンテキストで動かす (`GpuContext.FromExternal`)。共有のテクスチャ (最大 3 枚を順に使う) に直接描き (`RenderEngine.RenderToGpuSurface`)、`CompositionDrawingSurface` に渡して、`PreviewView` の子のビジュアルとしてフレームの位置に出す。読み戻しも、メモリ上の複製も、送り直しもない |
| 同期 | 描き終えたら GPU の処理が終わるのを待ってから (`Flush(submit, synchronous)`) 画面に渡す。画面に出ている・出す途中のテクスチャには描かない (次の絵に切り替わったら空く) |
| 選択の枠 | 子のビジュアルはコントロール自身の描画より上に出るので、選択の枠は別の層 (`PreviewView.Selection`) に描いて上に重ねる |
| 使う条件 | Linux (Windows は Direct3D 11 の共有テクスチャで。[22.35](#2235-windows-の-gpu-描画-angle))・描画方式が GPU (自動を含む)・画面の GPU と描画に選んだ GPU が同じ (`GL_RENDERER` で比べる。違う GPU の間では共有できないので、これまでどおり読み戻す)・自動のときは画面の GPU がソフトウェアでない。`--screenshot` のときは使わない (ウィンドウを画像に描く方法では写らないため)。環境変数 `VIDEO_BUILDER_SHARED_PREVIEW=0` で使わない、`=1` で条件を問わず使う |
| 表示 | 使っているときは、描画方式の説明に「画面と共有」と出る。使える環境なのに使わないときは理由を添える |
| 使えないとき | ヘッドレス・Vulkan・ソフトウェアの画面の描画など。これまでどおり読み戻して `PreviewView` が描く (UI のテストはこちらを通る) |

確かめたこと: Xvfb + Mesa のソフトウェアの GL (llvmpipe) で Avalonia の画面の描画を OpenGL にして (検証用に llvmpipe の除外を外して起動)、
共有のテクスチャで出した画面と、読み戻して出した画面を比べて同じ (RMSE 0.00003) こと、再生中にウィンドウの大きさを 2 回変えても
表示が続き、正しい向き・位置であること。実際の GPU での速さは測れていない。

### 22.29 音声の波形を少しずつ出す・プロキシを小さくする

| 項目 | 内容 |
|---|---|
| 波形を少しずつ出す | 波形は音声を全部読み終えるまで出なかった。計算しながら、できた所までをタイムライン・シンプル編集に出す (`WaveformCache.Peaks.Ready`、1 秒に 4 回まで更新)。まだの所は描かない。保存した波形を読むときは一度に出す |
| 波形を同時に計算 | 1 本ずつだったのを、コア数の半分 (最大 4) のファイルで同時に計算する |
| プロキシの画質の渡し忘れ | MJPEG のエンコーダに、画質 (量子化 4) をフレームごとに渡していなかった。渡さないと 0 (いちばん細かい) として扱われ、ffmpeg のコマンドで同じ設定にしたときより 3〜5 割大きかった。フレームごとに渡すようにした |

| | 以前 | 今回 |
|---|---|---|
| プロキシ (2560x1440 → 1280x720) の大きさ・1 分あたり | 106 MB (ノイズの多い動画は 133 MB) | 74 MB (80 MB) |
| 画質 (元の動画を縮小したものとの PSNR) | 29.18 dB | 29.14 dB (見分けられない) |

試して入れなかったもの:
- 動画ファイルの音声を読むとき、映像のストリームを読み飛ばす設定 (`AVDISCARD_ALL`): この環境のディスクでは速くならなかった (0.40〜0.47 秒 → 0.59 秒)。
- H.264 のプロキシ: キーフレームの間隔を 15 にすると 4 割ほど小さくなるが、シークのたびに前のキーフレームからデコードし直すので、プロキシの目的 (どこへでもすぐ移動できる) に合わない。すべてキーフレームにすると MJPEG と大きさが変わらない。

すでに作ったプロキシは大きいままなので、小さくしたいときは環境設定の「すべてのプロキシを削除」で作り直す。

### 22.30 クリップの切り替わりで止まらない・プロジェクトを開いたときに止まらない

| 項目 | 内容 |
|---|---|
| これから始まるクリップを先に開く | 再生・出力 (先読みがオンのとき) は、1 秒以内に始まる動画のクリップのデコーダを裏で開き、最初のフレームまでデコードしておく (`MediaCache.Preload`、同時に 2 つまで)。動画の途中から始まるクリップは、始まった瞬間にデコードすると前のキーフレームから読むので時間がかかっていた。開き終えるまでは誰にも貸さず、待ちもしない (間に合わなければ、これまでどおりその場で開く)。前のクリップのデコーダを読み進めれば届くとき (分割したクリップの続き) は開かない |
| プロキシが要るかを 1 本ずつ裏で調べる | プロジェクトを開いたとき、動画ごとのファイルの情報を UI スレッドで調べ (1 本 6 ms)、デコードの速さの計測を全部同時に始めていた。同時に測ると CPU を取り合って遅く測れ (19 本で、1 本だけのときの 4 分の 1 ほど)、要らないプロキシまで作るおそれがあった。裏のスレッド 1 本で、順に調べて測るようにした (`ProxyService.EnqueueIfNeeded`) |

| | 以前 | 今回 |
|---|---|---|
| 1080p の動画の 2 秒目から始まるクリップへの切り替わり (1 フレーム) | 78〜130 ms | 7〜8 ms |
| 動画 50 本のプロジェクトを開くときに UI が止まる時間 (ファイルの情報) | 約 300 ms | ほぼ 0 |
| デコードの速さの計測 (19 本) | 同時に 3 秒、1 本あたり 124〜290 fps と測れた | 1 本ずつ (約 0.25 秒 × 本数)、正しく 549 fps 前後 |

プロキシを作っている間 (CPU を多く使う) に測ると、まだ遅めに測れることがある。


### 22.31 FFmpeg 9 への移行

`FFmpeg.AutoGen` を 6.1.0.1 から **9.0.1.1** に上げ、FFmpeg 9 (libavcodec.so.63) を読み込むようにした。主な目的はアニメーション WebP の読み込み (FFmpeg 9.0 で追加)。ほかに、7.0〜9.0 で入った不具合・脆弱性の修正、VVC / MV-HEVC / APV / ProRes RAW の読み込み、D3D12VA・Vulkan・AMF まわりの改善が使えるようになる。

| 変更 | 内容 |
|---|---|
| エンコーダの対応形式 | `AVCodec.pix_fmts` / `sample_fmts` は 7.1 で非推奨、8.0 で削除された。代わりの `avcodec_get_supported_config` を使う (`CodecSupport`) |
| 拡大縮小の方式 | `ffmpeg.SWS_BILINEAR` などの定数が `SwsFlags` の列挙に移った。引数は int のままなので、キャストして渡す |
| 読み込むライブラリ | 探すファイル名をバインディングの `LibraryVersionMap` から決める (`libavcodec.so.63` / `avcodec-63.dll` / `libavcodec.63.dylib`)。見つからないときの文言も FFmpeg 9 にした |
| 音声のチャンネル | すでに新しい `AVChannelLayout` (`ch_layout`) を使っていたので、変更なし |

確かめたこと (FFmpeg 9.0.2 を ffmpeg.org のソースから共有ライブラリでビルドして使った):
- テストはすべて通る (VideoBuilder.Tests 194、VideoBuilder.App.Tests 45)。1 回目だけ `PreviewSpeedTests.A_clip_starting_soon_is_opened_ahead_and_the_cut_shows_the_same_frames` が落ちたが、単独で 5 回・全体でさらに 2 回流して再現しなかった。負荷が高いときに先読みが間に合わない、時間に依存するテスト。
- アニメーション WebP は `MediaInfo.Probe` で映像 (`webp_anim`、10 fps) として読める。

まだのこと:
- ~~アニメーション WebP を動画として扱う~~ → [22.32](#2232-アニメーション-webpgif-を動画として読む) で対応した。
- ~~配布物への同梱・Flatpak・ライセンスの表示~~ → [22.33](#2233-ライセンスと-ffmpeg-の同梱) で対応した。


### 22.32 アニメーション WebP・GIF を動画として読む

アニメーションする WebP・GIF を、動画オブジェクトとして読み込む。アニメーションしないものは、これまでどおり画像オブジェクトになる。

| 項目 | 内容 |
|---|---|
| 見分け方 | ファイルの先頭を読む (`AnimatedImageFiles`、Core)。WebP は拡張 (VP8X) チャンクのアニメーションの印、GIF は画像が 2 枚以上あるか。`ObjectFactory.DefinitionForFile` で使うので、タイムライン・シンプル編集のどちらから読み込んでも同じになる。動画オブジェクトのファイル選択でも選べる |
| 長さ | アニメーション WebP (`webp_anim`) は入れ物に長さが入っていないので、パケットを最後まで数えて求める (`AnimatedImageMedia.ScanDuration`)。GIF は FFmpeg が長さを出す |
| シーク | `webp_anim` はシークすると末尾まで進んでしまう (索引がない)。前へ戻るときや 2 秒より先へ飛ぶときは、ファイルを開き直して先頭からデコードする。小さなファイルなので速い |
| 透明な部分 | アニメーション画像 (`webp_anim` / `gif` / `apng`) のときだけ、`VideoReader` がアルファを掛けた BGRA を返し (`HasAlpha`)、描画は Premul として扱う。下のレイヤーが透けて見える。ほかの動画はこれまでどおり不透明 (アルファを掛ける処理が増えないように) |
| プロキシ | 作らない (`MediaInfo.IsAnimatedImage`)。小さく、MJPEG のプロキシにすると透明な部分がなくなるため |
| ループ | 動画オブジェクトの「ループ再生」をそのまま使える |

テスト (`AnimatedImageTests`): 素材は `tests/VideoBuilder.Tests/Assets` の 32x24・4 フレーム (左半分が赤→緑→青→黄、右半分が透明) の WebP (可逆) と GIF。どの順で取り出しても正しいフレームになること、透明な部分から下のレイヤー (白) が見えることを確かめる。描画を不透明のまま (`SKAlphaType.Opaque`) にすると、透明な部分が黒になってテストが落ちることも確かめた。

- アルファ付きの動画 (ProRes 4444・VP9 のアルファなど) の透明な部分は、まだ使っていない。必要になったら `HasAlpha` を広げる (4K ではアルファを掛ける処理の速さに注意)。


### 22.33 ライセンスと FFmpeg の同梱

| 項目 | 決めたこと |
|---|---|
| 本体のライセンス | **LGPL-3.0-or-later** (`COPYING.LESSER`・`COPYING`)。v2.1 にしないのは、同梱するアイコン (Material Symbols) が Apache-2.0 で、GPL/LGPL の v2 と両立しないため |
| 同梱する FFmpeg | **9.0.2 を libx264・libx265 入りで作る** (`--enable-gpl --enable-version3`。GPL-3.0-or-later のビルド)。ほかに libvpx・dav1d・Opus |
| 配布物のライセンス | GPL の FFmpeg と一緒に動く形で配るので、**全体を GPL-3.0-or-later の条件で配る**。本体のソースは LGPL-3.0-or-later のまま (GPL-3.0 と両立する) |
| 表示 | 「バージョン情報」に本体のライセンスと、読み込んだ FFmpeg のバージョン・ライセンス (`avcodec_license`) を出す。一覧は `THIRD_PARTY_NOTICES.md`。配布物には `licenses` に条文と FFmpeg の configure の設定を入れる |

FFmpeg の作り方は `packaging/ffmpeg/build.sh` にまとめた (バージョンと SHA-256 を固定して ffmpeg.org から取る)。CI・AppImage・開発用はこれを使う。

| 配布の形 | FFmpeg |
|---|---|
| AppImage | `build.sh --bundle` で、FFmpeg と、それが使うコーデックのライブラリ (x264・x265・libvpx・dav1d・Opus・libnuma) を `usr/bin/ffmpeg` に写す。RPATH を `$ORIGIN` にするので、`LD_LIBRARY_PATH` なしで互いを見つける。libc や GPU のドライバ (libva など) は利用者の環境のものを使う。FFmpeg はその PC 向けにしか作れないので、arm64 の AppImage は arm64 の PC で作る |
| Flatpak | ランタイムを **Freedesktop 25.08** に上げた (23.08 はサポート終了)。25.08 では ffmpeg-full 拡張がなくなったので、x264・x265・FFmpeg 9 をマニフェストのモジュールとしてソースから作り、`/app/lib` に入れる。libvpx・dav1d・Opus はランタイムのもの。OpenAL Soft は 1.24.3 に上げた (1.23.1 は 25.08 の CMake 4 で configure できず、1.25.0 は GCC 15 で C++ モジュールのビルドに失敗する) |
| CI | `build.sh --prefix ~/ffmpeg` で作り、`actions/cache` に入れる (build.sh が変わったら作り直す) |

Flatpak と `build.sh` で configure の設定・FFmpeg のソースが食い違わないことを、テスト (`PackagingTests`) で確かめる。

注意すること:
- GPL の求めに従い、配布物を公開するときは、同梱したものと同じバージョンのソース (video-builder・FFmpeg・x264・x265) を同じ場所から入手できるようにする (リリースに添付する)。
- プラグインは同じプロセスで動くので、GPL の FFmpeg を同梱した配布物と一緒に配るプラグインは、GPL-3.0 と両立するライセンスにする。
- H.264・H.265 には特許がある (著作権のライセンスとは別の話)。配布する国や方法によっては確認が要る。
- Windows・macOS 向けの FFmpeg の同梱 → [22.34](#2234-windowsmacos-向けの-ffmpeg-の同梱)。


### 22.34 Windows・macOS 向けの FFmpeg の同梱

Linux と同じ FFmpeg (9.0.2、GPL-3.0、x264・x265 入り) を、Windows・macOS の配布物にも同梱する。FFmpeg はその OS の上でしか作れないので、GitHub Actions のそれぞれの OS のランナーで作る (`.github/workflows/package.yml`。手動・タグ・packaging を変えた PR で動く)。

| | Windows (x64) | macOS (arm64・x64) |
|---|---|---|
| 作る環境 | MSYS2 の UCRT64 (`windows-latest`) | Homebrew (`macos-latest`・`macos-15-intel`) |
| コーデックのライブラリ | MSYS2 のパッケージ (x264・x265・libvpx・dav1d・Opus) | Homebrew のもの |
| 同梱のしかた (`build.sh --bundle`) | FFmpeg の DLL と、`ldd` で見つかる MSYS2 の DLL (C:\Windows のもの以外) を写す。GCC の実行時ライブラリ・winpthreads・libva・zlib なども入る (22 個。CI で確かめた) | 参照先を `otool -L` でたどって集め、`install_name_tool` で `@loader_path` に書き換え、ad-hoc で署名し直す。Homebrew の X11 のライブラリを拾わないよう、`--disable-libxcb --disable-xlib --disable-sdl2` を足す |
| 読み込み | `FFmpegLoader` が `SetDllDirectory` で同梱のフォルダを足す。FFmpeg.AutoGen はフルパスで読むが、そこから読まれる DLL (x264 など) は、既定ではアプリのフォルダと PATH からしか探さないため | 変更なし (`@loader_path` で互いを見つける) |
| GPU | NVENC・NVDEC (ヘッダは nv-codec-headers 12.1)、AMF、D3D11VA・DXVA2 | VideoToolbox |

発行 (`publish-all.sh <RID>`) は、`artifacts/ffmpeg-bundle/<RID>` があれば、発行したフォルダの `ffmpeg/` に写す。`FFmpegLoader` は実行ファイルの隣の `ffmpeg` フォルダを最初に探す。ライセンスの文書は `licenses/` に入れる。

確かめ方 (package.yml): 同梱したものだけで動くことを確かめるため、Windows は MSYS2 の PATH を使わないシェルで、`VIDEO_BUILDER_FFMPEG` を同梱のフォルダにしてテストの一部 (`AnimatedImageTests`・`PackagingTests`・`FFmpegBuildTests`) を流す。macOS は、同梱したライブラリが同梱のもの・OS のもの以外を参照していないことも `otool -L` で確かめる。`FFmpegBuildTests` は、読み込んだ FFmpeg が GPL v3 のビルドで、libx264・libx265・libvpx・Opus・dav1d を持つことを確かめる (CI でも流す)。

NVENC のヘッダは、Linux (Ubuntu の `libffmpeg-nvenc-dev`)・Flatpak・Windows のすべてで 12.1 にした。13.x は新しいドライバ (570 以降) でないと NVENC が使えないため。

注意すること:
- macOS: Homebrew のライブラリは、ランナーの macOS 向けに作られている。そのため、配布物が動く macOS の下限は、ランナーの macOS のバージョンになる。古い macOS にも対応するなら、コーデックのライブラリも `MACOSX_DEPLOYMENT_TARGET` を指定して自分で作る必要がある。
- macOS の署名は ad-hoc だけ。配布するには、Developer ID での署名と公証 (notarization) が要る (Gatekeeper)。
- Windows・macOS で、アプリ全体の動作はまだ確かめていない (22.4)。確かめたのは、同梱した FFmpeg だけでの読み込み・アニメーション WebP のデコードと描画・エンコーダがあること (package.yml で 13 件のテストが通る) まで。
- Windows の arm64 は作っていない。

### 22.35 Windows の GPU 描画 (ANGLE)

Windows でも GPU で描画する。Linux と同じ「OpenGL ES + Skia」の作りのまま、OpenGL ES を ANGLE (Direct3D 11 の上で OpenGL ES を動かすライブラリ) で動かす。

| 項目 | 内容 |
|---|---|
| ライブラリ | Avalonia が画面の描画に使う ANGLE (`Avalonia.Angle.Windows.Natives` の `av_libglesv2.dll`)。描画のライブラリ (`VideoBuilder.Rendering`) も同じパッケージを参照する (テストでも読めるように) |
| EGL の呼び出し | `EglApi` が OS ごとにライブラリを読み、関数を取り出す (Linux は `libEGL.so.1` の `eglInitialize` など、ANGLE は `EGL_Initialize` のような名前か `eglGetProcAddress`)。それまでの `DllImport("libEGL.so.1")` をやめた |
| GPU の一覧 | DXGI (`CreateDXGIFactory1` → `EnumAdapters1` → `GetDesc1`) で、名前・ベンダー・PCI の ID・LUID・ソフトウェアかどうかを調べる。Linux と違ってコンテキストを開かないので速い。ソフトウェアの GPU (Microsoft Basic Render Driver = WARP) も一覧に入るが、自動では選ばない |
| 設定に覚える ID | `名前|ベンダー:デバイス:サブシステム` (例: `NVIDIA GeForce RTX 4070 Laptop GPU|10DE:2860:3FC817AA`)。LUID は起動のたびに変わるので覚えない |
| 開き方 | コンテキストごとに、選んだ GPU (DXGI のアダプタを LUID で探す) で Direct3D 11 のデバイスを作り (`D3D11CreateDevice`。機能レベル 11_1〜10_0)、ANGLE に渡して (`eglCreateDeviceANGLE`)、そのデバイスのディスプレイ (`EGL_PLATFORM_DEVICE_EXT`) で開く。Avalonia の画面の描画と同じやり方。開けなければ、Windows が選ぶ GPU (なければ WARP) で同じように開く。その上は Linux と同じ (pbuffer → サーフェスなし、OpenGL ES 3 → 2)。Dispose でディスプレイ・ANGLE のデバイス・Direct3D 11 のデバイスを手放す |
| デバイスを分ける理由 | 最初は ANGLE の既定のディスプレイ (`EGL_PLATFORM_ANGLE_ANGLE`) で開いていたが、ANGLE は同じ設定のディスプレイを使い回し、Direct3D 11 のデバイスも 1 つになる。プレビューと出力のように別々のスレッドで同時に描くと壊れた (CI で、並列に流れるテストが Skia の flush の中で落ち、モザイクの絵も崩れた) |
| 0.5 画素の位置 | 幅・高さが奇数の画像 (文字など) を等倍で中心に置くと、左上が x.5 になる。以前は補間なし (最近傍) で描いていたため、どちらの画素を拾うかが CPU と Direct3D で逆になり、文字が 1 画素ずれた (Linux の GPU は CPU と同じ向きだったので出なかった)。ちょうど x.5 のときは整数の位置に寄せて描く (`RenderEngine.SnapHalfPixel`)。補間するかどうかの判断も、キャンバスの移動量だけでなく、画像を置く位置で見る |
| 同じ ID の GPU | 同じ名前・PCI の ID の GPU が 2 つあれば、2 つ目からは ID に `#2` などを付ける。WARP は画面のない環境で 2 つ出ることがあるので 1 つにする |
| ソフトウェアの判定 | `GL_RENDERER` に llvmpipe・SwiftShader・"Basic Render Driver" などが入っていれば、ソフトウェアとして自動では使わない (`GpuContext.IsSoftwareRenderer`) |
| 名前の照合 | ANGLE の `GL_RENDERER` は `ANGLE (NVIDIA, NVIDIA GeForce RTX 4070 Laptop GPU (0x00002860) Direct3D11 vs_5_0 ps_5_0, D3D11)` のような形なので、アダプタの名前が含まれていれば同じ GPU とみなす (`GpuContext.SameGpu`) |
| 動画のデコード | `DeviceHint` は Windows では (`cuda` か `d3d11va`, DXGI の番号)。Intel / AMD を選べば D3D11VA を先に試し、D3D11VA はその番号の GPU で開く (FFmpeg の D3D11VA のデバイス名は DXGI の列挙の番号)。NVIDIA を選んだときも、NVDEC が使えなければ同じ GPU で D3D11VA を使う |
| 確かめ方 | CI (`ci.yml` の `windows-gpu`): GPU のない `windows-latest` で、WARP を使って GPU のテスト (CPU と GPU の絵の比較・GPU の選択・繰り返しの描画) を流す。`VB_REQUIRE_GPU=1` で、GPU を作れないことを失敗にする |

#### 画面との共有 (読み戻さずに表示する)

Windows の Avalonia (ANGLE) は GL のコンテキストの共有に対応していない (`UsesSharedContext => false`。コンテキストごとに別の Direct3D 11 のデバイス) ため、
Linux (22.28) と同じ方法は使えない。代わりに、Direct3D 11 の共有テクスチャで受け渡す。

| 項目 | 内容 |
|---|---|
| 使えるか | 画面の描画が `D3D11TextureGlobalSharedHandle` の取り込みと keyed mutex に対応していれば (`ICompositionGpuInterop`)、`SharedPreviewTarget` を Direct3D 11 のやり方で作る |
| テクスチャ | 描画の GPU コンテキスト (自分の Direct3D 11 のデバイス) で、`R8G8B8A8_UNORM`・`D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX` のテクスチャを作る (`SharedD3DTexture`)。ANGLE で pbuffer として包み (`eglCreatePbufferFromClientBuffer` + `EGL_D3D_TEXTURE_ANGLE`)、GL のテクスチャに結び付けて (`eglBindTexImage`)、Skia で直接描く。最大 3 枚を順に使う |
| 受け渡し | 描く側は鍵 0 を取って描き (`AcquireSync(0)`。画面の描画が返すのを最大 100 ms 待ち、返らなければそのフレームは飛ばす)、GPU の処理を終えてから鍵 1 で返す。画面の描画には、共有のハンドルで取り込ませ (`ImportImage`)、`UpdateWithKeyedMutexAsync(image, 1, 0)` で渡す |
| 同じ GPU | 共有のテクスチャは同じ GPU の間でしか開けない。起動のときに、画面の描画の GPU を描画に選んだ GPU にそろえる (`Win32PlatformOptions.GraphicsAdapterSelectionCallback` → `ScreenGpu.Choose`。LUID で、なければ名前で探す)。描画の CPU を選んでいれば、これまでどおり。描画の GPU コンテキストの LUID が画面の描画 (`ICompositionGpuInterop.DeviceLuid`) と違えば共有しない (「使う GPU」を変えた直後など。起動し直すと共有できる) |
| 失敗したとき | テクスチャを作れない・Skia で包めない・画面の描画が取り込めないときは、その場で読み戻して表示する方法に戻す (`SharedPreviewTarget.Broken` → `ReloadBackend`) |
| 環境変数 | `VIDEO_BUILDER_SHARED_PREVIEW=0` で使わない。`VIDEO_BUILDER_SCREEN_GPU=default` で画面の描画の GPU を選ばない (DXGI の最初の GPU)。`VIDEO_BUILDER_SHARED_PREVIEW_ORIGIN=top` で描く向きを上下逆にする (上下が逆に出る環境があったときに確かめる用) |
| 確かめ方 | CI (Windows・WARP): 共有のテクスチャを作り、Skia で描いて色が読めること、鍵 1 で渡したあとは描く側が取れないこと。画面の GPU の選び方 (LUID・名前)。実機 (内蔵 GPU + RTX 4070 Laptop GPU、2026-11-21): 「画面と共有」で表示され、向き (BottomLeft) も正しく、読み戻すときより速くなった |

画面の描画の GPU を描画の GPU にそろえるので、外付けの GPU を選ぶと、画面の描画も外付けの GPU で行う (電池の持ちは少し悪くなる)。

**実機での確認 (2026-11-20)**: 内蔵 GPU (既定) と NVIDIA GeForce RTX 4070 Laptop GPU があるノート PC で確認した。

- 描画方式の説明に ANGLE の GPU の名前が出て、GPU で描画できた。
- 「使う GPU」に内蔵 GPU と RTX 4070 の両方が出て、どちらでも描画できた。
- 2K の映像のプレビューが、どちらの GPU でも 60fps で安定した。CPU 描画との差はあまりなかった (プロキシが効いているため)。
- ハードウェアエンコード (NVENC) での出力ができ、速かった。

### 22.36 Linux の arm64

Linux の arm64 (aarch64) にも、x64 と同じ作り (CPU で描画・デコード、FFmpeg 9 を同梱) で対応する。コードは変えずに、ビルド・テスト・配布物の作り方を arm64 でも回す。

| 項目 | 内容 |
|---|---|
| コード | 変更なし。SIMD は `Vector128`/`Vector256` (機種に依存しない API) で書いてあり、arm64 では `Vector256` が速く動かないので `Vector128` (NEON) の処理になる。SkiaSharp・HarfBuzzSharp は linux-arm64 のライブラリが NuGet に入っている。`FFmpegLoader` は `/usr/lib/aarch64-linux-gnu` も探す |
| CI (`ci.yml`) | `test` の job を `ubuntu-24.04` と `ubuntu-24.04-arm` (GitHub の arm64 ランナー。エミュレーションなし) の 2 つで流す。FFmpeg のキャッシュはアーキテクチャごとに分ける (`runner.arch` をキーに入れる) |
| 配布物 (`package.yml`) | `linux` の job (x64・arm64): FFmpeg を作って同梱 → 同梱したライブラリが同梱のものを参照していること (`ldd`) → 同梱の FFmpeg だけでテストの一部 → 発行して tar.gz → AppImage。`flatpak` の job (x64・arm64): その CPU のランナーに flatpak-builder と Freedesktop 25.08 を入れ、`packaging/flatpak/build.sh --bundle` をそのまま実行する。入れたアプリの中に FFmpeg 9 と、その CPU 向けの本体があることを確かめる。flatpak-github-actions のコンテナは、入っている SDK の更新に失敗する (Invalid cross-device link) ので使わない |
| AppImage | `packaging/appimage/build.sh [x64\|arm64]`。省くとこの PC のアーキテクチャ。FFmpeg を同梱するので、ほかのアーキテクチャを指定したら、発行の前に止める。CI では appimagetool を FUSE なしで動かす (`APPIMAGE_EXTRACT_AND_RUN=1`) |
| Flatpak | `packaging/flatpak/build.sh [--bundle] [--arch x64\|arm64]`。省くとこの PC のアーキテクチャ。マニフェストには変数を書けないので、発行したものを決まった場所 (`artifacts/flatpak/publish`) に写して、マニフェストはそこから入れる。バンドルの名前は `video-builder-<arch>.flatpak` |

Flatpak の build.sh とマニフェストで、入れるものの場所が食い違わないことを、テスト (`PackagingTests`) で確かめる。

CI の arm64 で分かったこと: 書き出しの拡大を Skia の bicubic と比べるテストで、Skia の側が x64 と違う絵になる (暗い色が 0 に切られる透明との境目で、最大 9 の差。3 より大きい差は全体の 0.01%)。自前の拡大の絵は x64 と同じだった (いちばん差の大きい画素で比べた)。arm64 では、このテストの許す差を広げ、代わりに大きな差の数を 0.02% までに抑える。

注意すること:
- arm64 の実機 (Raspberry Pi 5 など) での動作は、まだ確かめていない (22.4)。確かめるのは CI のランナー (Ubuntu 24.04) の上で、テストと、配布物を作れることまで。
- ARM の GPU 描画と V4L2 でのデコード → [22.37](#2237-arm-の-gpu-描画と-v4l2-でのデコード)。
- GitHub の arm64 ランナーは、公開リポジトリでは無料。非公開にすると、有料のランナーになる。

### 22.37 ARM の GPU 描画と V4L2 でのデコード

Linux の arm64 ([22.36](#2236-linux-の-arm64)) で、ARM の SoC の GPU (V3D・Mali・Adreno など) での描画と、V4L2 の装置での動画のデコード・エンコードを使えるようにする。

| 項目 | 内容 |
|---|---|
| GPU 描画 | 作りは x64 と同じ (EGL のデバイスを 1 つずつ開き、OpenGL ES 3 → 2 の順にコンテキストを作る)。ARM の GPU は名前で区別しないので、優先度は「その他の GPU」(10)。ソフトウェア (llvmpipe) より優先する |
| レンダーノード | EGL がレンダーノードを教えないとき、`/sys/class/drm/cardN/device/drm/renderD*` (同じ装置の下) から探す。これまでは「card N ↔ renderD(128+N)」と推測していたが、ARM の SoC では画面を出す装置 (vc4・rockchip-drm など) と GPU (v3d・panfrost など) が別の DRM デバイスで、番号の対応が外れる。sysfs が読めないときだけ、番号で推測する |
| 二重に並ぶ GPU | 画面を出すだけの装置 (レンダーノードがない) の EGL のデバイスも、実際には GPU で描くので、同じ GL_RENDERER が 2 つ並ぶ。レンダーノードのある、同じ名前の GPU があれば、ない方を一覧から外す |
| デコード (V4L2 M2M) | Linux では、hwaccel (CUDA・VAAPI) のあとに、FFmpeg の V4L2 M2M のデコーダ (`h264_v4l2m2m`・`hevc_v4l2m2m`・`vp9_v4l2m2m` など) を試す。hwaccel ではなく別のデコーダで、デコードしたフレームはふつうのメモリにある (GPU 上の形式は NONE) ので、`VideoReader`・プロキシの作成はそのまま扱える。デコーダは `/dev/video*` からデコードできる装置を探す。開けなかったデコーダは覚えて、2 回目からは試さない |
| 指定 | `VIDEO_BUILDER_HWDEC=v4l2m2m` で V4L2 だけを試す。ほかの種類 (`vaapi` など) を指定したら V4L2 は試さない |
| 表示 | 環境設定 > 動画のデコード に、V4L2 の行 (FFmpeg にデコーダがあるか・開けたもの「使用中」・開けなかった理由) を出す |
| エンコード | `h264_v4l2m2m` に加えて `hevc_v4l2m2m` を候補にした。使えるかは、これまでどおり開いてみて確かめる (`HardwareEncoders.Probe`) |
| FFmpeg | `--enable-v4l2-m2m` は Linux で自動で有効になる (linux/videodev2.h があれば)。CI で、読み込んだ FFmpeg に `h264_v4l2m2m` などがあることを確かめる |
| 実機の確認 | `tools/arm-check.sh` で、DRM デバイスとドライバ・レンダーノード・EGL・V4L2 の装置を表示する (何も変えない)。出力の最後に、アプリで見るところを出す |

使えないもの:
- ステートレスの V4L2 デコーダ (Raspberry Pi 5 の HEVC、Rockchip の rkvdec・hantro、Allwinner の cedrus など。`/dev/media*` と組で使う) は、FFmpeg の本体に入っていないので使えない。これらの SoC ではソフトウェアでデコードする。Raspberry Pi 5 には H.264 のデコーダがなく (CPU でデコードする前提)、エンコーダもない。
- Rockchip の MPP (`h264_rkmpp`)・Jetson の NVMPI も FFmpeg の本体にない。
- Flatpak: サンドボックスに入れる装置は `--device=dri` (GPU) だけで、`/dev/video*` は入らない。Flatpak には V4L2 の装置だけを許す設定がなく、`--device=all` は広すぎるので既定にはしない。使うときは `flatpak override --user --device=all io.github.kimu1109.VideoBuilder`。AppImage・tar.gz では使える。

確かめたこと: CI (x64・arm64 のランナー。GPU も V4L2 の装置もない) で、V4L2 を試してからソフトウェアに戻り、絵が同じこと。sysfs の探し方と、二重に並ぶ GPU を外すこと (テスト用に作ったフォルダで)。**実機ではまだ確かめていない** (22.4)。

**実機で確かめること**:

| 機種の例 | GPU 描画 | デコード | エンコード |
|---|---|---|---|
| Raspberry Pi 4 (V3D 4.2・bcm2835-codec) | V3D で描けるか。CPU 描画より速いか | `h264_v4l2m2m` (ステートフル) で使えるはず | `h264_v4l2m2m` |
| Raspberry Pi 5 (V3D 7.1) | 同上。画面を出す装置 (vc4) と GPU (v3d) が別の DRM デバイス | H.264 は CPU、HEVC はステートレスなので使えない | なし |
| Rockchip RK3588 など (Mali-G610・Panfrost) | Panfrost で描けるか | ステートレスなので使えない | (ベンダーのカーネルなら MPP。使えない) |
| Qualcomm (Adreno・Freedreno・venus) | Freedreno で描けるか | `h264_v4l2m2m`・`hevc_v4l2m2m` | `h264_v4l2m2m`・`hevc_v4l2m2m` |

### 22.38 図形の種類と見た目の設定

図形オブジェクトの種類を増やし、形・塗り・線を細かく設定できるようにする。

| 項目 | 内容 |
|---|---|
| 種類 | これまでの 7 種類 (背景・円・四角形・三角形・五角形・六角形・星型) に、ひし形・台形・平行四辺形・多角形・矢印・両矢印・山形矢印・十字・ハート・吹き出し (四角・丸)・扇形・角括弧・直線を足した 21 種類。番号はプロジェクトファイルに残るので末尾に足す (`ShapeType`)。矢印・山形矢印・直線は右向きで、向きは標準描画の回転で変える |
| 形の設定 | 角の丸み (px。直線でできた形)、頂点の数 (多角形・星型)、へこみ (星型)、傾き (台形・平行四辺形)、矢じりの長さ・幅・太さ (矢印)、しっぽの位置・長さ・幅 (吹き出し)、開始角度・中心角・穴の大きさ (扇形。円にも穴を開けられる)、始点・終点の形 (直線: なし・矢印・三角・円・四角) |
| 塗り | 単色・線形グラデーション (角度・中心)・円形グラデーション。2 色目は「色 2」 |
| 縁の線 | 塗りの上に、別の色の線を重ねる (縁の幅・縁の色)。線が枠からはみ出さないよう、形を線の太さの半分だけ内側に作る |
| 線の描き方 | 実線・破線 (長さ・間隔)・点線、破線のずらし (動かすと線が流れる)、角の形 (とがらせる・丸める・面取り)。ライン幅で線にしたときと縁の線に使う。直線では線そのものを破線にする |
| アニメーション | 数値の設定はすべてトラックバーなので、中間点と移動方法で動かせる (中心角を 0 → 360 にすると円が描かれていく、など) |
| 設定画面 | `ParamDefinition.ShowWhenKey`・`ShowWhenValues` で、図形の種類 (や塗りの種類・線の種類) で使わない設定を隠す。値は残るので、種類を戻せば元の設定に戻る。プラグインのパラメータでも使える |
| 互換性 | 新しい設定の既定値は、これまでの見た目と同じ (星型は頂点 5・へこみ 38.2%)。古いプロジェクトはそのまま同じ絵になる |

形は `Shapes.BuildPath` で SKPath として作り、塗り・線・縁を描く (`Shapes.Render`)。描いた絵は、すべての設定を含むキーでキャッシュする。

### 22.39 組み込みのエフェクト・アニメーションの追加

よく使うもの・処理が重いもの (自分でスクリプトやプラグインを書くと遅くなりがちなもの) を優先して、組み込みのエフェクトを増やす。

| 区分 | エフェクト | 内容 |
|---|---|---|
| ぼかし・光 | グロー | しきい値より明るい部分を取り出し (`PixelFx.BrightPass`)、「ぼかし」と同じ処理 (大きいときは縮小して計算) でぼかして加算する。強さ (100% を超えるぶんは重ねて加算)・拡散・しきい値・光の色 (空欄で元の色)・光だけを描く |
| | 方向ブラー | 決めた角度に長さぶん流す。長さ・角度・サイズ固定 |
| | 放射ブラー | 中心から外へ向かってぼかす (ズームブラー)。強さ・中心X/Y |
| | シャープ | アンシャープマスク (元の絵 + (元の絵 − ぼかした絵) × 強さ)。ぼかしは `PixelBlur` (複数スレッド) |
| | 色収差 | 赤と青を逆向きにずらす。ずれ・角度・中心から外へずらす (放射状) |
| | ラスター (波) | 行 (縦方向なら列) ごとに正弦波でずらす。振幅・波長・速さ (回/秒。0 以外なら波が流れる) |
| | ノイズ | 粒状のざらつき。強さ・粒の大きさ・カラーノイズ・毎フレーム変える・乱数シード。同じフレームなら同じ模様 (プレビューと出力で同じ) |
| 色・形 | 周辺減光 | 絵の周りを暗く (指定の色に) する。大きさ・ぼかしは絵の大きさに対する割合なので、プロキシの小さい絵にもそのままかけられる |
| | グラデーション | 2 色のグラデーションを、絵の見えている部分に重ねる (SrcATop)。中心・角度・幅・強さ |
| | 斜めクリッピング | 中心を通る直線で切って片側だけ残す (マスクと同じ仕組み)。角度・ぼかし |
| | レベル補正 | 入力の黒・白、ガンマ、出力の黒・白。256 個の表を引く |
| | ポスタリゼーション | 色の段階を減らす。表を引く |
| 動き | アニメーション効果 | 振動 (なめらか・回転の揺れも可)・点滅・回転・鼓動・揺れ (傾く)・浮遊 (上下)・弾む・横揺れを、キーフレームなしで付ける。絵は変えず `DrawModifiers` だけを変えるので軽い。振動の乱数は位置と時刻から決まる (同じフレームなら同じ位置) |
| 切り替え | トランジション | ぼかし・回転ズーム・ポップ (行き過ぎて戻る)・時計回りワイプ・ブラインド (横・縦)・ひし形ワイプを追加 (種類の番号は末尾に足す) |

処理の方針:

- ピクセルを動かす処理 (方向ブラー・放射ブラー・色収差・ラスター) は `PixelFx` に置き、行ごとに複数スレッドで実行する。読む位置は双線形補間で、絵の内側は固定小数点 (1 = 65536) で計算する。
- 方向ブラー・放射ブラーは「2 か所の平均」を、ずらす量 (放射ブラーは倍率の対数) を半分ずつにして繰り返す。n 回で 2ⁿ か所の平均になるので、長さ L に対して log₂L 回で済む。平均の丸めは回ごとに切り上げ・切り捨てを入れ替え、何度繰り返しても色が偏らないようにする。
- プレビュー (CPU 描画) で大きくぼかすときは、縦横半分の絵で計算して元の大きさに戻す (計算するピクセルが 1/4)。出力では使わない。
- GPU 描画中も、ピクセルを直接扱う効果は読み戻して同じ処理を使う (CPU と同じ絵になる)。グロー・周辺減光・グラデーション・斜めクリッピングは Skia の描画なので GPU のまま。
- レベル補正・ポスタリゼーション・ノイズは `IPixelEffect` も実装し、画面全体にかけるときは画面のバッファを複製せずに書き換える。
- 色だけを変えるもの・割合で決まるもの・描く位置だけを変えるもの (レベル補正・ポスタリゼーション・周辺減光・アニメーション効果、トランジションの回転ズーム・ポップ) は、プロキシなどの小さく作った絵のままかける (`NeedsFullResolution`)。小さい絵にかけた効果が倍率 1 の絵を返しても、元の倍率を引き継ぐ (`Effects.KeepScale`)。

#### 22.39.1 追加 (その 2): AviUtl の標準エフェクト・アニメーション効果にあたるもの

| 区分 | エフェクト | 内容 |
|---|---|---|
| 色 | 拡張色設定 | 赤・緑・青を足す / 引く (カラーマトリクス。ほかの色の変換とまとめて処理) |
| | 特定色域変換 | 変える色の色相から範囲内の色を、変えた後の色の色相へ回す。彩度・明るさも変えられる。灰色に近い色は変えない |
| | 色ずれ (色収差) | ずらす色を 赤と青 / 赤と緑 / 緑と青 から選べるようにした |
| キー | カラーキー | YCbCr の明るさの差と色の差がそれぞれの範囲内なら透明にする |
| | ルミナンスキー | 基準の明るさより暗い (明るい) 所を透明にする。境目はぼかしの幅でなめらかに |
| 光 | 発光 | グローと同じ処理で、しきい値を高めにした既定値 |
| | グロー (光の形) | 通常・十字・横・縦。筋は方向ブラーを 2 回かけて (先が細く消える) 作る |
| | 閃光 | 明るい部分を放射ブラーで中心へ向かう筋にして足す。筋が伸びるぶんの余白を付ける |
| | 拡散光 | ぼかした絵をスクリーンで重ねる |
| | ライト | アルファをぼかし、光の向きにずらして読んだ値との差で、光が当たる側の縁を明るくする |
| 縁 | 凸エッジ | アルファをぼかした高さの傾きと光の向きで、縁を明るく / 暗くする |
| | エッジ抽出 | 明るさ (または不透明度) のソーベル。線の色・しきい値・元の絵に重ねる |
| ぼかし | レンズブラー | 円の中を平均する (点はひまわりの種の並び)。光の強さで明るい所の重みを上げる。半径が 12 px 以下になるまで縮小して計算する |
| | モーションブラー | 変形の X・Y の前のフレームからの差 (× 間隔) の向きに方向ブラーをかけ、半分後ろへずらして尾を引かせる。回転・拡大率を戻して絵の中の向きに直す |
| | 境界ぼかし | アルファをぼかし、半分より小さい所 (縁の近く) ほど透明にする |
| 変形 | 波紋 | 中心からの距離を正弦波でずらす。速さで波が外へ進み、減衰で遠くほど弱く |
| | 極座標変換 | 横を円周、縦を半径に巻き付ける。拡大率・中心の穴・回転・渦巻 |
| | ディスプレイスメントマップ | 円・四角形の範囲を移動・拡大・回転でゆがめる (境目はぼかしの割合で弱める)。画像ファイルなら赤で横、緑で縦にずらす |
| | ミラー | 下・上・右・左に反転した絵を描き、離れるほど薄くする (透明度・減衰・境目の間隔) |
| | 万華鏡 | 中心の周りを扇形に分け、鏡のように折り返して並べる |
| | 球体 | 絵を地球儀のように球に貼る (横 = 経度、縦 = 緯度)。X・Y・Z 軸の回転、自転、陰影 |
| | 簡易変形 | 4 つの角を動かす射影変換 (正方形 → 四角形の変換を求めて Skia で描く) |
| その他 | 基本効果 | 位置・回転・拡大率・透明度をエフェクトとして足す。縦横比は絵を引き伸ばす |
| | 振動 | X・Y・Z・回転を周期 (フレーム) ごとにランダムに揺らす。なめらかに・オブジェクトごとに変える |
| | 画像ループ | 絵を縦横に並べる。速さで中の絵が流れる |
| | 画像ファイル合成・動画ファイル合成 | ファイルの絵を合成モード (11 種) で、オブジェクトの見えている部分にだけ重ねる。敷き詰めもできる。ファイルは `RenderContext.LoadImage` / `LoadVideoFrame` (エンジンのメディアキャッシュ) で読む |
| | インターレース解除 | 奇数の行・偶数の行 (もう片方を上下から補う)・二重化・平均 |
| 配置・動き | 砕け散る | 開始時刻から、絵を四角い破片に分け、中心から外へ飛ばす (重力・回転・消えるまでの時間) |
| | 円形配置・ランダム配置 | 絵を円周上 / 範囲の中にランダムに並べて描く |
| アニメーション効果 | 種類の追加 | 振り子 (上の端を支点に振れる)・反復移動・回転 (X 軸・リール)・回転 (Y 軸)・立方体の面 (6 つのオブジェクトに面を割り当てると立方体。カメラ制御と使う)。「振動」は「震える」に改名 (番号は同じ) |
| トランジション | 種類の追加 | ランダム方向から・縮小して登場・落ちて登場 (オブジェクトごとにランダムに遅れる)・弾んで登場・広がって登場・起き上がって登場 (下の端を支点に奥から起きる)・何処からともなく登場・点滅して登場 |

- 作り直し (AviUtl の動きに合わせる。以前の動きは別の名前で残す):
  - 砕け散る: ふぞろいな三角形の破片 (格子の内側の点をずらして、すき間なく割る) に割れ、中心から外へ時間差で割れ始める。破片は外へ飛びながら重力で落ち、ばらばらに回る (奥行きの回転は縦横の縮みで表す)。開始時間・中心X/Y・破片サイズ・速度・重力・時間差・距離影響・ランダム回転・ランダム方向・再生速度。以前のもの (四角い破片が一度に飛んで消える) は「飛び散る」(`filter.scatter`)
  - 弾んで登場 (トランジション 26): 右から跳ねながら入り、アウトでは左へ跳ねながら出ていく。高さ・回数。以前のもの (上から落ちて弾む) は「落ちて弾む」(21)
  - 何処からともなく登場 (トランジション 27): 忍者のように、ランダムな場所を 4 回高速で移動してから止まる。移動中は向きにぼかす。位置 (移動の範囲。絵の大きさの倍数)・移動のぼかし。以前のもの (ランダムな位置・回転から集まる) は「集まって登場」(24)
  - リール回転 (アニメーション効果 13): オブジェクトの長さの間に回転数だけ X 軸 (横回転なら Y 軸) で回り、加速・減速して元の向きで止まる。速く回っている間は、端が 1 フレームに動く距離 × ブラー (%) の長さで回る向きにぼかす。以前のもの (一定の速さで回り続ける) は「回転 (X軸)」(10)
- オブジェクトごとの乱数は、オブジェクトの ID から作る種 (`Effects.StableSeed`) を使う。プレビューと出力、開き直したあとでも同じ動きになる。
- 「エフェクトの追加」「調整レイヤーを追加」のメニューは、`EffectDefinition.Group` ごとのサブメニュー (色・ぼかし・光・縁・輪郭・キー・変形・切り抜き・マスク・動き・配置・切り替え・その他) にする。分類のないプラグインの効果は「プラグイン」に入れる。
- 入れていないもの: 個別オブジェクト (テキストの 1 文字ずつ) の座標の拡大縮小・回転、オブジェクト分割、オフスクリーン描画、カメラ制御オプション。このアプリのオブジェクトは 1 枚の絵として描くので、1 文字ずつ別のオブジェクトとして動かす仕組みがない。テキストの 1 文字ずつの動きは「文字の出し方」([22.20.9](#22209-テキストアニメーション-1-文字ずつ)) で付ける。

計測 (1920x1080 の画面全体、4 コアの VM、1 フレームあたり。出力 / プレビュー):

| エフェクト | 出力 | プレビュー |
|---|---|---|
| グロー (拡散 40) | 約 60〜75 ms | 同左 |
| 方向ブラー (長さ 120) | 約 95〜150 ms | 約 45 ms |
| 放射ブラー (強さ 30) | 約 270〜310 ms | 約 70 ms |
| シャープ (範囲 3) | 約 30〜40 ms | 同左 |
| 色収差 (ずれ 8) | 約 30〜40 ms | 同左 |
| ラスター (振幅 30) | 約 20 ms | 同左 |
| ノイズ | 約 13 ms | 同左 |
| レンズブラー (範囲 40) | 約 110 ms | 同左 |
| 閃光 (長さ 60) | 約 280〜350 ms | 約 80 ms |
| 波紋・万華鏡・エッジ抽出 | 約 50〜65 ms | 同左 |
| ライト・凸エッジ | 約 35〜50 ms | 同左 |
| 拡散光 | 約 17 ms | 同左 |

---

---

*以上*

### 22.40 プラグイン API (バージョン 3): メニューのコマンド

VOICEVOX との連携のように、外のアプリと通信して素材を作り、タイムラインに置くプラグインを書けるようにした ([PLUGIN_API.md](PLUGIN_API.md))。
バージョン 1・2 のプラグインはそのまま読み込める (`apiVersion` 1〜3 を受け付ける)。

| 追加したもの | 内容 |
|---|---|
| `IPluginHost.RegisterCommand` / `IPluginCommand` | 「プラグイン」メニューに項目を足す (コマンドがあるときだけメニューが出る)。`Group` で同じサブメニューにまとめ、`CanRun` はメニューを開くたびに確かめる |
| `IPluginCommandContext` | コマンドから本体を操作する窓口。再生位置・選択・プロジェクトのフォルダ・プラグイン専用の設定フォルダ・音声の長さを読み、フォーム・メッセージ・進み具合のダイアログを出す |
| `IPluginCommandContext.Edit` / `IPluginEditor` | プロジェクトの書き換え。中の変更は 1 回で取り消せ、例外が出たらすべて元に戻る。音声ファイル・字幕向けのテキスト・任意のオブジェクトを、長さを削らずに空いているレイヤーへ置く |
| `PluginForm` | 文字 (1 行・複数行)・数値・選択・チェックの入力フォーム。画面は本体が作るので、プラグインは Avalonia に依存しない。文字はプラグインが付けたものなので訳さない |
| `TimelineObject.PluginData` | オブジェクトに残すプラグインの情報 (キーは "プラグインの ID.名前")。プロジェクトと一緒に保存し、複製・取り消しにも含める (「セリフを直して作り直す」などに使う) |

- コマンドは UI のスレッドで呼ぶ。ネットワークの待ちは await するので画面は止まらない。`RunWithProgressAsync` の中止は `CancellationToken` で伝える。
- コマンドが投げた例外は、メッセージで知らせてプラグインのログに残す。
- 設定の置き場所は `$XDG_DATA_HOME/video-builder/plugin-data/<プラグインの ID>/`。
- サンプルプラグインには、フォームで文字と長さを聞いて再生位置にテキストを置くコマンドを追加した。

### 22.41 VOICEVOX 連携 (同梱のプラグイン)

プラグイン API v3 (22.40) の上に、VOICEVOX との連携を `plugins/VoicevoxPlugin` として作った。使い方は [plugins/VoicevoxPlugin/README.md](../plugins/VoicevoxPlugin/README.md)。

- 「プラグイン」→「VOICEVOX」に「セリフから音声を作る...」と「設定...」を出す。
- エンジンの HTTP API (`/speakers` → `/audio_query` → `/synthesis`) を呼ぶ。歌う用のスタイル (`type` が `talk` 以外) は一覧から除く。
  同じ PC のエンジンにはプロキシを通さずにつなぐ。つながらない・返事がないときは、理由をメッセージで出す (一覧は 10 秒、合成は 5 分で打ち切る)。
- 音声の長さは WAV のヘッダから求める (FFmpeg がなくても置ける)。オブジェクトの長さは端数を切り上げる。
- 字幕と音声は、2 つのレイヤーが並んで空いているところを先に探してから置く (置けないときに空の取り消し履歴を残さない)。
- 置いた音声には `PluginData` にセリフ・スタイル・調整の値を残す (作り直す機能のため)。
- 配布物: アプリは実行ファイルの隣の `plugins/` も読む (`AppPaths.BundledPluginDir`)。利用者が同じ ID のプラグインを入れていれば、そちらを優先し、同梱のものは何も言わずに飛ばす。
  `packaging/publish-plugins.sh` が `publish-all.sh` (tar.gz・Windows・macOS・Flatpak) と AppImage の作成から呼ばれ、プラグインを発行して入れる。
  プラグインには本体と共有するライブラリ (API・Core・SkiaSharp のネイティブライブラリ) を入れない。

#### 台本からの一括作成と作り直し

- 「台本から一括で作る...」: 1 行 1 セリフ。「話者: セリフ」で話者を選ぶ (キャラクター名か「名前(スタイル)」。全角・半角と空白の違いは NFKC で正規化して無視)。
  コロンの前が話者の名前でない行は、行全体を既定の話者で読む (「時刻は 10:30」のような文を誤って区切らない)。
  すべて作り終えてから、再生位置から「セリフの間」ずつ空けて並べる。字幕と音声が全部入るレイヤーの組を先に探すので、置けなければ何も変えない。
- 「選んだ音声を作り直す...」: 音声か、同じグループの字幕を選んで実行する。字幕の文字が今のセリフになる (字幕を直してから作り直せる)。
  先頭の位置は変えずに長さを合わせ、後ろにぶつかるものは差し替えない。前のファイルは残すので、取り消せば元に戻る。
- エンジン 0.26 から、しゃべる用のスタイルの種類に `streaming_talk` が加わった (以前は `talk`)。歌う用 (`sing`・`singing_teacher`・`frame_decode`) だけを除くようにしている。
  設定の接続確認では、バージョンに加えて話者の一覧も読み、話者とスタイルの数を出す。

### 22.42 プラグイン API (バージョン 4): テンプレート / VOICEVOX の字幕テンプレート

字幕の見た目を利用者が決められるよう、テンプレートの仕組みをプラグインから使えるようにした ([PLUGIN_API.md](PLUGIN_API.md#テンプレート-バージョン-4))。`apiVersion` 1〜4 を受け付ける。

- `ListTemplates` / `LoadTemplate` / `TemplateFields` と、`IPluginEditor.AddTemplate` / `SetTemplateValues`。公開項目は表示名で指す。
- `AddTemplate` は、テンプレートの既定の置き方によらず、シーンオブジェクト 1 つとして置き、「長さに合わせて伸縮」をオンにする。
  「複数のオブジェクト」として置くと、長さを合わせる方法がなく (オブジェクトごとの長さを伸ばすと動きが崩れる)、字幕と音声を 2 つのレイヤーの組で並べる前提も崩れるため。
  セリフごとにシーンが 1 つ増えるが、テンプレートを通常どおり挿入したときと同じ。
- 公開項目の値の読み書きは Core の `ExposedValues` (文字列で扱う。数値は不変の書式、チェックは "1" / "0")。

VOICEVOX 連携の「字幕のテンプレート...」:

- すべての話者の既定と、キャラクターごとのテンプレートを選ぶ (キャラクターごとの指定は「既定に従う」「文字だけ」「テンプレート」)。設定は Id で持つので、ライブラリで名前を変えても使える。
- セリフは、指定した表示名の公開項目 (空なら最初の文字の項目) に入れる。保存するときと、音声を作る前に、テンプレートと項目があるかを確かめて知らせる。
- 作り直すときは、字幕のテンプレートの項目から今のセリフを読み、作り直したセリフを書き戻して、長さを合わせる (ほかの項目はそのまま)。

#### 口パク (VOICEVOX 連携 1.2.0)

- 音声を作るとき、合成に使った `audio_query` のモーラ (子音・母音の長さ) から口の形の時刻表を作り、音声オブジェクトの `PluginData` (`voicevox.lips`) に "0:-;0.1:o;0.25:-" のような短い文字列で残す。
  - 母音 a・i・u・e・o (無声化した大文字も) はその口、ん (N)・っ (cl)・間 (pau) と、唇を閉じる子音 (m・b・p とその拗音) は閉じた口。
  - 前後の無音を含めた長さの合計を、実際の WAV の長さに合わせて伸び縮みさせる (話速・間の長さの調整・エンジンの丸めのずれを吸収)。
  - 0.04 秒より短い口の形は前の形にまとめる (パクパクしすぎないように)。
- メディアオブジェクト「口パク立ち絵 (VOICEVOX)」(`voicevox.mouth`): 描くたびに、同じシーンで鳴っている指定キャラクターの音声 (有効・表示中のレイヤー) を探し、
  再生位置 (開始位置・分割のずれ・再生速度を含む) の口の形の絵を返す。口を開けている音声を優先する。
  絵は `RenderContext.LoadImage` のキャッシュから返すので、毎フレーム読み直さない。時刻表の文字列の読み取り結果も `ConditionalWeakTable` で覚えておく。
- 口の形の絵がなければ「あ」の絵、それもなければ閉じた絵を使う (2 枚だけでも開け閉めになる)。
- 立ち絵 1 つで動画全体の口パクを受け持てるので、音声を動かす・作り直すたびに口パクを作り直す必要がない。

### 22.43 プラグイン API (バージョン 5): 入力パネル / VOICEVOX のポンポン置くモード

1 つ入力するたびにフォームが閉じて進み具合のダイアログが出る流れだと、セリフを続けて置くのに手間がかかるため、開いたままの入力パネルを足した。`apiVersion` 1〜5 を受け付ける。

- `IPluginCommandContext.ShowPanel(PluginPanel)`: モーダルでない入力パネル (`PluginPanelWindow`)。項目はフォームと同じ (入力欄の組み立ては `PluginFieldsView` で共通)。
  ボタンを押すと値を書き戻して `OnClick` を呼ぶ。例外は下端の状態の欄に出す。同じ Id のパネルは 1 つだけ。
- `Seek` (再生位置を動かす) と `CanEdit` (利用者がドラッグなどで編集している途中でないか)。
  `Edit` は編集の途中に呼ぶと例外にする (利用者の操作と 1 回の取り消しにまとまってしまうため)。

VOICEVOX 連携 (1.3.0) の「ポンポン置くモード...」:

- パネルにセリフを入れて Enter を押すと、待ち行列に入れて入力欄を空にし、すぐ次を受け付ける。待ち行列は 1 つずつ、音声を作って再生位置に置き、再生位置を「音声の後ろ + セリフの間」へ進める。
- 1 つ目を置いたレイヤーを覚えておき、次からも同じレイヤーに並べる (置いたものが選ばれて目安のレイヤーが 1 段下にずれていかないように)。
- 「話者: セリフ」の書き方も使える。「待ちを取り消す」で、作り始めていないセリフを取り消せる。
- エラーはダイアログを出さずにパネルの状態の欄に出し、次のセリフに進む。置いたものは 1 つずつ取り消せる。

### 22.44 文字の飾り

テキストを飾る機能をまとめて足した。テキストオブジェクトのパラメータと、どのオブジェクトにも使える効果に分けている。

テキストオブジェクト:

- **縦書き**: 行は右から左への列、文字は上から下へ (送りは文字の大きさ + 字間、列の幅は行の高さか文字の大きさの 1.25 倍の大きいほう)。
  揃えは上揃え・中央・下揃えになる。括弧・長音・ダッシュ・波線・三点リーダーなどは 90 度回し、句読点は右上に、小さい仮名は少し右上に寄せる
  (Skia には縦書きの字形の切り替えがないため、よく使う文字を決め打ちで扱う)。
- **下線・取り消し線**: 太さは文字の大きさの 6%。縦書きの下線は文字の右側の傍線になる。文字のアニメーション中は、線も 1 文字ずつ、その文字の透明度で出す。

効果 (フィルタ):

| 効果 | 分類 | 内容 |
|---|---|---|
| 縁取り「縁だけにする」 | 縁・輪郭 | 縁を描いたあと、元の形を抜く (袋文字) |
| 背景の帯 | 縁・輪郭 | 文字 (不透明な部分) を囲む四角に余白を足し、角の丸み・透明度・枠線付きで後ろに敷く。「絵の大きさ」を選ぶと絵全体を囲む |
| 内側シャドー | 光 | 一面に影の色を塗った層から、ずらした形 (ぼかしたもの) を抜き、元の形の中だけを残して重ねる |
| 立体 (押し出し) | 縁・輪郭 | 向きに沿って 1 ピクセルずつずらした単色の形を、奥の色から手前の色へ変えながら重ね、最後に元の絵を描く |
| 背景と被る色を反転 | 色 | 下記 |

**背景と被る色を反転** (`filter.overlapinvert`): 効果そのものは絵を変えず、オブジェクトを画面に重ねるとき (`RenderEngine.DrawPlaced`) に、
重ねる範囲の背景を描画先から読み戻し (`SKSurface.ReadPixels`。GPU の描画先でも同じ)、変換した絵を同じ範囲に描いたものと画素ごとに比べる。

- 絵の色 (乗算を戻したもの) と背景の色の距離 (RGB の差の二乗平均の平方根、0〜1) が「判定の幅」以下なら、背景の反転色 (または、背景が明るければ黒・暗ければ白) にする。
  「判定の幅」から「判定の幅 + なめらかさ」の間は、なめらかに混ぜる。「強さ」で混ぜる量を下げられる。
- 背景が透明なところは黒の上にあるとみなす。シーンオブジェクトの中では、そのシーンの中の背景 (透明なら黒) と比べる。
- 合成モードより優先する (この効果があるときは、合成モードは使わない)。
- SkiaSharp 2.88 の SkSL (SKRuntimeEffect) は描画中に落ちることがあるため (クロマキーと同じ理由)、CPU で計算する。重ねる範囲だけを読むので、文字くらいの大きさなら軽い。


### 22.45 トランジションの追加 (入りと出で別の動きをするもの)

トランジション (`filter.transition`) に 59 種類 (番号 28〜86) を足した。番号はプロジェクトファイルに残るので末尾に足している。
描画は `Effects.PairedTransition` (`src/VideoBuilder.Rendering/EffectsTransition.cs`)。

| 分類 | 種類 | 内容 |
|---|---|---|
| 動き | プッシュ (4 方向) | 入る側が押し出すように、両方が同じ向きに動く |
| | カバー (4 方向) | 入る側が上に重なるように入る。出る側は動かず、覆われた所を隠す |
| | アンカバー (4 方向) | 出る側がどいて、下の入る側が見えてくる |
| | ホイップパン (4 方向) | プッシュを真ん中ですばやく動かし、速さに合わせて動く向きにぶらす |
| | 重力で落下・飛び去る | 落下: 出る側が加速しながら下へ落ち、入る側が同じ速さで上から続く。飛び去る: 左上から飛んできて、右上へ飛んでいく |
| 形 | ワイプ (斜め 4 方向・角度指定) | 角から対角の角へ (角度指定は「角度」の向きへ) 直線の境界を動かす |
| | 中央から開く・中央へ閉じる (横・縦) | 中央の帯が広がる / 両端から帯がのびる |
| | 四角形ワイプ (中心から・外から)・円形ワイプ (外から)・星型・ハート型 | 形が広がる / 外から縮む。星は回りながら広がる |
| | 反時計回り・扇形 | 真上から反時計回り / 真上から両側へ開く |
| | チェッカーボード・ランダムブロック | 市松模様の片方ずつマスの左から開く / マスがばらばらの順に入れ替わる |
| | ディゾルブ (粒) | 画面の 2 ピクセル四方の粒がばらばらの順に入れ替わる |
| | ルールワイプ (画像) | グレースケールの画像の暗い所 (反転なら明るい所) から切り替わる。しきい値の幅は「ぼかし」/ 100。画像がなければ左からのワイプ |
| 立体 | 扉 (観音開き)・ドア (片開き) | 出る側が画面の端を軸に手前へ開く (絵の中で射影変換して描く)。手前へ開くので、開ききるころには画面の外へ出ていく |
| | キューブ回転 (4 方向) | 2 つの絵を、画面の大きさの立方体の隣り合う面として回す (標準描画の X・Y 軸回転と奥行きを使う) |
| | カードを裏返す (横・縦) | 前半で出る側が真横まで回り、後半で入る側が裏から回ってくる |
| | ページめくり | 出る側の右下の角が、紙を折り返すようにめくれる (対角線に垂直な折り目で鏡に写す)。裏は白っぽく、折り目の近くを暗くし、めくれた紙の影を落とす |
| 質感・色 | クロスズーム | 出る側が拡大しながら放射ブラー、入る側が拡大された所から戻る |
| | 白で切り替え・黒で切り替え | 出る側が白 (黒) になり、入る側が白 (黒) から戻る |
| | グリッチ・色収差・光漏れ・ピクセル化・波紋・渦巻き・フィルム焼け | 切り替わる瞬間に効果がいちばん強くなる |
| | 焼けて消える | なめらかな乱数 (3 段の値ノイズ) の低い所から焼け、焼けている縁を黄色〜茶色に光らせる |
| | 溶ける | 出る側が 48 本の縦の帯ごとに、ばらばらの速さで下へ溶け落ちる |

**進み具合**: u は「切り替えの始めが 0、終わりが 1」。入りは 経過 / イン秒、出は 1 − 残り / アウト秒なので、
重なっている 2 つのオブジェクトの入りと出で同じ値になる。

**ストーリーボード**: 28 番以降は、入ってくるクリップ (イン) と出ていくクリップ (アウト) の両方にかける (27 番までは手前のクリップにだけかける、以前のまま)。
どちらのクリップが手前のレイヤーにあっても同じ見え方になるようにしている:

- 形で切り替えるもの: 入る側は形の内側、出る側は外側を描く (補い合う)。ストーリーボードは奥のクリップに「奥のクリップ」(`under`、設定画面には出さない) を付け、
  奥のクリップで切り抜くだけの役ならそのまま描く (手前のクリップだけが切り抜かれるので、境目の半透明が重ならず線が出ない)。
- 前半・後半で切り替えるもの (クロスズーム・白 / 黒・カードを裏返す・質感のもの): 出る側を前半だけ、入る側を後半だけ描く。
- 「画面全体を基準にする」(`screen`) をオンにして、形を絵ではなく画面の大きさ・位置で決める (標準描画の X・Y・拡大率から、画面の四角形を絵の座標に直す)。
  大きさの違う 2 つの絵 (縦長の写真と横長の写真など) でも、形の位置がそろう。詳細編集でかけたときの既定はオフ (絵の大きさが基準)。

**共通の設定** (以前からの種類にも効く):

- 動き方: 加減速 (既定。以前の動き)・直線・加速・減速・行き過ぎて戻る・弾む・ばね。27 番までの種類では、加減速を使っていたもの (フェード・ワイプ・スライド・ズームなど) に効く。
- アウトの種類: アウトだけ別の種類にする (「インと同じ」が既定)。たとえば、フェードで入ってスライドで出ていく。
- 境界線の幅・色: 形で切り替えるもの (47〜64) で、入る側の形の内側に線を引く。
- 角度: ワイプ (角度指定) の向き (0 度で左から右へ、90 度で上から下へ)。

**互換**: 読み込み時に、増えた選択・チェック・文字のパラメータも既定値で補うようにした (以前はトラックバーだけ)。
ストーリーボードから作り直したオブジェクトと、保存してあったオブジェクトのパラメータの数がそろうので、以前のプロジェクトも「ストーリーボードのまま」と判定される。

### 22.46 アニメーション効果の追加

エフェクト「アニメーション効果」(`filter.anim`) に 15 種類 (番号 14〜28) と、どの種類にも効く設定を足した。
描画は `src/VideoBuilder.Rendering/EffectsAnimation.cs`。

| 種類 | 内容 |
|---|---|
| 円運動 | 元の位置を中心に、半径 = 強さの円を回る |
| 楕円軌道・8 の字 | 移動量 X・Y を半径に回る / 横に 1 往復する間に縦に 2 往復する。移動量 Y が 0 なら X の半分 |
| ぷるぷる (ゼリー) | 横に伸びると縦に縮む (面積はそのまま)。絵を伸び縮みさせて描き直し、中心X・中心Y の点を動かさない (下の端を中心にすると、床の上で揺れるように見える) |
| 呼吸 | ゆっくり大きくなりながら明るくなる (明るさの変化 %) |
| 色相が回る | 色相回転の行列を 1 回で 360 度 |
| 虹色が流れる | 斜めの虹色の帯を流しながら乗算で重ねる (白い文字はそのまま虹色になる) |
| 明るさの脈動 | 白へ近づく (強さが負なら暗くなる) のを繰り返す |
| 心臓の鼓動 (2 拍) | 1 回のうちに、強い拍と弱い拍の 2 回ふくらむ |
| 首振り | Y 軸で左右に向く |
| 旗のようになびく | 左の端を止めたまま、右へ行くほど大きく縦に波打つ (波長)。波の斜面の向きで明るさを変える |
| 跳ねながら往復 | 移動量 (X・Y) のところまで片道 3 回跳ねて行き、戻る。戻るときに左右反転できる |
| 手ぶれ | 周波数の違う 3 つの正弦波を重ねた、ゆっくりで不規則な揺れ (位置・回転) |
| ゆっくりズーム・パン | オブジェクトの長さ全体で、強さ % だけ寄る (負なら引く) と同時に、移動量だけ動く (Ken Burns) |
| カーソル点滅 | いちばん下の、何か描かれている行のまとまりの右端に、点滅する縦棒を描く。1 文字ずつ表示しているテキストでは、最後に出た文字の後ろに付いていく。高さはテキストなら文字の大きさ |

どの種類にも効く設定:

- 開始の遅延 (秒): その間は動かさない。
- 繰り返す回数 (0 で無限): 速さ × 時間がこの回数に達したら、その姿勢で止まる。
- 位相 (度): 動きの始まりをずらす (複数のオブジェクトを少しずつずらして動かすときに使う)。
- 中心X・中心Y: この効果が足した回転 (Z 軸) と拡大で、オブジェクトの中心からずれた点が動いたぶんを、位置で打ち消す (その点を中心に回る・ふくらむ)。

繰り返す回数・位相は、繰り返す種類だけに効く (震える・リール回転・手ぶれ・ゆっくりズーム・パンには効かない)。
旗・カーソルはピクセル単位で動くので、プロキシの小さい絵には直接かけない (`NeedsFullResolution`)。

### 22.47 文字アニメーションの追加

テキストの「文字の出し方」に 13 種類 (番号 5〜17) と、単位・順番・消え方を足した。
時刻の計算は `src/VideoBuilder.Rendering/TextAnimation.cs`、文字の描き方は `Generators.RenderText` (`CharStyle`)。

| 種類 | 内容 |
|---|---|
| 1 文字ずつ上から・左から・右から | 文字の大きさの 0.6〜0.8 倍ずれた所から、フェードインしながら入る |
| 1 文字ずつ回転して登場 | 半回転しながら、小さい所から大きくなる |
| 1 文字ずつ弾んで登場 | 上から落ちて、弾んで止まる |
| 1 文字ずつぼかしから | ぼけた状態からくっきりする (文字ごとのぼかしは `SKMaskFilter`) |
| 1 文字ずつ縮小して登場 | 2.5 倍の大きさから縮んで現れる |
| 1 文字ずつ落ちてくる | 上から加速しながら落ちてくる |
| 文字化けから表示 (デコード) | 出る間は、同じ種類 (英大文字・英小文字・数字・ひらがな・カタカナ・漢字・記号) のでたらめな文字を 0.05 秒ごとに替えて見せる |
| 波打つ (繰り返し) | 単位ごとに少しずつ遅れて上下に揺れ続ける (周期・高さ) |
| カラオケ (色が変わる) | 文字は始めから見えていて、行の向きに、文字の始めから「塗る色」で塗っていく |
| 線で描いてから塗る | 長さの前の 65% で輪郭線が伸び (`SKPathEffect.CreateTrim`)、残りで塗りが現れる |
| 数字のカウントアップ | テキストの中の数を 0 から数える (減速しながら)。桁区切り・小数の桁数はそのまま |

**単位**: 文字 (改行も 1 つ。以前からの数え方と同じ)・単語・行。単語は空白で区切り、日本語は「漢字 + 送り仮名」「カタカナ」「英数字」のまとまりで区切る
(「今日は良い天気です」→ 今日は / 良い / 天気です)。句読点と空白は前の単語に含める。

**順番**: 先頭から・ランダム (テキストから決まる乱数。同じテキストなら同じ順番)・中央から・両端から。逆順はこの順番を逆にする。同じ順番の単位は同時に動く。

**消え方**: フェードアウト・上へ消える・下へ落ちる・縮んで消える・散らばって消える。出し方と同じ順番・間隔で、オブジェクトの終わりでちょうど消え終わるように始める。

- 文字が動いても絵からはみ出さないよう、アニメーションするテキストの絵は横に文字の大きさの 1.6 倍、縦に 2 倍の余白を付ける (中心は変わらない)。
- 出終わってから消え始めるまでは、同じ絵を使い回す (毎フレーム描き直さない)。波打つは止まらないので毎フレーム描く。

### 22.48 移動方法の動き方 (イージング)・モーションパス・親オブジェクト

**動き方 (イージング)** (`Easing`、`src/VideoBuilder.Core/Easing.cs`): トラックバーの移動方法の、区間の進み具合 (0〜1) のかけ方。

- 種類: Sine・Quad・Cubic・Quart・Quint・Expo・Circ・行き過ぎて戻る (Back)・ゴム (Elastic)・弾む (Bounce)・ばね (Spring)・ベジェ曲線。
- 向き: 加速 (In)・減速 (Out)・加速して減速 (In-Out)。ゴム・弾む・ばねは「減速」が元の形 (終わりで揺れる)。ベジェ曲線は向きを使わない。
- 強さ: 行き過ぎて戻るの行き過ぎる量・ゴムの揺れる回数・ばねの揺れる速さ (1 = 標準)。減衰 (0〜1): ゴム・ばねの揺れの収まり方 (ばねは 1 で揺れない)。
  ゴム・ばねは、終わりでちょうど 1 になるよう、ずれを直線で打ち消す。
- `AnimatedValue.Ease` が null なら、以前からの加速・減速のチェックで決める (既存のプロジェクトの動きは変わらない)。
- 区間ごとの動き方 (`AnimatedValue.SectionEases`): 区間 (中間点の間) ごとに別の動き方にできる。null の区間は全体の動き方を使う。
  中間点を足すと分けた両方の区間が同じ設定を受け継ぎ、消すと前の区間の設定が残り、オブジェクトを分割すると前後で分ける (`TimelineObject`)。
- 直線移動・曲線移動・反復移動・プラグインの移動方法・キーフレームを無視に効く (加減速移動・瞬間移動・移動量指定・ランダム移動には効かない)。

UI: パラメータ名をクリックしたときのメニューに「動き方 (イージング)」「動きの向き」「動き方の編集...」「この区間の動き方...」を足した。
動き方の編集 (`EasingDialog`) は、種類・向き・強さ・減衰と、形のグラフ (横が時間、縦が進み具合。-0.5〜1.5 を表示し、行き過ぎも見える)。
ベジェ曲線はグラフの 2 つの制御点をドラッグして決める (プリセット: ease・ease-in・ease-out・ease-in-out・行き過ぎて止まる・溜めてから動く)。

**モーションパス**: 選択中で X か Y が動くオブジェクトは、プレビューに移動の軌跡 (点線) と区間の境目 (始め・中間点・終わり) の点を描く。
点をドラッグすると、その境目の X・Y の値が変わる (動いている軸だけ)。軌跡は `ObjectMotion.Path` で、親の動きを含め、グループ制御・カメラ・効果の動きは含めない。再生中は描かない。

**進む向きに回転** (標準描画 `orient`): 前後のフレームの X・Y の差から向き (右へ進むと 0 度) を求めて、回転に足す。止まっているときは、近くの動いているフレームの向き。

**親オブジェクト** (標準描画 `parent`・`parentDelay`・`parentInherit`): 親のレイヤー (表示の番号。0 でなし) にあるオブジェクトの位置 (X・Y・Z) を足す。
「親の回転・拡大も受け継ぐ」なら、親の拡大率と回転も子の位置にかける。遅れを付けると、その秒数だけ前の親の位置を使う (追いかける動き)。
親がその時刻にいなければ、直前にいたもの (なければ最初のもの) の、範囲の端の位置を使う。親の親もたどる (8 段まで。循環していても止まる)。
親の値は標準描画の値だけで、親にかけた効果 (アニメーション効果など) の動きは含めない。
親を持つオブジェクトは、自分の値が変わらなくても動くので、シーンの絵を使い回す判定 (`IsStill`) では動くものとして扱う。

### 22.49 フィルタの追加 (色・質感・光・変形・時間・合成・生成)

フィルタを 27 種類足した。描画は `src/VideoBuilder.Rendering/EffectsFilters.cs`、時間・合成のものは `RenderEngine`。
エフェクトの追加メニューに「質感」「時間・合成」「生成」の分類を足した。

| 分類 | フィルタ | 内容 |
|---|---|---|
| 色 | LUT (カラーグレーディング) | .cube 形式の 3D LUT (DOMAIN_MIN / MAX 対応) を三線形補間で引く。強さで元の色と混ぜる。読んだ LUT はファイルの更新時刻で使い回す |
| | トーンカーブ | 入力 0・64・128・192・255 の 5 点の出力から、行き過ぎない 3 次補間 (Fritsch–Carlson) で変換表を作る。RGB か 1 色 |
| | 色温度・色かぶり | 青 ↔ 黄、緑 ↔ 赤紫の向きに、色ごとに倍率をかける (カラーマトリクス。ほかの色の変換とまとめて処理される) |
| | シャドウ・ハイライト | 暗い所ほど白へ、明るい所ほど黒へ寄せる (明るさの 2 乗で重みを付ける) |
| | グラデーションマップ | 明るさで、暗い所・(中間)・明るい所の色の間を補間する (デュオトーン・トライトーン) |
| | カラープリセット | セピア・ティール & オレンジ・褪色・クール・ウォーム・白黒 (高コントラスト)・ビビッド・ブリーチバイパス・夕焼け・月明かり |
| 質感 | フィルム粒子 | なめらかな乱数の粒を明るさに足す (中間の明るさで強い)。毎フレーム変えられる |
| | VHS | 赤と青を左右にずらしてにじませ、横に揺らし、流れるノイズの帯と下端の乱れを足す |
| | ブラウン管 | 樽型にゆがめ、走査線・RGB の格子 (縦縞)・周辺減光をかける |
| | グリッチ | 4 フレームごとに、頻度の確率でグリッチが起き、四角いブロックのずれ・行のずれ・色の分離・まれな色の反転 |
| | ハーフトーン | 斜めの格子のマスの中心の色で、暗いほど大きい点を描く。CMY は C 15 度・M 75 度・Y 0 度・K 45 度の網点を重ねる |
| | 油絵・水彩 | 桑原フィルタ (4 つの四角のうち色のばらつきが最小のものの平均)。累積和の表で、筆の大きさによらず速い。水彩は淡く、輪郭を濃く、紙のむら |
| | スケッチ | 反転した明るさをぼかして覆い焼きで重ねる (鉛筆画)。紙の色を掛ける |
| | ドット絵化 | マスの平均の色を、色の段階に減らす (4×4 のベイヤー行列でディザ)。半透明の縁は不透明か透明かに分ける |
| | ASCII アート | マスの明るさに合う文字 (記号・ブロック・数字) で描き直す |
| 光 | レンズフレア | 光源の光と放射状の筋、光源から中心を通る線の上の光の輪 (ゴースト) をスクリーン合成で描く |
| | 光漏れ | 暖かい色の光が、なめらかな乱数で場所と強さを変えながら端から差し込む |
| 変形 | 渦巻き・膨張 / 収縮・魚眼 | 中心からの距離で読む位置を変える (共通の `RadialWarp`) |
| | ミラータイル | 縦横に並べて縮め、交互に鏡に写す |
| 時間・合成 | コマ落とし | オブジェクトを、何フレームかごとの時刻の状態で描く (動き・動画・効果のすべて) |
| | 残像 | 間隔ずつ前の時刻のオブジェクトを、薄くして先に描く。動画は時刻ごとに別のデコーダで読む |
| | トラックマット | マットのレイヤー (0 なら 1 つ上) に今いるオブジェクトだけを、別の描画先に描き (レイヤーが非表示でも描く)、その形 (アルファ) か明るさ (ルミナンス) で切り抜く。反転もできる |
| 生成 | パーティクル | 紙吹雪・雪・雨・火花・泡・きらめき。位置は時刻と番号から決まる (どのフレームからでも同じ)。絵の範囲に描く (画面全体なら調整レイヤーにかける) |
| | 集中線・スピード線 | 外から中心へ向かう細い三角形の線 / 横・縦の線。毎フレーム変えると揺れる |
| | 枠 | 額縁・ポラロイド (下を広く)・映画の黒帯 (2.39:1)・角丸 |

時間のフィルタは、オブジェクトを描く時刻を変えるので、絵そのものは変えない (効果の本体は何もしない)。
描画エンジンは、オブジェクトごとに描く時刻の並び (`ObjectFrames`) を作り、時刻ごとの文脈 (`RenderContext`) で描く。
その文脈で作った絵は、待たせて描くことがあるので、フレームの終わりまで残す。残像は選択枠に入れない。

**動画の再生速度**: 再生速度が途中で変わるとき (移動・中間点ごとに違う値) は、フレームごとの速度を足し合わせて素材の時刻を決める
(台形公式。細かさは最大 2000 区間)。速度を上げ下げしても飛ばず (スピードランプ)、0% の区間では止まる (以前は 経過時間 × 今の速度 で、速度が変わると時刻が飛んでいた)。
**逆再生**: オブジェクトの終わりで開始位置になるように、後ろから再生する。音声オブジェクトの再生には効かない。

入れていないもの: ベジェで描くマスク・トラッキング (手ぶれ補正)・メッシュワープ (編集の画面が要る)。
ゴッドレイ・ブルーム・コーナーピンは、既存の閃光・グロー・簡易変形で代わりになる。

### 22.50 音声エフェクトの追加

音声のエフェクトを 8 種類足した (`src/VideoBuilder.Rendering/AudioEffectsMore.cs`)。どれもブロックごとに呼ばれ、続きの状態を持ち、続いていないブロック (シーク・再生開始の直後) では作り直す。

| エフェクト | 内容 |
|---|---|
| リバーブ | Freeverb 方式: 8 本の櫛形フィルタ (高音の吸収付き) と 4 本のオールパス。左右で遅れを 23 サンプル変えて広がりを出す。初期反射までの時間 |
| コンプレッサー | 左右の大きいほうの包絡線 (アタック・リリース) がしきい値を超えたぶんを圧縮比で縮める (ニーの幅でなめらかに)。音量の持ち上げ |
| リミッター | 5ms 先読み (音は 5ms 遅れる) で大きな音の前に下げ始め、最後に上限で切り詰める (上限を超えない) |
| ノイズゲート | 包絡線がしきい値より小さい間 (保持の時間のあと)、下げる量だけ下げる |
| ノイズ除去 | スペクトル減算: ハン窓の FFT (48 kHz では 1024 点、4 分の 3 重ね) で、周波数ごとの雑音を「なめらかにした強さの最小値」(ゆっくりしか上がらない) で推定し、ウィーナー型の利得で下げる (利得も時間方向になめらかに)。雑音は直近約 1.5 秒の最小値で、始めの 1.5 秒は少しずつ効かせる (頭の音を削らない)。区間の長さ (約 21ms) だけ音が遅れる。ずっと同じ強さで鳴る音 (ハムなど) も雑音として下がる |
| ピッチ変更 | 遅延線方式: 遅れを少しずつ変える 2 つの読み出し口 (60ms の窓) を三角の窓で重ねる。速さは変わらない |
| フィルタ | RBJ の双 2 次フィルタ (ローパス・ハイパス・バンドパス・ノッチ)。急な傾きなら 2 段 (24 dB/oct) |
| ラウドネスの正規化 | オブジェクトで流れる範囲の素材を最初に 1 回だけ読み、統合ラウドネス (K 特性・400ms 区間・絶対ゲート −70 LUFS・相対ゲート −10 LU) を測って、目標との差だけ音量を変える (最大で上げる量まで)。測った値はファイル・開始位置・長さごとに覚える |

**フェードのカーブ** (音声フェード): 直線 (以前から)・等パワー (sin。2 つを重ねたとき音の強さの和が一定で、つなぎ目で音量が下がらない)・なめらか (S 字)。
右クリックのクロスフェードで付ける音声フェードは、等パワーにする。

### 22.51 Windows on ARM (ARM64 の Windows)

Snapdragon の PC (Adreno の GPU) などの ARM64 の Windows で、エミュレーションなしの ARM64 版として動かし、GPU を使う。
描画の作り (ANGLE = Direct3D 11 の上の OpenGL ES、22.35) は x64 と同じで、GPU の会社ごとの仕組みがない分を、Windows の標準の仕組みで埋める。

| 項目 | 内容 |
|---|---|
| ネイティブのライブラリ | ANGLE (`Avalonia.Angle.Windows.Natives`)・SkiaSharp・HarfBuzzSharp は、NuGet に `win-arm64` のライブラリが入っている。`publish-all.sh win-arm64` で発行し、ANGLE (`av_libglesv2.dll`) が入っていることも確かめる |
| GPU の選択 | Adreno (`Qualcomm(R) Adreno(TM) X1-85 GPU`、ベンダー 0x5143) を `GpuAdapter.IsQualcomm` で見分ける。優先度は「その他の GPU」(10) で、WARP より優先する。Linux の freedreno (`FD750` など) も同じく見分ける |
| 動画のデコード | ARM64 の Windows には CUDA (NVDEC) がないので試さない。D3D11VA → D3D12VA (D3D11VA の対応がない形式・ドライバのとき) → DXVA2 の順 (`HardwareDecoding.WindowsOrder`)。D3D12VA も DXGI の番号で、描画と同じ GPU を開く |
| 動画のエンコード | NVENC・Quick Sync・AMF がないので、Media Foundation のハードウェアエンコーダ (`h264_mf`・`hevc_mf`・`av1_mf`) を候補にした。`hw_encoding=1` で CPU で動く Media Foundation のエンコーダには落とさない。品質の指定はドライバによって違うので、ビットレート (目安の値、上限はその 2 倍のピーク制限付き VBR) で指定し、速度の指定は `compression_level` (速度と品質の釣り合い) にする。B フレームは使わない。x64 の PC でも使えるが、同じ方式を NVENC などで使えるときは一覧に出さない (`WithoutCoveredMediaFoundation`) |
| FFmpeg | Windows では `--enable-d3d11va --enable-dxva2 --enable-mediafoundation` を必ず付ける (`packaging/ffmpeg/build.sh` の `WINDOWS_FLAGS`)。ARM64 は MSYS2 の CLANGARM64 で作り、NVENC・AMF のヘッダと nasm は入れない |
| エミュレーション | x64 版を ARM64 の Windows で動かしているときは (`PlatformInfo.IsEmulatedOnArm64`。.NET の `OSArchitecture` はエミュレーションの中でも本当の CPU を返す)、描画方式の説明に「x64 版をエミュレーションで実行中」と添えて、ARM64 版を勧める |
| CI | `package.yml` の `windows` の job を、`windows-latest` (x64・UCRT64) と `windows-11-arm` (arm64・CLANGARM64) の 2 つで回す。作った FFmpeg に D3D11VA と `h264_mf` があること、本体・Skia・ANGLE・FFmpeg の DLL がその CPU 向け (PE の Machine が 0xAA64) であることを確かめる。`ci.yml` の `windows-gpu` も `windows-11-arm` で流す (WARP) |

注意すること:
- **実機 (Snapdragon X など) ではまだ確かめていない**。CI のランナーには GPU がないので、確かめるのは WARP での描画と、配布物を作れることまで。実機では、描画方式の説明に Adreno の名前と「画面と共有」が出るか、環境設定 > 動画のデコード で D3D11VA が使われるか、出力ダイアログに「ハードウェア (Media Foundation)」が出て出力できるかを見る。
- GitHub の `windows-11-arm` ランナーは、公開リポジトリでは無料。非公開にすると使えないことがある。

### 22.52 GPU と CPU の間の受け渡しを減らす (Linux の GPU 描画)

GPU 描画のときに、フレームが GPU と CPU の間を往復する回数と量を減らす。画面との共有 ([22.28](#2228-gpu-描画のプレビューを読み戻さずに表示する)) で残っていた、書き出しと動画の素材の経路が対象。

| 手法 | 内容 |
|---|---|
| 書き出しは RGBA のまま読み戻す | GPU の描画先は RGBA なので、BGRA で読み戻すと読み戻しのたびに R と B を入れ替える処理が入っていた。書き出し (動画・連番 PNG) では RGBA のビットマップで受け取り (`RenderEngine.ReadbackColorType`)、FFmpeg には RGBA として渡す (`MediaWriter.PrepareVideoFrame(…, rgba: true)`)。GPU が使えなくなって CPU で描くときは、BGRA で描いてから並べ替える (CPU 描画は BGRA が前提) |
| レベル補正・ポスタリゼーションを GPU のまま | 以前は GPU 描画中も読み戻して CPU の表引きをしていた。Skia の表のカラーフィルタ (`SKColorFilter.CreateTable`。アルファを外してから表を引き、掛け戻す。CPU と同じ考え方) で、読み戻さずに処理する (`Effects.ApplyLut`)。フィルタオブジェクトでも同じ |
| 動画のフレームを YUV のまま GPU へ | 以前は CPU で YUV → BGRA に変換してから (1080p で約 2.5 ms)、4 バイト/画素を GPU へ送っていた。GPU 描画中は、デコードしたままの YUV を GPU へ送り (4:2:0 なら 1.5 バイト/画素)、GPU で RGBA にする。ソフトウェアでデコードしたフレームは、複製せずにデコーダのフレームを参照して持つ |
| 書き出しの読み戻しを待たない | `SKSurface.ReadPixels` は GPU が描き終えるまで待つ。書き出しでは、描き終えたら読み戻しの命令だけを出し (`glReadPixels` を PBO へ + フェンス)、次のフレームを描いてから受け取る (`AsyncReadback`。2 枚分)。GPU がフレーム N を描く間に、CPU はフレーム N-1 の色変換を進める。受け取った PBO を写さずに、そのまま YUV に変換する |

#### YUV のまま送る仕組み

| 項目 | 内容 |
|---|---|
| 対象 | 8 ビットの YUV420P・YUV422P・YUV444P (J 形式も)・NV12 (ハードウェアデコードの転送結果)。透明な部分のある動画 (アニメーション WebP・GIF) は除く |
| デコーダ側 | `VideoReader.PreferYuv` のとき、BGRA に変換するかわりに YUV のフレームを置き場ごとに持つ。先読み中も前のフレームを描けるよう、置き場を 2 つ使う決まり (`PinCurrentFrame`) は BGRA と同じ。縮めるとき (`OutputDivisor`) は YUV のまま縮める。V4L2 のデコーダは受け取り用の置き場が少ないので、参照を持たずに複製する。`TryGetYuv` で面を、`EnsureBgra` で BGRA を受け取る (GPU へ送れなかったとき) |
| GPU 側 | `YuvTextures`。SkiaSharp 2.88 には YUV の画像を作る API (`SKYuvaPixmaps`) がなく、SkSL も描画時に落ちるので、OpenGL ES 3 を直接使う。Y・U・V の面を R8 / RG8 のテクスチャに送り (`GL_UNPACK_ROW_LENGTH` で行の余白を飛ばす)、自前のシェーダで RGBA8 のテクスチャに描いて、`SKImage.FromTexture` で包む。使い終えたテクスチャは戻ってきて使い回す。GL を使ったあとは `GRContext.ResetContext` で Skia に知らせる |
| 色 | CPU 描画と同じ絵になるように、sws の既定に合わせる。係数は BT.601 (J 形式はフルレンジ、ほかはリミテッド)。U・V は補間せずに 2x2 画素で同じ値 (sws の拡大縮小しない変換と同じ)。NV12 は sws の振る舞いが CPU の種類で違う (x64 は縦だけ補間し、U・V の行 k が Y の行 2k+1 の位置にあるとみなす。arm64 の NEON 版は補間しない)。初めて使うときに小さな NV12 を sws で変換して調べ (`VideoReader.SwsInterpolatesNv12ChromaVertically`)、同じ読み方にする |
| 使わないとき | OpenGL ES 3 でない (画面と共有するデスクトップの OpenGL など)・シェーダを作れない・U・V を間引いた形式で幅か高さが奇数 (sws が別の変換を使うので絵が合わない)・行の長さが画素の大きさで割り切れない。これまでどおり BGRA で送る。環境変数 `VIDEO_BUILDER_GPU_YUV=0` で使わない |

#### 読み戻しを待たない仕組み

| 項目 | 内容 |
|---|---|
| 描画先 | 自前の GL テクスチャ (RGBA8) を `SKSurface.Create(GRContext, GRBackendTexture, TopLeft, Rgba8888)` で包んで、Skia に描かせる。同じテクスチャを付けた自前の FBO から読む |
| 順番 | フレーム N を描く → Skia の命令を GL に出す (`Flush`) → `glReadPixels` (PBO へ) とフェンス → フレーム N-1 のフェンスを待って PBO を読み、色変換して、音声と一緒にエンコードの待ち行列へ。GL の命令は出した順に実行されるので、同じ描画先に次のフレームを描いても、前の読み戻しの中身は変わらない |
| 失敗したとき | 作れない (OpenGL ES 3 でないなど) ときは、これまでどおり。途中で読み戻せなかったときは、読み戻しをやめ、受け取っていないフレームも含めてこれまでどおり描き直す (順番は保つ)。`ExportStats.AsyncReadbackFrames`・`AsyncReadbackError` で分かる。環境変数 `VIDEO_BUILDER_ASYNC_READBACK=0` で使わない |
| 対象 | 動画の書き出し。連番 PNG は PNG の圧縮が大半なので、これまでどおり |

#### 確かめたこと

Mesa のソフトウェアの GL (llvmpipe, OpenGL ES 3.2) で、テスト (`GpuTransferTests`) を流した。

- RGBA で読み戻した絵が BGRA と同じ (差 0)。GPU で書き出した動画が CPU で書き出したものと同じ色 (R と B が入れ替わっていない)。連番 PNG も同じ。
- レベル補正・ポスタリゼーション: オブジェクトにかけたとき平均の差 0.015、フィルタオブジェクトで 0.006 (ポスタリゼーションの段の境目ちょうどの色で、1 段ずれる画素がある)。
- YUV のまま送ったフレーム: x264 (YUV420P)・VP9 の動画で、CPU 描画との平均の差 0.9・最大 3。面を直接作って sws と比べると、YUV420P・YUVJ420P・YUV422P・YUVJ444P・YUV444P (37x23)・NV12 (下端の 2 行を除く) で最大 3。
- 読み戻しを待たない書き出し: 動く図形の 12 フレームが、CPU で書き出したものとフレームごとに同じ (順番がずれていない)。3 フレームで失敗させても、残りがこれまでどおり書き出される。

**実際の GPU での速さは測れていない** (開発環境に GPU がない)。llvmpipe では GPU の処理も CPU で動くので、シェーダでの色変換は sws より遅い (1080p の書き出しで 78 fps → 65 fps、読み戻しを待たないと 70〜76 fps)。描画方式の「自動」はソフトウェアの GL を選ばないので、ふだんはこの経路にならない。実際の GPU では、CPU の色変換 (1080p で約 2.5 ms/フレーム) と送る量が減り、読み戻しの待ちが色変換と重なるはず。`GpuTransferTests.Report_gpu_export_speed` を、環境変数を変えて (`VIDEO_BUILDER_GPU_YUV=0`・`VIDEO_BUILDER_ASYNC_READBACK=0`) 流すと比べられる。

残っていること: 動画の書き出しで、YUV への変換も GPU で行う (読み戻す量がさらに減る)、ハードウェアデコードのフレームを CPU に写さずに使う (VAAPI の DMA-BUF を EGL で取り込む)、VAAPI のエンコードに GPU 上のフレームを渡す、ピクセルを直接扱うほかの効果 (モザイク・グローの明部抽出など) を GPU で処理する (SkiaSharp 3 への更新が必要)。
