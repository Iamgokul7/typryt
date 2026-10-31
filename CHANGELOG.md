# Changelog

## 1.0.2 (release candidate)

Everything since the 1.0.0 tag. Test evidence for this release is summarised in the README and was produced on the installed build.

### Safety (QA batch 1)
- Nothing typed in a password field, or in a control that cannot be identified, is buffered, corrected or sent to the engine (BUG-03).
- Keys typed while a correction is made are no longer lost, doubled or reordered (BUG-02); Backspaces are counted from the text that is really there in Notepad (BUG-01).
- Typed words are kept out of the logs and out of `personal_dict.json` (BUG-11): typing counts are stored as salted hashes only.

### Accuracy (QA batch 2)
- An unknown word is no longer treated as a typo: names, places, brands, loanwords and technical words are left alone (BUG-04, BUG-13). A word is changed only on strong evidence.
- The abbreviations switch now controls every abbreviation (BUG-06).
- Words that contain an apostrophe go through the rules instead of skipping them (BUG-14); apostrophes are handled by pattern (stem plus ending) instead of a word list.
- Stretched words are handled by pattern (`okk`, `sooo`, `thankss`, `plss`, `heyyy`).
- A missing space after a comma or ellipsis typed into the next word is added (BUG-15).
- New lexicons for technical, Indian-English, name, place and brand words and a curated misspelling map; the Hunspell dictionary is read through the pure-Python `spylls` package.

### Application layer
- The delimiter you type (Space, punctuation, Enter, Tab) is held while the word is corrected, then delivered, so only the word changes and no key is lost. A timer in the keyboard hook releases a held key after 150 ms (300 ms for Enter and Tab) whatever the correction is doing.
- Quoted and bracketed words, and a word before a period at the end of the text, are corrected.
- A dead engine is restarted with a back-off (at most 3 times in 5 minutes) and a single message (BUG-10).
- Running the program a second time brings the running dashboard forward (BUG-18).
- A settings or dictionary file saved with a byte-order mark is no longer renamed `.corrupt` and lost (BUG-19).

### This release
- **Undo** now also restores comma insertions and spacing/punctuation fixes (double space, space before punctuation, ellipsis, doubled punctuation) exactly (BUG-07). Undo stays per correction and never blocks a word.
- **Correction levels** are real: the engine returns graded confidence below the Medium threshold for weaker guesses, so High and Extreme change more than Medium and Low changes less (BUG-09). Medium behaviour is unchanged.
- **Learning** counts every unknown word that was left unchanged, not only words with no near neighbour in the dictionary (BUG-08); after the third typing the word is added and never changed again.
- **Edits inside a word**: after Backspace past the start of a word, paste, cut, undo or moving the cursor, Typryt corrects the real word before the cursor where the app's text can be read (Notepad, Word) and otherwise leaves the word as typed.
- **Fault safety net**: handlers for unhandled exceptions (logged by type and stack, never text), a keyboard hook that never throws, and a safe mode that releases held keys and pauses Typryt after a hook fault, repeated forced hold releases or an engine that cannot be restarted.
- **Engine identity**: every launch picks a free port and a random token; the engine requires the token on every request, proves it knows it in the health check, and listens on `127.0.0.1` only.
- **Installer**: per-user, no administrator rights, x64 only, shows the licence, has an icon, closes a running Typryt and its engine before installing and uninstalling (by install path), removes the start-with-Windows entry, asks whether to also delete your settings (default No), installs the third-party notices, and is built by one repeatable script (`installer/build_release.ps1`) that checks the packaged engine.
- Licence changed to PolyForm Strict 1.0.0; third-party notices added.
- The engine warms up its dictionary lookup at start.

## 1.0.1
Installer-based build used for the first QA run.

## 1.0.0
First version.
