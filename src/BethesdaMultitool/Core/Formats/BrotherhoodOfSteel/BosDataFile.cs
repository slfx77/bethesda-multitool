using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>One record of a <see cref="BosDataFile" />: a hash key and its bytes.</summary>
/// <param name="Index">Position in the directory.</param>
/// <param name="Hash">The record's key. ⚠ The function producing it is NOT established.</param>
/// <param name="Offset">Byte offset of the payload.</param>
/// <param name="Size">Payload length, derived from the next entry's offset (or EOF for the last).</param>
/// <param name="Type">
///     The payload's own leading dword — a small object-class code. Measured 2026-09-06 over all
///     43,006 retail records: 13 values, and the class predicts the size (types 2, 3, 4, 5, 9, 10,
///     11 and 12 have exactly ONE size each across the whole corpus). See
///     <see cref="BosDataFile.DescribeType" /> for what each names.
/// </param>
/// <param name="DeclaredLength">
///     The dword at payload <c>+16</c>. It equals <see cref="Size" /> on every record of types 2,
///     3, 4, 5, 6, 9, 10, 11 and 12 — 39,855 of them, unanimously — so for those it is a second,
///     independent derivation of the length. ⚠ It does NOT hold for types 0, 1, 8 and 13 (type 0
///     runs 4 bytes OVER on 209 records), so it is reported rather than trusted.
/// </param>
internal readonly record struct BosDataRecord(
    int Index,
    uint Hash,
    int Offset,
    int Size,
    uint Type,
    uint DeclaredLength);

/// <summary>
///     The <c>.DDF</c> record store from Fallout: Brotherhood of Steel. Original RE 2026-09-06
///     against the shipped disc; the Snowblind references are GPL or unlicensed and describe a
///     different container family anyway.
///     <para>
///         Little-endian. <c>+0</c> is the record COUNT. What follows is a <b>772-byte fixed
///         header</b> — established not by guessing field boundaries but by the fact that the first
///         772 bytes are <b>byte-identical across all 55 files</b> apart from that count. It carries
///         a constant signature (<c>+4</c> = 1055, <c>+8</c> = 19, <c>+12</c> = 0xD590B5DE, bytes
///         16-52 zero) and IEEE floats (80.0, 20.0), so it reads as a defaults or tuning block.
///     </para>
///     <para>
///         ⚑ At <c>+772</c> sits the directory: <c>count</c> records of
///         <c>(u32 hash, u32 offset, u32 zero)</c>. The arithmetic is exact —
///         <c>772 + 12 * count</c> equals the first entry's offset on <b>55/55</b> files, offsets
///         are strictly ascending on <b>55/55</b>, and the third field is zero on all
///         <b>43,006</b> entries. A record's SIZE is the gap to the next entry, the last running to
///         EOF, so the payloads tile the remainder of the file.
///     </para>
///     <para>
///         ⚑ The payload ECHOES its directory key at <c>+4</c> on <b>43,006/43,006</b> records, and
///         that echo is enforced here. It is a second, independent derivation of the walk: a
///         directory read at the wrong stride or from the wrong header length puts the payloads
///         somewhere else and every echo fails at once. Its leading dword is a class code — see
///         <see cref="DescribeType" />.
///     </para>
///     <para>
///         ⚑ <b>The records ARE named — by the sibling <see cref="BosStringDatabase" />.</b> A
///         level's <c>.SDB</c> is the name table for its <c>.DDF</c>: measured over the 54 pairs,
///         <b>7,383 of 7,383</b> SDB strings resolve to a record in that level's file, and the
///         union of all 56 databases names <b>42,350 of the 43,006</b> records (98.5%). The
///         leftover 3,933 strings are the game's DIALOGUE and UI prose, which name no record.
///     </para>
///     <para>
///         ⚠ Resolve names from the SIBLING database first. The union is convenient but lossy —
///         115 hashes carry DIFFERENT text in different databases, so a global lookup will
///         mislabel some records that a per-level lookup gets right.
///     </para>
///     <para>
///         ⚠ Records have no names of their own — the hash is the only key, and it is near-unique
///         WITHIN a file (ONE collision across all 43,006) but NOT across files: only 2,242 distinct
///         values back all 43,006 records, because <c>ALL.DDF</c> is a master table every per-level
///         file draws a subset from. The hash FUNCTION is unknown, as it is for the sibling
///         <see cref="BosStringDatabase" />; it is not needed to read a file, because the directory
///         stores each offset outright.
///     </para>
/// </summary>
internal sealed class BosDataFile
{
    /// <summary>Bytes of fixed header before the directory.</summary>
    public const int HeaderLength = 772;

    /// <summary>Bytes per directory record.</summary>
    public const int RecordLength = 12;

    /// <summary>The constant at <c>+4</c> on every retail file.</summary>
    public const uint Signature = 1055;

    /// <summary>The constant at <c>+8</c>.</summary>
    public const uint SecondSignature = 19;

    /// <summary>The constant at <c>+12</c>.</summary>
    public const uint ThirdSignature = 0xD590B5DE;

    private BosDataFile(string name, IReadOnlyList<BosDataRecord> records)
    {
        Name = name;
        Records = records;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The records, in directory order.</summary>
    public IReadOnlyList<BosDataRecord> Records { get; }

    /// <summary>
    ///     Content probe. There is no magic string, so this checks the three header constants AND
    ///     requires the directory arithmetic to hold — the constants alone would be weak evidence.
    /// </summary>
    public static bool IsDataFile(ReadOnlySpan<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>Parses the directory, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static BosDataFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var file, out var error))
        {
            throw new InvalidDataException(error);
        }

        return file;
    }

    /// <summary>Parses the directory, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out BosDataFile file, out string error)
    {
        file = null!;
        if (bytes.Length < HeaderLength)
        {
            error = $"{name}: {bytes.Length} bytes is shorter than the {HeaderLength}-byte header.";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != Signature ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != SecondSignature ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != ThirdSignature)
        {
            error = $"{name}: the header constants are not the {Signature}/{SecondSignature}/0x{ThirdSignature:X8} every file carries.";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count == 0 || count > (bytes.Length - HeaderLength) / RecordLength)
        {
            error = $"{name}: {count} records do not fit in {bytes.Length} bytes.";
            return false;
        }

        var directoryEnd = HeaderLength + ((int)count * RecordLength);
        var offsets = new int[count];
        var hashes = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var at = HeaderLength + (i * RecordLength);
            hashes[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            offsets[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);

            // The third field is zero on every one of the 43,006 retail entries; a non-zero value
            // means this is not the layout measured here.
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]) != 0)
            {
                error = $"{name}: record {i} has a non-zero third field.";
                return false;
            }
        }

        // ⚑ The gate: the first payload begins exactly where the directory ends, and offsets only
        // ever increase. Both hold on 55/55 retail files.
        if (offsets[0] != directoryEnd)
        {
            error = $"{name}: the first record starts at {offsets[0]} rather than {directoryEnd}.";
            return false;
        }

        var records = new BosDataRecord[count];
        for (var i = 0; i < count; i++)
        {
            var next = i + 1 < count ? offsets[i + 1] : bytes.Length;
            if (next <= offsets[i] || next > bytes.Length)
            {
                error = $"{name}: record {i} runs from {offsets[i]} to {next}, which is not forward and inside the file.";
                return false;
            }

            var payload = bytes[offsets[i]..next];

            // ⚑ The payload ECHOES its own directory key at +4, on all 43,006 retail records. That
            // is what makes this parse self-checking: a directory read at the wrong stride, or from
            // the wrong header length, lands the payloads somewhere else and the echo collapses.
            if (payload.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) != hashes[i])
            {
                error = $"{name}: record {i} does not echo its key 0x{hashes[i]:X8} at payload +4.";
                return false;
            }

            records[i] = new BosDataRecord(
                i,
                hashes[i],
                offsets[i],
                next - offsets[i],
                payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0,
                payload.Length >= 20 ? BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]) : 0);
        }

        file = new BosDataFile(name, records);
        error = string.Empty;
        return true;
    }

    /// <summary>Reads one record's payload.</summary>
    public static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> bytes, BosDataRecord record)
    {
        return bytes.Slice(record.Offset, record.Size);
    }

    /// <summary>
    ///     What a <see cref="BosDataRecord.Type" /> names, or <c>null</c> for a code retail never
    ///     uses (only 7 is absent from the 13 in use).
    ///     <para>
    ///         ⚑ These are read off the NAMES, not guessed from the bytes: pairing every record
    ///         with the string the level's <see cref="BosStringDatabase" /> gives it resolves
    ///         42,350 of 43,006, and each class comes back as one coherent English category. Type 9
    ///         is the clearest — "RangedBasic", "DuckAndCover", "RangedCowardly", "GiantRadscorpionAI"
    ///         can only be AI behaviour. Type 0 holds "Cyrus", "Cain" and "Nadia", the game's three
    ///         player characters.
    ///     </para>
    /// </summary>
    public static string? DescribeType(uint type)
    {
        return type switch
        {
            0 => "Actor",
            1 => "World object",
            2 => "Weapon",
            3 => "Inventory item",
            4 => "Throwable",
            5 => "Particle emitter",
            6 => "Effect",
            8 => "Level",
            9 => "AI behaviour",
            10 => "Light",
            11 => "Beam effect",
            12 => "Debris set",
            13 => "Trap",
            _ => null
        };
    }
}
