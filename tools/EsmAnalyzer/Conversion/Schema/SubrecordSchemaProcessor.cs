using static EsmAnalyzer.Conversion.EsmEndianHelpers;

namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Processes subrecord data using schema definitions.
/// </summary>
public static class SubrecordSchemaProcessor
{
    /// <summary>
    ///     Converts subrecord data based on schema.
    ///     Returns null if no schema is found.
    /// </summary>
    public static byte[]? ConvertWithSchema(string signature, ReadOnlySpan<byte> data, string recordType)
    {
        // Check for string subrecords first
        if (SubrecordSchemaRegistry.IsStringSubrecord(signature, recordType))
            return data.ToArray();

        // Get schema for this subrecord
        var schema = SubrecordSchemaRegistry.GetSchema(signature, recordType, data.Length);
        if (schema == null)
        {
            // Navmesh subrecords require custom parsing logic
            if (signature == "NVMI" && recordType == "NAVI")
            {
                var navmi = data.ToArray();
                EsmSubrecordConverter.ConvertNvmi(navmi);
                return navmi;
            }

            if (signature == "NVCI" && recordType == "NAVI")
            {
                var nvci = data.ToArray();
                EsmSubrecordConverter.ConvertNvci(nvci);
                return nvci;
            }

            if (signature == "NVGD" && recordType == "NAVM")
            {
                var nvgd = data.ToArray();
                EsmSubrecordConverter.ConvertNvgd(nvgd);
                return nvgd;
            }

            return null; // No schema - caller should handle
        }

        var result = data.ToArray();

        // Handle special schema types
        if (ReferenceEquals(schema, SubrecordSchema.String) ||
            ReferenceEquals(schema, SubrecordSchema.ByteArray))
            return result; // No conversion needed

        if (ReferenceEquals(schema, SubrecordSchema.FormIdArray))
        {
            ConvertFormIdArray(result);
            return result;
        }

        if (ReferenceEquals(schema, SubrecordSchema.FloatArray))
        {
            ConvertFloatArray(result);
            return result;
        }

        if (ReferenceEquals(schema, SubrecordSchema.TextureHashes))
        {
            ConvertTextureHashes(result);
            return result;
        }

        // Handle ATXT/BTXT special case (platform flag conversion)
        if ((signature == "ATXT" || signature == "BTXT") && data.Length == 8)
        {
            ConvertAtxtBtxt(result);
            return result;
        }

        // Handle AIDT special case (zero Xbox-specific bytes)
        if (signature == "AIDT" && data.Length == 20)
        {
            ConvertAidt(result);
            return result;
        }

        // Handle repeating arrays (ExpectedSize < 0 means repeat fields)
        if (schema.ExpectedSize < 0 && schema.Fields.Length > 0)
        {
            // Repeating structure - repeat fields until data is consumed
            var elementSize = schema.Fields.Sum(f => f.EffectiveSize);
            if (elementSize > 0 && data.Length % elementSize == 0)
            {
                var elementCount = data.Length / elementSize;
                for (var i = 0; i < elementCount; i++) ConvertFieldsAtOffset(result, i * elementSize, schema.Fields);
            }
            else if (schema.Fields.Length == 1)
            {
                // Single repeating field
                ConvertSingleRepeatingField(result, schema.Fields[0]);
            }

            return result;
        }

        // Variable length (ExpectedSize == 0) or fixed size (ExpectedSize > 0)
        // Process fields once, leave any remaining bytes untouched
        ConvertFieldsAtOffset(result, 0, schema.Fields);
        return result;
    }

    /// <summary>
    ///     Converts fields at a given offset.
    /// </summary>
    private static void ConvertFieldsAtOffset(byte[] data, int baseOffset, SubrecordField[] fields)
    {
        var offset = baseOffset;

        foreach (var field in fields)
        {
            if (offset >= data.Length)
                break;

            var size = field.EffectiveSize;
            if (offset + size > data.Length)
                break;

            ConvertField(data, offset, field.Type);
            offset += size;
        }
    }

    /// <summary>
    ///     Converts a single field at the given offset.
    /// </summary>
    private static void ConvertField(byte[] data, int offset, SubrecordFieldType type)
    {
        switch (type)
        {
            case SubrecordFieldType.UInt16:
            case SubrecordFieldType.Int16:
                Swap2Bytes(data, offset);
                break;

            case SubrecordFieldType.UInt32:
            case SubrecordFieldType.Int32:
            case SubrecordFieldType.FormId:
            case SubrecordFieldType.Float:
                Swap4Bytes(data, offset);
                break;

            case SubrecordFieldType.UInt64:
            case SubrecordFieldType.Int64:
            case SubrecordFieldType.Double:
                Swap8Bytes(data, offset);
                break;

            case SubrecordFieldType.Vec3:
                Swap4Bytes(data, offset);
                Swap4Bytes(data, offset + 4);
                Swap4Bytes(data, offset + 8);
                break;

            case SubrecordFieldType.Quaternion:
                Swap4Bytes(data, offset);
                Swap4Bytes(data, offset + 4);
                Swap4Bytes(data, offset + 8);
                Swap4Bytes(data, offset + 12);
                break;

            case SubrecordFieldType.PosRot:
                // 6 floats
                for (var i = 0; i < 6; i++)
                    Swap4Bytes(data, offset + i * 4);
                break;

            case SubrecordFieldType.UInt8:
            case SubrecordFieldType.Int8:
            case SubrecordFieldType.ByteArray:
            case SubrecordFieldType.String:
            case SubrecordFieldType.ColorRgba:
            case SubrecordFieldType.Padding:
                // No conversion needed
                break;
        }
    }

    /// <summary>
    ///     Converts a single repeating field across the entire data.
    /// </summary>
    private static void ConvertSingleRepeatingField(byte[] data, SubrecordField field)
    {
        var size = field.EffectiveSize;
        if (size <= 0 || data.Length % size != 0)
            return;

        var count = data.Length / size;
        for (var i = 0; i < count; i++) ConvertField(data, i * size, field.Type);
    }

    /// <summary>
    ///     Converts an array of FormIDs (4 bytes each).
    /// </summary>
    private static void ConvertFormIdArray(byte[] data)
    {
        for (var i = 0; i < data.Length; i += 4)
            Swap4Bytes(data, i);
    }

    /// <summary>
    ///     Converts an array of floats (4 bytes each).
    /// </summary>
    private static void ConvertFloatArray(byte[] data)
    {
        for (var i = 0; i < data.Length; i += 4)
            Swap4Bytes(data, i);
    }

    /// <summary>
    ///     Converts texture hashes (8 bytes each).
    /// </summary>
    private static void ConvertTextureHashes(byte[] data)
    {
        for (var i = 0; i < data.Length; i += 8)
            Swap8Bytes(data, i);
    }

    /// <summary>
    ///     Converts ATXT/BTXT with platform flag.
    /// </summary>
    private static void ConvertAtxtBtxt(byte[] data)
    {
        Swap4Bytes(data, 0); // FormID
        data[5] = 0x88; // Platform flag - set to PC value
        Swap2Bytes(data, 6); // Layer
    }

    /// <summary>
    ///     Converts AIDT with Xbox-specific bytes zeroed.
    /// </summary>
    private static void ConvertAidt(byte[] data)
    {
        // Zero Xbox-specific bytes 5-7
        data[5] = 0;
        data[6] = 0;
        data[7] = 0;
    }

    /// <summary>
    ///     Checks if a subrecord is a string (no conversion needed).
    /// </summary>
    public static bool IsStringSubrecord(string signature, string recordType)
    {
        return SubrecordSchemaRegistry.IsStringSubrecord(signature, recordType);
    }
}