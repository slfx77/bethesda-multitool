namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed Perk from memory dump.
///     Aggregates data from PERK main record header, DATA, DESC, PRKE chains.
/// </summary>
public record ReconstructedPerk
{
    /// <summary>FormID of the perk record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Perk description (DESC subrecord).</summary>
    public string? Description { get; init; }

    // DATA subrecord
    /// <summary>Is this a trait (1) or regular perk (0).</summary>
    public byte Trait { get; init; }

    /// <summary>Minimum level to take this perk.</summary>
    public byte MinLevel { get; init; }

    /// <summary>Number of ranks available.</summary>
    public byte Ranks { get; init; }

    /// <summary>Is this perk visible to players (1) or hidden (0).</summary>
    public byte Playable { get; init; }

    /// <summary>Icon file path (ICON/MICO subrecord).</summary>
    public string? IconPath { get; init; }

    /// <summary>Perk entries (PRKE/PRKC/EPFT chains).</summary>
    public List<PerkEntry> Entries { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Whether this is a trait rather than a perk.</summary>
    public bool IsTrait => Trait != 0;

    /// <summary>Whether this perk is visible in the perk selection UI.</summary>
    public bool IsPlayable => Playable != 0;
}
