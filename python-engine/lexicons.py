"""The lexicons and the curated misspelling map, loaded once at startup into in-memory structures.

The files live in python-engine/lexicons/ (technical words, Indian English words and loanwords, first names,
surnames, places, brands, and misspellings.txt). A word in any lexicon is never corrected. See LEXICON_SOURCES.md.
"""

import sys
from pathlib import Path

# A packaged engine unpacks its bundled files under sys._MEIPASS; from source they sit next to this file.
_BUNDLE_ROOT = Path(getattr(sys, "_MEIPASS", Path(__file__).parent))
LEXICON_DIR = _BUNDLE_ROOT / "lexicons"
MISSPELLINGS_FILE = "misspellings.txt"


class Lexicons:
    def __init__(self, folder: Path = LEXICON_DIR) -> None:
        # Every lexicon word, lowercase, in one set: a lookup is a single hash probe.
        self.words: set[str] = set()
        # Entries per lexicon file, for the startup log and the closing report.
        self.sizes: dict[str, int] = {}
        # Common misspellings: lowercase typo -> correction (an underscore in the file stands for a space).
        self.misspellings: dict[str, str] = {}

        for path in sorted(folder.glob("*.txt")):
            if path.name == MISSPELLINGS_FILE:
                continue
            entries: set[str] = set()
            for line in path.read_text(encoding="utf-8").splitlines():
                for token in line.split("#")[0].split():
                    entries.add(token.lower())
            self.sizes[path.name] = len(entries)
            self.words |= entries

        mapping = folder / MISSPELLINGS_FILE
        if mapping.exists():
            for line in mapping.read_text(encoding="utf-8").splitlines():
                line = line.split("#")[0].strip()
                if "=" not in line:
                    continue
                typo, correction = line.split("=", 1)
                self.misspellings[typo.strip().lower()] = correction.strip().replace("_", " ")
            self.sizes[MISSPELLINGS_FILE] = len(self.misspellings)
