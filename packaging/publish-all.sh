#!/usr/bin/env bash
# 各 OS 向けに video-builder を発行 (self-contained) して、起動に必要なファイルがそろっているかを確かめる。
#
#   packaging/publish-all.sh [RID ...]
#
# RID を省くと linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64 のすべて。結果は artifacts/publish/<RID>/ に置く。
# ここで確かめるのは「発行できて、実行ファイルと Skia などのネイティブライブラリが入っている」ことまで。
# Windows・macOS での実際の動作は確認していない (設計書 22.4)。FFmpeg と OpenAL は同梱しない。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=(linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64)
failed=0

for rid in "${rids[@]}"; do
  out="$root/artifacts/publish/$rid"
  rm -rf "$out"
  echo "== $rid"
  if ! dotnet publish "$root/src/VideoBuilder.App" -c Release -r "$rid" --self-contained true \
       -p:DebugType=none -p:PublishSingleFile=false -o "$out" -v q -nologo; then
    echo "!! $rid: 発行に失敗しました" >&2; failed=1; continue
  fi
  case "$rid" in
    win-*)   need=(video-builder.exe libSkiaSharp.dll libHarfBuzzSharp.dll) ;;
    osx-*)   need=(video-builder libSkiaSharp.dylib libHarfBuzzSharp.dylib) ;;
    *)       need=(video-builder libSkiaSharp.so libHarfBuzzSharp.so) ;;
  esac
  for f in "${need[@]}"; do
    if [ ! -e "$out/$f" ]; then echo "!! $rid: $f がありません" >&2; failed=1; fi
  done
  echo "   $(du -sh "$out" | cut -f1)  $out"
done
exit $failed
