using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

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
    public static IGameFileSystem OpenArchive(string archivePath, ArchiveHandleRegistry? registry = null)
    {
        return registry is null
            ? new ArchiveFileSystem(archivePath)
            : new ArchiveFileSystem(registry.Acquire(archivePath));
    }

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
            AddArchives(layers, dataDirectory, pattern, registry, true);
        }

        return new LayeredGameFileSystem(layers);
    }

    /// <summary>
    ///     Mounts a classic (pre-plugin-era) game from its install root, in the precedence its
    ///     <see cref="GameProfile" /> declares: the loose tree first — at
    ///     <see cref="GameProfile.ClassicLooseRoot" /> under the root when the profile names one,
    ///     otherwise the root itself — then one layer per file matching each entry of
    ///     <see cref="GameProfile.ClassicArchiveGlobs" />, in glob order.
    ///     <para>
    ///         These games need their own factory rather than <see cref="OpenDataFolder" />, which
    ///         only knows <c>*.bsa</c>/<c>*.ba2</c> in one directory. A classic glob may name a
    ///         subdirectory (<c>ARENA2\*.BSA</c>), an exact file (<c>GAMEDATA\3D.BS6</c>), or an
    ///         archive whose extension says otherwise — Daggerfall's <c>DAGGER.SND</c> and
    ///         Battlespire's <c>3D.BS6</c> are both XnGine BSAs. Order is the profile's, not
    ///         alphabetical, because for these games it IS the override rule (Fallout 2 resolves
    ///         <c>f2_res</c> over <c>patch*</c> over <c>critter</c> over <c>master</c>).
    ///     </para>
    /// </summary>
    /// <param name="profile">The game's profile; supplies the loose root and the archive globs.</param>
    /// <param name="installRoot">The directory <c>ClassicGameLocator</c> identified.</param>
    /// <param name="includeLooseFiles">Mount the loose tree as the highest-priority layer.</param>
    /// <param name="registry">Optional shared archive-handle registry for the archive layers.</param>
    public static LayeredGameFileSystem OpenGameRoot(
        GameProfile profile, string installRoot, bool includeLooseFiles = true,
        ArchiveHandleRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installRoot);

        var layers = new List<IGameFileSystem>();
        if (includeLooseFiles)
        {
            // Profile paths are engine-spelled (PSP_GAME\USRDIR) over an install the game treats
            // case-insensitively; HostPath re-spells them for the host and, on a case-sensitive
            // one, finds the directory however it is cased. On Windows these are the plain joins.
            var looseRoot = profile.ClassicLooseRoot.Length > 0
                ? HostPath.ResolveDirectory(installRoot, profile.ClassicLooseRoot)
                : installRoot;
            layers.Add(new LooseFileSystem(looseRoot));

            // Asset directories that sit OUTSIDE the loose root (the Battlespire CD's videos\
            // beside GAME.EXE) mount under their own name; an install without them is unaffected.
            foreach (var extra in profile.ClassicExtraLooseDirectories)
            {
                var directory = HostPath.ResolveDirectory(installRoot, extra);
                if (Directory.Exists(directory))
                {
                    layers.Add(new PrefixedFileSystem(new LooseFileSystem(directory), extra));
                }
            }
        }

        foreach (var glob in profile.ClassicArchiveGlobs)
        {
            // Split the glob's directory off with the engine-path helper, not Path: on a Unix host
            // Path.GetDirectoryName(@"ARENA2\*.BSA") is empty and the whole glob would reach the
            // enumerator as a pattern.
            var relativeDirectory = EnginePath.DirectoryName(glob);
            var pattern = EnginePath.FileName(glob);
            if (pattern.Length == 0)
            {
                continue;
            }

            var directory = relativeDirectory.Length == 0
                ? installRoot
                : HostPath.ResolveDirectory(installRoot, relativeDirectory);
            AddArchives(layers, directory, pattern, registry);
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

        // Case-insensitive on every host: the plain overload ignores case only on Windows, so
        // *.BSA would miss arch3d.bsa on Linux; the semantics are otherwise the plain overload's.
        var ordered = HostPath.EnumerateFiles(directory, pattern);
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
