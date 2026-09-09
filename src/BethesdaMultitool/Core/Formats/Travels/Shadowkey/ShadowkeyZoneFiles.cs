using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The six small per-zone binary files of Shadowkey (N-Gage, 2004): <c>.zon</c> trigger
///     rectangles, <c>.stn</c> lock strengths, <c>.pth</c> waypoint paths, <c>.sur</c> surfaces,
///     <c>.ent</c> entity placements and the one-off <c>.sta</c>. Original RE 2026-09-05 from the
///     retail bytes only — the <c>6R51.APP</c> ARM image was never opened, so every claim below is
///     a tiling or a cross-file identity, not a decompiled struct.
///     <para>
///         <b>Little-endian throughout</b>: Shadowkey is Symbian/ARM, the opposite of the DOS-era
///         Redguard and Battlespire readers next door. Every count in these files reads as a sane
///         small integer little-endian and as <c>count &lt;&lt; 8</c> big-endian, which is how the
///         endianness was settled.
///     </para>
///     <para>
///         <b>Retail census</b> (21 zones, <c>system/apps/6R51/</c>, all verified by tiling):
///         256 trigger rectangles, 59 lock rows, 7 paths holding 86 points, 351 surfaces and
///         8,258 entity placements. Twelve <c>.stn</c> and FIFTEEN <c>.pth</c> files are the
///         two-byte empty file <c>00 00</c>, which parses to an empty list and is not an error.
///         (Only six zones carry a path at all — crypt1, crypt2, Crypt3, delfhide, drgnfld and
///         ffarena — so the RE note's "14 empty .pth" is one short; the file sizes say 15.)
///         <c>.sta</c> exists for azra alone: 201 records, 6,436 bytes.
///     </para>
///     <list type="bullet">
///         <item>
///             <c>.zon</c>: u16 count then 72-byte records — four u16 tile bounds and a
///             <c>char[64]</c> label. <c>2 + 72 * count == length</c> in 21/21.
///         </item>
///         <item>
///             <c>.stn</c>: u16 count then pairs of length-prefixed strings (u16 length, then that
///             many bytes, no terminator and no padding). The walk lands exactly on EOF in 21/21.
///         </item>
///         <item>
///             <c>.pth</c>: u16 count then, per path, a <c>char[64]</c> name, a u16 point count, a
///             u16 alignment hole and eight bytes per point. Header plus the sum of
///             <c>68 + 8 * points</c> equals the length in 21/21. Points are eight bytes — two u32
///             — not four.
///         </item>
///         <item>
///             <c>.sur</c>: a ONE-byte count then 8-byte records. <c>1 + 8 * count == length</c> in
///             21/21, and the texture index is the record's LAST byte (see
///             <see cref="ShadowkeySurface" />).
///         </item>
///         <item>
///             <c>.ent</c>: u32 count then 72-byte records; <c>4 + 72 * count == length</c> in
///             21/21. <c>.sta</c> is the same header over the 32-byte PREFIX of that record.
///         </item>
///     </list>
///     <para>
///         <b>The 0xCC bytes mean two different things and the bytes say which.</b> The u16 slots at
///         <c>.ent</c>/<c>.sta</c> +26 and <c>.pth</c> +66 are 0xCCCC in 100% of the 8,466 records
///         that have one — MSVC debug stack fill in an alignment hole, never data, and never worth
///         validating. Inside a fixed-width text field the bytes after the NUL are 0xCC fill in
///         some records and STALE TEXT from an earlier record of the same file in others: 184 of
///         the 256 <c>.zon</c> tails hold something other than fill, 142 of them printable text,
///         and every one of those is explained byte-for-byte by an earlier record of the same file
///         at the same offsets — the writer serialises one reused stack struct, so a shorter name
///         leaves the end of a longer one behind. Over every tail in the family the byte classes
///         are exactly {0xCC, NUL, printable ASCII}. So every text field is
///         cut at its first NUL and the tail is discarded. Text is decoded Latin-1: one script
///         field carries a 0xFF byte, and the <c>char[8]</c> instance name is truncating and is not
///         always terminated at all.
///     </para>
///     <para>
///         The sibling <c>.pal</c> is a plain 768-byte 8-bit RGB palette (components reach 255 in
///         21/21, so the repo's promote-only-if-every-component-fits-6-bits sniff correctly leaves
///         it alone) and needs no Shadowkey-specific reader. <c>.ztx</c> and <c>.zmp</c> are
///         compressed and belong to <see cref="ShadowkeyCompressedFile" />.
///     </para>
/// </summary>
internal static class ShadowkeyZoneFiles
{
    /// <summary>Divisor turning a raw 24.8 fixed-point coordinate into map tiles.</summary>
    public const float FixedPointScale = 256f;

    /// <summary>Bytes in one <c>.zon</c> rectangle: four u16 bounds plus a 64-byte label.</summary>
    public const int TriggerRecordLength = 72;

    /// <summary>Bytes in one <c>.sur</c> row.</summary>
    public const int SurfaceRecordLength = 8;

    /// <summary>Bytes in one <c>.ent</c> record.</summary>
    public const int EntityRecordLength = 72;

    /// <summary>Bytes in one <c>.sta</c> record — the leading fields of an <c>.ent</c> record.</summary>
    public const int EntityCoreLength = 32;

    /// <summary>Bytes of a <c>.pth</c> path before its points: the name, the count and the hole.</summary>
    public const int PathHeaderLength = 68;

    /// <summary>Bytes in one <c>.pth</c> point: two u32 of 24.8 tile coordinates.</summary>
    public const int PathPointLength = 8;

    /// <summary>Characters in the <c>.zon</c> and <c>.pth</c> name fields.</summary>
    public const int NameFieldLength = 64;

    /// <summary>Characters in the <c>.ent</c> instance name — truncating, not always NUL-terminated.</summary>
    public const int InstanceNameLength = 8;

    /// <summary>Characters in the <c>.ent</c> script field.</summary>
    public const int ScriptFieldLength = 32;

    /// <summary>
    ///     Parses a <c>.zon</c> trigger table. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the byte position when the file is shorter than its count
    ///     word or when the records do not tile the file exactly.
    /// </summary>
    public static IReadOnlyList<ShadowkeyTriggerZone> ParseZon(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var count = ReadCount16(bytes, name, ".zon");
        RequireExactTiling(bytes.Length, 2, TriggerRecordLength, count, name, ".zon", "rectangles");

        var zones = new List<ShadowkeyTriggerZone>(count);
        for (var i = 0; i < count; i++)
        {
            var record = bytes.Slice(2 + i * TriggerRecordLength, TriggerRecordLength);
            zones.Add(new ShadowkeyTriggerZone(
                BinaryPrimitives.ReadUInt16LittleEndian(record),
                BinaryPrimitives.ReadUInt16LittleEndian(record[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[6..]),
                ReadFixedText(record.Slice(8, NameFieldLength))));
        }

        return zones;
    }

    /// <summary>
    ///     Parses a <c>.stn</c> lock table. The records are length-prefixed strings with no padding,
    ///     so the walk must land exactly on the end of the file; anything else — a length that runs
    ///     past EOF, or bytes left over after the last pair — throws
    ///     <see cref="InvalidDataException" /> naming <paramref name="name" /> and the byte
    ///     position where the walk broke.
    /// </summary>
    public static IReadOnlyList<ShadowkeyLockEntry> ParseStn(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var count = ReadCount16(bytes, name, ".stn");
        var entries = new List<ShadowkeyLockEntry>(count);
        var position = 2;
        for (var i = 0; i < count; i++)
        {
            var condition = ReadLengthPrefixedText(bytes, ref position, name, i, "condition");
            var entityName = ReadLengthPrefixedText(bytes, ref position, name, i, "entity name");
            entries.Add(new ShadowkeyLockEntry(condition, entityName));
        }

        if (position != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the .stn walk of {count} lock rows ends at byte {position} of a {bytes.Length}-byte file, leaving {bytes.Length - position} unread.");
        }

        return entries;
    }

    /// <summary>
    ///     Parses a <c>.pth</c> path set. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the byte position when a path header or its points run past
    ///     the end of the file, or when the walk does not consume the file exactly.
    /// </summary>
    public static IReadOnlyList<ShadowkeyPath> ParsePth(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var count = ReadCount16(bytes, name, ".pth");
        var paths = new List<ShadowkeyPath>(count);
        var position = 2;
        for (var i = 0; i < count; i++)
        {
            if (position + PathHeaderLength > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': .pth path {i} needs a {PathHeaderLength}-byte header at byte {position}, past the {bytes.Length}-byte file.");
            }

            var pathName = ReadFixedText(bytes.Slice(position, NameFieldLength));
            var pointCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(position + NameFieldLength)..]);
            position += PathHeaderLength;

            var pointBytes = (long)pointCount * PathPointLength;
            if (position + pointBytes > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': .pth path {i} declares {pointCount} points ({pointBytes} bytes) at byte {position}, past the {bytes.Length}-byte file.");
            }

            var points = new List<ShadowkeyPathPoint>(pointCount);
            for (var p = 0; p < pointCount; p++)
            {
                var point = bytes.Slice(position + p * PathPointLength, PathPointLength);
                points.Add(new ShadowkeyPathPoint(
                    BinaryPrimitives.ReadUInt32LittleEndian(point),
                    BinaryPrimitives.ReadUInt32LittleEndian(point[4..])));
            }

            position += (int)pointBytes;
            paths.Add(new ShadowkeyPath(pathName, points));
        }

        if (position != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the .pth walk of {count} paths ends at byte {position} of a {bytes.Length}-byte file, leaving {bytes.Length - position} unread.");
        }

        return paths;
    }

    /// <summary>
    ///     Parses a <c>.sur</c> surface table — a one-byte count then 8-byte rows. Throws
    ///     <see cref="InvalidDataException" /> naming <paramref name="name" /> and the byte position
    ///     when the file is empty or the rows do not tile it exactly.
    /// </summary>
    public static IReadOnlyList<ShadowkeySurface> ParseSur(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < 1)
        {
            throw new InvalidDataException($"'{name}': the .sur count byte is missing — the file is empty.");
        }

        int count = bytes[0];
        RequireExactTiling(bytes.Length, 1, SurfaceRecordLength, count, name, ".sur", "surfaces");

        var surfaces = new List<ShadowkeySurface>(count);
        for (var i = 0; i < count; i++)
        {
            var record = bytes.Slice(1 + i * SurfaceRecordLength, SurfaceRecordLength);
            surfaces.Add(new ShadowkeySurface(
                record[0],
                record[1],
                BinaryPrimitives.ReadInt16LittleEndian(record[2..]),
                BinaryPrimitives.ReadInt16LittleEndian(record[4..]),
                record[6],
                record[7]));
        }

        return surfaces;
    }

    /// <summary>
    ///     Parses a <c>.ent</c> placement list (72-byte records, names included). Throws
    ///     <see cref="InvalidDataException" /> naming <paramref name="name" /> and the byte position
    ///     when the count word is missing or the records do not tile the file exactly.
    /// </summary>
    public static ShadowkeyEntityList ParseEnt(ReadOnlySpan<byte> bytes, string name)
    {
        return ParseEntityRecords(bytes, name, EntityRecordLength, ".ent");
    }

    /// <summary>
    ///     Parses a <c>.sta</c> placement list — the same header over the 32-byte prefix of the
    ///     <c>.ent</c> record, so the returned entities carry empty names and
    ///     <see cref="ShadowkeyEntityList.HasNames" /> is <see langword="false" />. Same throw
    ///     contract as <see cref="ParseEnt" />.
    /// </summary>
    public static ShadowkeyEntityList ParseSta(ReadOnlySpan<byte> bytes, string name)
    {
        return ParseEntityRecords(bytes, name, EntityCoreLength, ".sta");
    }

    private static ShadowkeyEntityList ParseEntityRecords(
        ReadOnlySpan<byte> bytes,
        string name,
        int recordLength,
        string extension)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < 4)
        {
            throw new InvalidDataException(
                $"'{name}': the {extension} count needs 4 bytes but the file is {bytes.Length} bytes.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var expected = 4L + count * recordLength;
        if (expected != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': {extension} declares {count} records of {recordLength} bytes at byte 0, which needs {expected} bytes, but the file is {bytes.Length} bytes.");
        }

        var hasNames = recordLength >= EntityRecordLength;
        var entities = new List<ShadowkeyEntity>((int)count);
        for (var i = 0; i < count; i++)
        {
            var record = bytes.Slice(4 + i * recordLength, recordLength);
            entities.Add(new ShadowkeyEntity(
                BinaryPrimitives.ReadInt32LittleEndian(record),
                BinaryPrimitives.ReadInt32LittleEndian(record[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[20..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[28..]),
                hasNames ? ReadFixedText(record.Slice(32, InstanceNameLength)) : string.Empty,
                hasNames ? ReadFixedText(record.Slice(40, ScriptFieldLength)) : string.Empty));
        }

        return new ShadowkeyEntityList(entities, hasNames);
    }

    private static int ReadCount16(ReadOnlySpan<byte> bytes, string name, string extension)
    {
        if (bytes.Length < 2)
        {
            throw new InvalidDataException(
                $"'{name}': the {extension} count needs 2 bytes but the file is {bytes.Length} bytes.");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    private static void RequireExactTiling(
        int length,
        int headerLength,
        int recordLength,
        int count,
        string name,
        string extension,
        string plural)
    {
        var expected = headerLength + (long)count * recordLength;
        if (expected != length)
        {
            throw new InvalidDataException(
                $"'{name}': {extension} declares {count} {plural} of {recordLength} bytes at byte 0, which needs {expected} bytes, but the file is {length} bytes.");
        }
    }

    private static string ReadLengthPrefixedText(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string name,
        int index,
        string field)
    {
        if (position + 2 > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the .stn {field} of row {index} needs its 2-byte length at byte {position}, past the {bytes.Length}-byte file.");
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
        if (position + 2 + length > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the .stn {field} of row {index} is {length} bytes at byte {position + 2}, past the {bytes.Length}-byte file.");
        }

        var text = Encoding.Latin1.GetString(bytes.Slice(position + 2, length));
        position += 2 + length;
        return text;
    }

    /// <summary>
    ///     Reads a fixed-width text field: Latin-1, cut at the first NUL, or the whole field when
    ///     there is none — which is what the truncating 8-byte instance name needs. Bytes past the
    ///     NUL are 0xCC fill or an earlier record's stale text and are dropped.
    /// </summary>
    private static string ReadFixedText(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }
}
