"""5-word rolling context buffer, one per client session.

The engine keeps the last few words typed in each session. Those words drive the
comma trigger, capitalization after sentence ends, and the its/it's and were/we're
rules. Words are stored as the final text the user sees, so later rules compare
against what is actually on screen.
"""

import threading
from collections import deque

WINDOW_SIZE = 5


class ContextBuffer:
    def __init__(self) -> None:
        self._words: deque[str] = deque(maxlen=WINDOW_SIZE)

    def push(self, word: str) -> None:
        self._words.append(word)

    def replace_last(self, word: str) -> None:
        if self._words:
            self._words[-1] = word

    def previous(self) -> str | None:
        return self._words[-1] if self._words else None

    def words(self) -> list[str]:
        return list(self._words)


class BufferStore:
    """Holds one ContextBuffer per session id. Safe to call from FastAPI's thread pool."""

    def __init__(self) -> None:
        self._buffers: dict[str, ContextBuffer] = {}
        self._lock = threading.Lock()

    def get(self, session_id: str) -> ContextBuffer:
        with self._lock:
            buffer = self._buffers.get(session_id)
            if buffer is None:
                buffer = ContextBuffer()
                self._buffers[session_id] = buffer
            return buffer

    def reset(self, session_id: str) -> None:
        with self._lock:
            self._buffers.pop(session_id, None)
