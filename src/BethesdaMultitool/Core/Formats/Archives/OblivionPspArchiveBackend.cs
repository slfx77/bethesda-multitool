using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     The cancelled PSP Oblivion's <c>GR.ARC</c> pack behind the backend seam. Immutable after
///     open; reads go through one long-lived memory-mapped accessor whose positioned reads are safe
///     for unsynchronised concurrent use, per the <c>Core/Vfs</c> contract. The packs are large
///     (up to 216 MB) and hold single entries of several megabytes, so mapping beats buffering.
///     <para>
///         ⚠ <b>A dot in an entry name is usually a namespace separator, not a file extension.</b>
///         Alongside genuine files (<c>LegalScreen.jpg</c>, <c>Italian.sdb</c>,
///         <c>journaldata.xml</c>) the pack holds namespaced resource ids such as
///         <c>CContainerBehaviour.ClothSack</c> and <c>CEnemyBehaviourLoot.MythicDawn</c>, whose
///         suffix names a game object rather than a format. Taking the text after the last dot
///         would fill the archive's extension histogram with entries like ".clothsack".
///         The payload settles it: measured across all seven staged packs, EVERY namespaced name
///         has a RenderWare payload and every real extension (jpg 56, sdb 36, png 12, xml 8,
///         log 5) has one that is not, so this backend reports an extension only for entries
///         that are not RenderWare streams.
///     </para>
/// </summary>
internal sealed class OblivionPspArchiveBackend : IArchiveBackend
{
    /// <summary>Bytes of a RenderWare chunk header: type, size, then the library id this reads.</summary>
    private const int RenderWareHeaderLength = 12;

    /// <summary>RenderWare 3.6 library id, the vintage almost every payload carries.</summary>
    private const uint RenderWareLibrary36 = 0x1802FFFF;

    /// <summary>RenderWare 3.7 library id, on some June 2006 entries.</summary>
    private const uint RenderWareLibrary37 = 0x1C020065;

    private readonly MemoryMappedViewAccessor _accessor;

    private readonly OblivionPspArchive _archive;
    private readonly MemoryMappedFile _mmf;

    public OblivionPspArchiveBackend(OblivionPspArchive archive)
    {
        _archive = archive;
        _mmf = MemoryMappedFile.CreateFromFile(
            archive.FilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public string FormatName => _archive.IsTagged ? "ARC (Oblivion PSP, A2.0)" : "ARC (Oblivion PSP)";

    public string PlatformLabel => "PSP";

    public int TotalFiles => _archive.Entries.Count;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_archive.Entries.Count);
        foreach (var entry in _archive.Entries)
        {
            list.Add(new ArchiveEntry(
                entry.Name,
                string.Empty,
                entry.Name,
                ExtensionOf(entry),
                entry.Size,
                entry.Offset,
                false,
                entry));
        }

        return list;
    }

    public IEnumerable<string> EnumerateFilePaths()
    {
        foreach (var entry in _archive.Entries)
        {
            yield return entry.Name;
        }
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not OblivionPspArchiveEntry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        var bytes = new byte[record.Size];
        if (record.Size == 0)
        {
            // Legal: the community-modified February 2007 disc truncates Hub_5_Demo to nothing.
            return bytes;
        }

        var read = _accessor.ReadArray(record.Offset, bytes, 0, (int)record.Size);
        if (read != record.Size)
        {
            throw new InvalidDataException(
                $"Oblivion PSP entry '{record.Name}' is truncated: read {read} of {record.Size} bytes.");
        }

        return bytes;
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }

    /// <summary>
    ///     The entry's file extension, or empty when its name is a namespaced resource id rather
    ///     than a file name. See the type remarks: a RenderWare payload means the dot is a namespace
    ///     separator. Reads 12 bytes through the shared accessor, which the index build does once.
    /// </summary>
    private string ExtensionOf(OblivionPspArchiveEntry entry)
    {
        var extension = Path.GetExtension(entry.Name);
        if (extension.Length <= 1)
        {
            return string.Empty;
        }

        if (entry.Size >= RenderWareHeaderLength)
        {
            var library = _accessor.ReadUInt32(entry.Offset + 8);
            if (library is RenderWareLibrary36 or RenderWareLibrary37)
            {
                return string.Empty;
            }
        }

        return extension.ToLowerInvariant();
    }
}
