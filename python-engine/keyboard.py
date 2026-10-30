"""Keyboard layout costs for ranking spelling suggestions.

A typo is more likely when the wrong key is a neighbour on the keyboard, when two neighbouring
letters are swapped, or when a doubled letter is missed or added. Suggestions that need fewer
unlikely typing mistakes are preferred over suggestions that are merely common words.
"""

_ROWS = ["qwertyuiop", "asdfghjkl", "zxcvbnm"]
_OFFSET = [0.0, 0.5, 1.0]  # each row is shifted right on a real keyboard

_NEIGHBOURS: dict[str, set[str]] = {}
for _row_index, _row in enumerate(_ROWS):
    for _col, _key in enumerate(_row):
        _near = set()
        x = _col + _OFFSET[_row_index]
        for _other_index, _other_row in enumerate(_ROWS):
            for _other_col, _other in enumerate(_other_row):
                other_x = _other_col + _OFFSET[_other_index]
                if abs(other_x - x) <= 1.0 and abs(_other_index - _row_index) <= 1 and _other != _key:
                    _near.add(_other)
        _NEIGHBOURS[_key] = _near

NEAR_KEY_COST = 0.3
FAR_KEY_COST = 0.6
SWAP_COST = 0.2
DOUBLED_LETTER_COST = 0.2
OTHER_LENGTH_COST = 0.6


def _substitution_cost(a: str, b: str) -> float:
    if a == b:
        return 0.0
    return NEAR_KEY_COST if b in _NEIGHBOURS.get(a, set()) else FAR_KEY_COST


def edit_penalty(original: str, candidate: str) -> float:
    """How unlikely the candidate is as the intended word for the typed original. Zero means identical."""
    if original == candidate:
        return 0.0

    # Two letters swapped, such as "wnat" for "want".
    if len(original) == len(candidate) and sorted(original) == sorted(candidate):
        return SWAP_COST

    # Same length: each typed letter may be a neighbouring key.
    if len(original) == len(candidate):
        return sum(_substitution_cost(a, b) for a, b in zip(original, candidate))

    # One letter added: cheap when it doubles the letter next to it, such as "helo" for "hello".
    if len(candidate) == len(original) + 1:
        for i in range(len(candidate)):
            if candidate[:i] + candidate[i + 1:] == original:
                inserted = candidate[i]
                doubled = (i > 0 and candidate[i - 1] == inserted) or (i + 1 < len(candidate) and candidate[i + 1] == inserted)
                return DOUBLED_LETTER_COST if doubled else OTHER_LENGTH_COST
        return OTHER_LENGTH_COST

    # One letter removed: cheap when the removed letter was a doubled one, such as "occassion" for "occasion".
    if len(candidate) == len(original) - 1:
        for i in range(len(original)):
            if original[:i] + original[i + 1:] == candidate:
                removed = original[i]
                doubled = (i > 0 and original[i - 1] == removed) or (i + 1 < len(original) and original[i + 1] == removed)
                return DOUBLED_LETTER_COST if doubled else OTHER_LENGTH_COST
        return OTHER_LENGTH_COST

    return OTHER_LENGTH_COST * abs(len(original) - len(candidate))

