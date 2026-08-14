using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Reserved, currently unused experiment for replaying a planner-computed
///     <see cref="SubrecordDecision" /> list. Production override emission uses
///     <c>RecordMergeEngine</c> and does not call this type.
/// </summary>
/// <remarks>
///     No current planner stage populates a generic ordered decision list and this method
///     always throws. Do not treat <see cref="RecordPlan.OverrideSubrecords" /> as an active
///     generic writer contract until both production wiring and tests exist.
/// </remarks>
public static class SubrecordReplay
{
    /// <summary>
    ///     Unimplemented; no production caller exists.
    /// </summary>
    public static IReadOnlyList<EncodedSubrecord> Replay(
        ParsedMainRecord master,
        EncodedRecord encoded,
        IReadOnlyList<SubrecordDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(decisions);

        throw new NotImplementedException(
            "SubrecordReplay is not implemented or wired into production override emission.");
    }
}

