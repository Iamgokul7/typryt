"""Abbreviation expansions. Keys are lowercase; values are the expansion.

An initialism or acronym for a phrase goes here; a shortening of a single word (thx, ty, srsly, bday) is
slang and always on (slang_dict.py). Only lowercase or Title-case tokens are looked up. ALL CAPS tokens
(CEO, NASA) are never expanded; the engine checks that before this map is used.

The engine applies this map only when abbreviations are enabled in settings. When they are off, a word in
this map is passed through unchanged and never reaches the spell checker.

Expansions are plain words without added punctuation, and "I" stays capital inside them.
Left out on purpose because they are ambiguous: pm, gm, gn, ge, cu, cob, oob, k8.
"""

ABBREVIATIONS = {
    # Chat
    "brb": "be right back",
    "idk": "I don't know",
    "ngl": "not gonna lie",
    "tbh": "to be honest",
    "imo": "in my opinion",
    "imho": "in my humble opinion",
    "omg": "oh my god",
    "omfg": "oh my god",
    "smh": "shaking my head",
    "wtf": "what the heck",
    "wth": "what the heck",
    "ikr": "I know right",
    "idc": "I don't care",
    "nvm": "never mind",
    "np": "no problem",
    "ofc": "of course",
    "rn": "right now",
    "atm": "at the moment",
    "hmu": "hit me up",
    "bff": "best friends forever",
    "ttyl": "talk to you later",
    "hbu": "how about you",
    "wbu": "what about you",
    "hru": "how are you",
    "hbd": "happy birthday",
    "gg": "good game",
    "nm": "not much",
    "fr": "for real",
    "ily": "I love you",
    "tysm": "thank you so much",
    "brt": "be right there",
    "tbf": "to be fair",
    "g2g": "got to go",
    "gtg": "got to go",
    "bbl": "be back later",
    "bbs": "be back soon",
    "bfn": "bye for now",
    "afk": "away from keyboard",
    "omw": "on my way",
    "jk": "just kidding",
    "irl": "in real life",
    "yolo": "you only live once",
    "fomo": "fear of missing out",
    "ftw": "for the win",
    "pov": "point of view",
    "ootd": "outfit of the day",
    "tgif": "thank God it's Friday",
    "nsfw": "not safe for work",
    "yw": "you're welcome",
    "tia": "thanks in advance",
    "tmi": "too much information",
    "nbd": "no big deal",

    # Writing and work
    "btw": "by the way",
    "fyi": "for your information",
    "aka": "also known as",
    "asap": "as soon as possible",
    "eta": "estimated time of arrival",
    "tldr": "too long didn't read",
    "afaik": "as far as I know",
    "iirc": "if I recall correctly",
    "fwiw": "for what it's worth",
    "icymi": "in case you missed it",
    "ymmv": "your mileage may vary",
    "ama": "ask me anything",
    "faq": "frequently asked questions",
    "rsvp": "please reply",
    "diy": "do it yourself",
    "dm": "direct message",
    "eod": "end of day",
    "eom": "end of message",
    "eow": "end of week",
    "lmk": "let me know",
    "tba": "to be announced",
    "tbd": "to be determined",
    "wfh": "work from home",
}
