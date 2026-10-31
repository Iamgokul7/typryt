namespace Typryt;

/// <summary>
/// Marks the keys this app sends itself, so the keyboard hook can ignore them.
/// Set TYPRYT_TEST_INPUT=1 to let the hook accept keys sent by other programs, which
/// lets the automated tests type into an app. It is off by default, so real use is unchanged.
/// </summary>
public static class InputMarks
{
    public static readonly IntPtr Own = new(0x54595052); // "TYPR"

    public static readonly bool TestInput = Environment.GetEnvironmentVariable("TYPRYT_TEST_INPUT") == "1";

    /// <summary>
    /// Test only (needs TYPRYT_TEST_INPUT too): TYPRYT_TEST_OLD_PATH=1 brings back the earlier way of correcting, so a test can
    /// compare the two: the word and its delimiter are replaced after the delimiter has been delivered, and the whole
    /// text is deleted and typed again instead of only the part that differs.
    /// </summary>
    public static readonly bool OldPath = TestInput && Environment.GetEnvironmentVariable("TYPRYT_TEST_OLD_PATH") == "1";
}
