namespace BethesdaMultitool.Core.Formats.Zip;

/// <summary>
///     One central-directory entry of a PKZIP file, as <see cref="PkZipParser" /> read it. Sizes
///     and the CRC come from the central directory (authoritative even when the local header
///     deferred them to a data descriptor); <see cref="LocalHeaderOffset" /> is where the entry's
///     local header — and, after its name and extra field, its payload — starts.
/// </summary>
public sealed record PkZipEntry(
    string Name,
    ushort Method,
    ushort Flags,
    uint Crc32,
    uint CompressedSize,
    uint UncompressedSize,
    uint LocalHeaderOffset)
{
    /// <summary>Directory placeholder entries end in a slash and carry no payload.</summary>
    public bool IsDirectory => Name.EndsWith('/');

    /// <summary>Method 0: the payload is the file bytes verbatim.</summary>
    public bool IsStored => Method == PkZipParser.MethodStored;

    /// <summary>Method 8: raw DEFLATE (RFC 1951) with no zlib wrapper.</summary>
    public bool IsDeflated => Method == PkZipParser.MethodDeflate;

    /// <summary>General-purpose flag bit 0 — the entry is encrypted and cannot be read here.</summary>
    public bool IsEncrypted => (Flags & PkZipParser.FlagEncrypted) != 0;
}

/// <summary>
///     A parsed PKZIP central directory: the file it came from, its entries in directory order,
///     and the two numbers the exact probe pinned (where the central directory starts and ends).
///     Immutable; payload reads go through <see cref="PkZipParser.Extract" /> with a per-call
///     stream, which is what lets the archive backend honour the lock-free concurrent-read contract.
/// </summary>
public sealed class PkZipArchive
{
    internal PkZipArchive(
        string filePath, IReadOnlyList<PkZipEntry> entries, long centralDirectoryOffset,
        long endOfCentralDirectoryOffset, string comment)
    {
        FilePath = filePath;
        Entries = entries;
        CentralDirectoryOffset = centralDirectoryOffset;
        EndOfCentralDirectoryOffset = endOfCentralDirectoryOffset;
        Comment = comment;
    }

    public string FilePath { get; }

    /// <summary>Every central-directory entry, directories included, in directory order.</summary>
    public IReadOnlyList<PkZipEntry> Entries { get; }

    /// <summary>Byte offset of the first central-directory header; every payload ends at or before it.</summary>
    public long CentralDirectoryOffset { get; }

    /// <summary>Byte offset of the end-of-central-directory record.</summary>
    public long EndOfCentralDirectoryOffset { get; }

    /// <summary>The archive comment (empty for every fixture measured).</summary>
    public string Comment { get; }

    /// <summary>
    ///     True when the archive carries a <c>META-INF/MANIFEST.MF</c> — the Java archive
    ///     convention every J2ME MIDlet (the TES Travels JARs) follows.
    /// </summary>
    public bool IsJavaArchive => Entries.Any(static e =>
        string.Equals(e.Name, "META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
}
