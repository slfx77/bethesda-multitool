using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Tree (TREE) record. Tree placements (REFR bases of TREE) are stripped from
///     converted ESPs when this encoder is missing, leaving exterior worldspaces visually
///     bare.
///     fopdoc canonical order: EDID, OBND, MODL?, MODT?, ICON?, SNAM?, CNAM(32B), BNAM(8B).
/// </summary>
public sealed class TreeEncoder : IRecordEncoder
{
    public string RecordType => "TREE";

    public Type ModelType => typeof(TreeRecord);

    internal static EncodedRecord EncodeNew(TreeRecord tree)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(tree.EditorId))
        {
            warnings.Add($"New TREE 0x{tree.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", tree.EditorId ?? string.Empty));

        if (tree.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(tree.Bounds));
        }

        if (!string.IsNullOrEmpty(tree.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", tree.ModelPath));
        }

        if (tree.ModelTextureData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (!string.IsNullOrEmpty(tree.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", tree.IconPath));
        }

        if (tree.Seeds is { Count: > 0 } seeds)
        {
            subs.Add(new EncodedSubrecord("SNAM", EncodeSnamSeeds(seeds)));
        }

        if (tree.Data is not null)
        {
            subs.Add(new EncodedSubrecord("CNAM", EncodeCnamData(tree.Data)));
        }

        if (tree.BillboardSize is not null)
        {
            subs.Add(new EncodedSubrecord("BNAM", EncodeBnamBillboard(tree.BillboardSize)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>TREE CNAM payload (32 bytes, 8 LE floats).</summary>
    internal static byte[] EncodeCnamData(TreeData data)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), data.LeafCurvature);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), data.MinLeafAngle);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8, 4), data.MaxLeafAngle);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12, 4), data.BranchDimmingValue);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16, 4), data.LeafDimmingValue);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20, 4), data.ShadowRadius);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24, 4), data.RockSpeed);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28, 4), data.RustleSpeed);
        return bytes;
    }

    /// <summary>TREE BNAM payload (8 bytes, 2 LE floats).</summary>
    internal static byte[] EncodeBnamBillboard(TreeBillboardSize size)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), size.Width);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), size.Height);
        return bytes;
    }

    /// <summary>TREE SNAM payload (variable, count × 4 LE bytes = uint32 seed array).</summary>
    internal static byte[] EncodeSnamSeeds(IReadOnlyList<uint> seeds)
    {
        var bytes = new byte[seeds.Count * 4];
        for (var i = 0; i < seeds.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), seeds[i]);
        }

        return bytes;
    }
}
