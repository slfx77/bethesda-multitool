// Type conversion methods for NifSchemaConverter

using System.Buffers.Binary;
using static Xbox360MemoryCarver.Core.Formats.Nif.NifEndianUtils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

// Type conversion methods
internal sealed partial class NifSchemaConverter
{
    private void ConvertSingleValue(ConversionContext ctx, string typeName, int depth = 0)
    {
        typeName = ResolveTypeName(ctx, typeName);
        if (typeName == null) return;

        // Handle special string types
        if (TryConvertStringType(ctx, typeName)) return;

        // Handle basic types
        if (TryConvertBasicType(ctx, typeName)) return;

        // Handle enums
        if (TryConvertEnumType(ctx, typeName)) return;

        // Handle structs
        if (TryConvertStructType(ctx, typeName, depth)) return;

        // Unknown type - try bulk swap based on size
        ConvertUnknownType(ctx, typeName);
    }

    private static string? ResolveTypeName(ConversionContext ctx, string typeName)
    {
        if (typeName != "#T#") return typeName;

        if (ctx.TemplateType != null) return ctx.TemplateType;

        Log.Trace("    [Schema] WARNING: #T# used without template context, cannot resolve");
        return null;
    }

    private static bool TryConvertStringType(ConversionContext ctx, string typeName)
    {
        switch (typeName)
        {
            case "SizedString":
                ConvertSizedString(ctx);
                return true;
            case "SizedString16":
                ConvertSizedString16(ctx);
                return true;
            default:
                return false;
        }
    }

    private bool TryConvertBasicType(ConversionContext ctx, string typeName)
    {
        if (!_schema.BasicTypes.TryGetValue(typeName, out var basic)) return false;
        ConvertBasicType(ctx, basic);
        return true;
    }

    private bool TryConvertEnumType(ConversionContext ctx, string typeName)
    {
        if (!_schema.Enums.TryGetValue(typeName, out var enumDef)) return false;
        if (_schema.BasicTypes.TryGetValue(enumDef.Storage, out var storageType))
        {
            ConvertBasicType(ctx, storageType);
        }
        return true;
    }

    private bool TryConvertStructType(ConversionContext ctx, string typeName, int depth)
    {
        if (!_schema.Structs.TryGetValue(typeName, out var structDef)) return false;

        // Some structs with fixed size (like HavokFilter) are packed bitfields that should
        // be swapped as a single unit rather than field-by-field.
        if (TryBulkSwapFixedSizeStruct(ctx, structDef)) return true;

        // Clear field values for fresh struct instance
        foreach (var field in structDef.Fields)
        {
            ctx.FieldValues.Remove(field.Name);
        }

        ConvertFields(ctx, structDef.Fields, depth + 1);
        return true;
    }

    private static bool TryBulkSwapFixedSizeStruct(ConversionContext ctx, NifStructDef structDef)
    {
        if (structDef.FixedSize is not (2 or 4 or 8)) return false;

        if (structDef.FixedSize == 2) SwapUInt16InPlace(ctx.Buffer, ctx.Position);
        else if (structDef.FixedSize == 4) SwapUInt32InPlace(ctx.Buffer, ctx.Position);
        else if (structDef.FixedSize == 8) SwapUInt64InPlace(ctx.Buffer, ctx.Position);
        ctx.Position += structDef.FixedSize.Value;
        return true;
    }

    private void ConvertUnknownType(ConversionContext ctx, string typeName)
    {
        var size = _schema.GetTypeSize(typeName);
        if (!size.HasValue || size.Value <= 0)
        {
            Log.Trace($"    [Schema] WARNING: Unknown type '{typeName}' with no size, cannot advance position");
            return;
        }

        // Bulk swap based on size
        if (size.Value == 2) SwapUInt16InPlace(ctx.Buffer, ctx.Position);
        else if (size.Value == 4) SwapUInt32InPlace(ctx.Buffer, ctx.Position);
        else if (size.Value == 8) SwapUInt64InPlace(ctx.Buffer, ctx.Position);
        ctx.Position += size.Value;
    }

    /// <summary>
    ///     Converts a SizedString (uint length + chars) - swaps the length field.
    /// </summary>
    private static void ConvertSizedString(ConversionContext ctx)
    {
        if (ctx.Position + 4 > ctx.End) return;

        // Swap the length (uint, 4 bytes)
        SwapUInt32InPlace(ctx.Buffer, ctx.Position);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 4));
        ctx.Position += 4;

        // Skip the string data (chars don't need swapping)
        if (length > 0 && length < 0x10000) // Sanity check
            ctx.Position += (int)length;
    }

    /// <summary>
    ///     Converts a SizedString16 (ushort length + chars) - swaps the length field.
    /// </summary>
    private static void ConvertSizedString16(ConversionContext ctx)
    {
        if (ctx.Position + 2 > ctx.End) return;

        // Swap the length (ushort, 2 bytes)
        SwapUInt16InPlace(ctx.Buffer, ctx.Position);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 2));
        ctx.Position += 2;

        // Skip the string data (chars don't need swapping)
        if (length > 0)
            ctx.Position += length;
    }

    private static void ConvertBasicType(ConversionContext ctx, NifBasicType basic)
    {
        if (ctx.Position + basic.Size > ctx.End) return;

        var pos = ctx.Position; // Save position before modifying

        switch (basic.Size)
        {
            case 1:
                // No swap needed for single bytes
                ctx.Position += 1;
                break;

            case 2:
                SwapUInt16InPlace(ctx.Buffer, pos);
                ctx.Position += 2;
                break;

            case 4:
                SwapUInt32InPlace(ctx.Buffer, pos);
                // Handle block references (Ref, Ptr) that need remapping
                if (basic.IsGeneric)
                    RemapBlockRef(ctx.Buffer, pos, ctx.BlockRemap);
                ctx.Position += 4;
                break;

            case 8:
                SwapUInt64InPlace(ctx.Buffer, pos);
                ctx.Position += 8;
                break;
        }
    }

    private static void RemapBlockRef(byte[] buf, int pos, int[] blockRemap)
    {
        var idx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4));
        if (idx >= 0 && idx < blockRemap.Length)
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), blockRemap[idx]);
    }
}
