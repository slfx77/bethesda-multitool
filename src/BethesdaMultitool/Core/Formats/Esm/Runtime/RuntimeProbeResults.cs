using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

/// <summary>
///     Container for all runtime layout probe results. Passed to <see cref="RuntimeStructReader" />
///     constructor so individual readers can access their probe results without parameter explosion.
///     Phase 1B.6 removed six dead probes (Book / Magic / Terminal / WeaponCrit / Land /
///     WeaponSound.FineShift); the remaining five fields are the probes that actually find
///     non-zero per-build drift in observed dumps.
/// </summary>
internal sealed class RuntimeProbeResults
{
    /// <summary>
    ///     REFR field-shift (early/final) probe result. Surfaced so <c>dmp probe-shifts</c> can report
    ///     the winner's score/margin/samples — a thin margin flags an outlier dump whose shift pick is
    ///     low-confidence, which now also governs the heap-sweep recovery (it reuses this shift).
    /// </summary>
    public RuntimeLayoutProbeResult<bool>? RefrLayout { get; init; }

    public RuntimeNpcLayoutProbeResult? NpcLayout { get; init; }
    public RuntimeWorldCellLayoutProbeResult? WorldCellLayout { get; init; }
    public RuntimeLayoutProbeResult<int[]>? RaceLayout { get; init; }
    public RuntimeLayoutProbeResult<int[]>? EffectLayout { get; init; }
    public RuntimeWeaponSoundProbeResult? WeaponSoundLayout { get; init; }

    /// <summary>
    ///     Probed AMMO_DATA start offset (fSpeed/iFlags/pProjectile). The block drifts per build (172/184/188
    ///     observed) while the PDB-derived default 184 is correct only for late-April Beta, so this is probed
    ///     per DMP. Surfaced so <c>dmp probe-shifts</c> can report the winner's score/margin/samples.
    /// </summary>
    public RuntimeLayoutProbeResult<int>? AmmoDataLayout { get; init; }

    /// <summary>
    ///     Per-FormType uniform shift for the generic PDB reader.
    ///     Key = FormType byte, Value = shift in bytes to apply to all non-TESForm fields.
    /// </summary>
    public IReadOnlyDictionary<byte, int>? GenericTypeShifts { get; init; }

    /// <summary>
    ///     FormID → enumerated runtime entry lookup. Surfaced on <see cref="RuntimeMemoryContext" />
    ///     so specialized readers can resolve candidate pointers to their EditorIds — primarily
    ///     used by the QUST script scan to look up candidate Script* pointers before validating
    ///     via the Script.pOwnerQuest backpointer.
    /// </summary>
    public IReadOnlyDictionary<uint, RuntimeEditorIdEntry>? EditorIdsByFormId { get; init; }
}
