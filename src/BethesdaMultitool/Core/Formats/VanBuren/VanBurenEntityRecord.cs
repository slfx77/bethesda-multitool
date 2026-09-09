using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>
///     An <c>EEN2</c> entity record from the Van Buren prototype — the object definitions its
///     <c>_AMO/_ARM/_CON/_CRT/_DOR/_ITM/_USE/_WEA</c> groups are made of. Original RE 2026-09-06;
///     the only reference is GPL and none of it is ported.
///     <para>
///         Little-endian. <c>EEN2</c>, a zero dword, a dword whose meaning is NOT established (it is
///         not the record length — it misses by −110, −321, −184 … across retail), then two
///         <b>u16-length-prefixed ASCII strings</b>: the entity's NAME and an ASSET reference.
///         The length prefix is what makes this readable with no guessing —
///         <c>0x16</c> then exactly 22 characters of <c>GD_Items_First_Aid_Kit</c>, <c>0x0B</c> then
///         <c>GD_Ammo_9mm</c>, <c>0x19</c> then <c>DS_Vault13_Doors{1}Door01</c>.
///     </para>
///     <para>
///         Measured over all <b>285</b> retail EEN2 records: the two strings read as clean ASCII on
///         <b>282</b>, and the other three simply declare an EMPTY name — a record without one, not
///         a parse failure.
///     </para>
///     <para>
///         ⚑ The body carries nested 4-character tags, and their counts identify the record's kind:
///         <c>EEOV</c> and <c>GENT</c> appear in <b>all 285</b>, while <c>GCRE</c> appears
///         <b>108</b> times — exactly the number of entries in <c>_CRT.grp</c> — alongside
///         <c>GITM</c> 98, <c>GOBJ</c> 79 and <c>GWAM</c> 57. Those are surfaced as found rather
///         than interpreted: what each sub-record CONTAINS is still open.
///     </para>
/// </summary>
internal sealed class VanBurenEntityRecord
{
    /// <summary>The tag every entity record opens with.</summary>
    public const string Tag = "EEN2";

    /// <summary>Bytes before the first length-prefixed string.</summary>
    public const int HeaderLength = 12;

    private VanBurenEntityRecord(string name, string asset, uint unknown, IReadOnlyList<string> nestedTags)
    {
        Name = name;
        Asset = asset;
        Unknown = unknown;
        NestedTags = nestedTags;
    }

    /// <summary>The entity's name, e.g. <c>GD_Ammo_9mm</c>. Empty on three retail records.</summary>
    public string Name { get; }

    /// <summary>The asset it references, e.g. <c>AM_9mm_FMJ_INV.dds</c>. May be empty.</summary>
    public string Asset { get; }

    /// <summary>The dword at +8, whose meaning is not established — deliberately not named.</summary>
    public uint Unknown { get; }

    /// <summary>The 4-character tags found in the body, in order of first appearance.</summary>
    public IReadOnlyList<string> NestedTags { get; }

    /// <summary>Content probe.</summary>
    public static bool IsEntityRecord(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= HeaderLength && bytes[..4].SequenceEqual("EEN2"u8);
    }

    /// <summary>Parses one record, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static VanBurenEntityRecord Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var record, out var error))
        {
            throw new InvalidDataException(error);
        }

        return record;
    }

    /// <summary>Parses one record, reporting why rather than throwing.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> bytes,
        string name,
        out VanBurenEntityRecord record,
        out string error)
    {
        record = null!;
        if (!IsEntityRecord(bytes))
        {
            error = $"{name}: does not open with the {Tag} tag.";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 0)
        {
            error = $"{name}: the dword at +4 is not the zero every retail record carries.";
            return false;
        }

        var unknown = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var position = HeaderLength;
        if (!TryReadString(bytes, ref position, out var entityName))
        {
            error = $"{name}: the name string runs past the record.";
            return false;
        }

        // The asset reference is optional in the sense that a record can end after the name.
        if (!TryReadString(bytes, ref position, out var asset))
        {
            asset = string.Empty;
        }

        record = new VanBurenEntityRecord(entityName, asset, unknown, ScanTags(bytes[position..]));
        error = string.Empty;
        return true;
    }

    /// <summary>Reads a u16 length then that many ASCII bytes.</summary>
    private static bool TryReadString(ReadOnlySpan<byte> bytes, ref int position, out string value)
    {
        value = string.Empty;
        if (position + 2 > bytes.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
        if (position + 2 + length > bytes.Length)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(bytes.Slice(position + 2, length));
        position += 2 + length;
        return true;
    }

    /// <summary>
    ///     Collects the 4-character upper-case tags the body contains. This is a SCAN, not a walk of
    ///     a known structure — the sub-record layout is not established, so the tags are reported as
    ///     evidence rather than parsed into fields.
    /// </summary>
    private static List<string> ScanTags(ReadOnlySpan<byte> body)
    {
        var found = new List<string>();
        for (var i = 0; i + 4 <= body.Length; i++)
        {
            var slice = body.Slice(i, 4);
            if (!IsTag(slice))
            {
                continue;
            }

            var tag = Encoding.ASCII.GetString(slice);
            if (!found.Contains(tag, StringComparer.Ordinal))
            {
                found.Add(tag);
            }
        }

        return found;
    }

    private static bool IsTag(ReadOnlySpan<byte> slice)
    {
        foreach (var b in slice)
        {
            if (b is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'))
            {
                return false;
            }
        }

        return true;
    }
}
