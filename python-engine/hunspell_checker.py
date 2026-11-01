"""Layer 2 spell checker: Hunspell dictionary via the pure-Python spylls package.

spylls reads the same .aff/.dic files as Hunspell, so no C++ build is needed.
The en_US dictionary lives in python-engine/dictionaries/.

Asking spylls for suggestions is slow (about 100 to 150 ms), so suggestions are only requested through
suggest_within_budget: it gives up after a fixed time, and the answer is kept in a cache when it arrives late.
"""

import functools
import itertools
import sys
import threading
from collections import OrderedDict
from concurrent.futures import Future, ThreadPoolExecutor, TimeoutError as FutureTimeout
from pathlib import Path

from spylls.hunspell import Dictionary

# A packaged engine unpacks its bundled files under sys._MEIPASS; from source they sit next to this file.
_BUNDLE_ROOT = Path(getattr(sys, "_MEIPASS", Path(__file__).parent))
DICTIONARY_DIR = _BUNDLE_ROOT / "dictionaries"
DICTIONARY_BASENAME = "en_US"

# How long a correction waits for Hunspell's suggestions. On timeout the word is left unchanged.
SUGGEST_BUDGET_SECONDS = 0.30
# Only the first few suggestions matter, and spylls produces them lazily, so the rest are never computed.
SUGGESTION_COUNT = 3
CACHE_SIZE = 2048


class HunspellChecker:
    def __init__(self, dictionary_dir: Path = DICTIONARY_DIR) -> None:
        self._dictionary = Dictionary.from_files(str(dictionary_dir / DICTIONARY_BASENAME))
        self._pool = ThreadPoolExecutor(max_workers=2, thread_name_prefix="hunspell-suggest")
        self._cache: OrderedDict[str, list[str]] = OrderedDict()
        self._pending: dict[str, Future] = {}
        # Guards the cache and the pending table only. Re-entrant: a future that is already finished runs its
        # done-callback at once, on the thread that is holding the lock.
        self._lock = threading.RLock()

    def is_valid(self, word: str) -> bool:
        return self._dictionary.lookup(word)

    def suggest(self, word: str) -> list[str]:
        return list(self._dictionary.suggest(word))

    def cached_suggestions(self, word: str) -> list[str] | None:
        """The suggestions already known for the word, or None. Never starts a computation, so it cannot take a
        worker away from a correction that is waiting for its own answer."""
        with self._lock:
            return self._cache.get(word.lower())

    def suggest_within_budget(self, word: str, budget: float = SUGGEST_BUDGET_SECONDS) -> list[str] | None:
        """The first suggestions (lowercase), or None when they were not ready within the budget. A suggestion that
        finishes late is cached, so the same word is answered at once next time."""
        key = word.lower()
        with self._lock:
            cached = self._cache.get(key)
            if cached is not None:
                self._cache.move_to_end(key)
                return cached
            future = self._pending.get(key)
            if future is None:
                future = self._pool.submit(self._compute, key)
                self._pending[key] = future
                future.add_done_callback(functools.partial(self._store, key))
        try:
            return future.result(timeout=budget)
        except FutureTimeout:
            return None

    def _compute(self, key: str) -> list[str]:
        return [s.lower() for s in itertools.islice(self._dictionary.suggest(key), SUGGESTION_COUNT)]

    def _store(self, key: str, future: Future) -> None:
        with self._lock:
            self._pending.pop(key, None)
            if future.exception() is None:
                self._cache[key] = future.result()
                while len(self._cache) > CACHE_SIZE:
                    self._cache.popitem(last=False)
