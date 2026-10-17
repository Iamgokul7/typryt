"""Typryt correction engine: FastAPI server on localhost."""

from contextlib import asynccontextmanager

from fastapi import FastAPI
from pydantic import BaseModel

from comma_trigger import apply_comma_rule, needs_comma_after
from punctuation import fix_punctuation
from spell_checker import SpellChecker

spell_checker: SpellChecker | None = None


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Load the dictionary once at startup so each request stays fast.
    global spell_checker
    spell_checker = SpellChecker()
    yield


app = FastAPI(title="Typryt Engine", lifespan=lifespan)


class CorrectRequest(BaseModel):
    word: str
    # The word typed just before this one. Used for the comma trigger rule.
    previous_word: str | None = None
    abbreviations_enabled: bool = True


class CorrectResponse(BaseModel):
    original: str
    correction: str
    confidence: float
    # True when the client should insert a comma after previous_word.
    comma_after_previous: bool = False


class PunctuateRequest(BaseModel):
    text: str


class PunctuateResponse(BaseModel):
    text: str


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/correct", response_model=CorrectResponse)
def correct(request: CorrectRequest) -> CorrectResponse:
    result = spell_checker.correct(request.word, request.abbreviations_enabled)
    comma = request.previous_word is not None and needs_comma_after(request.previous_word, request.word)
    return CorrectResponse(
        original=result.original,
        correction=result.correction,
        confidence=result.confidence,
        comma_after_previous=comma,
    )


@app.post("/punctuate", response_model=PunctuateResponse)
def punctuate(request: PunctuateRequest) -> PunctuateResponse:
    text = fix_punctuation(request.text)
    text = apply_comma_rule(text)
    return PunctuateResponse(text=text)
