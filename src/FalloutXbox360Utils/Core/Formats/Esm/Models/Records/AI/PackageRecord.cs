namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Minimal AI Package (PACK) record, storing only location data needed for spawn resolution.
/// </summary>
public record PackageRecord
{
    /// <summary>FormID of the PACK record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Primary package location (PLDT subrecord).</summary>
    public PackageLocation? Location { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Package location from PLDT/PLD2 subrecord (12 bytes).
/// </summary>
public record PackageLocation
{
    /// <summary>Location type: 0=NearRef, 1=InCell, 2=NearCurrent, 3=NearEditor, 4=ObjectID, 5=ObjectType, 12=NearLinkedRef.</summary>
    public byte Type { get; init; }

    /// <summary>FormID or enum value, meaning depends on Type.</summary>
    public uint Union { get; init; }

    /// <summary>Search radius.</summary>
    public int Radius { get; init; }
}
