using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.OblivionPsp;

/// <summary>
///     The single data pack of the cancelled PSP Oblivion (<c>PSP_GAME\USRDIR\GR.ARC</c>): a flat,
///     uncompressed, name-indexed archive. Layout, measured across all seven staged betas
///     (June 2006 through April 2007 plus a community repack of the February 2007 disc) and
///     verified to tile every byte of every file:
///     <code>
///     magic       "A2.0"                    present in six of seven packs; ABSENT in June 2006
///     u32 count                             record count (64..139 on retail)
///     u32 dataStart                         file offset of the first payload byte
///     u32 nameTableOffset                   nameTableOffset + nameTableSize == file length, exactly
///     u32 nameTableSize
///     record[count]                         16 bytes: u32 nameOffset, u32 dataOffset, u32 dataSize, u32 zero
///     payloads                              32-byte aligned, in record order, no gaps but the padding
///     name table                            NUL-terminated Latin-1 strings, ending exactly at EOF
///     </code>
///     <para>
///         Everything is little-endian. That is proved rather than assumed: most payloads are
///         RenderWare streams of 12-byte <c>{type, size, libraryID}</c> chunk headers, and walking
///         that chain little-endian lands exactly on the payload end for 685 entries across the
///         seven packs while the big-endian walk lands on zero.
///     </para>
///     <para>
///         ⚠ <b>The June 2006 revision stores <c>dataOffset</c> RELATIVE to <c>dataStart</c></b>
///         where every tagged pack stores an absolute file offset. Reading it absolutely places its
///         first payload on top of its own header. The reason is visible in the header: June's
///         <c>dataStart</c> is the raw record-table end (1,040 = 16 + 64 x 16), which is not
///         32-aligned, while every <c>A2.0</c> pack aligns it — once the data area is itself
///         aligned the two conventions coincide and the format switched to absolute. Alignment is
///         therefore applied in RECORD space and the base added afterwards; aligning the absolute
///         offset instead is wrong for June by 16 bytes.
///     </para>
///     <para>
///         Nothing in the container is compressed and there is no type or flags field — the fourth
///         dword of every one of the 829 retail records is zero.
///     </para>
/// </summary>
internal sealed class OblivionPspArchive
{
    /// <summary>The format-revision tag the later packs carry. Six of seven retail packs have it.</summary>
    public static readonly byte[] Magic = "A2.0"u8.ToArray();

    /// <summary>Bytes per record table entry.</summary>
    public const int RecordLength = 16;

    /// <summary>Header length without the tag: four dwords.</summary>
    public const int UntaggedHeaderLength = 16;

    /// <summary>Header length with the tag.</summary>
    public const int TaggedHeaderLength = UntaggedHeaderLength + 4;

    /// <summary>Payload alignment, in bytes. Not a 2,048-byte disc sector — that reading is refuted.</summary>
    public const int PayloadAlignment = 32;

    /// <summary>Upper bound on a plausible record count, so a junk header cannot make us allocate.</summary>
    private const int MaxEntries = 1 << 16;

    private OblivionPspArchive(
        string filePath, bool isTagged, IReadOnlyList<OblivionPspArchiveEntry> entries,
        long dataStart, long nameTableOffset, long nameTableSize)
    {
        FilePath = filePath;
        IsTagged = isTagged;
        Entries = entries;
        DataStart = dataStart;
        NameTableOffset = nameTableOffset;
        NameTableSize = nameTableSize;
    }

    /// <summary>Path the pack was opened from.</summary>
    public string FilePath { get; }

    /// <summary>
    ///     Whether the pack carries the <c>A2.0</c> tag. False only for the June 2006 revision,
    ///     which is also the one storing relative payload offsets.
    /// </summary>
    public bool IsTagged { get; }

    /// <summary>Records in table order, which is the pack's own uppercase-name ordering.</summary>
    public IReadOnlyList<OblivionPspArchiveEntry> Entries { get; }

    /// <summary>File offset of the first payload byte.</summary>
    public long DataStart { get; }

    /// <summary>File offset of the name string table.</summary>
    public long NameTableOffset { get; }

    /// <summary>Byte length of the name string table; it ends exactly at EOF.</summary>
    public long NameTableSize { get; }

    /// <summary>Rounds <paramref name="value" /> up to the next <see cref="PayloadAlignment" /> boundary.</summary>
    public static long Align(long value)
    {
        return (value + PayloadAlignment - 1) & ~((long)PayloadAlignment - 1);
    }

    /// <summary>
    ///     True when <paramref name="path" /> is a <c>GR.ARC</c> pack. The probe is exact: the
    ///     record table must end where the header says the payloads start, the payloads must tile
    ///     with only alignment padding between them, the last one must end on the name table, and
    ///     the name table must end on EOF. The tag is not required, because the earliest build has
    ///     none — so the arithmetic is the whole of the claim.
    /// </summary>
    public static bool TryProbe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryRead(stream, path, out _) is null;
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

    /// <summary>Parses the pack, throwing <see cref="InvalidDataException" /> with the reason when it is not one.</summary>
    public static OblivionPspArchive Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reason = TryRead(stream, path, out var archive);
        if (reason is not null)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not an Oblivion PSP GR.ARC pack: {reason}");
        }

        return archive!;
    }

    /// <summary>Finds the entry named <paramref name="name" />, case-insensitively, or null.</summary>
    public OblivionPspArchiveEntry? Find(string name)
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
    ///     Reads the header, record table and name table. Returns null on success and the reason
    ///     the file is not a pack otherwise, so the probe and the parser share one implementation
    ///     and can never disagree about what is acceptable.
    /// </summary>
    private static string? TryRead(FileStream stream, string path, out OblivionPspArchive? archive)
    {
        archive = null;
        var length = stream.Length;
        if (length < TaggedHeaderLength)
        {
            return $"the file is {length} bytes, shorter than a header.";
        }

        Span<byte> head = stackalloc byte[TaggedHeaderLength];
        stream.Position = 0;
        stream.ReadExactly(head);

        var isTagged = head[..4].SequenceEqual(Magic);
        var headerLength = isTagged ? TaggedHeaderLength : UntaggedHeaderLength;
        var fields = isTagged ? head[4..] : head[..UntaggedHeaderLength];

        var count = BinaryPrimitives.ReadUInt32LittleEndian(fields);
        var dataStart = BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
        var nameTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]);
        var nameTableSize = BinaryPrimitives.ReadUInt32LittleEndian(fields[12..]);

        if (count is 0 or > MaxEntries)
        {
            return $"the header declares {count} entries.";
        }

        // The name table closes the file exactly — no trailer, no checksum, no padding after it.
        if ((long)nameTableOffset + nameTableSize != length)
        {
            return $"the name table ({nameTableOffset} + {nameTableSize}) does not end at the {length}-byte EOF.";
        }

        var tableEnd = headerLength + ((long)RecordLength * count);
        if (tableEnd > nameTableOffset)
        {
            return $"the {count}-record table ends at {tableEnd}, past the name table at {nameTableOffset}.";
        }

        // A tagged pack aligns its data area; the June revision starts it at the raw table end.
        var expectedDataStart = isTagged ? Align(tableEnd) : tableEnd;
        if (dataStart != expectedDataStart)
        {
            return $"dataStart is {dataStart}; the {count}-record table ends at {tableEnd}, so it should be {expectedDataStart}.";
        }

        var table = new byte[RecordLength * count];
        stream.Position = headerLength;
        stream.ReadExactly(table);

        var names = new byte[nameTableSize];
        stream.Position = nameTableOffset;
        stream.ReadExactly(names);

        // Absolute in a tagged pack, relative to the data area in the June one.
        long payloadBase = isTagged ? 0 : dataStart;
        var entries = new List<OblivionPspArchiveEntry>((int)count);
        long expectedOffset = isTagged ? dataStart : 0;

        for (var i = 0; i < count; i++)
        {
            var record = table.AsSpan(i * RecordLength, RecordLength);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            var reserved = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);

            if (reserved != 0)
            {
                return $"record {i}'s fourth dword is 0x{reserved:X8}; it is zero in every retail record.";
            }

            if (dataOffset != expectedOffset)
            {
                return $"record {i} starts at {dataOffset}; the previous payload and its alignment end at {expectedOffset}.";
            }

            var absolute = payloadBase + dataOffset;
            if (absolute + dataSize > nameTableOffset)
            {
                return $"record {i}'s payload ({dataSize} bytes at {absolute}) runs into the name table at {nameTableOffset}.";
            }

            if (nameOffset >= nameTableSize)
            {
                return $"record {i}'s name offset {nameOffset} is outside the {nameTableSize}-byte name table.";
            }

            var terminator = Array.IndexOf(names, (byte)0, (int)nameOffset);
            if (terminator < 0)
            {
                return $"record {i}'s name at {nameOffset} is not NUL-terminated inside the name table.";
            }

            var name = Encoding.Latin1.GetString(names, (int)nameOffset, terminator - (int)nameOffset);
            entries.Add(new OblivionPspArchiveEntry(i, name, nameOffset, absolute, dataSize));
            expectedOffset = Align(dataOffset + dataSize);
        }

        // Nothing lies between the last payload and the name table but alignment padding.
        if (payloadBase + expectedOffset != nameTableOffset)
        {
            return $"the payload run ends at {payloadBase + expectedOffset}, not on the name table at {nameTableOffset}.";
        }

        archive = new OblivionPspArchive(path, isTagged, entries, dataStart, nameTableOffset, nameTableSize);
        return null;
    }
}
