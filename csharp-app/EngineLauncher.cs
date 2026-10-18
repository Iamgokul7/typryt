using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace Typryt;

/// <summary>
/// Starts the Python engine when the app starts, and stops it when the app exits. If an
/// engine is already answering on the port, it is used and left running when the app exits.
/// Packaging the engine so no venv is needed is Phase 12.
/// </summary>
public sealed class EngineLauncher : IDisposable
{
    private const string Address = "http://127.0.0.1:8000/";
    private const int StartupTimeoutSeconds = 20;

    private readonly string _engineFolder;
    private Process? _owned;

    public EngineLauncher(string engineFolder)
    {
        _engineFolder = engineFolder;
    }

    /// <summary>Finds the python-engine folder of this repository, by searching up from the app's folder.</summary>
    public static string? FindEngineFolder()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null)
        {
            var candidate = Path.Combine(folder.FullName, "python-engine");
            if (Directory.Exists(candidate)) return candidate;
            folder = folder.Parent;
        }
        return null;
    }

    /// <summary>Starts the engine unless one is already answering. Returns true when an engine is available.</summary>
    public bool StartIfNeeded()
    {
        if (IsRunning()) return true;

        var python = Path.Combine(_engineFolder, "venv", "Scripts", "python.exe");
        if (!File.Exists(python)) return false;

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            Arguments = "-m uvicorn main:app --host 127.0.0.1 --port 8000",
            WorkingDirectory = _engineFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        _owned = Process.Start(startInfo);

        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning()) return true;
            if (_owned is null || _owned.HasExited) return false;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>True when the engine answers its health check.</summary>
    public static bool IsRunning()
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Address), Timeout = TimeSpan.FromMilliseconds(500) };
            using var response = client.GetAsync("health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Stops the engine, but only if this app started it.</summary>
    public void Dispose()
    {
        if (_owned is null) return;
        try
        {
            if (!_owned.HasExited) _owned.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            _owned.Dispose();
            _owned = null;
        }
    }
}
