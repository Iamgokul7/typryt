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

/// <summary>How eager corrections are. Each level sets the least confidence a correction may have.</summary>
public enum CorrectionLevel
{
    Low,
    Medium,
    High,
    Extreme,
}

public static class CorrectionLevels
{
    // Low: only rule-based fixes (slang, apostrophes, abbreviations). Medium: clear misspellings
    // (one-letter mistakes). High: also two-letter mistakes. Extreme: also Hunspell suggestions.
    public static double ThresholdFor(CorrectionLevel level) => level switch
    {
        CorrectionLevel.Low => 0.95,
        CorrectionLevel.Medium => 0.9,
        CorrectionLevel.High => 0.6,
        CorrectionLevel.Extreme => 0.5,
        _ => 0.9,
    };

    public static CorrectionLevel Parse(string? value) =>
        Enum.TryParse<CorrectionLevel>(value, ignoreCase: true, out var level) ? level : CorrectionLevel.Medium;
}

/// <summary>The contents of shared\config.json.</summary>
public sealed class AppConfig
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("first_run_done")] public bool FirstRunDone { get; set; }
    [JsonPropertyName("apps")] public List<AppPermission> Apps { get; set; } = new();
    [JsonPropertyName("correction_level")] public string CorrectionLevel { get; set; } = "medium";
    [JsonPropertyName("theme")] public string Theme { get; set; } = "dark";
    [JsonPropertyName("master_enabled")] public bool MasterEnabled { get; set; } = true;
    [JsonPropertyName("abbreviations_enabled")] public bool AbbreviationsEnabled { get; set; } = true;
    [JsonPropertyName("auto_capitalize_enabled")] public bool AutoCapitalizeEnabled { get; set; } = true;
    [JsonPropertyName("learning_enabled")] public bool LearningEnabled { get; set; } = true;
    [JsonPropertyName("undo_letter")] public string UndoLetter { get; set; } = "U";
    [JsonPropertyName("toast_seconds")] public int ToastSeconds { get; set; } = 3;
    [JsonPropertyName("toast_corner")] public string ToastCorner { get; set; } = "bottom-right";
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

    /// <summary>Default path: config.json in the app's data folder (see AppPaths).</summary>
    public static string DefaultPath() => Path.Combine(AppPaths.DataFolder(), ConfigFileName);

    public bool FirstRunDone => _config.FirstRunDone;

    /// <summary>The correction level the user chose. Read by the worker thread, changed from the UI.</summary>
    public CorrectionLevel Level
    {
        get => CorrectionLevels.Parse(_config.CorrectionLevel);
        set => _config.CorrectionLevel = value.ToString().ToLowerInvariant();
    }

    /// <summary>The least confidence a correction needs to be applied, from the chosen level.</summary>
    public double Threshold => CorrectionLevels.ThresholdFor(Level);

    /// <summary>"dark" or "light".</summary>
    public string Theme
    {
        get => _config.Theme is "light" ? "light" : "dark";
        set => _config.Theme = value is "light" ? "light" : "dark";
    }

    public IReadOnlyList<AppPermission> Apps => _config.Apps;

    /// <summary>Raised by Commit after the settings are saved. Raised on the thread that called Commit (the UI).</summary>
    public event Action? Changed;

    /// <summary>Turns all corrections on or off.</summary>
    public bool MasterEnabled { get => _config.MasterEnabled; set => _config.MasterEnabled = value; }

    public bool AbbreviationsEnabled { get => _config.AbbreviationsEnabled; set => _config.AbbreviationsEnabled = value; }

    public bool AutoCapitalizeEnabled { get => _config.AutoCapitalizeEnabled; set => _config.AutoCapitalizeEnabled = value; }

    /// <summary>When off, unknown words are not counted and undos are not remembered by the engine.</summary>
    public bool LearningEnabled { get => _config.LearningEnabled; set => _config.LearningEnabled = value; }

    /// <summary>The letter pressed with Alt to undo. Only A to Z are accepted; anything else keeps U.</summary>
    public char UndoLetter
    {
        get => _config.UndoLetter.Length == 1 ? _config.UndoLetter[0] : 'U';
        set
        {
            char upper = char.ToUpperInvariant(value);
            if (char.IsAsciiLetter(upper)) _config.UndoLetter = upper.ToString();
        }
    }

    /// <summary>How long the toast stays, in seconds (1 to 5).</summary>
    public int ToastSeconds
    {
        get => Math.Clamp(_config.ToastSeconds, 1, 5);
        set => _config.ToastSeconds = Math.Clamp(value, 1, 5);
    }

    /// <summary>"top-left", "top-right", "bottom-left", or "bottom-right".</summary>
    public string ToastCorner
    {
        get => IsCorner(_config.ToastCorner) ? _config.ToastCorner : "bottom-right";
        set => _config.ToastCorner = IsCorner(value) ? value : "bottom-right";
    }

    public static bool IsCorner(string? value) =>
        value is "top-left" or "top-right" or "bottom-left" or "bottom-right";

    /// <summary>Saves the settings and tells the rest of the app they changed.</summary>
    public void Commit()
    {
        Save();
        Changed?.Invoke();
    }

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
            // Keep hand-edited values within the allowed ranges.
            _config.ToastSeconds = Math.Clamp(_config.ToastSeconds, 1, 5);
            if (!IsCorner(_config.ToastCorner)) _config.ToastCorner = "bottom-right";
            if (_config.UndoLetter.Length != 1 || !char.IsAsciiLetter(_config.UndoLetter[0])) _config.UndoLetter = "U";
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
