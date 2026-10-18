using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace Typryt;

/// <summary>
/// Starts the correction engine when the app starts, and stops it when the app exits. An
/// installed app runs the packaged engine; a copy running from the project runs the engine
/// from its venv. If an engine is already answering on the port, it is used and left running.
/// </summary>
public sealed class EngineLauncher : IDisposable
{
    private const string Address = "http://127.0.0.1:8000/";
    private const int StartupTimeoutSeconds = 20;

    private Process? _owned;

    /// <summary>Starts the engine unless one is already answering. Returns true when an engine is available.</summary>
    public bool StartIfNeeded()
    {
        if (IsRunning()) return true;

        var startInfo = StartInfo();
        if (startInfo is null) return false;

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

    /// <summary>How to start the engine: the packaged exe when installed, otherwise the project's venv.</summary>
    private static ProcessStartInfo? StartInfo()
    {
        var packaged = AppPaths.PackagedEngineExe();
        if (packaged is not null)
        {
            return new ProcessStartInfo
            {
                FileName = packaged,
                WorkingDirectory = Path.GetDirectoryName(packaged)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
        }

        var engineFolder = AppPaths.RepositoryEngineFolder();
        if (engineFolder is null) return null;
        var python = Path.Combine(engineFolder, "venv", "Scripts", "python.exe");
        if (!File.Exists(python)) return null;

        return new ProcessStartInfo
        {
            FileName = python,
            Arguments = "-m uvicorn main:app --host 127.0.0.1 --port 8000",
            WorkingDirectory = engineFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
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
