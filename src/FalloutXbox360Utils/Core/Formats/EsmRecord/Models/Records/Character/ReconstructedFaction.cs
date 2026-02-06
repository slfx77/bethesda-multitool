namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed Faction from memory dump.
/// </summary>
public record ReconstructedFaction
{
    /// <summary>FormID of the faction record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Faction flags.</summary>
    public uint Flags { get; init; }

    /// <summary>Whether this faction is hidden from the player.</summary>
    public bool IsHiddenFromPlayer => (Flags & 0x01) != 0;

    /// <summary>Whether this faction allows evil acts.</summary>
    public bool AllowsEvil => (Flags & 0x02) != 0;

    /// <summary>Whether this faction allows special combat.</summary>
    public bool AllowsSpecialCombat => (Flags & 0x04) != 0;

    /// <summary>Faction relations (other faction FormIDs and their disposition).</summary>
    public List<FactionRelation> Relations { get; init; } = [];

    /// <summary>Faction rank names.</summary>
    public List<string> RankNames { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
