namespace FalloutXbox360Utils.Core.Converters.Esm.Schema;

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

    /// <summary>
    ///     4-byte FormID reference that is already little-endian on Xbox 360.
    ///     Used for specific fields (like WEAP DNAM Projectile) where Xbox
    ///     stores the FormID in native little-endian format.
    /// </summary>
    FormIdLittleEndian,

    /// <summary>
    ///     2-byte unsigned integer that is already little-endian on Xbox 360.
    ///     Used for specific fields (like QUST INDX quest stage index) where Xbox
    ///     stores the value in native little-endian format.
    /// </summary>
    UInt16LittleEndian,

    /// <summary>
    ///     4-byte unsigned integer stored in word-swapped (middle-endian) format on Xbox 360.
    ///     Xbox stores as two big-endian uint16 words in little-endian order: [HI_BE][LO_BE]
    ///     Example: value 21 stored as 00 15 00 00 on Xbox -> 15 00 00 00 on PC
    ///     Used for RGDL DATA DynamicBoneCount which follows NIF packed data conventions.
    /// </summary>
    UInt32WordSwapped,

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

    /// <summary>ARGB color (4 bytes) - converts Xbox ARGB to PC RGBA.</summary>
    ColorArgb,

    /// <summary>Position and rotation (6 floats, 24 bytes) - requires 6 float swaps.</summary>
    PosRot,

    /// <summary>Unused/padding bytes - no conversion needed.</summary>
    Padding
}
