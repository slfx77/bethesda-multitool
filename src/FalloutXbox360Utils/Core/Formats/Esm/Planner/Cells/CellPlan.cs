using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     The planner's decision for one cell. Carries the CELL anchor's disposition plus
///     the planned children (REFR/ACHR/ACRE in persistent / VWD / temporary buckets, with
///     LAND and NAVM in the temporary bucket per the legacy emission order).
/// </summary>
/// <remarks>
///     Children arrays are empty in Tier 5b.1 (this commit ships the data model + catalog
///     phase only). Tier 5b.2 populates them when the CellChildAllocator + walkers land.
///     The writer in Tier 5b.4 reads this record and produces the per-cell GRUP structure
///     legacy <c>CellGrupBuilder</c> currently produces.
/// </remarks>
public sealed record CellPlan
{
    /// <summary>Master / emitted FormID of the CELL anchor record.</summary>
    public required uint CellFormId { get; init; }

    /// <summary>
    ///     The planner decision for the CELL anchor itself. For <c>KeepMaster</c> the
    ///     plugin emits nothing (the master ESM owns this CELL); for <c>Override</c> the
    ///     planner produces an override record; for <c>New</c> a fresh CELL is allocated.
    /// </summary>
    public required RecordPlan CellRecordPlan { get; init; }

    /// <summary>
    ///     Master ESM grouping context (block / sub-block labels, interior / exterior
    ///     classification). Required so the writer can reproduce the legacy GRUP nesting
    ///     instead of recomputing it from grid coordinates.
    /// </summary>
    public required PcEsmCellContext Context { get; init; }

    /// <summary>
    ///     REFR / ACHR / ACRE records emitted inside the cell's Persistent Children GRUP
    ///     (type 8). Empty during Tier 5b.1.
    /// </summary>
    public required ImmutableArray<RecordPlan> PersistentChildren { get; init; }

    /// <summary>
    ///     REFR records emitted inside the cell's Visible-When-Distant Children GRUP
    ///     (type 10). Empty during Tier 5b.1.
    /// </summary>
    public required ImmutableArray<RecordPlan> VwdChildren { get; init; }

    /// <summary>
    ///     LAND + NAVM + REFR / ACHR / ACRE records emitted inside the cell's Temporary
    ///     Children GRUP (type 9). Order matters: LAND first, NAVMs next, placed refs after.
    ///     Empty during Tier 5b.1.
    /// </summary>
    public required ImmutableArray<RecordPlan> TemporaryChildren { get; init; }

    /// <summary>FormID of the parent worldspace; null for interior cells.</summary>
    public uint? ParentWorldspaceFormId { get; init; }
}
