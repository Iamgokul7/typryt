namespace Typryt;

/// <summary>
/// Remembers the last correction so the user can undo it.
///
/// An undo is allowed only within UndoWindow of the correction, and only when the user
/// has typed nothing since. Deleting characters after more text was typed would remove
/// the wrong text. Every undone word is remembered for the life of the app, so it is
/// never corrected again. Saving that list to disk is Phase 7.
/// </summary>
public sealed class UndoManager
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(3);

    private readonly Func<DateTime> _clock;
    private readonly HashSet<string> _undoneWords = new(StringComparer.OrdinalIgnoreCase);
    private Correction? _last;

    /// <summary>A correction the app applied to the screen.</summary>
    public sealed record Correction(string Original, string Corrected, string Trail, int KeySequence, DateTime AppliedAt);

    public UndoManager(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Words the user has undone. These are never corrected again.</summary>
    public IReadOnlyCollection<string> UndoneWords => _undoneWords;

    public bool IsUndone(string word) => _undoneWords.Contains(word);

    /// <summary>Records a correction that was just applied. The newest correction replaces the older one.</summary>
    public void Record(string original, string corrected, string trail, int keySequence)
    {
        _last = new Correction(original, corrected, trail, keySequence, _clock());
    }

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
        _undoneWords.Add(last.Original);
        return new UndoAttempt(last, UndoRefusal.None);
    }

    /// <summary>Forgets the pending correction, for example when the user moves to another window.</summary>
    public void Clear() => _last = null;
}
