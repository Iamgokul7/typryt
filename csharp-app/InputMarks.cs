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
}
