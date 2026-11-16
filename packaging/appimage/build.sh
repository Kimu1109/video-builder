#!/usr/bin/env bash
# AppImage を作る。
#
#   packaging/appimage/build.sh [x64|arm64]
#
# 必要なもの: .NET 10 SDK。appimagetool が PATH にあれば AppImage まで作り、
# なければ AppDir (展開した状態) を作ったところで止まる。
#
# FFmpeg と OpenAL は同梱しない (利用者の環境のものを使う)。README の「必要なもの」を参照。
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
  -p:DebugType=none -p:PublishSingleFile=false -o "$appdir/usr/bin"

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
