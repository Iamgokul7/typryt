namespace Typryt;

/// <summary>
/// Whether the correction engine is believed to be answering. Set by the engine watchdog and by failed
/// requests, read by the keyboard hook. When it is down, no key is held for a correction, so typing stays normal.
/// </summary>
public static class EngineState
{
    // Down until the engine has answered once: a key must never be held for an engine that is not there yet.
    private static volatile bool _up;

    public static bool Up => _up;

    public static void MarkUp() => _up = true;

    public static void MarkDown() => _up = false;
}
