using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     The planner's decision for one cell. Carries the CELL anchor's disposition plus
///     the planned children (REFR/ACHR/ACRE in persistent / VWD / temporary buckets, with
///     LAND and NAVM in the temporary bucket per the legacy emission order).
/// </summary>
/// <remarks>
///     <c>CellSectionPlanner</c> populates the child arrays and merge mode; the subsequent
///     verdict pass settles per-reference and cell-level emission decisions before the
///     planned cell writer builds GRUP bytes.
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
    ///     classification). Required so the writer can reproduce the source hierarchy
    ///     rather than recomputing it from grid coordinates.
    /// </summary>
    public required PcEsmCellContext Context { get; init; }

    /// <summary>
    ///     REFR / ACHR / ACRE records planned inside the cell's Persistent Children GRUP
    ///     (type 8).
    /// </summary>
    public required ImmutableArray<RecordPlan> PersistentChildren { get; init; }

    /// <summary>
    ///     REFR records planned inside the cell's Visible-When-Distant Children GRUP
    ///     (type 10).
    /// </summary>
    public required ImmutableArray<RecordPlan> VwdChildren { get; init; }

    /// <summary>
    ///     LAND + NAVM + REFR / ACHR / ACRE records emitted inside the cell's Temporary
    ///     Children GRUP (type 9). Order matters: LAND first, NAVMs next, placed refs after.
    /// </summary>
    public required ImmutableArray<RecordPlan> TemporaryChildren { get; init; }

    /// <summary>FormID of the parent worldspace; null for interior cells.</summary>
    public uint? ParentWorldspaceFormId { get; init; }

    // --- Planner-settled emission decisions. These are the writer's ONLY source for these
    //     choices: the writer-side fallbacks that used to recompute them were deleted with
    //     the legacy emission path (2026-08-11), so a cell reaching the writer without them
    //     throws rather than quietly taking a second opinion. ---

    /// <summary>
    ///     Binary cell-merge classification (PersistentOnly vs LoadedReplacement vs Skip) that
    ///     drives per-ref preservation. Settled by <c>CellSectionPlanner.PlanMergeMode</c>.
    ///     <para>
    ///         Nullable only as a construction-state marker for hand-built/intermediate plans.
    ///         Both verdict planning and writing reject an unset value.
    ///     </para>
    /// </summary>
    public CellMergeMode? Mode { get; init; }

    /// <summary>
    ///     True when captured render-culling markers (room/portal/occlusion/multibound) must be
    ///     dropped in this replaced interior. Derived from <see cref="Mode" /> + context.
    /// </summary>
    public bool DropRenderCullingMarkers { get; init; }

    /// <summary>
    ///     The planner's cell-emission verdict: true = emit this cell's bundle, false = suppress
    ///     (ITM / interior-no-new-content / navmesh-only). Settled by <c>PlanCellGates</c>;
    ///     null means the later verdict pass has not run, and the writer rejects it.
    /// </summary>
    public bool? Emits { get; init; }

    /// <summary>
    ///     Stat code for a planned suppression (<c>cell.itm-override-suppressed</c> /
    ///     <c>cell.interior-no-new-content-suppressed</c>); null when <see cref="Emits" />
    ///     is true or unplanned.
    /// </summary>
    public string? SuppressReason { get; init; }

    /// <summary>
    ///     True when the planned genuine children are navmeshes only — the writer must
    ///     discard the NAVM prefix (a navmesh-only master-cell override transfers
    ///     temp-children ownership for nothing) before the ITM suppression applies.
    /// </summary>
    public bool NavmOnlySuppressed { get; init; }

    /// <summary>
    ///     Per-placed-ref emit/drop verdicts keyed by child <c>RecordPlan.FormId</c>. Empty until
    ///     the per-ref-verdict stage populates it; the writer then serializes verdicts instead of
    ///     re-deriving base resolution / recovery / drops / bucketing.
    /// </summary>
    public ImmutableDictionary<uint, PlacedRefDecision> RefDecisions { get; init; } =
        ImmutableDictionary<uint, PlacedRefDecision>.Empty;
}
