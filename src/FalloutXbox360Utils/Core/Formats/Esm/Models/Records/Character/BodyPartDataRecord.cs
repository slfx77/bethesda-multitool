namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Body Part Data (BPTD) record.
///     Defines body part hit zones for dismemberment/VATS targeting.
/// </summary>
public record BodyPartDataRecord
{
    /// <summary>FormID of the body part data record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Body part names (BPTN subrecords).</summary>
    public List<string> PartNames { get; init; } = [];

    /// <summary>Body part node names (BPNN subrecords).</summary>
    public List<string> NodeNames { get; init; } = [];

    /// <summary>Number of texture variants (NAM5 subrecord).</summary>
    public uint TextureCount { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
