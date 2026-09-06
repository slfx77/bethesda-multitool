using BethesdaMultitool.Core.Formats.VanBuren;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     The Van Buren prototype's <c>.grp</c> behind the backend seam, so the whole <c>archive</c>
///     group works on one. Immutable after open; the file is read once into memory because the
///     largest archive in the build is 232 MB and its entries are read in bulk rather than sampled.
///     <para>
///         ⚠ Entries carry NO names in the container — only an offset and a size — so the names here
///         are synthetic (<c>NNNNN.&lt;TAG&gt;</c>, from the payload's own leading tag). They are
///         stable because entry ORDER is stable, which is the only identity the format offers.
///     </para>
/// </summary>
internal sealed class VanBurenGrpBackend : IArchiveBackend
{
    private readonly byte[] _bytes;
    private readonly VanBurenGrpArchive _archive;

    public VanBurenGrpBackend(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _bytes = File.ReadAllBytes(path);
        _archive = VanBurenGrpArchive.Parse(_bytes, Path.GetFileName(path));
    }

    public string FormatName => "GRP (Van Buren)";

    public string PlatformLabel => "PC";

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
                "." + entry.FileExtension.ToLowerInvariant(),
                entry.Size,
                entry.Offset,
                false,
                entry));
        }

        return list;
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Record is not VanBurenGrpEntry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        return VanBurenGrpArchive.Read(_bytes, record);
    }

    public void Dispose()
    {
        // Nothing to release: the archive is a byte array, held for the lifetime of the backend.
    }
}
