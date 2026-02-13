using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace MinidumpAnalyzer.Commands;

/// <summary>
///     Commands for script analysis in memory dumps.
/// </summary>
public static class ScriptCommands
{
    /// <summary>
    ///     Creates the 'scripts' parent command with subcommands.
    /// </summary>
    public static Command CreateScriptsCommand()
    {
        var command = new Command("scripts", "Script analysis commands");
        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateShowCommand());
        command.Subcommands.Add(CreateCompareCommand());
        command.Subcommands.Add(CreateCrossRefsCommand());
        return command;
    }

    public static Command CreateListCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };

        var command = new Command("list", "List all scripts found in a memory dump");
        command.Arguments.Add(inputArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            await ListScriptsAsync(input);
        });

        return command;
    }

    public static Command CreateShowCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };
        var nameArg = new Argument<string>("script") { Description = "Script EditorId or FormId (hex)" };

        var command = new Command("show", "Show details of a specific script");
        command.Arguments.Add(inputArg);
        command.Arguments.Add(nameArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var name = parseResult.GetValue(nameArg)!;
            await ShowScriptAsync(input, name);
        });

        return command;
    }

    public static Command CreateCompareCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };
        var reportOpt = new Option<string?>("-r", "--report") { Description = "Write detailed mismatch report to file" };
        var scriptOpt = new Option<string?>("--script") { Description = "Compare only this script (EditorId or FormId)" };
        var categoryOpt = new Option<string?>("--category") { Description = "Filter report to specific category (e.g., Other, UnresolvedVariable)" };

        var command = new Command("compare", "Semantic comparison of SCTX source vs decompiled SCDA bytecode");
        command.Arguments.Add(inputArg);
        command.Options.Add(reportOpt);
        command.Options.Add(scriptOpt);
        command.Options.Add(categoryOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var reportPath = parseResult.GetValue(reportOpt);
            var scriptFilter = parseResult.GetValue(scriptOpt);
            var categoryFilter = parseResult.GetValue(categoryOpt);
            await CompareScriptsAsync(input, reportPath, scriptFilter, categoryFilter);
        });

        return command;
    }

    public static Command CreateCrossRefsCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };

        var command = new Command("crossrefs", "Show cross-reference chain diagnostics for variable resolution");
        command.Arguments.Add(inputArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            await CrossRefDiagnosticsAsync(input);
        });

        return command;
    }

    #region Shared loader

    private static async Task<(RecordCollection Collection, List<ScriptRecord> Scripts)?> LoadDumpAsync(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error: File not found: {path}[/]");
            return null;
        }

        RecordCollection collection;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Analyzing dump...", async ctx =>
            {
                await Task.Yield(); // Ensure async context
            });

        var analyzer = new FalloutXbox360Utils.Core.Minidump.MinidumpAnalyzer();
        var analysisResult = await analyzer.AnalyzeAsync(path, includeMetadata: true);

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error: No ESM records found in dump[/]");
            return null;
        }

        var fileInfo = new FileInfo(path);
        using var mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var reconstructor = new RecordParser(
            analysisResult.EsmRecords,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);

        collection = reconstructor.ReconstructAll();

        return (collection, collection.Scripts);
    }

    #endregion

    #region List command

    private static async Task ListScriptsAsync(string path)
    {
        var result = await LoadDumpAsync(path);
        if (result == null)
        {
            return;
        }

        var (_, scripts) = result.Value;

        var table = new Table();
        table.AddColumn(new TableColumn("#").RightAligned());
        table.AddColumn("EditorId");
        table.AddColumn("FormId");
        table.AddColumn(new TableColumn("Vars").RightAligned());
        table.AddColumn(new TableColumn("Refs").RightAligned());
        table.AddColumn("SCTX");
        table.AddColumn("SCDA");
        table.AddColumn("Decompiled");
        table.AddColumn("Runtime");

        for (var i = 0; i < scripts.Count; i++)
        {
            var s = scripts[i];
            table.AddRow(
                (i + 1).ToString(),
                s.EditorId ?? "[dim]?[/]",
                $"0x{s.FormId:X8}",
                s.Variables.Count.ToString(),
                s.ReferencedObjects.Count.ToString(),
                s.HasSource ? "[green]Yes[/]" : "[dim]No[/]",
                s.CompiledData is { Length: > 0 } ? "[green]Yes[/]" : "[dim]No[/]",
                !string.IsNullOrEmpty(s.DecompiledText) ? "[green]Yes[/]" : "[dim]No[/]",
                s.FromRuntime ? "[cyan]RT[/]" : "[dim]ESM[/]");
        }

        AnsiConsole.Write(table);

        var withSource = scripts.Count(s => s.HasSource);
        var withBytecode = scripts.Count(s => s.CompiledData is { Length: > 0 });
        var withDecompiled = scripts.Count(s => !string.IsNullOrEmpty(s.DecompiledText));
        var withBoth = scripts.Count(s => s.HasSource && !string.IsNullOrEmpty(s.DecompiledText));
        var fromRuntime = scripts.Count(s => s.FromRuntime);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Total scripts:[/] {scripts.Count}");
        AnsiConsole.MarkupLine($"[cyan]With source (SCTX):[/] {withSource}");
        AnsiConsole.MarkupLine($"[cyan]With bytecode (SCDA):[/] {withBytecode}");
        AnsiConsole.MarkupLine($"[cyan]With decompiled text:[/] {withDecompiled}");
        AnsiConsole.MarkupLine($"[cyan]With both (comparable):[/] {withBoth}");
        AnsiConsole.MarkupLine($"[cyan]From runtime structs:[/] {fromRuntime}");
    }

    #endregion

    #region Show command

    private static async Task ShowScriptAsync(string path, string scriptName)
    {
        var result = await LoadDumpAsync(path);
        if (result == null)
        {
            return;
        }

        var (_, scripts) = result.Value;

        // Find script by EditorId or FormId
        var script = FindScript(scripts, scriptName);
        if (script == null)
        {
            AnsiConsole.MarkupLine($"[red]Script not found: {scriptName}[/]");
            return;
        }

        // Header
        AnsiConsole.MarkupLine($"[cyan]Script:[/] {script.EditorId ?? "(no editor ID)"}");
        AnsiConsole.MarkupLine($"[cyan]FormId:[/] 0x{script.FormId:X8}");
        AnsiConsole.MarkupLine($"[cyan]Variables:[/] {script.Variables.Count}");
        AnsiConsole.MarkupLine($"[cyan]Referenced Objects:[/] {script.ReferencedObjects.Count}");
        AnsiConsole.MarkupLine($"[cyan]Quest Script:[/] {script.IsQuestScript}");
        AnsiConsole.MarkupLine($"[cyan]Runtime:[/] {script.FromRuntime}");

        if (script.OwnerQuestFormId.HasValue)
        {
            AnsiConsole.MarkupLine($"[cyan]Owner Quest:[/] 0x{script.OwnerQuestFormId.Value:X8}");
        }

        // Variables
        if (script.Variables.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]--- Variables ---[/]");
            foreach (var v in script.Variables)
            {
                var typeName = v.Type == 1 ? "int" : "float";
                AnsiConsole.MarkupLine($"  [{v.Index,3}] {typeName,-5} {v.Name ?? "(unnamed)"}");
            }
        }

        // Source text
        if (script.HasSource)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]--- Source (SCTX) ---[/]");
            AnsiConsole.WriteLine(script.SourceText!);
        }

        // Decompiled text
        if (!string.IsNullOrEmpty(script.DecompiledText))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]--- Decompiled (SCDA) ---[/]");
            AnsiConsole.WriteLine(script.DecompiledText);
        }
    }

    #endregion

    #region Compare command

    private static async Task CompareScriptsAsync(
        string path, string? reportPath, string? scriptFilter, string? categoryFilter)
    {
        var result = await LoadDumpAsync(path);
        if (result == null)
        {
            return;
        }

        var (_, scripts) = result.Value;

        var scriptsWithBoth = scripts
            .Where(s => s.HasSource && !string.IsNullOrEmpty(s.DecompiledText))
            .ToList();

        // Apply script filter if specified
        if (!string.IsNullOrEmpty(scriptFilter))
        {
            var filtered = scriptsWithBoth
                .Where(s => MatchesFilter(s, scriptFilter))
                .ToList();

            if (filtered.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]No matching scripts with both SCTX and decompiled text: {scriptFilter}[/]");
                return;
            }

            scriptsWithBoth = filtered;
        }

        AnsiConsole.MarkupLine($"[cyan]Total scripts:[/] {scripts.Count}");
        AnsiConsole.MarkupLine($"[cyan]With both SCTX and decompiled:[/] {scriptsWithBoth.Count}");
        AnsiConsole.WriteLine();

        var nameMap = ScriptComparer.BuildFunctionNameNormalizationMap();

        var totalMatches = 0;
        var aggregateMismatches = new Dictionary<string, int>();
        var scriptResults = new List<(ScriptRecord Script, ScriptComparisonResult Result)>();

        foreach (var script in scriptsWithBoth)
        {
            var compResult = ScriptComparer.CompareScripts(
                script.SourceText!, script.DecompiledText!, nameMap);

            totalMatches += compResult.MatchCount;
            scriptResults.Add((script, compResult));

            foreach (var (category, count) in compResult.MismatchesByCategory)
            {
                aggregateMismatches.TryGetValue(category, out var existing);
                aggregateMismatches[category] = existing + count;
            }
        }

        var totalMismatches = aggregateMismatches.Values.Sum();
        var totalLines = totalMatches + totalMismatches;
        var overallMatchRate = totalLines > 0 ? 100.0 * totalMatches / totalLines : 0;

        // Summary table
        AnsiConsole.MarkupLine("[yellow]=== Semantic Comparison Results ===[/]");
        AnsiConsole.MarkupLine($"[cyan]Total lines compared:[/] {totalLines:N0}");
        AnsiConsole.MarkupLine($"[cyan]Matching lines:[/] {totalMatches:N0}");
        AnsiConsole.MarkupLine($"[cyan]Mismatched lines:[/] {totalMismatches:N0}");

        var rateColor = overallMatchRate >= 80 ? "green" : overallMatchRate >= 60 ? "yellow" : "red";
        AnsiConsole.MarkupLine($"[{rateColor}]Overall match rate: {overallMatchRate:F1}%[/]");
        AnsiConsole.WriteLine();

        // Category breakdown table
        var catTable = new Table();
        catTable.AddColumn("Category");
        catTable.AddColumn(new TableColumn("Count").RightAligned());
        catTable.AddColumn(new TableColumn("% of Total").RightAligned());

        foreach (var (category, count) in aggregateMismatches.OrderByDescending(kv => kv.Value))
        {
            var pct = totalLines > 0 ? 100.0 * count / totalLines : 0;
            catTable.AddRow(category, $"{count:N0}", $"{pct:F1}%");
        }

        AnsiConsole.Write(catTable);

        // Worst scripts
        var worstScripts = scriptResults
            .Where(x => x.Result.TotalMismatches > 0)
            .OrderBy(x => x.Result.MatchRate)
            .Take(10)
            .ToList();

        if (worstScripts.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]--- Worst Scripts ---[/]");
            var worstTable = new Table();
            worstTable.AddColumn("Script");
            worstTable.AddColumn(new TableColumn("Match %").RightAligned());
            worstTable.AddColumn(new TableColumn("Mismatches").RightAligned());

            foreach (var (script, compResult) in worstScripts)
            {
                var name = script.EditorId ?? $"0x{script.FormId:X8}";
                worstTable.AddRow(name, $"{compResult.MatchRate:F1}%", $"{compResult.TotalMismatches}");
            }

            AnsiConsole.Write(worstTable);
        }

        // Write detailed report if requested
        if (!string.IsNullOrEmpty(reportPath))
        {
            WriteDetailedReport(reportPath, scriptResults, nameMap, totalLines, totalMatches,
                totalMismatches, overallMatchRate, aggregateMismatches, categoryFilter);
        }
    }

    private static void WriteDetailedReport(
        string reportPath,
        List<(ScriptRecord Script, ScriptComparisonResult Result)> scriptResults,
        Dictionary<string, string> nameMap,
        int totalLines, int totalMatches, int totalMismatches,
        double overallMatchRate,
        Dictionary<string, int> aggregateMismatches,
        string? categoryFilter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
        using var writer = new StreamWriter(reportPath);

        writer.WriteLine($"=== Semantic Comparison Results ===");
        writer.WriteLine($"Total lines compared: {totalLines:N0}");
        writer.WriteLine($"Matching lines: {totalMatches:N0}");
        writer.WriteLine($"Mismatched lines: {totalMismatches:N0}");
        writer.WriteLine($"Overall match rate: {overallMatchRate:F1}%");
        writer.WriteLine();
        writer.WriteLine($"--- Mismatch Categories ---");
        foreach (var (category, count) in aggregateMismatches.OrderByDescending(kv => kv.Value))
        {
            var pct = totalLines > 0 ? 100.0 * count / totalLines : 0;
            writer.WriteLine($"  {category,-25} {count,6:N0}  ({pct:F1}%)");
        }

        writer.WriteLine();
        writer.WriteLine("--- All Mismatch Examples (first 10 per script) ---");
        foreach (var (script, compResult) in scriptResults)
        {
            if (compResult.Examples.Count == 0)
            {
                continue;
            }

            var examples = compResult.Examples.AsEnumerable();
            if (!string.IsNullOrEmpty(categoryFilter))
            {
                examples = examples.Where(e =>
                    e.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase));
            }

            var filteredExamples = examples.ToList();
            if (filteredExamples.Count == 0)
            {
                continue;
            }

            var name = script.EditorId ?? $"0x{script.FormId:X8}";
            writer.WriteLine(
                $"\n  {name} ({compResult.MatchRate:F1}% match, {compResult.TotalMismatches} mismatches):");
            foreach (var (source, decompiled, category) in filteredExamples)
            {
                writer.WriteLine($"    [{category}]");
                writer.WriteLine($"      SCTX: {source}");
                writer.WriteLine($"      SCDA: {decompiled}");
            }
        }

        AnsiConsole.MarkupLine($"[green]Report written to:[/] {Path.GetFullPath(reportPath)}");
    }

    #endregion

    #region CrossRefs command

    private static async Task CrossRefDiagnosticsAsync(string path)
    {
        var result = await LoadDumpAsync(path);
        if (result == null)
        {
            return;
        }

        var (collection, scripts) = result.Value;

        // Count objects with script pointers
        var npcsWithScript = collection.Npcs.Count(n => n.Script is > 0);
        var creaturesWithScript = collection.Creatures.Count(c => c.Script is > 0);
        var containersWithScript = collection.Containers.Count(c => c.Script is > 0);
        var activatorsWithScript = collection.Activators.Count(a => a.Script is > 0);

        AnsiConsole.MarkupLine("[yellow]=== Cross-Reference Chain Diagnostics ===[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[cyan]Runtime Objects with Script Pointers:[/]");
        var objectTable = new Table();
        objectTable.AddColumn("Type");
        objectTable.AddColumn(new TableColumn("Total").RightAligned());
        objectTable.AddColumn(new TableColumn("With Script").RightAligned());
        objectTable.AddRow("NPCs", $"{collection.Npcs.Count}", $"{npcsWithScript}");
        objectTable.AddRow("Creatures", $"{collection.Creatures.Count}", $"{creaturesWithScript}");
        objectTable.AddRow("Containers", $"{collection.Containers.Count}", $"{containersWithScript}");
        objectTable.AddRow("Activators", $"{collection.Activators.Count}", $"{activatorsWithScript}");
        AnsiConsole.Write(objectTable);

        // Build variableDb like ScriptRecordHandler does
        var variableDb = new Dictionary<uint, List<ScriptVariableInfo>>();
        foreach (var script in scripts)
        {
            if (script.Variables.Count > 0)
            {
                variableDb.TryAdd(script.FormId, script.Variables);
            }

            if (script.OwnerQuestFormId.HasValue && script.Variables.Count > 0)
            {
                variableDb.TryAdd(script.OwnerQuestFormId.Value, script.Variables);
            }
        }

        AnsiConsole.MarkupLine($"\n[cyan]Variable Database:[/] {variableDb.Count} entries (scripts + quests)");

        // Simulate object→script chain extension
        var objectToScript = new Dictionary<uint, uint>();
        foreach (var npc in collection.Npcs)
        {
            if (npc.Script is > 0)
            {
                objectToScript.TryAdd(npc.FormId, npc.Script.Value);
            }
        }

        foreach (var creature in collection.Creatures)
        {
            if (creature.Script is > 0)
            {
                objectToScript.TryAdd(creature.FormId, creature.Script.Value);
            }
        }

        foreach (var container in collection.Containers)
        {
            if (container.Script is > 0)
            {
                objectToScript.TryAdd(container.FormId, container.Script.Value);
            }
        }

        foreach (var activator in collection.Activators)
        {
            if (activator.Script is > 0)
            {
                objectToScript.TryAdd(activator.FormId, activator.Script.Value);
            }
        }

        var extendedCount = 0;
        foreach (var (objectFormId, scriptFormId) in objectToScript)
        {
            if (variableDb.TryGetValue(scriptFormId, out var vars))
            {
                if (variableDb.TryAdd(objectFormId, vars))
                {
                    extendedCount++;
                }
            }
        }

        AnsiConsole.MarkupLine($"[cyan]Object→Script mappings:[/] {objectToScript.Count}");
        AnsiConsole.MarkupLine($"[cyan]Extended variableDb entries:[/] +{extendedCount} (total: {variableDb.Count})");

        // Count unresolved variable references
        var unresolvedPattern = new Regex(@"\w+\.var\d+");
        var totalUnresolved = 0;
        var scriptsWithUnresolved = 0;
        var unresolvedExamples = new List<(string ScriptName, string Ref)>();

        foreach (var script in scripts.Where(s => !string.IsNullOrEmpty(s.DecompiledText)))
        {
            var matches = unresolvedPattern.Matches(script.DecompiledText!);
            if (matches.Count > 0)
            {
                totalUnresolved += matches.Count;
                scriptsWithUnresolved++;

                if (unresolvedExamples.Count < 20)
                {
                    var name = script.EditorId ?? $"0x{script.FormId:X8}";
                    foreach (var m in matches.Cast<Match>().Take(3))
                    {
                        unresolvedExamples.Add((name, m.Value));
                    }
                }
            }
        }

        AnsiConsole.MarkupLine($"\n[cyan]Unresolved Variable References:[/]");
        AnsiConsole.MarkupLine($"  Total: {totalUnresolved}");
        AnsiConsole.MarkupLine($"  Scripts affected: {scriptsWithUnresolved}");

        if (unresolvedExamples.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]--- Sample Unresolved References ---[/]");
            var unresolvedTable = new Table();
            unresolvedTable.AddColumn("Script");
            unresolvedTable.AddColumn("Reference");
            foreach (var (scriptName, refStr) in unresolvedExamples.Take(15))
            {
                unresolvedTable.AddRow(scriptName, refStr);
            }

            AnsiConsole.Write(unresolvedTable);
        }

        // EditorID REF heuristic analysis
        var editorIdDb = collection.FormIdToEditorId;
        var refSuffixHits = 0;
        var refSuffixTotal = 0;
        foreach (var script in scripts)
        {
            foreach (var refFormId in script.ReferencedObjects)
            {
                if (variableDb.ContainsKey(refFormId))
                {
                    continue;
                }

                if (!editorIdDb.TryGetValue(refFormId, out var editorId))
                {
                    continue;
                }

                if (!editorId.EndsWith("REF", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                refSuffixTotal++;
                var baseName = editorId[..^3];
                var baseFormId = editorIdDb
                    .FirstOrDefault(kvp => kvp.Value.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                    .Key;
                if (baseFormId != 0 && variableDb.ContainsKey(baseFormId))
                {
                    refSuffixHits++;
                }
            }
        }

        AnsiConsole.MarkupLine($"\n[cyan]EditorID REF→base heuristic:[/]");
        AnsiConsole.MarkupLine($"  SCRO entries ending in 'REF': {refSuffixTotal}");
        AnsiConsole.MarkupLine($"  Resolvable via base EditorId: {refSuffixHits}");
    }

    #endregion

    #region Helpers

    private static ScriptRecord? FindScript(List<ScriptRecord> scripts, string filter)
    {
        // Try exact EditorId match first
        var match = scripts.FirstOrDefault(s =>
            s.EditorId != null && s.EditorId.Equals(filter, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        // Try FormId (hex)
        var hexStr = filter.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? filter[2..] : filter;
        if (uint.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var formId))
        {
            match = scripts.FirstOrDefault(s => s.FormId == formId);
            if (match != null)
            {
                return match;
            }
        }

        // Try partial EditorId match
        return scripts.FirstOrDefault(s =>
            s.EditorId != null && s.EditorId.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesFilter(ScriptRecord script, string filter)
    {
        if (script.EditorId != null &&
            script.EditorId.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hexStr = filter.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? filter[2..] : filter;
        if (uint.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var formId))
        {
            return script.FormId == formId;
        }

        return false;
    }

    #endregion
}
