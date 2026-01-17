using System.Diagnostics;
using Microsoft.Win32;

namespace Xbox360MemoryCarver;

/// <summary>
///     Result of a dependency check with details about availability.
/// </summary>
public sealed record DependencyStatus
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsAvailable { get; init; }
    public string? Version { get; init; }
    public string? Path { get; init; }
    public string? DownloadUrl { get; init; }
    public string? InstallInstructions { get; init; }
}

/// <summary>
///     Aggregated dependency check result for a tab.
/// </summary>
public sealed class TabDependencyResult
{
    public required string TabName { get; init; }
    public required List<DependencyStatus> Dependencies { get; init; }

    public bool AllAvailable => Dependencies.All(d => d.IsAvailable);
    public bool AnyAvailable => Dependencies.Any(d => d.IsAvailable);
    public IEnumerable<DependencyStatus> Missing => Dependencies.Where(d => !d.IsAvailable);
    public IEnumerable<DependencyStatus> Available => Dependencies.Where(d => d.IsAvailable);
}

/// <summary>
///     Checks for external dependencies required by various tabs.
/// </summary>
public static class DependencyChecker
{
    /// <summary>
    ///     Microsoft XNA Framework 4.0 download URL.
    /// </summary>
    public const string XnaFrameworkUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=20914";

    /// <summary>
    ///     FFmpeg download URL.
    /// </summary>
    public const string FfmpegUrl = "https://www.ffmpeg.org/download.html";

    // Cache dependency status to avoid repeated checks
    private static DependencyStatus? _xnaStatus;
    private static DependencyStatus? _ffmpegStatus;
    private static DependencyStatus? _ddxConvStatus;
    private static DependencyStatus? _xuiHelperStatus;

    // Track which dependency sets have been shown to user

    /// <summary>
    ///     Returns true if the carver dependencies dialog has already been shown this session.
    /// </summary>
    public static bool CarverDependenciesShown { get; set; }

    /// <summary>
    ///     Returns true if the DDX converter dependencies dialog has already been shown this session.
    /// </summary>
    public static bool DdxConverterDependenciesShown { get; set; }

    /// <summary>
    ///     Checks if Microsoft XNA Framework 4.0 (XnaNative.dll) is available.
    ///     Required for DDX → DDS texture conversion.
    /// </summary>
    public static DependencyStatus CheckXnaNative(bool forceRecheck = false)
    {
        if (_xnaStatus != null && !forceRecheck) return _xnaStatus;

        var (isAvailable, version, path) = FindXnaNative();

        _xnaStatus = new DependencyStatus
        {
            Name = "Microsoft XNA Framework 4.0",
            Description = "Required for DDX texture decompression (LZX algorithm)",
            IsAvailable = isAvailable,
            Version = version,
            Path = path,
            DownloadUrl = XnaFrameworkUrl,
            InstallInstructions = "Install the Microsoft XNA Framework Redistributable 4.0 from the download link. " +
                                  "After installation, restart this application."
        };

        return _xnaStatus;
    }

    /// <summary>
    ///     Checks if FFmpeg is available in PATH or common locations.
    ///     Required for XMA → WAV audio conversion.
    /// </summary>
    public static DependencyStatus CheckFfmpeg(bool forceRecheck = false)
    {
        if (_ffmpegStatus != null && !forceRecheck) return _ffmpegStatus;

        var (isAvailable, version, path) = FindFfmpeg();

        _ffmpegStatus = new DependencyStatus
        {
            Name = "FFmpeg",
            Description = "Required for XMA audio to WAV conversion",
            IsAvailable = isAvailable,
            Version = version,
            Path = path,
            DownloadUrl = FfmpegUrl,
            InstallInstructions = "Download FFmpeg from ffmpeg.org, extract it, and either:\n" +
                                  "• Add the 'bin' folder to your system PATH, or\n" +
                                  "• Place ffmpeg.exe in C:\\ffmpeg\\bin\\ or a similar location"
        };

        return _ffmpegStatus;
    }

    /// <summary>
    ///     Checks if DDXConv executable is available.
    ///     This is a built-in tool but requires XnaNative.dll to function.
    /// </summary>
    public static DependencyStatus CheckDdxConv(bool forceRecheck = false)
    {
        if (_ddxConvStatus != null && !forceRecheck) return _ddxConvStatus;

        var (isAvailable, path) = FindDdxConv();

        _ddxConvStatus = new DependencyStatus
        {
            Name = "DDXConv",
            Description = "Built-in DDX to DDS converter",
            IsAvailable = isAvailable,
            Path = path,
            DownloadUrl = null,
            InstallInstructions = "DDXConv should be included with this application. " +
                                  "Try rebuilding the solution or check that all projects compiled successfully."
        };

        return _ddxConvStatus;
    }

    /// <summary>
    ///     Checks if XUIHelper.CLI is available.
    ///     This is a built-in tool for XUR → XUI conversion.
    /// </summary>
    public static DependencyStatus CheckXuiHelper(bool forceRecheck = false)
    {
        if (_xuiHelperStatus != null && !forceRecheck) return _xuiHelperStatus;

        var (isAvailable, path) = FindXuiHelper();

        _xuiHelperStatus = new DependencyStatus
        {
            Name = "XUIHelper",
            Description = "Built-in XUR to XUI converter",
            IsAvailable = isAvailable,
            Path = path,
            DownloadUrl = null,
            InstallInstructions = "XUIHelper should be included with this application. " +
                                  "Try rebuilding the solution or check that all projects compiled successfully."
        };

        return _xuiHelperStatus;
    }

    /// <summary>
    ///     Checks all dependencies required by the Single File / Batch Mode tabs.
    /// </summary>
    public static TabDependencyResult CheckCarverDependencies()
    {
        return new TabDependencyResult
        {
            TabName = "Memory Carver",
            Dependencies =
            [
                CheckXnaNative(),
                CheckFfmpeg(),
                CheckXuiHelper()
            ]
        };
    }

    /// <summary>
    ///     Checks all dependencies required by the DDX Converter tab.
    /// </summary>
    public static TabDependencyResult CheckDdxConverterDependencies()
    {
        return new TabDependencyResult
        {
            TabName = "DDX Converter",
            Dependencies =
            [
                CheckDdxConv(),
                CheckXnaNative()
            ]
        };
    }

    /// <summary>
    ///     Checks all dependencies required by the NIF Converter tab.
    ///     NIF conversion is fully self-contained with no external dependencies.
    /// </summary>
    public static TabDependencyResult CheckNifConverterDependencies()
    {
        return new TabDependencyResult
        {
            TabName = "NIF Converter",
            Dependencies = [] // No external dependencies
        };
    }

    /// <summary>
    ///     Resets the cached dependency status, forcing a fresh check next time.
    /// </summary>
    public static void ResetCache()
    {
        _xnaStatus = null;
        _ffmpegStatus = null;
        _ddxConvStatus = null;
        _xuiHelperStatus = null;
    }

    /// <summary>
    ///     Resets all state including "shown" flags, for testing purposes.
    /// </summary>
    public static void ResetAll()
    {
        ResetCache();
        CarverDependenciesShown = false;
        DdxConverterDependenciesShown = false;
    }

    #region Private Detection Methods

    private static (bool isAvailable, string? version, string? path) FindXnaNative()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            var versions = new[] { "v4.0", "v3.1", "v3.0" };

            foreach (var version in versions)
            {
                var subKeyName = @"SOFTWARE\Microsoft\XNA\Framework\" + version;
                using var subKey = baseKey.OpenSubKey(subKeyName);

                if (subKey == null) continue;

                var nativeLibPath = subKey.GetValue("NativeLibraryPath", null) as string;
                if (string.IsNullOrEmpty(nativeLibPath)) continue;

                var dllPath = Path.GetFullPath(Path.Combine(nativeLibPath, "XnaNative.dll"));
                if (File.Exists(dllPath)) return (true, version, dllPath);
            }
        }
        catch
        {
            // Registry access failed - XNA not installed
        }

        return (false, null, null);
    }

    private static (bool isAvailable, string? version, string? path) FindFfmpeg()
    {
        // Check PATH first
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var exeNames = OperatingSystem.IsWindows()
            ? new[] { "ffmpeg.exe" }
            : new[] { "ffmpeg" };

        foreach (var dir in pathDirs)
        foreach (var exeName in exeNames)
        {
            var ffmpegPath = Path.Combine(dir, exeName);
            if (File.Exists(ffmpegPath))
            {
                var version = GetFfmpegVersion(ffmpegPath);
                return (true, version, ffmpegPath);
            }
        }

        // Check common installation locations on Windows
        if (OperatingSystem.IsWindows())
        {
            var commonPaths = new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "ffmpeg", "bin", "ffmpeg.exe")
            };

            var foundPath = commonPaths.FirstOrDefault(File.Exists);
            if (foundPath != null)
            {
                var version = GetFfmpegVersion(foundPath);
                return (true, version, foundPath);
            }
        }

        return (false, null, null);
    }

    private static string? GetFfmpegVersion(string ffmpegPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadLine();
            process.WaitForExit(1000);

            // Parse "ffmpeg version X.X.X ..."
            if (!string.IsNullOrEmpty(output) &&
                output.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            {
                var parts = output.Split(' ');
                if (parts.Length >= 3) return parts[2];
            }
        }
        catch
        {
            // Version check failed - still available, just unknown version
        }

        return "unknown";
    }

    private static (bool isAvailable, string? path) FindDdxConv()
    {
        const string exeName = "DDXConv.exe";
        const string folderName = "DDXConv";
        const string targetFramework = "net10.0";

        var assemblyDir = AppContext.BaseDirectory;

        // Try common locations
        var candidates = new List<string>
        {
            Path.Combine(assemblyDir, exeName),
            Path.Combine(assemblyDir, "..", folderName, exeName)
        };

        // Add workspace-relative paths
        var workspaceRoot = FindWorkspaceRoot(assemblyDir);
        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            candidates.Add(Path.Combine(workspaceRoot, "src", folderName, folderName, "bin", "Release",
                targetFramework, exeName));
            candidates.Add(Path.Combine(workspaceRoot, "src", folderName, folderName, "bin", "Debug",
                targetFramework, exeName));
        }

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath)) return (true, fullPath);
        }

        return (false, null);
    }

    private static (bool isAvailable, string? path) FindXuiHelper()
    {
        const string exeName = "XUIHelper.CLI.exe";
        const string folderName = "XUIHelper";
        const string cliProject = "XUIHelper.CLI";
        const string targetFramework = "net8.0";

        var assemblyDir = AppContext.BaseDirectory;

        // Try common locations
        var candidates = new List<string>
        {
            Path.Combine(assemblyDir, exeName),
            Path.Combine(assemblyDir, "..", folderName, exeName)
        };

        // Add workspace-relative paths
        var workspaceRoot = FindWorkspaceRoot(assemblyDir);
        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            candidates.Add(Path.Combine(workspaceRoot, "src", folderName, cliProject, "bin", "Release",
                targetFramework, exeName));
            candidates.Add(Path.Combine(workspaceRoot, "src", folderName, cliProject, "bin", "Debug",
                targetFramework, exeName));
        }

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath)) return (true, fullPath);
        }

        return (false, null);
    }

    private static string? FindWorkspaceRoot(string startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.GetFiles(dir, "*.slnx").Length > 0 || Directory.GetFiles(dir, "*.sln").Length > 0) return dir;

            var parent = Directory.GetParent(dir);
            if (parent == null) break;

            dir = parent.FullName;
        }

        return null;
    }

    #endregion
}
