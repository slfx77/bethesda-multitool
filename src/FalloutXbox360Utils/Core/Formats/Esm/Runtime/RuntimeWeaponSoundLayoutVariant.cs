namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Identifies which TESObjectWEAP sound block layout the dump uses.
///     V1 — early FO3-derived builds (xex, xex1, xex2): 7 sound fields, no Distant /
///     AttackLoop / Idle / MeleeBlock / ModSilenced. Fire2D directly follows Fire3D.
///     V2 — all FNV-era builds (xex3+, MemDebug, Debug, Jacobstown): 14 sound fields
///     including all FNV additions.
/// </summary>
internal enum RuntimeWeaponSoundLayoutVariant
{
    V2 = 0,
    V1 = 1
}