namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed Consumable (ALCH) from memory dump.
///     Aggregates data from ALCH main record header, DATA, ENIT, EFID subrecords.
/// </summary>
public record ReconstructedConsumable
{
    /// <summary>FormID of the consumable record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // DATA subrecord (4 bytes)
    /// <summary>Weight in units.</summary>
    public float Weight { get; init; }

    // ENIT subrecord (20 bytes)
    /// <summary>Base value in caps.</summary>
    public uint Value { get; init; }

    /// <summary>Addiction FormID (if addictive).</summary>
    public uint? AddictionFormId { get; init; }

    /// <summary>Addiction chance (0.0-1.0).</summary>
    public float AddictionChance { get; init; }

    /// <summary>Effect FormIDs (EFID subrecords).</summary>
    public List<uint> EffectFormIds { get; init; } = [];

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
