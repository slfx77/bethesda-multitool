namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Faction from ESM or memory dump.
/// </summary>
public record FactionRecord
{
    /// <summary>FormID of the faction record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Faction flags (raw byte pair from DATA subrecord).</summary>
    public uint Flags { get; init; }

    /// <summary>Whether this faction is hidden from the player.</summary>
    public bool IsHiddenFromPlayer => (Flags & 0x01) != 0;

    /// <summary>Whether this faction allows evil acts.</summary>
    public bool AllowsEvil => (Flags & 0x02) != 0;

    /// <summary>Whether this faction allows special combat.</summary>
    public bool AllowsSpecialCombat => (Flags & 0x04) != 0;

    /// <summary>Whether this faction allows sell (vendor flag).</summary>
    public bool AllowsSell => (Flags & 0x4000) != 0;

    /// <summary>Whether this faction tracks crime.</summary>
    public bool TrackCrime => (Flags & 0x40) != 0;

    /// <summary>Crime gold multiplier from CRVA subrecord.</summary>
    public float CrimeGoldMultiplier { get; init; }

    /// <summary>Faction relations (other faction FormIDs and their disposition).</summary>
    public List<FactionRelation> Relations { get; init; } = [];

    /// <summary>Faction ranks with titles.</summary>
    public List<FactionRank> Ranks { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     A faction rank with male/female titles and insignia path.
/// </summary>
public record FactionRank(int RankNumber, string? MaleTitle, string? FemaleTitle, string? Insignia);
