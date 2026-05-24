namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Result of the weapon sound layout probe.
///     Carries the selected layout variant (V1 vs V2). The fine-grained shift dimension
///     was deleted in Phase 1B.6 — always 0 across every observed dump.
/// </summary>
internal sealed record RuntimeWeaponSoundProbeResult(
    RuntimeWeaponSoundLayoutVariant Variant,
    int WinnerScore,
    int RunnerUpScore,
    int SampleCount,
    bool IsHighConfidence)
{
    public int Margin => WinnerScore - RunnerUpScore;
}
