using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Re-serializes a typed-field dictionary (as produced by
///     <see cref="FalloutXbox360Utils.Core.Formats.Esm.SubrecordSchemaView.Raw" />)
///     back into the byte layout defined by a
///     <see cref="SubrecordSchema" />. Used by encoders for record types whose
///     parsers store schema-parsed dictionaries instead of typed model fields (CSTY, LGTM,
///     WATR DNAM/GNAM). The walk follows the schema's field order, looks up each field by
///     name in the dictionary, and writes the typed value at the cumulative offset.
///     Missing dictionary entries are zero-filled — gives valid output even when the parser
///     dropped fields.
/// </summary>
internal static class SchemaDictionarySerializer
{
    internal static byte[] Serialize(SubrecordSchema schema, IReadOnlyDictionary<string, object?>? values)
    {
        var size = schema.ExpectedSize;
        if (size <= 0)
        {
            return [];
        }

        var bytes = new byte[size];
        var offset = 0;
        foreach (var field in schema.Fields)
        {
            var fieldSize = field.EffectiveSize;
            if (fieldSize <= 0)
            {
                continue;
            }

            if (field.Type != SubrecordFieldType.Padding && values is not null)
            {
                WriteField(bytes, offset, field, values);
            }

            offset += fieldSize;
        }

        return bytes;
    }

    private static void WriteField(
        byte[] bytes, int offset, SubrecordField field, IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue(field.Name, out var value))
        {
            return; // missing — leave zero
        }

        switch (field.Type)
        {
            case SubrecordFieldType.UInt8:
                bytes[offset] = AsByte(value);
                break;
            case SubrecordFieldType.Int8:
                bytes[offset] = (byte)AsSByte(value);
                break;
            case SubrecordFieldType.UInt16:
            case SubrecordFieldType.UInt16LittleEndian:
                SubrecordEncoder.WriteUInt16(bytes, offset, AsUShort(value));
                break;
            case SubrecordFieldType.Int16:
                SubrecordEncoder.WriteInt16(bytes, offset, AsShort(value));
                break;
            case SubrecordFieldType.UInt32:
            case SubrecordFieldType.FormId:
            case SubrecordFieldType.FormIdLittleEndian:
            case SubrecordFieldType.UInt32WordSwapped:
            case SubrecordFieldType.ColorRgba:
            case SubrecordFieldType.ColorArgb:
                SubrecordEncoder.WriteUInt32(bytes, offset, AsUInt(value));
                break;
            case SubrecordFieldType.Int32:
            case SubrecordFieldType.Int32LittleEndian:
                SubrecordEncoder.WriteInt32(bytes, offset, AsInt(value));
                break;
            case SubrecordFieldType.Float:
                SubrecordEncoder.WriteFloat(bytes, offset, AsFloat(value));
                break;
            case SubrecordFieldType.ByteArray:
                if (value is byte[] arr)
                {
                    Array.Copy(arr, 0, bytes, offset, Math.Min(arr.Length, field.EffectiveSize));
                }

                break;
            case SubrecordFieldType.Vec3:
                if (value is float[] vec3 && vec3.Length >= 3)
                {
                    SubrecordEncoder.WriteFloat(bytes, offset + 0, vec3[0]);
                    SubrecordEncoder.WriteFloat(bytes, offset + 4, vec3[1]);
                    SubrecordEncoder.WriteFloat(bytes, offset + 8, vec3[2]);
                }

                break;
            // Quaternion/PosRot/UInt64/Double/String/Padding fall through with zero-fill.
        }
    }

    private static byte AsByte(object? v)
    {
        return v switch
        {
            byte b => b,
            sbyte sb => (byte)sb,
            ushort us => (byte)us,
            short s => (byte)s,
            uint u => (byte)u,
            int i => (byte)i,
            _ => 0
        };
    }

    private static sbyte AsSByte(object? v)
    {
        return v switch
        {
            sbyte sb => sb,
            byte b => (sbyte)b,
            short s => (sbyte)s,
            ushort us => (sbyte)us,
            int i => (sbyte)i,
            uint u => (sbyte)u,
            _ => 0
        };
    }

    private static ushort AsUShort(object? v)
    {
        return v switch
        {
            ushort us => us,
            short s => (ushort)s,
            uint u => (ushort)u,
            int i => (ushort)i,
            byte b => b,
            sbyte sb => (ushort)sb,
            _ => 0
        };
    }

    private static short AsShort(object? v)
    {
        return v switch
        {
            short s => s,
            ushort us => (short)us,
            int i => (short)i,
            uint u => (short)u,
            byte b => b,
            sbyte sb => sb,
            _ => 0
        };
    }

    private static uint AsUInt(object? v)
    {
        return v switch
        {
            uint u => u,
            int i => (uint)i,
            ushort us => us,
            short s => (uint)s,
            byte b => b,
            sbyte sb => (uint)sb,
            _ => 0
        };
    }

    private static int AsInt(object? v)
    {
        return v switch
        {
            int i => i,
            uint u => (int)u,
            short s => s,
            ushort us => us,
            byte b => b,
            sbyte sb => sb,
            _ => 0
        };
    }

    private static float AsFloat(object? v)
    {
        return v switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            uint u => u,
            _ => 0f
        };
    }
}
