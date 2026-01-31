namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed Worldspace from memory dump.
/// </summary>
public record ReconstructedWorldspace
{
    /// <summary>FormID of the WRLD record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "WastelandNV").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Parent worldspace FormID (if any).</summary>
    public uint? ParentWorldspaceFormId { get; init; }

    /// <summary>Climate FormID (CNAM subrecord).</summary>
    public uint? ClimateFormId { get; init; }

    /// <summary>Water FormID (NAM2 subrecord).</summary>
    public uint? WaterFormId { get; init; }

    /// <summary>Cells belonging to this worldspace.</summary>
    public List<ReconstructedCell> Cells { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
