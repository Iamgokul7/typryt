using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Typryt;

/// <summary>
/// Starts the correction engine when the app starts, and stops it when the app exits. An
/// installed app runs the packaged engine; a copy running from the project runs the engine
/// from its venv. If an engine is already answering on the port, it is used and left running.
/// </summary>
public sealed class EngineLauncher : IDisposable
{
    private const int StartupTimeoutSeconds = 20;

    private Process? _owned;

    /// <summary>Starts the engine unless one is already answering. Returns true when an engine is available.</summary>
    public bool StartIfNeeded()
    {
        if (IsRunning())
        {
            EngineState.MarkUp();
            return true;
        }

        // A port that was free a moment ago can be taken before the engine binds it. The engine then exits at once,
        // and the start is tried again on another port.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            EngineIdentity.PrepareForStart();
            var startInfo = StartInfo();
            if (startInfo is null) return false;

            _owned = Process.Start(startInfo);
            if (_owned is not null) TieToApp(_owned);

            var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (IsRunning())
                {
                    EngineState.MarkUp();
                    return true;
                }
                if (_owned is null || _owned.HasExited) break;
                Thread.Sleep(250);
            }
            if (_owned is not null && !_owned.HasExited) return false;
            Log.Write("engine", $"the engine exited while starting (attempt {attempt + 1} of 3)");
            _owned?.Dispose();
            _owned = null;
        }
        return false;
    }

    // Watchdog settings: a health check every 2 s, a restart after 2 failed checks in a row, a wait of 1, 2 and
    // 4 s before the 1st, 2nd and 3rd restart, and at most 3 restarts in 5 minutes.
    private const int CheckIntervalMilliseconds = 2000;
    private const int FailuresBeforeRestart = 2;
    // Test only: TYPRYT_TEST_ENGINE_RESTARTS raises the limit, so a test can kill the engine many times in a row.
    private static readonly int MaxRestarts =
        InputMarks.TestInput && int.TryParse(Environment.GetEnvironmentVariable("TYPRYT_TEST_ENGINE_RESTARTS"), out int testLimit) ? testLimit : 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _watchdogStop = new();
    private readonly List<DateTime> _restarts = new();

    /// <summary>
    /// Watches the engine and starts it again when it stops answering. After MaxRestarts in RestartWindow it
    /// stops restarting and calls onGaveUp once; the app then keeps running without corrections.
    /// </summary>
    public void StartWatchdog(Action onGaveUp)
    {
        Task.Run(() => WatchAsync(onGaveUp, _watchdogStop.Token));
    }

    private async Task WatchAsync(Action onGaveUp, CancellationToken ct)
    {
        int failures = 0;
        bool gaveUp = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(CheckIntervalMilliseconds, ct);
                if (IsRunning())
                {
                    failures = 0;
                    EngineState.MarkUp();
                    continue;
                }

                failures++;
                EngineState.MarkDown();
                if (failures < FailuresBeforeRestart || gaveUp) continue;

                _restarts.RemoveAll(t => DateTime.UtcNow - t > RestartWindow);
                if (_restarts.Count >= MaxRestarts)
                {
                    gaveUp = true;
                    Log.Write("engine", $"watchdog: {MaxRestarts} restarts in {RestartWindow.TotalMinutes:0} minutes, not restarting again");
                    onGaveUp();
                    continue;
                }

                int waitSeconds = Math.Min(1 << Math.Min(_restarts.Count, 4), 4);
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
                _restarts.Add(DateTime.UtcNow);
                Log.Write("engine", $"watchdog: engine not answering, restart {_restarts.Count} of {MaxRestarts}");
                StopOwned();
                bool running = StartIfNeeded();
                Log.Write("engine", running ? "watchdog: engine is running again" : "watchdog: engine did not start");
                failures = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("engine watchdog", ex);
        }
    }

    private void StopOwned()
    {
        var owned = _owned;
        _owned = null;
        if (owned is null) return;
        try
        {
            if (!owned.HasExited) owned.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            owned.Dispose();
        }
    }

    /// <summary>
    /// Puts the engine in a Windows job that ends it when this app's process ends, including when the app crashes.
    /// The job handle is kept for the life of the app; Windows closes it when the process exits.
    /// </summary>
    private static void TieToApp(Process engine)
    {
        try
        {
            if (_job == IntPtr.Zero) _job = CreateKillOnCloseJob();
            if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, engine.Handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            // Without the job the engine still stops on a normal exit (Dispose), so this is only logged.
            Log.Error("engine job", ex);
        }
    }

    private static IntPtr _job = IntPtr.Zero;

    private static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    /// <summary>How to start the engine: the packaged exe when installed, otherwise the project's venv.</summary>
    private static ProcessStartInfo? StartInfo()
    {
        var packaged = AppPaths.PackagedEngineExe();
        if (packaged is not null)
        {
            return WithIdentity(new ProcessStartInfo
            {
                FileName = packaged,
                WorkingDirectory = Path.GetDirectoryName(packaged)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }

        var engineFolder = AppPaths.RepositoryEngineFolder();
        if (engineFolder is null) return null;
        var python = Path.Combine(engineFolder, "venv", "Scripts", "python.exe");
        if (!File.Exists(python)) return null;

        return WithIdentity(new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"-m uvicorn main:app --host 127.0.0.1 --port {EngineIdentity.Port}",
            WorkingDirectory = engineFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>Gives the engine its port and token through its environment (the command line of a process can be read by others).</summary>
    private static ProcessStartInfo WithIdentity(ProcessStartInfo info)
    {
        info.Environment["TYPRYT_ENGINE_PORT"] = EngineIdentity.Port.ToString();
        info.Environment["TYPRYT_ENGINE_TOKEN"] = EngineIdentity.Token;
        return info;
    }

    /// <summary>
    /// True when the engine of this launch answers its health check. A health check from the engine carries a proof that
    /// it knows the token (a keyed hash of a fresh nonce); an answer without it, from some other program on the port, does not count.
    /// </summary>
    // One client for every health check (the watchdog asks every 2 seconds): a new client each time left connections and handlers behind.
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromMilliseconds(500) };

    public static bool IsRunning()
    {
        try
        {
            string nonce = Guid.NewGuid().ToString("N");
            var client = HealthClient;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(EngineIdentity.BaseAddress, "health"));
            request.Headers.Add(EngineIdentity.TokenHeader, EngineIdentity.Token);
            request.Headers.Add(EngineIdentity.NonceHeader, nonce);
            using var response = client.Send(request);
            if (!response.IsSuccessStatusCode) return false;
            using var reader = new StreamReader(response.Content.ReadAsStream());
            using var doc = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            return doc.RootElement.TryGetProperty("proof", out var proof)
                && string.Equals(proof.GetString(), EngineIdentity.ExpectedProof(nonce), StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
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
        _watchdogStop.Cancel();
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
