namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     A recovered destruction block — the <c>DEST</c> header plus its <c>DSTD</c> stages.
///     <para>
///         Sourced from the runtime <c>DestructibleObjectData</c> allocation that
///         <c>BGSDestructibleObjectForm.pData</c> points at. The header maps onto the 8-byte DEST
///         schema exactly (<c>iHealth</c> → Health, <c>cNumStages</c> → the stage count, <c>cFlags</c>
///         → Flags, two trailing pad bytes in both), and each <see cref="DestructionStage" /> maps
///         onto the 20-byte DSTD schema.
///     </para>
///     <para>
///         <b>The stage list is what makes the header safe to write.</b> DEST's count is not
///         decorative: the engine sizes its stage array from it and then fills the slots from the
///         DSTD blocks that follow, so emitting a header with a non-zero count and no stages leaves
///         that array unpopulated. Header and stages therefore travel together, and a capture that
///         resolved the header but not the stages reports zero stages rather than a count it cannot
///         back up.
///     </para>
/// </summary>
public sealed record DestructionData(
    int Health,
    byte Flags,
    IReadOnlyList<DestructionStage> Stages);

/// <summary>
///     One destruction stage. Field order follows the runtime
///     <c>DestructibleObjectStage</c> (24 bytes), not the DSTD wire order — the two differ, and
///     DSTD's <c>Index</c> has no runtime member because the engine takes it from the stage's
///     position in the array.
/// </summary>
public sealed record DestructionStage(
    byte HealthPercent,
    byte DamageStage,
    byte Flags,
    int SelfDamagePerSecond,
    uint ExplosionFormId,
    uint DebrisFormId,
    int DebrisCount,
    string? ReplacementModel);
