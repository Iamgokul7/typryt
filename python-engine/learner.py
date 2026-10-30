"""Learning system: the personal dictionary and the never-correct list.

Two lists decide which words the engine leaves alone:
- personal_words: words the user added, or words typed often that no dictionary knows.
- never_correct: words the user undid. Each one is blocked until a stored end time, or forever
  when no end time is set. An expired block is dropped and the word is corrected again.

A word typed PROMOTE_AFTER times that no dictionary knows and that the engine could not
correct becomes a personal word. The counts are saved so learning works across restarts.

Everything is stored in lowercase in personal_dict.json. Saves are atomic: the file is
written next to the target and then swapped in, so a crash never leaves a half-written file.
"""

import json
import os
import re
import threading
import time
from pathlib import Path

PROMOTE_AFTER = 3
MAX_TRACKED_UNKNOWN_WORDS = 5000
MAX_WORD_LENGTH = 40
DEFAULT_BLOCK_HOURS = 24

# Letters, with apostrophes and hyphens allowed inside, starting with a letter.
_VALID_WORD = re.compile(r"^[A-Za-z][A-Za-z'\-]*$")


def normalize(word: str) -> str:
    return word.strip().lower()


def is_valid_word(word: str) -> bool:
    cleaned = word.strip()
    return 0 < len(cleaned) <= MAX_WORD_LENGTH and _VALID_WORD.match(cleaned) is not None


class Learner:
    def __init__(self, path: Path, clock=time.time) -> None:
        self._path = Path(path)
        self._clock = clock
        self._lock = threading.RLock()
        self._personal_words: set[str] = set()
        # word -> end of the block as Unix seconds, or None when the block never ends.
        self._never_correct: dict[str, float | None] = {}
        self._unknown_counts: dict[str, int] = {}
        self._load()

    # ----- lookups -----

    def is_personal(self, word: str) -> bool:
        with self._lock:
            return normalize(word) in self._personal_words

    def is_never_correct(self, word: str) -> bool:
        with self._lock:
            return self._active_block(normalize(word))

    def should_skip(self, word: str) -> bool:
        """True when the word must be left alone: it is a personal word or is blocked."""
        key = normalize(word)
        with self._lock:
            return key in self._personal_words or self._active_block(key)

    # ----- learning -----

    def record_undo(self, word: str, hours: float | None = DEFAULT_BLOCK_HOURS) -> None:
        """The user undid a correction of this word: block it for the given hours (None means forever)."""
        key = normalize(word)
        if not is_valid_word(key):
            return
        until = None if hours is None else self._clock() + hours * 3600
        with self._lock:
            self._never_correct[key] = until
            self._unknown_counts.pop(key, None)
            self._save()

    def observe_unknown(self, word: str) -> bool:
        """Counts one typing of an unknown word. Returns True when it just became a personal word."""
        key = normalize(word)
        if not is_valid_word(key) or "'" in key or "-" in key or len(key) < 2:
            return False
        with self._lock:
            if key in self._personal_words or self._active_block(key):
                return False
            count = self._unknown_counts.get(key, 0) + 1
            if count >= PROMOTE_AFTER:
                self._unknown_counts.pop(key, None)
                self._personal_words.add(key)
                self._save()
                return True
            self._unknown_counts[key] = count
            # Keep the saved file small: forget the oldest counts first.
            while len(self._unknown_counts) > MAX_TRACKED_UNKNOWN_WORDS:
                self._unknown_counts.pop(next(iter(self._unknown_counts)))
            self._save()
            return False

    # ----- managing the lists -----

    def add_word(self, word: str) -> bool:
        """Adds a personal word. Returns False if the word is not valid."""
        key = normalize(word)
        if not is_valid_word(key):
            return False
        with self._lock:
            self._personal_words.add(key)
            self._unknown_counts.pop(key, None)
            self._save()
        return True

    def remove_word(self, word: str) -> bool:
        key = normalize(word)
        with self._lock:
            if key not in self._personal_words:
                return False
            self._personal_words.discard(key)
            self._save()
        return True

    def remove_never_correct(self, word: str) -> bool:
        key = normalize(word)
        with self._lock:
            if key not in self._never_correct:
                return False
            del self._never_correct[key]
            self._save()
        return True

    def snapshot(self) -> dict:
        with self._lock:
            self._drop_expired()
            return {
                "personal_words": sorted(self._personal_words),
                "never_correct": sorted(self._never_correct),
                "never_correct_until": dict(sorted(self._never_correct.items())),
            }

    # ----- internals -----

    def _active_block(self, key: str) -> bool:
        """True when the word is blocked now. Expired blocks are removed as they are found."""
        if key not in self._never_correct:
            return False
        until = self._never_correct[key]
        if until is None or until > self._clock():
            return True
        del self._never_correct[key]
        self._save()
        return False

    def _drop_expired(self) -> None:
        now = self._clock()
        expired = [w for w, until in self._never_correct.items() if until is not None and until <= now]
        for word in expired:
            del self._never_correct[word]
        if expired:
            self._save()

    # ----- saving and loading -----

    def _load(self) -> None:
        if not self._path.exists():
            return
        try:
            data = json.loads(self._path.read_text(encoding="utf-8"))
            self._personal_words = {normalize(w) for w in data.get("personal_words", []) if is_valid_word(w)}
            self._never_correct = self._load_blocks(data)
            counts = data.get("unknown_counts", {})
            self._unknown_counts = {
                normalize(w): int(c) for w, c in counts.items() if is_valid_word(w) and int(c) > 0
            }
            self._drop_expired()
        except (OSError, ValueError, AttributeError, TypeError):
            # An unreadable file must not stop the engine. Keep a copy for inspection and start empty.
            backup = self._path.with_suffix(self._path.suffix + ".corrupt")
            try:
                os.replace(self._path, backup)
            except OSError:
                pass
            self._personal_words = set()
            self._never_correct = {}
            self._unknown_counts = {}

    def _load_blocks(self, data: dict) -> dict[str, float | None]:
        """Reads blocks in the current format (word -> end time) and the older list format (a plain list).
        A word from the older format gets the default block time from now."""
        stored = data.get("never_correct", {})
        if isinstance(stored, list):
            default_until = self._clock() + DEFAULT_BLOCK_HOURS * 3600
            return {normalize(w): default_until for w in stored if is_valid_word(w)}
        return {
            normalize(w): (None if until is None else float(until))
            for w, until in stored.items()
            if is_valid_word(w)
        }

    def _save(self) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        data = {
            "personal_words": sorted(self._personal_words),
            "never_correct": dict(sorted(self._never_correct.items())),
            "unknown_counts": dict(self._unknown_counts),
        }
        temporary = self._path.with_suffix(self._path.suffix + ".tmp")
        temporary.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
        os.replace(temporary, self._path)
