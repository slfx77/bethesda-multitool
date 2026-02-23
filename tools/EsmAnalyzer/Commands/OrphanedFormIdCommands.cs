using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using Spectre.Console;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using static EsmAnalyzer.Commands.OrphanedRefAnalyzer;

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
        var esmScripts = OrphanedRefAnalyzer.ExtractScriptsFromEsm(allEsmRecords, data, bigEndian);
        AnsiConsole.MarkupLine($"[grey]Found {esmScripts.Count:N0} scripts in ESM[/]");

        // ── Phase 3: Load dumps (optional) ───────────────────────────────────
        var dumpScripts = new List<(string Source, ScriptRecord Script)>();
        if (dumpPath != null)
        {
            await OrphanedRefAnalyzer.LoadDumpScriptsAsync(dumpPath, knownFormIds, edidMap, dumpScripts);
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
            OrphanedRefAnalyzer.ScanAllRecordsForOrphans(
                allEsmRecords, data, bigEndian, knownFormIds, edidMap, allRecordOrphans, stats);
        }

        // ── Phase 5: Decompile scripts with orphans for context ──────────────
        var localOrphans = orphans.Where(o => !o.IsExternalPlugin).ToList();
        var externalOrphans = orphans.Where(o => o.IsExternalPlugin).ToList();
        stats.OrphanedRefs = localOrphans.Count;
        stats.ExternalRefs = externalOrphans.Count;
        stats.UniqueOrphanedFormIds = localOrphans.Select(o => o.OrphanedFormId).Distinct().Count();

        // Decompile context for ESM scripts with orphans
        var orphanedFormIdSet = new HashSet<uint>(localOrphans.Select(o => o.OrphanedFormId));
        OrphanedRefAnalyzer.DecompileContextForOrphans(
            esmScripts, orphanedFormIdSet, edidMap, bigEndian, localOrphans);

        // For dump scripts, use their existing DecompiledText
        foreach (var orphan in localOrphans.Where(o => o.Source != "ESM" && o.DecompiledContext == null))
        {
            var dumpScript = dumpScripts
                .FirstOrDefault(ds => ds.Script.FormId == orphan.ScriptFormId).Script;
            if (dumpScript?.DecompiledText != null)
            {
                orphan.DecompiledContext = OrphanedRefAnalyzer.FindOrphanInText(
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
        OrphanedRefDisplay.DisplayStats(stats, dumpPath != null, comparePath != null, allRecords, allRecordOrphans.Count);
        AnsiConsole.WriteLine();

        if (localOrphans.Count == 0 && allRecordOrphans.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No orphaned FormID references found![/]");
            return 0;
        }

        // Group by orphan FormID
        OrphanedRefDisplay.DisplayOrphansByFormId(localOrphans, limit, comparePath != null);

        // External plugin refs (separate section)
        if (externalOrphans.Count > 0)
        {
            AnsiConsole.WriteLine();
            OrphanedRefDisplay.DisplayExternalRefs(externalOrphans);
        }

        // All-records orphans (separate section)
        if (allRecordOrphans.Count > 0)
        {
            AnsiConsole.WriteLine();
            OrphanedRefDisplay.DisplayAllRecordOrphans(
                allRecordOrphans, limit, edidMap, compareFormIds, compareEdidMap, compareRecordTypeMap);
        }

        // TSV export
        if (!string.IsNullOrEmpty(outputPath))
        {
            OrphanedRefDisplay.WriteTsvOutput(outputPath, localOrphans, externalOrphans, allRecordOrphans, edidMap,
                compareFormIds, compareEdidMap, compareRecordTypeMap);
        }

        return 0;
    }
}
