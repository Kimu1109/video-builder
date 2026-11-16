# Video Builder 設計書

| 項目 | 内容 |
|---|---|
| 文書バージョン | 0.1 (ドラフト) |
| 作成日 | 2026-11-16 |
| 対象 | Video Builder (仮称) 初期実装 〜 v1.0 |

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
21. [未決事項](#21-未決事項)

---

## 1. 概要

Video Builder は C# と Avalonia UI で作るデスクトップ向け動画編集アプリケーションである。
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
| 言語 / ランタイム | C# 13 / .NET 9 (LTS 公開後は .NET 10 へ移行) | 指定どおり。Native AOT も検討できる |
| UI | Avalonia UI 11.x | クロスプラットフォームの XAML UI。Linux で安定して動く |
| MVVM | CommunityToolkit.Mvvm | Source Generator によりボイラープレートが少ない |
| DI | Microsoft.Extensions.DependencyInjection | 標準的で軽量 |
| 2D 描画 | SkiaSharp (Avalonia が内部で使うものと共通) | GPU (OpenGL / Vulkan) 対応。テキスト描画も強い |
| GPU エフェクト | SkiaSharp `SKRuntimeEffect` (SkSL シェーダ) | 追加依存なしでフィルタを GPU 実行できる |
| 動画デコード / エンコード | FFmpeg 7.x + `FFmpeg.AutoGen` | 対応コーデックが最も多い。ハードウェアデコードも使える |
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
│ VideoBuilder.Application  (UseCase / Command / Undo)     │
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
4. **ダーク基調**: 映像を見やすくするため、既定はダークテーマ。ライトテーマも用意する。

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

ライトテーマは同じトークン名で値だけ差し替える (`Bg.Base=#F3F3F3`, `Fg.Primary=#1E1E1E` など)。

### 7.3 タイポグラフィ

| 用途 | フォント | サイズ |
|---|---|---|
| UI 全般 | `Noto Sans CJK JP`, `Noto Sans JP`, `Inter` (フォールバック: システム既定) | 12px |
| 数値・タイムコード | `JetBrains Mono`, `Noto Sans Mono` | 12px (tabular) |
| ダイアログ見出し | UI フォント SemiBold | 13px |
| タイムライン内ラベル | UI フォント | 11px |

フォントはアプリに同梱し (OFL ライセンス)、Linux 環境ごとの差を減らす。

### 7.4 サイズ / 余白

| 項目 | 値 |
|---|---|
| 基本グリッド | 4px |
| コントロール高さ | 22px (小) / 24px (標準) |
| ボタン内余白 | 水平 8px / 垂直 2px |
| アイコンサイズ | 16px (標準) / 14px (タイムライン内) / 20px (ツールバー大) |
| 枠線 | 1px |
| フォーカスリング | 1px `Accent` (内側にオフセット 0) |
| CornerRadius | **0 (全コントロール)** |

### 7.5 Avalonia スタイル実装方針

- `FluentTheme` をベースにし、`ResourceDictionary` で `ControlCornerRadius` / `OverlayCornerRadius` を 0 に上書きする。
- 色は `ThemeVariant` (Dark / Light) ごとの `ResourceDictionary.ThemeDictionaries` で定義する。
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
- スクリプト: AviUtl スクリプトとの互換を目指し、**Lua (NLua / MoonSharp)** で `obj.*` API のサブセットを提供する案を検討中 ([未決事項](#21-未決事項))。

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

主な環境設定の項目: 最大画像サイズ、キャッシュサイズ、レイヤー高さ、ウィンドウモード (分離 / ドッキング)、テーマ、言語、ショートカット、ハードウェアデコードの有効化、プレビュー品質。

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
├─ Directory.Build.props          # 共通設定 (Nullable, LangVersion, TreatWarningsAsErrors)
├─ Directory.Packages.props       # 中央パッケージ管理
├─ src/
│  ├─ VideoBuilder.App/           # Avalonia エントリポイント
│  │  ├─ Views/                   # MainWindow, TimelineWindow, ObjectSettingsWindow, ExportDialog
│  │  ├─ ViewModels/
│  │  ├─ Controls/                # TimelineCanvas, TrackBar, LayerHeader, IconButton, PreviewSurface
│  │  ├─ Themes/                  # Base.axaml, Dark.axaml, Light.axaml, Icons.axaml
│  │  └─ Assets/                  # フォント, アプリアイコン
│  ├─ VideoBuilder.Application/   # コマンド, Undo, ユースケース, ショートカット
│  ├─ VideoBuilder.Core/          # Project, Scene, Layer, TimelineObject, AnimatedValue, Interpolators
│  ├─ VideoBuilder.Rendering/     # RenderEngine, Effects, TextRenderer, Cache
│  ├─ VideoBuilder.Media/         # MediaReader, MediaWriter, AudioOutput, ProxyGenerator
│  └─ VideoBuilder.Plugin.Abstractions/
├─ tests/
│  ├─ VideoBuilder.Core.Tests/
│  ├─ VideoBuilder.Rendering.Tests/
│  └─ VideoBuilder.App.Tests/
├─ tools/
│  └─ IconGen/                    # Material Symbols SVG → Icons.axaml 変換
├─ packaging/
│  ├─ appimage/
│  └─ flatpak/
└─ docs/
   └─ DESIGN.md                   # 本書
```

---

## 20. 開発ロードマップ

| マイルストーン | 内容 | 完了条件 |
|---|---|---|
| **M0: 基盤** | ソリューション作成、テーマ (角型・ダーク)、アイコン生成、ウィンドウ骨格 | 空のメイン / タイムライン / 設定ダイアログが表示される |
| **M1: タイムライン** | Core モデル、TimelineCanvas、オブジェクトの追加・移動・長さ変更、Undo | 図形オブジェクトをタイムラインで編集できる |
| **M2: プレビュー** | RenderEngine (画像・図形・テキスト)、標準描画、プレビュー表示 | 静止画とテキストの合成がプレビューに出る |
| **M3: メディア** | FFmpeg 入力、動画・音声オブジェクト、音声再生、A/V 同期 | 動画を読み込んで音声付きで再生できる |
| **M4: アニメーション** | 中間点、移動方法、トラックバー完成 | AviUtl と同じ手順で移動アニメーションが作れる |
| **M5: エフェクト** | 基本フィルタ、フィルタオブジェクト、合成モード、クリッピング | 主要フィルタ 7 種が動く |
| **M6: 出力** | 出力ダイアログ、各エンコーダ | MP4 / WebM を出力できる |
| **M7: 仕上げ (v1.0)** | シーン、グループ / カメラ制御、エイリアス、自動保存、AppImage / Flatpak | リリース |
| M8 以降 | プラグイン API 公開、スクリプト、.exo インポート、プロキシ | – |

---

## 21. 未決事項

| # | 項目 | 選択肢 | 現時点の推し |
|---|---|---|---|
| 1 | スクリプト言語 | Lua (AviUtl 互換重視) / C# Script (Roslyn) | Lua (互換性を重視) |
| 2 | 既定のウィンドウモード | 分離 (AviUtl 準拠) / ドッキング | X11 は分離、Wayland はドッキング |
| 3 | GPU バックエンド | Skia GL / Skia Vulkan | まず GL、Vulkan は後から |
| 4 | 3D (カメラ制御) の実装方式 | Skia の 3D 行列 (`SKMatrix44`) / 独自 OpenGL | `SKMatrix44` で始める |
| 5 | 色空間 | sRGB 8bit / リニア 16bit float | v1.0 は 8bit、内部設計は float に拡張できる形にする |
| 6 | アプリ名 | Video Builder (仮) | – |

---

*以上*
