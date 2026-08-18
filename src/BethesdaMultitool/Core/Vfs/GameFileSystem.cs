namespace BethesdaMultitool.Core.Vfs;

/// <summary>
///     Factory entry points for <see cref="IGameFileSystem" />. Opening parses archive tables and
///     memory-maps files — do it off the UI thread and share the instance; reads are then
///     lock-free and concurrent per the interface contract.
/// </summary>
public static class GameFileSystem
{
    /// <summary>
    ///     Opens a single archive (BSA or BA2, dispatched by file magic) — via a shared
    ///     <paramref name="registry" /> handle when given, else a privately owned open.
    /// </summary>
    public static IGameFileSystem OpenArchive(string archivePath, ArchiveHandleRegistry? registry = null) =>
        registry is null
            ? new ArchiveFileSystem(archivePath)
            : new ArchiveFileSystem(registry.Acquire(archivePath));

    /// <summary>
    ///     Opens a game <c>Data</c> folder with engine-faithful precedence: loose files shadow
    ///     archives, and archives resolve in alphabetical filename order (BSAs before BA2s).
    ///     Archive layers open lazily on first touch, so mounting costs no archive parses and a
    ///     lookup that hits an early layer never opens the later ones; a layer that fails to open
    ///     is treated as empty rather than failing the whole mount. Pass a
    ///     <paramref name="registry" /> to share handles (and their parses) across mounts.
    /// </summary>
    /// <param name="dataDirectory">The Data directory (loose root and archive location).</param>
    /// <param name="includeLooseFiles">Mount the loose tree as the highest-priority layer.</param>
    /// <param name="includeBa2">Mount <c>.ba2</c> archives (FO4/FO76) after the BSAs.</param>
    /// <param name="registry">Optional shared archive-handle registry for the archive layers.</param>
    public static LayeredGameFileSystem OpenDataFolder(
        string dataDirectory, bool includeLooseFiles = true, bool includeBa2 = true,
        ArchiveHandleRegistry? registry = null)
    {
        var layers = new List<IGameFileSystem>();
        if (includeLooseFiles)
        {
            layers.Add(new LooseFileSystem(dataDirectory));
        }

        AddArchives(layers, dataDirectory, "*.bsa", registry);
        if (includeBa2)
        {
            AddArchives(layers, dataDirectory, "*.ba2", registry);
        }

        return new LayeredGameFileSystem(layers);
    }

    /// <summary>
    ///     Opens a narrow slice of a Data folder: the loose tree (optional) plus only those archives
    ///     whose filename matches one of <paramref name="filenamePatterns" />. For a lookup that is known
    ///     to live in a handful of named archives this avoids the whole-folder mount, whose miss path
    ///     touches every layer — on Starfield that means indexing ~1.5 M entries across 89 archives to
    ///     find one file.
    ///     <para>
    ///         Within the subset, <c>*Patch*</c> archives are mounted <b>before</b> their siblings, so a
    ///         patch entry wins. <see cref="OpenDataFolder" />'s plain alphabetical order gets this
    ///         backwards for Bethesda's "<c>Name - ThingPatch.ba2</c>" convention
    ///         (<c>Terrain01</c> &lt; <c>TerrainPatch</c>), which would silently shadow every patched file.
    ///     </para>
    /// </summary>
    /// <param name="dataDirectory">The Data directory (loose root and archive location).</param>
    /// <param name="filenamePatterns">Filename globs, e.g. <c>*Terrain*.ba2</c>. Empty mounts no archives.</param>
    /// <param name="includeLooseFiles">Mount the loose tree as the highest-priority layer.</param>
    /// <param name="registry">Optional shared archive-handle registry for the archive layers.</param>
    public static LayeredGameFileSystem OpenArchiveSubset(
        string dataDirectory, IReadOnlyList<string> filenamePatterns, bool includeLooseFiles = true,
        ArchiveHandleRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(filenamePatterns);
        var layers = new List<IGameFileSystem>();
        if (includeLooseFiles)
        {
            layers.Add(new LooseFileSystem(dataDirectory));
        }

        foreach (var pattern in filenamePatterns)
        {
            AddArchives(layers, dataDirectory, pattern, registry, patchesFirst: true);
        }

        return new LayeredGameFileSystem(layers);
    }

    private static void AddArchives(
        List<IGameFileSystem> layers, string directory, string pattern, ArchiveHandleRegistry? registry,
        bool patchesFirst = false)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var ordered = Directory.EnumerateFiles(directory, pattern);
        ordered = patchesFirst
            ? ordered
                .OrderByDescending(p => Path.GetFileNameWithoutExtension(p)
                    .Contains("Patch", StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            : ordered.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

        foreach (var path in ordered)
        {
            // Lazy: no parse until the layer is first touched. A corrupt/locked archive logs on
            // first touch and behaves as empty, so resolution falls through to later layers
            // (mirrors DataFolderIndex's tolerance, moved from mount time to first access).
            layers.Add(ArchiveFileSystem.CreateLazy(path, registry));
        }
    }
}
