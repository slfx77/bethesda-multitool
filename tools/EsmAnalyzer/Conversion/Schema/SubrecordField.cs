namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Represents a single field within a subrecord schema.
/// </summary>
/// <param name="Name">Human-readable name for documentation.</param>
/// <param name="Type">The data type of this field.</param>
/// <param name="Size">Size in bytes (0 for variable-size types like String).</param>
public readonly record struct SubrecordField(string Name, SubrecordFieldType Type, int Size = 0)
{
    /// <summary>Creates a UInt8 field.</summary>
    public static SubrecordField UInt8(string name) => new(name, SubrecordFieldType.UInt8, 1);

    /// <summary>Creates an Int8 field.</summary>
    public static SubrecordField Int8(string name) => new(name, SubrecordFieldType.Int8, 1);

    /// <summary>Creates a UInt16 field.</summary>
    public static SubrecordField UInt16(string name) => new(name, SubrecordFieldType.UInt16, 2);

    /// <summary>Creates an Int16 field.</summary>
    public static SubrecordField Int16(string name) => new(name, SubrecordFieldType.Int16, 2);

    /// <summary>Creates a UInt32 field.</summary>
    public static SubrecordField UInt32(string name) => new(name, SubrecordFieldType.UInt32, 4);

    /// <summary>Creates an Int32 field.</summary>
    public static SubrecordField Int32(string name) => new(name, SubrecordFieldType.Int32, 4);

    /// <summary>Creates a FormId field.</summary>
    public static SubrecordField FormId(string name) => new(name, SubrecordFieldType.FormId, 4);

    /// <summary>Creates a Float field.</summary>
    public static SubrecordField Float(string name) => new(name, SubrecordFieldType.Float, 4);

    /// <summary>Creates a UInt64 field.</summary>
    public static SubrecordField UInt64(string name) => new(name, SubrecordFieldType.UInt64, 8);

    /// <summary>Creates a Double field.</summary>
    public static SubrecordField Double(string name) => new(name, SubrecordFieldType.Double, 8);

    /// <summary>Creates a byte array field with fixed size.</summary>
    public static SubrecordField Bytes(string name, int size) => new(name, SubrecordFieldType.ByteArray, size);

    /// <summary>Creates a Vec3 field (3 floats).</summary>
    public static SubrecordField Vec3(string name) => new(name, SubrecordFieldType.Vec3, 12);

    /// <summary>Creates a Quaternion field (4 floats).</summary>
    public static SubrecordField Quaternion(string name) => new(name, SubrecordFieldType.Quaternion, 16);

    /// <summary>Creates an RGBA color field (4 bytes).</summary>
    public static SubrecordField ColorRgba(string name) => new(name, SubrecordFieldType.ColorRgba, 4);

    /// <summary>Creates a PosRot field (6 floats for position + rotation).</summary>
    public static SubrecordField PosRot(string name) => new(name, SubrecordFieldType.PosRot, 24);

    /// <summary>Creates padding/unused bytes.</summary>
    public static SubrecordField Padding(int size) => new("Unused", SubrecordFieldType.Padding, size);

    /// <summary>
    ///     Gets the effective size of this field.
    ///     Returns the specified size, or calculates from type if not specified.
    /// </summary>
    public int EffectiveSize => Size > 0 ? Size : Type switch
    {
        SubrecordFieldType.UInt8 => 1,
        SubrecordFieldType.Int8 => 1,
        SubrecordFieldType.UInt16 => 2,
        SubrecordFieldType.Int16 => 2,
        SubrecordFieldType.UInt32 => 4,
        SubrecordFieldType.Int32 => 4,
        SubrecordFieldType.FormId => 4,
        SubrecordFieldType.Float => 4,
        SubrecordFieldType.UInt64 => 8,
        SubrecordFieldType.Int64 => 8,
        SubrecordFieldType.Double => 8,
        SubrecordFieldType.Vec3 => 12,
        SubrecordFieldType.Quaternion => 16,
        SubrecordFieldType.ColorRgba => 4,
        SubrecordFieldType.PosRot => 24,
        _ => 0
    };
}
