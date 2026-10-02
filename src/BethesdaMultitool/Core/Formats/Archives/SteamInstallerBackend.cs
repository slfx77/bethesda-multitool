using BethesdaMultitool.Core.Formats.Steam;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     A Steam retail disc's installer payload behind the backend seam, so
///     <c>archive list/extract/info/find</c> and the asset browser see the game files the way they
///     see any other container's — the disc stops being an opaque 6.4 GB of ciphertext.
///     <para>
///         ⚑ A MULTI-FILE family: the anchor is the <c>.sim</c> manifest (a few tens of KB) but the
///         payload is the sibling <c>.sid</c> parts, so <see cref="ContainerSizeBytes" /> is
///         overridden. Without it <c>archive info</c> would report a 25 KB manifest as the size of a
///         6.4 GB release — the same trap the Arena floppy installer hit.
///     </para>
///     <para>
///         ⚠ Listing needs NO key (the manifest is plaintext); extraction needs the per-depot legacy
///         key. That asymmetry is deliberate and is why <see cref="ListFiles" /> never throws for a
///         keyless disc — <c>archive list</c> stays useful, and only <see cref="Extract" /> reports
///         the missing key, naming the depot.
///     </para>
///     <para>
///         Immutable after construction and free of per-instance mutable state, so every member is
///         safe for unsynchronised concurrent reads.
///     </para>
/// </summary>
internal sealed class SteamInstallerBackend : IArchiveBackend
{
    private readonly SteamInstallerArchive _archive;

    public SteamInstallerBackend(SteamInstallerArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        _archive = archive;
    }

    public string FormatName => "SID (Steam retail disc)";

    public string PlatformLabel => "PC";

    public int TotalFiles => _archive.Manifest.Files.Count;

    public long? ContainerSizeBytes => _archive.ContainerSizeBytes;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var files = _archive.Manifest.Files;
        var list = new List<ArchiveEntry>(files.Count);
        foreach (var file in files)
        {
            var separator = file.Path.LastIndexOf('/');
            var folder = separator < 0 ? string.Empty : file.Path[..separator];
            var name = separator < 0 ? file.Path : file.Path[(separator + 1)..];

            list.Add(new ArchiveEntry(
                file.Path,
                folder,
                name,
                Path.GetExtension(name),
                file.Size,
                file.Offset,
                true,
                file));
        }

        return list;
    }

    public IEnumerable<string> EnumerateFilePaths()
    {
        foreach (var file in _archive.Manifest.Files)
        {
            yield return file.Path;
        }
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _archive.Extract(Resolve(entry));
    }

    /// <summary>
    ///     Streams straight to disk. Overridden because members run to 1.6 GB (the voice BSA) and
    ///     the interface default would materialise each one in memory first.
    /// </summary>
    public async Task<bool> ExtractToDiskAsync(ArchiveEntry entry, string outputDir, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var file = Resolve(entry);

        var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0 || Path.IsPathRooted(relative) ||
            file.Path.Split('/').Any(static part => part == ".."))
        {
            throw new InvalidOperationException($"Archive entry path is not extractable: '{file.Path}'.");
        }

        var target = Path.Combine(outputDir, relative);
        if (!overwrite && File.Exists(target))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = File.Create(target);
        _archive.ExtractTo(file, output);
        return true;
    }

    public void Dispose()
    {
        _archive.Dispose();
    }

    private static SteamInstallerFile Resolve(ArchiveEntry entry)
    {
        return entry.Record as SteamInstallerFile
               ?? throw new InvalidOperationException(
                   $"Entry '{entry.FullPath}' did not come from {nameof(SteamInstallerBackend)}.");
    }
}
