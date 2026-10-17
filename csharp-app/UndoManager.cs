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

    /// <summary>Returns the correction to undo, or null if undo is not allowed now.</summary>
    /// <param name="currentKeySequence">The app's key counter right now.</param>
    public Correction? TryUndo(int currentKeySequence)
    {
        var last = _last;
        if (last is null) return null;

        bool withinWindow = _clock() - last.AppliedAt <= UndoWindow;
        bool nothingTypedSince = currentKeySequence == last.KeySequence;
        if (!withinWindow || !nothingTypedSince) return null;

        _last = null;
        _undoneWords.Add(last.Original);
        return last;
    }

    /// <summary>Forgets the pending correction, for example when the user moves to another window.</summary>
    public void Clear() => _last = null;
}
