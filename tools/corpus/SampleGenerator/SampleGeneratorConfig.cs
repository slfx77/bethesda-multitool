namespace SampleGenerator;

/// <summary>
///     Resolved roots for one run.
///     <para>
///         Unlike the sibling NeversoftMultitool generator, which reads every build from one
///         <c>--media-root</c>, this collection is scattered across a Downloads folder, a MEGA sync
///         folder, a PS2 dump folder, several Steam libraries and the repository's own
///         <c>Sample/</c> tree. So media is located by <em>searching</em> a list of roots for the
///         file or directory a catalog entry names, rather than by convention.
///     </para>
/// </summary>
internal static class SampleGeneratorConfig
{
    /// <summary>Semicolon-separated media search roots.</summary>
    internal const string MediaRootsVariable = "BETHESDA_MEDIA_ROOTS";

    /// <summary>Overrides the repository's <c>Sample/</c> directory.</summary>
    internal const string SampleRootVariable = "BETHESDA_SAMPLE_ROOT";

    /// <summary>Overrides the extracted-media cache location.</summary>
    internal const string ResearchRootVariable = "BETHESDA_RESEARCH_ROOT";

    /// <summary>How deep <see cref="FindMedia" /> descends into a media root.</summary>
    private const int MediaSearchDepth = 3;

    /// <summary>The repository's <c>Sample/</c> directory — the parent of every output below.</summary>
    internal static string SampleRoot { get; private set; } = string.Empty;

    /// <summary>The generated corpus, <c>Sample/Builds</c> — build directories and nothing else.</summary>
    internal static string SampleBuilds { get; private set; } = string.Empty;

    /// <summary>Original media relocated out of the builds, <c>Sample/Media</c>.</summary>
    internal static string SampleMedia { get; private set; } = string.Empty;

    /// <summary>Symbols and executables collected from the builds, <c>Sample/DebugSymbols</c>.</summary>
    internal static string SampleDebugSymbols { get; private set; } = string.Empty;

    /// <summary>Binaries staged for decompilation, <c>Sample/ReverseEngineering</c>.</summary>
    internal static string SampleReverseEngineering { get; private set; } = string.Empty;

    /// <summary>
    ///     Generator output, <c>Sample/Catalog</c>: the corpus census plus one manifest per build.
    ///     Kept out of <c>Sample/Builds</c> so a build directory contains only game files.
    /// </summary>
    internal static string SampleCatalog { get; private set; } = string.Empty;

    /// <summary>
    ///     Cache of media expanded out of <c>.7z</c>/<c>.zip</c> sources. Kept outside
    ///     <see cref="SampleRoot" /> so a corpus rebuild does not re-run multi-gigabyte extractions.
    /// </summary>
    internal static string ResearchRoot { get; private set; } = string.Empty;

    /// <summary>Read-only roots searched for the media a catalog entry names.</summary>
    internal static IReadOnlyList<string> MediaRoots { get; private set; } = [];

    internal static void Configure(IReadOnlyList<string> mediaRoots, string? sampleRoot, string? researchRoot)
    {
        var resolvedSample = FirstNonEmpty(sampleRoot, Environment.GetEnvironmentVariable(SampleRootVariable));
        resolvedSample = string.IsNullOrWhiteSpace(resolvedSample)
            ? Path.Combine(FindRepositoryRoot(), "Sample")
            : NormalizeRoot(resolvedSample, "sample");

        var resolvedResearch = FirstNonEmpty(researchRoot, Environment.GetEnvironmentVariable(ResearchRootVariable));
        resolvedResearch = string.IsNullOrWhiteSpace(resolvedResearch)
            ? Path.Combine(Path.GetTempPath(), "BethesdaSampleGenerator")
            : NormalizeRoot(resolvedResearch, "research");

        SampleRoot = NormalizeRoot(resolvedSample, "sample");
        SampleBuilds = Path.Combine(SampleRoot, "Builds");
        SampleMedia = Path.Combine(SampleRoot, "Media");
        SampleDebugSymbols = Path.Combine(SampleRoot, "DebugSymbols");
        SampleReverseEngineering = Path.Combine(SampleRoot, "ReverseEngineering");
        SampleCatalog = Path.Combine(SampleRoot, "Catalog");
        ResearchRoot = NormalizeRoot(resolvedResearch, "research");

        if (SampleGeneratorPathSafety.IsStrictDescendant(ResearchRoot, SampleRoot) ||
            SampleGeneratorPathSafety.IsStrictDescendant(SampleRoot, ResearchRoot) ||
            SampleRoot.Equals(ResearchRoot, SampleGeneratorPathSafety.PathComparison))
        {
            throw new ArgumentException(
                $"The sample and research roots must not overlap: '{SampleRoot}' and '{ResearchRoot}'.");
        }

        MediaRoots = ResolveMediaRoots(mediaRoots);

        SampleGeneratorPathSafety.RejectReparseTraversal(SampleRoot, SampleRoot);
        Directory.CreateDirectory(SampleBuilds);
        Directory.CreateDirectory(SampleMedia);
        Directory.CreateDirectory(SampleDebugSymbols);
        Directory.CreateDirectory(SampleReverseEngineering);
        Directory.CreateDirectory(SampleCatalog);
        Directory.CreateDirectory(ResearchRoot);
        SampleGeneratorPathSafety.RejectReparseTraversal(SampleRoot, SampleBuilds);
    }

    /// <summary>
    ///     Locates the file or directory a catalog entry names, searching each media root to
    ///     <see cref="MediaSearchDepth" /> levels. Returns null when nothing matches, which the
    ///     caller reports as a missing build rather than a failure.
    /// </summary>
    internal static string? FindMedia(string name, bool directory)
    {
        foreach (var root in MediaRoots)
        {
            var direct = Path.Combine(root, name);
            if (directory ? Directory.Exists(direct) : File.Exists(direct))
            {
                return direct;
            }

            var found = SearchTree(root, name, directory, MediaSearchDepth);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    ///     Every fixed drive's Steam library, both casings. Steam writes "steamapps"; older installs
    ///     and manual moves leave "SteamApps", and the distinction matters off Windows. Mirrors the
    ///     probe order in the test suite's <c>RealAssetPaths</c> so the two agree on what is installed.
    /// </summary>
    internal static string? FindSteamGame(string gameFolder)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var rootPath = drive.RootDirectory.FullName;
            string[] libraries =
            [
                Path.Combine(rootPath, "SteamLibrary", "steamapps", "common"),
                Path.Combine(rootPath, "SteamLibrary", "SteamApps", "common"),
                Path.Combine(rootPath, "Steam", "steamapps", "common"),
                Path.Combine(rootPath, "Program Files (x86)", "Steam", "steamapps", "common"),
            ];

            foreach (var library in libraries)
            {
                var candidate = Path.Combine(library, gameFolder);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? SearchTree(string root, string name, bool directory, int depth)
    {
        if (depth <= 0 || !Directory.Exists(root))
        {
            return null;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var child in children)
        {
            var candidate = Path.Combine(child, name);
            if (directory ? Directory.Exists(candidate) : File.Exists(candidate))
            {
                return candidate;
            }

            var deeper = SearchTree(child, name, directory, depth - 1);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }

    private static string[] ResolveMediaRoots(IReadOnlyList<string> requested)
    {
        var roots = new List<string>();
        foreach (var root in requested)
        {
            roots.Add(root);
        }

        if (roots.Count == 0)
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(MediaRootsVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                roots.AddRange(fromEnvironment.Split(';', StringSplitOptions.RemoveEmptyEntries |
                                                         StringSplitOptions.TrimEntries));
            }
        }

        if (roots.Count == 0)
        {
            roots.AddRange(DefaultMediaRoots());
        }

        return roots
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Machine-shaped defaults, the way RealAssetPaths keeps one literal for the Brotherhood of
    // Steel image: they are a LAST resort behind both --media-root and BETHESDA_MEDIA_ROOTS, so
    // another machine configures rather than edits. Only roots that exist are kept.
    private static IEnumerable<string> DefaultMediaRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            yield return Path.Combine(profile, "Downloads");
            yield return Path.Combine(profile, "Documents", "MEGA downloads");
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
            {
                yield return Path.Combine(drive.RootDirectory.FullName, "PS2");
            }
        }
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;

    private static string NormalizeRoot(string path, string label)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Directory.GetParent(fullPath) is null)
        {
            throw new ArgumentException($"The {label} root cannot be a filesystem root: {fullPath}");
        }

        return fullPath;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BethesdaMultitool.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find the repository root; pass --sample-root or set {SampleRootVariable}.");
    }
}
