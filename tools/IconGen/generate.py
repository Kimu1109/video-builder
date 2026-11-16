#!/usr/bin/env python3
"""Material Symbols (Sharp) の SVG から Avalonia 用の Icons.axaml を作る。

使い方:
    npm pack @material-symbols/svg-400 && tar xzf material-symbols-svg-400-*.tgz
    python3 tools/IconGen/generate.py package/sharp

icons.txt に書いたアイコンだけを取り込む (フォント全体は同梱しない)。
パスは viewBox "0 -960 960 960" のまま出力し、Icon コントロール側で拡大縮小する。
"""
import pathlib
import re
import sys

here = pathlib.Path(__file__).resolve().parent
out = here.parent.parent / "src" / "VideoBuilder.App" / "Themes" / "Icons.axaml"


def path_data(svg: pathlib.Path) -> str:
    text = svg.read_text(encoding="utf-8")
    paths = re.findall(r'<path[^>]*\sd="([^"]+)"', text)
    if not paths:
        raise SystemExit(f"{svg}: path が見つかりません")
    return " ".join(paths)


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    src = pathlib.Path(sys.argv[1])
    lines = [
        '<ResourceDictionary xmlns="https://github.com/avaloniaui"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "  <!-- tools/IconGen/generate.py が生成。手で編集しない。",
        "       Material Symbols (Sharp) / Apache License 2.0 / Google -->",
    ]
    for raw in (here / "icons.txt").read_text(encoding="utf-8").splitlines():
        raw = raw.strip()
        if not raw or raw.startswith("#"):
            continue
        parts = raw.split()
        name, key = parts[0], parts[1]
        lines.append(f'  <StreamGeometry x:Key="Icon.{key}">{path_data(src / (name + ".svg"))}</StreamGeometry>')
        if "fill" in parts[2:]:
            lines.append(f'  <StreamGeometry x:Key="Icon.{key}.Fill">{path_data(src / (name + "-fill.svg"))}</StreamGeometry>')
    lines.append("</ResourceDictionary>")
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"{out}: {len(lines) - 5} icons")


if __name__ == "__main__":
    main()
