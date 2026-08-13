using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Shared inputs for per-child encoding in the planner cell writer: the plan's
///     remap/validity data, the master record dictionary and index, and the (optional)
///     pipeline stats sink for drop accounting.
/// </summary>
internal sealed record CellChildEncodeContext(
    EmitPlan Plan,
    IReadOnlyDictionary<uint, ParsedMainRecord> MasterByFormId,
    HashSet<uint> ValidFormIds,
    PluginBuildOptions Options,
    ConversionPipelineStats? Stats,
    MasterRecordIndex? MasterIndex,
    IReadOnlySet<uint> MasterRefFormIds,
    IReadOnlyDictionary<uint, string>? DmpBaseTypes,
    PlannerXespParentClassifier XespClassifier)
{
    /// <summary>
    ///     Master ref FormIDs the plan emits as overrides ANYWHERE in the plugin (cross-cell
    ///     moves included). A ref's HOME cell must consult this set — not just its own
    ///     per-cell covered set — before carrying it forward or synthesizing a removal, or a
    ///     moved ref would be duplicated (carry) or disabled (tombstone) in the cell the
    ///     capture moved it out of.
    /// </summary>
    public IReadOnlySet<uint> GloballyEmittedMasterRefs { get; init; } =
        ImmutableHashSet<uint>.Empty;

    /// <summary>
    ///     Resolve a placed ref's base record signature (e.g. "CONT", "WEAP"). The final
    ///     (post-remap) base resolves against master; the original captured base resolves
    ///     against the DMP base-type map for proto content the master lacks. Null when
    ///     neither knows it. Used to gate stack-count (XCNT) emission by base kind.
    /// </summary>
    public string? ResolveBaseRecordType(uint originalBaseFormId, uint finalBaseFormId)
    {
        if (MasterByFormId.TryGetValue(finalBaseFormId, out var finalRec))
        {
            return finalRec.Header.Signature;
        }

        if (MasterByFormId.TryGetValue(originalBaseFormId, out var origRec))
        {
            return origRec.Header.Signature;
        }

        return DmpBaseTypes is { } types && types.TryGetValue(originalBaseFormId, out var type)
            ? type
            : null;
    }

    /// <summary>
    ///     True only when <paramref name="refFormId" /> resolves to a live REFR whose NAME
    ///     base is a DOOR. FormID existence alone is insufficient for XTEL: prototype and
    ///     retail data can reuse the same REFR identity with different base types.
    ///     <para>
    ///     Pure plan lookup since 2026-08-12 (retirement Stage H3). <see cref="NavmDoorLinkPlanner" />
    ///     already walks master door placements AND every planned door-based child to build
    ///     <c>ValidDoorRefFormIds</c>; the writer used to re-derive the same answer with an
    ///     O(cells × children) scan per XTEL, which was both the last cross-cell search in
    ///     the writer and a second opinion on a settled decision.
    ///     </para>
    /// </summary>
    public bool IsLiveDoorReference(uint refFormId) =>
        Plan.NavmDoorLinks.ValidDoorRefFormIds.Contains(refFormId);
}

/// <summary>
///     Per-cell mutable state accumulated while encoding one cell's children: the
///     merge-mode classification, marker-drop policy, coverage of master refs by emitted
///     overrides, and the count of genuine (DMP-sourced) children that survived encoding.
/// </summary>
internal sealed class CellEncodeState
{
    public required uint CellFormId { get; init; }
    public required CellMergeMode Mode { get; init; }
    public required bool IsMasterAnchored { get; init; }
    public required bool IsInterior { get; init; }
    public required bool DropRenderCullingMarkers { get; init; }

    /// <summary>
    ///     Planner-settled per-ref verdicts for this cell (keyed by child FormID). Empty when
    ///     the verdict pass didn't run — the writer's transitional decision chain then owns
    ///     each ref's fate.
    /// </summary>
    public ImmutableDictionary<uint, PlacedRefDecision> RefDecisions { get; init; } =
        ImmutableDictionary<uint, PlacedRefDecision>.Empty;

    /// <summary>Master ref FormIDs covered by an emitted override in this cell.</summary>
    public HashSet<uint> CoveredMasterRefFormIds { get; } = [];

    /// <summary>DMP-sourced child records that survived encoding (excludes carry-forwards).</summary>
    public int GenuineChildCount { get; set; }

    /// <summary>Of the genuine children, how many are NAVM records.</summary>
    public int GenuineNavmCount { get; set; }

    /// <summary>
    ///     Genuine children that are NEW (plugin-range FormID) records — proto content the
    ///     master lacks. An interior cell with zero of these is a visited-but-unchanged base
    ///     cell we needn't (and mustn't) override: an ESM interior override routes the cell
    ///     through the engine's fragile master seek/scan attach path, so overriding cells we
    ///     have no new content for only destabilizes base/DLC interiors (e.g. Vault11c).
    /// </summary>
    public int GenuineNewCount { get; set; }

    /// <summary>Emitted FormIDs of this cell's NAVM children (discarded on suppression).</summary>
    public List<uint> EmittedNavmFormIds { get; } = [];
}
