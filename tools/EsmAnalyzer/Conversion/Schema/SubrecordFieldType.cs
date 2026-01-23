namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Represents the type of a field within a subrecord schema.
///     These types define how bytes are converted during endian conversion.
/// </summary>
public enum SubrecordFieldType
{
    /// <summary>Single byte - no conversion needed.</summary>
    UInt8,

    /// <summary>Signed byte - no conversion needed.</summary>
    Int8,

    /// <summary>2-byte unsigned integer - requires byte swap.</summary>
    UInt16,

    /// <summary>2-byte signed integer - requires byte swap.</summary>
    Int16,

    /// <summary>4-byte unsigned integer - requires byte swap.</summary>
    UInt32,

    /// <summary>4-byte signed integer - requires byte swap.</summary>
    Int32,

    /// <summary>4-byte FormID reference - requires byte swap.</summary>
    FormId,

    /// <summary>4-byte IEEE 754 float - requires byte swap.</summary>
    Float,

    /// <summary>8-byte unsigned integer - requires byte swap.</summary>
    UInt64,

    /// <summary>8-byte signed integer - requires byte swap.</summary>
    Int64,

    /// <summary>8-byte IEEE 754 double - requires byte swap.</summary>
    Double,

    /// <summary>Fixed-size array of bytes - no conversion needed.</summary>
    ByteArray,

    /// <summary>Null-terminated string - no conversion needed.</summary>
    String,

    /// <summary>3D vector (3 floats, 12 bytes) - requires 3 float swaps.</summary>
    Vec3,

    /// <summary>Quaternion (4 floats, 16 bytes) - requires 4 float swaps.</summary>
    Quaternion,

    /// <summary>RGBA color (4 bytes) - no conversion needed.</summary>
    ColorRgba,

    /// <summary>Position and rotation (6 floats, 24 bytes) - requires 6 float swaps.</summary>
    PosRot,

    /// <summary>Unused/padding bytes - no conversion needed.</summary>
    Padding
}