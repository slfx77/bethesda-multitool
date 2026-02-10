namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Form ID List (FLST) record.
///     A utility record containing an ordered list of FormIDs, used for
///     leveled lists, quest targets, faction relations, and other groupings.
/// </summary>
public record FormListRecord
{
    /// <summary>FormID of the form list record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>List of FormIDs (LNAM subrecords).</summary>
    public List<uint> FormIds { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
