namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Parsed Encounter Zone (ECZN) record. Defines the difficulty / ownership /
///     reset behavior for a region of placed actors and items.
/// </summary>
public record EncounterZoneRecord
{
    /// <summary>FormID of the encounter zone.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (rarely set on ECZN — usually null).</summary>
    public string? FullName { get; init; }

    /// <summary>Owner FormID (FACT / NPC_) from DATA byte 0-3.</summary>
    public uint OwnerFormId { get; init; }

    /// <summary>Rank within the owner faction (-1 = ignore) from DATA byte 4.</summary>
    public sbyte Rank { get; init; }

    /// <summary>Minimum level cap (0 = ignore, -1 = match player) from DATA byte 5.</summary>
    public sbyte MinimumLevel { get; init; }

    /// <summary>Flags bitmask from DATA byte 6 (NeverResets, MatchPCBelowMinLevel, DisableCombatBoundary).</summary>
    public byte Flags { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
