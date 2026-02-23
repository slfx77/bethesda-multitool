using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using Spectre.Console;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Compare heightmap data between two ESM files and generate teleport commands.
/// </summary>
internal static class CompareHeightmapsCommand
{
    /// <summary>
    ///     Compares heightmaps between two ESM files and generates console commands for teleportation.
    /// </summary>
    internal static int CompareHeightmaps(string file1Path, string file2Path, string? worldspaceName,
        string outputPath, int threshold, int maxResults, bool showStats)
    {
        AnsiConsole.MarkupLine("[blue]Comparing heightmaps between:[/]");
        AnsiConsole.MarkupLine($"  File 1: [cyan]{Path.GetFileName(file1Path)}[/]");
        AnsiConsole.MarkupLine($"  File 2: [cyan]{Path.GetFileName(file2Path)}[/]");
        AnsiConsole.WriteLine();

        // Load both ESM files
        var esm1 = EsmFileLoader.Load(file1Path, false);
        var esm2 = EsmFileLoader.Load(file2Path, false);
        if (esm1 == null || esm2 == null)
        {
            return 1;
        }

        // Determine target worldspace
        uint targetFormId;
        if (string.IsNullOrEmpty(worldspaceName))
        {
            targetFormId = FalloutWorldspaces.KnownWorldspaces[FalloutWorldspaces.DefaultWorldspace];
            worldspaceName = FalloutWorldspaces.DefaultWorldspace;
        }
        else if (FalloutWorldspaces.KnownWorldspaces.TryGetValue(worldspaceName, out var knownId))
        {
            targetFormId = knownId;
        }
        else
        {
            var parsed = EsmFileLoader.ParseFormId(worldspaceName);
            if (parsed == null)
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] Unknown worldspace '{worldspaceName}'");
                return 1;
            }

            targetFormId = parsed.Value;
        }

        AnsiConsole.MarkupLine($"Worldspace: [cyan]{worldspaceName}[/] (0x{targetFormId:X8})");
        AnsiConsole.MarkupLine($"Difference threshold: [cyan]{threshold}[/] world units");
        AnsiConsole.WriteLine();

        // Extract worldspace bounds from WRLD record (use file 2 / final)
        var bounds = HeightmapDataParser.ExtractWorldspaceBounds(esm2.Data, esm2.IsBigEndian, targetFormId);
        if (bounds != null)
        {
            AnsiConsole.MarkupLine(
                $"Playable bounds: X=[cyan]{bounds.MinCellX}[/] to [cyan]{bounds.MaxCellX}[/], Y=[cyan]{bounds.MinCellY}[/] to [cyan]{bounds.MaxCellY}[/]");
            AnsiConsole.WriteLine();
        }

        // Extract heightmaps from both files (returns heightmaps and cell names)
        var (heightmaps1, cellNames1) =
            HeightmapDataParser.ExtractHeightmapsForComparison(esm1.Data, esm1.IsBigEndian, targetFormId, "File 1");
        var (heightmaps2, cellNames2) =
            HeightmapDataParser.ExtractHeightmapsForComparison(esm2.Data, esm2.IsBigEndian, targetFormId, "File 2");

        // Merge cell names from both files (prefer file 2 / final)
        var cellNames = new Dictionary<(int, int), string>(cellNames1);
        foreach (var kvp in cellNames2)
        {
            cellNames[kvp.Key] = kvp.Value;
        }

        if (heightmaps1.Count == 0 || heightmaps2.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Could not extract heightmaps from one or both files");
            return 1;
        }

        AnsiConsole.MarkupLine($"File 1: [cyan]{heightmaps1.Count}[/] cells with heightmap data");
        AnsiConsole.MarkupLine($"File 2: [cyan]{heightmaps2.Count}[/] cells with heightmap data");
        AnsiConsole.WriteLine();

        // Compare heightmaps and find differences
        var differences = HeightmapDataParser.CompareHeightmapData(heightmaps1, heightmaps2, threshold, cellNames);

        AnsiConsole.MarkupLine(
            $"Found [cyan]{differences.Count}[/] cells with significant differences (>= {threshold} units)");

        if (differences.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No significant terrain differences found between the two files.[/]");
            return 0;
        }

        // Group adjacent cells together
        var allGroups = CellUtils.GroupAdjacentCells(differences);
        AnsiConsole.MarkupLine($"Consolidated into [cyan]{allGroups.Count}[/] contiguous regions");
        AnsiConsole.WriteLine();

        // Separate in-bounds and out-of-bounds groups
        List<CellGroup> inBoundsGroups;
        List<CellGroup> outOfBoundsGroups;
        if (bounds != null)
        {
            // A group is in-bounds if ANY of its cells are in bounds
            inBoundsGroups = allGroups.Where(g => g.Cells.Any(c => bounds.IsInBounds(c.CellX, c.CellY))).ToList();
            outOfBoundsGroups = allGroups.Where(g => g.Cells.All(c => !bounds.IsInBounds(c.CellX, c.CellY))).ToList();
        }
        else
        {
            inBoundsGroups = allGroups;
            outOfBoundsGroups = [];
        }

        // Sort by impact score (magnitude x coverage)
        inBoundsGroups = inBoundsGroups.OrderByDescending(g => g.ImpactScore).ToList();
        outOfBoundsGroups = outOfBoundsGroups.OrderByDescending(g => g.ImpactScore).ToList();

        // Limit results if requested (apply to groups, not cells)
        var displayInBounds = inBoundsGroups;
        var displayOutBounds = outOfBoundsGroups;
        if (maxResults > 0)
        {
            var totalToShow = Math.Min(maxResults, inBoundsGroups.Count + outOfBoundsGroups.Count);
            if (totalToShow < allGroups.Count)
            {
                AnsiConsole.MarkupLine($"[grey]Showing top {totalToShow} regions (use --max 0 to show all)[/]");
                // Prioritize in-bounds groups
                displayInBounds = inBoundsGroups.Take(maxResults).ToList();
                var remaining = maxResults - displayInBounds.Count;
                displayOutBounds = remaining > 0 ? outOfBoundsGroups.Take(remaining).ToList() : [];
            }
        }

        // Display summary table for in-bounds groups
        if (displayInBounds.Count > 0)
        {
            AnsiConsole.MarkupLine("[green]IN-BOUNDS TERRAIN REGIONS:[/]");
            HeightmapComparisonHelper.DisplayGroupTable(displayInBounds, worldspaceName);
            AnsiConsole.WriteLine();
        }

        // Display summary table for out-of-bounds groups
        if (displayOutBounds.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]OUT-OF-BOUNDS TERRAIN REGIONS:[/]");
            HeightmapComparisonHelper.DisplayGroupTable(displayOutBounds, worldspaceName);
            AnsiConsole.WriteLine();
        }

        // Show statistics if requested
        if (showStats)
        {
            HeightmapComparisonHelper.ShowComparisonStats(heightmaps1, heightmaps2, differences, allGroups,
                inBoundsGroups, outOfBoundsGroups);
        }

        // Generate output file with detailed information and console commands
        HeightmapComparisonHelper.GenerateHeightmapComparisonOutput(outputPath, worldspaceName, file1Path, file2Path,
            inBoundsGroups, outOfBoundsGroups, bounds);

        AnsiConsole.MarkupLine($"[green]Output saved to:[/] [cyan]{outputPath}[/]");
        return 0;
    }
}
