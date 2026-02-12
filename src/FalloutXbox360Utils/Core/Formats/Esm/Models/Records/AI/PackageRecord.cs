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
