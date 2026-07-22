using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Re-emits master cell children the DMP capture didn't cover. Under the in-game-proven
///     interior cell-ownership rule, overriding a CELL makes this plugin own the cell's
///     temporary children — every master temporary we don't re-emit is dropped by the engine.
///     Ports the legacy carry-forward (PluginBuilder's PersistentOnly PreserveAllMissing /
///     LoadedReplacement PreserveLoadedReplacementMissing branches), reusing the same
///     <see cref="CellStructuralReferencePreserver" /> filters and verbatim byte
///     reconstruction (XEMI stripped, compression cleared).
/// </summary>
internal static class MasterChildCarryForward
{
    /// <summary>
    ///     Append carried-forward master children (and, for exterior replacements, deletion
    ///     tombstones) to the bundle buckets. Call only after genuine children emitted —
    ///     carry-forward alone must never be the reason a cell override exists.
    /// </summary>
    public static void Apply(
        CellChildEncodeContext context,
        CellEncodeState state,
        CellRecord? dmpCell,
        List<byte[]> persistentRecords,
        List<byte[]> vwdRecords,
        List<byte[]> temporaryRecords)
    {
        if (context.MasterIndex is null
            || !state.IsMasterAnchored
            || !context.MasterIndex.RefsByCell.TryGetValue(state.CellFormId, out var masterRefIds))
        {
            return;
        }

        // Two master-ref views: carry-forward excludes persistent (GroupType 8) children —
        // they load globally regardless of who owns the cell override (proven in-game: a
        // header-only override left doors/NPCs intact while statics vanished), so carrying
        // them is pure ITM bloat. Deletion tombstones need the FULL set: persistent master
        // markers (Gomorrah's room-bounds REFRs live in the persistent GRUP) survive
        // ownership-by-omission and can only be removed with an explicit tombstone.
        var carryRefs = new List<ParsedMainRecord>(masterRefIds.Count);
        var allRefs = new List<ParsedMainRecord>(masterRefIds.Count);
        foreach (var refId in masterRefIds)
        {
            if (!context.MasterByFormId.TryGetValue(refId, out var masterRef))
            {
                continue;
            }

            var hasLocation = context.MasterIndex.ChildLocations.TryGetValue(refId, out var location);

            // Master TEMPORARY actors are never re-emitted (carried) NOR tombstoned by an
            // ESM plugin: re-emission leaves the actor's form skipped by the attach-time
            // temp stream and thus never initialized (raw XLKR pointer slot -> the
            // Gomorrah01 patrol AV; see PlannedPlacedRefEncoder.EncodeOverride), and a
            // tombstone would remove an actor the proto capture simply didn't include.
            // Leaving them untouched lets the master's copy stream vanilla-style.
            if (masterRef.Header.Signature is "ACHR" or "ACRE"
                && hasLocation && location!.GroupType == 9)
            {
                continue;
            }

            allRefs.Add(masterRef);
            if (!(hasLocation && location!.GroupType == 8))
            {
                carryRefs.Add(masterRef);
            }
        }

        if (allRefs.Count == 0)
        {
            return;
        }

        var stats = context.Stats ?? new ConversionPipelineStats();

        // Coverage = refs this CELL emitted ∪ refs the plan emitted anywhere else
        // (cross-cell moves). Without the global view, a ref the capture moved to a
        // neighbor cell would be tombstoned here (disabling the moved instance) or
        // carried here (duplicate FormID).
        ISet<uint> covered = state.CoveredMasterRefFormIds;
        if (context.GloballyEmittedMasterRefs.Count > 0)
        {
            var union = new HashSet<uint>(state.CoveredMasterRefFormIds);
            union.UnionWith(context.GloballyEmittedMasterRefs);
            covered = union;
        }

        if (state.Mode == CellMergeMode.LoadedReplacement)
        {
            var hasAuthoritativeDmpStructuralRefs = dmpCell?.PlacedObjects
                .Any(placed => placed.StructuralData is { HasAny: true }) ?? false;
            var dmpCapturedBaseTypes = dmpCell is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : PlacedReferenceAnalysis.ComputeDmpCapturedBaseTypes(dmpCell.PlacedObjects, context.MasterByFormId);

            CellStructuralReferencePreserver.PreserveLoadedReplacementMissing(
                carryRefs, covered, context.MasterIndex.ChildLocations,
                context.MasterByFormId, persistentRecords, vwdRecords, temporaryRecords, stats,
                hasAuthoritativeDmpStructuralRefs,
                dmpCapturedBaseTypes,
                state.DropRenderCullingMarkers);

            // Removal records with the legacy force-remove filter. Render-culling markers
            // get TRUE deleted-flag stubs (the engine's room-portal culling graph honors
            // initially-disabled markers — in-game verified — and nothing references
            // culling markers, so hard deletion is safe); everything else is removed via
            // undelete-and-disable so every FormID keeps resolving.
            var deleted = DeletedRefSynthesizer.Synthesize(
                allRefs,
                covered,
                masterRef =>
                    !(state.DropRenderCullingMarkers
                      && CellStructuralReferencePreserver.IsRenderCullingMarker(masterRef, context.MasterByFormId))
                    && (!hasAuthoritativeDmpStructuralRefs
                        || !CellStructuralReferencePreserver.IsStructuralCellRef(masterRef, context.MasterByFormId))
                    && CellStructuralReferencePreserver.ShouldPreserveInLoadedReplacement(
                        masterRef, context.MasterByFormId, dmpCapturedBaseTypes),
                useHardDeletion: masterRef =>
                    state.DropRenderCullingMarkers
                    && CellStructuralReferencePreserver.IsRenderCullingMarker(masterRef, context.MasterByFormId));
            persistentRecords.AddRange(deleted.Persistent);
            temporaryRecords.AddRange(deleted.Temporary);
        }
        else
        {
            // PersistentOnly (and Skip cells that still emitted genuine content): the DMP
            // didn't author this cell's temporaries — carry every uncovered master child.
            // Unlike legacy, pass the REAL covered set (legacy passed an empty set, which
            // double-emitted refs also covered by a map-marker override).
            CellStructuralReferencePreserver.PreserveAllMissing(
                carryRefs, covered, context.MasterIndex.ChildLocations,
                persistentRecords, vwdRecords, temporaryRecords, stats);
        }
    }

    /// <summary>
    ///     Exterior overridden cells must keep their terrain: copy master LAND verbatim when
    ///     the DMP carries no usable terrain for the cell (legacy parity — full-GRUP
    ///     replacement would otherwise drop LAND from the cell).
    /// </summary>
    public static void AppendMasterLandFallback(
        CellChildEncodeContext context,
        CellEncodeState state,
        List<byte[]> temporaryPrefixRecords)
    {
        // A planned LAND (captured-terrain override or new record) already occupies the
        // prefix — copying master's LAND beside it would put two LANDs in one cell.
        if (temporaryPrefixRecords.Count > 0)
        {
            return;
        }

        if (context.MasterIndex is null || !state.IsMasterAnchored || state.IsInterior)
        {
            return;
        }

        if (!context.MasterIndex.LandsByCell.TryGetValue(state.CellFormId, out var landFormIds)
            || landFormIds.Count == 0
            || !context.MasterByFormId.TryGetValue(landFormIds[0], out var masterLand))
        {
            return;
        }

        temporaryPrefixRecords.Add(CellGrupBuilder.ReconstructRecordBytes(masterLand));
        context.Stats?.IncrementEmitted("LAND");
    }
}
