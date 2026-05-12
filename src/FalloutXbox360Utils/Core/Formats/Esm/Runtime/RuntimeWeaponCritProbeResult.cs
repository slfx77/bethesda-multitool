namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Result of the weapon critical-data layout probe.
///     <see cref="CritBlockShift" /> is the offset adjustment to apply on top of the
///     reference <c>OBJ_WEAP_CRITICAL</c> position (PDB +464) so that CritDamage /
///     CritChance / EffectOnDeath / CritEffect read correctly for the dump under test.
/// </summary>
internal sealed record RuntimeWeaponCritProbeResult(
    int CritBlockShift,
    int WinnerScore,
    int RunnerUpScore,
    int SampleCount,
    bool IsHighConfidence)
{
    public int Margin => WinnerScore - RunnerUpScore;
}
