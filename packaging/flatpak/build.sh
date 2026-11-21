#!/usr/bin/env bash
# Flatpak を作る。
#
#   packaging/flatpak/build.sh [--bundle] [--arch x64|arm64]
#
# 必要なもの: .NET 10 SDK、flatpak、flatpak-builder、Flathub の org.freedesktop.Platform//25.08 と Sdk//25.08
# (flatpak install flathub org.freedesktop.Platform//25.08 org.freedesktop.Sdk//25.08)。
# FFmpeg 9・x264・x265 もソースから作るので、1 回目は時間がかかる (2 回目からは flatpak-builder が使い回す)。
# linux-<arch> 向けに発行してから flatpak-builder で組み立て、利用者の環境 (--user) に入れる。
# --bundle を付けると、配布用の 1 ファイル (artifacts/flatpak/video-builder-<arch>.flatpak) も作る。
#
# --arch を省くと、この PC のアーキテクチャ (uname -m) にする。FFmpeg などはマニフェストの中でソースから作るので、
# ほかのアーキテクチャ向けに作るには、そのアーキテクチャのランタイムと qemu-user (binfmt) が要り、とても遅い。
# arm64 向けは arm64 の PC で作るのがよい。
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="$root/artifacts/flatpak"
mkdir -p "$out"

case "$(uname -m)" in
  x86_64) arch=x64 ;;
  aarch64|arm64) arch=arm64 ;;
  *) arch="" ;;
esac
bundle=0
while [ $# -gt 0 ]; do
  case "$1" in
    --bundle) bundle=1; shift ;;
    --arch) arch="$2"; shift 2 ;;
    *) echo "不明な引数: $1" >&2; exit 1 ;;
  esac
done
case "$arch" in
  x64) flatpak_arch=x86_64 ;;
  arm64) flatpak_arch=aarch64 ;;
  *) echo "対応していないアーキテクチャ: $arch" >&2; exit 1 ;;
esac

"$root/packaging/publish-all.sh" "linux-$arch"
# マニフェストは決まった場所 (artifacts/flatpak/publish) から入れるので、そこに写す
rm -rf "$out/publish"
cp -a "$root/artifacts/publish/linux-$arch" "$out/publish"

flatpak-builder --user --install --force-clean --arch="$flatpak_arch" --repo="$out/repo" --state-dir="$out/.flatpak-builder" \
  "$out/build-$flatpak_arch" "$here/io.github.kimu1109.VideoBuilder.yml"

if [ "$bundle" = 1 ]; then
  flatpak build-bundle --arch="$flatpak_arch" "$out/repo" "$out/video-builder-$arch.flatpak" io.github.kimu1109.VideoBuilder \
    --runtime-repo=https://flathub.org/repo/flathub.flatpakrepo
  echo "バンドル: $out/video-builder-$arch.flatpak"
fi
echo "起動: flatpak run io.github.kimu1109.VideoBuilder"
