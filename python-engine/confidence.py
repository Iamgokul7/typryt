"""Confidence scores for corrections, always between 0 and 1.

The C# side maps the user's correction level (Low, Medium, High, Extreme) to a threshold and applies a
correction only when its confidence meets it (thresholds in PermissionManager.cs: 0.95, 0.90, 0.60, 0.50).
The scores are graded by how sure the engine is:

  exact rules and the curated misspelling map   RULE_CONFIDENCE         0.97  (every level applies them)
  stretched words (okk, thankss)                 STRETCHED_CONFIDENCE    0.93
  distance-1 fixes with strong evidence          0.91 to 0.94, by how common the target word is
  anything weaker is not applied at all: the word is left unchanged (confidence 1.0)
"""

import math

RULE_CONFIDENCE = 0.97
STRETCHED_CONFIDENCE = 0.93

STRONG_FIX_BASE = 0.91
STRONG_FIX_MAX_BOOST = 0.03
# A target word at or below this count earns no boost; at 10 billion it earns the full boost.
BOOST_FLOOR_LOG10 = 6.0
BOOST_SPAN_LOG10 = 4.0

MAX_CONFIDENCE = 0.99


def graded_fix_confidence(term_count: int) -> float:
    """0.91 for a target word seen a million times, rising to 0.94 for the very common ones."""
    if term_count <= 0:
        return STRONG_FIX_BASE
    boost = STRONG_FIX_MAX_BOOST * (math.log10(term_count) - BOOST_FLOOR_LOG10) / BOOST_SPAN_LOG10
    return round(min(MAX_CONFIDENCE, STRONG_FIX_BASE + max(0.0, min(STRONG_FIX_MAX_BOOST, boost))), 3)
