#!/usr/bin/env bash
# アプリに同梱するプラグインを発行して、発行したアプリのフォルダの plugins/ に置く。
#
#   packaging/publish-plugins.sh <アプリを発行したフォルダ>
#
# アプリは実行ファイルの隣の plugins/<名前>/ も読む (利用者が同じ ID のプラグインを入れていれば、そちらを優先)。
# プラグインの DLL は OS に依存しないので、どの RID のアプリにも同じものを入れる。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
app="${1:?発行したフォルダを指定してください}"

# 名前 (plugins/ の下のフォルダ名) と プロジェクト
plugins=(
  "voicevox plugins/VoicevoxPlugin/VoicevoxPlugin.csproj"
)

for entry in "${plugins[@]}"; do
  read -r name project <<<"$entry"
  out="$app/plugins/$name"
  rm -rf "$out"
  dotnet publish "$root/$project" -c Release -p:DebugType=none -o "$out" -v q -nologo
  for f in plugin.json "$(basename "$project" .csproj).dll"; do
    if [ ! -e "$out/$f" ]; then echo "!! プラグイン $name: $f がありません" >&2; exit 1; fi
  done
done
