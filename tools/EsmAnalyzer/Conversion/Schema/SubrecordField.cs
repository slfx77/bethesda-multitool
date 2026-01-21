namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Defines a single field within a subrecord structure.
/// </summary>
/// <param name="Type">The primitive type of this field.</param>
/// <param name="Offset">Byte offset from start of subrecord data (-1 for array types that fill remaining space).</param>
/// <param name="Count">
///     For array types: number of elements (-1 means fill remaining space).
///     For scalar types: should be 1 (or use ItemSize for repeated structs).
/// </param>
/// <param name="ItemSize">Size of each item in bytes (used for struct arrays where items have internal layout).</param>
/// <param name="PcValue">For PlatformByte type: the value to write on PC.</param>
public readonly record struct SubrecordField(
    SubrecordFieldType Type,
    int Offset = 0,
    int Count = 1,
    int ItemSize = 0,
    byte PcValue = 0)
{
    /// <summary>Creates a 2-byte field at the specified offset.</summary>
    public static SubrecordField UInt16(int offset)
    {
        return new SubrecordField(SubrecordFieldType.UInt16, offset);
    }

    /// <summary>Creates a 4-byte field at the specified offset.</summary>
    public static SubrecordField UInt32(int offset)
    {
        return new SubrecordField(SubrecordFieldType.UInt32, offset);
    }

    /// <summary>Creates an 8-byte field at the specified offset.</summary>
    public static SubrecordField UInt64(int offset)
    {
        return new SubrecordField(SubrecordFieldType.UInt64, offset);
    }

    /// <summary>Creates an array of 2-byte values starting at offset.</summary>
    /// <param name="offset">Start offset (-1 for start of data).</param>
    /// <param name="count">Number of elements (-1 to fill remaining space).</param>
    public static SubrecordField UInt16Array(int offset = 0, int count = -1)
    {
        return new SubrecordField(SubrecordFieldType.UInt16Array, offset, count);
    }

    /// <summary>Creates an array of 4-byte values starting at offset.</summary>
    /// <param name="offset">Start offset (-1 for start of data).</param>
    /// <param name="count">Number of elements (-1 to fill remaining space).</param>
    public static SubrecordField UInt32Array(int offset = 0, int count = -1)
    {
        return new SubrecordField(SubrecordFieldType.UInt32Array, offset, count);
    }

    /// <summary>Creates an array of 8-byte values starting at offset.</summary>
    /// <param name="offset">Start offset.</param>
    /// <param name="count">Number of elements (-1 to fill remaining space).</param>
    public static SubrecordField UInt64Array(int offset = 0, int count = -1)
    {
        return new SubrecordField(SubrecordFieldType.UInt64Array, offset, count);
    }

    /// <summary>Creates a platform-specific byte field that should be set to pcValue on PC.</summary>
    public static SubrecordField PlatformByte(int offset, byte pcValue)
    {
        return new SubrecordField(SubrecordFieldType.PlatformByte, offset, 1, 0, pcValue);
    }

    /// <summary>Marks this subrecord as needing custom conversion logic.</summary>
    public static SubrecordField Custom()
    {
        return new SubrecordField(SubrecordFieldType.Custom);
    }
}