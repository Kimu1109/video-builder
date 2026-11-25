#!/usr/bin/env bash
# video-builder で使う FFmpeg (共有ライブラリ) を、ffmpeg.org のソースから作る。Linux・macOS・Windows (MSYS2) で動く。
#
#   packaging/ffmpeg/build.sh --prefix <インストール先> [--bundle <同梱先>]
#
# --prefix  : make install する場所 (CI・開発用。Linux・macOS は <prefix>/lib、Windows は <prefix>/bin を VIDEO_BUILDER_FFMPEG に指定する)
# --bundle  : 配布物に入れるライブラリを <同梱先> に集める (発行したアプリの ffmpeg フォルダにする)。
#             FFmpeg のライブラリと、それが使うコーデックのライブラリ (x264・x265 など) を写し、同じフォルダから互いを見つけられるようにする。
#               Linux  : RPATH を $ORIGIN にする (patchelf が要る)
#               macOS  : 参照先を @loader_path にして、署名し直す (ad-hoc。install_name_tool・codesign は Xcode のコマンドラインツール)
#               Windows: 必要な DLL を写すだけ (読み込むときに FFmpegLoader が SetDllDirectory でこのフォルダを足す)
#             ライセンスの文書は <同梱先>/licenses に入れる。
#
# ライセンス: --enable-gpl --enable-version3 で作るので、出来上がる FFmpeg は GPL-3.0-or-later になる
# (libx264・libx265 が GPL のため)。video-builder 本体は LGPL-3.0-or-later で、配布物全体は GPL-3.0 の条件で配る。
# THIRD_PARTY_NOTICES.md と設計書 22.33・22.34 を参照。
#
# 必要なもの:
#   Ubuntu 24.04 : build-essential nasm pkg-config libx264-dev libx265-dev libvpx-dev libdav1d-dev libopus-dev libffmpeg-nvenc-dev
#                  libva-dev libdrm-dev (VAAPI。libva は同梱せず、利用者の環境のものを使う。GPU のドライバと組で動くため)
#                  (--bundle のときは patchelf も)
#   macOS        : Xcode のコマンドラインツール、Homebrew の nasm pkgconf x264 x265 libvpx dav1d opus
#   Windows      : MSYS2 の UCRT64 環境で、mingw-w64-ucrt-x86_64-{toolchain,nasm,pkgconf,x264,x265,libvpx,dav1d,opus,ffnvcodec-headers,amf-headers}
#                  と make diffutils curl xz。UCRT64 のシェルから実行する
#   Windows ARM64: MSYS2 の CLANGARM64 環境で、mingw-w64-clang-aarch64-{toolchain,pkgconf,x264,x265,libvpx,dav1d,opus}
#                  と make diffutils curl xz。CLANGARM64 のシェルから、ARM64 の Windows の上で実行する
#                  (NVENC・AMF・nasm は要らない。GPU は D3D11VA・D3D12VA・Media Foundation で使う)
# NVENC (NVIDIA) のヘッダ (ffnvcodec) と AMF のヘッダは、あれば configure が見つけて有効にする (macOS にはない)。
set -euo pipefail

FFMPEG_VERSION="9.0.2"
FFMPEG_SHA256="8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e"

# 配布物 (GPL) に入れて有効にするコーデック。ここを変えたら THIRD_PARTY_NOTICES.md と Flatpak のマニフェストも直す
CONFIGURE_FLAGS=(
  --enable-shared --disable-static --disable-doc --disable-debug
  --enable-gpl --enable-version3
  --enable-libx264 --enable-libx265
  --enable-libvpx --enable-libdav1d --enable-libopus
)

# macOS だけに足す設定: Homebrew の X11 のライブラリ (libxcb など) を見つけて画面取り込みを有効にしてしまい、
# 同梱するものが増えるので切る (アプリは使わない)
MACOS_FLAGS=(--disable-libxcb --disable-xlib --disable-sdl2)

# Windows だけに足す設定: GPU でのデコード (D3D11VA・DXVA2) と、Media Foundation のエンコーダ (h264_mf など) を必ず入れる。
# Windows on ARM (Snapdragon) では NVENC・Quick Sync・AMF がないので、GPU のエンコーダは Media Foundation からしか使えない。
# D3D12VA は、MSYS2 のヘッダが新しければ configure が自動で有効にする。
# 画面の取り込み (gfxcapture。Windows.Graphics.Capture を使う C++ のフィルタ) はアプリが使わないうえ、
# CLANGARM64 の libc++ では作れない (<chrono> の中で ratio などが見つからない) ので切る
WINDOWS_FLAGS=(--enable-d3d11va --enable-dxva2 --enable-mediafoundation --disable-filter=gfxcapture)

# Linux の --bundle で写すライブラリ (ファイル名の先頭)。libc や GPU のドライバ (libva・libdrm) などは利用者の環境のものを使う
BUNDLE_PATTERN='^(libavcodec|libavformat|libavutil|libavfilter|libavdevice|libswscale|libswresample|libx264|libx265|libvpx|libdav1d|libopus|libnuma)\.so'

prefix="" bundle=""
while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) prefix="$2"; shift 2 ;;
    --bundle) bundle="$2"; shift 2 ;;
    --print-version) echo "$FFMPEG_VERSION"; exit 0 ;;
    --print-configure) echo "${CONFIGURE_FLAGS[*]}"; exit 0 ;;
    --print-windows-configure) echo "${WINDOWS_FLAGS[*]}"; exit 0 ;;
    *) echo "不明な引数: $1" >&2; exit 1 ;;
  esac
done
[ -n "$prefix" ] || { echo "--prefix を指定してください" >&2; exit 1; }
prefix="$(mkdir -p "$prefix" && cd "$prefix" && pwd)"

case "$(uname -s)" in
  Linux*) os=linux ;;
  Darwin*) os=macos ;;
  MINGW*|MSYS*|UCRT*|CLANG*) os=windows ;;   # CLANGARM64 (ARM64 の Windows) もここ
  *) echo "対応していない OS: $(uname -s)" >&2; exit 1 ;;
esac

cpus() { nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4; }
check_sha256() {
  if command -v sha256sum >/dev/null; then echo "$FFMPEG_SHA256  $1" | sha256sum -c -
  else echo "$FFMPEG_SHA256  $1" | shasum -a 256 -c -; fi
}

work="${FFMPEG_WORK_DIR:-$(mktemp -d)}"
mkdir -p "$work"
tarball="$work/ffmpeg-$FFMPEG_VERSION.tar.xz"
licenses="$prefix/share/licenses/ffmpeg"

# 作り終えた印は CONFIGURATION.txt (ライブラリのファイル名は OS ごとに違うため)
if [ ! -f "$licenses/CONFIGURATION.txt" ]; then
  if [ ! -f "$tarball" ]; then
    curl -sSfL --retry 3 -o "$tarball" "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz"
  fi
  check_sha256 "$tarball"
  rm -rf "$work/ffmpeg-$FFMPEG_VERSION"
  tar -C "$work" -xJf "$tarball"
  extra=()
  [ "$os" = macos ] && extra=("${MACOS_FLAGS[@]}")
  [ "$os" = windows ] && extra=("${WINDOWS_FLAGS[@]}")
  # MSYS2 の CLANG64・CLANGARM64: コンパイラは clang。何も指定しないと configure は gcc を探し、
  # Windows の PATH にある別の gcc (ランナーの x64 の MinGW など) を使ってしまう。
  # また ARM64 の Windows の MSYS2 はシェル自体が x64 のエミュレーションで動き、uname -m が x86_64 を返すので、CPU も指定する
  if [ "$os" = windows ] && [[ "${MSYSTEM:-}" == CLANG* ]]; then
    extra+=(--cc=clang --cxx=clang++ --target-os=mingw32 --arch="${MSYSTEM_CARCH:-$(uname -m)}")
  fi
  (
    cd "$work/ffmpeg-$FFMPEG_VERSION"
    if ! ./configure --prefix="$prefix" "${CONFIGURE_FLAGS[@]}" ${extra[@]+"${extra[@]}"}; then
      # 何が見つからなかったのか分かるよう、configure の記録の最後を出す
      tail -n 60 ffbuild/config.log >&2 || true
      exit 1
    fi
    make -j"$(cpus)"
    make install
  )
  # 配布するときにソースと一緒に置けるよう、ライセンスの文書と configure の設定を残す
  mkdir -p "$licenses"
  cp "$work/ffmpeg-$FFMPEG_VERSION/COPYING.GPLv3" "$work/ffmpeg-$FFMPEG_VERSION/LICENSE.md" "$licenses/"
  {
    echo "ffmpeg $FFMPEG_VERSION ($os ${MSYSTEM_CARCH:-$(uname -m)}): ${CONFIGURE_FLAGS[*]}${extra[@]+ ${extra[*]}}"
    echo "source: https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz (sha256 $FFMPEG_SHA256)"
  } > "$licenses/CONFIGURATION.txt"
else
  echo "FFmpeg はすでに $prefix にあります (作り直すときは消してから実行)"
fi

[ -n "$bundle" ] || exit 0
mkdir -p "$bundle/licenses"
bundle="$(cd "$bundle" && pwd)"

bundle_linux() {
  command -v patchelf >/dev/null || { echo "--bundle には patchelf が要ります" >&2; exit 1; }
  # FFmpeg のライブラリと、それが使うコーデックのライブラリ (ldd で見つかるもの) を写す
  # 読み込むのは soname の名前 (libavcodec.so.63 など) だけなので、それだけを写す (libavcodec.so.63.1.102 は写さない)
  local libs deps f name
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
}

# macOS: OS に入っているもの (/usr/lib・/System) 以外の参照先を、たどって全部集める
mac_deps() { otool -L "$1" | tail -n +2 | awk '{print $1}' | grep -vE '^(/usr/lib/|/System/)' || true; }
mac_resolve() {
  # @rpath/… や @loader_path/… は、prefix と Homebrew の lib から探す
  local ref="$1" from="$2" name d
  case "$ref" in
    @rpath/*|@loader_path/*|@executable_path/*)
      name="${ref##*/}"
      for d in "$(dirname "$from")" "$prefix/lib" "$(brew --prefix 2>/dev/null)/lib"; do
        [ -e "$d/$name" ] && { echo "$d/$name"; return; }
      done
      echo "見つからない参照: $ref ($from)" >&2; exit 1 ;;
    *) echo "$ref" ;;
  esac
}
bundle_macos() {
  local queue=() seen=" " f ref path name
  # libavcodec.63.dylib のような名前 (AutoGen が読む名前) のものだけを起点にする
  for f in "$prefix"/lib/lib*.dylib; do
    [[ "$(basename "$f")" =~ ^lib[a-z0-9_]+\.[0-9]+\.dylib$ ]] && queue+=("$f")
  done
  while [ ${#queue[@]} -gt 0 ]; do
    f="${queue[0]}"; queue=("${queue[@]:1}")
    name="$(basename "$f")"
    case "$seen" in *" $name "*) continue ;; esac
    seen="$seen$name "
    cp -L "$f" "$bundle/$name"
    chmod 644 "$bundle/$name"
    for ref in $(mac_deps "$f"); do
      path="$(mac_resolve "$ref" "$f")"
      queue+=("$path")
    done
  done
  # 参照先を @loader_path (同じフォルダ) に書き換え、書き換えで壊れた署名を付け直す
  for f in "$bundle"/*.dylib; do
    install_name_tool -id "@loader_path/$(basename "$f")" "$f"
    for ref in $(mac_deps "$f"); do
      [ "$ref" = "@loader_path/$(basename "$ref")" ] && continue
      install_name_tool -change "$ref" "@loader_path/$(basename "$ref")" "$f"
    done
    codesign --force --sign - "$f"
  done
}

bundle_windows() {
  # FFmpeg の DLL (avcodec-63.dll など) と、MSYS2 から来る DLL (x264・x265・コンパイラの実行時ライブラリなど) を写す。
  # 参照先は objdump で DLL の import を読んでたどる (ldd は DLL を実際に読み込むので、
  # ARM64 の Windows で x64 のエミュレーションで動く MSYS2 からは ARM64 の DLL を調べられない)。
  # prefix と MSYS2 (MINGW_PREFIX) の bin にないもの (Windows に入っている DLL) は写さない
  local objdump queue=() seen=" " f name dep imports
  # llvm-objdump はどの CPU の DLL も読めるので先に使う (CLANGARM64 にある。UCRT64 は binutils の objdump)
  objdump="$(command -v llvm-objdump || command -v objdump)" || { echo "--bundle には objdump が要ります" >&2; exit 1; }
  for f in "$prefix"/bin/*.dll; do queue+=("$f"); done
  while [ ${#queue[@]} -gt 0 ]; do
    f="${queue[0]}"; queue=("${queue[@]:1}")
    name="$(basename "$f")"
    case "$seen" in *" ${name,,} "*) continue ;; esac
    seen="$seen${name,,} "
    cp "$f" "$bundle/"
    # 読めなければ止める (参照先を写し漏らした配布物を作らない)
    imports="$("$objdump" -p "$f")" || { echo "$f の参照先を読めません" >&2; exit 1; }
    for dep in $(printf '%s\n' "$imports" | awk '/DLL Name:/ {print $3}' | tr -d '\r'); do
      for d in "$prefix/bin" "${MINGW_PREFIX:-/nonexistent}/bin"; do
        if [ -e "$d/$dep" ]; then queue+=("$d/$dep"); break; fi
      done
    done
  done
}

"bundle_$os"
cp "$licenses/"* "$bundle/licenses/"
echo "同梱するライブラリ: $bundle"
ls "$bundle"
