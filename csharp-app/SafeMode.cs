namespace Typryt;

/// <summary>
/// The safety net. When something goes wrong that could cost the user keystrokes (a fault in the keyboard hook, holds
/// that keep being cut short, an engine that cannot be restarted), Typryt lets go of every held key, pauses itself,
/// says so once, and stays paused until the user turns it on again from the tray or the master switch.
/// The log line carries a reason code and never any text.
/// </summary>
public static class SafeMode
{
    public const string HookFault = "hook_fault";
    public const string ForcedReleases = "hold_releases";
    public const string EngineDown = "engine_down";

    private const int ForcedReleaseLimit = 5;
    private static readonly TimeSpan ForcedReleaseWindow = TimeSpan.FromSeconds(60);

    private static readonly object Gate = new();
    private static readonly Queue<DateTime> Forced = new();
    private static volatile bool _paused;

    public static bool Paused => _paused;

    public static string Reason { get; private set; } = "";

    /// <summary>Raised once when the app pauses itself. The argument is the reason code.</summary>
    public static event Action<string>? Entered;

    /// <summary>Raised when the user turns the app on again.</summary>
    public static event Action? Cleared;

    public static void Enter(string reason)
    {
        lock (Gate)
        {
            if (_paused) return;
            _paused = true;
            Reason = reason;
        }
        // Whatever was held is delivered first, so no key is lost while the app pauses.
        try { KeyboardHook.ReleaseAllHolds(); } catch (Exception ex) { Log.Fault("safe mode release", ex); }
        Log.Write("safemode", $"paused: {reason}");
        Entered?.Invoke(reason);
    }

    public static void Clear()
    {
        lock (Gate)
        {
            if (!_paused) return;
            _paused = false;
            Reason = "";
            Forced.Clear();
        }
        Log.Write("safemode", "turned on again by the user");
        Cleared?.Invoke();
    }

    /// <summary>A hold was ended by its time limit instead of by the correction. Five of these within a minute pause the app.</summary>
    public static void NoteForcedRelease()
    {
        bool tooMany;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            Forced.Enqueue(now);
            while (Forced.Count > 0 && now - Forced.Peek() > ForcedReleaseWindow) Forced.Dequeue();
            tooMany = Forced.Count >= ForcedReleaseLimit;
        }
        if (tooMany) Enter(ForcedReleases);
    }

    /// <summary>The words shown to the user for a reason code.</summary>
    public static string Describe(string reason) => reason switch
    {
        HookFault => "keyboard hook fault",
        ForcedReleases => "corrections kept running late",
        EngineDown => "correction engine stopped",
        _ => reason,
    };
}
