using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Extraction;

/// <summary>
///     Discovers build sources (ESM files and memory dumps) and extracts their metadata.
/// </summary>
public static class BuildDiscovery
{
    /// <summary>
    ///     Scans the builds and dumps directories to discover all available data sources.
    ///     Returns an ordered list of BuildInfo objects sorted by build date.
    /// </summary>
    public static List<BuildInfo> DiscoverBuilds(string? buildsDir, string? dumpsDir)
    {
        var builds = new List<BuildInfo>();

        if (buildsDir != null && Directory.Exists(buildsDir))
        {
            builds.AddRange(DiscoverEsmBuilds(buildsDir));
        }

        if (dumpsDir != null && Directory.Exists(dumpsDir))
        {
            builds.AddRange(DiscoverDmpBuilds(dumpsDir));
        }

        // Sort by build date (nulls last)
        return builds
            .OrderBy(b => b.BuildDate ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    /// <summary>
    ///     Discovers ESM builds by scanning subdirectories for FalloutNV.esm files
    ///     and reading PE timestamps from companion .exe files.
    /// </summary>
    private static List<BuildInfo> DiscoverEsmBuilds(string buildsDir)
    {
        var builds = new List<BuildInfo>();

        foreach (var subDir in Directory.GetDirectories(buildsDir))
        {
            var dirName = Path.GetFileName(subDir);

            // Find FalloutNV.esm anywhere under this build directory
            var esmFiles = Directory.GetFiles(subDir, "FalloutNV.esm", SearchOption.AllDirectories);
            if (esmFiles.Length == 0)
            {
                continue;
            }

            var esmPath = esmFiles[0];

            // Find a .exe to read the PE timestamp from
            var buildDate = FindBuildDate(subDir);
            var peTimestamp = FindPeTimestamp(subDir);

            builds.Add(new BuildInfo
            {
                Label = dirName,
                SourcePath = esmPath,
                SourceType = BuildSourceType.Esm,
                BuildDate = buildDate,
                BuildType = dirName.Contains("Final", StringComparison.OrdinalIgnoreCase) ? "Final" : "Development",
                PeTimestamp = peTimestamp
            });
        }

        return builds;
    }

    /// <summary>
    ///     Discovers memory dump files and extracts build metadata from minidump modules.
    /// </summary>
    private static List<BuildInfo> DiscoverDmpBuilds(string dumpsDir)
    {
        var builds = new List<BuildInfo>();

        var dmpFiles = Directory.GetFiles(dumpsDir, "*.dmp", SearchOption.TopDirectoryOnly);

        foreach (var dmpPath in dmpFiles)
        {
            var fileName = Path.GetFileName(dmpPath);

            try
            {
                var info = MinidumpParser.Parse(dmpPath);
                var gameModule = MinidumpAnalyzer.FindGameModule(info);
                var buildType = MinidumpAnalyzer.DetectBuildType(info);

                DateTimeOffset? buildDate = null;
                uint? peTimestamp = null;

                if (gameModule != null)
                {
                    peTimestamp = gameModule.TimeDateStamp;
                    if (peTimestamp > 0)
                    {
                        buildDate = DateTimeOffset.FromUnixTimeSeconds(peTimestamp.Value);
                    }
                }

                builds.Add(new BuildInfo
                {
                    Label = fileName,
                    SourcePath = dmpPath,
                    SourceType = BuildSourceType.Dmp,
                    BuildDate = buildDate,
                    BuildType = buildType,
                    PeTimestamp = peTimestamp
                });
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn($"[BuildDiscovery] Failed to parse DMP: {fileName}: {ex.Message}");
            }
        }

        return builds;
    }

    /// <summary>
    ///     Finds the build date by reading PE timestamps from .exe files in the build directory.
    /// </summary>
    private static DateTimeOffset? FindBuildDate(string buildDir)
    {
        var peTimestamp = FindPeTimestamp(buildDir);
        if (peTimestamp == null || peTimestamp == 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(peTimestamp.Value);
    }

    /// <summary>
    ///     Finds the raw PE timestamp from .exe files in the build directory.
    ///     Prefers Fallout.exe, falls back to any .exe found.
    /// </summary>
    private static uint? FindPeTimestamp(string buildDir)
    {
        // Look for Fallout.exe first (the main game executable)
        var exeFiles = Directory.GetFiles(buildDir, "Fallout.exe", SearchOption.AllDirectories);
        if (exeFiles.Length > 0)
        {
            var timestamp = PeTimestampReader.ReadTimestamp(exeFiles[0]);
            if (timestamp != null)
            {
                return timestamp;
            }
        }

        // Fall back to any .exe file
        exeFiles = Directory.GetFiles(buildDir, "*.exe", SearchOption.AllDirectories);
        foreach (var exePath in exeFiles)
        {
            var timestamp = PeTimestampReader.ReadTimestamp(exePath);
            if (timestamp != null)
            {
                return timestamp;
            }
        }

        return null;
    }
}
