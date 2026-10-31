"""Apostrophe rules: contractions typed without the apostrophe.

Only forms that are unambiguous are listed here. Context-dependent pairs
(its/it's, were/we're, well/we'll, id/i'd) need the context buffer and are
handled in Phase 4, not here.
"""

import re

APOSTROPHE = {
    # Negations
    "dont": "don't",
    "doesnt": "doesn't",
    "didnt": "didn't",
    "cant": "can't",
    "wont": "won't",
    "shouldnt": "shouldn't",
    "wouldnt": "wouldn't",
    "couldnt": "couldn't",
    "isnt": "isn't",
    "arent": "aren't",
    "wasnt": "wasn't",
    "werent": "weren't",
    "hasnt": "hasn't",
    "havent": "haven't",
    "hadnt": "hadn't",
    "mustnt": "mustn't",
    "neednt": "needn't",

    # Pronoun + am / is / are / have / will
    "im": "I'm",
    "iam": "I'm",
    "idont": "I don't",
    "imnot": "I'm not",
    "ive": "I've",
    "youre": "you're",
    "theyre": "they're",
    "hes": "he's",
    "shes": "she's",
    "thats": "that's",
    "whats": "what's",
    "whos": "who's",
    "theres": "there's",
    "heres": "here's",
    "weve": "we've",
    "youve": "you've",
    "theyve": "they've",
    "youll": "you'll",
    "theyll": "they'll",

    # Perfect-modal forms
    "shouldve": "should've",
    "wouldve": "would've",
    "couldve": "could've",
    "mightve": "might've",
    "mustve": "must've",

    # No pattern can produce these
    "aint": "ain't",
    "arnt": "aren't",
    "shant": "shan't",
    "oclock": "o'clock",
    "yall": "y'all",
}


_LONE_I_CONTRACTION = re.compile(r"^i(['’])(m|ll|ve|d)$")


def fix_apostrophe_word(word: str) -> str | None:
    """A word that already has an apostrophe (straight or curly) is a finished contraction and stays as typed.
    The one fix is the lone lowercase I: i'm -> I'm, i'll -> I'll, i've -> I've, i'd -> I'd. The typed apostrophe
    is kept. Returns None when nothing needs fixing."""
    if _LONE_I_CONTRACTION.match(word):
        return "I" + word[1:]
    return None


# ---- Patterns --------------------------------------------------------------------------------
# A contraction typed without its apostrophe is a stem plus an ending, so the rules are patterns, not a
# list of words. The explicit table above only holds what no pattern can produce (aint, oclock, yall) and
# the forms that were already handled.

# Auxiliary verbs that take n't. The stem of a typed word may be spelled wrongly by one letter (dosent, woulnt).
_NT_AUXILIARIES = ("do", "does", "did", "is", "are", "was", "were", "has", "have", "had",
                   "would", "could", "should", "might", "must", "need", "ought")
_NT_ENDINGS = ("ent", "int", "nt")   # doesnt, dosent, didint

_MODALS_FOR_VE = ("should", "would", "could", "might", "must")

# Real words that these combinations also spell. They are left alone whatever the context
# (its/it's and were/we're are decided separately, by the next word).
_AMBIGUOUS = {"well", "hell", "shell", "were", "its", "ill", "id", "wed", "hed", "shed"}

_PRONOUN_ENDINGS = {
    "i": ("m", "ll", "ve", "d"),
    "you": ("ll", "ve", "re", "d"),
    "we": ("ll", "ve", "re", "d"),
    "they": ("ll", "ve", "re", "d"),
    "he": ("s", "ll", "d"),
    "she": ("s", "ll", "d"),
    "it": ("s", "ll", "d"),
}
_WH_AND_THAT = ("that", "there", "here", "who", "what", "where", "when", "why", "how",
                "everyone", "everybody", "someone", "somebody", "anyone", "anybody", "nobody")


def _build_forms() -> dict[str, str]:
    forms: dict[str, str] = {}
    for stem, endings in _PRONOUN_ENDINGS.items():
        for ending in endings:
            typed = stem + ending
            if typed not in _AMBIGUOUS:
                forms[typed] = ("I" if stem == "i" else stem) + "'" + ending
    for stem in _WH_AND_THAT:
        for ending in ("s", "ll", "d", "ve", "re"):
            typed = stem + ending
            if typed not in _AMBIGUOUS:
                forms[typed] = stem + "'" + ending
    for modal in _MODALS_FOR_VE:
        forms[modal + "ve"] = modal + "'ve"
    return forms


_FORMS = _build_forms()


def _edit_distance_is_one(a: str, b: str) -> bool:
    """True when a and b differ by exactly one inserted, deleted or replaced letter."""
    if a == b or abs(len(a) - len(b)) > 1:
        return False
    if len(a) == len(b):
        return sum(1 for x, y in zip(a, b) if x != y) == 1
    short, long_ = (a, b) if len(a) < len(b) else (b, a)
    return any(long_[:i] + long_[i + 1:] == short for i in range(len(long_)))


def _negation(typed: str, is_valid) -> str | None:
    """aux + nt: hadnt -> hadn't. A stem with one wrong letter (dosent, woulnt, havnt) is accepted only when the
    typed word is not itself a valid word, so real words that end in nt are never touched."""
    for ending in _NT_ENDINGS:
        if not typed.endswith(ending):
            continue
        stem = typed[: -len(ending)]
        if stem in _NT_AUXILIARIES:
            return stem + "n't"
        if len(stem) >= 3 and is_valid is not None and not is_valid(typed):
            # When two auxiliaries are one edit away (dos: do, does), the longer one wins: typists drop letters
            # more often than they add them.
            close = [aux for aux in _NT_AUXILIARIES if stem[0] == aux[0] and _edit_distance_is_one(stem, aux)]
            if close:
                return max(close, key=len) + "n't"
    return None


def apply_apostrophe(word: str, is_valid=None) -> str | None:
    """Return the apostrophe form of a word, or None if no rule applies.

    Case is preserved: 'Dont' becomes "Don't", and 'im' becomes "I'm".
    is_valid(lowercase_word) tells whether the typed word is a real word; it guards the one-letter-off stems.
    """
    typed = word.lower()
    if typed in _AMBIGUOUS:
        return None
    expansion = APOSTROPHE.get(typed) or _FORMS.get(typed) or _negation(typed, is_valid)
    if expansion is None:
        return None
    if word[0].isupper() and not expansion[0].isupper():
        return expansion[0].upper() + expansion[1:]
    return expansion


# Context pairs: "its" vs "it's" and "were" vs "we're" cannot be decided from the
# word alone. The next word decides, so the engine revises the earlier word when
# the next word arrives.
IT_IS_FOLLOWERS = {
    "a", "an", "the", "not", "been", "going", "time", "my", "so", "all", "about",
    "really", "very", "too", "just", "okay", "ok", "fine", "true", "important",
    "possible", "likely", "here", "there", "raining", "snowing", "hot", "cold",
    "late", "early", "getting", "done", "always", "never", "also", "still", "only",
    "now", "what", "that", "this", "how", "why", "when", "where", "better", "worse",
}

# Only followers that rarely follow "they were", so the rule doesn't misfire.
WE_ARE_FOLLOWERS = {
    "going", "doing", "coming", "ready", "gonna", "happy", "sure", "trying",
    "having", "leaving", "planning", "glad", "sorry", "both",
}

# Words after which "were" starts a clause ("and were going" = "and we're going").
# After a subject such as "they", "were" is past tense and must stay.
CLAUSE_LEADERS = {"and", "but", "so", "because", "if", "when", "then", "oh", "yes", "no", "hey", "or"}


# Subjects that make a following "were" past tense ("they were going"), so it must stay.
SUBJECT_PRONOUNS = {"they", "we", "you", "i", "he", "she", "it", "who", "people", "these", "those", "all", "both"}

# How far back the clause check looks: the words just before "were" (the buffer holds five words).
CLAUSE_CONTEXT_WORDS = 3


def starts_clause(before: list[str]) -> bool:
    """True when "were" that follows these words starts a clause (no subject before it).

    before is the words before the earlier word, oldest first (up to CLAUSE_CONTEXT_WORDS of them).
    No words at all (the start of the text) starts a clause. A subject pronoun in the window means
    past tense. A clause leader right before it means a clause starts.
    """
    recent = [w.lower() for w in before[-CLAUSE_CONTEXT_WORDS:]]
    if not recent:
        return True
    if recent[-1] in SUBJECT_PRONOUNS:
        return False
    # Anything else (a noun subject such as "the dogs") is treated as past tense: "were" stays.
    return recent[-1] in CLAUSE_LEADERS


def resolve_pending(previous_word: str, current_word: str, before: list[str] | None = None) -> str | None:
    """Return the corrected previous word when current_word settles it, else None.

    previous_word is what is already on screen; current_word is the word just typed;
    before is the words before previous_word, oldest first, from the context buffer.
    """
    follower = current_word.lower()
    previous = previous_word.lower()
    if previous == "its" and follower in IT_IS_FOLLOWERS:
        return _match_case(previous_word, "it's")
    if previous == "were" and follower in WE_ARE_FOLLOWERS:
        if starts_clause(before or []):
            return _match_case(previous_word, "we're")
    return None


def _match_case(original: str, correction: str) -> str:
    if original[0].isupper():
        return correction[0].upper() + correction[1:]
    return correction
