using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Fallout;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     Fallout 2's DAT2 behind the backend seam. Immutable after open; reads go through one
///     long-lived memory-mapped accessor whose positioned reads are safe for unsynchronised
///     concurrent use, and each zlib entry inflates through its own stream.
/// </summary>
internal sealed class Dat2Backend : IArchiveBackend
{
    private readonly Dat2Directory _directory;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public Dat2Backend(Dat2Directory directory)
    {
        _directory = directory;
        _mmf = MemoryMappedFile.CreateFromFile(directory.FilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public string FormatName => "DAT2 (Fallout 2)";

    public string PlatformLabel => "PC";

    public int TotalFiles => _directory.Entries.Count;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_directory.Entries.Count);
        foreach (var entry in _directory.Entries)
        {
            var fullPath = entry.FullPath;
            var slash = fullPath.LastIndexOf('/');
            var name = slash < 0 ? fullPath : fullPath[(slash + 1)..];
            var folder = slash < 0 ? string.Empty : fullPath[..slash];
            var ext = Path.GetExtension(name);
            list.Add(new ArchiveEntry(
                fullPath, folder, name,
                string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant(),
                entry.UnpackedSize, entry.Offset, entry.IsCompressed, entry));
        }

        return list;
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not Dat2Entry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        var packed = new byte[record.PackedSize];
        if (packed.Length > 0)
        {
            var read = _accessor.ReadArray(record.Offset, packed, 0, packed.Length);
            if (read != packed.Length)
            {
                throw new InvalidDataException($"DAT2 entry '{record.FullPath}' is truncated: read {read} of {packed.Length} bytes.");
            }
        }

        if (!record.IsCompressed)
        {
            return packed;
        }

        var output = new byte[record.UnpackedSize];
        using var inflate = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        var total = 0;
        while (total < output.Length)
        {
            var n = inflate.Read(output, total, output.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        if (total != output.Length)
        {
            throw new InvalidDataException($"DAT2 entry '{record.FullPath}' inflated to {total} of {output.Length} declared bytes.");
        }

        return output;
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
