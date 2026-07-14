using BethesdaMultitool.Core.Formats.Bsa.Index;

namespace BethesdaMultitool.Core.Formats.Papyrus;

/// <summary>
///     Format-neutral Papyrus script access over classic BSA and BA2 archives. Entries are filtered
///     by their virtual <c>.pex</c> path and retain the backing archive record so extraction stays
///     lazy; callers can inspect large archives without materializing every script.
/// </summary>
public sealed class PexArchiveReader : IDisposable
{
    private readonly ArchiveReader _archive;

    private PexArchiveReader(ArchiveReader archive)
    {
        _archive = archive;
        Entries = archive.ListFiles()
            .Where(entry => entry.Extension.Equals(".pex", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new PexArchiveEntry(
                entry.FullPath,
                entry.Size,
                entry.Compressed,
                entry))
            .OrderBy(entry => entry.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Whether the backing container is BA2 rather than BSA.</summary>
    public bool IsBa2 => _archive.IsBa2;

    /// <summary>Short backing-container label: <c>BSA</c> or <c>BA2</c>.</summary>
    public string FormatName => _archive.FormatName;

    /// <summary>All Papyrus binaries in deterministic virtual-path order.</summary>
    public IReadOnlyList<PexArchiveEntry> Entries { get; }

    /// <summary>Opens a BSA or BA2 archive by file magic.</summary>
    public static PexArchiveReader Open(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return new PexArchiveReader(ArchiveReader.Open(archivePath));
    }

    /// <summary>Extracts one script binary without parsing it.</summary>
    public byte[] Extract(PexArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureOwnedEntry(entry);
        if (entry.UncompressedSize < 0 || entry.UncompressedSize > PexParser.MaximumFileSize)
        {
            throw new PexParseException(
                $"archive entry {entry.VirtualPath} has unsupported size {entry.UncompressedSize}",
                0);
        }

        var bytes = _archive.Extract(entry.Source);
        if (bytes.Length > PexParser.MaximumFileSize)
        {
            throw new PexParseException(
                $"archive entry {entry.VirtualPath} exceeds the {PexParser.MaximumFileSize}-byte safety limit",
                0);
        }

        return bytes;
    }

    /// <summary>Extracts and parses one script.</summary>
    public PexFile Parse(PexArchiveEntry entry) => PexParser.Parse(Extract(entry));

    /// <summary>
    ///     Finds one script by exact normalized virtual path, or by a unique case-insensitive
    ///     substring when no exact path exists. Returns <see langword="null" /> for no match and
    ///     throws when a substring is ambiguous.
    /// </summary>
    public PexArchiveEntry? Find(string pathOrSubstring)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrSubstring);
        var normalized = Normalize(pathOrSubstring);
        var exact = Entries.FirstOrDefault(entry =>
            Normalize(entry.VirtualPath).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var matches = Entries.Where(entry =>
                Normalize(entry.VirtualPath).Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Papyrus script selector '{pathOrSubstring}' matches more than one archive entry.")
        };
    }

    public void Dispose() => _archive.Dispose();

    private void EnsureOwnedEntry(PexArchiveEntry entry)
    {
        if (!Entries.Contains(entry))
        {
            throw new ArgumentException("The Papyrus entry does not belong to this archive reader.", nameof(entry));
        }
    }

    private static string Normalize(string path) => path.Replace('/', '\\');
}

/// <summary>Metadata for one lazily extracted Papyrus archive entry.</summary>
public sealed record PexArchiveEntry
{
    internal PexArchiveEntry(
        string virtualPath,
        long uncompressedSize,
        bool compressed,
        ArchiveReader.ArchiveEntry source)
    {
        VirtualPath = virtualPath;
        UncompressedSize = uncompressedSize;
        Compressed = compressed;
        Source = source;
    }

    public string VirtualPath { get; }
    public long UncompressedSize { get; }
    public bool Compressed { get; }
    internal ArchiveReader.ArchiveEntry Source { get; }
}
