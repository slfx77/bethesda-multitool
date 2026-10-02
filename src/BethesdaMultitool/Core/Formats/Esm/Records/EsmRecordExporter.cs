using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Scripts;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

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
        List<CellRecord>? cells = null,
        List<WorldspaceRecord>? worldspaces = null)
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

    /// <summary>
    ///     Writes each carved SCTX fragment VERBATIM to <c>script_sources/sctx_NNNN_0xOFFSET{ext}</c> (the
    ///     Windows-1252 bytes the fragment was decoded from, no BOM, nothing added) and describes them in
    ///     <c>script_sources/manifest.json</c>. The fragments come from a keyword-filtered scan of a memory dump
    ///     and have no owning record, so the manifest names them <c>carved-sctx-fragment</c> and carries the
    ///     partial-capture note. The extension is the dump game's default (<c>.gek</c>).
    /// </summary>
    /// <param name="scriptSources">The carved fragments.</param>
    /// <param name="outputDir">The directory that receives <c>script_sources/</c>.</param>
    /// <param name="sourcePath">
    ///     The dump the fragments were carved from. When given, the manifest records its path, size and
    ///     SHA-256; when null, its <c>source</c> is null.
    /// </param>
    public static async Task ExportScriptSourcesAsync(
        List<SctxRecord> scriptSources,
        string outputDir,
        string? sourcePath = null)
    {
        if (scriptSources.Count == 0)
        {
            return;
        }

        var sctxDir = Path.Combine(outputDir, "script_sources");
        Directory.CreateDirectory(sctxDir);

        // A memory dump is only ever Fallout: New Vegas (MinidumpAnalyzer pins it), so its fragments take
        // that game's script extension.
        var extension = ScriptExportFileNamer.DefaultExtension(GameProfiles.DefaultGame);
        var fragments = new List<CarvedScriptFragment>(scriptSources.Count);
        for (var i = 0; i < scriptSources.Count; i++)
        {
            var sctx = scriptSources[i];
            var filename = $"sctx_{i:D4}_0x{sctx.Offset:X8}{extension}";
            var bytes = EsmStringUtils.EncodeGameText(sctx.Text);
            await File.WriteAllBytesAsync(Path.Combine(sctxDir, filename), bytes);
            fragments.Add(new CarvedScriptFragment(
                filename,
                sctx.Offset,
                sctx.Length,
                ScriptExportWriter.DetectLineEndings(sctx.Text),
                bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        var source = sourcePath is null
            ? null
            : ScriptExportSource.Describe(sourcePath, null, GameProfiles.DefaultGame, true, null);
        await using (var stream = new FileStream(
                         Path.Combine(sctxDir, "manifest.json"),
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None))
        {
            ScriptExportManifestWriter.WriteCarvedFragments(stream, source, fragments, DateTimeOffset.UtcNow);
        }

        Log.Debug($"  [ESM] Exported {scriptSources.Count} script sources to script_sources/");
    }

    /// <summary>
    ///     Exports parsed scripts as individual files under <c>{outputDir}/scripts/</c> for the memory-dump
    ///     extraction pipeline (carve, GUI Extract and Batch). Author-written source goes to
    ///     <c>{stem}{ext}</c> verbatim, a script with no source text but decompiled bytecode gets a
    ///     <c>{stem}.decompiled{ext}</c> reconstruction, and <c>scripts.manifest.json</c> records the source,
    ///     each file's provenance and hash, and the variables and references the old per-script report
    ///     wrapper carried. The extension is the game's default; existing files are replaced, as re-extraction
    ///     always has.
    /// </summary>
    /// <param name="scripts">
    ///     The scripts to export. For a memory dump these must be the dump's OWN scripts, captured before any
    ///     load-order merge, because every file is labelled with <paramref name="source" />'s provenance.
    /// </param>
    /// <param name="formIdMap">Names referenced FormIDs in the manifest; null leaves them unnamed.</param>
    /// <param name="outputDir">The directory that receives <c>scripts/</c>.</param>
    /// <param name="source">Identity of the input the scripts were read from.</param>
    /// <returns>What was written, or null when there were no scripts (nothing is created then).</returns>
    public static Task<ScriptExportSummary?> ExportParsedScriptsAsync(
        IReadOnlyList<ScriptRecord> scripts,
        Dictionary<uint, string>? formIdMap,
        string outputDir,
        ScriptExportSource source)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(source);
        if (scripts.Count == 0)
        {
            return Task.FromResult<ScriptExportSummary?>(null);
        }

        var options = new ScriptExportOptions
        {
            Extension = ScriptExportFileNamer.DefaultExtension(source.Game),
            Decompiled = ScriptDecompiledPolicy.Missing,
            Overwrite = true
        };
        Func<uint, string?>? resolveEditorId = formIdMap is null ? null : formId => formIdMap.GetValueOrDefault(formId);
        var summary = ScriptExportWriter.Write(
            scripts,
            source,
            options,
            Path.Combine(outputDir, "scripts"),
            resolveEditorId,
            DateTimeOffset.UtcNow);

        Log.Debug(
            $"  [ESM] Exported {summary.ScriptCount} scripts to scripts/ ({summary.StoredSourceFiles} plugin source, " +
            $"{summary.CapturedSourceFiles} captured source, {summary.DecompiledFiles} decompiled, " +
            $"{summary.SkippedScripts} skipped)");
        return Task.FromResult<ScriptExportSummary?>(summary);
    }

    /// <summary>
    ///     Export cell information as a cell-centric CSV.
    ///     Each row represents a CELL record with its editor ID, grid coordinates,
    ///     worldspace association, heightmap status, and placed object count.
    /// </summary>
    private static async Task ExportCellInfoAsync(
        List<CellRecord>? cells,
        List<WorldspaceRecord>? worldspaces,
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

        var report = GeckMiscWriter.GenerateRuntimeEditorIdsReport(runtimeEditorIds);
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

        var report = GeckMiscWriter.GenerateAssetListReport(assetStrings);
        var path = Path.Combine(outputDir, "assets.txt");
        await File.WriteAllTextAsync(path, report);

        Log.Debug($"  [ESM] Exported {assetStrings.Count} asset strings to assets.txt");
    }
}
