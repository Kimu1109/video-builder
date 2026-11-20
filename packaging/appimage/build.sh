#!/usr/bin/env bash
# AppImage を作る。
#
#   packaging/appimage/build.sh [x64|arm64]
#
# 必要なもの: .NET 10 SDK。appimagetool が PATH にあれば AppImage まで作り、
# なければ AppDir (展開した状態) を作ったところで止まる。
#
# FFmpeg 9 (x264・x265 入りの GPL ビルド) は packaging/ffmpeg/build.sh で作り、usr/bin/ffmpeg に同梱する
# (そのための開発用パッケージと patchelf が要る。README の「必要なもの」を参照)。
# 作った FFmpeg は artifacts/ffmpeg に残し、2 回目からは使い回す。OpenAL は同梱しない (利用者の環境のものを使う)。
# 配布物全体は GPL-3.0-or-later の条件で配る (THIRD_PARTY_NOTICES.md)。
set -euo pipefail

arch="${1:-x64}"
case "$arch" in
  x64) appimage_arch=x86_64 ;;
  arm64) appimage_arch=aarch64 ;;
  *) echo "対応していないアーキテクチャ: $arch" >&2; exit 1 ;;
esac

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/artifacts/appimage"
appdir="$out/video-builder.AppDir"
version="$(git -C "$root" describe --tags --always 2>/dev/null || echo dev)"

rm -rf "$appdir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/scalable/apps"

dotnet publish "$root/src/VideoBuilder.App" -c Release -r "linux-$arch" --self-contained true \
  -p:DebugType=none -p:PublishSingleFile=false -p:PublishReadyToRun=true -o "$appdir/usr/bin"

# FFmpeg と、それが使うコーデックのライブラリを同梱する (FFmpegLoader は実行ファイルの隣の ffmpeg フォルダを探す)
# FFmpeg はこの PC 向けにしか作れないので、ほかのアーキテクチャの AppImage は、そのアーキテクチャの PC で作る
if [ "$appimage_arch" != "$(uname -m)" ]; then
  echo "FFmpeg を同梱するため、$appimage_arch の AppImage は $appimage_arch の PC で作ってください" >&2
  exit 1
fi
"$root/packaging/ffmpeg/build.sh" --prefix "$root/artifacts/ffmpeg/linux-$arch" --bundle "$appdir/usr/bin/ffmpeg"
# ライセンスの文書
mkdir -p "$appdir/usr/share/licenses/video-builder"
cp "$root/LICENSE" "$root/COPYING" "$root/COPYING.LESSER" "$root/THIRD_PARTY_NOTICES.md" "$appdir/usr/share/licenses/video-builder/"

cp "$root/packaging/video-builder.desktop" "$appdir/usr/share/applications/"
cp "$root/packaging/video-builder.desktop" "$appdir/"
cp "$root/packaging/video-builder.svg" "$appdir/usr/share/icons/hicolor/scalable/apps/"
cp "$root/packaging/video-builder.svg" "$appdir/"

cat > "$appdir/AppRun" <<'APPRUN'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/bin/video-builder" "$@"
APPRUN
chmod +x "$appdir/AppRun" "$appdir/usr/bin/video-builder"

echo "AppDir: $appdir"
if command -v appimagetool >/dev/null 2>&1; then
  ARCH="$appimage_arch" appimagetool "$appdir" "$out/video-builder-$version-$appimage_arch.AppImage"
  echo "AppImage: $out/video-builder-$version-$appimage_arch.AppImage"
else
  echo "appimagetool が見つからないため、AppDir までで終了します。"
  echo "https://github.com/AppImage/appimagetool から入手して、もう一度実行してください。"
fi
