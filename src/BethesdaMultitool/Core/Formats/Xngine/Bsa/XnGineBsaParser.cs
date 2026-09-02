using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Xngine.Bsa;

/// <summary>
///     The XnGine BSA used by Daggerfall, Battlespire and Redguard — the successor to Arena's
///     container and still nothing like the Gamebryo one. A 4-byte header (u16 entry count, u16
///     record type) is followed by every payload concatenated from offset 4, with the directory at
///     END of file.
///     <para>
///         Two directory forms, chosen by the header's type word. Name records (0x0100) are 18
///         bytes: a 12-byte NUL-padded name, a u16 compression flag (0x0100 marks Battlespire's
///         per-entry LZSS; every Daggerfall entry has 0), and a signed 32-bit size. Number
///         records (0x0200) are 8 bytes: a 32-bit id and a signed 32-bit size — ARCH3D.BSA uses
///         these because its 10,251 meshes are referenced by number, not name. Entry offsets are
///         implicit in both forms: a running sum from offset 4 in directory order.
///     </para>
///     <para>
///         Layout verified against all five retail Daggerfall archives (MONSTER, MIDI, MAPS,
///         BLOCKS and ARCH3D) and the eight Battlespire XnGine containers (3D.BS6, 3D.BSA,
///         BS6.BSA, BSI.BSA, FLC.BSA, TXT.BSA and the numbered SPIRE.SND), each of which tiles
///         exactly. Battlespire's DMKA/DMOG/DMZR.BS6 carry a different type word (0x4C52) and are
///         NOT this container — the probe correctly refuses them.
///     </para>
/// </summary>
internal static class XnGineBsaParser
{
    /// <summary>Bytes of header before the first payload.</summary>
    public const int HeaderLength = 4;

    /// <summary>Directory record type whose entries are named.</summary>
    public const ushort NameRecordType = 0x0100;

    /// <summary>Directory record type whose entries are numbered.</summary>
    public const ushort NumberRecordType = 0x0200;

    /// <summary>Bytes in a name directory record.</summary>
    public const int NameRecordLength = 18;

    /// <summary>Bytes in a number directory record.</summary>
    public const int NumberRecordLength = 8;

    /// <summary>
    ///     Bytes reserved for a name inside a name record. The two bytes after it are a u16
    ///     compression flag — 0x0100 for Battlespire's per-entry LZSS, 0x0000 otherwise — not
    ///     name padding, even though every uncompressed archive makes them look like it.
    /// </summary>
    private const int NameBytes = 12;

    /// <summary>Flag value marking an entry as Battlespire-LZSS compressed.</summary>
    public const ushort CompressedFlag = 0x0100;

    /// <summary>
    ///     Exact-arithmetic content probe: the trailing directory's sizes must sum, together with
    ///     the header and the directory itself, to EXACTLY the physical file length, and every
    ///     name must be a plausible NUL-padded DOS name. The type word is the only magic-like
    ///     field and two values are far too weak to claim a file on their own.
    /// </summary>
    public static bool TryProbe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadDirectory(stream) is not null;
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

    /// <summary>Parses the container, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static XnGineBsaArchive Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return TryReadDirectory(stream) ??
               throw new InvalidDataException(
                   $"'{Path.GetFileName(path)}' is not an XnGine BSA: the trailing directory does not tile the file.");
    }

    private static XnGineBsaArchive? TryReadDirectory(FileStream stream)
    {
        var fileLength = stream.Length;
        if (fileLength < HeaderLength + NumberRecordLength)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        stream.Position = 0;
        stream.ReadExactly(header);

        int count = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var recordType = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        if (count == 0)
        {
            return null;
        }

        var recordLength = recordType switch
        {
            NameRecordType => NameRecordLength,
            NumberRecordType => NumberRecordLength,
            _ => 0
        };

        if (recordLength == 0)
        {
            return null;
        }

        var directoryLength = (long)count * recordLength;
        var directoryOffset = fileLength - directoryLength;
        if (directoryOffset < HeaderLength)
        {
            return null;
        }

        var directory = new byte[directoryLength];
        stream.Position = directoryOffset;
        stream.ReadExactly(directory);

        var entries = new List<XnGineBsaEntry>(count);
        long runningOffset = HeaderLength;
        for (var i = 0; i < count; i++)
        {
            var record = directory.AsSpan(i * recordLength, recordLength);

            string name;
            int size;
            uint? id = null;
            var compressed = false;
            if (recordType == NameRecordType)
            {
                var parsed = ReadDosName(record[..NameBytes]);
                if (parsed is null)
                {
                    return null;
                }

                var flag = BinaryPrimitives.ReadUInt16LittleEndian(record[NameBytes..]);
                if (flag is not (0 or CompressedFlag))
                {
                    // Only two flag values exist across every retail archive of the three games;
                    // anything else fails the probe rather than being carried as mystery state.
                    return null;
                }

                name = parsed;
                compressed = flag == CompressedFlag;
                size = BinaryPrimitives.ReadInt32LittleEndian(record[(NameBytes + 2)..]);
            }
            else
            {
                var number = BinaryPrimitives.ReadUInt32LittleEndian(record);
                id = number;
                name = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                size = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            }

            // Sizes are signed in the format; a negative one is corruption, not a flag.
            if (size < 0 || runningOffset + size > directoryOffset)
            {
                return null;
            }

            entries.Add(new XnGineBsaEntry(name, id, runningOffset, size, compressed));
            runningOffset += size;
        }

        // The exact-tiling gate: payloads must end precisely where the directory starts.
        return runningOffset == directoryOffset
            ? new XnGineBsaArchive(stream.Name, recordType, entries)
            : null;
    }

    /// <summary>
    ///     A name must be 1..12 printable-ASCII bytes followed only by NUL padding (a full
    ///     12-character name has no terminator) — anything else disqualifies the whole probe,
    ///     which is most of the defense for a format whose only magic is a two-valued type word.
    /// </summary>
    private static string? ReadDosName(ReadOnlySpan<byte> raw)
    {
        var length = raw.IndexOf((byte)0);
        if (length < 0)
        {
            length = raw.Length;
        }

        if (length == 0)
        {
            return null;
        }

        for (var i = 0; i < length; i++)
        {
            if (raw[i] < 0x20 || raw[i] > 0x7E)
            {
                return null;
            }
        }

        for (var i = length; i < raw.Length; i++)
        {
            if (raw[i] != 0)
            {
                return null;
            }
        }

        return Encoding.ASCII.GetString(raw[..length]);
    }
}

/// <summary>A parsed XnGine BSA: the source path, its directory form, and its entries in file order.</summary>
internal sealed class XnGineBsaArchive
{
    public XnGineBsaArchive(string filePath, ushort recordType, IReadOnlyList<XnGineBsaEntry> entries)
    {
        FilePath = filePath;
        RecordType = recordType;
        Entries = entries;
    }

    public string FilePath { get; }

    /// <summary>The header's record type — <c>0x0100</c> named or <c>0x0200</c> numbered.</summary>
    public ushort RecordType { get; }

    /// <summary>True when the directory identifies entries by number rather than name.</summary>
    public bool IsNumbered => RecordType == XnGineBsaParser.NumberRecordType;

    public IReadOnlyList<XnGineBsaEntry> Entries { get; }
}

/// <summary>
///     One XnGine BSA directory entry. <see cref="Id" /> is set only for numbered archives, where
///     it is the record's real identity and <see cref="Name" /> is that number rendered as text.
///     <see cref="Size" /> is the STORED size — for a compressed entry that is the compressed
///     payload length, and the decompressed size is not recorded anywhere in the archive.
/// </summary>
internal readonly record struct XnGineBsaEntry(string Name, uint? Id, long Offset, int Size, bool Compressed);
