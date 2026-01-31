namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Generic detected subrecord from memory dump.
///     Used for any subrecord type defined in the schema.
/// </summary>
public record DetectedSubrecord
{
    /// <summary>4-character subrecord signature (e.g., "DATA", "EDID").</summary>
    public required string Signature { get; init; }

    /// <summary>Offset in the dump where subrecord was found.</summary>
    public long Offset { get; init; }

    /// <summary>Size of subrecord data in bytes.</summary>
    public int DataSize { get; init; }

    /// <summary>Whether detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Raw data bytes (for post-processing).</summary>
    public byte[]? RawData { get; init; }

    /// <summary>Parsed field values (if schema was applied).</summary>
    public Dictionary<string, object?>? Fields { get; init; }
}
