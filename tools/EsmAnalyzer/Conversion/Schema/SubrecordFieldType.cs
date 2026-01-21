namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Defines the primitive field types that can appear in subrecord structures.
/// </summary>
public enum SubrecordFieldType
{
    /// <summary>No conversion needed (single byte or string).</summary>
    None,

    /// <summary>2-byte value (uint16/int16).</summary>
    UInt16,

    /// <summary>4-byte value (uint32/int32/float/FormID).</summary>
    UInt32,

    /// <summary>8-byte value (uint64).</summary>
    UInt64,

    /// <summary>Array of bytes (no conversion).</summary>
    ByteArray,

    /// <summary>Array of 2-byte values.</summary>
    UInt16Array,

    /// <summary>Array of 4-byte values.</summary>
    UInt32Array,

    /// <summary>Array of 8-byte values.</summary>
    UInt64Array,

    /// <summary>Platform-specific byte that needs specific PC value.</summary>
    PlatformByte,

    /// <summary>Custom handler required (complex logic).</summary>
    Custom
}