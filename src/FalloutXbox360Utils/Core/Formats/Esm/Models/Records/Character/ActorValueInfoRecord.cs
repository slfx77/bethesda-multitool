namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Actor Value Info (AVIF) record.
///     Defines a stat, skill, or attribute (Strength, Guns, Action Points, etc.).
/// </summary>
public record ActorValueInfoRecord
{
    /// <summary>FormID of the actor value info record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (FULL subrecord).</summary>
    public string? FullName { get; init; }

    /// <summary>Description (DESC subrecord).</summary>
    public string? Description { get; init; }

    /// <summary>Icon path (ICON subrecord).</summary>
    public string? Icon { get; init; }

    /// <summary>Abbreviation (ANAM subrecord).</summary>
    public string? Abbreviation { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
