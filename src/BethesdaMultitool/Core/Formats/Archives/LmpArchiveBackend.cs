using BethesdaMultitool.Core.Formats.Travels.Dawnstar;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     Dawnstar's <c>.lmp</c> lump behind the backend seam, so <c>archive list/extract/info/find</c>
///     and the asset browser see its members the way they see any other container's.
///     <para>
///         A lump is small (11 KB and 88 KB on retail) and its whole point is that the payloads
///         tile it, so <see cref="DawnstarLumpArchive" /> keeps the file in memory and this wraps
///         it — no memory map, unlike the sibling <see cref="RedguardRobBackend" /> whose archives
///         run to megabytes. Immutable after construction and free of per-instance mutable state,
///         so every member is safe for unsynchronised concurrent reads.
///     </para>
///     <para>
///         Members are flat: a lump has no folder tree and every name already carries its
///         extension (<c>charin.dat</c>, <c>ban_male_body.png</c>), so the virtual path is the name
///         itself. Nothing in a lump is ever compressed.
///     </para>
/// </summary>
internal sealed class LmpArchiveBackend : IArchiveBackend
{
    private readonly DawnstarLumpArchive _archive;

    public LmpArchiveBackend(DawnstarLumpArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        _archive = archive;
    }

    public string FormatName => "LMP (Dawnstar)";

    public string PlatformLabel => "J2ME";

    public int TotalFiles => _archive.Entries.Length;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_archive.Entries.Length);
        foreach (var entry in _archive.Entries)
        {
            list.Add(new ArchiveEntry(
                entry.Name,
                string.Empty,
                entry.Name,
                Path.GetExtension(entry.Name).ToLowerInvariant(),
                entry.Length,
                entry.Offset,
                false,
                entry));
        }

        return list;
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not DawnstarLumpEntry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        return _archive.Read(record).ToArray();
    }

    public void Dispose()
    {
        // The lump is a byte array owned by the parsed archive; there is nothing to release.
    }
}
