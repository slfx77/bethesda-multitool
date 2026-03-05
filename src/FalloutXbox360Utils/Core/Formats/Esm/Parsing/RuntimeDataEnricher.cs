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
                // Merge: prefer runtime non-null values over ESM
                var existing = context.ScanResult.RefrRecords[idx];
                context.ScanResult.RefrRecords[idx] = existing with
                {
                    BaseFormId = runtimeRefr.BaseFormId != 0 ? runtimeRefr.BaseFormId : existing.BaseFormId,
                    Position = runtimeRefr.Position ?? existing.Position,
                    Scale = Math.Abs(runtimeRefr.Scale - 1.0f) > 0.001f ? runtimeRefr.Scale : existing.Scale,
                    ParentCellFormId = runtimeRefr.ParentCellFormId ?? existing.ParentCellFormId,
                    ParentCellIsInterior = runtimeRefr.ParentCellIsInterior ?? existing.ParentCellIsInterior,
                    IsMapMarker = runtimeRefr.IsMapMarker || existing.IsMapMarker,
                    MarkerType = runtimeRefr.MarkerType ?? existing.MarkerType,
                    MarkerName = runtimeRefr.MarkerName ?? existing.MarkerName,
                    EnableParentFormId = runtimeRefr.EnableParentFormId ?? existing.EnableParentFormId,
                    EnableParentFlags = runtimeRefr.EnableParentFlags ?? existing.EnableParentFlags,
                    LinkedRefFormId = runtimeRefr.LinkedRefFormId ?? existing.LinkedRefFormId,
                    OwnerFormId = runtimeRefr.OwnerFormId ?? existing.OwnerFormId,
                    DestinationDoorFormId = runtimeRefr.DestinationDoorFormId ?? existing.DestinationDoorFormId
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
