"""Correction pipeline: slang, apostrophes, then symspellpy, then Hunspell.

Order follows the spec. Each layer returns a correction with a confidence score.
The C# side applies a correction only when its confidence is above the user's threshold.
"""

from dataclasses import dataclass

from symspellpy import SymSpell, Verbosity
from importlib.resources import files

from apostrophe import apply_apostrophe
from hunspell_checker import HunspellChecker
from slang_dict import SLANG

DICTIONARY_NAME = "frequency_dictionary_en_82_765.txt"
MAX_EDIT_DISTANCE = 2

# Confidence for each source. Deterministic rules score higher than fuzzy matches.
RULE_CONFIDENCE = 0.95
SYMSPELL_CONFIDENCE_BY_DISTANCE = {1: 0.9, 2: 0.6}
HUNSPELL_SUGGESTION_CONFIDENCE = 0.5


@dataclass
class CorrectionResult:
    original: str
    correction: str
    confidence: float


def _match_case(original: str, correction: str) -> str:
    """Capitalize the correction's first letter when the original started with a capital."""
    if original[0].isupper() and correction[0].islower():
        return correction[0].upper() + correction[1:]
    return correction


class SpellChecker:
    def __init__(self) -> None:
        self._symspell = SymSpell(max_dictionary_edit_distance=MAX_EDIT_DISTANCE, prefix_length=7)
        dictionary_path = files("symspellpy") / DICTIONARY_NAME
        if not self._symspell.load_dictionary(str(dictionary_path), term_index=0, count_index=1):
            raise RuntimeError(f"Could not load symspellpy dictionary: {dictionary_path}")
        self._hunspell = HunspellChecker()

    def correct(self, word: str) -> CorrectionResult:
        # Numbers, mixed alphanumerics, and punctuation pass through.
        # ALL CAPS (LOL, CEO) is never corrected.
        if not word.isalpha() or (len(word) > 1 and word.isupper()):
            return CorrectionResult(word, word, 1.0)

        lowered = word.lower()

        # Layer 0: slang and apostrophe rules are deterministic.
        slang = SLANG.get(lowered)
        if slang is not None:
            return CorrectionResult(word, _match_case(word, slang), RULE_CONFIDENCE)

        apostrophe = apply_apostrophe(word)
        if apostrophe is not None:
            return CorrectionResult(word, apostrophe, RULE_CONFIDENCE)

        # Layer 1: symspellpy. A distance-0 match means the word is already correct.
        suggestions = self._symspell.lookup(lowered, Verbosity.TOP, max_edit_distance=MAX_EDIT_DISTANCE)
        if suggestions and suggestions[0].distance == 0:
            return CorrectionResult(word, word, 1.0)

        # Layer 2: if Hunspell accepts the word, keep it even when symspell has a guess.
        if self._hunspell.is_valid(lowered):
            return CorrectionResult(word, word, 1.0)

        if suggestions:
            best = suggestions[0]
            confidence = SYMSPELL_CONFIDENCE_BY_DISTANCE.get(best.distance, 0.0)
            return CorrectionResult(word, _match_case(word, best.term), confidence)

        hunspell_suggestions = self._hunspell.suggest(lowered)
        if hunspell_suggestions:
            return CorrectionResult(
                word, _match_case(word, hunspell_suggestions[0]), HUNSPELL_SUGGESTION_CONFIDENCE
            )

        return CorrectionResult(word, word, 1.0)
