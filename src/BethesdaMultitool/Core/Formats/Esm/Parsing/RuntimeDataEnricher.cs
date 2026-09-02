using System.Diagnostics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Records;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Enriches ESM scan results with runtime memory data (LAND, REFR, worldspace cell maps).
///     Extracted from RecordParser.ParseAll to isolate the DMP-specific enrichment phases.
/// </summary>
internal static class RuntimeDataEnricher
{
    /// <summary>
    ///     Enrich LAND records with runtime cell coordinates for heightmap stitching.
    /// </summary>
    public static void EnrichLandRecords(RecordParserContext context)
    {
        if (context.RuntimeReader == null)
        {
            return;
        }

        // LAND records have no editor ID, so they are absent from RuntimeEditorIds by construction —
        // that is precisely why RuntimeLandFormEntries exists as a separate pAllForms-derived list.
        //
        // ⚠ This used to fall back to the WHOLE RuntimeEditorIds table when LAND FormType detection
        // was low-confidence, which broke the reader's stated contract ("caller is responsible for
        // filtering to LAND entries"). RuntimeWorldReader had no guard of its own, so every NPC,
        // weapon and static in the table was read as a TESObjectLAND and any that produced a
        // plausible cell coordinate was ADDED as a new ExtractedLandRecord. Measured on xex44
        // (2026-08-30): 63,239 entries in, **15,745 fabricated LAND records out**, none of which can
        // be genuine because an entry in that table has an editor ID and a LAND never does.
        //
        // Falling back to nothing is the honest answer: no runtime terrain beats invented terrain
        // attached to real cells. Detection stays empirical — the corpus spans development builds
        // whose record enumeration was still changing, so the final build's PDB cannot stand in for
        // it — which means a dump with too few carved LAND records to calibrate against simply
        // yields no runtime terrain.
        var landEntries = context.ScanResult.RuntimeLandFormEntries.Count > 0
            ? context.ScanResult.RuntimeLandFormEntries
            : ResolveLandFormTypeByMeshYield(context);
        if (landEntries.Count == 0)
        {
            Logger.Instance.Debug(
                "  [Semantic] No runtime LAND entries; skipping terrain enrichment rather than " +
                "reinterpreting unrelated records as LAND.");
            return;
        }

        var runtimeLandData = context.RuntimeReader.ReadAllRuntimeLandData(landEntries);
        if (runtimeLandData.Count > 0)
        {
            var existingCount = context.ScanResult.LandRecords.Count;
            EsmLandEnricher.EnrichLandRecordsWithRuntimeData(context.ScanResult, runtimeLandData);
            var addedCount = context.ScanResult.LandRecords.Count - existingCount;
            Logger.Instance.Debug(
                $"  [Semantic] Enriched LAND records: {runtimeLandData.Count} with terrain data " +
                $"({existingCount} existing + {addedCount} runtime-only = {context.ScanResult.LandRecords.Count} total)");
        }
    }

    /// <summary>
    ///     Identify this build's LAND FormType from the dump itself when the FormID-correlation
    ///     heuristic could not, by asking which candidate type actually yields terrain meshes.
    ///     <para>
    ///         The record enumeration changed during development, so the byte differs between dumps
    ///         and the final build's PDB cannot arbitrate — measured 2026-08-30, the Release_Beta
    ///         dumps carry LAND at <c>0x42</c> while the shipped PDB maps <c>TESObjectLAND</c> to
    ///         <c>0x44</c>. What does hold everywhere is that a genuine LAND resolves a terrain
    ///         mesh, and nothing else does.
    ///     </para>
    ///     <para>
    ///         ⚠ A mesh is the gate, not merely "parsed with plausible coordinates". Reading an
    ///         unrelated record as a <c>TESObjectLAND</c> yields believable cell coordinates
    ///         surprisingly often — <c>Fallout_Debug.xex2</c> has a candidate type with 130 such
    ///         false positives and zero meshes — and accepting those is exactly how the old
    ///         whole-table fallback fabricated 15,745 terrain records.
    ///     </para>
    /// </summary>
    private static List<RuntimeEditorIdEntry> ResolveLandFormTypeByMeshYield(RecordParserContext context)
    {
        var candidates = context.ScanResult.RuntimeLandCandidateEntries;
        if (candidates.Count == 0 || context.RuntimeReader == null)
        {
            return [];
        }

        List<RuntimeEditorIdEntry> best = [];
        var bestMeshCount = 0;
        byte bestFormType = 0;
        var tiedWith = new List<byte>();

        // Deterministic tie-break: on equal mesh yield the LOWER FormType byte wins, and the tie is
        // logged. Without an explicit rule the winner fell out of GroupBy enumeration order — i.e.
        // hash-table memory layout — and the log then presented it as evidence-decided. Measured
        // corpus separation is 100% vs 0%, so a real tie means something unusual is going on and
        // deserves eyes.
        foreach (var group in candidates.GroupBy(entry => entry.FormType).OrderBy(group => group.Key))
        {
            var entries = group.ToList();
            var data = context.RuntimeReader.ReadAllRuntimeLandData(entries, false);
            var meshCount = data.Values.Count(land => land.TerrainMesh != null);
            if (meshCount > bestMeshCount)
            {
                bestMeshCount = meshCount;
                bestFormType = group.Key;
                best = entries;
                tiedWith.Clear();
            }
            else if (meshCount > 0 && meshCount == bestMeshCount)
            {
                tiedWith.Add(group.Key);
            }
        }

        if (bestMeshCount == 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] No LAND FormType among {candidates.Count} candidate entry(s) yielded a " +
                "terrain mesh; this build's dump carries no runtime terrain.");
            return [];
        }

        if (tiedWith.Count > 0)
        {
            Logger.Instance.Warn(
                $"[Semantic Parse] LAND FormType mesh yield TIED at {bestMeshCount}: kept 0x{bestFormType:X2}, " +
                $"rejected {string.Join(", ", tiedWith.Select(formType => $"0x{formType:X2}"))} — " +
                "the corpus norm is one type at 100% and the rest at 0%, so verify this dump manually.");
        }

        Logger.Instance.Info(
            $"[Semantic Parse] Runtime LAND FormType resolved to 0x{bestFormType:X2} by mesh yield " +
            $"({bestMeshCount} of {best.Count} entries produced a terrain mesh).");

        // Persist the resolution: everything downstream that asks "which byte is LAND on this dump"
        // reads RuntimeLandFormEntries (gap scanner, formtype census, shift probes), and before this
        // a mesh-yield-resolved dump left it empty forever — consumers saw zero LAND while the
        // enricher quietly used the list it never shared.
        context.ScanResult.RuntimeLandFormEntries.AddRange(best);
        return best;
    }

    /// <summary>
    ///     Enrich placed references with runtime REFR/ACHR/ACRE data from pAllForms.
    /// </summary>
    public static void EnrichPlacedReferences(RecordParserContext context, Stopwatch phaseSw)
    {
        if (context.RuntimeReader == null || context.ScanResult.RuntimeRefrFormEntries.Count == 0)
        {
            return;
        }

        phaseSw.Restart();
        var runtimeRefrs = context.RuntimeReader.ReadAllRuntimeRefrs(
            context.ScanResult.RuntimeRefrFormEntries);

        if (runtimeRefrs.Count == 0)
        {
            return;
        }

        // Build index of existing ESM-scanned REFRs by FormID for merging
        var existingByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < context.ScanResult.RefrRecords.Count; i++)
        {
            existingByFormId.TryAdd(context.ScanResult.RefrRecords[i].Header.FormId, i);
        }

        var mergedCount = 0;
        var addedCount = 0;
        foreach (var (formId, runtimeRefr) in runtimeRefrs)
        {
            if (existingByFormId.TryGetValue(formId, out var idx))
            {
                // Merge: keep ESM-authored values authoritative and let runtime fill gaps.
                var existing = context.ScanResult.RefrRecords[idx];
                context.ScanResult.RefrRecords[idx] = existing with
                {
                    BaseFormId = existing.BaseFormId != 0 ? existing.BaseFormId : runtimeRefr.BaseFormId,
                    Position = existing.Position ?? runtimeRefr.Position,
                    Scale = Math.Abs(existing.Scale - 1.0f) > 0.001f ? existing.Scale : runtimeRefr.Scale,
                    Radius = existing.Radius ?? runtimeRefr.Radius,
                    RadioData = existing.RadioData ?? runtimeRefr.RadioData,
                    ParentCellFormId = existing.ParentCellFormId ?? runtimeRefr.ParentCellFormId,
                    ParentCellIsInterior = existing.ParentCellIsInterior ?? runtimeRefr.ParentCellIsInterior,
                    PersistentCellFormId = existing.PersistentCellFormId ?? runtimeRefr.PersistentCellFormId,
                    StartingPosition = existing.StartingPosition ?? runtimeRefr.StartingPosition,
                    StartingWorldOrCellFormId =
                    existing.StartingWorldOrCellFormId ?? runtimeRefr.StartingWorldOrCellFormId,
                    PackageStartLocation = existing.PackageStartLocation ?? runtimeRefr.PackageStartLocation,
                    MerchantContainerFormId = existing.MerchantContainerFormId ?? runtimeRefr.MerchantContainerFormId,
                    LeveledCreatureOriginalBaseFormId = existing.LeveledCreatureOriginalBaseFormId ??
                                                        runtimeRefr.LeveledCreatureOriginalBaseFormId,
                    LeveledCreatureTemplateFormId = existing.LeveledCreatureTemplateFormId ??
                                                    runtimeRefr.LeveledCreatureTemplateFormId,
                    IsMapMarker = existing.IsMapMarker || runtimeRefr.IsMapMarker,
                    MarkerType = existing.MarkerType ?? runtimeRefr.MarkerType,
                    MarkerName = existing.MarkerName ?? runtimeRefr.MarkerName,
                    EncounterZoneFormId = existing.EncounterZoneFormId ?? runtimeRefr.EncounterZoneFormId,
                    MaterialSwapFormId = existing.MaterialSwapFormId ?? runtimeRefr.MaterialSwapFormId,
                    EmittanceFormId = existing.EmittanceFormId ?? runtimeRefr.EmittanceFormId,
                    LockLevel = existing.LockLevel ?? runtimeRefr.LockLevel,
                    LockKeyFormId = existing.LockKeyFormId ?? runtimeRefr.LockKeyFormId,
                    LockFlags = existing.LockFlags ?? runtimeRefr.LockFlags,
                    LockNumTries = existing.LockNumTries ?? runtimeRefr.LockNumTries,
                    LockTimesUnlocked = existing.LockTimesUnlocked ?? runtimeRefr.LockTimesUnlocked,
                    EnableParentFormId = existing.EnableParentFormId ?? runtimeRefr.EnableParentFormId,
                    EnableParentFlags = existing.EnableParentFlags ?? runtimeRefr.EnableParentFlags,
                    LinkedRefKeywordFormId = existing.LinkedRefKeywordFormId ?? runtimeRefr.LinkedRefKeywordFormId,
                    LinkedRefFormId = existing.LinkedRefFormId ?? runtimeRefr.LinkedRefFormId,
                    LinkedRefChildrenFormIds = existing.LinkedRefChildrenFormIds.Count > 0
                        ? existing.LinkedRefChildrenFormIds
                        : runtimeRefr.LinkedRefChildrenFormIds,
                    OwnerFormId = existing.OwnerFormId ?? runtimeRefr.OwnerFormId,
                    DestinationDoorFormId = existing.DestinationDoorFormId ?? runtimeRefr.DestinationDoorFormId,
                    TeleportPosRot = existing.TeleportPosRot ?? runtimeRefr.TeleportPosRot,
                    TeleportFlags = existing.TeleportFlags ?? runtimeRefr.TeleportFlags,
                    StructuralData = existing.StructuralData ?? runtimeRefr.StructuralData
                };
                mergedCount++;
            }
            else
            {
                context.ScanResult.RefrRecords.Add(runtimeRefr);
                addedCount++;
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] Runtime REFRs: {phaseSw.Elapsed} ({runtimeRefrs.Count} read, " +
            $"{mergedCount} merged, {addedCount} new, " +
            $"{context.ScanResult.RefrRecords.Count} total)");
    }

    /// <summary>
    ///     Enrich worldspace cell maps by walking TESWorldSpace pCellMap hash tables.
    /// </summary>
    public static void EnrichWorldspaceCellMaps(RecordParserContext context, Stopwatch phaseSw)
    {
        if (context.RuntimeReader == null)
        {
            return;
        }

        phaseSw.Restart();
        var wrldEntries = context.ScanResult.RuntimeEditorIds
            .Where(e => e.FormType == 0x41)
            .ToList();

        if (wrldEntries.Count == 0)
        {
            return;
        }

        var cellMaps = context.RuntimeReader.ReadAllWorldspaceCellMaps(wrldEntries);
        if (cellMaps.Count > 0)
        {
            // Trust the parent worldspace's pCellMap as authoritative for cell ownership.
            // RuntimeCellMapWalker fills each entry's WorldspaceFormId from the cell's own
            // pWorldSpace pointer, which can be unreadable or point at a stale worldspace
            // (e.g. Lucky38TSW / GomorrahTSW resolving to GreenhouseWorld01 even though
            // TheStripWorld's pCellMap holds them). Override here, after the layout probe
            // has already scored layouts using the raw pWorldSpace reads.
            foreach (var (wsFormId, wsData) in cellMaps)
            {
                for (var i = 0; i < wsData.Cells.Count; i++)
                {
                    if (wsData.Cells[i].WorldspaceFormId != wsFormId)
                    {
                        wsData.Cells[i] = wsData.Cells[i] with
                        {
                            RawWorldspaceFormId = wsData.Cells[i].RawWorldspaceFormId ??
                                                  wsData.Cells[i].WorldspaceFormId,
                            WorldspaceFormId = wsFormId
                        };
                    }
                }
            }

            context.RuntimeWorldspaceCellMaps = cellMaps;
            var totalCells = cellMaps.Values.Sum(w => w.Cells.Count);
            Logger.Instance.Debug(
                $"  [Semantic] Worldspace cell maps: {phaseSw.Elapsed} ({cellMaps.Count} worldspaces, {totalCells} cells)");
        }
    }
}
