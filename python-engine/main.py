"""Typryt correction engine: FastAPI server on localhost."""

from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

from apostrophe import resolve_pending
from comma_trigger import apply_comma_rule, needs_comma_after
from context_buffer import BufferStore
from learner import Learner
from punctuation import fix_punctuation
from spell_checker import SpellChecker

# The user's learned words live in the shared folder, next to config.json.
LEARNER_PATH = Path(__file__).resolve().parent.parent / "shared" / "personal_dict.json"

spell_checker: SpellChecker | None = None
learner: Learner | None = None
buffers = BufferStore()


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Load the dictionary and the learned words once at startup so each request stays fast.
    global spell_checker, learner
    learner = Learner(LEARNER_PATH)
    spell_checker = SpellChecker(learner)
    yield


app = FastAPI(title="Typryt Engine", lifespan=lifespan)


class CorrectRequest(BaseModel):
    word: str
    # Identifies the typing session; each session has its own 5-word context buffer.
    session_id: str = "default"
    abbreviations_enabled: bool = True
    # Learning mode: when off, unknown words are not counted and undos are not remembered.
    learning_enabled: bool = True


class PreviousCorrection(BaseModel):
    # An earlier word on screen that the next word has settled (its -> it's, were -> we're).
    original: str
    correction: str


class CorrectResponse(BaseModel):
    original: str
    correction: str
    confidence: float
    # True when the client should insert a comma after the previous word.
    comma_after_previous: bool = False
    # Set when this word settles the previous one. The client replaces that word on screen.
    previous_correction: PreviousCorrection | None = None


class ResetRequest(BaseModel):
    session_id: str = "default"


class PunctuateRequest(BaseModel):
    text: str


class PunctuateResponse(BaseModel):
    text: str


class WordRequest(BaseModel):
    word: str


class LearnUndoRequest(BaseModel):
    word: str


class DictionarySnapshot(BaseModel):
    personal_words: list[str]
    never_correct: list[str]


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/correct", response_model=CorrectResponse)
def correct(request: CorrectRequest) -> CorrectResponse:
    buffer = buffers.get(request.session_id)
    previous = buffer.previous()

    comma = previous is not None and needs_comma_after(previous, request.word)

    previous_correction = None
    if previous is not None:
        revised = resolve_pending(previous, request.word)
        if revised is not None:
            previous_correction = PreviousCorrection(original=previous, correction=revised)
            buffer.replace_last(revised)

    result = spell_checker.correct(
        request.word,
        previous,
        request.abbreviations_enabled,
        request.learning_enabled,
    )
    buffer.push(result.correction)

    return CorrectResponse(
        original=result.original,
        correction=result.correction,
        confidence=result.confidence,
        comma_after_previous=comma,
        previous_correction=previous_correction,
    )


@app.post("/reset")
def reset(request: ResetRequest) -> dict[str, str]:
    buffers.reset(request.session_id)
    return {"status": "reset"}


@app.post("/punctuate", response_model=PunctuateResponse)
def punctuate(request: PunctuateRequest) -> PunctuateResponse:
    text = fix_punctuation(request.text)
    text = apply_comma_rule(text)
    return PunctuateResponse(text=text)


@app.post("/learn/undo")
def learn_undo(request: LearnUndoRequest) -> dict[str, str]:
    # The user undid a correction: this word is never corrected again.
    learner.record_undo(request.word)
    return {"status": "recorded"}


@app.get("/dictionary", response_model=DictionarySnapshot)
def dictionary() -> DictionarySnapshot:
    return DictionarySnapshot(**learner.snapshot())


@app.post("/dictionary/words")
def add_personal_word(request: WordRequest) -> dict[str, bool]:
    if not learner.add_word(request.word):
        raise HTTPException(status_code=400, detail="Not a valid word.")
    return {"added": True}


@app.delete("/dictionary/words/{word}")
def remove_personal_word(word: str) -> dict[str, bool]:
    return {"removed": learner.remove_word(word)}


@app.delete("/dictionary/never-correct/{word}")
def remove_never_correct(word: str) -> dict[str, bool]:
    # Unblocks a word the user undid, without restarting the app.
    return {"removed": learner.remove_never_correct(word)}
