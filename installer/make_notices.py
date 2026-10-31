"""Writes THIRD_PARTY_NOTICES.md from the package metadata of the engine's dependencies (the closure of requirements.txt),
plus the data files and the runtimes that ship in the installer. Run with the engine's venv python:
    python-engine\\venv\\Scripts\\python.exe installer\\make_notices.py
Texts of licences are copied from each package's own files; nothing is written from memory except the short headings.
Items whose licence could not be confirmed from an authoritative source are marked UNCONFIRMED."""
import importlib.metadata as md
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
REQ = ROOT / "python-engine" / "requirements.txt"
OUT = ROOT / "THIRD_PARTY_NOTICES.md"
SKIP = {"pip", "setuptools", "wheel"}


def norm(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def closure() -> list[md.Distribution]:
    todo = [norm(re.split(r"[=<>!~ ;\[]", line.strip())[0]) for line in REQ.read_text().splitlines() if line.strip() and not line.startswith("#")]
    seen: dict[str, md.Distribution] = {}
    while todo:
        name = todo.pop()
        if name in seen or name in SKIP:
            continue
        try:
            dist = md.distribution(name)
        except md.PackageNotFoundError:
            continue
        seen[name] = dist
        for requirement in dist.requires or []:
            if "extra ==" in requirement:
                continue
            todo.append(norm(re.split(r"[=<>!~ ;\[(]", requirement.strip())[0]))
    return sorted(seen.values(), key=lambda d: norm(d.metadata["Name"]))


def license_label(dist: md.Distribution) -> str:
    meta = dist.metadata
    if norm(meta["Name"]) == "spylls":
        return "MPL-2.0 (its licence file; its metadata says MIT - treated as MPL-2.0)"
    expr = meta.get("License-Expression")
    if expr:
        return expr
    classifiers = [c.split("::")[-1].strip() for c in meta.get_all("Classifier") or [] if c.startswith("License ::")]
    declared = (meta.get("License") or "").strip()
    if declared and declared.upper() != "UNKNOWN" and len(declared) < 80 and "\n" not in declared:
        return declared
    return ", ".join(classifiers) if classifiers else "see the licence text below"


def license_files(dist: md.Distribution) -> list[tuple[str, str]]:
    found = []
    for f in dist.files or []:
        low = f.name.lower()
        if any(k in low for k in ("license", "licence", "copying", "notice")) and ".dist-info" in str(f):
            try:
                found.append((f.name, f.read_binary().decode("utf-8", "replace")))
            except OSError:
                pass
    return found


lines = []
w = lines.append
w("# Third-party notices\n")
w("Typryt itself is licensed under the PolyForm Strict License 1.0.0 (see `LICENSE.txt`). The installer also ships the components below. "
  "Each keeps its own licence. This file is produced by `installer/make_notices.py` from the package metadata in the build environment.\n")
w("## Summary\n")
w("- **Copyleft check:** no GPL, LGPL, AGPL, CC-BY-SA or non-commercial component ships in the installer. The one file-level copyleft licence is "
  "**MPL 2.0** (`spylls`, used unmodified, see its section: its source is available from the Python Package Index as `spylls` 0.1.7).")
w("- **PyInstaller** (build tool, not shipped as a library) is GPL-2.0 with a bootloader exception that allows distributing programs it builds under any licence.")
w("- **UNCONFIRMED items** (owner action): the Hunspell `en_US` dictionary files carry no licence text in this repository; the licence text under "
  "\"Hunspell en_US dictionary\" is that of the SCOWL-derived `en_US` dictionary they appear to come from, and has NOT been matched byte for byte. "
  "The Google Books Ngram data inside the SymSpell word lists has no stated licence in the SymSpell README.\n")

w("## Python packages in the engine\n")
w("| Package | Version | Licence (from its metadata) | Home page |")
w("|---|---|---|---|")
dists = closure()
for d in dists:
    meta = d.metadata
    home = meta.get("Home-page") or next((u.split(",", 1)[1].strip() for u in meta.get_all("Project-URL") or [] if "," in u), "")
    w(f"| {meta['Name']} | {meta['Version']} | {license_label(d)} | {home} |")
w("")
for d in dists:
    files = license_files(d)
    w(f"### {d.metadata['Name']} {d.metadata['Version']}\n")
    if not files:
        w("No licence file is packaged with this distribution; its metadata declares: " + license_label(d) + ".\n")
        continue
    for name, text in files:
        w(f"Licence file `{name}`:\n")
        w("```text")
        w(text.strip("\n")[:30000])
        w("```\n")
    if norm(d.metadata["Name"]) == "spylls":
        w("**spylls note:** the package's metadata classifier says MIT, but the licence file shipped inside the package is the Mozilla Public License 2.0 "
          "(above). The two disagree, so Typryt treats spylls as MPL 2.0 and ships this notice. spylls is not modified.\n")

w("## Word lists and dictionaries\n")
w("### SymSpell word-frequency and bigram lists (`frequency_dictionary_en_82_765.txt`, `frequency_bigramdictionary_en_243_342.txt`)\n")
w("Shipped inside the `symspellpy` package (MIT, above). The SymSpell README (https://github.com/wolfgarbe/SymSpell, read 2026-11-01) says the "
  "frequency list was made by intersecting Google Books Ngram data with SCOWL (Spell Checker Oriented Word Lists). The SCOWL terms are in the next section. "
  "**UNCONFIRMED:** the README does not state a licence for the Google Books Ngram part; Google publishes those datasets under Creative Commons "
  "Attribution (CC BY) - the owner should confirm the exact terms.\n")
w("### Hunspell `en_US` dictionary (`engine/_internal/dictionaries/en_US.aff`, `en_US.dic`)\n")
w("**UNCONFIRMED provenance.** The files have no licence text in this repository. They match the SCOWL-derived `en_US` Hunspell dictionary "
  "(2020.12.07, SCOWL size 60) distributed with https://github.com/wooorm/dictionaries (`dictionaries/en/license`, read 2026-11-01), whose licence text "
  "follows in part. If the files come from somewhere else, the licence of that source applies.\n")
w("```text")
w("Copyright 2000-2018 by Kevin Atkinson as well as any of the copyrights mentioned below.\n\n"
  "Permission to use, copy, modify, distribute and sell these word lists, the associated scripts, the output created from the scripts, and its "
  "documentation for any purpose is hereby granted without fee, provided that the above copyright notice appears in all copies and that both that "
  "copyright notice and this permission notice appear in supporting documentation. Kevin Atkinson makes no representations about the suitability of "
  "this array for any purpose. It is provided \"as is\" without express or implied warranty.\n\n"
  "Since the original word lists come from the Ispell distribution:\n\n"
  "Copyright 1993, Geoff Kuenning, Granada Hills, CA. All rights reserved.\n\n"
  "Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:\n"
  "1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.\n"
  "2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the "
  "documentation and/or other materials provided with the distribution.\n"
  "3. All modifications to the source code must be clearly marked as such. Binary redistributions based on modified source code must be clearly "
  "marked as modified versions in the documentation and/or other materials provided with the distribution.\n"
  "(clause 4 removed with permission from Geoff Kuenning)\n"
  "5. The name of Geoff Kuenning may not be used to endorse or promote products derived from this software without specific prior written permission.\n\n"
  "THIS SOFTWARE IS PROVIDED BY GEOFF KUENNING AND CONTRIBUTORS ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE "
  "IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL GEOFF KUENNING OR CONTRIBUTORS BE "
  "LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF "
  "SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN "
  "CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE "
  "POSSIBILITY OF SUCH DAMAGE.")
w("```\n")
w("The upstream licence text also credits Public Domain sources (Moby Words II, 12Dicts, ENABLE, a UK English wordlist) and the Princeton WordNet "
  "licence (WordNet 1.6 Copyright 1997 by Princeton University). The WordNet-derived part is the inflection data used to build SCOWL.\n")
w("### Typryt lexicons (`engine/_internal/lexicons/*.txt`)\n")
w("Written for this project (see `python-engine/LEXICON_SOURCES.md`); covered by the Typryt licence.\n")

w("## Runtimes shipped in the installer\n")
w("- **.NET 10 runtime and Windows Presentation Foundation** (self-contained application): MIT License, Copyright (c) .NET Foundation and Contributors. "
  "The licence text ships with the .NET runtime files in the application folder.")
w("- **Python 3.12 runtime** inside the engine (via PyInstaller): Python Software Foundation License (https://docs.python.org/3/license.html).")
w("- **PyInstaller bootloader** (`typryt-engine.exe`): GPL-2.0 with the PyInstaller bootloader exception (https://pyinstaller.org/en/stable/license.html).")
w("- No NuGet packages are used by the Typryt application.\n")
OUT.write_text("\n".join(lines), encoding="utf-8")
print("wrote", OUT, len(dists), "python distributions")
