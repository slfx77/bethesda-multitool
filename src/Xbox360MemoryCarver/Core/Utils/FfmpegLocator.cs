namespace Xbox360MemoryCarver.Core.Utils;

/// <summary>
///     Utility for locating FFmpeg executable on the system.
///     Searches PATH, common installation directories, and standard locations.
/// </summary>
public static class FfmpegLocator
{
    private const string FfmpegExeName = "ffmpeg.exe";
    private const string FfmpegName = "ffmpeg";

    private static readonly Lazy<string?> CachedPath = new(FindFfmpegInternal,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     Gets the path to FFmpeg if found, or null if not available.
    ///     Result is cached after first lookup.
    /// </summary>
    public static string? FfmpegPath => CachedPath.Value;

    /// <summary>
    ///     Returns true if FFmpeg is available on this system.
    /// </summary>
    public static bool IsAvailable => FfmpegPath != null;

    private static string? FindFfmpegInternal()
    {
        // Check PATH environment variable first
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                // Windows: ffmpeg.exe
                var ffmpegPath = Path.Combine(dir, FfmpegExeName);
                if (File.Exists(ffmpegPath))
                {
                    return ffmpegPath;
                }

                // Unix: ffmpeg (no extension)
                ffmpegPath = Path.Combine(dir, FfmpegName);
                if (File.Exists(ffmpegPath))
                {
                    return ffmpegPath;
                }
            }
            catch
            {
                // Invalid path entries in PATH - skip
            }
        }

        // Check common installation directories on Windows
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

        var commonPaths = new[]
        {
            // C:\ffmpeg\bin\ffmpeg.exe (common manual install)
            Path.Combine(systemDrive, FfmpegName, "bin", FfmpegExeName),
            // C:\Program Files\ffmpeg\bin\ffmpeg.exe
            Path.Combine(programFiles, FfmpegName, "bin", FfmpegExeName),
            // C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe
            Path.Combine(programFilesX86, FfmpegName, "bin", FfmpegExeName),
            // %LocalAppData%\ffmpeg\bin\ffmpeg.exe (scoop, etc.)
            Path.Combine(localAppData, FfmpegName, "bin", FfmpegExeName),
            // Chocolatey default location
            Path.Combine(systemDrive, "ProgramData", "chocolatey", "bin", FfmpegExeName),
            // Scoop default location
            Path.Combine(localAppData, "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg*", FfmpegExeName)
        };

        foreach (var path in commonPaths)
        {
            try
            {
                // Handle wildcard for WinGet packages
                if (path.Contains('*'))
                {
                    var dir = Path.GetDirectoryName(path);
                    var pattern = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
                    var parentDir = Path.GetDirectoryName(dir);

                    if (parentDir != null && Directory.Exists(parentDir))
                    {
                        var matchingDirs = Directory.GetDirectories(parentDir, pattern);
                        foreach (var matchDir in matchingDirs)
                        {
                            var ffmpegPath = Path.Combine(matchDir, FfmpegExeName);
                            if (File.Exists(ffmpegPath))
                            {
                                return ffmpegPath;
                            }
                        }
                    }
                }
                else if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // Skip inaccessible paths
            }
        }

        return null;
    }

    /// <summary>
    ///     Forces a re-scan for FFmpeg (useful if user installed it after startup).
    /// </summary>
    public static string? Rescan()
    {
        // Can't reset Lazy<T>, but we can just call the method directly
        return FindFfmpegInternal();
    }
}
