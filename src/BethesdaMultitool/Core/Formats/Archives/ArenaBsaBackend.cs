using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     TES Arena GLOBAL.BSA behind the backend seam. Immutable after open (the
///     <see cref="IArchiveBackend.MarkShared" /> default no-op is honest); reads go through one
///     long-lived memory-mapped accessor whose positioned reads are safe for unsynchronised
///     concurrent use, per the <c>Core/Vfs</c> contract.
/// </summary>
internal sealed class ArenaBsaBackend : IArchiveBackend
{
    private readonly ArenaBsaArchive _archive;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public ArenaBsaBackend(ArenaBsaArchive archive)
    {
        _archive = archive;
        _mmf = MemoryMappedFile.CreateFromFile(
            archive.FilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public string FormatName => "BSA (Arena)";

    public string PlatformLabel => "DOS";

    public int TotalFiles => _archive.Entries.Count;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_archive.Entries.Count);
        foreach (var entry in _archive.Entries)
        {
            var ext = Path.GetExtension(entry.Name);
            list.Add(new ArchiveEntry(
                entry.Name,
                string.Empty,
                entry.Name,
                string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant(),
                entry.Size,
                entry.Offset,
                entry.Flag != 0,
                entry));
        }

        return list;
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not ArenaBsaEntry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        if (record.Flag != 0)
        {
            throw new InvalidDataException(
                $"Arena BSA entry '{record.Name}' declares compression flag 0x{record.Flag:X4}; " +
                "no retail entry is compressed and no codec is defined for the flag.");
        }

        var bytes = new byte[record.Size];
        var read = _accessor.ReadArray(record.Offset, bytes, 0, record.Size);
        if (read != record.Size)
        {
            throw new InvalidDataException(
                $"Arena BSA entry '{record.Name}' is truncated: read {read} of {record.Size} bytes.");
        }

        return bytes;
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
