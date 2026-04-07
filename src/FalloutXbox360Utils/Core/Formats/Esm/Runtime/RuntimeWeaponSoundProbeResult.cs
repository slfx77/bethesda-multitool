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

/// <summary>
///     Result of the weapon sound layout probe.
///     Carries the selected layout variant plus a fine-grained shift for builds that
///     drift from the reference offsets within a variant.
/// </summary>
internal sealed record RuntimeWeaponSoundProbeResult(
    RuntimeWeaponSoundLayoutVariant Variant,
    int FineShift,
    int WinnerScore,
    int RunnerUpScore,
    int SampleCount,
    bool IsHighConfidence)
{
    public int Margin => WinnerScore - RunnerUpScore;
}
