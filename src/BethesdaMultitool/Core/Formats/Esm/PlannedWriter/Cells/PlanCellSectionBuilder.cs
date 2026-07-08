using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
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
    ///     Full-result variant: also reports which NAVM FormIDs were actually written, so
    ///     NAVI rows and NVEX valid-sets can be built from real emission rather than the
    ///     plan (a planned NAVM whose cell got suppressed must not get a NAVI row — the
    ///     engine null-derefs NavMeshInfoMap over dangling NVMI entries).
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

        var context = new CellChildEncodeContext(
            plan, masterByFormId, validFormIds, options, stats, masterIndex, masterRefFormIds, dmpBaseTypes);

        var bundles = ConvertCellsToBundles(plan, context, out var emittedNavmFormIds);
        if (bundles.Count == 0)
        {
            return new CellSectionBuildResult(null, emittedNavmFormIds, ImmutableHashSet<uint>.Empty);
        }

        SanitizeNavmNvexInBundles(bundles, emittedNavmFormIds, masterByFormId);
        DeletedRefLinkStripper.Apply(bundles, stats);

        var newWorldspaces = BuildNewWorldspaces(plan, options);

        var sectionBytes = CellGrupBuilder.BuildCellSection(
            bundles, masterByFormId, newWorldspacesByDmpFormId: newWorldspaces);
        return new CellSectionBuildResult(
            sectionBytes, emittedNavmFormIds, CollectOverriddenChildFormIds(bundles));
    }

    /// <summary>
    ///     Master-resident cell-child records this plugin emits (overrides, carry-forwards,
    ///     deletion records, LAND copies). The ESM-flagged TES4 must list these in ONAM or
    ///     the FO3/FNV runtime mishandles the overrides at load.
    /// </summary>
    private static HashSet<uint> CollectOverriddenChildFormIds(List<CellOverrideBundle> bundles)
    {
        var result = new HashSet<uint>();
        foreach (var bundle in bundles)
        {
            CollectFromRecords(bundle.PersistentChildRecords, result);
            CollectFromRecords(bundle.VwdChildRecords, result);
            CollectFromRecords(bundle.TemporaryChildRecords, result);
        }

        return result;

        static void CollectFromRecords(IReadOnlyList<byte[]> records, HashSet<uint> result)
        {
            foreach (var record in records)
            {
                if (record.Length < 24)
                {
                    continue;
                }

                var sig = System.Text.Encoding.ASCII.GetString(record, 0, 4);
                if (sig is not ("REFR" or "ACHR" or "ACRE" or "PGRE" or "PMIS" or "LAND" or "NAVM"))
                {
                    continue;
                }

                // xEdit rule (wbImplementation.pas: "ONAMs are for overridden temporary refs
                // only"): persistent-flagged records are excluded. Listing them sends the
                // runtime's temporary-group loader after records that live in persistent
                // groups — "Failed to load temporary data" for the whole cell.
                var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    record.AsSpan(8, 4));
                if ((flags & 0x400u) != 0)
                {
                    continue;
                }

                var formId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    record.AsSpan(12, 4));
                if (formId < 0x01000000u)
                {
                    result.Add(formId);
                }
            }
        }
    }

    /// <summary>
    ///     Post-emission pass that drops NVEX entries pointing at NAVM FormIDs that aren't
    ///     in the emitted set, and patches DATA.EdgeLinkCount to match the kept entries.
    ///     Mirrors the legacy <c>PluginBuilder.SanitizeNavmNvexInBundles</c> hook.
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
            var mode = ResolveMergeMode(isMasterAnchored, dmpCell, context);
            var state = new CellEncodeState
            {
                CellFormId = cellFormId,
                Mode = mode,
                IsMasterAnchored = isMasterAnchored,
                IsInterior = cellPlan.Context.IsInterior,
                DropRenderCullingMarkers = isMasterAnchored
                    && cellPlan.Context.IsInterior
                    && mode == CellMergeMode.LoadedReplacement
                    && !context.Options.ReplaceCellTemporariesOnOverride,
            };

            var persistent = new List<byte[]>();
            var vwd = new List<byte[]>();
            var temporary = new List<byte[]>();
            var navmPrefix = new List<byte[]>();

            EncodeBucketChildren(cellPlan.PersistentChildren, 8, context, state, persistent, vwd, temporary, navmPrefix);
            EncodeBucketChildren(cellPlan.VwdChildren, 10, context, state, persistent, vwd, temporary, navmPrefix);
            EncodeBucketChildren(cellPlan.TemporaryChildren, 9, context, state, persistent, vwd, temporary, navmPrefix);

            // A navmesh must ride along with a genuine content override, never create one:
            // a navmesh-only master-cell override would transfer temp-children ownership
            // (blanking the interior) for nothing but a mesh master already serves.
            if (isMasterAnchored
                && state.GenuineChildCount > 0
                && state.GenuineChildCount == state.GenuineNavmCount)
            {
                navmPrefix.Clear();
                state.EmittedNavmFormIds.Clear();
                state.GenuineChildCount = 0;
                context.Stats?.IncrementDropReason("cell.navmesh-only-override-suppressed");
            }

            // Master-anchored bundle with no surviving genuine children = a byte-identical
            // (ITM) CELL override. It carries zero information but still makes this plugin
            // the cell's winning file, which drops the master's temporary children in-game
            // (Doc Mitchell's-house blanking class, in-game confirmed). Skip it; the master
            // cell stands untouched. Carry-forward must never be the reason a cell emits.
            if (isMasterAnchored && state.GenuineChildCount == 0)
            {
                context.Stats?.IncrementDropReason("cell.itm-override-suppressed");
                continue;
            }

            // LAND and NAVM both live in Temporary Children; LAND first, then NAVM, then
            // placed refs (vanilla master layout).
            var landPrefix = new List<byte[]>();
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
                TemporaryChildRecords = temporaryAll,
            });
        }

        return bundles;
    }

    /// <summary>
    ///     Per-cell merge-mode classification, mirroring legacy: master cells classify via
    ///     <see cref="CellMerger.Classify" /> (any non-persistent master-resident capture ⇒
    ///     LoadedReplacement; persistent-only ⇒ PersistentOnly); brand-new cells emit all
    ///     their children unconditionally (LoadedReplacement semantics).
    /// </summary>
    private static CellMergeMode ResolveMergeMode(
        bool isMasterAnchored, CellRecord? dmpCell, CellChildEncodeContext context)
    {
        if (!isMasterAnchored)
        {
            return CellMergeMode.LoadedReplacement;
        }

        if (dmpCell is null || context.MasterIndex is null)
        {
            return CellMergeMode.Skip;
        }

        return CellMerger.Classify(dmpCell, context.MasterRefFormIds);
    }

    private static void EncodeBucketChildren(
        IReadOnlyList<RecordPlan> children,
        int plannedGroupType,
        CellChildEncodeContext context,
        CellEncodeState state,
        List<byte[]> persistent,
        List<byte[]> vwd,
        List<byte[]> temporary,
        List<byte[]> navmPrefix)
    {
        foreach (var child in children)
        {
            if (child.Type == "NAVM")
            {
                if (context.Options.DiagnosticSkipCellNavm)
                {
                    context.Stats?.IncrementSkipped("NAVM");
                    context.Stats?.IncrementDropReason("navm.diagnostic-skip");
                    continue;
                }

                var navmBytes = EncodeNavm(
                    child, state.CellFormId, context.Plan.SourceToEmittedFormId, context.Options);
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

            if (child.Type is not ("REFR" or "ACHR" or "ACRE"))
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
    ///     Produce the CELL record bytes the bundle hands to legacy GRUP framing. For
    ///     KeepMaster / Override cells the master byte slice is reused verbatim; for
    ///     <see cref="RecordDisposition.New" /> cells the CELL is fresh-encoded through
    ///     <see cref="CellEncoder" /> + <see cref="PluginRecordByteBuilder.BuildNewRecordBytes" />.
    ///     Returns null when neither path is available (e.g. New disposition with no model).
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

        var flags = options.CompressRecords ? CompressedFlag : 0u;
        return PluginRecordByteBuilder.BuildNewRecordBytes(
            "CELL", cellPlan.CellFormId, flags, encoded.Subrecords);
    }

    private static byte[]? EncodeNavm(
        RecordPlan child,
        uint cellFormId,
        IReadOnlyDictionary<uint, uint> nvexRewrites,
        PluginBuildOptions options)
    {
        if (child.Model is not NavMeshRecord navm)
        {
            return null;
        }

        if (child.Disposition != RecordDisposition.New)
        {
            return null; // Master (KeepMaster) NAVMs are intentionally not emitted: engine RE
                         // shows they load from master via the cell's TESForm file-list merge
                         // (memory/navm_engine_load_mechanism.md), so copying them is redundant.
        }

        return PlannedNavmEncoder.EncodeRecord(navm, cellFormId, child.FormId, nvexRewrites, options);
    }
}
