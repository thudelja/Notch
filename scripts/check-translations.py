#!/usr/bin/env python3
"""Checks that every text of the Mac app has a Slovenian translation.

    swift build -Xswiftc -emit-localized-strings -Xswiftc -emit-localized-strings-path -Xswiftc .build/strings
    python3 scripts/check-translations.py .build/strings Resources/sl.lproj/Localizable.strings

The Swift compiler lists the app's localizable texts (SwiftUI literals, String(localized:)) in .stringsdata files; texts
about a count (Plural.text, Plural.noun, Format.count) are found in the sources, as "singular|plural". Every one needs a
translation in the .strings file, and a translation must keep its key's placeholders. Exits with 1 and lists what's
missing otherwise. CI runs this for the Mac app (the Windows tests do the same for the Windows app).
"""
import json
import pathlib
import re
import subprocess
import sys

SPECIFIER = re.compile(r"%(?:(\d+)\$)?(?:lld|ld|d|@|lf|f)")


def compiler_keys(folder):
    keys = set()
    for path in pathlib.Path(folder).rglob("*.stringsdata"):
        data = json.loads(path.read_text())
        for table, entries in data.get("tables", {}).items():
            if table != "Localizable":
                continue
            for entry in entries:
                keys.add(entry["key"])
    return keys


LITERAL = r'"((?:[^"\\]|\\.)*)"'
PLURAL = re.compile(r"Plural\.(?:text|noun)\(\s*[^,]+,\s*" + LITERAL + r"\s*,\s*" + LITERAL)
COUNT = re.compile(r"Format\.count\(\s*[^,]+,\s*" + LITERAL + r"(?:\s*,\s*" + LITERAL + r")?\s*\)")


def plural_keys(sources):
    keys = set()
    for path in pathlib.Path(sources).rglob("*.swift"):
        text = path.read_text()
        for match in PLURAL.finditer(text):
            keys.add(match.group(1) + "|" + match.group(2))
        for match in COUNT.finditer(text):
            singular = match.group(1)
            keys.add(singular + "|" + (match.group(2) if match.group(2) is not None else singular + "s"))
    return keys


def translations(path):
    output = subprocess.run(["plutil", "-convert", "json", "-o", "-", str(path)], check=True, capture_output=True, text=True).stdout
    return json.loads(output)


def specifiers(text):
    """The placeholders of a Mac text, by position: "%@ of %lld" → {1: "@", 2: "lld"}."""
    found = {}
    for index, match in enumerate(SPECIFIER.finditer(text.replace("%%", "")), start=1):
        position = int(match.group(1)) if match.group(1) else index
        found[position] = match.group(0).split("$")[-1].lstrip("%")
    return found


def main():
    stringsdata, strings_file = sys.argv[1], sys.argv[2]
    sources = sys.argv[3] if len(sys.argv) > 3 else "Sources"
    compiled = compiler_keys(stringsdata)
    if not compiled:
        print(f"error: no texts found in {stringsdata}; was the app built with -emit-localized-strings?")
        return 1
    plurals = plural_keys(sources)
    slovenian = translations(strings_file)

    print(f"{len(compiled)} texts and {len(plurals)} count texts in the app; {len(slovenian)} translations.")
    problems = 0
    missing = sorted(key for key in compiled | plurals if key not in slovenian)
    if missing:
        problems += len(missing)
        print(f"\n{len(missing)} text(s) without a Slovenian translation:")
        print(json.dumps(missing, ensure_ascii=False, indent=1))
    for key in sorted(compiled & slovenian.keys()):
        if specifiers(key) != specifiers(slovenian[key]):
            problems += 1
            print(f"Placeholders differ: {key!r} → {slovenian[key]!r}")
    for key in sorted(plurals & slovenian.keys()):
        if len(slovenian[key].split("|")) != 4:
            problems += 1
            print(f"Needs four forms (1, 2, 3–4, 5+): {key!r} → {slovenian[key]!r}")
    unused = sorted(slovenian.keys() - compiled - plurals)
    if unused:
        print(f"\n{len(unused)} translation(s) no longer used (fine, but can go):")
        print(json.dumps(unused, ensure_ascii=False, indent=1))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
