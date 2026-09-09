using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Compression;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>One block of the installer's compressed stream.</summary>
/// <param name="StreamOffset">Offset of the block's first byte in the concatenated data stream.</param>
/// <param name="DataOffset">Offset of the block's first codec (or stored) byte.</param>
/// <param name="DataLength">Length of the codec input, or 10,000 for a stored block.</param>
/// <param name="UncompressedLength">Bytes the block contributes; 10,000 but for a file's last block.</param>
/// <param name="IsStored">Whether the block is 10,000 raw bytes (a <c>clen</c> of 0xFFFF).</param>
internal sealed record ArenaInstallerBlock(
    long StreamOffset,
    long DataOffset,
    int DataLength,
    int UncompressedLength,
    bool IsStored);

/// <summary>One file the installer writes.</summary>
/// <param name="Index">Position among the directory's file records.</param>
/// <param name="Name">File name as the directory spells it (upper case, 8.3).</param>
/// <param name="Directory">Parent directory name the record carries ("ARENA").</param>
/// <param name="Size">Installed size in bytes.</param>
/// <param name="Blocks">The blocks that reconstruct it, in stream order.</param>
internal sealed record ArenaInstallerEntry(
    int Index,
    string Name,
    string Directory,
    long Size,
    IReadOnlyList<ArenaInstallerBlock> Blocks);

/// <summary>
///     The floppy installer container of TES Arena v1.04 — "Bethesda Softworks Install Utility
///     V2.31" (its <c>INSTALL.EXE</c> also carries the format strings <c>%s*.h1</c> and <c>*.tds</c>).
///     A release is three parts, spread across the disks:
///     <code>
///     ARENA.H1..Hn   ONE directory, split by disk: 96-byte records, concatenated in disk order.
///                    byte 0   record type: 0x08 directory, 0x02 file
///                    +1       13-byte NUL-padded name
///                    +14      13-byte NUL-padded parent directory name
///                    +27..+91 zero on all 107 retail records
///                    +92      LE u32 installed size
///     ARENA.TDS      a single LE u32: the total installed size, i.e. the sum of the file sizes
///                    (the installer's free-space check, "You need at least %ld").
///     ARENA.1..n     ONE byte stream, concatenated in disk order: the files' payloads back to
///                    back in directory order with NO per-file header.
///     </code>
///     A block is <c>[u16 clen][u16 ulen]</c> followed by <c>clen - 2</c> bytes of LZHUF — the
///     repo's <see cref="LzhufCodec" /> (Arena's own type-08 codec) with a FRESH tree and window
///     per block. <c>ulen</c> is 10,000 for a full block and the remainder for a file's last one.
///     <para>
///         ⚠ <c>clen</c> COUNTS the <c>ulen</c> word, so the stride is <c>clen + 2</c>; the natural
///         reading <c>clen + 4</c> loses sync after the first block.
///     </para>
///     <para>
///         ⚠ <c>clen == 0xFFFF</c> marks a STORED block: exactly 10,000 raw bytes follow with NO
///         <c>ulen</c> word (42 blocks on v1.04 — already-compressed MIF payloads and GLOBAL.BSA
///         runs). ⚠ A file whose size is an exact multiple of 10,000 is terminated by an EMPTY
///         block (<c>clen</c> 3 or 4, <c>ulen</c> 0) carrying the encoder's flush bytes, so the
///         walk cannot stop on the byte count alone.
///     </para>
///     <para>
///         ⚑ Measured on the retail v1.04 set 2026-09-07: 107 directory records (1 directory + 106
///         files) whose sizes sum to 20,513,662 — exactly the dword in <c>ARENA.TDS</c> — and 2,115
///         blocks (2,069 LZHUF + 42 stored + 4 empty) consuming exactly 11,083,470 of the
///         11,083,470-byte stream. No block straddles a disk boundary.
///     </para>
///     <para>
///         The volumes may sit in one directory or, as a disk-image dump leaves them, in one
///         directory per floppy; <see cref="ResolveVolumes" /> looks beside the anchor first and
///         then through the sibling directories of the anchor's parent.
///     </para>
/// </summary>
internal sealed class ArenaInstallerArchive
{
    /// <summary>Bytes per directory record.</summary>
    public const int RecordLength = 96;

    /// <summary>Uncompressed bytes in a full block.</summary>
    public const int FullBlockLength = 10000;

    /// <summary><c>clen</c> value marking a stored block.</summary>
    public const int StoredMarker = 0xFFFF;

    /// <summary>Record type of a directory record.</summary>
    public const byte DirectoryRecordType = 0x08;

    /// <summary>Record type of a file record.</summary>
    public const byte FileRecordType = 0x02;

    /// <summary>Highest volume number the resolver will look for.</summary>
    private const int MaxVolumes = 26;

    private ArenaInstallerArchive(
        string filePath,
        IReadOnlyList<string> headerVolumes,
        IReadOnlyList<string> dataVolumes,
        string totalSizePath,
        long totalSize,
        string rootDirectory,
        IReadOnlyList<ArenaInstallerEntry> entries,
        byte[] dataStream,
        long directoryBytes,
        int blockCount,
        int storedBlockCount,
        int emptyBlockCount)
    {
        DirectoryBytes = directoryBytes;
        FilePath = filePath;
        HeaderVolumes = headerVolumes;
        DataVolumes = dataVolumes;
        TotalSizePath = totalSizePath;
        TotalSize = totalSize;
        RootDirectory = rootDirectory;
        Entries = entries;
        DataStream = dataStream;
        BlockCount = blockCount;
        StoredBlockCount = storedBlockCount;
        EmptyBlockCount = emptyBlockCount;
    }

    /// <summary>The anchor the archive was opened from (an <c>ARENA.H1</c> or <c>ARENA.1</c>).</summary>
    public string FilePath { get; }

    /// <summary>The <c>ARENA.Hn</c> volumes, in disk order.</summary>
    public IReadOnlyList<string> HeaderVolumes { get; }

    /// <summary>The <c>ARENA.n</c> volumes, in disk order.</summary>
    public IReadOnlyList<string> DataVolumes { get; }

    /// <summary>Path of the <c>ARENA.TDS</c> total-size file.</summary>
    public string TotalSizePath { get; }

    /// <summary>The size <c>ARENA.TDS</c> declares; equals the sum of the file records' sizes.</summary>
    public long TotalSize { get; }

    /// <summary>Name of the single directory record the release creates ("ARENA").</summary>
    public string RootDirectory { get; }

    /// <summary>Files in directory order.</summary>
    public IReadOnlyList<ArenaInstallerEntry> Entries { get; }

    /// <summary>The concatenated <c>ARENA.n</c> stream, held once for the archive's lifetime.</summary>
    public byte[] DataStream { get; }

    /// <summary>
    ///     Bytes the whole RELEASE occupies: every header volume, every data volume and
    ///     <c>ARENA.TDS</c>. A release is spread over eight files-plus-eight, so the anchor's own
    ///     length (8,160 bytes for <c>ARENA.H1</c>) is not the container's size, and reporting it as
    ///     such is what <c>archive info</c> used to do.
    /// </summary>
    public long ContainerSizeBytes => DirectoryBytes + DataStream.LongLength + 4;

    /// <summary>Total length of the <c>ARENA.Hn</c> volumes, i.e. the split directory.</summary>
    public long DirectoryBytes { get; }

    /// <summary>Total blocks the walk consumed.</summary>
    public int BlockCount { get; }

    /// <summary>Blocks stored raw (<c>clen == 0xFFFF</c>).</summary>
    public int StoredBlockCount { get; }

    /// <summary>Empty terminator blocks (<c>ulen == 0</c>).</summary>
    public int EmptyBlockCount { get; }

    /// <summary>
    ///     True when <paramref name="path" /> anchors a Bethesda Install Utility release. The claim
    ///     is entirely arithmetic — these files have no magic at all — so the probe does the whole
    ///     walk: the split directory must be a whole number of 96-byte records with a plausible
    ///     type/name shape, its file sizes must sum to the dword in <c>ARENA.TDS</c> exactly, and
    ///     the block stream must consume every byte of the concatenated volumes with every file's
    ///     declared size satisfied.
    /// </summary>
    public static bool TryProbe(string path)
    {
        return TryParse(path, out _);
    }

    /// <summary>
    ///     Probe and parse in ONE pass, for the probe chain. <see cref="TryProbe" /> followed by
    ///     <see cref="Parse" /> would read and concatenate the whole 11 MB stream twice and walk its
    ///     2,115 blocks twice, since the probe IS the walk for this magic-less family.
    /// </summary>
    public static bool TryParse(string path, out ArenaInstallerArchive? archive)
    {
        archive = null;
        if (!IsAnchorName(path))
        {
            return false;
        }

        try
        {
            return TryRead(path, out archive) is null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Parses the release, throwing <see cref="InvalidDataException" /> with the reason when it is not one.</summary>
    public static ArenaInstallerArchive Parse(string path)
    {
        var reason = TryRead(path, out var archive);
        if (reason is not null)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' does not anchor a Bethesda Install Utility release: {reason}");
        }

        return archive!;
    }

    /// <summary>
    ///     Whether the file name is one the container can be opened by: the first header volume or
    ///     the first data volume. Every other name is rejected without touching the disk, which is
    ///     what keeps this magic-less family out of the rest of the probe chain's way.
    /// </summary>
    public static bool IsAnchorName(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, "ARENA.H1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "ARENA.1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Finds the volumes of the release <paramref name="anchor" /> belongs to. Looks in the
    ///     anchor's own directory first, then in the sibling directories of its parent, which is how
    ///     a per-floppy image dump lays the eight disks out. Returns the header volumes, data
    ///     volumes and the <c>ARENA.TDS</c> path, or null with a reason.
    /// </summary>
    public static string? ResolveVolumes(
        string anchor, out List<string> headerVolumes, out List<string> dataVolumes, out string totalSizePath)
    {
        headerVolumes = [];
        dataVolumes = [];
        totalSizePath = string.Empty;

        var anchorDirectory = Path.GetDirectoryName(Path.GetFullPath(anchor));
        if (anchorDirectory is null)
        {
            return "the anchor has no containing directory.";
        }

        // The sibling directories are enumerated only when something is NOT beside the anchor: a
        // release copied into one directory then costs a handful of File.Exists calls, where an
        // eager scan would enumerate the anchor's parent — which may be a temp or downloads folder
        // with tens of thousands of entries — before looking at the obvious place.
        List<string>? roots = null;

        List<string> Roots()
        {
            if (roots is not null)
            {
                return roots;
            }

            roots = [anchorDirectory];
            var parent = Path.GetDirectoryName(anchorDirectory);
            if (parent is not null && Directory.Exists(parent))
            {
                foreach (var sibling in Directory.GetDirectories(parent).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.Equals(sibling, anchorDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        roots.Add(sibling);
                    }
                }
            }

            return roots;
        }

        string? Find(string fileName)
        {
            var beside = Path.Combine(anchorDirectory, fileName);
            return File.Exists(beside) ? beside : FindInRoots(Roots(), fileName);
        }

        for (var volume = 1; volume <= MaxVolumes; volume++)
        {
            var header = Find($"ARENA.H{volume}");
            var data = Find($"ARENA.{volume}");
            if (header is null || data is null)
            {
                break;
            }

            headerVolumes.Add(header);
            dataVolumes.Add(data);
        }

        if (headerVolumes.Count == 0)
        {
            return "no ARENA.H1 / ARENA.1 pair was found beside the anchor or in its parent's sibling directories.";
        }

        var tds = Find("ARENA.TDS");
        if (tds is null)
        {
            return "ARENA.TDS is missing, so the directory's size sum cannot be checked against it.";
        }

        totalSizePath = tds;
        return null;
    }

    /// <summary>Finds the entry named <paramref name="name" />, case-insensitively, or null.</summary>
    public ArenaInstallerEntry? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Reconstructs one file from <see cref="DataStream" />.</summary>
    public byte[] Extract(ArenaInstallerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var output = new byte[entry.Size];
        var written = 0;
        foreach (var block in entry.Blocks)
        {
            if (block.UncompressedLength == 0)
            {
                continue;
            }

            var input = DataStream.AsSpan((int)block.DataOffset, block.DataLength);
            if (block.IsStored)
            {
                input.CopyTo(output.AsSpan(written));
                written += block.UncompressedLength;
                continue;
            }

            var plain = LzhufCodec.Decompress(input, block.UncompressedLength);
            plain.CopyTo(output, written);
            written += plain.Length;
        }

        if (written != output.Length)
        {
            throw new InvalidDataException(
                $"'{entry.Name}' decoded to {written} of its declared {output.Length} bytes.");
        }

        return output;
    }

    /// <summary>
    ///     Resolves the volumes, parses the split directory and walks the whole block stream.
    ///     Returns null on success and the reason otherwise, so the probe and the parser share one
    ///     implementation and can never disagree about what is acceptable.
    /// </summary>
    private static string? TryRead(string path, out ArenaInstallerArchive? archive)
    {
        archive = null;
        var reason = ResolveVolumes(path, out var headerVolumes, out var dataVolumes, out var totalSizePath);
        if (reason is not null)
        {
            return reason;
        }

        var totalSizeBytes = File.ReadAllBytes(totalSizePath);
        if (totalSizeBytes.Length != 4)
        {
            return $"ARENA.TDS is {totalSizeBytes.Length} bytes, not the single dword the installer writes.";
        }

        var declaredTotal = BinaryPrimitives.ReadUInt32LittleEndian(totalSizeBytes);

        var directory = Concatenate(headerVolumes);
        if (directory.Length == 0 || directory.Length % RecordLength != 0)
        {
            return
                $"the {headerVolumes.Count} header volume(s) hold {directory.Length} bytes, not a whole number of {RecordLength}-byte records.";
        }

        var rootDirectory = string.Empty;
        var entries = new List<ArenaInstallerEntry>();
        var sizes = new List<long>();
        var names = new List<string>();
        long sum = 0;

        for (var offset = 0; offset < directory.Length; offset += RecordLength)
        {
            var record = directory.AsSpan(offset, RecordLength);
            var type = record[0];
            if (type is not (DirectoryRecordType or FileRecordType))
            {
                return
                    $"the record at {offset} has type 0x{type:X2}; only 0x{DirectoryRecordType:X2} and 0x{FileRecordType:X2} occur.";
            }

            var name = ReadPaddedName(record.Slice(1, 13));
            var parentName = ReadPaddedName(record.Slice(14, 13));
            if (name.Length == 0)
            {
                return $"the record at {offset} has an empty name.";
            }

            foreach (var reserved in record[27..92])
            {
                if (reserved != 0)
                {
                    return $"the record at {offset} has a non-zero byte between its parent name and its size.";
                }
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(record[92..]);
            if (type == DirectoryRecordType)
            {
                if (size != 0)
                {
                    return $"the directory record '{name}' declares a size of {size}.";
                }

                if (rootDirectory.Length == 0)
                {
                    rootDirectory = name;
                }

                continue;
            }

            if (parentName.Length == 0)
            {
                return $"file record '{name}' names no parent directory.";
            }

            names.Add(name);
            sizes.Add(size);
            sum += size;
        }

        if (names.Count == 0)
        {
            return "the directory holds no file records.";
        }

        if (sum != declaredTotal)
        {
            return $"the {names.Count} file records sum to {sum} bytes; ARENA.TDS declares {declaredTotal}.";
        }

        var data = Concatenate(dataVolumes);
        var position = 0;
        var storedBlocks = 0;
        var emptyBlocks = 0;
        var blockCount = 0;

        for (var i = 0; i < names.Count; i++)
        {
            var size = sizes[i];
            var blocks = new List<ArenaInstallerBlock>();
            long produced = 0;

            while (true)
            {
                if (position + 2 > data.Length)
                {
                    return $"the stream ends inside '{names[i]}' ({produced} of {size} bytes decoded).";
                }

                var compressedLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
                if (compressedLength == StoredMarker)
                {
                    if (position + 2 + FullBlockLength > data.Length)
                    {
                        return $"the stored block at {position} runs past the end of the stream.";
                    }

                    blocks.Add(new ArenaInstallerBlock(position, position + 2, FullBlockLength, FullBlockLength, true));
                    position += 2 + FullBlockLength;
                    produced += FullBlockLength;
                    storedBlocks++;
                    blockCount++;
                }
                else
                {
                    if (compressedLength < 2 || position + 2 + compressedLength > data.Length)
                    {
                        return $"the block at {position} declares a compressed length of {compressedLength}.";
                    }

                    var uncompressedLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 2));
                    if (uncompressedLength > FullBlockLength)
                    {
                        return
                            $"the block at {position} declares {uncompressedLength} uncompressed bytes, more than a full block.";
                    }

                    blocks.Add(new ArenaInstallerBlock(
                        position, position + 4, compressedLength - 2, uncompressedLength, false));
                    position += compressedLength + 2;
                    produced += uncompressedLength;
                    blockCount++;

                    if (uncompressedLength == 0)
                    {
                        emptyBlocks++;
                        if (produced != size)
                        {
                            return $"'{names[i]}' hit an empty terminator block at {produced} of its {size} bytes.";
                        }

                        break;
                    }
                }

                if (produced > size)
                {
                    return $"'{names[i]}' overshot its {size}-byte size by {produced - size} bytes.";
                }

                // The ONLY place the walk may stop on the byte count: a file whose size is an exact
                // multiple of 10,000 still has an empty terminator block to consume, and that block
                // is what breaks the loop above. Checking this at the TOP as well would be dead
                // code — the first iteration has produced == 0, and 0 % 10,000 is 0.
                if (produced == size && size % FullBlockLength != 0)
                {
                    break;
                }
            }

            entries.Add(new ArenaInstallerEntry(i, names[i], rootDirectory, size, blocks));
        }

        if (position != data.Length)
        {
            return $"the walk consumed {position} of the stream's {data.Length} bytes.";
        }

        archive = new ArenaInstallerArchive(
            path, headerVolumes, dataVolumes, totalSizePath, declaredTotal, rootDirectory,
            entries, data, directory.LongLength, blockCount, storedBlocks, emptyBlocks);
        return null;
    }

    private static string? FindInRoots(IEnumerable<string> roots, string fileName)
    {
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static byte[] Concatenate(List<string> volumes)
    {
        long total = 0;
        var parts = new byte[volumes.Count][];
        for (var i = 0; i < volumes.Count; i++)
        {
            parts[i] = File.ReadAllBytes(volumes[i]);
            total += parts[i].Length;
        }

        var result = new byte[total];
        var written = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, written);
            written += part.Length;
        }

        return result;
    }

    /// <summary>Reads a NUL-PADDED fixed-width name field.</summary>
    private static string ReadPaddedName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]).Trim();
    }
}
