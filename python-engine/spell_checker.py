"""Correction pipeline for one word.

Order: lone "i", slang, apostrophes, abbreviations, symspellpy, Hunspell.
Capitalization after a sentence end is applied last. Each result carries a
confidence between 0 and 1; the C# side applies a correction only when that
confidence meets the user's level threshold.
"""

import math
import re
from dataclasses import dataclass
from importlib.resources import files

from symspellpy import SymSpell, Verbosity

from abbreviation_dict import ABBREVIATIONS
from apostrophe import apply_apostrophe, fix_apostrophe_word
from capitalization import capitalize_for_context
from confidence import (
    HUNSPELL_SUGGESTION_CONFIDENCE,
    RULE_CONFIDENCE,
    symspell_confidence,
)
from hunspell_checker import HunspellChecker
from keyboard import edit_penalty
from learner import Learner
from slang_dict import SLANG

DICTIONARY_NAME = "frequency_dictionary_en_82_765.txt"
BIGRAM_NAME = "frequency_bigramdictionary_en_243_342.txt"
MAX_EDIT_DISTANCE = 2
BIGRAM_WEIGHT = 0.02
# Words this short are changed only to a very common word; rarer matches ("ing" -> "ign") are left as typed.
SHORT_WORD_MAX_LENGTH = 3
SHORT_WORD_MIN_COUNT = 10_000_000


# lol, lool, lols, lolol, lmao, lmfao, rofl, rotfl, haha, hehe, hahaha, hehehe
_LAUGHTER = re.compile(r"^(?:l+o+l+[sz]?|(?:lo)+l+|lm+f?a+o+|ro+f+l+|rotfl|(?:ha|he)(?:ha|he)+h?)$")


@dataclass
class CorrectionResult:
    original: str
    correction: str
    confidence: float


def _best_candidate(word: str, candidates, counts: dict, bigrams: dict, previous: str | None):
    """Picks the candidate a typist most likely meant: common words first, then keys close together.

    When the previous word is known, a candidate that often follows it ("so how") gets a small bonus.
    The bonus only breaks close calls; it never adds a candidate.
    """
    # The typing cost counts more than word frequency, so a common word does not win over a closer one.
    def score(candidate) -> float:
        frequency = math.log10(counts.get(candidate.term, 0) + 1)
        total = 0.05 * frequency - edit_penalty(word, candidate.term) - 0.3 * candidate.distance
        if previous is not None:
            pair = bigrams.get(f"{previous} {candidate.term}", 0)
            total += BIGRAM_WEIGHT * math.log10(pair + 1)
        return total

    return max(candidates, key=score)


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
        self._symspell.load_bigram_dictionary(str(files("symspellpy") / BIGRAM_NAME), term_index=0, count_index=2)
        self._hunspell = HunspellChecker()

    @staticmethod
    def _alphanumeric_rule(word: str, abbreviations_enabled: bool) -> CorrectionResult | None:
        """Letter-and-number slang. Returns None when the token is not an entry in either table."""
        lowered = word.lower()
        abbreviation = ABBREVIATIONS.get(lowered)
        if abbreviation is not None:
            if abbreviations_enabled:
                return CorrectionResult(word, _match_case(word, abbreviation), RULE_CONFIDENCE)
            return CorrectionResult(word, word, 1.0)
        slang = SLANG.get(lowered)
        if slang is not None:
            return CorrectionResult(word, _match_case(word, slang), RULE_CONFIDENCE)
        return None

    def _is_known_word(self, lowered: str) -> bool:
        """True when the word is in the frequency list or in Hunspell."""
        return lowered in self._symspell.words or self._hunspell.is_valid(lowered)

    def correct(
        self,
        word: str,
        previous_word: str | None = None,
        abbreviations_enabled: bool = True,
        learning_enabled: bool = True,
        auto_capitalize: bool = True,
    ) -> CorrectionResult:
        # A word with an apostrophe (straight or curly) is already a contraction: only a lone lowercase i
        # (i'm, i'll, i've, i'd) is fixed, everything else is left as typed.
        if "'" in word or "’" in word:
            fixed = fix_apostrophe_word(word)
            if fixed is not None:
                return CorrectionResult(word, fixed, RULE_CONFIDENCE)
            return CorrectionResult(word, word, 1.0)

        # ALL CAPS (LOL, CEO, G2G) is never corrected.
        if len(word) > 1 and word.isupper():
            return CorrectionResult(word, word, 1.0)

        # Numbers, mixed alphanumerics, and punctuation pass through. The one exception is letter-and-number slang
        # (gr8, b4, l8r, 2day, g2g): only a token that is exactly an entry in the slang or abbreviation table is expanded.
        if not word.isalpha():
            ruled = self._alphanumeric_rule(word, abbreviations_enabled) if word.isalnum() else None
            return ruled or CorrectionResult(word, word, 1.0)

        # Personal words and words the user undid come first, before every other rule.
        if self._learner.should_skip(word):
            return CorrectionResult(word, word, 1.0)

        result = self._word_correction(word, previous_word, abbreviations_enabled, learning_enabled)

        # Capitalize after a sentence end. Only applies when the word is otherwise unchanged
        # or already corrected; the confidence stays with the underlying correction.
        capitalized = (
            capitalize_for_context(result.correction, previous_word) if auto_capitalize else result.correction
        )
        if capitalized != result.correction:
            return CorrectionResult(word, capitalized, max(result.confidence, RULE_CONFIDENCE))
        return result

    def _word_correction(
        self, word: str, previous_word: str | None, abbreviations_enabled: bool, learning_enabled: bool
    ) -> CorrectionResult:
        lowered = word.lower()
        # The bigram list has plain lowercase words, so the previous word is reduced to its last word.
        previous = None
        if previous_word:
            tokens = previous_word.lower().split()
            if tokens:
                previous = "".join(ch for ch in tokens[-1] if ch.isalpha()) or None

        # A lone "i" is always "I".
        if lowered == "i":
            return CorrectionResult(word, "I", RULE_CONFIDENCE)

        # Laughter and reaction tokens (lol, lmao, haha) are never expanded, with abbreviations on or off, and
        # are never sent on to the spell checker.
        if _LAUGHTER.match(lowered):
            return CorrectionResult(word, word, 1.0)

        # Layer 0: slang, apostrophe, and abbreviation rules are deterministic.
        # An abbreviation is expanded when the setting is on. When it is off the word is passed through as typed:
        # it must not fall through to the spell checker, which would "correct" it (brb -> bob).
        abbreviation = ABBREVIATIONS.get(lowered)
        if abbreviation is not None:
            if abbreviations_enabled:
                return CorrectionResult(word, _match_case(word, abbreviation), RULE_CONFIDENCE)
            return CorrectionResult(word, word, 1.0)

        slang = SLANG.get(lowered)
        if slang is not None:
            return CorrectionResult(word, _match_case(word, slang), RULE_CONFIDENCE)

        apostrophe = apply_apostrophe(word, is_valid=self._is_known_word)
        if apostrophe is not None:
            return CorrectionResult(word, apostrophe, RULE_CONFIDENCE)

        # Words of two letters or fewer are too short for similarity matching to be reliable.
        if len(lowered) <= 2:
            return CorrectionResult(word, word, 1.0)

        # Layer 1: symspellpy. A distance-0 match means the word is already correct.
        suggestions = self._symspell.lookup(lowered, Verbosity.TOP, max_edit_distance=MAX_EDIT_DISTANCE)
        if suggestions and suggestions[0].distance == 0:
            return CorrectionResult(word, word, 1.0)

        # Layer 2: if Hunspell accepts the word, keep it even when symspell has a guess.
        if self._hunspell.is_valid(lowered):
            return CorrectionResult(word, word, 1.0)

        if suggestions:
            candidates = self._symspell.lookup(lowered, Verbosity.ALL, max_edit_distance=MAX_EDIT_DISTANCE)
            best = _best_candidate(lowered, candidates, self._symspell.words, self._symspell.bigrams, previous)
            term_count = self._symspell.words.get(best.term, 0)
            if len(lowered) <= SHORT_WORD_MAX_LENGTH and term_count < SHORT_WORD_MIN_COUNT:
                return CorrectionResult(word, word, 1.0)
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

