"""Correction pipeline for one word.

Order: lone "i", laughter, abbreviations, slang, apostrophes, days and months, the curated misspelling map,
stretched words, then, for a word nobody knows, a conservative check: the word is changed only when the
evidence is strong (distance 1, a frequent target, a typing-slip pattern or Hunspell's agreement). Anything
less is left unchanged. Capitalization after a sentence end is applied last. Each result carries a confidence
between 0 and 1 (see confidence.py); the C# side applies a correction only when that confidence meets the
user's level threshold.
"""

import itertools
import math
import re
from dataclasses import dataclass
from importlib.resources import files

from symspellpy import SymSpell, Verbosity

from abbreviation_dict import ABBREVIATIONS
from apostrophe import apply_apostrophe, fix_apostrophe_word
from capitalization import capitalize_for_context
from confidence import RULE_CONFIDENCE, STRETCHED_CONFIDENCE, graded_fix_confidence
from hunspell_checker import HunspellChecker
from keyboard import edit_penalty
from learner import Learner
from lexicons import Lexicons
from slang_dict import INFORMAL_WORDS, SLANG

DICTIONARY_NAME = "frequency_dictionary_en_82_765.txt"
BIGRAM_NAME = "frequency_bigramdictionary_en_243_342.txt"
MAX_EDIT_DISTANCE = 2
BIGRAM_WEIGHT = 0.02

# A word nobody knows is changed only when all of this holds (see _strong_fix):
MIN_FIX_WORD_LENGTH = 4            # shorter words are changed only by the rule tables and the misspelling map
MIN_TYPO_LIKE_LENGTH = 5           # a typing-slip pattern is trusted only on words at least this long
MIN_TARGET_COUNT = 1_000_000       # the replacement must be a frequent word
TYPO_LIKE_PENALTY = 0.3            # one swapped pair, one neighbouring key, or one doubled letter
AMBIGUITY_MARGIN = 0.03            # two candidates this close in score: leave the word alone

# Weak guesses (see _weak_fix) stay below the Medium threshold of 0.90 so that Medium and Low never apply them.
WEAK_MIN_TARGET_COUNT = 100_000
WEAK_BASE_CONFIDENCE = 0.50
WEAK_MAX_CONFIDENCE = 0.85


# lol, lool, lols, lolol, lmao, lmfao, rofl, rotfl, haha, hehe, hahaha, hehehe
_LAUGHTER = re.compile(r"^(?:l+o+l+[sz]?|(?:lo)+l+|lm+f?a+o+|ro+f+l+|rotfl|(?:ha|he)(?:ha|he)+h?)$")


# A run of one repeated letter.
_RUN = re.compile(r"(.)\1+")

# A word this frequent in the frequency list is a real word even when Hunspell does not list it.
COMMON_WORD_COUNT = 1_000_000


_DAYS_AND_MONTHS = {
    name.lower(): name
    for name in (
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December",
    )
}
# "may" and "march" are also everyday words ("I may go", "we march"), so they are capitalized only after a
# word that is followed by a month.
_AMBIGUOUS_MONTHS = {"may", "march"}
_BEFORE_A_MONTH = {
    "in", "of", "since", "until", "till", "by", "from", "this", "next", "last", "early", "late", "mid",
    "during", "every", "before", "after", "through", "throughout",
}


def _at_sentence_start(previous_word: str | None) -> bool:
    """No word before it (the start of the text), or the word before it ended a sentence."""
    return previous_word is None or previous_word.endswith((".", "!", "?"))


@dataclass
class CorrectionResult:
    original: str
    correction: str
    confidence: float
    # A weaker guess for a word the engine left unchanged. It is never in `correction`: the app applies it only
    # when the user's level threshold is at or below weak_confidence (High, Extreme). Medium and Low ignore it.
    weak_correction: str | None = None
    weak_confidence: float = 0.0


def _score_candidates(word: str, candidates, counts: dict, bigrams: dict, previous: str | None):
    """Scores each candidate by how likely a typist meant it: common words first, then keys close together.

    When the previous word is known, a candidate that often follows it ("so how") gets a small bonus.
    The bonus only breaks close calls; it never adds a candidate. Returns (score, candidate) pairs, best first.
    """
    # The typing cost counts more than word frequency, so a common word does not win over a closer one.
    def score(candidate) -> float:
        frequency = math.log10(counts.get(candidate.term, 0) + 1)
        total = 0.05 * frequency - edit_penalty(word, candidate.term) - 0.3 * candidate.distance
        if previous is not None:
            pair = bigrams.get(f"{previous} {candidate.term}", 0)
            total += BIGRAM_WEIGHT * math.log10(pair + 1)
        return total

    return sorted(((score(c), c) for c in candidates), key=lambda pair: pair[0], reverse=True)


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
        # The lexicons and the misspelling map are read once, here, into sets and a dict.
        self._lexicons = Lexicons()

    def warm_up(self) -> None:
        """Loads what is read lazily on first use, so the first real words are answered as fast as the later ones."""
        for word in ("recieve", "definately", "seperate"):
            self._symspell.lookup(word, Verbosity.ALL, max_edit_distance=MAX_EDIT_DISTANCE)
            self._hunspell.suggest_within_budget(word, budget=5.0)

    @staticmethod
    def _day_or_month(word: str, lowered: str, previous: str | None) -> CorrectionResult | None:
        proper = _DAYS_AND_MONTHS.get(lowered)
        if proper is None:
            return None
        if lowered in _AMBIGUOUS_MONTHS and previous not in _BEFORE_A_MONTH:
            return None                      # "I may go", "we march": an ordinary word, left alone
        if word == proper:
            return CorrectionResult(word, word, 1.0)
        return CorrectionResult(word, proper, RULE_CONFIDENCE)

    def _stretched(self, word: str, lowered: str, previous_word: str | None, abbreviations_enabled: bool) -> CorrectionResult | None:
        """A word with repeated letters that is not a word as typed (okk, sooo, thankss, plss, heyyy).

        Only runs of three or more of one letter, or a doubled last letter, count as stretching; the other doubled
        letters of real words (coffee, committee) are never touched, and a word that is valid as typed is left alone.
        Each collapsed form is looked up again through the rules and the dictionaries and is used only when it is a
        confident match; otherwise the word is left unchanged.
        """
        runs = [m for m in _RUN.finditer(lowered)]
        stretch_runs = [m for m in runs if len(m.group()) >= 3 or (len(m.group()) == 2 and m.end() == len(lowered))]
        if not stretch_runs or len(stretch_runs) > 3:
            return None
        if self._is_valid_word(lowered):
            return None
        # Capitalized words are names unless they start a sentence ("Heyyy there").
        if word[0].isupper() and not _at_sentence_start(previous_word):
            return None

        # For each stretched run: keep two letters first (cool, good, really), then one (so, love, no).
        options = [(2, 1) if len(m.group()) >= 3 else (1,) for m in stretch_runs]
        for lengths in itertools.product(*options):
            pieces, last = [], 0
            for m, length in zip(stretch_runs, lengths):
                pieces.append(lowered[last:m.start()] + m.group()[0] * length)
                last = m.end()
            pieces.append(lowered[last:])
            candidate = "".join(pieces)
            if candidate == lowered or len(candidate) < 2:
                continue
            known = self._known_form(candidate, abbreviations_enabled)
            if known is not None:
                return CorrectionResult(word, _match_case(word, known), STRETCHED_CONFIDENCE)
        return None

    def _known_form(self, candidate: str, abbreviations_enabled: bool) -> str | None:
        """What a collapsed word means, when it is a confident match: a slang or abbreviation entry, a contraction, or a real word."""
        abbreviation = ABBREVIATIONS.get(candidate)
        if abbreviation is not None:
            return abbreviation if abbreviations_enabled else candidate
        slang = SLANG.get(candidate)
        if slang is not None:
            return slang
        apostrophe = apply_apostrophe(candidate, is_valid=self._is_known_word)
        if apostrophe is not None:
            return apostrophe
        # Hunspell, or a very common word in the frequency list (Hunspell does not list lowercase "ok").
        if self._is_valid_word(candidate) or self._symspell.words.get(candidate, 0) >= COMMON_WORD_COUNT:
            return candidate
        return None

    def _is_valid_word(self, lowered: str) -> bool:
        """A real word as typed: Hunspell knows it, or it is a known informal word."""
        return lowered in INFORMAL_WORDS or self._hunspell.is_valid(lowered)

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

        # Days of the week and months are always capitalized (monday -> Monday).
        day_or_month = self._day_or_month(word, lowered, previous)
        if day_or_month is not None:
            return day_or_month

        # The curated misspelling map: exact match, checked before any guessing.
        fixed = self._lexicons.misspellings.get(lowered)
        if fixed is not None:
            proper = _DAYS_AND_MONTHS.get(fixed.lower())      # wensday -> Wednesday, never wednesday
            return CorrectionResult(word, _match_case(word, proper or fixed), RULE_CONFIDENCE)

        # A stretched word (okk, sooo, thankss, plss): collapse the repeated letters and look again.
        stretched = self._stretched(word, lowered, previous_word, abbreviations_enabled)
        if stretched is not None:
            return stretched

        # Words of two letters or fewer are too short for similarity matching to be reliable.
        if len(lowered) <= 2:
            return CorrectionResult(word, word, 1.0)

        # A capitalized word that is not at the start of a sentence is a name, a brand or a loanword: never corrected.
        # Learning still counts it, so a name typed again and again becomes a personal word.
        capitalized = word[0].isupper()
        sentence_start = _at_sentence_start(previous_word)
        if capitalized and not sentence_start:
            if learning_enabled and not self._is_known_anywhere(lowered):
                self._learner.observe_unknown(word)
            return CorrectionResult(word, word, 1.0)

        # Words that are known: the lexicons, the informal words, the frequency list (distance 0), Hunspell.
        if lowered in self._lexicons.words or lowered in INFORMAL_WORDS:
            return CorrectionResult(word, word, 1.0)
        suggestions = self._symspell.lookup(lowered, Verbosity.TOP, max_edit_distance=MAX_EDIT_DISTANCE)
        if suggestions and suggestions[0].distance == 0:
            return CorrectionResult(word, word, 1.0)
        if self._hunspell.is_valid(lowered):
            return CorrectionResult(word, word, 1.0)

        # An unknown word is not the same as a typo. It is changed only on strong evidence.
        fixed = self._strong_fix(word, lowered, previous, strict=capitalized)
        if fixed is not None:
            return fixed

        # No dictionary knows this word and the engine did not change it with confidence: count it, whether or not a
        # distant dictionary word came close (Gokulify is not "Nullify"). Enough typings make it a personal word,
        # which is then left alone at every level. A word the engine corrected is never counted.
        if learning_enabled and self._learner.observe_unknown(word):
            return CorrectionResult(word, word, 1.0)

        # Weaker guesses, below the Medium threshold: only the High and Extreme levels apply them.
        weak = self._weak_fix(word, lowered, previous, strict=capitalized)
        if weak is not None:
            return CorrectionResult(word, word, 1.0, weak_correction=weak.correction, weak_confidence=weak.confidence)
        return CorrectionResult(word, word, 1.0)

    def _is_known_anywhere(self, lowered: str) -> bool:
        """A word some dictionary, lexicon or informal list knows."""
        return (
            lowered in self._lexicons.words
            or lowered in INFORMAL_WORDS
            or lowered in self._symspell.words
            or self._hunspell.is_valid(lowered)
        )

    def _weak_fix(self, word: str, lowered: str, previous: str | None, strict: bool) -> CorrectionResult | None:
        """A guess that did not pass the strong gates. Its confidence is graded below the Medium threshold
        (0.90): 0.5 to 0.85, by how many of the signs of a real typing slip it shows. Medium never applies it;
        High (0.60) and Extreme (0.50) apply the stronger and the weaker guesses."""
        if len(lowered) < MIN_FIX_WORD_LENGTH:
            return None
        found = self._symspell.lookup(lowered, Verbosity.ALL, max_edit_distance=MAX_EDIT_DISTANCE)
        if not found:
            return None
        distance_one = [c for c in found if c.distance == 1]
        pool = distance_one or found
        scored = _score_candidates(lowered, pool, self._symspell.words, self._symspell.bigrams, previous)
        best_score, best = scored[0]
        count = self._symspell.words.get(best.term, 0)
        if count < WEAK_MIN_TARGET_COUNT:
            return None

        margin = len(scored) == 1 or best_score - scored[1][0] >= AMBIGUITY_MARGIN
        frequent = count >= MIN_TARGET_COUNT
        typo_like = len(lowered) >= MIN_TYPO_LIKE_LENGTH and edit_penalty(lowered, best.term) <= TYPO_LIKE_PENALTY
        known = self._hunspell.cached_suggestions(lowered)
        agrees = known is not None and known[:1] == [best.term]    # only an answer Hunspell already gave: asking would hold up the next corrections

        if best.distance == 1 and (typo_like or agrees):
            # A slip of the kind a typist makes (swapped pair, neighbouring key, doubled letter) or Hunspell's own pick: High applies it.
            confidence = 0.62 + (0.08 if margin else 0) + (0.05 if frequent else 0) + (0.08 if typo_like and agrees else 0)
        elif best.distance == 1:
            confidence = 0.50 + (0.04 if margin else 0) + (0.03 if frequent else 0)         # Extreme only
        else:
            confidence = 0.50 + (0.03 if margin else 0) + (0.02 if frequent else 0)         # Extreme only
        if strict:
            confidence -= 0.12      # a capitalized word at a sentence start may be a name
        confidence = round(min(WEAK_MAX_CONFIDENCE, confidence), 3)
        if confidence < WEAK_BASE_CONFIDENCE:
            return None
        return CorrectionResult(word, _match_case(word, best.term), confidence)

    def _strong_fix(self, word: str, lowered: str, previous: str | None, strict: bool) -> CorrectionResult | None:
        """A correction for an unknown word, or None (leave it as typed). Every one of these must hold:
        a distance-1 candidate, clearly ahead of the others, that is a frequent word, and either a typing slip
        (swapped pair, neighbouring key, doubled letter) or Hunspell's own first suggestion. A capitalized word
        at the start of a sentence needs both, since it may be a name."""
        if len(lowered) < MIN_FIX_WORD_LENGTH:
            return None
        candidates = [c for c in self._symspell.lookup(lowered, Verbosity.ALL, max_edit_distance=1) if c.distance == 1]
        if not candidates:
            return None
        scored = _score_candidates(lowered, candidates, self._symspell.words, self._symspell.bigrams, previous)
        best_score, best = scored[0]
        if len(scored) > 1 and best_score - scored[1][0] < AMBIGUITY_MARGIN:
            return None
        count = self._symspell.words.get(best.term, 0)
        if count < MIN_TARGET_COUNT:
            return None

        typo_like = len(lowered) >= MIN_TYPO_LIKE_LENGTH and edit_penalty(lowered, best.term) <= TYPO_LIKE_PENALTY
        if strict:
            if not (typo_like and self._hunspell_agrees(lowered, best.term)):
                return None
        elif not typo_like and not self._hunspell_agrees(lowered, best.term):
            return None
        return CorrectionResult(word, _match_case(word, best.term), graded_fix_confidence(count))

    def _hunspell_agrees(self, lowered: str, term: str) -> bool:
        """True when Hunspell's first suggestion for the word is the same word. Asked within a time budget: if
        Hunspell is not ready in time, that counts as no agreement and the word is left unchanged."""
        suggestions = self._hunspell.suggest_within_budget(lowered)
        return suggestions is not None and suggestions[:1] == [term]

