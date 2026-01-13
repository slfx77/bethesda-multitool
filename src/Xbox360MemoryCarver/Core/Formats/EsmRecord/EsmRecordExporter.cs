namespace Xbox360MemoryCarver.Core.Formats.EsmRecord;

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
    }

    private static async Task ExportEditorIdsAsync(List<EdidRecord> editorIds, string outputDir)
    {
        if (editorIds.Count == 0) return;

        var edidPath = Path.Combine(outputDir, "editor_ids.txt");
        var edidLines = editorIds
            .OrderBy(e => e.Name)
            .Select(e => e.Name);
        await File.WriteAllLinesAsync(edidPath, edidLines);

        Log.Debug($"  [ESM] Exported {editorIds.Count} editor IDs to editor_ids.txt");
    }

    private static async Task ExportGameSettingsAsync(List<GmstRecord> gameSettings, string outputDir)
    {
        if (gameSettings.Count == 0) return;

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
        if (scriptSources.Count == 0) return;

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
        if (formIdMap.Count == 0) return;

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
        if (formIdReferences.Count == 0) return;

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
}
