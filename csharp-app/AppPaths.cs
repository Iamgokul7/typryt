using System.IO;

namespace Typryt;

/// <summary>
/// Where the app finds its files. Running from the project keeps the project's shared folder;
/// an installed copy keeps its data in %APPDATA%\Typryt. The engine follows the same rule.
/// </summary>
public static class AppPaths
{
    /// <summary>The project root, when the app runs from the repository; null when installed.</summary>
    public static string? RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, "shared")) &&
                Directory.Exists(Path.Combine(folder.FullName, "csharp-app")))
            {
                return folder.FullName;
            }
            folder = folder.Parent;
        }
        return null;
    }

    /// <summary>The folder for config.json, stats.json, and the learned words. Created if missing.</summary>
    public static string DataFolder()
    {
        var root = RepositoryRoot();
        var folder = root is not null
            ? Path.Combine(root, "shared")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Typryt");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The packaged engine, when the app is installed. Null when running from the project.</summary>
    public static string? PackagedEngineExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "engine", "typryt-engine.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>The python-engine folder of the repository, when running from the project.</summary>
    public static string? RepositoryEngineFolder()
    {
        var root = RepositoryRoot();
        return root is null ? null : Path.Combine(root, "python-engine");
    }
}
