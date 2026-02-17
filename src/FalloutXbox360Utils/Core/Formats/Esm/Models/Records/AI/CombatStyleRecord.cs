namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Combat Style (CSTY) record.
///     Defines AI combat behavior parameters (attack chances, dodge, blocking, etc.).
/// </summary>
public record CombatStyleRecord
{
    /// <summary>FormID of the combat style record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Combat style data fields (from CSTD subrecord, schema-parsed).</summary>
    public Dictionary<string, object?>? StyleData { get; init; }

    /// <summary>Advanced combat style data fields (from CSAD subrecord).</summary>
    public Dictionary<string, object?>? AdvancedData { get; init; }

    /// <summary>Simple combat style data (from CSSD subrecord).</summary>
    public Dictionary<string, object?>? SimpleData { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
