using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Daggerfall;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     The Daggerfall CD's <c>ARENA2\PACKED.DAT</c> behind the backend seam: two PKWARE-DCL-packed
///     files (<c>ARCH3D.BSA</c> and <c>DAGGER.SND</c>) the disc carries in no other form. Immutable
///     after open, so the <see cref="IArchiveBackend.MarkShared" /> default no-op is honest; reads
///     go through one long-lived memory-mapped accessor whose positioned reads are safe for
///     unsynchronised concurrent use, per the <c>Core/Vfs</c> contract.
///     <para>
///         Entries present under the container's own destination directory (<c>ARENA2/…</c>), which
///         is where the installer writes them and what the disc's <c>DATA\DAG_*.LST</c> manifests
///         name, so an extraction reproduces the installed tree rather than a flat pair of files.
///     </para>
/// </summary>
internal sealed class DaggerfallPackedBackend : IArchiveBackend
{
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly DaggerfallPackedArchive _archive;
    private readonly MemoryMappedFile _mmf;

    public DaggerfallPackedBackend(DaggerfallPackedArchive archive)
    {
        _archive = archive;
        _mmf = MemoryMappedFile.CreateFromFile(
            archive.FilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public string FormatName => "PACKED.DAT (Daggerfall)";

    public string PlatformLabel => "DOS";

    public int TotalFiles => _archive.Entries.Count;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var folder = _archive.TargetDirectory;
        var list = new List<ArchiveEntry>(_archive.Entries.Count);
        foreach (var entry in _archive.Entries)
        {
            var extension = Path.GetExtension(entry.Name);
            list.Add(new ArchiveEntry(
                $"{folder}/{entry.Name}",
                folder,
                entry.Name,
                string.IsNullOrEmpty(extension) ? string.Empty : extension.ToLowerInvariant(),
                entry.UncompressedSize,
                entry.FirstBlockOffset,
                true,
                entry));
        }

        return list;
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not DaggerfallPackedEntry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        return DaggerfallPackedArchive.Extract(record, ReadRaw);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }

    private byte[] ReadRaw(long offset, int count)
    {
        var bytes = new byte[count];
        var read = _accessor.ReadArray(offset, bytes, 0, count);
        if (read != count)
        {
            throw new InvalidDataException(
                $"PACKED.DAT block at {offset} is truncated: read {read} of {count} bytes.");
        }

        return bytes;
    }
}
