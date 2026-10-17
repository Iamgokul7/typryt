"""Comma trigger: words that almost always need a comma after them.

Phase 3 uses the previous word passed in the request. The full 5-word context
buffer comes in Phase 4.
"""

import re

TRIGGER_WORDS = (
    "however",
    "therefore",
    "moreover",
    "furthermore",
    "meanwhile",
    "nevertheless",
    "otherwise",
    "instead",
    "consequently",
    "additionally",
    "finally",
    "basically",
    "honestly",
    "clearly",
    "obviously",
    "unfortunately",
)

# "instead of", "otherwise than" and similar phrases take no comma.
NO_COMMA_BEFORE = ("of", "than")

_TRIGGER_PATTERN = re.compile(
    r"\b(" + "|".join(TRIGGER_WORDS) + r")\b"
    r"(?![,\w])"
    r"(?!\s+(?:" + "|".join(NO_COMMA_BEFORE) + r")\b)"
    r"(?=\s+\w)",
    re.IGNORECASE,
)


def needs_comma_after(previous_word: str, current_word: str) -> bool:
    """True when previous_word is a trigger word and current_word follows it without a comma."""
    word = previous_word.strip()
    if not word or word.endswith(","):
        return False
    if word.lower() not in TRIGGER_WORDS:
        return False
    return current_word.lower() not in NO_COMMA_BEFORE


def apply_comma_rule(text: str) -> str:
    """Insert a comma after trigger words that are followed by another word."""
    return _TRIGGER_PATTERN.sub(r"\1,", text)
