using System.Windows.Automation;

namespace Typryt;

/// <summary>
/// Knows whether the focused control is a password field, so that nothing typed there is buffered,
/// sent to the engine, or corrected. Focus changes are followed with UI Automation events, and the
/// answer is cached for the hook thread. The worker also asks again just before it contacts the
/// engine. When the focused control cannot be identified, the answer is "do not correct".
/// </summary>
internal static class FocusGuard
{
    // The first UI Automation call into a browser can take well over 100 ms; later ones take a few.
    private const int CheckTimeoutMilliseconds = 250;

    private static volatile bool _blocked;
    private static int _generation;
    private static AutomationFocusChangedEventHandler? _handler;

    /// <summary>True when the control that has focus is a password field or could not be identified. Cheap: read on the hook thread.</summary>
    public static bool Blocked => _blocked;

    /// <summary>Changes every time focus moves. A job made under one generation is dropped under another.</summary>
    public static int Generation => Volatile.Read(ref _generation);

    public static void Start()
    {
        _handler = OnFocusChanged;
        Automation.AddAutomationFocusChangedEventHandler(_handler);

        // Reads the control that has focus now, which also warms up UI Automation for the first real check.
        Task.Run(() => Update(FocusedElementOrNull()));
    }

    public static void Stop()
    {
        if (_handler is null) return;
        try
        {
            Automation.RemoveAutomationFocusChangedEventHandler(_handler);
        }
        catch (Exception)
        {
            // Shutting down: nothing to undo.
        }
        _handler = null;
    }

    /// <summary>
    /// True when text typed under the given generation may be corrected: focus has not moved since the key
    /// was typed, and the control that has focus now is not a password field. False when that cannot be
    /// found out in time.
    /// </summary>
    public static async Task<bool> IsSafeAsync(int generation)
    {
        if (generation != Generation) return false;

        var read = Task.Run(() => IsPasswordOrUnknown(FocusedElementOrNull()));
        if (await Task.WhenAny(read, Task.Delay(CheckTimeoutMilliseconds)) != read) return false;
        return !await read && generation == Generation;
    }

    private static void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e) => Update(sender as AutomationElement);

    private static void Update(AutomationElement? element)
    {
        _blocked = IsPasswordOrUnknown(element);
        Interlocked.Increment(ref _generation);
    }

    private static bool IsPasswordOrUnknown(AutomationElement? element)
    {
        if (element is null) return true;
        try
        {
            return element.Current.IsPassword;
        }
        catch (Exception)
        {
            // The control went away or the app did not answer: treat it as unknown.
            return true;
        }
    }

    private static AutomationElement? FocusedElementOrNull()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
