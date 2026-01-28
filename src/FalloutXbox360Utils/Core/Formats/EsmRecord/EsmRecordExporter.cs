using System.Globalization;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Exports ESM records to files.
/// </summary>
public static class EsmRecordExporter
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Export ESM records to files in the specified output directory.
    /// </summary>
    public static async Task ExportRecordsAsync(
        EsmRecordScanResult records,
        Dictionary<uint, string> formIdMap,
        string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        await ExportEditorIdsAsync(records.EditorIds, outputDir);
        await ExportGameSettingsAsync(records.GameSettings, outputDir);
        await ExportScriptSourcesAsync(records.ScriptSources, outputDir);
        await ExportFormIdMapAsync(formIdMap, outputDir);
        await ExportFormIdReferencesAsync(records.FormIdReferences, formIdMap, outputDir);
        await ExportHeightmapsAsync(records.LandRecords, records.Heightmaps, records.CellGrids, outputDir);
        await ExportRefrRecordsAsync(records.RefrRecords, formIdMap, outputDir);
    }

    private static async Task ExportEditorIdsAsync(List<EdidRecord> editorIds, string outputDir)
    {
        if (editorIds.Count == 0)
        {
            return;
        }

        var edidPath = Path.Combine(outputDir, "editor_ids.txt");
        var edidLines = editorIds
            .OrderBy(e => e.Name)
            .Select(e => e.Name);
        await File.WriteAllLinesAsync(edidPath, edidLines);

        Log.Debug($"  [ESM] Exported {editorIds.Count} editor IDs to editor_ids.txt");
    }

    private static async Task ExportGameSettingsAsync(List<GmstRecord> gameSettings, string outputDir)
    {
        if (gameSettings.Count == 0)
        {
            return;
        }

        var gmstPath = Path.Combine(outputDir, "game_settings.txt");
        var gmstLines = gameSettings
            .Select(g => g.Name)
            .Distinct()
            .OrderBy(n => n);
        await File.WriteAllLinesAsync(gmstPath, gmstLines);

        Log.Debug($"  [ESM] Exported {gameSettings.Count} game settings to game_settings.txt");
    }

    private static async Task ExportScriptSourcesAsync(List<SctxRecord> scriptSources, string outputDir)
    {
        if (scriptSources.Count == 0)
        {
            return;
        }

        var sctxDir = Path.Combine(outputDir, "script_sources");
        Directory.CreateDirectory(sctxDir);

        for (var i = 0; i < scriptSources.Count; i++)
        {
            var sctx = scriptSources[i];
            var filename = $"sctx_{i:D4}_0x{sctx.Offset:X8}.txt";
            await File.WriteAllTextAsync(Path.Combine(sctxDir, filename), sctx.Text);
        }

        Log.Debug($"  [ESM] Exported {scriptSources.Count} script sources to script_sources/");
    }

    private static async Task ExportFormIdMapAsync(Dictionary<uint, string> formIdMap, string outputDir)
    {
        if (formIdMap.Count == 0)
        {
            return;
        }

        var formIdPath = Path.Combine(outputDir, "formid_map.csv");
        var formIdLines = new List<string> { "FormID,EditorID" };
        formIdLines.AddRange(formIdMap
            .OrderBy(kv => kv.Key)
            .Select(kv => $"0x{kv.Key:X8},{kv.Value}"));
        await File.WriteAllLinesAsync(formIdPath, formIdLines);

        Log.Debug($"  [ESM] Exported {formIdMap.Count} FormID correlations to formid_map.csv");
    }

    private static async Task ExportFormIdReferencesAsync(
        List<ScroRecord> formIdReferences,
        Dictionary<uint, string> formIdMap,
        string outputDir)
    {
        if (formIdReferences.Count == 0)
        {
            return;
        }

        var scroPath = Path.Combine(outputDir, "formid_references.txt");
        var scroLines = formIdReferences
            .OrderBy(s => s.FormId)
            .Select(s =>
            {
                var name = formIdMap.TryGetValue(s.FormId, out var n) ? $" ({n})" : "";
                return $"0x{s.FormId:X8}{name}";
            });
        await File.WriteAllLinesAsync(scroPath, scroLines);

        Log.Debug($"  [ESM] Exported {formIdReferences.Count} FormID references to formid_references.txt");
    }

    /// <summary>
    ///     Export heightmaps to CSV files from both LAND records and directly-detected VHGT subrecords.
    ///     Each heightmap is a 33×33 grid of float values.
    ///     Attempts to correlate with XCLC cell grids for positioning.
    /// </summary>
    private static async Task ExportHeightmapsAsync(
        List<ExtractedLandRecord> landRecords,
        List<DetectedVhgtHeightmap> detectedHeightmaps,
        List<CellGridSubrecord> cellGrids,
        string outputDir)
    {
        if (landRecords.Count == 0 && detectedHeightmaps.Count == 0)
        {
            return;
        }

        var heightmapDir = Path.Combine(outputDir, "heightmaps");
        Directory.CreateDirectory(heightmapDir);

        // Export summary CSV with grid coordinates if available
        var summaryLines = new List<string>
        {
            "Source,ID,Offset,BigEndian,HeightOffset,MinHeight,MaxHeight,TextureLayers,GridX,GridY"
        };

        var exported = 0;

        // Export from LAND records (with FormID)
        foreach (var land in landRecords)
        {
            if (land.Heightmap == null)
            {
                continue;
            }

            var heights = land.Heightmap.CalculateHeights();
            var filename = $"land_0x{land.Header.FormId:X8}.csv";
            var filepath = Path.Combine(heightmapDir, filename);

            await WriteHeightmapCsvAsync(filepath, heights);

            var (minHeight, maxHeight) = GetHeightRange(heights);

            // Try to find associated cell grid
            var grid = FindNearestCellGrid(land.Header.Offset, cellGrids);
            var gridX = grid?.GridX.ToString() ?? "";
            var gridY = grid?.GridY.ToString() ?? "";

            summaryLines.Add(
                $"LAND,0x{land.Header.FormId:X8},{land.Header.Offset},{land.Header.IsBigEndian}," +
                $"{land.Heightmap.HeightOffset:F2},{minHeight:F2},{maxHeight:F2},{land.TextureLayers.Count},{gridX},{gridY}");

            exported++;
        }

        // Export directly-detected VHGT subrecords (standalone)
        var vhgtIndex = 0;
        foreach (var vhgt in detectedHeightmaps)
        {
            var heights = vhgt.CalculateHeights();
            var filename = $"vhgt_{vhgtIndex:D4}_0x{vhgt.Offset:X8}.csv";
            var filepath = Path.Combine(heightmapDir, filename);

            await WriteHeightmapCsvAsync(filepath, heights);

            var (minHeight, maxHeight) = GetHeightRange(heights);

            // Try to find associated cell grid (XCLC is usually before VHGT in a LAND record)
            var grid = FindNearestCellGrid(vhgt.Offset, cellGrids);
            var gridX = grid?.GridX.ToString() ?? "";
            var gridY = grid?.GridY.ToString() ?? "";

            summaryLines.Add(
                $"VHGT,{vhgtIndex:D4},{vhgt.Offset},{vhgt.IsBigEndian}," +
                $"{vhgt.HeightOffset:F2},{minHeight:F2},{maxHeight:F2},0,{gridX},{gridY}");

            exported++;
            vhgtIndex++;
        }

        // Write summary
        await File.WriteAllLinesAsync(Path.Combine(heightmapDir, "_summary.csv"), summaryLines);

        // Also export cell grids for reference
        if (cellGrids.Count > 0)
        {
            var gridLines = new List<string> { "GridX,GridY,Offset,BigEndian,LandFlags" };
            gridLines.AddRange(cellGrids.OrderBy(g => g.GridX).ThenBy(g => g.GridY)
                .Select(g => $"{g.GridX},{g.GridY},{g.Offset},{g.IsBigEndian},{g.LandFlags}"));
            await File.WriteAllLinesAsync(Path.Combine(heightmapDir, "_cell_grids.csv"), gridLines);
        }

        Log.Debug($"  [ESM] Exported {exported} heightmaps to heightmaps/");
    }

    private static async Task WriteHeightmapCsvAsync(string filepath, float[,] heights)
    {
        var lines = new List<string>();
        for (var y = 0; y < 33; y++)
        {
            var row = new List<string>();
            for (var x = 0; x < 33; x++)
            {
                row.Add(heights[y, x].ToString("F2", CultureInfo.InvariantCulture));
            }

            lines.Add(string.Join(",", row));
        }

        await File.WriteAllLinesAsync(filepath, lines);
    }

    private static (float min, float max) GetHeightRange(float[,] heights)
    {
        var minHeight = float.MaxValue;
        var maxHeight = float.MinValue;
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                minHeight = Math.Min(minHeight, heights[y, x]);
                maxHeight = Math.Max(maxHeight, heights[y, x]);
            }
        }

        return (minHeight, maxHeight);
    }

    /// <summary>
    ///     Find the nearest XCLC cell grid to a given offset.
    ///     In a LAND record, XCLC typically precedes VHGT within ~200 bytes.
    ///     Returns null if no suitable match found.
    /// </summary>
    private static CellGridSubrecord? FindNearestCellGrid(long targetOffset, List<CellGridSubrecord> cellGrids)
    {
        if (cellGrids.Count == 0)
        {
            return null;
        }

        // XCLC usually comes before VHGT in a LAND record (within ~200 bytes)
        // But in memory dumps, they might be further apart due to fragmentation
        // Use a generous window of 8KB before and 1KB after
        const long searchBefore = 8192;
        const long searchAfter = 1024;

        CellGridSubrecord? nearest = null;
        var nearestDistance = long.MaxValue;

        foreach (var grid in cellGrids)
        {
            var distance = targetOffset - grid.Offset;

            // XCLC should be before VHGT (positive distance) and within range
            if (distance >= -searchAfter && distance <= searchBefore)
            {
                var absDistance = Math.Abs(distance);
                if (absDistance < nearestDistance)
                {
                    nearestDistance = absDistance;
                    nearest = grid;
                }
            }
        }

        return nearest;
    }

    /// <summary>
    ///     Export extracted REFR records (placed objects) to CSV.
    ///     Includes position, scale, and base object information.
    /// </summary>
    private static async Task ExportRefrRecordsAsync(
        List<ExtractedRefrRecord> refrRecords,
        Dictionary<uint, string> formIdMap,
        string outputDir)
    {
        if (refrRecords.Count == 0)
        {
            return;
        }

        var refrPath = Path.Combine(outputDir, "placed_objects.csv");
        var lines = new List<string>
        {
            "FormID,BaseFormID,BaseEditorID,PosX,PosY,PosZ,RotX,RotY,RotZ,Scale,OwnerFormID,Offset,BigEndian"
        };

        foreach (var refr in refrRecords.OrderBy(r => r.Header.FormId))
        {
            var baseEditorId = refr.BaseEditorId ??
                               (formIdMap.TryGetValue(refr.BaseFormId, out var name) ? name : "");

            var posX = refr.Position?.X.ToString("F2") ?? "";
            var posY = refr.Position?.Y.ToString("F2") ?? "";
            var posZ = refr.Position?.Z.ToString("F2") ?? "";
            var rotX = refr.Position?.RotX.ToString("F4") ?? "";
            var rotY = refr.Position?.RotY.ToString("F4") ?? "";
            var rotZ = refr.Position?.RotZ.ToString("F4") ?? "";
            var owner = refr.OwnerFormId.HasValue ? $"0x{refr.OwnerFormId:X8}" : "";

            lines.Add(
                $"0x{refr.Header.FormId:X8},0x{refr.BaseFormId:X8},{baseEditorId}," +
                $"{posX},{posY},{posZ},{rotX},{rotY},{rotZ},{refr.Scale:F2},{owner}," +
                $"{refr.Header.Offset},{refr.Header.IsBigEndian}");
        }

        await File.WriteAllLinesAsync(refrPath, lines);

        // Also export a simplified positions file for visualization
        var positionsPath = Path.Combine(outputDir, "object_positions.csv");
        var posLines = new List<string> { "X,Y,Z,EditorID" };
        foreach (var refr in refrRecords.Where(r => r.Position != null))
        {
            var baseEditorId = refr.BaseEditorId ??
                               (formIdMap.TryGetValue(refr.BaseFormId, out var name)
                                   ? name
                                   : $"0x{refr.BaseFormId:X8}");
            posLines.Add($"{refr.Position!.X:F2},{refr.Position.Y:F2},{refr.Position.Z:F2},{baseEditorId}");
        }

        await File.WriteAllLinesAsync(positionsPath, posLines);

        Log.Debug($"  [ESM] Exported {refrRecords.Count} placed objects to placed_objects.csv");
    }
}
