using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

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
        string outputDir,
        List<ReconstructedCell>? cells = null,
        List<ReconstructedWorldspace>? worldspaces = null)
    {
        Directory.CreateDirectory(outputDir);

        await ExportGameSettingsAsync(records.GameSettings, outputDir);
        await ExportScriptSourcesAsync(records.ScriptSources, outputDir);
        await ExportCellInfoAsync(cells, worldspaces, formIdMap, outputDir);
        await ExportRuntimeEditorIdsAsync(records.RuntimeEditorIds, outputDir);
        await ExportDialogueAsync(records.RuntimeEditorIds, outputDir);
        await ExportAssetStringsAsync(records.AssetStrings, outputDir);
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

    public static async Task ExportScriptSourcesAsync(List<SctxRecord> scriptSources, string outputDir)
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

    /// <summary>
    ///     Export reconstructed scripts as individual text files.
    ///     Each file contains header info, source text (SCTX), decompiled bytecode, variables, and references.
    /// </summary>
    public static async Task ExportReconstructedScriptsAsync(
        List<ReconstructedScript> scripts,
        Dictionary<uint, string>? formIdMap,
        string outputDir)
    {
        if (scripts.Count == 0)
        {
            return;
        }

        var scriptsDir = Path.Combine(outputDir, "scripts");
        Directory.CreateDirectory(scriptsDir);

        foreach (var script in scripts)
        {
            var name = script.EditorId ?? $"0x{script.FormId:X8}";
            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var filename = $"{safeName}.txt";
            var content = FormatScriptExport(script, name, formIdMap);
            await File.WriteAllTextAsync(Path.Combine(scriptsDir, filename), content);
        }

        Log.Debug($"  [ESM] Exported {scripts.Count} reconstructed scripts to scripts/");
    }

    private static string FormatScriptExport(
        ReconstructedScript script, string name, Dictionary<uint, string>? formIdMap)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"; Script: {name}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"; FormID: 0x{script.FormId:X8}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"; Type: {script.ScriptType}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"; Variables: {script.VariableCount}, Refs: {script.RefObjectCount}, Compiled: {script.CompiledSize:N0} bytes");
        sb.AppendLine();

        AppendScriptSource(sb, script);
        AppendScriptDecompiled(sb, script);
        AppendScriptVariables(sb, script);
        AppendScriptReferences(sb, script, formIdMap);

        return sb.ToString();
    }

    private static void AppendScriptSource(StringBuilder sb, ReconstructedScript script)
    {
        if (!script.HasSource) { return; }
        sb.AppendLine("; === Source Text (SCTX) ===");
        sb.AppendLine(script.SourceText);
        sb.AppendLine();
    }

    private static void AppendScriptDecompiled(StringBuilder sb, ReconstructedScript script)
    {
        if (string.IsNullOrEmpty(script.DecompiledText)) { return; }
        sb.AppendLine("; === Decompiled Bytecode (SCDA) ===");
        sb.AppendLine(script.DecompiledText);
        sb.AppendLine();
    }

    private static void AppendScriptVariables(StringBuilder sb, ReconstructedScript script)
    {
        if (script.Variables.Count == 0) { return; }
        sb.AppendLine("; === Variables ===");
        foreach (var v in script.Variables)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"; [{v.Index}] {v.TypeName} {v.Name ?? "(unnamed)"}");
        }

        sb.AppendLine();
    }

    private static void AppendScriptReferences(
        StringBuilder sb, ReconstructedScript script, Dictionary<uint, string>? formIdMap)
    {
        if (script.ReferencedObjects.Count == 0) { return; }
        sb.AppendLine("; === Referenced Objects ===");
        foreach (var refId in script.ReferencedObjects)
        {
            var editorId = formIdMap?.GetValueOrDefault(refId);
            var display = editorId != null ? $"{editorId} [0x{refId:X8}]" : $"0x{refId:X8}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"; {display}");
        }
    }

    /// <summary>
    ///     Export cell information as a cell-centric CSV.
    ///     Each row represents a CELL record with its editor ID, grid coordinates,
    ///     worldspace association, heightmap status, and placed object count.
    /// </summary>
    private static async Task ExportCellInfoAsync(
        List<ReconstructedCell>? cells,
        List<ReconstructedWorldspace>? worldspaces,
        Dictionary<uint, string> formIdMap,
        string outputDir)
    {
        if (cells == null || cells.Count == 0)
        {
            return;
        }

        // Build worldspace EditorID lookup
        var worldspaceEditorIds = new Dictionary<uint, string>();
        if (worldspaces != null)
        {
            foreach (var ws in worldspaces)
            {
                if (ws.EditorId != null)
                {
                    worldspaceEditorIds.TryAdd(ws.FormId, ws.EditorId);
                }
            }
        }

        var path = Path.Combine(outputDir, "cell_info.csv");
        var lines = new List<string>
        {
            "CellFormID,CellEditorID,CellName,GridX,GridY,IsInterior,HasWater,WorldspaceEditorID,HasHeightmap,PlacedObjectCount"
        };

        foreach (var cell in cells.OrderBy(c => c.GridX ?? int.MaxValue).ThenBy(c => c.GridY ?? int.MaxValue))
        {
            var cellFormId = cell.FormId != 0 ? $"{cell.FormId:X8}" : "";
            var cellEditorId = CsvEscape(cell.EditorId ?? "");
            var cellName = CsvEscape(cell.FullName ?? "");
            var gridX = cell.GridX?.ToString() ?? "";
            var gridY = cell.GridY?.ToString() ?? "";
            var isInterior = (cell.Flags & 0x01) != 0 ? "True" : "False";
            var hasWater = (cell.Flags & 0x02) != 0 ? "True" : "False";

            // Resolve worldspace EditorID
            var wsEditorId = "";
            if (cell.WorldspaceFormId.HasValue &&
                worldspaceEditorIds.TryGetValue(cell.WorldspaceFormId.Value, out var wsName))
            {
                wsEditorId = wsName;
            }
            else if (cell.WorldspaceFormId.HasValue &&
                     formIdMap.TryGetValue(cell.WorldspaceFormId.Value, out var wsEdid))
            {
                wsEditorId = wsEdid;
            }

            var hasHeightmap = cell.Heightmap != null ? "True" : "False";
            var objectCount = cell.PlacedObjects.Count.ToString();

            lines.Add(
                $"{cellFormId},{cellEditorId},{cellName},{gridX},{gridY}," +
                $"{isInterior},{hasWater},{wsEditorId},{hasHeightmap},{objectCount}");
        }

        await File.WriteAllLinesAsync(path, lines);

        Log.Debug($"  [ESM] Exported {cells.Count} cells to cell_info.csv");
    }

    /// <summary>
    ///     Export dialogue lines to a dedicated report file.
    ///     Contains all INFO records that have extracted dialogue prompt text.
    /// </summary>
    private static async Task ExportDialogueAsync(List<RuntimeEditorIdEntry> entries, string outputDir)
    {
        var dialogueEntries = entries
            .Where(e => !string.IsNullOrEmpty(e.DialogueLine))
            .OrderBy(e => e.EditorId)
            .ToList();

        if (dialogueEntries.Count == 0)
        {
            return;
        }

        var path = Path.Combine(outputDir, "dialogue.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"Dialogue Lines ({dialogueEntries.Count:N0})");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine();

        foreach (var entry in dialogueEntries)
        {
            var formId = entry.FormId != 0 ? $"[{entry.FormId:X8}]" : "";
            sb.AppendLine($"{formId} {entry.EditorId}");
            sb.AppendLine($"  \"{entry.DialogueLine}\"");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(path, sb.ToString());

        Log.Debug($"  [ESM] Exported {dialogueEntries.Count} dialogue lines to dialogue.txt");
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static async Task ExportRuntimeEditorIdsAsync(
        List<RuntimeEditorIdEntry> runtimeEditorIds,
        string outputDir)
    {
        if (runtimeEditorIds.Count == 0)
        {
            return;
        }

        var report = GeckReportGenerator.GenerateRuntimeEditorIdsReport(runtimeEditorIds);
        var path = Path.Combine(outputDir, "runtime_editorids.csv");
        await File.WriteAllTextAsync(path, report);

        Log.Debug($"  [ESM] Exported {runtimeEditorIds.Count} runtime EditorIDs to runtime_editorids.csv");
    }

    private static async Task ExportAssetStringsAsync(
        List<DetectedAssetString> assetStrings,
        string outputDir)
    {
        if (assetStrings.Count == 0)
        {
            return;
        }

        var report = GeckReportGenerator.GenerateAssetListReport(assetStrings);
        var path = Path.Combine(outputDir, "assets.txt");
        await File.WriteAllTextAsync(path, report);

        Log.Debug($"  [ESM] Exported {assetStrings.Count} asset strings to assets.txt");
    }
}
