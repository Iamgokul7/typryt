using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typryt;

/// <summary>
/// One app and whether corrections are allowed in it. Process is the program name without
/// ".exe" in lowercase, as Process.ProcessName reports it (for example "chrome").
/// </summary>
public sealed class AppPermission
{
    [JsonPropertyName("process")] public string Process { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

/// <summary>The contents of shared\config.json.</summary>
public sealed class AppConfig
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("first_run_done")] public bool FirstRunDone { get; set; }
    [JsonPropertyName("apps")] public List<AppPermission> Apps { get; set; } = new();
}

/// <summary>
/// Decides which apps corrections may run in. Settings live in shared\config.json, next to the
/// engine's learned-words file. Apps not in the list are off until the user turns them on.
/// </summary>
public sealed class PermissionManager
{
    public const string ConfigFileName = "config.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private AppConfig _config = new();

    public PermissionManager(string path)
    {
        _path = path;
    }

    /// <summary>Default path: the shared folder of this repository, found by searching up from the app's folder.</summary>
    public static string DefaultPath()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null)
        {
            var shared = Path.Combine(folder.FullName, "shared");
            if (Directory.Exists(shared)) return Path.Combine(shared, ConfigFileName);
            folder = folder.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
    }

    public bool FirstRunDone => _config.FirstRunDone;

    public IReadOnlyList<AppPermission> Apps => _config.Apps;

    /// <summary>Loads the config. A missing or unreadable file starts from the defaults.</summary>
    public void Load()
    {
        if (!File.Exists(_path))
        {
            _config = new AppConfig { Apps = DefaultApps.Build() };
            return;
        }
        try
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), Options);
            _config = loaded ?? new AppConfig();
            _config.Apps ??= new List<AppPermission>();
            _config.Apps.RemoveAll(a => string.IsNullOrWhiteSpace(a.Process));
        }
        catch (JsonException)
        {
            // A damaged file is kept aside, and the defaults are used.
            File.Move(_path, _path + ".corrupt", overwrite: true);
            _config = new AppConfig { Apps = DefaultApps.Build() };
        }
    }

    /// <summary>Adds apps found by the scan that are not in the list yet. They start off.</summary>
    public void MergeScanned(IEnumerable<AppPermission> found)
    {
        foreach (var app in found)
        {
            if (_config.Apps.Any(a => string.Equals(a.Process, app.Process, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            _config.Apps.Add(new AppPermission { Process = app.Process, Name = app.Name, Enabled = false });
        }
    }

    public void SetEnabled(string process, bool enabled)
    {
        var app = _config.Apps.FirstOrDefault(a => string.Equals(a.Process, process, StringComparison.OrdinalIgnoreCase));
        if (app is not null) app.Enabled = enabled;
    }

    public void MarkFirstRunDone() => _config.FirstRunDone = true;

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_config, Options));
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>True when corrections may run in the app with this process name (for example "notepad").</summary>
    public bool IsAllowed(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        return _config.Apps.Any(a => a.Enabled && string.Equals(a.Process, processName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The apps that start on or off before the user changes anything.</summary>
public static class DefaultApps
{
    public static List<AppPermission> Build() =>
    [
        On("chrome", "Google Chrome"),
        On("msedge", "Microsoft Edge"),
        On("firefox", "Firefox"),
        On("notepad", "Notepad"),
        On("wordpad", "WordPad"),
        On("winword", "Microsoft Word"),
        Off("Code", "Visual Studio Code"),
        Off("WindowsTerminal", "Windows Terminal"),
        Off("cmd", "Command Prompt"),
        Off("powershell", "Windows PowerShell"),
        Off("pwsh", "PowerShell 7"),
    ];

    private static AppPermission On(string process, string name) => new() { Process = process, Name = name, Enabled = true };

    private static AppPermission Off(string process, string name) => new() { Process = process, Name = name, Enabled = false };
}
