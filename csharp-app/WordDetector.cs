using System.Text;
using System.Text.RegularExpressions;

namespace Typryt;

/// <summary>
/// One keystroke, already translated from the Windows virtual key by KeyboardHook.
/// Character is the printed character, or null for keys that print nothing.
/// </summary>
public readonly record struct KeyInput(char? Character, bool IsBackspace, bool IsBreak);

/// <summary>A word that ended with a terminator key (space or punctuation).</summary>
public sealed record CompletedWord(string Text, char Terminator);

/// <summary>
/// Builds the word being typed. A space or punctuation key completes the word.
/// Enter, Tab, arrows, and shortcuts break the word without completing it.
///
/// A period is held back: "www.google.com" must not split at the dots. The period
/// only completes a word when the next key is a space or other terminator.
/// </summary>
public sealed class WordDetector
{
    private readonly StringBuilder _word = new();
    private CompletedWord? _pendingPeriod;

    private static readonly char[] Terminators = [' ', ',', '!', '?', ';', ':'];

    private static readonly Regex MixedAlphanumeric = new(@"[A-Za-z]\d|\d[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex UrlOrEmail = new(@"(://|www\.|@|\.(com|org|net|io|dev)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string CurrentWord => _word.ToString();

    public CompletedWord? OnKey(KeyInput key)
    {
        if (key.IsBackspace)
        {
            if (_pendingPeriod is not null)
            {
                // The period was deleted, so the word is back to what it was before the period.
                _word.Clear().Append(_pendingPeriod.Text);
                _pendingPeriod = null;
            }
            else if (_word.Length > 0)
            {
                _word.Length--;
            }
            return null;
        }

        if (key.IsBreak || key.Character is null)
        {
            _pendingPeriod = null;
            _word.Clear();
            return null;
        }

        char c = key.Character.Value;

        if (c == '.')
        {
            if (_pendingPeriod is not null || _word.Length == 0)
            {
                // A second period, or a period with no word before it: nothing to correct.
                _pendingPeriod = null;
                _word.Clear();
                return null;
            }
            _pendingPeriod = new CompletedWord(_word.ToString(), '.');
            _word.Clear();
            return null;
        }

        if (char.IsLetterOrDigit(c) || c == '\'')
        {
            if (_pendingPeriod is not null)
            {
                // A letter right after a period means the period was inside the word (a URL or name).
                _word.Append(_pendingPeriod.Text).Append('.');
                _pendingPeriod = null;
            }
            _word.Append(c);
            return null;
        }

        if (Array.IndexOf(Terminators, c) >= 0)
        {
            CompletedWord? result = null;
            if (_pendingPeriod is not null)
            {
                if (IsCorrectable(_pendingPeriod.Text)) result = _pendingPeriod;
                _pendingPeriod = null;
            }
            else if (_word.Length > 0 && IsCorrectable(_word.ToString()))
            {
                result = new CompletedWord(_word.ToString(), c);
            }
            _word.Clear();
            return result;
        }

        // Any other printable character (symbols, brackets, slashes) breaks the word.
        _pendingPeriod = null;
        _word.Clear();
        return null;
    }

    /// <summary>
    /// Pre-filters from the spec. These words are never sent to the engine:
    /// ALL CAPS, numbers, mixed letters and digits, URLs, and email addresses.
    /// </summary>
    public static bool IsCorrectable(string word)
    {
        if (word.Length == 0) return false;
        if (word.All(char.IsDigit)) return false;
        if (word.Length > 1 && word.All(c => !char.IsLetter(c) || char.IsUpper(c))) return false;
        if (MixedAlphanumeric.IsMatch(word)) return false;
        if (UrlOrEmail.IsMatch(word)) return false;
        return true;
    }
}
