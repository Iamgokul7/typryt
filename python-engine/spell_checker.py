"""Layer 1 spell checker: symspellpy lookup with a simple confidence score."""

from dataclasses import dataclass
from importlib.resources import files

from symspellpy import SymSpell, Verbosity

DICTIONARY_NAME = "frequency_dictionary_en_82_765.txt"
MAX_EDIT_DISTANCE = 2

# Phase 1 mapping from edit distance to confidence. Phase 4 replaces this.
CONFIDENCE_BY_DISTANCE = {0: 1.0, 1: 0.9, 2: 0.6}


@dataclass
class CorrectionResult:
    original: str
    correction: str
    confidence: float


class SpellChecker:
    def __init__(self) -> None:
        self._symspell = SymSpell(max_dictionary_edit_distance=MAX_EDIT_DISTANCE, prefix_length=7)
        dictionary_path = files("symspellpy") / DICTIONARY_NAME
        if not self._symspell.load_dictionary(str(dictionary_path), term_index=0, count_index=1):
            raise RuntimeError(f"Could not load symspellpy dictionary: {dictionary_path}")

    def correct(self, word: str) -> CorrectionResult:
        # Only plain alphabetic words are checked. Numbers, mixed alphanumerics,
        # and punctuation pass through unchanged. ALL CAPS (LOL, CEO) is never corrected.
        if not word.isalpha() or (len(word) > 1 and word.isupper()):
            return CorrectionResult(word, word, 1.0)

        lowered = word.lower()
        suggestions = self._symspell.lookup(lowered, Verbosity.TOP, max_edit_distance=MAX_EDIT_DISTANCE)
        if not suggestions:
            return CorrectionResult(word, word, 1.0)

        best = suggestions[0]
        if best.term == lowered:
            return CorrectionResult(word, word, 1.0)

        correction = best.term
        if word[0].isupper():
            correction = correction[0].upper() + correction[1:]
        confidence = CONFIDENCE_BY_DISTANCE.get(best.distance, 0.0)
        return CorrectionResult(word, correction, confidence)
