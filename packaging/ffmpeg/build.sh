#!/usr/bin/env bash
# video-builder で使う FFmpeg (共有ライブラリ) を、ffmpeg.org のソースから作る。
#
#   packaging/ffmpeg/build.sh --prefix <インストール先> [--bundle <同梱先>]
#
# --prefix  : make install する場所 (CI・開発用。<prefix>/lib を VIDEO_BUILDER_FFMPEG に指定する)
# --bundle  : 配布物に入れるライブラリを <同梱先> に集める (AppImage などで使う)。
#             FFmpeg のライブラリと、コーデックのライブラリ (x264・x265 など) を写し、
#             同じフォルダから互いを見つけられるように RPATH を $ORIGIN にする (patchelf が要る)。
#             ライセンスの文書は <同梱先>/licenses に入れる。
#
# ライセンス: --enable-gpl --enable-version3 で作るので、出来上がる FFmpeg は GPL-3.0-or-later になる
# (libx264・libx265 が GPL のため)。video-builder 本体は LGPL-3.0-or-later で、配布物全体は GPL-3.0 の条件で配る。
# THIRD_PARTY_NOTICES.md と設計書 22.33 を参照。
#
# 必要なもの (Ubuntu 24.04): build-essential nasm pkg-config libx264-dev libx265-dev libvpx-dev libdav1d-dev libopus-dev
#                            (--bundle のときは patchelf も)
set -euo pipefail

FFMPEG_VERSION="9.0.2"
FFMPEG_SHA256="8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e"

# 配布物 (GPL) に入れて有効にするコーデック。ここを変えたら THIRD_PARTY_NOTICES.md も直す
CONFIGURE_FLAGS=(
  --enable-shared --disable-static --disable-doc --disable-debug
  --enable-gpl --enable-version3
  --enable-libx264 --enable-libx265
  --enable-libvpx --enable-libdav1d --enable-libopus
)

# --bundle で写すライブラリ (ファイル名の先頭)。libc や GPU のドライバ (libva・libdrm) などは利用者の環境のものを使う
BUNDLE_PATTERN='^(libavcodec|libavformat|libavutil|libavfilter|libavdevice|libswscale|libswresample|libx264|libx265|libvpx|libdav1d|libopus|libnuma)\.so'

prefix="" bundle=""
while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) prefix="$2"; shift 2 ;;
    --bundle) bundle="$2"; shift 2 ;;
    --print-version) echo "$FFMPEG_VERSION"; exit 0 ;;
    --print-configure) echo "${CONFIGURE_FLAGS[*]}"; exit 0 ;;
    *) echo "不明な引数: $1" >&2; exit 1 ;;
  esac
done
[ -n "$prefix" ] || { echo "--prefix を指定してください" >&2; exit 1; }
prefix="$(mkdir -p "$prefix" && cd "$prefix" && pwd)"

work="${FFMPEG_WORK_DIR:-$(mktemp -d)}"
mkdir -p "$work"
tarball="$work/ffmpeg-$FFMPEG_VERSION.tar.xz"

if [ ! -f "$prefix/lib/libavcodec.so" ]; then
  if [ ! -f "$tarball" ]; then
    curl -sSfL --retry 3 -o "$tarball" "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz"
  fi
  echo "$FFMPEG_SHA256  $tarball" | sha256sum -c -
  rm -rf "$work/ffmpeg-$FFMPEG_VERSION"
  tar -C "$work" -xJf "$tarball"
  (
    cd "$work/ffmpeg-$FFMPEG_VERSION"
    ./configure --prefix="$prefix" "${CONFIGURE_FLAGS[@]}"
    make -j"$(nproc)"
    make install
  )
  # 配布するときにソースと一緒に置けるよう、ライセンスの文書と configure の設定を残す
  mkdir -p "$prefix/share/licenses/ffmpeg"
  cp "$work/ffmpeg-$FFMPEG_VERSION/COPYING.GPLv3" "$work/ffmpeg-$FFMPEG_VERSION/LICENSE.md" "$prefix/share/licenses/ffmpeg/"
  echo "ffmpeg $FFMPEG_VERSION: ${CONFIGURE_FLAGS[*]}" > "$prefix/share/licenses/ffmpeg/CONFIGURATION.txt"
  echo "source: https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz (sha256 $FFMPEG_SHA256)" >> "$prefix/share/licenses/ffmpeg/CONFIGURATION.txt"
else
  echo "FFmpeg はすでに $prefix にあります (作り直すときは消してから実行)"
fi

if [ -n "$bundle" ]; then
  command -v patchelf >/dev/null || { echo "--bundle には patchelf が要ります" >&2; exit 1; }
  mkdir -p "$bundle/licenses"
  # FFmpeg のライブラリと、それが使うコーデックのライブラリ (ldd で見つかるもの) を写す
  # 読み込むのは soname の名前 (libavcodec.so.63 など) だけなので、それだけを写す (libavcodec.so.63.1.102 は写さない)
  libs=$(ls "$prefix"/lib/lib*.so.* 2>/dev/null | grep -E '\.so\.[0-9]+$')
  deps=$(for l in $libs; do ldd "$l" | awk '$3 ~ /^\// {print $3}'; done | sort -u)
  for f in $libs $deps; do
    name="$(basename "$f")"
    if [[ "$name" =~ $BUNDLE_PATTERN ]]; then
      cp -L "$f" "$bundle/$name"
      chmod 644 "$bundle/$name"
      patchelf --set-rpath '$ORIGIN' "$bundle/$name"
    fi
  done
  cp "$prefix/share/licenses/ffmpeg/"* "$bundle/licenses/"
  echo "同梱するライブラリ: $bundle"
  ls "$bundle"
fi
