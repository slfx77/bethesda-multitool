namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;

/// <summary>
///     Package idle-marker collection. Runtime fields match PDB BGSIdleCollection:
///     cIdleFlags, cIdleCount, pIdleArray, and fTimerCheckForIdle.
/// </summary>
public record PackageIdleCollection
{
    public byte Flags { get; init; }
    public byte Count { get; init; }
    public float TimerCheckForIdle { get; init; }
    public List<uint> IdleAnimationFormIds { get; init; } = [];
}
