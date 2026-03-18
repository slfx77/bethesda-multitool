using System.Diagnostics;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

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

        // Use pAllForms entries (LAND records lack editor IDs, so they're absent from RuntimeEditorIds)
        var landEntries = context.ScanResult.RuntimeLandFormEntries.Count > 0
            ? context.ScanResult.RuntimeLandFormEntries
            : context.ScanResult.RuntimeEditorIds; // Fallback for compatibility
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
                    DestinationDoorFormId = existing.DestinationDoorFormId ?? runtimeRefr.DestinationDoorFormId
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
            context.RuntimeWorldspaceCellMaps = cellMaps;
            var totalCells = cellMaps.Values.Sum(w => w.Cells.Count);
            Logger.Instance.Debug(
                $"  [Semantic] Worldspace cell maps: {phaseSw.Elapsed} ({cellMaps.Count} worldspaces, {totalCells} cells)");
        }
    }
}
