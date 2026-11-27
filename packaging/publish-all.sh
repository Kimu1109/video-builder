#!/usr/bin/env bash
# 各 OS 向けに video-builder を発行 (self-contained) して、起動に必要なファイルがそろっているかを確かめる。
#
#   packaging/publish-all.sh [RID ...]
#
# RID を省くと linux-x64 linux-arm64 win-x64 win-arm64 osx-x64 osx-arm64 のすべて。結果は artifacts/publish/<RID>/ に置く。
# ここで確かめるのは「発行できて、実行ファイルと Skia などのネイティブライブラリが入っている」ことまで。
#
# FFmpeg: artifacts/ffmpeg-bundle/<RID>/ があれば、発行したフォルダの ffmpeg/ に写して同梱する。
# FFmpeg はその OS の上でしか作れないので、先にその OS で packaging/ffmpeg/build.sh --bundle artifacts/ffmpeg-bundle/<RID> を
# 実行しておく (GitHub Actions の package.yml がそうしている。設計書 22.34)。なければ FFmpeg なしで発行する。
# ライセンスの文書 (LICENSE・COPYING・COPYING.LESSER・THIRD_PARTY_NOTICES.md) も licenses/ に入れる。OpenAL は同梱しない。
# 同梱するプラグインは plugins/ に入れる (packaging/publish-plugins.sh)。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=(linux-x64 linux-arm64 win-x64 win-arm64 osx-x64 osx-arm64)
failed=0

for rid in "${rids[@]}"; do
  out="$root/artifacts/publish/$rid"
  rm -rf "$out"
  echo "== $rid"
  # Linux は ReadyToRun (前もって機械語にしておく) で発行し、起動時の JIT の時間を減らす。AppImage (appimage/build.sh) と同じ。
  # Flatpak と tar.gz はこのスクリプトで発行したものを使う
  r2r=()
  case "$rid" in linux-*) r2r=(-p:PublishReadyToRun=true) ;; esac
  if ! dotnet publish "$root/src/VideoBuilder.App" -c Release -r "$rid" --self-contained true \
       -p:DebugType=none -p:PublishSingleFile=false ${r2r[@]+"${r2r[@]}"} -o "$out" -v q -nologo; then
    echo "!! $rid: 発行に失敗しました" >&2; failed=1; continue
  fi
  case "$rid" in
    # Windows は GPU 描画に使う ANGLE (av_libglesv2.dll) も要る (ARM64 版にも Avalonia が同梱する ARM64 の ANGLE が入る)
    win-*)   need=(video-builder.exe libSkiaSharp.dll libHarfBuzzSharp.dll av_libglesv2.dll) ;;
    osx-*)   need=(video-builder libSkiaSharp.dylib libHarfBuzzSharp.dylib) ;;
    *)       need=(video-builder libSkiaSharp.so libHarfBuzzSharp.so) ;;
  esac
  bundle="$root/artifacts/ffmpeg-bundle/$rid"
  if [ -d "$bundle" ]; then
    cp -R "$bundle" "$out/ffmpeg"
    case "$rid" in
      win-*) need+=(ffmpeg/avcodec-63.dll) ;;
      osx-*) need+=(ffmpeg/libavcodec.63.dylib) ;;
      *)     need+=(ffmpeg/libavcodec.so.63) ;;
    esac
  else
    echo "   (FFmpeg は同梱しない: $bundle がありません)"
  fi
  # 同梱するプラグイン (VOICEVOX 連携など)
  if ! "$root/packaging/publish-plugins.sh" "$out"; then
    echo "!! $rid: プラグインの発行に失敗しました" >&2; failed=1
  fi
  mkdir -p "$out/licenses"
  cp "$root/LICENSE" "$root/COPYING" "$root/COPYING.LESSER" "$root/THIRD_PARTY_NOTICES.md" "$out/licenses/"
  for f in "${need[@]}"; do
    if [ ! -e "$out/$f" ]; then echo "!! $rid: $f がありません" >&2; failed=1; fi
  done
  echo "   $(du -sh "$out" | cut -f1)  $out"
done
exit $failed
