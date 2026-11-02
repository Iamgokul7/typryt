namespace Typryt;

/// <summary>
/// Component-level disable flags for G6 memory-isolation testing.
/// Set TYPRYT_TEST_DISABLE to a comma-separated list of flag names (requires TYPRYT_TEST_INPUT=1).
/// Supported flags: focus_guard, verify, stats.
/// </summary>
internal static class TestMode
{
    private static readonly HashSet<string> _flags = Parse();

    private static HashSet<string> Parse()
    {
        if (!InputMarks.TestInput) return new HashSet<string>();
        var raw = Environment.GetEnvironmentVariable("TYPRYT_TEST_DISABLE") ?? "";
        return new HashSet<string>(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>FocusGuard disabled: corrections are never blocked, UI Automation subscription is skipped.</summary>
    public static bool DisableFocusGuard => _flags.Contains("focus_guard");

    /// <summary>Post-correction read-back (VerifyAfterAsync) disabled: no UI Automation text reads after corrections.</summary>
    public static bool DisableVerify => _flags.Contains("verify");

    /// <summary>Stats writes disabled: RecordCorrection and RecordUndo are skipped.</summary>
    public static bool DisableStats => _flags.Contains("stats");
}
