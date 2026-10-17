"""Confidence scores for corrections, always between 0 and 1.

Each source has a base score (rules are most certain, then symspell by edit
distance, then Hunspell suggestions). Word frequency only breaks ties: a more
common candidate gets a small boost, capped so it never reaches certainty.
The C# side maps the user's correction level (Low, Medium, High, Extreme) to a
threshold and applies a correction only when its confidence meets that threshold.
"""

import math

RULE_CONFIDENCE = 0.95
SYMSPELL_BASE_BY_DISTANCE = {1: 0.9, 2: 0.6}
HUNSPELL_SUGGESTION_CONFIDENCE = 0.5

MAX_FREQUENCY_BOOST = 0.05
MAX_CONFIDENCE = 0.99


def frequency_boost(term_count: int) -> float:
    """Small boost for common words: 0 for unseen words, up to MAX_FREQUENCY_BOOST."""
    if term_count <= 0:
        return 0.0
    return min(MAX_FREQUENCY_BOOST, math.log10(term_count + 1) / 100)


def symspell_confidence(distance: int, term_count: int) -> float:
    base = SYMSPELL_BASE_BY_DISTANCE.get(distance, 0.0)
    return round(min(MAX_CONFIDENCE, base + frequency_boost(term_count)), 3)
