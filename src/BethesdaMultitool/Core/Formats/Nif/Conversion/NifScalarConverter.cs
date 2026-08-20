using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using static BethesdaMultitool.Core.Formats.Nif.Conversion.NifEndianUtils;

namespace BethesdaMultitool.Core.Formats.Nif.Conversion;

/// <summary>
///     Leaf value/string conversion helpers for the schema-driven NIF converter: scalar basic
///     types, sized strings, enums, packed-bitflag and fixed-size struct bulk swaps, and the
///     <c>NiAGDDataBlock.Data</c> special case. These never recurse back into field-list walking,
///     so they live here as stateless static helpers driven entirely by the threaded
///     <see cref="NifConversionContext" /> and the converter's schema / measure / version state.
/// </summary>
internal static class NifScalarConverter
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Enum/bitflags type names whose bytes are stored in PC-native order even in Xbox 360
    ///     NIFs. The schema converter normally swaps every multi-byte enum/bitflags via its
    ///     ushort/uint storage type, but Bethesda's Xbox tools wrote these specific types as
    ///     raw byte pairs — swapping inverts the semantic bit positions. Add a type here only
    ///     when both an Xbox source and its PC counterpart show identical disk bytes for the
    ///     field (compare via <c>NifAnalyzer block</c>).
    /// </summary>
    private static readonly HashSet<string> BytePackedBitflagTypes = new(StringComparer.Ordinal)
    {
        "BSPartFlag",
    };

    // `bool` is a 32-bit int up to and including NIF 4.0.0.2, then an 8-bit byte from 4.1.0.1 on
    // (see nif.xml <basic name="bool">). The schema stores the modern 1-byte size, which is correct
    // for every Bethesda stream we convert (Oblivion 20.0.0.x, FO3/FNV/Skyrim 20.2.0.x) — only the
    // Morrowind-era legacy-Gamebryo measure path (4.0.0.2) needs the widened 4-byte width.
    private const uint BoolWidensAtOrBelowVersion = 0x04000002;

    /// <summary>
    ///     Returns the on-disk byte width of a basic type for the current NIF version. Identical to
    ///     the schema's stored size except for <c>bool</c>, which is 4 bytes up to and including
    ///     4.0.0.2 (Morrowind) and 1 byte thereafter — the schema stores only the modern 1-byte width.
    /// </summary>
    public static int EffectiveTypeSize(uint version, string typeName, int schemaSize)
    {
        return typeName == "bool" && version <= BoolWidensAtOrBelowVersion ? 4 : schemaSize;
    }

    /// <summary>
    ///     Swap the packed data buffer of a <c>NiAGDDataBlock</c> as a uint32 stream rather
    ///     than as the per-byte no-op the schema would otherwise apply. The buffer length
    ///     is <c>Num Data × Block Size</c>; we look both values up from the field-values
    ///     map that the converter populated when processing earlier fields. If anything
    ///     looks off (missing values, non-4-aligned size, out-of-bounds), bail and let the
    ///     normal path run — better to fall back to legacy behavior than corrupt bytes.
    /// </summary>
    public static bool TryConvertNiAgdDataBlockData(bool measure, NifConversionContext ctx, NifFieldDef field)
    {
        if (field.Width is null
            || !ctx.FieldValues.TryGetValue("Num Data", out var numDataObj)
            || !ctx.FieldValues.TryGetValue("Block Size", out var blockSizeObj))
        {
            return false;
        }

        if (numDataObj is not int numData || blockSizeObj is not int blockSize)
        {
            return false;
        }

        var totalBytes = (long)numData * blockSize;
        if (totalBytes is < 0 or > int.MaxValue)
        {
            return false;
        }

        // The fix only applies when the packed data is 4-byte aligned (the only layout
        // we have empirical evidence for). NIFs with byte-sized channels would need
        // per-channel swap; fall back to the original behavior in that case.
        if (blockSize % 4 != 0)
        {
            return false;
        }

        var totalInt = (int)totalBytes;
        if (ctx.Position + totalInt > ctx.End)
        {
            return false;
        }

        if (!measure)
        {
            for (var offset = 0; offset < totalInt; offset += 4)
            {
                SwapUInt32InPlace(ctx.Buffer, ctx.Position + offset);
            }
        }

        ctx.Position += totalInt;
        return true;
    }

    public static bool TryConvertStringType(bool measure, NifConversionContext ctx, string typeName)
    {
        switch (typeName)
        {
            case "SizedString":
                ConvertSizedString(measure, ctx);
                return true;
            case "SizedString16":
                ConvertSizedString16(measure, ctx);
                return true;
            default:
                return false;
        }
    }

    public static bool TryConvertBasicType(NifSchema schema, bool measure, uint version, NifConversionContext ctx,
        string typeName)
    {
        if (!schema.BasicTypes.TryGetValue(typeName, out var basic))
        {
            return false;
        }

        ConvertBasicType(measure, version, ctx, basic);
        return true;
    }

    public static bool TryConvertEnumType(NifSchema schema, bool measure, uint version, NifConversionContext ctx,
        string typeName)
    {
        if (!schema.Enums.TryGetValue(typeName, out var enumDef))
        {
            return false;
        }

        if (BytePackedBitflagTypes.Contains(typeName))
        {
            // Xbox 360 NIF tools wrote BSPartFlag (BSDismemberSkinInstance.Partitions[].PartFlag)
            // as raw bytes in the same layout as PC, not as a byte-swapped ushort. A normal
            // per-field swap inverts the bit positions: PF_EDITOR_VISIBLE (bit 0, value 0x0001)
            // becomes PF_START_NET_BONESET (bit 8, value 0x0100) and vice versa. The effect on
            // a converted prototype outfit is that body partitions lose the visible flag (so
            // limbs flicker / cull) and gore-cap partitions GAIN the visible flag (so the caps
            // render through the body, garbage-textured if the cap textures aren't loaded).
            // Skip the swap: advance the position by the storage size without touching bytes.
            var skipSize = schema.GetTypeSize(enumDef.Storage) ?? 2;
            ctx.Position += skipSize;
            return true;
        }

        if (schema.BasicTypes.TryGetValue(enumDef.Storage, out var storageType))
        {
            ConvertBasicType(measure, version, ctx, storageType);
        }

        return true;
    }

    public static bool TryBulkSwapFixedSizeStruct(NifSchema schema, bool measure, NifConversionContext ctx,
        NifStructDef structDef)
    {
        if (structDef.FixedSize is not (2 or 4 or 8))
        {
            return false;
        }

        // Only bulk-swap structs where all fields are single bytes (packed uint32/uint64 values
        // like UDecVector4, ByteColor4). Structs with multi-byte sub-fields (e.g., BodyPartList
        // = 2 × ushort, HalfTexCoord = 2 × hfloat) need per-field endian conversion — bulk
        // swap cross-contaminates adjacent fields. HavokFilter never reaches here: it is
        // special-cased upstream in NifValueConverter.TryConvertStructType because its 360
        // on-disk convention is site-dependent (whole BE uint32 at NiStream sites, raw LE
        // inside bhkRigidBodyCInfo).
        foreach (var field in structDef.Fields)
        {
            var fieldSize = schema.GetTypeSize(field.Type) ?? 0;
            if (fieldSize > 1)
            {
                return false;
            }
        }

        if (!measure)
        {
            if (structDef.FixedSize == 2)
            {
                SwapUInt16InPlace(ctx.Buffer, ctx.Position);
            }
            else if (structDef.FixedSize == 4)
            {
                SwapUInt32InPlace(ctx.Buffer, ctx.Position);
            }
            else if (structDef.FixedSize == 8)
            {
                SwapUInt64InPlace(ctx.Buffer, ctx.Position);
            }
        }

        ctx.Position += structDef.FixedSize.Value;
        return true;
    }

    public static void ConvertUnknownType(NifSchema schema, bool measure, NifConversionContext ctx, string typeName)
    {
        var size = schema.GetTypeSize(typeName);
        if (!size.HasValue || size.Value <= 0)
        {
            Log.Trace($"    [Schema] WARNING: Unknown type '{typeName}' with no size, cannot advance position");
            return;
        }

        // Bulk swap based on size
        if (!measure)
        {
            if (size.Value == 2)
            {
                SwapUInt16InPlace(ctx.Buffer, ctx.Position);
            }
            else if (size.Value == 4)
            {
                SwapUInt32InPlace(ctx.Buffer, ctx.Position);
            }
            else if (size.Value == 8)
            {
                SwapUInt64InPlace(ctx.Buffer, ctx.Position);
            }
        }

        ctx.Position += size.Value;
    }

    /// <summary>
    ///     Converts a SizedString (uint length + chars) - swaps the length field. In measure mode the
    ///     swap is skipped (data is already LE) and the string is captured when Name capture is armed.
    /// </summary>
    private static void ConvertSizedString(bool measure, NifConversionContext ctx)
    {
        if (ctx.Position + 4 > ctx.End)
        {
            return;
        }

        // Swap the length (uint, 4 bytes)
        if (!measure)
        {
            SwapUInt32InPlace(ctx.Buffer, ctx.Position);
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 4));
        ctx.Position += 4;

        // Skip the string data (chars don't need swapping)
        if (length is > 0 and < 0x10000) // Sanity check
        {
            if (measure && ctx.CapturingName)
            {
                ctx.CapturingName = false;
                if (ctx.Position + (int)length <= ctx.End)
                {
                    ctx.CapturedName = Encoding.ASCII.GetString(ctx.Buffer, ctx.Position, (int)length);
                }
            }

            ctx.Position += (int)length;
        }
        else if (measure)
        {
            // Empty/oversized name: disarm so a later inline string isn't mistaken for the block name.
            ctx.CapturingName = false;
        }
    }

    /// <summary>
    ///     Converts a SizedString16 (ushort length + chars) - swaps the length field.
    /// </summary>
    private static void ConvertSizedString16(bool measure, NifConversionContext ctx)
    {
        if (ctx.Position + 2 > ctx.End)
        {
            return;
        }

        // Swap the length (ushort, 2 bytes)
        if (!measure)
        {
            SwapUInt16InPlace(ctx.Buffer, ctx.Position);
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 2));
        ctx.Position += 2;

        // Skip the string data (chars don't need swapping)
        if (length > 0)
        {
            ctx.Position += length;
        }
    }

    private static void ConvertBasicType(bool measure, uint version, NifConversionContext ctx, NifBasicType basic)
    {
        var size = EffectiveTypeSize(version, basic.Name, basic.Size);
        if (ctx.Position + size > ctx.End)
        {
            return;
        }

        var pos = ctx.Position; // Save position before modifying

        switch (size)
        {
            case 1:
                // No swap needed for single bytes
                ctx.Position += 1;
                break;

            case 2:
                if (!measure)
                {
                    SwapUInt16InPlace(ctx.Buffer, pos);
                }

                ctx.Position += 2;
                break;

            case 4:
                if (!measure)
                {
                    SwapUInt32InPlace(ctx.Buffer, pos);
                    // Handle block references (Ref, Ptr) that need remapping
                    if (basic.IsGeneric)
                    {
                        RemapBlockRef(ctx.Buffer, pos, ctx.BlockRemap);
                    }
                }

                ctx.Position += 4;
                break;

            case 8:
                if (!measure)
                {
                    SwapUInt64InPlace(ctx.Buffer, pos);
                }

                ctx.Position += 8;
                break;
        }
    }

    private static void RemapBlockRef(byte[] buf, int pos, int[] blockRemap)
    {
        var idx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4));
        if (idx >= 0 && idx < blockRemap.Length)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), blockRemap[idx]);
        }
    }
}
