using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Compression;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>One packed stream: a whole destination file, split into fixed-size compressed blocks.</summary>
/// <param name="Index">Position in the container's entry table.</param>
/// <param name="Name">Destination file name, e.g. <c>ARCH3D.BSA</c>.</param>
/// <param name="UncompressedSize">Size of the file the blocks reconstruct.</param>
/// <param name="FirstBlockOffset">File offset of the entry's first block header.</param>
/// <param name="Blocks">The entry's blocks, in stream order.</param>
internal sealed record DaggerfallPackedEntry(
    int Index,
    string Name,
    long UncompressedSize,
    long FirstBlockOffset,
    IReadOnlyList<DaggerfallPackedBlock> Blocks);

/// <summary>One compressed block: a 36-byte header and a DCL stream of exactly <paramref name="CompressedSize" /> bytes.</summary>
/// <param name="HeaderOffset">File offset of the block header.</param>
/// <param name="UncompressedSize">Bytes this block contributes; 0x40000 but for a stream's last block.</param>
/// <param name="CompressedSize">Length of the DCL stream that follows the header.</param>
/// <param name="Tag">The header's one per-block dword, purpose unknown (see the type remarks).</param>
internal sealed record DaggerfallPackedBlock(
    long HeaderOffset,
    int UncompressedSize,
    int CompressedSize,
    uint Tag)
{
    /// <summary>File offset of the block's first compressed byte.</summary>
    public long DataOffset => HeaderOffset + DaggerfallPackedArchive.BlockHeaderLength;
}

/// <summary>
///     <c>ARENA2\PACKED.DAT</c> from the 1996 Daggerfall CD-ROM — the container the installer
///     unpacks into <c>ARENA2\ARCH3D.BSA</c> and <c>ARENA2\DAGGER.SND</c>, which the disc carries
///     in no other form (the <c>DATA\DAG_*.LST</c> manifests spell out both mappings). Everything
///     is little-endian.
///     <code>
///     u32 entryTableOffset          file offset of the entry table
///     u32 targetDirectoryOffset     file offset of the 60-byte destination directory name
///     blocks                        from offset 8, entry by entry in table order
///     entry[n]                      25 bytes: u32 uncompressedSize, u32 zero,
///                                   13-byte NUL-padded name, u32 firstBlockOffset
///     directory name                60 bytes, NUL-padded ("ARENA2"), ending exactly at EOF
///     </code>
///     A block is a 36-byte header — <c>0x00491038</c>, <c>0x004D2038</c>, uncompressed size,
///     compressed size, compressed size again, uncompressed size again, <c>0x00080000</c>, a
///     per-block dword, <c>0x00549754</c> — followed by <c>compressedSize</c> bytes of PKWARE DCL
///     ("implode") stream. The three constant dwords and the doubled sizes are a DOS buffer struct
///     the installer dumped verbatim; the per-block dword is the only field that varies and its
///     meaning is not established. Blocks carry 0x40000 bytes each, the last of a stream the
///     remainder, and each stream's blocks tile with no gap up to the next entry's first block
///     (the last entry's up to the entry table).
///     <para>
///         ⚑ Measured on the retail disc 2026-09-07: 104 + 30 blocks reconstruct 27,143,532 and
///         7,661,766 bytes whose MD5s are <c>26d3e935…</c> and <c>37c10bc7…</c> — byte-identical to
///         the Steam install's loose <c>ARCH3D.BSA</c> and <c>DAGGER.SND</c>. That is the proof the
///         codec is DCL: the container tiling alone was already exact while the payload was still
///         undecoded.
///     </para>
///     <para>
///         ⚠⚠ The DCL stream starts at header +36 and is <c>compressedSize</c> bytes long TO THE
///         BYTE: every block closes on the codec's own end-of-stream code, whose 16 bits (the match
///         flag, a 7-bit length symbol and 8 extra bits) close the payload's LAST byte — byte
///         aligned on 14 of the 134 retail blocks and spanning the last THREE bytes on the other
///         120, which leaves 0 to 7 unused bits in the final byte (0 on 14 blocks, 1 on 9, 2 on 18,
///         3 on 24, 4 on 22, 5 on 14, 6 on 15, 7 on 18 — 134 in total; measured 2026-09-07).
///         ⛔ Two earlier notes here were WRONG about that: the first said the format carried no end
///         code at all and that "the encoder's flush" left two declared bytes unread, which was an
///         artefact of stopping the decode the instant the block's declared uncompressed size was
///         reached; the second, correcting it, said the end code "consumes the last two bytes
///         exactly", which holds on only 14 of the 134 blocks. What IS true on 134 of 134 is that
///         decoding one symbol past the declared size hits the end code, emits no further output,
///         and leaves 0 of the declared compressed bytes unread. <see cref="Extract" /> therefore
///         checks it: the compressed size is VERIFIED from the payload rather than merely bounding
///         it.
///     </para>
///     <para>
///         ⚠ An earlier reading of the container placed an "8-byte trailer" after the payload and
///         started the stream 8 bytes into it. Its tiling is identical (<c>36 + csize</c> ==
///         <c>28 + csize + 8</c>), so the container arithmetic could not discriminate the two; only
///         the payload can. Taken LITERALLY — a DCL stream at header +28, <c>csize</c> bytes long —
///         it decodes NOTHING on 134 of 134 blocks, because the bytes at +28 are the per-block dword
///         and fail the DCL header's literal-flag/dictionary-exponent check immediately. The only
///         variant that starves late, as an earlier version of this note described, is a stream at
///         +36 truncated to <c>csize - 8</c> bytes: that falls 5 to 1,037 bytes short of the block's
///         uncompressed size (median 23.5, 0 of 134 blocks completing).
///     </para>
/// </summary>
internal sealed class DaggerfallPackedArchive
{
    /// <summary>Bytes per block header.</summary>
    public const int BlockHeaderLength = 36;

    /// <summary>Bytes per entry-table record.</summary>
    public const int EntryLength = 25;

    /// <summary>Length of the NUL-padded destination directory name that closes the file.</summary>
    public const int DirectoryNameLength = 60;

    /// <summary>Uncompressed bytes per block, but for the last block of a stream.</summary>
    public const int BlockSize = 0x40000;

    /// <summary>File offset of the first block; the two header dwords precede it.</summary>
    public const int FirstBlockOffset = 8;

    /// <summary>First constant dword of a block header — a DOS buffer pointer, not an offset.</summary>
    private const uint HeaderConstant0 = 0x00491038;

    /// <summary>Second constant dword of a block header.</summary>
    private const uint HeaderConstant1 = 0x004D2038;

    /// <summary>Seventh dword of a block header: 0x80000, twice the block size.</summary>
    private const uint HeaderConstant6 = 0x00080000;

    /// <summary>Ninth dword of a block header — constant on all 134 retail blocks.</summary>
    private const uint HeaderConstant8 = 0x00549754;

    /// <summary>Guard against a junk header making us allocate an absurd entry table.</summary>
    private const int MaxEntries = 4096;

    private DaggerfallPackedArchive(
        string filePath, string targetDirectory, IReadOnlyList<DaggerfallPackedEntry> entries)
    {
        FilePath = filePath;
        TargetDirectory = targetDirectory;
        Entries = entries;
    }

    /// <summary>Path the container was opened from.</summary>
    public string FilePath { get; }

    /// <summary>The destination directory the installer writes into ("ARENA2" on the retail disc).</summary>
    public string TargetDirectory { get; }

    /// <summary>Entries in table order.</summary>
    public IReadOnlyList<DaggerfallPackedEntry> Entries { get; }

    /// <summary>
    ///     True when <paramref name="path" /> is a Daggerfall <c>PACKED.DAT</c>. The probe is the
    ///     tiling: the entry table must be a whole number of records ending where the 60-byte
    ///     directory name begins, that name must end exactly at EOF, and every entry's block chain
    ///     must start where the previous one ended, satisfy its header constants, and sum to the
    ///     entry's declared size. Nothing about the payload is decoded — the arithmetic alone is
    ///     what accepts or rejects the file.
    /// </summary>
    public static bool TryProbe(string path)
    {
        return TryParse(path, out _);
    }

    /// <summary>
    ///     Probe and parse in ONE pass, for the probe chain: <see cref="TryProbe" /> followed by
    ///     <see cref="Parse" /> reads every block header twice, which this avoids.
    /// </summary>
    public static bool TryParse(string path, out DaggerfallPackedArchive? archive)
    {
        archive = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryRead(stream, path, out archive) is null;
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

    /// <summary>Parses the container, throwing <see cref="InvalidDataException" /> with the reason when it is not one.</summary>
    public static DaggerfallPackedArchive Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reason = TryRead(stream, path, out var archive);
        if (reason is not null)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a Daggerfall PACKED.DAT: {reason}");
        }

        return archive!;
    }

    /// <summary>Finds the entry named <paramref name="name" />, case-insensitively, or null.</summary>
    public DaggerfallPackedEntry? Find(string name)
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

    /// <summary>
    ///     Reconstructs one entry by decoding its blocks in order. <paramref name="read" /> supplies
    ///     the compressed bytes so callers can hand over a memory-mapped view instead of a stream.
    ///     <para>
    ///         Each block must decode to its declared uncompressed size AND consume its declared
    ///         compressed size exactly, ending on the codec's end-of-stream code. Both halves of the
    ///         block header are then checked against the payload rather than assumed — the second
    ///         check is what a wrong framing (the "8-byte trailer" reading, say) fails on.
    ///     </para>
    /// </summary>
    public static byte[] Extract(DaggerfallPackedEntry entry, Func<long, int, byte[]> read)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(read);

        var output = new byte[entry.UncompressedSize];
        var written = 0;
        foreach (var block in entry.Blocks)
        {
            var compressed = read(block.DataOffset, block.CompressedSize);
            var plain = PkwareDclDecoder.Decompress(compressed, block.UncompressedSize, out var consumed);
            if (consumed != block.CompressedSize)
            {
                throw new InvalidDataException(
                    $"the block at {block.HeaderOffset} decoded its {block.UncompressedSize} declared bytes " +
                    $"from {consumed} of the {block.CompressedSize} compressed bytes its header declares.");
            }

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
    ///     Reads the header, entry table and every block header. Returns null on success and the
    ///     reason the file is not a container otherwise, so the probe and the parser share one
    ///     implementation and can never disagree.
    /// </summary>
    private static string? TryRead(FileStream stream, string path, out DaggerfallPackedArchive? archive)
    {
        archive = null;
        var length = stream.Length;
        if (length < FirstBlockOffset + EntryLength + DirectoryNameLength)
        {
            return $"the file is {length} bytes, shorter than a header, one entry and the directory name.";
        }

        Span<byte> head = stackalloc byte[FirstBlockOffset];
        stream.Position = 0;
        stream.ReadExactly(head);
        var entryTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(head);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);

        if (directoryOffset + (long)DirectoryNameLength != length)
        {
            return
                $"the {DirectoryNameLength}-byte directory name at {directoryOffset} does not end at the {length}-byte EOF.";
        }

        if (entryTableOffset <= FirstBlockOffset || entryTableOffset > directoryOffset)
        {
            return
                $"the entry table offset {entryTableOffset} is not between the first block and the directory name at {directoryOffset}.";
        }

        var tableBytes = directoryOffset - entryTableOffset;
        if (tableBytes % EntryLength != 0)
        {
            return $"the entry table is {tableBytes} bytes, not a whole number of {EntryLength}-byte records.";
        }

        var count = (int)(tableBytes / EntryLength);
        if (count is 0 or > MaxEntries)
        {
            return $"the entry table holds {count} records.";
        }

        var table = new byte[tableBytes];
        stream.Position = entryTableOffset;
        stream.ReadExactly(table);

        var nameField = new byte[DirectoryNameLength];
        stream.Position = directoryOffset;
        stream.ReadExactly(nameField);
        var targetDirectory = ReadPaddedName(nameField);
        if (targetDirectory.Length == 0)
        {
            return "the destination directory name is empty.";
        }

        var entries = new List<DaggerfallPackedEntry>(count);
        long expectedStart = FirstBlockOffset;
        Span<byte> blockHeader = stackalloc byte[BlockHeaderLength];

        for (var i = 0; i < count; i++)
        {
            var record = table.AsSpan(i * EntryLength, EntryLength);
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var reserved = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var name = ReadPaddedName(record.Slice(8, 13));
            var firstBlock = BinaryPrimitives.ReadUInt32LittleEndian(record[21..]);

            if (reserved != 0)
            {
                return $"entry {i}'s second dword is 0x{reserved:X8}; it is zero in every retail record.";
            }

            if (name.Length == 0)
            {
                return $"entry {i} has an empty name.";
            }

            if (firstBlock != expectedStart)
            {
                return $"entry {i} ('{name}') starts at {firstBlock}; the previous blocks end at {expectedStart}.";
            }

            if (uncompressedSize == 0)
            {
                return $"entry {i} ('{name}') declares a zero-byte file.";
            }

            var limit = i + 1 < count
                ? BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan((i + 1) * EntryLength + 21, 4))
                : entryTableOffset;

            var blocks = new List<DaggerfallPackedBlock>();
            long produced = 0;
            var offset = (long)firstBlock;
            while (produced < uncompressedSize)
            {
                if (offset + BlockHeaderLength > limit)
                {
                    return
                        $"entry {i} ('{name}') runs out of room for a block header at {offset} (its stream ends at {limit}).";
                }

                stream.Position = offset;
                stream.ReadExactly(blockHeader);
                var constant0 = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
                var constant1 = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[4..]);
                var plainSize = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[8..]);
                var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[12..]);
                var packedSizeAgain = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[16..]);
                var plainSizeAgain = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[20..]);
                var constant6 = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[24..]);
                var tag = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[28..]);
                var constant8 = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[32..]);

                if (constant0 != HeaderConstant0 || constant1 != HeaderConstant1 ||
                    constant6 != HeaderConstant6 || constant8 != HeaderConstant8)
                {
                    return
                        $"the block header at {offset} carries 0x{constant0:X8}/0x{constant1:X8}/0x{constant6:X8}/0x{constant8:X8}, " +
                        $"not the container's 0x{HeaderConstant0:X8}/0x{HeaderConstant1:X8}/0x{HeaderConstant6:X8}/0x{HeaderConstant8:X8}.";
                }

                if (plainSize != plainSizeAgain || packedSize != packedSizeAgain)
                {
                    return
                        $"the block header at {offset} disagrees with itself: sizes {plainSize}/{plainSizeAgain} and {packedSize}/{packedSizeAgain}.";
                }

                if (plainSize is 0 or > BlockSize || packedSize == 0)
                {
                    return
                        $"the block at {offset} declares {plainSize} uncompressed and {packedSize} compressed bytes.";
                }

                var remaining = uncompressedSize - produced;
                var expectedPlain = remaining >= BlockSize ? BlockSize : remaining;
                if (plainSize != expectedPlain)
                {
                    return
                        $"the block at {offset} declares {plainSize} uncompressed bytes; {expectedPlain} remain of '{name}'.";
                }

                if (offset + BlockHeaderLength + packedSize > limit)
                {
                    return $"the block at {offset} overruns entry {i}'s stream, which ends at {limit}.";
                }

                blocks.Add(new DaggerfallPackedBlock(offset, (int)plainSize, (int)packedSize, tag));
                produced += plainSize;
                offset += BlockHeaderLength + packedSize;
            }

            if (offset != limit)
            {
                return
                    $"entry {i} ('{name}') decodes to its declared size but its blocks end at {offset}, not {limit}.";
            }

            entries.Add(new DaggerfallPackedEntry(i, name, uncompressedSize, firstBlock, blocks));
            expectedStart = offset;
        }

        if (expectedStart != entryTableOffset)
        {
            return $"the block area ends at {expectedStart}, not on the entry table at {entryTableOffset}.";
        }

        archive = new DaggerfallPackedArchive(path, targetDirectory, entries);
        return null;
    }

    /// <summary>Reads a NUL-PADDED fixed-width field (the container pads; it does not terminate-and-run-on).</summary>
    private static string ReadPaddedName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]).Trim();
    }
}
