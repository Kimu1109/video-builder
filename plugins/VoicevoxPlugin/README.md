# VOICEVOX 連携プラグイン

[VOICEVOX](https://voicevox.hiroshiba.jp/) のエンジンにセリフを送って音声を作り、タイムラインの再生位置に置くプラグインです。
字幕 (セリフのテキスト) も一緒に置けます。video-builder の配布物に同梱しています。

## 使い方

1. VOICEVOX (またはエンジンだけ) を起動しておきます。
2. メニューの「プラグイン」→「VOICEVOX」→「セリフから音声を作る...」を選びます。
3. セリフ・話者・話速・音高・抑揚・音量・前後の無音を決めて「作って置く」を押します。
   - テキストオブジェクトを 1 つ選んでから開くと、その文字がセリフに入ります。
   - 「字幕も置く」にチェックすると、字幕を目安のレイヤー (選んでいるオブジェクトのレイヤー) に、音声をそのすぐ下に置いて、グループにします。
   - 前回使った話者と調整の値を覚えています。

作った音声 (WAV) は、プロジェクトを保存していればプロジェクトの隣の `voices` フォルダに保存します。
保存していなければ、プラグインのデータフォルダ (`$XDG_DATA_HOME/video-builder/plugin-data/voicevox/voices`) に保存します。
置いた音声オブジェクトには、セリフ・話者・調整の値を残しています (あとで作り直す機能に使う予定です)。

## 設定

「プラグイン」→「VOICEVOX」→「設定...」で、エンジンの URL を変えられます (既定は `http://127.0.0.1:50021`)。
同じ API を持つエンジン (AivisSpeech など) の URL も指定できます。保存すると、つながるかを確かめます。
同じ PC のエンジン (localhost・127.0.0.1) には、プロキシの設定があっても直接つなぎます。

## クレジットと利用規約

VOICEVOX の音声を使った動画には、「VOICEVOX:ずんだもん」のようなクレジットの表記が必要です。
キャラクターごとに利用規約があるので、公開する前に確かめてください。

## 開発

- 使っているエンジンの API: `GET /version`・`GET /speakers`・`POST /audio_query`・`POST /synthesis`
- テスト: `tests/VideoBuilder.App.Tests/VoicevoxPluginTests.cs` (エンジンの代わりに小さな HTTP サーバーを立てて確かめる)
- ソースから動かすアプリで試すときは、`dotnet build plugins/VoicevoxPlugin` の出力 (`bin/Debug/net10.0`) を
  `$XDG_DATA_HOME/video-builder/plugins/voicevox/` に写します。
- 配布物には `packaging/publish-plugins.sh` が、アプリの `plugins/voicevox/` に入れます。
