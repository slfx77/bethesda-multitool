using System.IO.MemoryMappedFiles;
using System.Text;
using System.CommandLine;
using EsmAnalyzer.Core;
using Spectre.Console;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Finds FormID references in scripts (and optionally all records) that point to
///     non-existent records — potential evidence of cut content.
/// </summary>
public static class OrphanedFormIdCommands
{
    public static Command CreateOrphanRefsCommand()
    {
        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file to scan" };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output TSV file for full results" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Max orphans to display (default: 200)", DefaultValueFactory = _ => 200 };
        var dumpOption = new Option<string?>("--dump")
        { Description = "Path to a .dmp file or directory of dumps — extracts runtime scripts and merges FormIDs" };
        var compareOption = new Option<string?>("--compare")
        { Description = "Second ESM to cross-reference (shows which orphans exist in this file)" };
        var allRecordsOption = new Option<bool>("--all-records")
        { Description = "Also scan non-script records for orphaned FormID references" };

        var command = new Command("orphan-refs",
            "Find FormID references that point to non-existent records (cut content detection)");
        command.Arguments.Add(fileArg);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);
        command.Options.Add(dumpOption);
        command.Options.Add(compareOption);
        command.Options.Add(allRecordsOption);

        command.SetAction(parseResult =>
        {
            var filePath = parseResult.GetValue(fileArg)!;
            var outputPath = parseResult.GetValue(outputOption);
            var limit = parseResult.GetValue(limitOption);
            var dumpPath = parseResult.GetValue(dumpOption);
            var comparePath = parseResult.GetValue(compareOption);
            var allRecords = parseResult.GetValue(allRecordsOption);

            return RunOrphanRefsAsync(filePath, outputPath, limit, dumpPath, comparePath, allRecords);
        });

        return command;
    }

    private static async Task<int> RunOrphanRefsAsync(
        string filePath, string? outputPath, int limit,
        string? dumpPath, string? comparePath, bool allRecords)
    {
        AnsiConsole.MarkupLine("[bold cyan]Orphaned FormID Reference Finder[/]");
        AnsiConsole.MarkupLine($"[grey]ESM:[/] {filePath}");
        if (dumpPath != null)
        {
            AnsiConsole.MarkupLine($"[grey]Dump:[/] {dumpPath}");
        }

        if (comparePath != null)
        {
            AnsiConsole.MarkupLine($"[grey]Compare:[/] {comparePath}");
        }

        AnsiConsole.WriteLine();

        // ── Phase 1: Load ESM and build FormID universe ──────────────────────
        var esm = EsmFileLoader.Load(filePath, printStatus: true);
        if (esm == null)
        {
            return 1;
        }

        var data = esm.Data;
        var bigEndian = esm.IsBigEndian;

        AnsiConsole.MarkupLine("[grey]Building FormID universe...[/]");
        var allEsmRecords = EsmRecordParser.ScanAllRecords(data, bigEndian);
        var knownFormIds = new HashSet<uint>(
            allEsmRecords
                .Where(r => r.Signature != "GRUP" && r.Signature != "TES4")
                .Select(r => r.FormId));

        // Well-known engine FormIDs
        knownFormIds.Add(0x00000014); // Player base
        knownFormIds.Add(0x00000007); // PlayerRef

        AnsiConsole.MarkupLine($"[grey]Known FormIDs from ESM: {knownFormIds.Count:N0}[/]");

        AnsiConsole.MarkupLine("[grey]Building EDID map...[/]");
        var edidMap = EsmHelpers.BuildFormIdToEdidMap(data, bigEndian);

        // Build record type map for context
        var recordTypeMap = new Dictionary<uint, string>();
        foreach (var rec in allEsmRecords)
        {
            if (rec.Signature != "GRUP" && rec.Signature != "TES4")
            {
                recordTypeMap.TryAdd(rec.FormId, rec.Signature);
            }
        }

        // ── Phase 2: Extract scripts from ESM ────────────────────────────────
        AnsiConsole.MarkupLine("[grey]Extracting scripts from ESM...[/]");
        var esmScripts = ExtractScriptsFromEsm(allEsmRecords, data, bigEndian);
        AnsiConsole.MarkupLine($"[grey]Found {esmScripts.Count:N0} scripts in ESM[/]");

        // ── Phase 3: Load dumps (optional) ───────────────────────────────────
        var dumpScripts = new List<(string Source, ScriptRecord Script)>();
        if (dumpPath != null)
        {
            await LoadDumpScriptsAsync(dumpPath, knownFormIds, edidMap, dumpScripts);
        }

        // ── Phase 4: Identify orphans ────────────────────────────────────────
        AnsiConsole.MarkupLine("\n[grey]Cross-referencing...[/]");
        var orphans = new List<OrphanedReference>();
        var stats = new OrphanStats();

        // Check ESM scripts
        foreach (var script in esmScripts)
        {
            stats.ScriptsScanned++;
            foreach (var refFormId in script.ReferencedObjects)
            {
                // Skip SCRV entries (local variable indices marked with high bit)
                if ((refFormId & 0x80000000) != 0)
                {
                    continue;
                }

                stats.TotalScroRefs++;
                if (refFormId == 0)
                {
                    continue;
                }

                if (!knownFormIds.Contains(refFormId))
                {
                    var pluginIndex = (refFormId >> 24) & 0xFF;
                    orphans.Add(new OrphanedReference
                    {
                        Source = "ESM",
                        ScriptEditorId = script.EditorId ?? "(unknown)",
                        ScriptFormId = script.FormId,
                        OrphanedFormId = refFormId,
                        IsExternalPlugin = pluginIndex != 0,
                        PluginIndex = (byte)pluginIndex
                    });
                }
            }
        }

        // Check dump scripts
        foreach (var (source, script) in dumpScripts)
        {
            stats.DumpScriptsScanned++;
            foreach (var refFormId in script.ReferencedObjects)
            {
                // Skip SCRV entries (local variable indices marked with high bit)
                if ((refFormId & 0x80000000) != 0)
                {
                    continue;
                }

                stats.TotalScroRefs++;
                if (refFormId == 0)
                {
                    continue;
                }

                if (!knownFormIds.Contains(refFormId))
                {
                    var pluginIndex = (refFormId >> 24) & 0xFF;
                    orphans.Add(new OrphanedReference
                    {
                        Source = source,
                        ScriptEditorId = script.EditorId ?? "(unknown)",
                        ScriptFormId = script.FormId,
                        OrphanedFormId = refFormId,
                        IsExternalPlugin = pluginIndex != 0,
                        PluginIndex = (byte)pluginIndex
                    });
                }
            }
        }

        // ── Phase 4b: All-records mode (optional) ────────────────────────────
        var allRecordOrphans = new List<AllRecordOrphanedReference>();
        if (allRecords)
        {
            AnsiConsole.MarkupLine("[grey]Scanning all record types for orphaned FormID fields...[/]");
            ScanAllRecordsForOrphans(allEsmRecords, data, bigEndian, knownFormIds, edidMap, allRecordOrphans, stats);
        }

        // ── Phase 5: Decompile scripts with orphans for context ──────────────
        var localOrphans = orphans.Where(o => !o.IsExternalPlugin).ToList();
        var externalOrphans = orphans.Where(o => o.IsExternalPlugin).ToList();
        stats.OrphanedRefs = localOrphans.Count;
        stats.ExternalRefs = externalOrphans.Count;
        stats.UniqueOrphanedFormIds = localOrphans.Select(o => o.OrphanedFormId).Distinct().Count();

        // Decompile context for ESM scripts with orphans
        var orphanedFormIdSet = new HashSet<uint>(localOrphans.Select(o => o.OrphanedFormId));
        DecompileContextForOrphans(esmScripts, orphanedFormIdSet, edidMap, bigEndian, localOrphans);

        // For dump scripts, use their existing DecompiledText
        foreach (var orphan in localOrphans.Where(o => o.Source != "ESM" && o.DecompiledContext == null))
        {
            var dumpScript = dumpScripts
                .FirstOrDefault(ds => ds.Script.FormId == orphan.ScriptFormId).Script;
            if (dumpScript?.DecompiledText != null)
            {
                orphan.DecompiledContext = FindOrphanInText(
                    dumpScript.DecompiledText, orphan.OrphanedFormId);
            }
        }

        // ── Phase 6: Compare mode (optional) ─────────────────────────────────
        Dictionary<uint, string>? compareEdidMap = null;
        HashSet<uint>? compareFormIds = null;
        Dictionary<uint, string>? compareRecordTypeMap = null;
        if (comparePath != null)
        {
            AnsiConsole.MarkupLine($"\n[grey]Loading compare file: {comparePath}[/]");
            var compare = EsmFileLoader.Load(comparePath, printStatus: false);
            if (compare != null)
            {
                var compareRecords = EsmRecordParser.ScanAllRecords(compare.Data, compare.IsBigEndian);
                compareFormIds = new HashSet<uint>(
                    compareRecords
                        .Where(r => r.Signature != "GRUP" && r.Signature != "TES4")
                        .Select(r => r.FormId));
                compareEdidMap = EsmHelpers.BuildFormIdToEdidMap(compare.Data, compare.IsBigEndian);
                compareRecordTypeMap = new Dictionary<uint, string>();
                foreach (var rec in compareRecords)
                {
                    if (rec.Signature != "GRUP" && rec.Signature != "TES4")
                    {
                        compareRecordTypeMap.TryAdd(rec.FormId, rec.Signature);
                    }
                }

                stats.CompareFormIdCount = compareFormIds.Count;

                foreach (var orphan in localOrphans)
                {
                    if (compareFormIds.Contains(orphan.OrphanedFormId))
                    {
                        orphan.ExistsInCompareFile = true;
                        orphan.CompareEdid = compareEdidMap.GetValueOrDefault(orphan.OrphanedFormId);
                        orphan.CompareRecordType = compareRecordTypeMap.GetValueOrDefault(orphan.OrphanedFormId);
                        stats.ExistInCompareFile++;
                    }
                }
            }
        }

        // ── Phase 7: Output ──────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        DisplayStats(stats, dumpPath != null, comparePath != null, allRecords, allRecordOrphans.Count);
        AnsiConsole.WriteLine();

        if (localOrphans.Count == 0 && allRecordOrphans.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No orphaned FormID references found![/]");
            return 0;
        }

        // Group by orphan FormID
        DisplayOrphansByFormId(localOrphans, limit, comparePath != null);

        // External plugin refs (separate section)
        if (externalOrphans.Count > 0)
        {
            AnsiConsole.WriteLine();
            DisplayExternalRefs(externalOrphans);
        }

        // All-records orphans (separate section)
        if (allRecordOrphans.Count > 0)
        {
            AnsiConsole.WriteLine();
            DisplayAllRecordOrphans(allRecordOrphans, limit, edidMap, compareFormIds, compareEdidMap, compareRecordTypeMap);
        }

        // TSV export
        if (!string.IsNullOrEmpty(outputPath))
        {
            WriteTsvOutput(outputPath, localOrphans, externalOrphans, allRecordOrphans, edidMap,
                compareFormIds, compareEdidMap, compareRecordTypeMap);
        }

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ESM Script Extraction
    // ═══════════════════════════════════════════════════════════════════════

    private static List<ParsedScript> ExtractScriptsFromEsm(
        List<AnalyzerRecordInfo> records, byte[] data, bool bigEndian)
    {
        var scripts = new List<ParsedScript>();

        foreach (var record in records.Where(r => r.Signature == "SCPT"))
        {
            var recordDataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
            var recordDataEnd = recordDataStart + (int)record.DataSize;
            if (recordDataEnd > data.Length)
            {
                continue;
            }

            var recordData = data.AsSpan(recordDataStart, (int)record.DataSize).ToArray();

            // Handle compressed records
            if (record.IsCompressed && record.DataSize >= 4)
            {
                var decompressedSize = BinaryUtils.ReadUInt32(recordData, 0, bigEndian);
                if (decompressedSize > 0 && decompressedSize < 100_000_000)
                {
                    try
                    {
                        recordData = EsmHelpers.DecompressZlib(recordData[4..], (int)decompressedSize);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

            string? editorId = null;
            var referencedObjects = new List<uint>();
            var variables = new List<ScriptVariableInfo>();
            byte[]? compiledData = null;
            uint pendingSlsdIndex = 0;
            byte pendingSlsdType = 0;
            bool havePendingSlsd = false;

            foreach (var sub in subrecords)
            {
                switch (sub.Signature)
                {
                    case "EDID":
                        editorId = EsmRecordParser.GetSubrecordString(sub);
                        break;
                    case "SCDA":
                        compiledData = sub.Data;
                        break;
                    case "SCRO":
                        if (sub.Data.Length >= 4)
                        {
                            referencedObjects.Add(BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian));
                        }
                        break;
                    case "SCRV":
                        // Local variable ref — store with high bit marker
                        if (sub.Data.Length >= 4)
                        {
                            referencedObjects.Add(BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian) | 0x80000000);
                        }
                        break;
                    case "SLSD":
                        if (sub.Data.Length >= 16)
                        {
                            pendingSlsdIndex = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                            // Type byte at offset 12
                            pendingSlsdType = sub.Data.Length > 12 ? sub.Data[12] : (byte)0;
                            havePendingSlsd = true;
                        }
                        break;
                    case "SCVR":
                        if (havePendingSlsd)
                        {
                            var varName = EsmRecordParser.GetSubrecordString(sub);
                            variables.Add(new ScriptVariableInfo(pendingSlsdIndex, varName, pendingSlsdType));
                            havePendingSlsd = false;
                        }
                        break;
                }
            }

            scripts.Add(new ParsedScript
            {
                FormId = record.FormId,
                EditorId = editorId,
                ReferencedObjects = referencedObjects,
                Variables = variables,
                CompiledData = compiledData,
                IsBigEndian = bigEndian
            });
        }

        return scripts;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dump Loading
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task LoadDumpScriptsAsync(
        string dumpPath,
        HashSet<uint> knownFormIds,
        Dictionary<uint, string> edidMap,
        List<(string Source, ScriptRecord Script)> dumpScripts)
    {
        var dumpFiles = new List<string>();

        if (Directory.Exists(dumpPath))
        {
            dumpFiles.AddRange(Directory.GetFiles(dumpPath, "*.dmp"));
            AnsiConsole.MarkupLine($"[grey]Found {dumpFiles.Count} dump files in directory[/]");
        }
        else if (File.Exists(dumpPath))
        {
            dumpFiles.Add(dumpPath);
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]WARN: Dump path not found: {dumpPath}[/]");
            return;
        }

        foreach (var dumpFile in dumpFiles)
        {
            var fileName = Path.GetFileName(dumpFile);
            AnsiConsole.MarkupLine($"[grey]Loading dump: {fileName}...[/]");

            try
            {
                var analyzer = new FalloutXbox360Utils.Core.Minidump.MinidumpAnalyzer();
                var analysisResult = await analyzer.AnalyzeAsync(dumpFile, includeMetadata: true);

                if (analysisResult.EsmRecords == null)
                {
                    AnsiConsole.MarkupLine($"[yellow]  No ESM records in {fileName}[/]");
                    continue;
                }

                // Merge FormIDs from dump's FormIdMap
                if (analysisResult.FormIdMap != null)
                {
                    foreach (var (formId, name) in analysisResult.FormIdMap)
                    {
                        knownFormIds.Add(formId);
                        edidMap.TryAdd(formId, name);
                    }
                }

                var fileInfo = new FileInfo(dumpFile);
                using var mmf = MemoryMappedFile.CreateFromFile(
                    dumpFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

                var reconstructor = new FalloutXbox360Utils.Core.Formats.Esm.RecordParser(
                    analysisResult.EsmRecords,
                    analysisResult.FormIdMap,
                    accessor,
                    fileInfo.Length,
                    analysisResult.MinidumpInfo);

                var collection = reconstructor.ReconstructAll();

                // Merge dump FormIDs into known set
                foreach (var (formId, name) in collection.FormIdToEditorId)
                {
                    knownFormIds.Add(formId);
                    edidMap.TryAdd(formId, name);
                }

                foreach (var script in collection.Scripts)
                {
                    dumpScripts.Add((fileName, script));
                }

                AnsiConsole.MarkupLine(
                    $"[grey]  {fileName}: {collection.Scripts.Count} scripts, {collection.FormIdToEditorId.Count} FormIDs[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]  Error loading {fileName}: {ex.Message}[/]");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // All-Records Scanning
    // ═══════════════════════════════════════════════════════════════════════

    private static void ScanAllRecordsForOrphans(
        List<AnalyzerRecordInfo> records, byte[] data, bool bigEndian,
        HashSet<uint> knownFormIds, Dictionary<uint, string> edidMap,
        List<AllRecordOrphanedReference> orphans, OrphanStats stats)
    {
        foreach (var record in records)
        {
            if (record.Signature is "GRUP" or "TES4" or "SCPT")
            {
                continue; // SCPT already handled by script scanning
            }

            var recordDataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
            var recordDataEnd = recordDataStart + (int)record.DataSize;
            if (recordDataEnd > data.Length)
            {
                continue;
            }

            var recordData = data.AsSpan(recordDataStart, (int)record.DataSize).ToArray();

            if (record.IsCompressed && record.DataSize >= 4)
            {
                var decompressedSize = BinaryUtils.ReadUInt32(recordData, 0, bigEndian);
                if (decompressedSize > 0 && decompressedSize < 100_000_000)
                {
                    try
                    {
                        recordData = EsmHelpers.DecompressZlib(recordData[4..], (int)decompressedSize);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

            foreach (var sub in subrecords)
            {
                var schema = SubrecordSchemaRegistry.GetSchema(sub.Signature, record.Signature, sub.Data.Length);
                if (schema == null)
                {
                    continue;
                }

                var offset = 0;
                foreach (var field in schema.Fields)
                {
                    if (offset >= sub.Data.Length)
                    {
                        break;
                    }

                    var fieldSize = field.EffectiveSize;
                    if (fieldSize <= 0)
                    {
                        fieldSize = sub.Data.Length - offset;
                    }

                    if (field.Type is SubrecordFieldType.FormId or SubrecordFieldType.FormIdLittleEndian)
                    {
                        if (offset + 4 <= sub.Data.Length)
                        {
                            stats.AllRecordFormIdFieldsChecked++;

                            var formId = field.Type == SubrecordFieldType.FormIdLittleEndian
                                ? BitConverter.ToUInt32(sub.Data, offset)
                                : BinaryUtils.ReadUInt32(sub.Data, offset, bigEndian);

                            if (formId != 0 && (formId >> 24) == 0 && !knownFormIds.Contains(formId))
                            {
                                orphans.Add(new AllRecordOrphanedReference
                                {
                                    RecordType = record.Signature,
                                    RecordFormId = record.FormId,
                                    SubrecordType = sub.Signature,
                                    FieldName = field.Name,
                                    OrphanedFormId = formId
                                });
                            }
                        }
                    }

                    offset += fieldSize;
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Decompilation for Context
    // ═══════════════════════════════════════════════════════════════════════

    private static void DecompileContextForOrphans(
        List<ParsedScript> scripts,
        HashSet<uint> orphanedFormIds,
        Dictionary<uint, string> edidMap,
        bool bigEndian,
        List<OrphanedReference> orphans)
    {
        // Build set of script FormIDs that have orphans
        var scriptsWithOrphans = new HashSet<uint>(
            orphans.Where(o => o.Source == "ESM").Select(o => o.ScriptFormId));

        foreach (var script in scripts)
        {
            if (!scriptsWithOrphans.Contains(script.FormId))
            {
                continue;
            }

            if (script.CompiledData is not { Length: > 0 })
            {
                continue;
            }

            // Build resolve callback that marks orphans
            string? ResolveFormName(uint formId)
            {
                if (orphanedFormIds.Contains(formId))
                {
                    return $"__ORPHAN_0x{formId:X8}";
                }

                return edidMap.GetValueOrDefault(formId);
            }

            try
            {
                var decompiler = new ScriptDecompiler(
                    script.Variables,
                    script.ReferencedObjects,
                    ResolveFormName,
                    bigEndian,
                    script.EditorId);

                var decompiled = decompiler.Decompile(script.CompiledData);

                // Find context lines for each orphan in this script
                foreach (var orphan in orphans.Where(o =>
                             o.Source == "ESM" && o.ScriptFormId == script.FormId))
                {
                    orphan.DecompiledContext = FindOrphanInText(
                        decompiled, orphan.OrphanedFormId);
                }
            }
            catch
            {
                // Decompilation failure — leave context as null
            }
        }
    }

    private static string? FindOrphanInText(string text, uint orphanedFormId)
    {
        var marker = $"__ORPHAN_0x{orphanedFormId:X8}";
        var hexMarker = $"0x{orphanedFormId:X8}";

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains(hexMarker, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Length > 120 ? trimmed[..117] + "..." : trimmed;
            }
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Display
    // ═══════════════════════════════════════════════════════════════════════

    private static void DisplayStats(OrphanStats stats, bool hasDumps, bool hasCompare, bool allRecords, int allRecordOrphanCount)
    {
        AnsiConsole.MarkupLine("[bold]Scan Statistics:[/]");
        var table = new Table();
        table.AddColumn("Metric");
        table.AddColumn(new TableColumn("Count").RightAligned());
        table.Border = TableBorder.Rounded;

        table.AddRow("ESM scripts scanned", $"{stats.ScriptsScanned:N0}");
        if (hasDumps)
        {
            table.AddRow("Dump scripts scanned", $"{stats.DumpScriptsScanned:N0}");
        }

        table.AddRow("Total SCRO references", $"{stats.TotalScroRefs:N0}");
        table.AddRow("[yellow]Orphaned references (local)[/]", $"[yellow]{stats.OrphanedRefs:N0}[/]");
        table.AddRow("[grey]External plugin references[/]", $"[grey]{stats.ExternalRefs:N0}[/]");
        table.AddRow("Unique orphaned FormIDs", $"{stats.UniqueOrphanedFormIds:N0}");

        if (hasCompare)
        {
            table.AddRow("[green]Orphans found in compare file[/]", $"[green]{stats.ExistInCompareFile:N0}[/]");
        }

        if (allRecords)
        {
            table.AddRow("All-record FormID fields checked", $"{stats.AllRecordFormIdFieldsChecked:N0}");
            table.AddRow("[yellow]All-record orphans[/]", $"[yellow]{allRecordOrphanCount:N0}[/]");
        }

        AnsiConsole.Write(table);
    }

    private static void DisplayOrphansByFormId(List<OrphanedReference> orphans, int limit, bool hasCompare)
    {
        if (orphans.Count == 0)
        {
            return;
        }

        // Group by orphan FormID
        var grouped = orphans
            .GroupBy(o => o.OrphanedFormId)
            .OrderByDescending(g => g.Count())
            .ToList();

        AnsiConsole.MarkupLine($"[bold yellow]Orphaned FormID References ({orphans.Count:N0} total, {grouped.Count:N0} unique FormIDs)[/]");
        if (orphans.Count > limit)
        {
            AnsiConsole.MarkupLine($"[grey](showing first {limit} references)[/]");
        }

        AnsiConsole.WriteLine();

        // Detail table
        var table = new Table();
        table.AddColumn("Source");
        table.AddColumn("Script");
        table.AddColumn("Script FID");
        table.AddColumn("Orphaned FID");
        if (hasCompare)
        {
            table.AddColumn("In Compare?");
        }

        table.AddColumn("Context");
        table.Border = TableBorder.Rounded;

        var shown = 0;
        foreach (var orphan in orphans.Take(limit))
        {
            var context = orphan.DecompiledContext ?? "[dim](no context)[/]";
            // Escape Spectre markup characters in context
            context = context.Replace("[", "[[").Replace("]", "]]");

            var row = new List<string>
            {
                orphan.Source,
                Markup.Escape(orphan.ScriptEditorId),
                $"0x{orphan.ScriptFormId:X8}",
                $"0x{orphan.OrphanedFormId:X8}"
            };

            if (hasCompare)
            {
                if (orphan.ExistsInCompareFile)
                {
                    var info = orphan.CompareRecordType ?? "?";
                    if (orphan.CompareEdid != null)
                    {
                        info += $" {Markup.Escape(orphan.CompareEdid)}";
                    }

                    row.Add($"[green]Yes[/] ({info})");
                }
                else
                {
                    row.Add("[dim]No[/]");
                }
            }

            row.Add(context);
            table.AddRow(row.ToArray());
            shown++;
        }

        AnsiConsole.Write(table);

        // Multi-reference summary
        var multiRef = grouped.Where(g => g.Count() > 1).ToList();
        if (multiRef.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Orphaned FormIDs referenced by multiple scripts:[/]");
            foreach (var group in multiRef.Take(20))
            {
                var scriptNames = string.Join(", ",
                    group.Select(o => o.ScriptEditorId).Distinct().Take(5));
                AnsiConsole.MarkupLine(
                    $"  0x{group.Key:X8}: Referenced by {group.Count()} scripts ({Markup.Escape(scriptNames)})");
            }
        }
    }

    private static void DisplayExternalRefs(List<OrphanedReference> externals)
    {
        var byPlugin = externals.GroupBy(e => e.PluginIndex).OrderBy(g => g.Key).ToList();
        AnsiConsole.MarkupLine($"[bold grey]External Plugin References ({externals.Count:N0} total, {byPlugin.Count} plugins)[/]");
        AnsiConsole.MarkupLine("[grey]These reference master files not in this ESM — expected, not orphans.[/]");

        var table = new Table();
        table.AddColumn("Plugin Index");
        table.AddColumn(new TableColumn("Count").RightAligned());
        table.AddColumn("Sample FormIDs");
        table.Border = TableBorder.Rounded;

        foreach (var group in byPlugin)
        {
            var samples = string.Join(", ",
                group.Select(e => $"0x{e.OrphanedFormId:X8}").Distinct().Take(3));
            table.AddRow($"0x{group.Key:X2}", $"{group.Count():N0}", samples);
        }

        AnsiConsole.Write(table);
    }

    private static void DisplayAllRecordOrphans(
        List<AllRecordOrphanedReference> orphans, int limit,
        Dictionary<uint, string> edidMap,
        HashSet<uint>? compareFormIds,
        Dictionary<uint, string>? compareEdidMap,
        Dictionary<uint, string>? compareRecordTypeMap)
    {
        AnsiConsole.MarkupLine($"[bold yellow]All-Record Orphaned FormID References ({orphans.Count:N0})[/]");
        if (orphans.Count > limit)
        {
            AnsiConsole.MarkupLine($"[grey](showing first {limit})[/]");
        }

        // Group by record type
        var byType = orphans.GroupBy(o => o.RecordType).OrderByDescending(g => g.Count()).ToList();
        var summaryTable = new Table();
        summaryTable.AddColumn("Record Type");
        summaryTable.AddColumn(new TableColumn("Orphan Count").RightAligned());
        summaryTable.Border = TableBorder.Rounded;
        foreach (var group in byType.Take(20))
        {
            summaryTable.AddRow(group.Key, $"{group.Count():N0}");
        }

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        var table = new Table();
        table.AddColumn("Record");
        table.AddColumn("Subrecord");
        table.AddColumn("Field");
        table.AddColumn("Orphaned FID");
        if (compareFormIds != null)
        {
            table.AddColumn("In Compare?");
        }

        table.Border = TableBorder.Rounded;

        foreach (var orphan in orphans.Take(limit))
        {
            var recordEdid = edidMap.GetValueOrDefault(orphan.RecordFormId);
            var recordLabel = recordEdid != null
                ? $"{orphan.RecordType} {Markup.Escape(recordEdid)}"
                : $"{orphan.RecordType} 0x{orphan.RecordFormId:X8}";

            var row = new List<string>
            {
                recordLabel,
                orphan.SubrecordType,
                orphan.FieldName,
                $"0x{orphan.OrphanedFormId:X8}"
            };

            if (compareFormIds != null)
            {
                if (compareFormIds.Contains(orphan.OrphanedFormId))
                {
                    var info = compareRecordTypeMap?.GetValueOrDefault(orphan.OrphanedFormId) ?? "?";
                    var edid = compareEdidMap?.GetValueOrDefault(orphan.OrphanedFormId);
                    if (edid != null)
                    {
                        info += $" {Markup.Escape(edid)}";
                    }

                    row.Add($"[green]Yes[/] ({info})");
                }
                else
                {
                    row.Add("[dim]No[/]");
                }
            }

            table.AddRow(row.ToArray());
        }

        AnsiConsole.Write(table);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TSV Export
    // ═══════════════════════════════════════════════════════════════════════

    private static void WriteTsvOutput(
        string outputPath,
        List<OrphanedReference> localOrphans,
        List<OrphanedReference> externalOrphans,
        List<AllRecordOrphanedReference> allRecordOrphans,
        Dictionary<uint, string> edidMap,
        HashSet<uint>? compareFormIds,
        Dictionary<uint, string>? compareEdidMap,
        Dictionary<uint, string>? compareRecordTypeMap)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);

        writer.WriteLine("Category\tSource\tScriptEditorId\tScriptFormId\tOrphanedFormId\tInCompareFile\tCompareEdid\tCompareRecordType\tContext");

        foreach (var o in localOrphans)
        {
            var inCompare = compareFormIds?.Contains(o.OrphanedFormId) == true ? "Yes" : "No";
            var compareEdid = compareEdidMap?.GetValueOrDefault(o.OrphanedFormId) ?? "";
            var compareType = compareRecordTypeMap?.GetValueOrDefault(o.OrphanedFormId) ?? "";
            writer.WriteLine(
                $"Script\t{o.Source}\t{o.ScriptEditorId}\t0x{o.ScriptFormId:X8}\t0x{o.OrphanedFormId:X8}\t{inCompare}\t{compareEdid}\t{compareType}\t{o.DecompiledContext ?? ""}");
        }

        foreach (var o in externalOrphans)
        {
            writer.WriteLine(
                $"External\t{o.Source}\t{o.ScriptEditorId}\t0x{o.ScriptFormId:X8}\t0x{o.OrphanedFormId:X8}\t\t\t\tPlugin index 0x{o.PluginIndex:X2}");
        }

        foreach (var o in allRecordOrphans)
        {
            var inCompare = compareFormIds?.Contains(o.OrphanedFormId) == true ? "Yes" : "No";
            var compareEdid = compareEdidMap?.GetValueOrDefault(o.OrphanedFormId) ?? "";
            var compareType = compareRecordTypeMap?.GetValueOrDefault(o.OrphanedFormId) ?? "";
            var recordEdid = edidMap.GetValueOrDefault(o.RecordFormId) ?? o.RecordType;
            writer.WriteLine(
                $"AllRecord\t{o.RecordType}\t{recordEdid}\t0x{o.RecordFormId:X8}\t0x{o.OrphanedFormId:X8}\t{inCompare}\t{compareEdid}\t{compareType}\t{o.SubrecordType}.{o.FieldName}");
        }

        AnsiConsole.MarkupLine($"[grey]Full results written to: {Path.GetFullPath(outputPath)}[/]");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Data Structures
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class ParsedScript
    {
        public uint FormId { get; init; }
        public string? EditorId { get; init; }
        public List<uint> ReferencedObjects { get; init; } = [];
        public List<ScriptVariableInfo> Variables { get; init; } = [];
        public byte[]? CompiledData { get; init; }
        public bool IsBigEndian { get; init; }
    }

    private sealed class OrphanedReference
    {
        public required string Source { get; init; }
        public required string ScriptEditorId { get; init; }
        public uint ScriptFormId { get; init; }
        public uint OrphanedFormId { get; init; }
        public bool IsExternalPlugin { get; init; }
        public byte PluginIndex { get; init; }
        public string? DecompiledContext { get; set; }
        public bool ExistsInCompareFile { get; set; }
        public string? CompareEdid { get; set; }
        public string? CompareRecordType { get; set; }
    }

    private sealed class AllRecordOrphanedReference
    {
        public required string RecordType { get; init; }
        public uint RecordFormId { get; init; }
        public required string SubrecordType { get; init; }
        public required string FieldName { get; init; }
        public uint OrphanedFormId { get; init; }
    }

    private sealed class OrphanStats
    {
        public int ScriptsScanned { get; set; }
        public int DumpScriptsScanned { get; set; }
        public int TotalScroRefs { get; set; }
        public int OrphanedRefs { get; set; }
        public int ExternalRefs { get; set; }
        public int UniqueOrphanedFormIds { get; set; }
        public int ExistInCompareFile { get; set; }
        public int CompareFormIdCount { get; set; }
        public int AllRecordFormIdFieldsChecked { get; set; }
    }
}
