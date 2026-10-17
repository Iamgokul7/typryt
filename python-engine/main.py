"""Typryt correction engine: FastAPI server on localhost."""

from contextlib import asynccontextmanager

from fastapi import FastAPI
from pydantic import BaseModel

from apostrophe import resolve_pending
from comma_trigger import apply_comma_rule, needs_comma_after
from context_buffer import BufferStore
from punctuation import fix_punctuation
from spell_checker import SpellChecker

spell_checker: SpellChecker | None = None
buffers = BufferStore()


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Load the dictionary once at startup so each request stays fast.
    global spell_checker
    spell_checker = SpellChecker()
    yield


app = FastAPI(title="Typryt Engine", lifespan=lifespan)


class CorrectRequest(BaseModel):
    word: str
    # Identifies the typing session; each session has its own 5-word context buffer.
    session_id: str = "default"
    abbreviations_enabled: bool = True


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

    result = spell_checker.correct(request.word, previous, request.abbreviations_enabled)
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
