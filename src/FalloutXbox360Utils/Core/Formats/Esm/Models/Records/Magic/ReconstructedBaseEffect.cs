namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Reconstructed Base Effect (MGEF) from memory dump.
///     Foundation for resolving effect names on enchantments and spells.
/// </summary>
public record ReconstructedBaseEffect
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? Description { get; init; }

    /// <summary>4-char effect code from EDID (e.g., "REFA" for fire resist).</summary>
    public string? EffectCode { get; init; }

    /// <summary>Flags from DATA.</summary>
    public uint Flags { get; init; }

    /// <summary>Base cost from DATA.</summary>
    public float BaseCost { get; init; }

    /// <summary>Associated item FormID from DATA.</summary>
    public uint AssociatedItem { get; init; }

    /// <summary>Magic school from DATA.</summary>
    public int MagicSchool { get; init; }

    /// <summary>Resist value (actor value index) from DATA.</summary>
    public int ResistValue { get; init; }

    /// <summary>Archetype from DATA (e.g., ValueModifier, Script, Absorb).</summary>
    public uint Archetype { get; init; }

    /// <summary>Actor value used for the effect (first/primary) from DATA.</summary>
    public int ActorValue { get; init; }

    /// <summary>Projectile FormID from DATA.</summary>
    public uint Projectile { get; init; }

    /// <summary>Explosion FormID from DATA.</summary>
    public uint Explosion { get; init; }

    /// <summary>Icon path from ICON subrecord.</summary>
    public string? Icon { get; init; }

    /// <summary>Model path from MODL subrecord.</summary>
    public string? ModelPath { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    public string ArchetypeName => Archetype switch
    {
        0 => "ValueModifier",
        1 => "Script",
        2 => "Dispel",
        3 => "CureDisease",
        5 => "Absorb",
        6 => "DualValueModifier",
        7 => "Calm",
        8 => "Demoralize",
        9 => "Frenzy",
        10 => "Disarm",
        11 => "CommandCreature",
        12 => "CommandHumanoid",
        13 => "Invisibility",
        14 => "Light",
        17 => "Paralysis",
        18 => "NightEye",
        19 => "Darkness",
        24 => "Telekinesis",
        25 => "CureParalysis",
        30 => "CureAddiction",
        31 => "CurePoison",
        32 => "Concussion",
        33 => "ValueAndParts",
        34 => "LimbCondition",
        35 => "Turbo",
        _ => $"Unknown({Archetype})"
    };
}
