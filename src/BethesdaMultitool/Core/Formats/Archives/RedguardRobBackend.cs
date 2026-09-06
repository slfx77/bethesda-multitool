using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Redguard;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     Redguard's per-map <c>.ROB</c> object archive behind the backend seam. Immutable after open
///     and read through one long-lived memory-mapped accessor, matching the sibling classic
///     backends and the lock-free <c>Core/Vfs</c> read contract.
///     <para>
///         Segments carry a bare 8-character name with no extension; every payload is a <c>.3D</c>
///         mesh (measured across all 4,667 non-empty retail segments), so entries are surfaced as
///         <c>NAME.3D</c> — the same convention the MIT reference exporter writes to disk, and what
///         makes <c>archive</c>'s extension histogram and the asset browser's classifier meaningful.
///     </para>
/// </summary>
internal sealed class RedguardRobBackend : IArchiveBackend
{
    private readonly RedguardRobArchive _archive;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public RedguardRobBackend(RedguardRobArchive archive)
    {
        _archive = archive;
        _mmf = MemoryMappedFile.CreateFromFile(
            archive.FilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public string FormatName => "ROB (Redguard)";

    public string PlatformLabel => "DOS";

    public int TotalFiles => _archive.Entries.Count;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_archive.Entries.Count);
        foreach (var entry in _archive.Entries)
        {
            var name = entry.Name + ".3D";
            list.Add(new ArchiveEntry(
                name,
                string.Empty,
                name,
                ".3d",
                entry.Size,
                entry.Offset,
                false,
                entry));
        }

        return list;
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not RedguardRobEntry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        var bytes = new byte[record.Size];
        if (record.Size == 0)
        {
            return bytes;
        }

        var read = _accessor.ReadArray(record.Offset, bytes, 0, record.Size);
        if (read != record.Size)
        {
            throw new InvalidDataException(
                $"Redguard ROB segment '{record.Name}' is truncated: read {read} of {record.Size} bytes.");
        }

        return bytes;
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
