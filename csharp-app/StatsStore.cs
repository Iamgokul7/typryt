using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typryt;

/// <summary>One correction shown in the history. Times are local.</summary>
public sealed class HistoryEntry
{
    [JsonPropertyName("original")] public string Original { get; set; } = "";
    [JsonPropertyName("corrected")] public string Corrected { get; set; } = "";
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("undone")] public bool Undone { get; set; }
}

/// <summary>
/// Counts corrections and undos, and keeps the last 50 corrections. Saved to shared\stats.json.
/// This file holds the words the user typed, so it is git-ignored and never leaves the PC.
/// </summary>
public sealed class StatsStore
{
    public const int HistoryLimit = 50;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    private StatsData _data = new();

    private sealed class StatsData
    {
        [JsonPropertyName("total_fixed")] public int TotalFixed { get; set; }
        [JsonPropertyName("total_undone")] public int TotalUndone { get; set; }
        [JsonPropertyName("fixed_today")] public int FixedToday { get; set; }
        [JsonPropertyName("today")] public string Today { get; set; } = "";
        [JsonPropertyName("history")] public List<HistoryEntry> History { get; set; } = new();
    }

    /// <summary>Raised after any change. Raised on the thread that made the change.</summary>
    public event Action? Changed;

    public StatsStore(string path)
    {
        _path = path;
        Load();
    }

    public static string DefaultPath() => Path.Combine(AppPaths.DataFolder(), "stats.json");

    public int TotalFixed { get { lock (_lock) return _data.TotalFixed; } }

    public int TotalUndone { get { lock (_lock) return _data.TotalUndone; } }

    public int FixedToday { get { lock (_lock) return TodayCountLocked(); } }

    /// <summary>The most recent corrections first, at most HistoryLimit.</summary>
    public IReadOnlyList<HistoryEntry> History()
    {
        lock (_lock) return _data.History.Select(Copy).ToList();
    }

    public void RecordCorrection(string original, string corrected)
    {
        lock (_lock)
        {
            RollDayLocked();
            _data.TotalFixed++;
            _data.FixedToday++;
            _data.History.Insert(0, new HistoryEntry { Original = original, Corrected = corrected, At = DateTime.Now });
            if (_data.History.Count > HistoryLimit)
            {
                _data.History.RemoveRange(HistoryLimit, _data.History.Count - HistoryLimit);
            }
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Marks the newest matching correction as undone and counts the undo.</summary>
    public void RecordUndo(string original, string corrected)
    {
        lock (_lock)
        {
            _data.TotalUndone++;
            var entry = _data.History.FirstOrDefault(e => !e.Undone
                && string.Equals(e.Original, original, StringComparison.Ordinal)
                && string.Equals(e.Corrected, corrected, StringComparison.Ordinal));
            if (entry is not null) entry.Undone = true;
            Save();
        }
        Changed?.Invoke();
    }

    private int TodayCountLocked()
    {
        return _data.Today == Stamp(DateTime.Now) ? _data.FixedToday : 0;
    }

    private void RollDayLocked()
    {
        var today = Stamp(DateTime.Now);
        if (_data.Today != today)
        {
            _data.Today = today;
            _data.FixedToday = 0;
        }
    }

    private static string Stamp(DateTime when) => when.ToString("yyyy-MM-dd");

    private static HistoryEntry Copy(HistoryEntry e) => new()
    {
        Original = e.Original,
        Corrected = e.Corrected,
        At = e.At,
        Undone = e.Undone,
    };

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            _data = JsonSerializer.Deserialize<StatsData>(File.ReadAllText(_path)) ?? new StatsData();
            _data.History ??= new List<HistoryEntry>();
        }
        catch (JsonException)
        {
            // A damaged file is kept aside, and counting starts again.
            File.Move(_path, _path + ".corrupt", overwrite: true);
            _data = new StatsData();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_data, _options));
        File.Move(temporary, _path, overwrite: true);
    }
}
