using FalloutXbox360Utils.Core.Converters.Esm.Schema;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Provides schema-based reading of ESM subrecord data.
///     Wraps SubrecordSchemaReader to provide a unified interface for
///     both conversion and analysis code paths.
/// </summary>
public static class SubrecordDataReader
{
    /// <summary>
    ///     Reads all fields from a subrecord using the schema registry.
    ///     Automatically handles endianness based on file format (BE for Xbox 360, LE for PC).
    /// </summary>
    /// <param name="signature">4-character subrecord signature (e.g., "ACBS", "AIDT").</param>
    /// <param name="recordType">Optional parent record type for context-specific schemas (e.g., "NPC_", "WEAP").</param>
    /// <param name="data">Raw subrecord data (excluding 6-byte header).</param>
    /// <param name="bigEndian">True for Xbox 360 (BE), false for PC (LE).</param>
    /// <returns>Dictionary of field names to values, or empty if no schema found.</returns>
    public static Dictionary<string, object?> ReadFields(
        string signature,
        string? recordType,
        ReadOnlySpan<byte> data,
        bool bigEndian)
    {
        return SubrecordSchemaReader.ReadFields(signature, data, recordType ?? "", bigEndian);
    }

    /// <summary>
    ///     Reads all fields from a subrecord using the schema registry.
    ///     Overload for byte array data.
    /// </summary>
    public static Dictionary<string, object?> ReadFields(
        string signature,
        string? recordType,
        byte[] data,
        bool bigEndian)
    {
        return SubrecordSchemaReader.ReadFields(signature, data.AsSpan(), recordType ?? "", bigEndian);
    }

    /// <summary>
    ///     Checks if a schema exists for the given signature and record type.
    /// </summary>
    public static bool HasSchema(string signature, string? recordType, int dataLength)
    {
        return SubrecordSchemaRegistry.GetSchema(signature, recordType ?? "", dataLength) != null;
    }

    /// <summary>
    ///     Gets a typed field value from the fields dictionary.
    /// </summary>
    public static T? GetField<T>(Dictionary<string, object?> fields, string fieldName)
    {
        if (fields.TryGetValue(fieldName, out var value) && value is T typedValue)
        {
            return typedValue;
        }

        return default;
    }

    /// <summary>
    ///     Gets a uint field, with fallback to default if not found or wrong type.
    /// </summary>
    public static uint GetUInt32(Dictionary<string, object?> fields, string fieldName, uint defaultValue = 0)
    {
        if (fields.TryGetValue(fieldName, out var value))
        {
            return value switch
            {
                uint u => u,
                int i => (uint)i,
                ushort us => us,
                short s => (uint)s,
                byte b => b,
                sbyte sb => (uint)sb,
                _ => defaultValue
            };
        }

        return defaultValue;
    }

    /// <summary>
    ///     Gets a ushort field, with fallback to default if not found or wrong type.
    /// </summary>
    public static ushort GetUInt16(Dictionary<string, object?> fields, string fieldName, ushort defaultValue = 0)
    {
        if (fields.TryGetValue(fieldName, out var value))
        {
            return value switch
            {
                ushort us => us,
                short s => (ushort)s,
                uint u => (ushort)u,
                int i => (ushort)i,
                byte b => b,
                sbyte sb => (ushort)sb,
                _ => defaultValue
            };
        }

        return defaultValue;
    }

    /// <summary>
    ///     Gets a short field, with fallback to default if not found or wrong type.
    /// </summary>
    public static short GetInt16(Dictionary<string, object?> fields, string fieldName, short defaultValue = 0)
    {
        if (fields.TryGetValue(fieldName, out var value))
        {
            return value switch
            {
                short s => s,
                ushort us => (short)us,
                int i => (short)i,
                uint u => (short)u,
                byte b => b,
                sbyte sb => sb,
                _ => defaultValue
            };
        }

        return defaultValue;
    }

    /// <summary>
    ///     Gets a byte field, with fallback to default if not found or wrong type.
    /// </summary>
    public static byte GetByte(Dictionary<string, object?> fields, string fieldName, byte defaultValue = 0)
    {
        if (fields.TryGetValue(fieldName, out var value))
        {
            return value switch
            {
                byte b => b,
                sbyte sb => (byte)sb,
                ushort us => (byte)us,
                short s => (byte)s,
                uint u => (byte)u,
                int i => (byte)i,
                _ => defaultValue
            };
        }

        return defaultValue;
    }

    /// <summary>
    ///     Gets a float field, with fallback to default if not found or wrong type.
    /// </summary>
    public static float GetFloat(Dictionary<string, object?> fields, string fieldName, float defaultValue = 0f)
    {
        if (fields.TryGetValue(fieldName, out var value))
        {
            return value switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                uint u => u,
                _ => defaultValue
            };
        }

        return defaultValue;
    }

    /// <summary>
    ///     Gets a string field, with fallback to default if not found or wrong type.
    /// </summary>
    public static string? GetString(Dictionary<string, object?> fields, string fieldName)
    {
        if (fields.TryGetValue(fieldName, out var value) && value is string s)
        {
            return s;
        }

        return null;
    }
}
