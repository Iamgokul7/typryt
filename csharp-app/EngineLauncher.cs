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
        if (_owned is not null) TieToApp(_owned);

        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning()) return true;
            if (_owned is null || _owned.HasExited) return false;
            Thread.Sleep(250);
        }
        return false;
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
