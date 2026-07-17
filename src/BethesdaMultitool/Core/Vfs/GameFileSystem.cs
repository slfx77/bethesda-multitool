namespace BethesdaMultitool.Core.Vfs;

/// <summary>
///     Factory entry points for <see cref="IGameFileSystem" />. Opening parses archive tables and
///     memory-maps files — do it off the UI thread and share the instance; reads are then
///     lock-free and concurrent per the interface contract.
/// </summary>
public static class GameFileSystem
{
    /// <summary>Opens a single archive (BSA or BA2, dispatched by file magic).</summary>
    public static IGameFileSystem OpenArchive(string archivePath) => new ArchiveFileSystem(archivePath);

    /// <summary>
    ///     Opens a game <c>Data</c> folder with engine-faithful precedence: loose files shadow
    ///     archives, and archives resolve in alphabetical filename order (BSAs before BA2s).
    ///     Archives that fail to open are skipped rather than failing the whole mount.
    /// </summary>
    /// <param name="dataDirectory">The Data directory (loose root and archive location).</param>
    /// <param name="includeLooseFiles">Mount the loose tree as the highest-priority layer.</param>
    /// <param name="includeBa2">Mount <c>.ba2</c> archives (FO4/FO76) after the BSAs.</param>
    public static LayeredGameFileSystem OpenDataFolder(
        string dataDirectory, bool includeLooseFiles = true, bool includeBa2 = true)
    {
        var layers = new List<IGameFileSystem>();
        if (includeLooseFiles)
        {
            layers.Add(new LooseFileSystem(dataDirectory));
        }

        AddArchives(layers, dataDirectory, "*.bsa");
        if (includeBa2)
        {
            AddArchives(layers, dataDirectory, "*.ba2");
        }

        return new LayeredGameFileSystem(layers);
    }

    private static void AddArchives(List<IGameFileSystem> layers, string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, pattern)
                     .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                layers.Add(new ArchiveFileSystem(path));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                           or UnauthorizedAccessException or EndOfStreamException)
            {
                // A corrupt/locked archive must not take down the whole mount; resolution simply
                // falls through to the remaining layers (mirrors DataFolderIndex's tolerance).
            }
        }
    }
}
