using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>One self-describing property: its name, its declared type, and its raw value bytes.</summary>
internal readonly record struct VanBurenVegProperty(string Name, string Type, ReadOnlyMemory<byte> Value);

/// <summary>One <c>VFX V1.0</c> block — a single visual effect's property list.</summary>
internal sealed record VanBurenVfxBlock(IReadOnlyList<VanBurenVegProperty> Properties);

/// <summary>
///     The <c>VEG </c> visual-effect group from the cancelled Van Buren prototype's <c>.grp</c>
///     archives — the last of the three families this repo had recorded as unidentified.
///     <para>
///         ⚑⚑ <b>Solved 2026-09-06 by EXACT TILING on 119 of 119 payloads.</b> It is a
///         SELF-DESCRIBING property list, the same shape Fallout Tactics' <c>.ent</c> turned out to
///         have: every property carries its own name AND its type name, so no type-size table is
///         needed and none should be written.
///     </para>
///     <para>
///         Layout: <c>VEG V1.1</c>, a u32 <b>block count</b> (1-9 on retail), twelve zero bytes,
///         then that many blocks of <c>VFX V1.0</c> + eight zero bytes + a property block — and
///         finally ONE MORE property block carrying no tag at all.
///     </para>
///     <para>
///         ⚠ <b>That trailing untagged block is the trap.</b> Walking only the counted blocks leaves
///         exactly 91 bytes over on every single file, which reads as a mysterious trailer; it is in
///         fact the group's own properties, and on all 119 it is exactly three of them —
///         <c>Loop</c>, <c>MinStartTime</c>, <c>MaxStartTime</c>.
///     </para>
///     <para>
///         A property is <c>u16 nameLength + name</c>, <c>u16 typeLength + type</c>, the constant
///         <c>0xFFFFFFFF</c>, a <c>u32 value length</c> and that many value bytes. Retail types are
///         <c>float</c> (3,965), <c>bool</c> (3,025), <c>enum VFX_WaveType</c>, <c>VFX_Vector</c>,
///         <c>VFX_Color</c>, <c>VFX_Byte</c>, <c>VFX_Rotation</c>, <c>VFX_Resource</c>, <c>int</c>,
///         <c>VFX_Target</c>, <c>enum GFX_BONE_ID</c> and <c>VFX_SnapBone</c>. A string-like value
///         is itself <c>u16 length + characters</c>, so its declared length counts those two bytes.
///     </para>
/// </summary>
internal sealed class VanBurenVegFile
{
    /// <summary>Tag every group opens with.</summary>
    public const string GroupTag = "VEG V1.1";

    /// <summary>Tag every effect block opens with.</summary>
    public const string BlockTag = "VFX V1.0";

    /// <summary>Separator between a property's type and its value length.</summary>
    public const uint PropertySeparator = 0xFFFF_FFFF;

    private VanBurenVegFile(IReadOnlyList<VanBurenVfxBlock> blocks, IReadOnlyList<VanBurenVegProperty> groupProperties)
    {
        Blocks = blocks;
        GroupProperties = groupProperties;
    }

    /// <summary>The counted <c>VFX V1.0</c> blocks.</summary>
    public IReadOnlyList<VanBurenVfxBlock> Blocks { get; }

    /// <summary>The trailing untagged block — <c>Loop</c>, <c>MinStartTime</c>, <c>MaxStartTime</c> on retail.</summary>
    public IReadOnlyList<VanBurenVegProperty> GroupProperties { get; }

    /// <summary>Whether the bytes open with the group tag.</summary>
    public static bool IsVeg(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= GroupTag.Length && Encoding.Latin1.GetString(bytes[..GroupTag.Length]) == GroupTag;
    }

    /// <summary>Parses the group, reporting why rather than throwing.</summary>
    public static bool TryParse(
        ReadOnlyMemory<byte> bytes, string name, out VanBurenVegFile file, out string error)
    {
        file = null!;
        var span = bytes.Span;
        if (!IsVeg(span) || span.Length < 24)
        {
            error = $"{name}: not a {GroupTag} group.";
            return false;
        }

        var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        if (blockCount is 0 or > 1000)
        {
            error = $"{name}: implausible block count {blockCount}.";
            return false;
        }

        var at = 24;
        var blocks = new List<VanBurenVfxBlock>((int)blockCount);
        for (var i = 0; i < blockCount; i++)
        {
            if (at + 16 > span.Length || Encoding.Latin1.GetString(span.Slice(at, BlockTag.Length)) != BlockTag)
            {
                error = $"{name}: block {i} does not open with {BlockTag}.";
                return false;
            }

            at += 16;
            if (!TryReadProperties(bytes, ref at, name, i, out var properties, out error))
            {
                return false;
            }

            blocks.Add(new VanBurenVfxBlock(properties));
        }

        // ⚠ The untagged group block. Omitting it leaves exactly 91 bytes unread on every file.
        if (!TryReadProperties(bytes, ref at, name, -1, out var groupProperties, out error))
        {
            return false;
        }

        if (at != span.Length)
        {
            error = $"{name}: {span.Length - at} bytes remain after the group block.";
            return false;
        }

        file = new VanBurenVegFile(blocks, groupProperties);
        error = string.Empty;
        return true;
    }

    private static bool TryReadProperties(
        ReadOnlyMemory<byte> bytes,
        ref int at,
        string name,
        int block,
        out IReadOnlyList<VanBurenVegProperty> properties,
        out string error)
    {
        properties = [];
        var span = bytes.Span;
        var where = block < 0 ? "group block" : $"block {block}";
        if (at + 4 > span.Length)
        {
            error = $"{name}: {where} has no property count.";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
        at += 4;
        if (count > 5000)
        {
            error = $"{name}: {where} declares {count} properties.";
            return false;
        }

        var list = new List<VanBurenVegProperty>((int)count);
        for (var i = 0; i < count; i++)
        {
            if (!TryReadString(span, ref at, out var propertyName)
                || !TryReadString(span, ref at, out var type)
                || at + 8 > span.Length)
            {
                error = $"{name}: {where} property {i} is truncated.";
                return false;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(span[at..]) != PropertySeparator)
            {
                error = $"{name}: {where} property '{propertyName}' lacks the 0x{PropertySeparator:X8} separator.";
                return false;
            }

            at += 4;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
            at += 4;
            if (at + length > span.Length)
            {
                error = $"{name}: {where} property '{propertyName}' declares {length} value bytes.";
                return false;
            }

            list.Add(new VanBurenVegProperty(propertyName, type, bytes.Slice(at, (int)length)));
            at += (int)length;
        }

        properties = list;
        error = string.Empty;
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> span, ref int at, out string value)
    {
        value = string.Empty;
        if (at + 2 > span.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
        at += 2;
        if (at + length > span.Length)
        {
            return false;
        }

        value = Encoding.Latin1.GetString(span.Slice(at, length));
        at += length;
        return true;
    }
}
