using System.IO;
using Microsoft.Win32;

namespace Typryt;

/// <summary>
/// Finds the apps installed on this PC. It reads the Windows uninstall list (machine and user,
/// 64-bit and 32-bit) and adds built-in apps such as Notepad that do not appear there.
/// An app is only listed when its program file can be found, because that is what the
/// permission check matches against.
/// </summary>
public static class AppScanner
{
    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public static List<AppPermission> Scan()
    {
        var found = new Dictionary<string, AppPermission>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var keyPath in UninstallKeys)
            {
                using var key = root.OpenSubKey(keyPath);
                if (key is null) continue;
                foreach (var subName in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(subName);
                    if (app is null) continue;
                    var name = app.GetValue("DisplayName") as string;
                    var exe = ExecutableFrom(app);
                    if (string.IsNullOrWhiteSpace(name) || exe is null) continue;
                    AddIfNew(found, exe, name);
                }
            }
        }

        // Built-in apps are not in the uninstall list, so they are added here.
        foreach (var builtIn in DefaultApps.Build())
        {
            var exe = FindBuiltIn(builtIn.Process);
            if (exe is not null) AddIfNew(found, exe, builtIn.Name);
        }

        return found.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Installers, updaters, and helpers are not apps the user types in, so they are not listed.
    private static readonly string[] GenericPrefixes =
    [
        "unins", "uninst", "update", "setup", "install", "helper", "crash", "repair",
    ];

    private static void AddIfNew(Dictionary<string, AppPermission> found, string exePath, string name)
    {
        var process = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (process.Length == 0 || found.ContainsKey(process)) return;
        if (GenericPrefixes.Any(prefix => process.StartsWith(prefix, StringComparison.Ordinal))) return;
        found[process] = new AppPermission { Process = process, Name = name, Enabled = false };
    }

    /// <summary>The program file an uninstall entry points to, if it exists on disk.</summary>
    private static string? ExecutableFrom(RegistryKey app)
    {
        var icon = app.GetValue("DisplayIcon") as string;
        if (!string.IsNullOrWhiteSpace(icon))
        {
            // DisplayIcon can look like "C:\...\app.exe,0" or be quoted.
            var path = icon.Trim().Trim('"');
            int comma = path.LastIndexOf(',');
            if (comma > 0) path = path[..comma].Trim('"');
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
        }

        var location = app.GetValue("InstallLocation") as string;
        if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
        {
            // Use the first program file in the install folder, if there is exactly one.
            var exes = Directory.GetFiles(location, "*.exe", SearchOption.TopDirectoryOnly);
            if (exes.Length == 1) return exes[0];
        }
        return null;
    }

    /// <summary>Finds a built-in program in the Windows folders.</summary>
    private static string? FindBuiltIn(string process)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(windows, "System32", process + ".exe"),
            Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", process + ".exe"),
            Path.Combine(programFiles, "Windows NT", "Accessories", process + ".exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
