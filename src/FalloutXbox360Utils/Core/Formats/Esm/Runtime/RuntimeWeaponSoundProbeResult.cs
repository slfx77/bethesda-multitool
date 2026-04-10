namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

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
