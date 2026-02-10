using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Creature from memory dump.
///     Similar to NPC but for non-human entities.
/// </summary>
public record CreatureRecord
{
    /// <summary>FormID of the creature record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Actor base stats from ACBS subrecord.</summary>
    public ActorBaseSubrecord? Stats { get; init; }

    /// <summary>Creature type (0=Animal, 1=MutatedAnimal, 2=MutatedInsect, etc.).</summary>
    public byte CreatureType { get; init; }

    /// <summary>Combat skill level.</summary>
    public byte CombatSkill { get; init; }

    /// <summary>Magic skill level.</summary>
    public byte MagicSkill { get; init; }

    /// <summary>Stealth skill level.</summary>
    public byte StealthSkill { get; init; }

    /// <summary>Attack damage.</summary>
    public short AttackDamage { get; init; }

    /// <summary>Script FormID.</summary>
    public uint? Script { get; init; }

    /// <summary>Death item FormID.</summary>
    public uint? DeathItem { get; init; }

    /// <summary>Model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Faction memberships.</summary>
    public List<FactionMembership> Factions { get; init; } = [];

    /// <summary>Spell/ability FormIDs.</summary>
    public List<uint> Spells { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable creature type name.</summary>
    public string CreatureTypeName => CreatureType switch
    {
        0 => "Animal",
        1 => "Mutated Animal",
        2 => "Mutated Insect",
        3 => "Abomination",
        4 => "Super Mutant",
        5 => "Feral Ghoul",
        6 => "Robot",
        7 => "Giant",
        _ => $"Unknown ({CreatureType})"
    };
}
