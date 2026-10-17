"""Abbreviation expansions. Keys are lowercase; values are the expansion.

Only lowercase or Title-case tokens are looked up. ALL CAPS tokens (LOL, CEO,
ROFL) are never expanded; the engine checks that before this map is used.
Slang that is already in slang_dict.py (imo, tbh, omg, wtf, smh, nvm, ttyl,
np, ty, gg) is not repeated here, because slang runs first.

The engine applies this map only when abbreviations are enabled in settings.
"""

ABBREVIATIONS = {
    "brb": "be right back",
    "idk": "I don't know",
    "ngl": "not gonna lie",
    "fyi": "for your information",
    "btw": "by the way",
    "aka": "also known as",
    "asap": "as soon as possible",
    "eta": "estimated time of arrival",
    "tldr": "too long didn't read",
    "afk": "away from keyboard",
    "afaik": "as far as I know",
    "bbl": "be back later",
    "bbs": "be back soon",
    "bfn": "bye for now",
    "diy": "do it yourself",
    "dm": "direct message",
    "eod": "end of day",
    "eow": "end of week",
    "fomo": "fear of missing out",
    "ftw": "for the win",
    "gtg": "got to go",
    "iirc": "if I recall correctly",
    "irl": "in real life",
    "jk": "just kidding",
    "lmk": "let me know",
    "nbd": "no big deal",
    "omw": "on my way",
    "tba": "to be announced",
    "tbd": "to be determined",
    "tia": "thanks in advance",
    "tmi": "too much information",
    "wfh": "work from home",
    "yw": "you're welcome",
    "approx": "approximately",
    "dept": "department",
    "govt": "government",
    "info": "information",
    "esp": "especially",
    "vs": "versus",
}
