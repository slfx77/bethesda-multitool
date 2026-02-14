using System.Diagnostics;

namespace FalloutXbox360Utils;

/// <summary>
///     Checks for external dependencies required by various tabs.
/// </summary>
#pragma warning disable S1075 // URIs should not be hardcoded - these are intentional download URLs
public static class DependencyChecker
{
    /// <summary>
    ///     FFmpeg download URL.
    /// </summary>
    public const string FfmpegUrl = "https://www.ffmpeg.org/download.html";

    // Cache dependency status to avoid repeated checks
    private static DependencyStatus? _ffmpegStatus;

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
    ///     Checks if FFmpeg is available in PATH or common locations.
    ///     Required for XMA -> WAV audio conversion.
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
    ///     Checks all dependencies required by the Single File / Batch Mode tabs.
    /// </summary>
    public static TabDependencyResult CheckCarverDependencies()
    {
        return new TabDependencyResult
        {
            TabName = "Memory Carver",
            Dependencies =
            [
                CheckFfmpeg()
            ]
        };
    }

    /// <summary>
    ///     Checks all dependencies required by the DDX Converter tab.
    ///     DDXConv is now compiled-in, so no external dependencies are needed.
    /// </summary>
    public static TabDependencyResult CheckDdxConverterDependencies()
    {
        return new TabDependencyResult
        {
            TabName = "DDX Converter",
            Dependencies = [] // DDXConv is compiled-in, no external dependencies
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
        _ffmpegStatus = null;
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

    private static (bool isAvailable, string? version, string? path) FindFfmpeg()
    {
        // Check PATH first
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var exeNames = OperatingSystem.IsWindows()
            ? new[] { "ffmpeg.exe" }
            : new[] { "ffmpeg" };

        foreach (var dir in pathDirs)
        {
            foreach (var exeName in exeNames)
            {
                var ffmpegPath = Path.Combine(dir, exeName);
                if (File.Exists(ffmpegPath))
                {
                    var version = GetFfmpegVersion(ffmpegPath);
                    return (true, version, ffmpegPath);
                }
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

    #endregion
}
