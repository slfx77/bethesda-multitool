namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     VATS attack data parsed from a WEAP VATS subrecord (20 bytes).
///     Layout from PDB OBJ_WEAP_VATS_SPECIAL (size 20):
///     +0  pVATSSpecialEffect (TESForm*, 4 bytes)
///     +4  fVATSSpecialAP (float, AP cost)
///     +8  fVATSSpecialMultiplier (float, damage multiplier)
///     +12 fVATSSkillRequired (float, skill requirement)
///     +16 bSilent (bool, 1 byte)
///     +17 bModRequired (bool, 1 byte)
///     +18 cFlags (uint8 — additional flags)
///     +19 padding
/// </summary>
public record VatsAttackData
{
    /// <summary>FormID of the magic effect applied during a successful VATS attack.</summary>
    public uint EffectFormId { get; init; }

    /// <summary>Action point cost when attacking via VATS.</summary>
    public float ActionPointCost { get; init; }

    /// <summary>Damage multiplier in VATS.</summary>
    public float DamageMultiplier { get; init; }

    /// <summary>Skill requirement for the VATS attack.</summary>
    public float SkillRequired { get; init; }

    /// <summary>True if this VATS attack is silent (no enemy alert).</summary>
    public bool IsSilent { get; init; }

    /// <summary>True if this VATS attack requires a mod to be installed.</summary>
    public bool RequiresMod { get; init; }

    /// <summary>Additional packed flags byte (cFlags from PDB).</summary>
    public byte ExtraFlags { get; init; }
}
