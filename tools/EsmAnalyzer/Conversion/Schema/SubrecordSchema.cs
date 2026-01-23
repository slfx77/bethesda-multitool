namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Represents the schema for a subrecord - defines how to convert its bytes.
/// </summary>
public sealed class SubrecordSchema
{
    /// <summary>
    ///     Creates a schema from a sequence of fields.
    /// </summary>
    public SubrecordSchema(params SubrecordField[] fields)
    {
        Fields = fields;
        ExpectedSize = fields.Sum(f => f.EffectiveSize);
    }

    /// <summary>
    ///     The ordered list of fields in this subrecord.
    /// </summary>
    public SubrecordField[] Fields { get; }

    /// <summary>
    ///     Expected total size in bytes (sum of all field sizes).
    ///     0 means variable-length subrecord.
    /// </summary>
    public int ExpectedSize { get; init; }

    /// <summary>
    ///     Optional constraint on parent record type.
    ///     If null, schema applies to all record types.
    /// </summary>
    public string? RecordType { get; init; }

    /// <summary>
    ///     Optional constraint on data length.
    ///     If null, schema applies to any length matching ExpectedSize.
    /// </summary>
    public int? DataLength { get; init; }

    /// <summary>
    ///     Human-readable description of this subrecord format.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    ///     Marker for string subrecords - no conversion needed.
    /// </summary>
    public static SubrecordSchema String { get; } = new()
    {
        ExpectedSize = 0,
        Description = "String data - no conversion"
    };

    /// <summary>
    ///     Marker for byte array subrecords - no conversion needed.
    /// </summary>
    public static SubrecordSchema ByteArray { get; } = new()
    {
        ExpectedSize = 0,
        Description = "Byte array - no conversion"
    };

    /// <summary>
    ///     Creates a schema for an array of FormIDs (variable length, 4 bytes each).
    /// </summary>
    public static SubrecordSchema FormIdArray { get; } = new(SubrecordField.FormId("Item"))
    {
        ExpectedSize = -1, // Repeating array
        Description = "Array of FormIDs"
    };

    /// <summary>
    ///     Creates a schema for an array of floats (variable length, 4 bytes each).
    /// </summary>
    public static SubrecordSchema FloatArray { get; } = new(SubrecordField.Float("Value"))
    {
        ExpectedSize = -1, // Repeating array
        Description = "Array of floats"
    };

    /// <summary>
    ///     Creates a schema for texture hashes (8 bytes per entry).
    /// </summary>
    public static SubrecordSchema TextureHashes { get; } = new(SubrecordField.UInt64("Hash"))
    {
        ExpectedSize = -1, // Repeating array
        Description = "Texture hash array (8 bytes per hash)"
    };

    /// <summary>
    ///     Creates a schema that matches any data length (repeating element).
    /// </summary>
    public static SubrecordSchema Repeating(int elementSize, SubrecordField[] elementFields, string? description = null)
    {
        return new SubrecordSchema(elementFields)
        {
            ExpectedSize = 0, // Variable
            Description = description
        };
    }

    /// <summary>
    ///     Creates a simple 4-byte swap schema (FormID, uint32, float).
    /// </summary>
    public static SubrecordSchema Simple4Byte(string name = "Value")
    {
        return new SubrecordSchema(SubrecordField.UInt32(name));
    }

    /// <summary>
    ///     Creates a simple 2-byte swap schema (uint16, int16).
    /// </summary>
    public static SubrecordSchema Simple2Byte(string name = "Value")
    {
        return new SubrecordSchema(SubrecordField.UInt16(name));
    }
}