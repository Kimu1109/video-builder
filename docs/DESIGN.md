# video-builder 設計書

| 項目 | 内容 |
|---|---|
| 文書バージョン | 0.9 |
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
| F-14 | エイリアス (オブジェクト設定のテンプレート保存) | 推奨 |
| F-15 | スクリプト (Lua 互換レイヤー or C# スクリプト) | 将来 |
| F-16 | プラグイン (入力・出力・フィルタ) | 将来 |
| F-17 | ライト / ダークテーマ切替 (既定はシステムのテーマ・アクセントカラーに従う) | 必須 |

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
| 動画デコード / エンコード | FFmpeg + `FFmpeg.AutoGen` (現在は 6.1。[22.2](#222-設計書との差分)) | 対応コーデックが最も多い。ハードウェアデコードも使える |
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
     f. フィルタオブジェクトの場合: 現在のフレームバッファ全体に効果を適用
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

- Linux: AppImage / Flatpak には FFmpeg 共有ライブラリを同梱する (LGPL ビルド)。GPL コーデック (x264 / x265) はシステムの FFmpeg を使う設定も用意する。
- ライセンス表示を「ヘルプ > ライセンス」に表示する。

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
- (将来) AviUtl の `.exo` 読み込みに対応する。対応する効果だけ変換し、変換できないものは警告一覧に出す。

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
| エイリアス / プリセット | `$XDG_DATA_HOME/video-builder/aliases/` |
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
│  │  ├─ Views/                   # MainWindow, TimelineView, ObjectSettingsView, 各ダイアログ
│  │  ├─ Controls/                # TimelineCanvas, PreviewView, Icon, IconButton, NumberBox, MiniSlider
│  │  ├─ Services/                # PreviewService, ThemeService, AppSettings, MediaImport
│  │  └─ Themes/                  # Tokens.axaml (色), Base.axaml (スタイル), Icons.axaml (生成)
│  ├─ VideoBuilder.Editing/       # EditorSession, UndoStack, IEditCommand
│  ├─ VideoBuilder.Core/          # Project, Scene, TimelineObject, Effect, AnimatedValue, EffectRegistry
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
| M8 以降 | プラグイン API 公開、スクリプト、.exo インポート、プロキシ | – |

---

## 21. 決定事項

初版で未決だった項目は、次のとおり決定した。

| # | 項目 | 決定 | 補足 |
|---|---|---|---|
| 1 | スクリプト言語 | **Lua** | AviUtl スクリプトとの互換性を優先する。C# スクリプトは採用しない |
| 2 | 既定のウィンドウモード | **X11 は分離、Wayland はドッキング** | 環境設定でいつでも切り替えられる |
| 3 | GPU バックエンド | **まず Skia GL** | Vulkan は v1.0 以降に対応する |
| 4 | 3D (カメラ制御) の実装方式 | **`SKMatrix44`** | 性能が足りない場合に独自 OpenGL を検討する |
| 5 | 色空間 | **v1.0 は sRGB 8bit** | 内部設計は 16bit float (リニア) に拡張できる形にする |
| 6 | アプリ名 | **video-builder** | リポジトリ名と同じ。C# の名前空間は `VideoBuilder` |
| 7 | テーマ | **ライト / ダーク切替。既定はシステムに従う** | アクセントカラーもシステムに従う ([7.3](#73-テーマ切替とシステムカラー)) |
| 8 | ランタイム | **.NET 10 (LTS)** | サポート期間が長いため |
| 9 | FFmpeg | **当面 6.1 のまま** | 7.x への移行と同梱は、配布方法を決めるときに再検討する |
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
| 配布 | AppImage 用のビルドスクリプト (`packaging/appimage/build.sh`) |

### 22.2 設計書との差分

| 項目 | 設計 | 現在の実装 | 理由 |
|---|---|---|---|
| FFmpeg のバージョン | 7.x | **6.1** (`FFmpeg.AutoGen 6.1.0.1`) | Ubuntu 24.04 LTS 標準の FFmpeg が 6.1 のため。`FFmpeg.AutoGen` はライブラリのメジャーバージョンと一致している必要がある。7.x への移行は同梱方針とあわせて決める |
| 描画バックエンド | Skia GL | **GPU (EGL + OpenGL ES) と CPU の切替**。既定は自動 | 下の [22.5](#225-gpu-描画) を参照 |
| クロマキー | SkSL シェーダ | C# のピクセル処理 (GPU 描画中も、この効果だけ CPU) | SkiaSharp 2.88 の `SKRuntimeEffect` は描画時にネイティブ側でクラッシュするため |
| MVVM / DI / ログ | CommunityToolkit.Mvvm / MS.DI / Serilog | 未使用 (コードビハインド + 静的な `Svc`) | 画面数が少ない間は依存を増やさない。画面が増えたら導入する |
| フォント同梱 | Noto Sans JP などを同梱 | Inter と Noto Sans JP (Regular / Bold) を同梱。等幅フォントはシステムのものを使用 | – |
| メディアプール | 素材を ID で参照 | オブジェクトがファイルパスを直接持つ | 単純化。素材の再リンク機能を作るときに導入する |
| 再生位置の単位 | (未定義) | 秒 | AviUtl はフレームだが、フレームレートを変えても意味が変わらない秒にした |

### 22.3 未実装

| 項目 | 状況 |
|---|---|
| ハードウェアデコード | 未着手。GPU のある環境で確認しながら実装する必要がある (ハードウェアエンコードは [22.11](#2211-ハードウェアエンコード)) |
| プロキシ生成 | 未着手 |
| `.exo` インポート | 未着手。実際の `.exo` ファイルで確認しながら実装する必要がある |
| プラグインの音声効果・入力・出力 | API が未定義 (今は映像のフィルタ・メディアオブジェクト・移動方法のみ) |
| カメラ制御の奥行きによる並べ替え・深度ぼけ | 未実装。描画順は常にレイヤー順 |
| Flatpak | 未着手 |
| AppImage | AppDir を作って起動するところまで確認。`appimagetool` による AppImage 化は未確認 |
| Windows / macOS | 動作未確認。GPU 描画は Linux のみ |

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
   - `host.RegisterFilter(定義, IVideoEffect)` … フィルタ効果
   - `host.RegisterMediaObject(定義, IVideoSource)` … メディアオブジェクト
   - `host.RegisterInterpolator(IInterpolator)` … トラックバーの移動方法
3. `plugin.json` (id / name / version / apiVersion / assembly) と DLL を、`$XDG_DATA_HOME/video-builder/plugins/<名前>/` に置く。

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
| フィルタ結果のキャッシュ | 元の絵とパラメータが前のフレームと同じなら、フィルタの結果を使い回す (動かないテロップの縁取りや、位置だけ動く図形の影など)。時刻で結果が変わる効果 (フェード・スクリプト制御・プラグイン) は対象外 |
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

**確認できていないこと**: 開発環境に GPU がないため、ハードウェアでの実際のエンコードは一度も動かしていない。
確認したのは、(1) 使えない環境で検出が「なし」になり、出力を試みても安全に失敗すること、(2) エンコーダごとの設定の組み立て、
(3) ソフトウェアエンコードが従来どおり動くこと、の 3 点。GPU のある環境でテストを実行すると、使えるエンコーダそれぞれで
短い動画を書き出して読み戻すテスト (`Usable_hardware_encoders_produce_a_readable_file`) が動く。

---

---

*以上*
