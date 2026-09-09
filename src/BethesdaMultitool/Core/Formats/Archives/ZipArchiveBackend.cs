using BethesdaMultitool.Core.Formats.Zip;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     Plain PKZIP behind the backend seam: the TES Travels J2ME JARs (Stormhold, Dawnstar,
///     Oblivion mobile) and Fallout Tactics' <c>.bos</c> archives. Immutable after open — the
///     directory is parsed once by <see cref="PkZipParser" /> and every extraction opens its own
///     <c>FileStream</c> + <c>DeflateStream</c>, so members are safe for unsynchronised concurrent
///     use (the <c>Core/Vfs</c> contract; <c>System.IO.Compression.ZipArchive</c> is not).
///     <para>
///         Directory placeholder entries are not surfaced: the facade lists files, and folder
///         structure is derived from the paths as the other flat backends do.
///     </para>
/// </summary>
internal sealed class ZipArchiveBackend : IArchiveBackend
{
    private readonly PkZipArchive _archive;
    private readonly List<PkZipEntry> _files;

    public ZipArchiveBackend(PkZipArchive archive)
    {
        _archive = archive;
        _files = archive.Entries.Where(static e => !e.IsDirectory).ToList();
    }

    public string FormatName => _archive.IsJavaArchive ? "JAR (PKZIP)" : "ZIP (PKZIP)";

    public string PlatformLabel => _archive.IsJavaArchive ? "J2ME" : "PC";

    public int TotalFiles => _files.Count;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_files.Count);
        foreach (var entry in _files)
        {
            var fullPath = entry.Name.Replace('/', '\\').TrimStart('\\');
            var slash = fullPath.LastIndexOf('\\');
            var folder = slash < 0 ? string.Empty : fullPath[..slash];
            var name = slash < 0 ? fullPath : fullPath[(slash + 1)..];
            var ext = Path.GetExtension(name);
            list.Add(new ArchiveEntry(
                fullPath,
                folder,
                name,
                string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant(),
                entry.UncompressedSize,
                entry.LocalHeaderOffset,
                !entry.IsStored,
                entry));
        }

        return list;
    }

    public IEnumerable<string> EnumerateFilePaths()
    {
        foreach (var entry in _files)
        {
            yield return entry.Name.Replace('/', '\\').TrimStart('\\');
        }
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not PkZipEntry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        return PkZipParser.Extract(_archive, record);
    }

    public void Dispose()
    {
        // Nothing is held open between calls.
    }
}
