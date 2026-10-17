"""Punctuation and spacing fixes applied to a line of text.

Rules, in order:
1. Three dots become a single ellipsis character.
2. Repeated . ! ? , collapse to one (done.. -> done.).
3. Spaces before punctuation are removed (hello . -> hello.).
4. Runs of spaces collapse to one.
"""

import re

_ELLIPSIS = re.compile(r"\.\.\.")
_REPEATED_PUNCTUATION = re.compile(r"([.!?,])\1+")
_SPACE_BEFORE_PUNCTUATION = re.compile(r"[ \t]+([.!?,;:])")
_MULTIPLE_SPACES = re.compile(r"[ \t]{2,}")


def fix_punctuation(text: str) -> str:
    text = _ELLIPSIS.sub("…", text)
    text = _REPEATED_PUNCTUATION.sub(r"\1", text)
    text = _SPACE_BEFORE_PUNCTUATION.sub(r"\1", text)
    text = _MULTIPLE_SPACES.sub(" ", text)
    return text
