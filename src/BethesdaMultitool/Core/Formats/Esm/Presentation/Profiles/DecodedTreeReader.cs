using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     Absent-safe navigation + value extraction over a schema-decoded <see cref="DecodedNode" /> tree,
///     for the profile-driven record presentation. Reads <see cref="DecodedNode.RawValue" /> (NOT
///     <see cref="DecodedNode.Value" />): the decoder formats <c>Value</c> its own way (floats via
///     InvariantCulture, FormIds as "Name (0x…)", flags/enums as labels), which diverges from how the
///     typed detail builders format — the profile must re-format raw values via the same
///     <see cref="RecordDetailHelpers" />.
///     <para>
///         Top-level <em>signed</em> members carry a <see cref="DecodedNode.Signature" /> (e.g. RNAM, ACBS);
///         array nodes (Head Parts, Factions) do not, so they are found by <see cref="DecodedNode.Label" />.
///         Fields that sit behind a union or dynamic array (ACBS Level, DATA Attributes, DNAM Skill Values)
///         are preserved by the decoder as a <em>raw tail</em> child whose bytes start at that field — read
///         them with <see cref="Bytes" /> + the little-endian primitives (PC plugins are little-endian).
///     </para>
/// </summary>
internal static class DecodedTreeReader
{
    /// <summary>First top-level node whose subrecord signature is <paramref name="signature" />, or null.</summary>
    public static DecodedNode? TopBySignature(IReadOnlyList<DecodedNode> tree, string signature) =>
        tree.FirstOrDefault(n => n.Signature == signature);

    /// <summary>Every top-level node with the given signature (e.g. the repeated CNTO inventory entries).</summary>
    public static IEnumerable<DecodedNode> AllTopBySignature(IReadOnlyList<DecodedNode> tree, string signature) =>
        tree.Where(n => n.Signature == signature);

    /// <summary>First top-level node with the given label (for unsignatured array nodes), or null.</summary>
    public static DecodedNode? TopByLabel(IReadOnlyList<DecodedNode> tree, string label) =>
        tree.FirstOrDefault(n => n.Label == label);

    /// <summary>First child of <paramref name="node" /> with the given label, or null.</summary>
    public static DecodedNode? ChildByLabel(DecodedNode? node, string label) =>
        node?.Children.FirstOrDefault(c => c.Label == label);

    /// <summary>The node's integer raw value (decoder boxes all integer fields as <c>long</c>), or null.</summary>
    public static long? Int(DecodedNode? node) => node?.RawValue switch
    {
        long l => l,
        int i => i,
        uint u => u,
        ushort us => us,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        _ => null
    };

    /// <summary>The node's float raw value, or null.</summary>
    public static float? Float(DecodedNode? node) => node?.RawValue switch
    {
        float f => f,
        double d => (float)d,
        _ => null
    };

    /// <summary>The node's string raw value, or null.</summary>
    public static string? Str(DecodedNode? node) => node?.RawValue as string;

    /// <summary>The node's raw bytes (raw/ByteArray nodes), or null.</summary>
    public static byte[]? Bytes(DecodedNode? node) => node?.RawValue as byte[];

    /// <summary>The node's FormID (set on FormID-reference nodes), or null when 0/absent.</summary>
    public static uint? FormId(DecodedNode? node) => node?.FormId is { } f and not 0 ? f : null;

    // Little-endian primitive reads from a raw-tail byte span (PC plugins).
    public static short? ReadS16(byte[]? data, int offset) =>
        data is not null && offset + 2 <= data.Length ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset)) : null;

    public static uint? ReadU32(byte[]? data, int offset) =>
        data is not null && offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) : null;

    public static int? ReadS32(byte[]? data, int offset) =>
        data is not null && offset + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)) : null;

    public static float? ReadFloat(byte[]? data, int offset) =>
        data is not null && offset + 4 <= data.Length ? BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset)) : null;
}
