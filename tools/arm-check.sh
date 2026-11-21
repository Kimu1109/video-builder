#!/usr/bin/env bash
# ARM の実機 (Raspberry Pi など) で、video-builder の GPU 描画・ハードウェアデコードに関わる構成を調べて表示する (設計書 22.37)。
#
#   tools/arm-check.sh [video-builder の実行ファイル]
#
# 何も変更しない (読むだけ)。出てきた内容を、そのまま Issue などに貼ってもらえれば、どこで GPU やデコーダを使えていないかが分かる。
# 実行ファイルを渡すと、その隣の ffmpeg フォルダ (AppImage・tar.gz に同梱したもの) に V4L2 のデコーダがあるかも調べる。
set -uo pipefail

app="${1:-}"
section() { printf '\n== %s\n' "$1"; }

section "システム"
uname -srm
[ -r /etc/os-release ] && . /etc/os-release && echo "$PRETTY_NAME"
[ -r /proc/device-tree/model ] && { tr -d '\0' < /proc/device-tree/model; echo; }

section "DRM デバイス (GPU と、画面を出す装置)"
# card ごとに、ドライバとレンダーノード (GPU で描くのに使う) を出す。レンダーノードがないものは画面を出すだけの装置
for card in /sys/class/drm/card[0-9]*; do
  [[ "$(basename "$card")" =~ ^card[0-9]+$ ]] || continue
  driver=$(basename "$(readlink -f "$card/device/driver" 2>/dev/null)" 2>/dev/null)
  render=$(ls "$card/device/drm" 2>/dev/null | grep '^renderD' | tr '\n' ' ')
  printf '%-8s driver=%-16s render=%s\n' "$(basename "$card")" "${driver:-?}" "${render:-(なし)}"
done
ls -l /dev/dri 2>/dev/null || echo "/dev/dri がありません (GPU のドライバが読み込まれていない)"
id -nG | grep -qw -e render -e video || echo "注意: このユーザーは render / video グループに入っていません"

section "EGL / OpenGL ES"
if command -v eglinfo >/dev/null; then
  # デバイスごとの GL_RENDERER と OpenGL ES の版 (video-builder は EGL のデバイスを 1 つずつ開いて調べる)
  eglinfo 2>/dev/null | grep -E '^(Device platform|Device #|.*EGL API version|.*OpenGL ES profile (renderer|version))' | head -40
else
  echo "eglinfo がありません (Debian / Ubuntu / Raspberry Pi OS: sudo apt install mesa-utils-bin)"
fi
egl=$(ls /usr/lib/*/libEGL.so.1 /usr/lib/libEGL.so.1 2>/dev/null)
if [ -n "$egl" ]; then echo "$egl"; else echo "libEGL.so.1 が見つかりません"; fi

section "V4L2 (動画のデコード・エンコードの装置)"
found=0
for v in /sys/class/video4linux/video*; do
  [ -e "$v" ] || continue
  found=1
  printf '%-8s %s\n' "$(basename "$v")" "$(cat "$v/name" 2>/dev/null)"
done
[ $found = 1 ] || echo "/dev/video* がありません"
if command -v v4l2-ctl >/dev/null; then
  # M2M (メモリからメモリへ) の装置と、扱える形式。デコーダは「出力」側に H264 / HEVC などが並ぶ
  for d in /dev/video*; do
    caps=$(v4l2-ctl -d "$d" --info 2>/dev/null | grep -i 'Video Memory-to-Memory' | head -1)
    [ -n "$caps" ] || continue
    echo "--- $d (M2M)"
    v4l2-ctl -d "$d" --list-formats-out 2>/dev/null | grep -E "^\s*\[" | head -10
  done
else
  echo "v4l2-ctl がありません (sudo apt install v4l-utils)。M2M の装置かどうかは調べずに進めます"
fi
# ステートレスのデコーダ (Raspberry Pi 5 の HEVC・Rockchip など) は /dev/media* と組で使う (video-builder はまだ使えない)
if ls /dev/media* >/dev/null 2>&1; then echo "メディア装置: $(ls /dev/media* | tr '\n' ' ')"; fi

section "VAAPI"
if command -v vainfo >/dev/null; then vainfo 2>&1 | head -15; else echo "vainfo がありません (ARM ではたいてい使えないので、なくてもよい)"; fi

if [ -n "$app" ]; then
  section "video-builder に同梱した FFmpeg"
  dir="$(dirname "$(readlink -f "$app")")/ffmpeg"
  if [ -e "$dir/libavcodec.so.63" ]; then
    file -L "$dir/libavcodec.so.63" | cut -d, -f1-2
    # V4L2 M2M のデコーダ・エンコーダ (h264_v4l2m2m など) の名前が入っているか
    grep -a -o -E '(h264|hevc|vp8|vp9|mpeg2|mpeg4)_v4l2m2m' "$dir/libavcodec.so.63" | sort -u | tr '\n' ' '; echo
  else
    echo "$dir/libavcodec.so.63 がありません"
  fi
fi

section "次にアプリで見るところ"
cat <<'EOF'
- 環境設定 > 描画: 「描画方式」の下の「現在:」が GPU (V3D・Mali などの名前) になっているか。「使う GPU」の一覧に同じ GPU が 2 つ並んでいないか
- H.264 の動画をタイムラインに置いて再生したあと、環境設定 > 動画のデコード の下に「V4L2 (h264_v4l2m2m): 使用中」と出るか
- パフォーマンスチェック (Ctrl+Shift+P) の詳細の窓で、プレビュー・再生・ベンチマークの数字
- 出力ダイアログの形式に「ハードウェア (V4L2)」が出るか。出せば最後まで書き出せるか
EOF
