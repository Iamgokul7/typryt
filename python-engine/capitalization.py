"""Capitalization rules that depend on the previous word.

- A lone "i" always becomes "I" (no context needed).
- The first word after a sentence end (. ! ?) is capitalized.

The first word of a field is left alone, because the engine can't tell where a
text box starts.
"""

SENTENCE_END = (".", "!", "?")


def is_sentence_start(previous_word: str | None) -> bool:
    return previous_word is not None and previous_word.endswith(SENTENCE_END)


def capitalize_for_context(word: str, previous_word: str | None) -> str:
    """Capitalize word if it follows a sentence end. Words already capitalized stay as they are."""
    if not is_sentence_start(previous_word) or not word or not word[0].islower():
        return word
    return word[0].upper() + word[1:]
