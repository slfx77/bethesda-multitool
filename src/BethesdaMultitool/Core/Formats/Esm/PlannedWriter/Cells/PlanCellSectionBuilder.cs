using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     The planner-side equivalent of legacy <see cref="CellGrupBuilder.BuildCellSection" />.
///     Walks <see cref="EmitPlan.CellsByFormId" />, encodes each cell's children via the
///     planned encoders (<see cref="PlannedPlacedRefEncoder" /> for placed refs, override
///     merges included), carries forward uncovered master children per the cell-ownership
///     rule (<see cref="MasterChildCarryForward" />), and delegates the GRUP framing to the
///     legacy <see cref="CellGrupBuilder" /> so nesting/labels match byte-for-byte.
/// </summary>
internal static class PlanCellSectionBuilder
{
    private const uint PersistentFlag = 0x00000400u;
    private const uint CompressedFlag = 0x00040000u;

    public static byte[]? BuildCellSection(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        PluginBuildOptions options,
        ConversionPipelineStats? stats = null,
        MasterRecordIndex? masterIndex = null)
    {
        return BuildCellSectionCore(plan, masterByFormId, options, stats, masterIndex).SectionBytes;
    }

    /// <summary>
    ///     Full-result variant: also reports which NAVM FormIDs were written and which placed
    ///     refs/children the bundles ended up containing. The written-NAVM set is retained as
    ///     the writer's own view for NVEX sanitation; NAVI now takes its rows and NVCI graph
    ///     from the plan (<c>PlanNavmEmission</c> / <c>PlanNavmConnectivity</c>), which is what
    ///     lets NAVI be built without waiting for this pass.
    /// </summary>
    internal static CellSectionBuildResult BuildCellSectionCore(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        PluginBuildOptions options,
        ConversionPipelineStats? stats = null,
        MasterRecordIndex? masterIndex = null,
        IReadOnlyDictionary<uint, string>? dmpBaseTypes = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(masterByFormId);
        ArgumentNullException.ThrowIfNull(options);

        // Master ∪ emitted, the same validity set legacy BuildPlacedRefValidFormIds feeds
        // into RefrEncoder for optional FormID-bearing subrecords (XEZN/XLKR/XOWN/XESP/XTEL).
        var validFormIds = new HashSet<uint>(plan.EmittedFormIds);
        foreach (var formId in masterByFormId.Keys)
        {
            validFormIds.Add(formId);
        }

        var masterRefFormIds = masterIndex is null
            ? []
            : new HashSet<uint>(masterIndex.RefToCell.Keys);

        // The XESP census indexes every captured child, so only build it when a stats sink
        // is actually collecting.
        var xespClassifier = new PlannerXespParentClassifier(
            plan, masterByFormId, masterRefFormIds, stats is not null);

        // Master ref FormIDs the plan emits anywhere (cross-cell moves included): a moved
        // ref's home cell must neither carry-forward nor tombstone it.
        var globallyEmittedMasterRefs = new HashSet<uint>();
        foreach (var plannedCell in plan.CellsByFormId.Values)
        {
            foreach (var (refFormId, decision) in plannedCell.RefDecisions)
            {
                if (decision.Verdict == PlacedRefEmitVerdict.Emit && decision.MarksMasterCovered)
                {
                    globallyEmittedMasterRefs.Add(refFormId);
                }
            }
        }

        var context = new CellChildEncodeContext(
            plan, masterByFormId, validFormIds, options, stats, masterIndex, masterRefFormIds,
            dmpBaseTypes, xespClassifier)
        {
            GloballyEmittedMasterRefs = globallyEmittedMasterRefs
        };

        var bundles = ConvertCellsToBundles(plan, context, out var emittedNavmFormIds);
        if (bundles.Count == 0)
        {
            return new CellSectionBuildResult(null, emittedNavmFormIds,
                ImmutableHashSet<uint>.Empty, ImmutableHashSet<uint>.Empty);
        }

        SanitizeNavmNvexInBundles(bundles, emittedNavmFormIds, masterByFormId);
        DeletedRefLinkStripper.Apply(bundles, stats);

        var newWorldspaces = BuildNewWorldspaces(plan, options);

        var sectionBytes = CellGrupBuilder.BuildCellSection(
            bundles, masterByFormId, newWorldspaces);
        return new CellSectionBuildResult(
            sectionBytes,
            emittedNavmFormIds,
            EmittedPlacedReferenceCollector.Collect(bundles),
            OverriddenChildFormIdCollector.Collect(bundles));
    }

    /// <summary>
    ///     Post-emission pass that drops NVEX entries pointing at NAVM FormIDs that aren't
    ///     in the emitted set, and patches DATA.EdgeLinkCount to match the kept entries.
    ///     Mirrors the retired cell-merge NVEX sanitation hook.
    ///     Without this the engine spams PATHFINDING errors every frame for plugin NAVMs
    ///     whose NVEX cross-links don't resolve, eventually filling the log to GB.
    /// </summary>
    private static void SanitizeNavmNvexInBundles(
        List<CellOverrideBundle> bundles,
        IReadOnlySet<uint> emittedNavmFormIds,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId)
    {
        // Valid NVEX targets = NAVMs actually written (not merely planned) ∪ master's own.
        var validTargets = new HashSet<uint>(emittedNavmFormIds);
        foreach (var (formId, record) in masterByFormId)
        {
            if (string.Equals(record.Header.Signature, "NAVM", StringComparison.Ordinal))
            {
                validTargets.Add(formId);
            }
        }

        for (var b = 0; b < bundles.Count; b++)
        {
            var bundle = bundles[b];
            var newTemp = new List<byte[]>(bundle.TemporaryChildRecords.Count);
            var bundleChanged = false;
            foreach (var rec in bundle.TemporaryChildRecords)
            {
                if (rec.Length < 4 || rec[0] != (byte)'N' || rec[1] != (byte)'A'
                    || rec[2] != (byte)'V' || rec[3] != (byte)'M')
                {
                    newTemp.Add(rec);
                    continue;
                }

                var sanitized = NavMeshByteRewriter.SanitizeNvexInNavmRecord(rec, validTargets, out _);
                newTemp.Add(sanitized);
                if (!ReferenceEquals(sanitized, rec))
                {
                    bundleChanged = true;
                }
            }

            if (bundleChanged)
            {
                bundles[b] = bundle with { TemporaryChildRecords = newTemp };
            }
        }
    }

    /// <summary>
    ///     Translate each <see cref="RecordDisposition.New" /> worldspace plan into the
    ///     legacy <see cref="NewWorldspaceEntry" /> shape <see cref="CellGrupBuilder" />
    ///     expects: keyed by source DMP FormID, value = emitted FormID + encoded record
    ///     bytes. Subsumes legacy <c>PreEncodeNewWorldspacesWithCells</c>.
    /// </summary>
    private static Dictionary<uint, NewWorldspaceEntry>? BuildNewWorldspaces(
        EmitPlan plan, PluginBuildOptions options)
    {
        if (plan.WorldspacesByFormId.IsEmpty)
        {
            return null;
        }

        Dictionary<uint, NewWorldspaceEntry>? result = null;

        foreach (var wrldPlan in plan.WorldspacesByFormId.Values)
        {
            if (wrldPlan.WorldspaceRecordPlan.Disposition != RecordDisposition.New)
            {
                continue;
            }

            if (wrldPlan.WorldspaceRecordPlan.Model is not WorldspaceRecord wrld)
            {
                continue;
            }

            var encoded = WrldEncoder.EncodeNew(wrld);
            if (encoded.Subrecords.Count == 0)
            {
                continue;
            }

            var flags = options.CompressRecords ? CompressedFlag : 0u;
            var bytes = PluginRecordByteBuilder.BuildNewRecordBytes(
                "WRLD", wrldPlan.WorldspaceFormId, flags, encoded.Subrecords);

            // Key by source FormID so the legacy framing's lookup-by-DMP-FormID still works.
            var sourceFormId = wrldPlan.WorldspaceRecordPlan.SourceFormId ?? wrldPlan.WorldspaceFormId;
            result ??= [];
            result[sourceFormId] = new NewWorldspaceEntry(wrldPlan.WorldspaceFormId, bytes);
        }

        return result;
    }

    /// <summary>
    ///     Convert each <see cref="CellPlan" /> entry to a bundle the legacy framing engine
    ///     consumes: classify the cell's merge mode, encode the genuine (DMP-sourced)
    ///     children, suppress information-free overrides, then carry forward uncovered
    ///     master children so the ownership transfer doesn't blank the cell.
    /// </summary>
    private static List<CellOverrideBundle> ConvertCellsToBundles(
        EmitPlan plan, CellChildEncodeContext context, out HashSet<uint> emittedNavmFormIds)
    {
        var bundles = new List<CellOverrideBundle>(plan.CellsByFormId.Count);
        emittedNavmFormIds = [];

        foreach (var (cellFormId, cellPlan) in plan.CellsByFormId)
        {
            if (cellPlan.CellRecordPlan.Disposition == RecordDisposition.Skip)
            {
                continue;
            }

            var cellRecordBytes = EncodeCellAnchor(cellPlan, context.Options);
            if (cellRecordBytes is null)
            {
                continue; // Skip cells we can't anchor (no master + no DMP model).
            }

            var isMasterAnchored = cellPlan.CellRecordPlan.Master is not null;
            var dmpCell = cellPlan.CellRecordPlan.Model as CellRecord;

            // The merge mode and marker-drop decision are settled at plan time by
            // CellSectionPlanner.PlanMergeMode. EsmPlanner now validates the complete CELL
            // input tuple up front; this remains the final tripwire for hand-built plans.
            if (cellPlan.Mode is not { } mode)
            {
                throw new InvalidOperationException(
                    $"CELL 0x{cellFormId:X8} reached the writer without a planned merge mode. " +
                    "CellSectionPlanner.PlanMergeMode must settle it for every cell.");
            }

            var state = new CellEncodeState
            {
                CellFormId = cellFormId,
                Mode = mode,
                IsMasterAnchored = isMasterAnchored,
                IsInterior = cellPlan.Context.IsInterior,
                DropRenderCullingMarkers = cellPlan.DropRenderCullingMarkers,
                RefDecisions = cellPlan.RefDecisions
            };

            var persistent = new List<byte[]>();
            var vwd = new List<byte[]>();
            var temporary = new List<byte[]>();
            var landPrefix = new List<byte[]>();
            var navmPrefix = new List<byte[]>();

            EncodeBucketChildren(cellPlan.PersistentChildren, 8, context, state, persistent, vwd, temporary, landPrefix,
                navmPrefix);
            EncodeBucketChildren(cellPlan.VwdChildren, 10, context, state, persistent, vwd, temporary, landPrefix,
                navmPrefix);
            EncodeBucketChildren(cellPlan.TemporaryChildren, 9, context, state, persistent, vwd, temporary, landPrefix,
                navmPrefix);

            // Cell-emission gates are settled at plan time by CellChildVerdictPlanner's
            // PlanCellGates; the writer only applies them. (The inline re-computation that
            // used to shadow them went with retirement Stage H1.) Full rationale for each
            // gate — navmesh-only ownership transfer, ITM blanking class, fragile interior
            // master seek/scan attach — lives on CellChildVerdictPlanner.
            if (cellPlan.Emits is not { } emits)
            {
                throw new InvalidOperationException(
                    $"CELL 0x{cellFormId:X8} reached the writer without a planned emission gate.");
            }

            if (cellPlan.NavmOnlySuppressed)
            {
                navmPrefix.Clear();
                state.EmittedNavmFormIds.Clear();
                state.GenuineChildCount = 0;
                context.Stats?.IncrementDropReason("cell.navmesh-only-override-suppressed");
            }

            if (!emits)
            {
                context.Stats?.IncrementDropReason(
                    cellPlan.SuppressReason ?? "cell.itm-override-suppressed");
                continue;
            }

            // LAND and NAVM both live in Temporary Children; LAND first, then NAVM, then
            // placed refs (vanilla master layout).
            MasterChildCarryForward.AppendMasterLandFallback(context, state, landPrefix);
            MasterChildCarryForward.Apply(context, state, dmpCell, persistent, vwd, temporary);

            var temporaryAll = new List<byte[]>(landPrefix.Count + navmPrefix.Count + temporary.Count);
            temporaryAll.AddRange(landPrefix);
            temporaryAll.AddRange(navmPrefix);
            temporaryAll.AddRange(temporary);

            emittedNavmFormIds.UnionWith(state.EmittedNavmFormIds);
            bundles.Add(new CellOverrideBundle
            {
                CellFormId = cellFormId,
                Context = cellPlan.Context,
                CellRecordBytes = cellRecordBytes,
                PersistentChildRecords = persistent,
                VwdChildRecords = vwd,
                TemporaryChildRecords = temporaryAll
            });
        }

        return bundles;
    }

    private static void EncodeBucketChildren(
        IReadOnlyList<RecordPlan> children,
        int plannedGroupType,
        CellChildEncodeContext context,
        CellEncodeState state,
        List<byte[]> persistent,
        List<byte[]> vwd,
        List<byte[]> temporary,
        List<byte[]> landPrefix,
        List<byte[]> navmPrefix)
    {
        foreach (var child in children)
        {
            if (child.Type == "LAND")
            {
                var landBytes = PlannedLandEncoder.EncodeRecord(child, context.Options);
                if (landBytes is not null)
                {
                    landPrefix.Add(landBytes);
                    context.Stats?.IncrementEmitted("LAND");
                    if (context.Stats is { } landStats)
                    {
                        if (child.Disposition == RecordDisposition.Override)
                        {
                            landStats.OverridesEmitted++;
                        }
                        else
                        {
                            landStats.NewRecordsEmitted++;
                        }
                    }
                }

                // LAND does not participate in genuine-child emission gates.
                continue;
            }

            if (child.Type == "NAVM")
            {
                if (context.Options.DiagnosticSkipCellNavm)
                {
                    context.Stats?.IncrementSkipped("NAVM");
                    context.Stats?.IncrementDropReason("navm.diagnostic-skip");
                    continue;
                }

                var navmBytes = EncodeNavm(
                    child, state.CellFormId, context.Plan.SourceToEmittedFormId,
                    context.Plan.NavmDoorLinks, context.Options);
                if (navmBytes is null)
                {
                    continue;
                }

                navmPrefix.Add(navmBytes);
                state.EmittedNavmFormIds.Add(child.FormId);
                state.GenuineChildCount++;
                state.GenuineNavmCount++;
                continue;
            }

            if (child.Type is not ("REFR" or "ACHR" or "ACRE" or "PGRE"))
            {
                continue;
            }

            // Overrides re-bucket to master's original child GRUP (legacy parity); new
            // refs keep the planner's persistence-based bucket.
            var routeGroupType = plannedGroupType;
            var bytes = PlannedPlacedRefEncoder.Encode(child, context, state, ref routeGroupType);
            if (bytes is null)
            {
                continue;
            }

            state.GenuineChildCount++;
            if (child.Disposition == RecordDisposition.New)
            {
                state.GenuineNewCount++;
            }

            switch (routeGroupType)
            {
                case 8:
                    persistent.Add(bytes);
                    break;
                case 10:
                    vwd.Add(bytes);
                    break;
                default:
                    temporary.Add(bytes);
                    break;
            }
        }
    }

    /// <summary>
    ///     Produces the CELL anchor bytes, reusing a master slice or encoding a new model.
    /// </summary>
    private static byte[]? EncodeCellAnchor(CellPlan cellPlan, PluginBuildOptions options)
    {
        if (cellPlan.CellRecordPlan.Master is { } master)
        {
            return CellGrupBuilder.ReconstructRecordBytes(master);
        }

        if (cellPlan.CellRecordPlan.Model is not CellRecord cellModel)
        {
            return null;
        }

        var encoded = new CellEncoder().Encode(cellModel);
        if (encoded.Subrecords.Count == 0)
        {
            return null;
        }

        // Exterior persistent-container CELLs use direct World Children placement and the persistent bit. The latter
        // is present on CK-authored/master containers and is required for the runtime
        // to register their group-8 children as worldspace persistents.
        var flags = cellModel.IsPersistentCell ? PersistentFlag : 0u;
        if (options.CompressRecords)
        {
            flags |= CompressedFlag;
        }

        return PluginRecordByteBuilder.BuildNewRecordBytes(
            "CELL", cellPlan.CellFormId, flags, encoded.Subrecords);
    }

    private static byte[]? EncodeNavm(
        RecordPlan child,
        uint cellFormId,
        IReadOnlyDictionary<uint, uint> nvexRewrites,
        NavmDoorLinkPlan doorLinks,
        PluginBuildOptions options)
    {
        if (child.Model is not NavMeshRecord navm)
        {
            return null;
        }

        if (child.Disposition != RecordDisposition.New)
        {
            return null; // Master (KeepMaster) NAVMs are intentionally not emitted: engine RE
            // shows they load from master via the cell's TESForm file-list merge,
            // so copying them is redundant.
        }

        return PlannedNavmEncoder.EncodeRecord(
            navm, cellFormId, child.FormId, nvexRewrites,
            doorLinks.SourceToEmittedDoorRef, doorLinks.ValidDoorRefFormIds, options);
    }
}
