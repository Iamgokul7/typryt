"""Correction pipeline for one word.

Order: lone "i", slang, apostrophes, abbreviations, symspellpy, Hunspell.
Capitalization after a sentence end is applied last. Each result carries a
confidence between 0 and 1; the C# side applies a correction only when that
confidence meets the user's level threshold.
"""

from dataclasses import dataclass
from importlib.resources import files

from symspellpy import SymSpell, Verbosity

from abbreviation_dict import ABBREVIATIONS
from apostrophe import apply_apostrophe
from capitalization import capitalize_for_context
from confidence import (
    HUNSPELL_SUGGESTION_CONFIDENCE,
    RULE_CONFIDENCE,
    symspell_confidence,
)
from hunspell_checker import HunspellChecker
from learner import Learner
from slang_dict import SLANG

DICTIONARY_NAME = "frequency_dictionary_en_82_765.txt"
MAX_EDIT_DISTANCE = 2


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
    def __init__(self, learner: Learner) -> None:
        self._learner = learner
        self._symspell = SymSpell(max_dictionary_edit_distance=MAX_EDIT_DISTANCE, prefix_length=7)
        dictionary_path = files("symspellpy") / DICTIONARY_NAME
        if not self._symspell.load_dictionary(str(dictionary_path), term_index=0, count_index=1):
            raise RuntimeError(f"Could not load symspellpy dictionary: {dictionary_path}")
        self._hunspell = HunspellChecker()

    def correct(
        self,
        word: str,
        previous_word: str | None = None,
        abbreviations_enabled: bool = True,
        learning_enabled: bool = True,
    ) -> CorrectionResult:
        # Numbers, mixed alphanumerics, and punctuation pass through.
        # ALL CAPS (LOL, CEO) is never corrected.
        if not word.isalpha() or (len(word) > 1 and word.isupper()):
            return CorrectionResult(word, word, 1.0)

        # Personal words and words the user undid come first, before every other rule.
        if self._learner.should_skip(word):
            return CorrectionResult(word, word, 1.0)

        result = self._word_correction(word, abbreviations_enabled, learning_enabled)

        # Capitalize after a sentence end. Only applies when the word is otherwise unchanged
        # or already corrected; the confidence stays with the underlying correction.
        capitalized = capitalize_for_context(result.correction, previous_word)
        if capitalized != result.correction:
            return CorrectionResult(word, capitalized, max(result.confidence, RULE_CONFIDENCE))
        return result

    def _word_correction(self, word: str, abbreviations_enabled: bool, learning_enabled: bool) -> CorrectionResult:
        lowered = word.lower()

        # A lone "i" is always "I".
        if lowered == "i":
            return CorrectionResult(word, "I", RULE_CONFIDENCE)

        # Layer 0: slang, apostrophe, and abbreviation rules are deterministic.
        slang = SLANG.get(lowered)
        if slang is not None:
            return CorrectionResult(word, _match_case(word, slang), RULE_CONFIDENCE)

        apostrophe = apply_apostrophe(word)
        if apostrophe is not None:
            return CorrectionResult(word, apostrophe, RULE_CONFIDENCE)

        if abbreviations_enabled:
            abbreviation = ABBREVIATIONS.get(lowered)
            if abbreviation is not None:
                return CorrectionResult(word, _match_case(word, abbreviation), RULE_CONFIDENCE)

        # Layer 1: symspellpy. A distance-0 match means the word is already correct.
        suggestions = self._symspell.lookup(lowered, Verbosity.TOP, max_edit_distance=MAX_EDIT_DISTANCE)
        if suggestions and suggestions[0].distance == 0:
            return CorrectionResult(word, word, 1.0)

        # Layer 2: if Hunspell accepts the word, keep it even when symspell has a guess.
        if self._hunspell.is_valid(lowered):
            return CorrectionResult(word, word, 1.0)

        if suggestions:
            best = suggestions[0]
            term_count = self._symspell.words.get(best.term, 0)
            confidence = symspell_confidence(best.distance, term_count)
            return CorrectionResult(word, _match_case(word, best.term), confidence)

        hunspell_suggestions = self._hunspell.suggest(lowered)
        if hunspell_suggestions:
            return CorrectionResult(
                word, _match_case(word, hunspell_suggestions[0]), HUNSPELL_SUGGESTION_CONFIDENCE
            )

        # No dictionary knows this word and no correction was found: count it. Enough
        # typings make it a personal word (the learner decides).
        if learning_enabled:
            self._learner.observe_unknown(word)
        return CorrectionResult(word, word, 1.0)
