#!/usr/bin/env python3
"""tools/I18n/en.txt から、英語の対応表 (C#) を作る。  使い方: python3 tools/I18n/generate.py"""
import json
import pathlib

here = pathlib.Path(__file__).resolve().parent
out = here.parent.parent / "src" / "VideoBuilder.App" / "Services" / "Translations.En.cs"

pairs = []
for n, raw in enumerate((here / "en.txt").read_text(encoding="utf-8").splitlines(), 1):
    if not raw.strip() or raw.startswith("#"):
        continue
    if " = " not in raw:
        raise SystemExit(f"en.txt:{n}: ' = ' がありません")
    ja, en = raw.split(" = ", 1)
    pairs.append((ja.replace("\\n", "\n"), en.replace("\\n", "\n")))

keys = [p[0] for p in pairs]
dup = {k for k in keys if keys.count(k) > 1}
if dup:
    raise SystemExit(f"重複: {sorted(dup)}")

lines = [
    "// tools/I18n/generate.py が tools/I18n/en.txt から生成。手で編集しない。",
    "namespace VideoBuilder.App.Services;",
    "",
    "internal static class TranslationsEn",
    "{",
    "    public static readonly (string Ja, string En)[] Pairs =",
    "    {",
]
for ja, en in pairs:
    lines.append(f"        ({json.dumps(ja, ensure_ascii=False)}, {json.dumps(en, ensure_ascii=False)}),")
lines += ["    };", "}", ""]
out.write_text("\n".join(lines), encoding="utf-8")
print(f"{out}: {len(pairs)} entries")
