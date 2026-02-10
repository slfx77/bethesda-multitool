namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Furniture (FURN) record.
///     A sittable/sleepable world object with marker flags for available positions.
/// </summary>
public record FurnitureRecord
{
    /// <summary>FormID of the furniture record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Script FormID (SCRI subrecord).</summary>
    public uint? Script { get; init; }

    /// <summary>Furniture marker flags (MNAM subrecord) — active positions bitmask.</summary>
    public uint MarkerFlags { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
