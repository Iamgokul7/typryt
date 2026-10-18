"""Layer 2 spell checker: Hunspell dictionary via the pure-Python spylls package.

spylls reads the same .aff/.dic files as Hunspell, so no C++ build is needed.
The en_US dictionary lives in python-engine/dictionaries/.
"""

import sys
from pathlib import Path

from spylls.hunspell import Dictionary

# A packaged engine unpacks its bundled files under sys._MEIPASS; from source they sit next to this file.
_BUNDLE_ROOT = Path(getattr(sys, "_MEIPASS", Path(__file__).parent))
DICTIONARY_DIR = _BUNDLE_ROOT / "dictionaries"
DICTIONARY_BASENAME = "en_US"


class HunspellChecker:
    def __init__(self, dictionary_dir: Path = DICTIONARY_DIR) -> None:
        self._dictionary = Dictionary.from_files(str(dictionary_dir / DICTIONARY_BASENAME))

    def is_valid(self, word: str) -> bool:
        return self._dictionary.lookup(word)

    def suggest(self, word: str) -> list[str]:
        return list(self._dictionary.suggest(word))
