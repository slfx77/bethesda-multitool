using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     The planner's decision for one worldspace and the cells it owns. New WRLDs that
///     carry captured cells emit alongside their World Children GRUP — same shape as
///     legacy <c>PreEncodeNewWorldspacesWithCells</c>. Existing master WRLDs are
///     <c>KeepMaster</c> and the writer just nests cells under them.
/// </summary>
public sealed record WorldspacePlan
{
    /// <summary>Master / emitted FormID of the WRLD record.</summary>
    public required uint WorldspaceFormId { get; init; }

    /// <summary>
    ///     The planner decision for the WRLD anchor. <c>KeepMaster</c> means the writer
    ///     consults the master ESM bytes; <c>New</c> means the writer emits the WRLD
    ///     record immediately above the World Children GRUP it owns.
    /// </summary>
    public required RecordPlan WorldspaceRecordPlan { get; init; }

    /// <summary>
    ///     FormIDs of every cell that emits under this worldspace, in the order the
    ///     writer produces them (block / sub-block sorted). Each FormID maps to a
    ///     <see cref="CellPlan" /> in <c>EmitPlan.CellsByFormId</c>.
    /// </summary>
    public required ImmutableArray<uint> CellFormIds { get; init; }
}
