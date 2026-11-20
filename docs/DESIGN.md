# video-builder 設計書

| 項目 | 内容 |
|---|---|
| 文書バージョン | 0.15 |
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
| エフェクト (M5) | 色調補正・ぼかし・縁取り・シャドー・クリッピング・モザイク・クロマキー・単色化・反転・フェード、フィルタオブジェクト、合成モード 13 種 |
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
| 描画バックエンド | Skia GL | **GPU (EGL + OpenGL ES) と CPU の切替**。既定は自動 | 下の [22.5](#225-gpu-描画) を参照 |
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
| Windows / macOS | 発行 (`dotnet publish`) が通り、必要なネイティブライブラリが入ることを確認。実機での動作は未確認。GPU 描画は Linux のみ |

### 22.4 確認できていないこと

- **GPU 描画の速度**: 開発環境に GPU がなく、ソフトウェア実装の OpenGL (llvmpipe) でしか動かしていない。絵が CPU 描画と一致することは確認したが、実際の GPU でどれだけ速くなるかは未計測。
- **音声の再生**: 開発環境に音声デバイスがないため、OpenAL での実際の出力は確認していない (デバイスがない場合に無音で再生が進むことは確認済み)。
- **波形・サムネイル・BPM グリッド**: テスト用の動画で表示を確認した。長時間の素材での速度は未計測。
- **英語表示**: 主要な画面を確認した。エラーメッセージの一部 (FFmpeg やプラグインの読み込みエラーの詳細) は日本語のまま。
- **実機での操作感**: 画面は仮想ディスプレイ上のスクリーンショットと UI 自動テストで確認した。Wayland・タイル型 WM・IME での動作は未確認。

---

### 22.5 GPU 描画

| 項目 | 内容 |
|---|---|
| 方式 | 描画スレッドが画面に出さない OpenGL ES コンテキスト (EGL) を作り、その上の Skia GPU サーフェスに合成する。結果は読み戻して、プレビュー表示と出力に使う |
| GPU で処理するもの | オブジェクトの合成 (移動・拡大・回転・合成モード)、ぼかし・縁取り・シャドー・色調補正などのフィルタ、フィルタオブジェクト、シーンの入れ子 |
| CPU のままのもの | 動画のデコード、テキスト・図形の生成 (結果はキャッシュ)、クロマキー |
| 設定 | 環境設定 > 描画方式: **自動** / GPU / CPU。自動では、本物の GPU があれば GPU、なければ CPU を使う (ソフトウェア実装の OpenGL は CPU 描画より遅いため選ばない) |
| フォールバック | GPU コンテキストを作れない場合や読み戻しに失敗した場合は、自動的に CPU 描画になる |
| 出力 | プレビューと同じ方式で描く。出力用スレッドが自分の GPU コンテキストを作る |
| 対応 OS | Linux (`libEGL.so.1`)。Windows / macOS は未対応で、CPU 描画になる |
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
| Windows | NVDEC (CUDA) → D3D11VA → DXVA2 |
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

速く動くプラグインを書きやすくするために、API を整理した ([PLUGIN_API.md](PLUGIN_API.md))。バージョン 1 のプラグインはそのまま読み込める (`apiVersion` 1〜2 を受け付ける)。

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
- 公開する項目は、保存のダイアログで選ぶ (テキストの本文と、音声以外のファイルは最初から選んでおく)。ダイアログに出すトラックバーは、オブジェクト本体 (先頭のエフェクト) のものだけ (変形などまで出すと一覧が長くなりすぎる)。
- 保存したあとも、メイン以外のシーンを開いてプロパティのパラメータの行を右クリックし「テンプレートの項目として公開」で追加・削除・名前の変更ができる。
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
| 使う条件 | Linux・描画方式が GPU (自動を含む)・画面の GPU と描画に選んだ GPU が同じ (`GL_RENDERER` で比べる。違う GPU の間では共有できないので、これまでどおり読み戻す)・自動のときは画面の GPU がソフトウェアでない。`--screenshot` のときは使わない (ウィンドウを画像に描く方法では写らないため)。環境変数 `VIDEO_BUILDER_SHARED_PREVIEW=0` で使わない、`=1` で条件を問わず使う |
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
- Windows・macOS 向けの FFmpeg の同梱は、まだ作っていない (`publish-all.sh` は FFmpeg を入れない)。

---

---

*以上*
