namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Defines the conversion rules for a specific subrecord type.
/// </summary>
/// <param name="Signature">The 4-character subrecord signature (e.g., "NAME", "EDID").</param>
/// <param name="Fields">Array of field definitions describing the layout.</param>
/// <param name="RecordTypes">
///     Optional: Only apply this schema when parent record is one of these types.
///     Null means apply to all record types.
/// </param>
/// <param name="ExcludeRecordTypes">
///     Optional: Do NOT apply this schema when parent record is one of these types.
/// </param>
/// <param name="DataLength">
///     Optional: Only apply this schema when data length matches.
///     -1 means apply to any length.
/// </param>
/// <param name="MinLength">Optional: Only apply when data length >= this value.</param>
/// <param name="MaxLength">Optional: Only apply when data length &lt;= this value.</param>
/// <param name="CustomHandler">
///     Optional: Name of the custom handler method for complex conversions.
///     Only used when Fields contains a Custom field type.
/// </param>
/// <param name="IsString">
///     True if this subrecord contains string data (no byte swapping needed).
/// </param>
public sealed record SubrecordSchema(
    string Signature,
    SubrecordField[] Fields,
    string[]? RecordTypes = null,
    string[]? ExcludeRecordTypes = null,
    int DataLength = -1,
    int MinLength = 0,
    int MaxLength = int.MaxValue,
    string? CustomHandler = null,
    bool IsString = false,
    bool IsFormId = false,
    bool IsFormIdArray = false)
{
    /// <summary>Creates a schema for a simple string subrecord (no conversion).</summary>
    public static SubrecordSchema String(string signature, string[]? recordTypes = null,
        string[]? excludeRecordTypes = null)
    {
        return new SubrecordSchema(signature, [], recordTypes, excludeRecordTypes, IsString: true);
    }

    /// <summary>Creates a schema for a simple FormID/uint32 subrecord.</summary>
    public static SubrecordSchema FormId(string signature, string[]? recordTypes = null,
        string[]? excludeRecordTypes = null, int dataLength = -1)
    {
        return new SubrecordSchema(signature, [SubrecordField.UInt32(0)], recordTypes, excludeRecordTypes, dataLength,
            IsFormId: true);
    }

    /// <summary>Creates a schema for an array of FormIDs/uint32s.</summary>
    public static SubrecordSchema FormIdArray(string signature, string[]? recordTypes = null,
        string[]? excludeRecordTypes = null)
    {
        return new SubrecordSchema(signature, [SubrecordField.UInt32Array()], recordTypes, excludeRecordTypes,
            IsFormIdArray: true);
    }

    /// <summary>Creates a schema for an array of uint16s.</summary>
    public static SubrecordSchema UInt16Array(string signature, string[]? recordTypes = null,
        string[]? excludeRecordTypes = null)
    {
        return new SubrecordSchema(signature, [SubrecordField.UInt16Array()], recordTypes, excludeRecordTypes);
    }

    /// <summary>Creates a schema requiring a custom handler.</summary>
    public static SubrecordSchema Custom(string signature, string handlerName, string[]? recordTypes = null,
        int dataLength = -1)
    {
        return new SubrecordSchema(signature, [SubrecordField.Custom()], recordTypes, DataLength: dataLength,
            CustomHandler: handlerName);
    }

    /// <summary>Checks if this schema matches the given context.</summary>
    public bool Matches(string signature, string recordType, int dataLength)
    {
        if (Signature != signature)
            return false;

        if (DataLength >= 0 && DataLength != dataLength)
            return false;

        if (dataLength < MinLength || dataLength > MaxLength)
            return false;

        if (RecordTypes is not null && !RecordTypes.Contains(recordType))
            return false;

        if (ExcludeRecordTypes is not null && ExcludeRecordTypes.Contains(recordType))
            return false;

        return true;
    }
}