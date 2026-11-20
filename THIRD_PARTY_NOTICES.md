# ライセンスと、使っているソフトウェア

## video-builder 本体

video-builder は **GNU Lesser General Public License v3.0 以降 (LGPL-3.0-or-later)** で配布する。
条文は [COPYING.LESSER](COPYING.LESSER) (LGPL-3.0) と、それが元にしている [COPYING](COPYING) (GPL-3.0) にある。

## 配布物 (AppImage・Flatpak) のライセンス

配布物には、**libx264・libx265 を有効にした FFmpeg (GPL-3.0-or-later のビルド)** を同梱する。
GPL のライブラリと一緒に動く形で配るので、**配布物全体は GPL-3.0-or-later の条件で配る**。
video-builder 本体のソースは LGPL-3.0-or-later のままで、GPL-3.0 と両立する。

- 同梱する FFmpeg は `packaging/ffmpeg/build.sh` で作る (Flatpak はマニフェストの中で同じ設定で作る)。
  バージョン・configure の設定・ソースの入手先は、配布物の中の `licenses/ffmpeg/CONFIGURATION.txt` にも入れる。
- GPL の求めに従い、配布物を公開するときは、同梱したものと同じバージョンのソース
  (video-builder、FFmpeg、x264、x265) を同じ場所から入手できるようにする (リリースに添付する)。
- プラグインは同じプロセスの中で動くので、GPL の FFmpeg を同梱した配布物と一緒に配るプラグインは、
  GPL-3.0 と両立するライセンスにする必要がある。

## 使っているソフトウェア

| ソフトウェア | ライセンス | 入手先 | 備考 |
|---|---|---|---|
| FFmpeg 9.0.2 | GPL-3.0-or-later (このビルド。`--enable-gpl --enable-version3`) | https://ffmpeg.org/ | 同梱する。動画・音声の読み書き |
| x264 | GPL-2.0-or-later | https://code.videolan.org/videolan/x264 | FFmpeg に組み込む (H.264 の出力) |
| x265 | GPL-2.0-or-later | https://bitbucket.org/multicoreware/x265_git | FFmpeg に組み込む (H.265 の出力) |
| libvpx | BSD-3-Clause | https://chromium.googlesource.com/webm/libvpx | FFmpeg に組み込む (VP9) |
| dav1d | BSD-2-Clause | https://code.videolan.org/videolan/dav1d | FFmpeg に組み込む (AV1 の読み込み) |
| Opus | BSD-3-Clause | https://opus-codec.org/ | FFmpeg に組み込む |
| libnuma | LGPL-2.1-or-later | https://github.com/numactl/numactl | x265 が使う。AppImage に同梱する |
| OpenAL Soft | LGPL-2.0-or-later | https://openal-soft.org/ | 音声の再生。Flatpak には同梱する (1.24.3) |
| Avalonia UI | MIT | https://avaloniaui.net/ | |
| SkiaSharp / HarfBuzzSharp | MIT | https://github.com/mono/SkiaSharp | |
| Svg.Skia | MIT | https://github.com/wieslawsoltes/Svg.Skia | |
| FFmpeg.AutoGen | MIT | https://github.com/Ruslan-B/FFmpeg.AutoGen | |
| Silk.NET (OpenAL) | MIT | https://github.com/dotnet/Silk.NET | |
| MoonSharp | BSD-3-Clause | https://github.com/moonsharp-devs/moonsharp | Lua スクリプト |
| .NET ランタイム | MIT | https://github.com/dotnet/runtime | self-contained で同梱する |
| Noto Sans JP | SIL Open Font License 1.1 | https://fonts.google.com/noto | 同梱する ([OFL.txt](src/VideoBuilder.App/Assets/Fonts/OFL.txt)) |
| Inter | SIL Open Font License 1.1 | https://rsms.me/inter/ | Avalonia.Fonts.Inter |
| Material Symbols (Sharp) | Apache-2.0 | https://github.com/google/material-design-icons | アイコン。Apache-2.0 は GPL-2.0 と両立しないため、本体・配布物は v3 にしている |

H.264・H.265・AAC などには、著作権のライセンスとは別に特許がある。配布する国や方法によっては、別途確認が要る。
