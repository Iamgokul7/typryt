"""Apostrophe rules: contractions typed without the apostrophe.

Only forms that are unambiguous are listed here. Context-dependent pairs
(its/it's, were/we're, well/we'll, id/i'd) need the context buffer and are
handled in Phase 4, not here.
"""

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
}


def apply_apostrophe(word: str) -> str | None:
    """Return the apostrophe form of a word, or None if no rule applies.

    Case is preserved: 'Dont' becomes "Don't", and 'im' becomes "I'm".
    """
    expansion = APOSTROPHE.get(word.lower())
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
