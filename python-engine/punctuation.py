"""Punctuation and spacing fixes applied to a line of text.

Rules, in order:
1. Three dots become a single ellipsis character.
2. Repeated . ! ? , collapse to one (done.. -> done.).
3. A comma with a space before it and a letter after it moves next to the word and gets a space after
   (done ,really -> done, really).
4. Other spaces before punctuation are removed (hello . -> hello.).
5. Runs of spaces collapse to one.
"""

import re

_ELLIPSIS = re.compile(r"\.\.\.")
_REPEATED_PUNCTUATION = re.compile(r"([.!?,])\1+")
# An ellipsis typed straight into the next word ("wait...what") gets a space after it.
_ELLIPSIS_THEN_LETTER = re.compile(r"…(?=[^\W\d_])")
# A comma that had a space before it and is followed by a letter ("done ,really") gets its space after it.
# Commas inside numbers ("1,000") have no space before them, so they never match.
_SPACE_BEFORE_COMMA_THEN_LETTER = re.compile(r"[ \t]+,(?=[^\W\d_])")
_SPACE_BEFORE_PUNCTUATION = re.compile(r"[ \t]+([.!?,;:])")
_MULTIPLE_SPACES = re.compile(r"[ \t]{2,}")


def fix_punctuation(text: str) -> str:
    text = _ELLIPSIS.sub("…", text)
    text = _ELLIPSIS_THEN_LETTER.sub("… ", text)
    text = _REPEATED_PUNCTUATION.sub(r"\1", text)
    text = _SPACE_BEFORE_COMMA_THEN_LETTER.sub(", ", text)
    text = _SPACE_BEFORE_PUNCTUATION.sub(r"\1", text)
    text = _MULTIPLE_SPACES.sub(" ", text)
    return text
