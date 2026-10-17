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
