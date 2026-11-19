#!/usr/bin/env bash
# Flatpak を作る。
#
#   packaging/flatpak/build.sh [--bundle]
#
# 必要なもの: .NET 10 SDK、flatpak、flatpak-builder、Flathub の org.freedesktop.Platform//23.08 と Sdk//23.08
# (flatpak install flathub org.freedesktop.Platform//23.08 org.freedesktop.Sdk//23.08)。
# linux-x64 向けに発行してから flatpak-builder で組み立て、利用者の環境 (--user) に入れる。
# --bundle を付けると、配布用の 1 ファイル (artifacts/flatpak/video-builder.flatpak) も作る。
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="$root/artifacts/flatpak"
mkdir -p "$out"

"$root/packaging/publish-all.sh" linux-x64

flatpak-builder --user --install --force-clean --repo="$out/repo" --state-dir="$out/.flatpak-builder" \
  "$out/build" "$here/io.github.kimu1109.VideoBuilder.yml"

if [ "${1:-}" = "--bundle" ]; then
  flatpak build-bundle "$out/repo" "$out/video-builder.flatpak" io.github.kimu1109.VideoBuilder \
    --runtime-repo=https://flathub.org/repo/flathub.flatpakrepo
  echo "バンドル: $out/video-builder.flatpak"
fi
echo "起動: flatpak run io.github.kimu1109.VideoBuilder"
