namespace Typryt;

/// <summary>
/// Remembers the last correction so the user can undo it.
///
/// An undo is allowed only within UndoWindow of the correction, and only when the user
/// has typed nothing since. Deleting characters after more text was typed would remove
/// the wrong text. The engine, not this class, remembers which words were undone.
/// </summary>
public sealed class UndoManager
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(3);

    private readonly Func<DateTime> _clock;
    private Correction? _last;

    /// <summary>A correction the app applied to the screen.</summary>
    public sealed record Correction(string Original, string Corrected, string Trail, int KeySequence, DateTime AppliedAt,
        bool IsSpan = false, string? StatsOriginal = null, string? StatsCorrected = null);

    /// <summary>Why an undo was refused. None means the undo is allowed.</summary>
    public enum UndoRefusal
    {
        None,
        NothingToUndo,
        TooLate,
        TypedSince,
    }

    /// <summary>The outcome of an undo request: the correction to undo, or the reason it was refused.</summary>
    public sealed record UndoAttempt(Correction? Correction, UndoRefusal Refusal);

    public UndoManager(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Records a correction that was just applied. The newest correction replaces the older one.</summary>
    public void Record(string original, string corrected, string trail, int keySequence)
    {
        _last = new Correction(original, corrected, trail, keySequence, _clock());
    }

    /// <summary>
    /// Records a change that is more than one word: a comma added to the earlier word, or a punctuation or spacing fix.
    /// Undo restores <paramref name="before"/> exactly. When the same key already recorded a word correction, the two
    /// are merged so that one undo restores the text as it was typed.
    /// </summary>
    public void RecordSpan(string before, string after, int keySequence, string? statsOriginal = null, string? statsCorrected = null)
    {
        var older = _last;
        if (older is not null && !older.IsSpan && _clock() - older.AppliedAt <= UndoWindow)
        {
            // The word correction is on screen inside the text this change rewrites, followed by at most the few
            // characters typed after it ("receive" + " " + " " collapsed to one space).
            string olderOnScreen = older.Corrected + older.Trail;
            int at = before.LastIndexOf(olderOnScreen, StringComparison.Ordinal);
            if (at >= 0 && before.Length - (at + olderOnScreen.Length) <= 8)
            {
                before = before[..at] + older.Original + older.Trail + before[(at + olderOnScreen.Length)..];
                statsOriginal ??= older.StatsOriginal ?? (older.IsSpan ? null : older.Original);
                statsCorrected ??= older.StatsCorrected ?? (older.IsSpan ? null : older.Corrected);
            }
        }
        _last = new Correction(before, after, "", keySequence, _clock(), IsSpan: true, statsOriginal, statsCorrected);
    }

    /// <summary>Returns the correction to undo, or null if undo is not allowed now.</summary>
    /// <param name="currentKeySequence">The app's key counter right now.</param>
    public Correction? TryUndo(int currentKeySequence) => Attempt(currentKeySequence).Correction;

    /// <summary>
    /// Decides an undo request. A refused request forgets the pending correction, so the same
    /// refusal is not reported again for a later Alt+U press.
    /// </summary>
    /// <param name="currentKeySequence">The app's key counter right now.</param>
    public UndoAttempt Attempt(int currentKeySequence)
    {
        var last = _last;
        if (last is null) return new UndoAttempt(null, UndoRefusal.NothingToUndo);

        if (_clock() - last.AppliedAt > UndoWindow)
        {
            _last = null;
            return new UndoAttempt(null, UndoRefusal.TooLate);
        }

        if (currentKeySequence != last.KeySequence)
        {
            _last = null;
            return new UndoAttempt(null, UndoRefusal.TypedSince);
        }

        _last = null;
        return new UndoAttempt(last, UndoRefusal.None);
    }

    /// <summary>Forgets the pending correction, for example when the user moves to another window.</summary>
    public void Clear() => _last = null;
}
