using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Compression;
using BethesdaMultitool.Core.Formats.Fallout;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     Fallout 1's DAT1 behind the backend seam. Immutable after open; reads go through one
///     long-lived memory-mapped accessor whose positioned reads are safe for unsynchronised
///     concurrent use, per the <c>Core/Vfs</c> contract. Compressed entries decode through the
///     block-framed LZSS ported from dat-unpacker.
/// </summary>
internal sealed class Dat1Backend : IArchiveBackend
{
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly Dat1Directory _directory;
    private readonly MemoryMappedFile _mmf;

    public Dat1Backend(Dat1Directory directory)
    {
        _directory = directory;
        _mmf = MemoryMappedFile.CreateFromFile(directory.FilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public string FormatName => "DAT1 (Fallout)";

    public string PlatformLabel => "DOS";

    public int TotalFiles => _directory.Entries.Count;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_directory.Entries.Count);
        foreach (var entry in _directory.Entries)
        {
            var ext = Path.GetExtension(entry.Name);
            list.Add(new ArchiveEntry(
                entry.FullPath,
                entry.Directory == "." ? string.Empty : entry.Directory.Replace('\\', '/'),
                entry.Name,
                string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant(),
                entry.Size,
                entry.Offset,
                entry.IsCompressed,
                entry));
        }

        return list;
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not Dat1Entry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        var stored = new byte[record.StoredLength];
        if (stored.Length == 0)
        {
            return record.IsCompressed ? new byte[record.Size] : stored;
        }

        var read = _accessor.ReadArray(record.Offset, stored, 0, stored.Length);
        if (read != stored.Length)
        {
            throw new InvalidDataException(
                $"DAT1 entry '{record.FullPath}' is truncated: read {read} of {stored.Length} bytes.");
        }

        return record.IsCompressed ? FalloutLzss.Decompress(stored, (int)record.Size) : stored;
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
