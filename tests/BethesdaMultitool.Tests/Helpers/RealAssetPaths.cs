namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Locates retail game files for the opt-in real-asset suites (the ones behind
///     <c>BucketBTestGuard</c> / <c>RUN_BUCKET_B</c>).
///     Tests must not hardcode an install path: the drive letter and library folder differ per machine,
///     so a literal like <c>E:\SteamLibrary\...</c> resolves only on the machine it was written on. The
///     failure is silent — everywhere else the file simply does not exist, the test skips, and the run
///     reads as "no assets installed" rather than "this test can never run for you".
///     <para>
///         Probe order: the <c>BETHESDA_TEST_DATA_ROOT</c> override (first as a flat directory of
///         masters/archives, then as a mirrored Steam layout), then any repo-relative <c>Sample/</c>
///         candidates the caller offers, then every fixed drive's Steam library. Returns null when
///         nothing matches; callers pair that with <c>Assert.SkipWhen</c> so a missing install skips
///         rather than fails.
///     </para>
/// </summary>
internal static class RealAssetPaths
{
    /// <summary>Environment override pointing at a directory of retail assets.</summary>
    public const string RootVariable = "BETHESDA_TEST_DATA_ROOT";

    /// <summary>Standard skip message naming the override, so a skipped run says how to enable it.</summary>
    public static string SkipMessage(string what) =>
        $"{what} not found. Install the game or set {RootVariable} to a directory containing it.";

    /// <summary>
    ///     Resolve a file inside a Steam-installed game. <paramref name="gameFolder" /> is the folder
    ///     under <c>steamapps\common</c> (e.g. "Fallout New Vegas"); <paramref name="relativePath" /> is
    ///     the path beneath it (e.g. <c>Data\FalloutNV.esm</c>). Optional
    ///     <paramref name="repoRelativeCandidates" /> are checked against the repo root first-class, for
    ///     assets that also live under <c>Sample/</c>.
    /// </summary>
    public static string? SteamGameFile(
        string gameFolder, string relativePath, params string[] repoRelativeCandidates)
    {
        var steamRelative = Path.Combine(gameFolder, relativePath);

        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrEmpty(root))
        {
            // Flat first: the common case is one scratch folder holding the masters under test.
            var flat = Path.Combine(root, Path.GetFileName(relativePath));
            if (File.Exists(flat))
            {
                return flat;
            }

            var mirrored = Path.Combine(root, steamRelative);
            if (File.Exists(mirrored))
            {
                return mirrored;
            }
        }

        if (RepoRoot is { } repoRoot)
        {
            foreach (var candidate in repoRelativeCandidates)
            {
                var full = Path.Combine(repoRoot, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        foreach (var library in SteamLibraryRoots())
        {
            var full = Path.Combine(library, steamRelative);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>
    ///     Resolve a directory inside a Steam-installed game (a Data folder, say) using the same probe
    ///     order as <see cref="SteamGameFile" />.
    /// </summary>
    public static string? SteamGameDirectory(string gameFolder, string relativePath)
    {
        var steamRelative = Path.Combine(gameFolder, relativePath);

        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrEmpty(root))
        {
            var mirrored = Path.Combine(root, steamRelative);
            if (Directory.Exists(mirrored))
            {
                return mirrored;
            }
        }

        foreach (var library in SteamLibraryRoots())
        {
            var full = Path.Combine(library, steamRelative);
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    // Every fixed drive, not just the one this repo happens to sit on: Steam libraries are routinely
    // placed on a separate data drive. Both casings appear in the wild — Steam writes "steamapps",
    // older installs and manual moves leave "SteamApps" — and the distinction matters on Linux.
    private static IEnumerable<string> SteamLibraryRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var rootPath = drive.RootDirectory.FullName;
            yield return Path.Combine(rootPath, "SteamLibrary", "steamapps", "common");
            yield return Path.Combine(rootPath, "SteamLibrary", "SteamApps", "common");
            yield return Path.Combine(rootPath, "Steam", "steamapps", "common");
            yield return Path.Combine(rootPath, "Program Files (x86)", "Steam", "steamapps", "common");
        }
    }

    // Self-contained on purpose: this file is also linked into tools/EsmSchemaGen.Tests, which is
    // outside the solution and cannot see the rest of this project. Probing upward for
    // Directory.Build.props walks out of the nested bin/ output. Null (no root found) is not an
    // error — it just means the repo-relative candidates are skipped.
    private static readonly Lazy<string?> LazyRepoRoot = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    });

    private static string? RepoRoot => LazyRepoRoot.Value;
}
