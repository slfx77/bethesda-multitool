using BethesdaMultitool.Core.Formats.Arena;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     The TES Arena v1.04 floppy release behind the backend seam: one split directory
///     (<c>ARENA.H1..Hn</c>) over one block stream (<c>ARENA.1..n</c>), which together install the
///     game's 106 files. Immutable after open — the parsed archive owns the concatenated stream as
///     a plain array and every read is a bounded span over it, so the
///     <see cref="IArchiveBackend.MarkShared" /> default no-op is honest and reads are safe for
///     unsynchronised concurrent use per the <c>Core/Vfs</c> contract.
///     <para>
///         Entries present under the directory record's own name (<c>ARENA/…</c>), the directory
///         the installer creates, so an extraction reproduces the installed tree.
///     </para>
/// </summary>
internal sealed class ArenaInstallerBackend : IArchiveBackend
{
    private readonly ArenaInstallerArchive _archive;

    public ArenaInstallerBackend(ArenaInstallerArchive archive)
    {
        _archive = archive;
    }

    public string FormatName => "Install Utility (Arena)";

    public string PlatformLabel => "DOS";

    public int TotalFiles => _archive.Entries.Count;

    /// <summary>
    ///     The whole release, not the anchor: eight <c>ARENA.Hn</c> plus eight <c>ARENA.n</c> plus
    ///     the four bytes of <c>ARENA.TDS</c> (11,093,746 on the retail v1.04 set, against the
    ///     8,160-byte <c>ARENA.H1</c> the file system reports for the opened path).
    /// </summary>
    public long? ContainerSizeBytes => _archive.ContainerSizeBytes;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_archive.Entries.Count);
        foreach (var entry in _archive.Entries)
        {
            var extension = Path.GetExtension(entry.Name);
            var folder = entry.Directory;
            list.Add(new ArchiveEntry(
                folder.Length == 0 ? entry.Name : $"{folder}/{entry.Name}",
                folder,
                entry.Name,
                string.IsNullOrEmpty(extension) ? string.Empty : extension.ToLowerInvariant(),
                entry.Size,
                entry.Blocks.Count == 0 ? 0 : entry.Blocks[0].StreamOffset,
                entry.Blocks.Any(static block => !block.IsStored && block.UncompressedLength > 0),
                entry));
        }

        return list;
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not ArenaInstallerEntry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        return _archive.Extract(record);
    }

    public void Dispose()
    {
        // The archive holds only managed arrays; nothing to release.
    }
}
