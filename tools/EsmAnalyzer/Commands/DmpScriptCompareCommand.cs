using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Compare subcommand logic for DMP script analysis.
///     Performs semantic comparison of SCTX source vs decompiled SCDA bytecode.
/// </summary>
internal static class DmpScriptCompareCommand
{
    internal static async Task CompareScriptsAsync(
        string path, string? reportPath, string? scriptFilter, string? categoryFilter)
    {
        var result = await DmpScriptCommands.LoadDumpAsync(path);
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
            WriteDetailedReport(reportPath, scriptResults, totalLines, totalMatches,
                totalMismatches, overallMatchRate, aggregateMismatches, categoryFilter);
        }
    }

    private static void WriteDetailedReport(
        string reportPath,
        List<(ScriptRecord Script, ScriptComparisonResult Result)> scriptResults,
        int totalLines, int totalMatches, int totalMismatches,
        double overallMatchRate,
        Dictionary<string, int> aggregateMismatches,
        string? categoryFilter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
        using var writer = new StreamWriter(reportPath);

        writer.WriteLine("=== Semantic Comparison Results ===");
        writer.WriteLine($"Total lines compared: {totalLines:N0}");
        writer.WriteLine($"Matching lines: {totalMatches:N0}");
        writer.WriteLine($"Mismatched lines: {totalMismatches:N0}");
        writer.WriteLine($"Overall match rate: {overallMatchRate:F1}%");
        writer.WriteLine();
        writer.WriteLine("--- Mismatch Categories ---");
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
}
