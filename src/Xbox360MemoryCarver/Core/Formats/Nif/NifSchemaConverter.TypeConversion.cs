// Type conversion methods for NifSchemaConverter

using System.Buffers.Binary;
using static Xbox360MemoryCarver.Core.Formats.Nif.NifEndianUtils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

// Type conversion methods
internal sealed partial class NifSchemaConverter
{
    private void ConvertSingleValue(ConversionContext ctx, string typeName, int depth = 0)
    {
        // Resolve template type placeholder (#T#) to the actual type
        if (typeName == "#T#")
        {
            if (ctx.TemplateType != null)
            {
                typeName = ctx.TemplateType;
            }
            else
            {
                Log.Trace("    [Schema] WARNING: #T# used without template context, cannot resolve");
                return;
            }
        }

        // Handle SizedString explicitly (inline string with uint length prefix)
        // Note: "string" is a struct that contains either SizedString (old) or NiFixedString (new)
        // based on version, so we let it fall through to struct handling
        if (typeName == "SizedString")
        {
            ConvertSizedString(ctx);
            return;
        }

        if (typeName == "SizedString16")
        {
            ConvertSizedString16(ctx);
            return;
        }

        // Check basic types first
        if (_schema.BasicTypes.TryGetValue(typeName, out var basic))
        {
            ConvertBasicType(ctx, basic);
            return;
        }

        // Check enums (convert based on storage type)
        if (_schema.Enums.TryGetValue(typeName, out var enumDef))
        {
            if (_schema.BasicTypes.TryGetValue(enumDef.Storage, out var storageType))
                ConvertBasicType(ctx, storageType);
            return;
        }

        // Check structs (recursively convert fields OR bulk swap if fixed size)
        if (_schema.Structs.TryGetValue(typeName, out var structDef))
        {
            // Some structs with fixed size (like HavokFilter) are packed bitfields that should
            // be swapped as a single unit rather than field-by-field. The fields describe the
            // layout AFTER endian conversion, not before.
            if (structDef.FixedSize is 2 or 4 or 8)
            {
                // Bulk swap the entire struct as a single unit
                if (structDef.FixedSize == 2) SwapUInt16InPlace(ctx.Buffer, ctx.Position);
                else if (structDef.FixedSize == 4) SwapUInt32InPlace(ctx.Buffer, ctx.Position);
                else if (structDef.FixedSize == 8) SwapUInt64InPlace(ctx.Buffer, ctx.Position);
                ctx.Position += structDef.FixedSize.Value;
                return;
            }

            // Clear any field values that this struct defines, so each struct instance
            // in an array starts fresh. This prevents stale values from previous instances
            // from affecting conditional field parsing.
            foreach (var field in structDef.Fields) ctx.FieldValues.Remove(field.Name);

            ConvertFields(ctx, structDef.Fields, depth + 1);
            return;
        }

        // Unknown type - try to look up size and bulk swap
        var size = _schema.GetTypeSize(typeName);
        if (size.HasValue && size.Value > 0)
        {
            // Bulk swap based on size
            if (size.Value == 2) SwapUInt16InPlace(ctx.Buffer, ctx.Position);
            else if (size.Value == 4) SwapUInt32InPlace(ctx.Buffer, ctx.Position);
            else if (size.Value == 8) SwapUInt64InPlace(ctx.Buffer, ctx.Position);
            ctx.Position += size.Value;
        }
        else
        {
            Log.Trace($"    [Schema] WARNING: Unknown type '{typeName}' with no size, cannot advance position");
        }
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
