using Spectre.Console;
using System.Text;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Display and output rendering helpers for heightmap comparison results.
/// </summary>
internal static class HeightmapComparisonHelper
{
    /// <summary>
    ///     Displays a summary table of cell groups.
    /// </summary>
    internal static void DisplayGroupTable(List<CellGroup> groups, string worldspaceName)
    {
        var table = new Table();
        _ = table.AddColumn("Rank");
        _ = table.AddColumn("Region");
        _ = table.AddColumn("Size");
        _ = table.AddColumn("Max Diff");
        _ = table.AddColumn("Avg Diff");
        _ = table.AddColumn("Points Changed");
        _ = table.AddColumn("Console Command");

        var rank = 1;
        foreach (var group in groups)
        {
            var center = group.CenterCell;
            if (center == null) continue;
            var command = $"cow {worldspaceName} {center.CellX} {center.CellY}";

            string locationName;
            if (group.Cells.Count == 1)
            {
                locationName = string.IsNullOrEmpty(center.EditorId)
                    ? $"({center.CellX}, {center.CellY})"
                    : $"{center.EditorId} ({center.CellX}, {center.CellY})";
            }
            else
            {
                var namedPart = string.IsNullOrEmpty(group.CombinedEditorIds) ? "" : $"{group.CombinedEditorIds} ";
                locationName = $"{namedPart}({group.MinX},{group.MinY}) to ({group.MaxX},{group.MaxY})";
            }

            _ = table.AddRow(
                rank.ToString(),
                locationName,
                group.SizeDescription,
                $"{group.MaxDifference:F0}",
                $"{group.AvgDifference:F0}",
                $"{group.TotalDiffPointCount}/{group.TotalPoints}",
                command
            );
            rank++;
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    ///     Shows additional comparison statistics.
    /// </summary>
    internal static void ShowComparisonStats(
        Dictionary<(int x, int y), float[,]> heightmaps1,
        Dictionary<(int x, int y), float[,]> heightmaps2,
        List<CellHeightDifference> differences,
        List<CellGroup> allGroups,
        List<CellGroup> inBoundsGroups,
        List<CellGroup> outOfBoundsGroups)
    {
        AnsiConsole.MarkupLine("[blue]Comparison Statistics:[/]");

        var commonCells = heightmaps1.Keys.Intersect(heightmaps2.Keys).Count();
        var onlyIn1 = heightmaps1.Keys.Except(heightmaps2.Keys).Count();
        var onlyIn2 = heightmaps2.Keys.Except(heightmaps1.Keys).Count();

        AnsiConsole.MarkupLine($"  Common cells: {commonCells}");
        AnsiConsole.MarkupLine($"  Only in File 1: {onlyIn1}");
        AnsiConsole.MarkupLine($"  Only in File 2: {onlyIn2}");
        AnsiConsole.MarkupLine($"  Cells with differences: {differences.Count}");
        AnsiConsole.MarkupLine($"  Contiguous regions: {allGroups.Count}");

        if (differences.Count > 0)
        {
            var avgMaxDiff = differences.Average(d => d.MaxDifference);
            var totalChangedPoints = differences.Sum(d => d.DiffPointCount);
            AnsiConsole.MarkupLine($"  Average max difference: {avgMaxDiff:F0} units");
            AnsiConsole.MarkupLine($"  Total changed height points: {totalChangedPoints}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"  [green]In playable area: {inBoundsGroups.Count} regions ({inBoundsGroups.Sum(g => g.Cells.Count)} cells)[/]");
        AnsiConsole.MarkupLine(
            $"  [grey]Out of bounds: {outOfBoundsGroups.Count} regions ({outOfBoundsGroups.Sum(g => g.Cells.Count)} cells)[/]");

        // Show largest groups
        if (allGroups.Count > 0)
        {
            var largestGroup = allGroups.OrderByDescending(g => g.Cells.Count).First();
            if (largestGroup.Cells.Count > 1)
            {
                AnsiConsole.MarkupLine(
                    $"  Largest region: {largestGroup.SizeDescription} at ({largestGroup.MinX},{largestGroup.MinY}) to ({largestGroup.MaxX},{largestGroup.MaxY})");
            }
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>
    ///     Generates the output file with console commands.
    /// </summary>
    internal static void GenerateHeightmapComparisonOutput(string outputPath, string worldspaceName, string file1Path,
        string file2Path,
        List<CellGroup> inBoundsGroups, List<CellGroup> outOfBoundsGroups, WorldspaceBounds? bounds)
    {
        var sb = new StringBuilder();
        var totalCells = inBoundsGroups.Sum(g => g.Cells.Count) + outOfBoundsGroups.Sum(g => g.Cells.Count);

        _ = sb.AppendLine("================================================================================");
        _ = sb.AppendLine("FALLOUT: NEW VEGAS - TERRAIN DIFFERENCE ANALYSIS");
        _ = sb.AppendLine("================================================================================");
        _ = sb.AppendLine();
        _ = sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _ = sb.AppendLine($"File 1 (Proto): {Path.GetFileName(file1Path)}");
        _ = sb.AppendLine($"File 2 (Final): {Path.GetFileName(file2Path)}");
        _ = sb.AppendLine($"Worldspace: {worldspaceName}");
        if (bounds != null)
        {
            _ = sb.AppendLine(
                $"Playable Area: X=[{bounds.MinCellX} to {bounds.MaxCellX}], Y=[{bounds.MinCellY} to {bounds.MaxCellY}]");
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine(
            $"Total differences: {totalCells} cells in {inBoundsGroups.Count + outOfBoundsGroups.Count} contiguous regions");
        _ = sb.AppendLine(
            $"  In playable area: {inBoundsGroups.Count} regions ({inBoundsGroups.Sum(g => g.Cells.Count)} cells)");
        _ = sb.AppendLine(
            $"  Out of bounds: {outOfBoundsGroups.Count} regions ({outOfBoundsGroups.Sum(g => g.Cells.Count)} cells)");
        _ = sb.AppendLine();
        _ = sb.AppendLine("================================================================================");
        _ = sb.AppendLine("HOW TO USE THESE COMMANDS");
        _ = sb.AppendLine("================================================================================");
        _ = sb.AppendLine();
        _ = sb.AppendLine("1. Open the game console with the ~ (tilde) key");
        _ = sb.AppendLine("2. Copy and paste the command (or type it manually)");
        _ = sb.AppendLine("3. Press Enter to teleport");
        _ = sb.AppendLine();
        _ = sb.AppendLine("Commands:");
        _ = sb.AppendLine("  cow <worldspace> <x> <y>  - Center on World (teleport to cell)");
        _ = sb.AppendLine("  player.setpos x <val>    - Fine-tune X position");
        _ = sb.AppendLine("  player.setpos y <val>    - Fine-tune Y position");
        _ = sb.AppendLine("  player.setpos z <val>    - Adjust height (if stuck underground)");
        _ = sb.AppendLine("  tcl                      - Toggle collision (if stuck)");
        _ = sb.AppendLine();
        _ = sb.AppendLine("================================================================================");
        _ = sb.AppendLine("IN-BOUNDS TERRAIN REGIONS (playable area)");
        _ = sb.AppendLine("================================================================================");
        _ = sb.AppendLine();

        if (inBoundsGroups.Count == 0)
        {
            _ = sb.AppendLine("No terrain differences found within the playable area.");
            _ = sb.AppendLine();
        }
        else
        {
            var rank = 1;
            foreach (var group in inBoundsGroups)
            {
                AppendGroupEntry(sb, group, worldspaceName, rank++);
            }
        }

        if (outOfBoundsGroups.Count > 0)
        {
            _ = sb.AppendLine("================================================================================");
            _ = sb.AppendLine("OUT-OF-BOUNDS TERRAIN REGIONS (outside playable area)");
            _ = sb.AppendLine("================================================================================");
            _ = sb.AppendLine();

            var rank = 1;
            foreach (var group in outOfBoundsGroups)
            {
                AppendGroupEntry(sb, group, worldspaceName, rank++);
            }
        }

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            _ = Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    private static void AppendGroupEntry(StringBuilder sb, CellGroup group, string worldspaceName, int rank)
    {
        var maxDiffCell = group.MaxDiffCell;
        if (maxDiffCell == null) return;

        // Header with region info
        string header;
        if (group.Cells.Count == 1)
        {
            header = string.IsNullOrEmpty(maxDiffCell.EditorId)
                ? $"--- #{rank}: Cell ({maxDiffCell.CellX}, {maxDiffCell.CellY}) ---"
                : $"--- #{rank}: {maxDiffCell.EditorId} ({maxDiffCell.CellX}, {maxDiffCell.CellY}) ---";
        }
        else
        {
            var namedPart = string.IsNullOrEmpty(group.CombinedEditorIds) ? "" : $"{group.CombinedEditorIds} - ";
            header = $"--- #{rank}: {namedPart}Region ({group.MinX},{group.MinY}) to ({group.MaxX},{group.MaxY}) ---";
        }

        _ = sb.AppendLine(header);
        _ = sb.AppendLine($"Region Size: {group.SizeDescription}");
        _ = sb.AppendLine(
            $"Max Height Difference: {group.MaxDifference:F0} units (at cell {maxDiffCell.CellX}, {maxDiffCell.CellY})");
        _ = sb.AppendLine($"Avg Height Difference: {group.AvgDifference:F0} units");
        _ = sb.AppendLine($"Affected Points: {group.TotalDiffPointCount} / {group.TotalPoints}");
        _ = sb.AppendLine();

        // List cells if more than one
        if (group.Cells.Count > 1)
        {
            _ = sb.AppendLine("Cells in this region:");
            foreach (var cell in group.Cells.OrderByDescending(c => c.MaxDifference))
            {
                var cellName = string.IsNullOrEmpty(cell.EditorId)
                    ? $"({cell.CellX}, {cell.CellY})"
                    : $"{cell.EditorId} ({cell.CellX}, {cell.CellY})";
                _ = sb.AppendLine($"  {cellName}: max diff {cell.MaxDifference:F0}, {cell.DiffPointCount} points");
            }

            _ = sb.AppendLine();
        }

        // Calculate world coordinates for the max diff location
        var (worldX, worldY) = CellUtils.CellToWorldCoordinates(
            maxDiffCell.CellX, maxDiffCell.CellY,
            maxDiffCell.MaxDiffLocalX, maxDiffCell.MaxDiffLocalY);
        var estimatedZ = (int)Math.Max(maxDiffCell.AvgHeight1, maxDiffCell.AvgHeight2) + 500;

        _ = sb.AppendLine("Console Commands:");
        _ = sb.AppendLine($"  cow {worldspaceName} {maxDiffCell.CellX} {maxDiffCell.CellY}");
        _ = sb.AppendLine();
        _ = sb.AppendLine("For precise location of max difference:");
        _ = sb.AppendLine($"  player.setpos x {worldX}");
        _ = sb.AppendLine($"  player.setpos y {worldY}");
        _ = sb.AppendLine($"  player.setpos z {estimatedZ}");
        _ = sb.AppendLine();
    }
}
