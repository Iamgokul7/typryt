"""Typryt correction engine: FastAPI server on localhost."""

from contextlib import asynccontextmanager

from fastapi import FastAPI
from pydantic import BaseModel

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


class CorrectResponse(BaseModel):
    original: str
    correction: str
    confidence: float


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/correct", response_model=CorrectResponse)
def correct(request: CorrectRequest) -> CorrectResponse:
    result = spell_checker.correct(request.word)
    return CorrectResponse(
        original=result.original,
        correction=result.correction,
        confidence=result.confidence,
    )
