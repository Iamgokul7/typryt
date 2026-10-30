using System.IO;
using System.Threading;

namespace Typryt;

/// <summary>
/// A plain daily log in %APPDATA%\Typryt\logs (or the project's shared\logs when running from the
/// project). Each line has a time, a category, and a message. Files older than 7 days are removed.
/// The log holds only counts, timings, lengths and errors. It never holds text the user typed.
/// </summary>
public static class Log
{
    private const int KeepDays = 7;
    private static readonly object Gate = new();
    private static string? _folder;

    public static string Folder
    {
        get
        {
            _folder ??= Path.Combine(AppPaths.DataFolder(), "logs");
            return _folder;
        }
    }

    public static void Write(string category, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, $"typryt-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // A log that cannot be written must never stop typing.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>How many unexpected errors this run has logged. Counted even when the log file cannot be written.</summary>
    public static int ErrorCount => _errorCount;

    private static int _errorCount;

    /// <summary>
    /// Logs an unexpected error with its place, type and message, and counts it. The app keeps running;
    /// the text the failed correction was for is left as it was.
    /// </summary>
    public static void Error(string place, Exception ex)
    {
        int count = Interlocked.Increment(ref _errorCount);
        Write("error", $"#{count} {place}: {ex.GetType().FullName}: {ex.Message}");
    }

    /// <summary>
    /// Older builds wrote the typed words into the log. The first time this build starts, those log files are
    /// deleted, and a marker file stops it from running again. Nothing else in the folder is touched.
    /// </summary>
    public static void PurgeWordLogsOnce()
    {
        try
        {
            string marker = Path.Combine(Folder, ".no-words-in-logs");
            if (File.Exists(marker)) return;
            if (Directory.Exists(Folder))
            {
                foreach (var file in Directory.GetFiles(Folder, "typryt-*.log")) File.Delete(file);
            }
            Directory.CreateDirectory(Folder);
            File.WriteAllText(marker, "Logs written before this marker existed held typed words and were deleted.");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes log files older than KeepDays. Called once at startup.</summary>
    public static void PruneOld()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.GetFiles(Folder, "typryt-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
    }
}
