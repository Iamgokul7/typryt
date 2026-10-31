# Lexicon and data sources

The engine's own word lists live in `python-engine/lexicons/` and are loaded once at startup into in-memory sets
(`lexicons.py`). A word in any lexicon is never corrected. The curated misspelling map is checked before any guessing.

| File | Entries | What it holds |
|---|---|---|
| `tech.txt` | 574 | programming languages, keywords, libraries, tools, platforms and common tech terms |
| `indian.txt` | 652 | Indian English words and loanwords: food, clothing, festivals, greetings, family terms, everyday words, plus common loanwords from other languages |
| `names_first.txt` | 741 | common first names, Indian and international |
| `names_last.txt` | 1,153 | common surnames, Indian and international |
| `places.txt` | 767 | Indian states and cities, world countries and cities |
| `brands.txt` | 454 | brands, products, services and well-known organisations |
| `misspellings.txt` | 1,866 | common misspellings with their corrections (exact match) |

## Source and licence

Every list above was **written for this project** by the Typryt developer from general knowledge. No third-party
list, dataset or text was copied, so there is no licence to carry: the files are covered by the project's own
licence (`LICENSE.txt`). Names, places, brands and words are facts, not creative works, and appear here only as
plain lowercase tokens.

Entries that equal a word of the frozen QA held-out set were deliberately left out, so that set stays unseen
by the engine's data.

## Checks

`_test_run/retest_batch2/validate_data.py` is run after every commit that touches this data. It checks that no
misspelling key is a real word (Hunspell or the frequency list), no key equals its value, no value is itself a key
(no chains), no lexicon entry is also a misspelling key, the slang and abbreviation tables share no key, and no
entry is a held-out word.

## Other data the engine uses (unchanged)

- `symspellpy` frequency dictionary and bigram dictionary, shipped with the symspellpy package (MIT licence).
- `dictionaries/en_US.aff` and `en_US.dic`, the Hunspell en_US dictionary read through the pure-Python `spylls`
  package (the dictionary's own licence is in its files; spylls is MPL 2.0).
