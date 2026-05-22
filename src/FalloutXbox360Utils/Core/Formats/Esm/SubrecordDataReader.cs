namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Type-coercion helpers for the schema-decoded subrecord field dictionary produced by
///     <see cref="SubrecordSchemaView" />. The view delegates its typed accessors to the
///     <c>GetXxx</c> methods below — they handle the cross-type promotions (uint→int,
///     short→byte, etc.) that arise because <see cref="Conversion.Schema.SubrecordSchemaReader" />
///     returns values boxed as <see cref="object" />.
/// </summary>
public static class SubrecordDataReader
{
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
    ///     Gets an int field, with fallback to default if not found or wrong type.
    /// </summary>
    public static int GetInt32(Dictionary<string, object?> fields, string fieldName, int defaultValue = 0)
    {
        if (fields.TryGetValue(fieldName, out var value))
        {
            return value switch
            {
                int i => i,
                uint u => (int)u,
                short s => s,
                ushort us => us,
                byte b => b,
                sbyte sb => sb,
                _ => defaultValue
            };
        }

        return defaultValue;
    }

    /// <summary>
    ///     Gets an sbyte field, with fallback to default if not found or wrong type.
    /// </summary>
    public static sbyte GetSByte(Dictionary<string, object?> fields, string fieldName, sbyte defaultValue = 0)
    {
        if (fields.TryGetValue(fieldName, out var value))
        {
            return value switch
            {
                sbyte sb => sb,
                byte b => (sbyte)b,
                short s => (sbyte)s,
                ushort us => (sbyte)us,
                int i => (sbyte)i,
                uint u => (sbyte)u,
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
