using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     CrossRefs subcommand logic for DMP script analysis.
///     Shows cross-reference chain diagnostics for variable resolution.
/// </summary>
internal static class DmpScriptCrossRefCommand
{
    internal static async Task CrossRefDiagnosticsAsync(string path)
    {
        var result = await DmpScriptCommands.LoadDumpAsync(path);
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

        // Simulate object->script chain extension
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
}
