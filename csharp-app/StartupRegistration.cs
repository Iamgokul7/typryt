using Microsoft.Win32;

namespace Typryt;

/// <summary>
/// Starts Typryt when the current user signs in to Windows. This writes to the current user's
/// Run key only, so no administrator rights are needed.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Typryt";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Adds or removes the startup entry. The entry points to the running app's executable.</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The app's path is unknown.");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName);
        }
    }
}
