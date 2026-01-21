using static EsmAnalyzer.Conversion.EsmEndianHelpers;

namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Processes subrecord data using schema definitions.
///     This replaces the large switch statement in EsmSubrecordConverter.
/// </summary>
public static class SubrecordSchemaProcessor
{
    /// <summary>
    ///     Converts subrecord data from Xbox 360 (big-endian) to PC (little-endian) format
    ///     using schema-based field definitions.
    /// </summary>
    /// <param name="signature">The 4-character subrecord signature.</param>
    /// <param name="recordType">The parent record type (e.g., "NPC_", "WEAP").</param>
    /// <param name="data">The subrecord data to convert (modified in place).</param>
    /// <param name="customHandler">Delegate for handling custom conversions.</param>
    /// <returns>True if conversion was handled, false if no matching schema found.</returns>
    public static bool TryConvert(
        string signature,
        string recordType,
        byte[] data,
        Func<string, string, byte[], bool>? customHandler = null)
    {
        var schema = SubrecordSchemaRegistry.FindSchema(signature, recordType, data.Length);

        if (schema is null)
            return false;

        // String subrecords need no conversion
        if (schema.IsString)
            return true;

        // Process each field in the schema
        foreach (var field in schema.Fields)
            switch (field.Type)
            {
                case SubrecordFieldType.None:
                case SubrecordFieldType.ByteArray:
                    // No conversion needed
                    break;

                case SubrecordFieldType.UInt16:
                    if (field.Offset >= 0 && field.Offset + 2 <= data.Length)
                        Swap2Bytes(data, field.Offset);
                    break;

                case SubrecordFieldType.UInt32:
                    if (field.Offset >= 0 && field.Offset + 4 <= data.Length)
                        Swap4Bytes(data, field.Offset);
                    break;

                case SubrecordFieldType.UInt64:
                    if (field.Offset >= 0 && field.Offset + 8 <= data.Length)
                        Swap8Bytes(data, field.Offset);
                    break;

                case SubrecordFieldType.UInt16Array:
                    ProcessUInt16Array(data, field);
                    break;

                case SubrecordFieldType.UInt32Array:
                    ProcessUInt32Array(data, field);
                    break;

                case SubrecordFieldType.UInt64Array:
                    ProcessUInt64Array(data, field);
                    break;

                case SubrecordFieldType.PlatformByte:
                    if (field.Offset >= 0 && field.Offset < data.Length)
                        data[field.Offset] = field.PcValue;
                    break;

                case SubrecordFieldType.Custom:
                    if (customHandler is not null && schema.CustomHandler is not null)
                        if (!customHandler(schema.CustomHandler, recordType, data))
                            return false;
                    break;
            }

        return true;
    }

    private static void ProcessUInt16Array(byte[] data, SubrecordField field)
    {
        var startOffset = field.Offset >= 0 ? field.Offset : 0;
        var count = field.Count;

        if (count < 0)
            // Fill remaining space
            count = (data.Length - startOffset) / 2;

        for (var i = 0; i < count; i++)
        {
            var offset = startOffset + i * 2;
            if (offset + 2 <= data.Length)
                Swap2Bytes(data, offset);
        }
    }

    private static void ProcessUInt32Array(byte[] data, SubrecordField field)
    {
        var startOffset = field.Offset >= 0 ? field.Offset : 0;
        var count = field.Count;

        if (count < 0)
            // Fill remaining space
            count = (data.Length - startOffset) / 4;

        for (var i = 0; i < count; i++)
        {
            var offset = startOffset + i * 4;
            if (offset + 4 <= data.Length)
                Swap4Bytes(data, offset);
        }
    }

    private static void ProcessUInt64Array(byte[] data, SubrecordField field)
    {
        var startOffset = field.Offset >= 0 ? field.Offset : 0;
        var count = field.Count;

        if (count < 0)
            // Fill remaining space
            count = (data.Length - startOffset) / 8;
        for (var i = 0; i < count; i++)
        {
            var offset = startOffset + i * 8;
            if (offset + 8 <= data.Length)
                Swap8Bytes(data, offset);
        }
    }

    /// <summary>
    ///     Checks if a subrecord is a string type (no conversion needed).
    /// </summary>
    public static bool IsStringSubrecord(string signature, string recordType)
    {
        var schema = SubrecordSchemaRegistry.FindSchema(signature, recordType, 0);
        return schema?.IsString ?? false;
    }
}