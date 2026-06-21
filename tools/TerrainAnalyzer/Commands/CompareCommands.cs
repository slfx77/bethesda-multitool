using System.CommandLine;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace TerrainAnalyzer.Commands;

internal static class CompareCommands
{
    internal static Command CreateCompareCommand()
    {
        var command = new Command("vhgt-compare",
            "Compare VHGT delta-decoded heights vs ExactHeights from runtime terrain meshes");

        var dmpArg = new Argument<string>("dmp") { Description = "Path to memory dump file (.dmp)" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };
        var verifyGridOpt = new Option<bool>("--verify-grid")
        {
            Description = "Verify vertex grid ordering matches VHGT convention"
        };

        command.Arguments.Add(dmpArg);
        command.Options.Add(verboseOpt);
        command.Options.Add(verifyGridOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var dmp = parseResult.GetValue(dmpArg)!;
            var verbose = parseResult.GetValue(verboseOpt);
            var verifyGrid = parseResult.GetValue(verifyGridOpt);
            await ExecuteAsync(dmp, verbose, verifyGrid);
        });

        return command;
    }

    private static async Task ExecuteAsync(string dmpPath, bool verbose, bool verifyGrid)
    {
        using var data = await DumpLoader.LoadAsync(dmpPath, preserveVhgt: true, verbose: verbose);

        // Optionally verify vertex grid ordering
        if (verifyGrid)
        {
            VerifyGridOrdering(data);
        }

        // Find cells that had VHGT heightmap AND now have ExactHeights from runtime mesh
        var comparisons = new List<CellComparison>();

        foreach (var land in data.ScanResult.LandRecords)
        {
            if (land.Heightmap?.ExactHeights == null || land.RuntimeTerrainMesh == null)
            {
                continue;
            }

            if (!data.OriginalVhgtHeightmaps.TryGetValue(land.Header.FormId, out var originalVhgt))
            {
                continue;
            }

            var vhgtHeights = originalVhgt.CalculateHeights();
            var exactHeights = land.Heightmap.ExactHeights;
            var sanitizedMask = land.RuntimeTerrainMesh.SanitizedMask;
            var garbageZ = land.RuntimeTerrainMesh.SanitizedZCount;

            // Build a position-based mask: mark vertices as bad if their X,Y coordinates
            // indicate unmapped memory (near origin when they should be in cell-local [-2048,2048])
            var vertices = land.RuntimeTerrainMesh.Vertices;
            var positionMask = new bool[RuntimeTerrainMesh.VertexCount];
            for (var i = 0; i < RuntimeTerrainMesh.VertexCount; i++)
            {
                var x = vertices[i * 3];
                var y = vertices[i * 3 + 1];

                // Valid cell-local coordinates should have |X| or |Y| > ~100
                // Garbage vertices from unmapped memory cluster near (0,0,0)
                if (MathF.Abs(x) < 10f && MathF.Abs(y) < 10f)
                {
                    positionMask[i] = true;
                }

                // Also merge in Z-based sanitization mask
                if (sanitizedMask != null && sanitizedMask[i])
                {
                    positionMask[i] = true;
                }
            }

            // Compute full correlation (all 1089 vertices)
            var fullStats = ComputeCorrelation(vhgtHeights, exactHeights, mask: null);

            // Compute clean correlation (excluding garbage vertices by position + Z)
            var cleanStats = ComputeCorrelation(vhgtHeights, exactHeights, positionMask);

            var cleanCount = positionMask.Count(bad => !bad);

            // When verify-grid is active, test transposed ordering and dump raw values
            if (verifyGrid)
            {
                DiagnoseHeightComparison(
                    land.Header.FormId, land.BestCellX ?? 0, land.BestCellY ?? 0,
                    vhgtHeights, exactHeights, positionMask, vertices);
            }

            comparisons.Add(new CellComparison
            {
                FormId = land.Header.FormId,
                CellX = land.BestCellX ?? 0,
                CellY = land.BestCellY ?? 0,
                VhgtOffset = originalVhgt.HeightOffset,
                ExactOffset = land.Heightmap.HeightOffset,
                MeanError = fullStats.MeanError,
                MaxError = fullStats.MaxError,
                Correlation = fullStats.Correlation,
                CleanCorrelation = cleanStats.Correlation,
                CleanPercent = cleanCount * 100.0f / RuntimeTerrainMesh.VertexCount,
                GarbageZ = garbageZ,
                VhgtCorner = vhgtHeights[0, 0],
                ExactCorner = exactHeights[0, 0],
                IsBigEndian = land.Header.IsBigEndian
            });
        }

        if (comparisons.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No cells found with both VHGT and runtime terrain data.[/]");
            AnsiConsole.MarkupLine(
                $"  VHGT heightmaps preserved: {data.OriginalVhgtHeightmaps.Count}");
            AnsiConsole.MarkupLine(
                $"  Cells with ExactHeights: {data.ScanResult.LandRecords.Count(l => l.Heightmap?.ExactHeights != null)}");
            return;
        }

        // Render comparison table
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]VHGT vs ExactHeights comparison[/]");
        AnsiConsole.MarkupLine($"  Cells with both sources: {comparisons.Count}");

        var table = new Table();
        table.AddColumn("Cell");
        table.AddColumn("FormID");
        table.AddColumn(new TableColumn("VHGT Offset").RightAligned());
        table.AddColumn(new TableColumn("Exact Offset").RightAligned());
        table.AddColumn(new TableColumn("Offset Delta").RightAligned());
        table.AddColumn(new TableColumn("Mean Error").RightAligned());
        table.AddColumn(new TableColumn("Max Error").RightAligned());
        table.AddColumn(new TableColumn("Correlation").RightAligned());
        table.AddColumn(new TableColumn("Clean Correlation").RightAligned());
        table.AddColumn(new TableColumn("Clean %").RightAligned());
        table.AddColumn(new TableColumn("Garbage Z").RightAligned());
        table.AddColumn("BE");

        foreach (var c in comparisons.OrderBy(c => c.CellX).ThenBy(c => c.CellY))
        {
            var corrColor = c.Correlation > 0.95 ? "green" : c.Correlation > 0.8 ? "yellow" : "red";
            var cleanCorrColor = c.CleanCorrelation > 0.95 ? "green"
                : c.CleanCorrelation > 0.8 ? "yellow" : "red";
            var garbColor = c.GarbageZ == 0 ? "green" : c.GarbageZ < 100 ? "yellow" : "red";
            var offsetDelta = c.VhgtOffset - c.ExactOffset;

            table.AddRow(
                $"{c.CellX},{c.CellY}",
                $"0x{c.FormId:X8}",
                $"{c.VhgtOffset:F2}",
                $"{c.ExactOffset:F2}",
                $"{offsetDelta:F2}",
                $"{c.MeanError:F1}",
                $"{c.MaxError:F1}",
                $"[{corrColor}]{c.Correlation:F4}[/]",
                $"[{cleanCorrColor}]{c.CleanCorrelation:F4}[/]",
                $"{c.CleanPercent:F1}",
                $"[{garbColor}]{c.GarbageZ}[/]",
                c.IsBigEndian ? "Y" : "N");
        }

        AnsiConsole.Write(table);

        // Analysis summary
        var avgCorr = comparisons.Average(c => c.Correlation);
        var avgCleanCorr = comparisons.Average(c => c.CleanCorrelation);
        var highCorr = comparisons.Count(c => c.CleanCorrelation > 0.95);
        AnsiConsole.MarkupLine(
            $"\n  Average correlation: {avgCorr:F4} (full), {avgCleanCorr:F4} (clean)");
        AnsiConsole.MarkupLine(
            $"  {highCorr}/{comparisons.Count} cells with clean correlation > 0.95");

        if (avgCleanCorr > 0.95)
        {
            AnsiConsole.MarkupLine(
                "  [green]Shapes match well[/] — offset differences suggest HeightOffset endianness issue");
        }
        else if (avgCleanCorr > 0.8)
        {
            AnsiConsole.MarkupLine(
                "  [yellow]Shapes partially match[/] — some terrain differences beyond HeightOffset");
        }
        else
        {
            AnsiConsole.MarkupLine(
                "  [red]Shape mismatch[/] — terrain differences go beyond HeightOffset");
        }
    }

    private static CorrelationStats ComputeCorrelation(float[,] vhgtHeights, float[,] exactHeights, bool[]? mask)
    {
        var totalError = 0.0;
        var maxError = 0.0;
        var sumVhgt = 0.0;
        var sumExact = 0.0;
        var count = 0;

        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                var idx = y * 33 + x;
                if (mask != null && mask[idx])
                {
                    continue; // Skip sanitized vertices
                }

                var err = Math.Abs(vhgtHeights[y, x] - exactHeights[y, x]);
                totalError += err;
                if (err > maxError)
                {
                    maxError = err;
                }

                sumVhgt += vhgtHeights[y, x];
                sumExact += exactHeights[y, x];
                count++;
            }
        }

        if (count == 0)
        {
            return new CorrelationStats(0, 0, 0);
        }

        var meanError = (float)(totalError / count);
        var meanVhgt = sumVhgt / count;
        var meanExact = sumExact / count;

        // Pearson correlation
        var sumProduct = 0.0;
        var sumVhgtSq = 0.0;
        var sumExactSq = 0.0;

        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                var idx = y * 33 + x;
                if (mask != null && mask[idx])
                {
                    continue;
                }

                var vDev = vhgtHeights[y, x] - (float)meanVhgt;
                var eDev = exactHeights[y, x] - (float)meanExact;
                sumProduct += vDev * eDev;
                sumVhgtSq += vDev * vDev;
                sumExactSq += eDev * eDev;
            }
        }

        var denom = Math.Sqrt(sumVhgtSq * sumExactSq);
        var correlation = denom > 0 ? (float)(sumProduct / denom) : 0;

        return new CorrelationStats(meanError, (float)maxError, correlation);
    }

    /// <summary>
    ///     Position-aware height comparison: maps each runtime mesh vertex to its correct
    ///     position in the 33×33 VHGT grid using the vertex XY coordinates, then computes
    ///     Pearson correlation only at matching positions.
    /// </summary>
    private static void DiagnoseHeightComparison(
        uint formId, int cellX, int cellY,
        float[,] vhgtHeights, float[,] exactHeights, bool[]? mask, float[] vertices)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[blue]Position-aware comparison for cell ({cellX},{cellY}) FormID 0x{formId:X8}[/]");

        // Map each valid mesh vertex to its actual VHGT grid position using XY coordinates.
        // Cell-local coords: X,Y in [-2048, 2048], spacing = 128, grid = 33×33.
        // Grid col = round((X + 2048) / 128), row = round((Y + 2048) / 128)
        var meshHeightAtGrid = new float?[33, 33]; // null = no mesh data at this position
        var mappedCount = 0;
        var unmappedCount = 0;
        var outOfRangeCount = 0;

        // Detect row length by finding the first Y-step
        var rowLength = 0;
        for (var i = 1; i < RuntimeTerrainMesh.VertexCount; i++)
        {
            var prevY = vertices[(i - 1) * 3 + 1];
            var currY = vertices[i * 3 + 1];
            if (MathF.Abs(currY - prevY) > 64f) // Y step > half spacing → new row
            {
                rowLength = i;
                break;
            }
        }

        AnsiConsole.WriteLine($"  Detected row length: {rowLength} vertices");
        AnsiConsole.WriteLine($"  Expected grid: {rowLength}x{(rowLength > 0 ? RuntimeTerrainMesh.VertexCount / rowLength : 0)}");

        for (var i = 0; i < RuntimeTerrainMesh.VertexCount; i++)
        {
            if (mask != null && mask[i])
            {
                unmappedCount++;
                continue;
            }

            var vx = vertices[i * 3];
            var vy = vertices[i * 3 + 1];
            var vz = vertices[i * 3 + 2];

            // Convert to grid position
            var gridCol = (int)MathF.Round((vx + 2048f) / 128f);
            var gridRow = (int)MathF.Round((vy + 2048f) / 128f);

            if (gridRow < 0 || gridRow >= 33 || gridCol < 0 || gridCol >= 33)
            {
                outOfRangeCount++;
                continue;
            }

            meshHeightAtGrid[gridRow, gridCol] = vz;
            mappedCount++;
        }

        AnsiConsole.WriteLine(
            $"  Mapped: {mappedCount}  Unmapped (garbage): {unmappedCount}  Out of range: {outOfRangeCount}");

        // Compute position-aware correlation between VHGT and mesh heights
        var vhgtValues = new List<float>();
        var meshValues = new List<float>();
        var posDiffs = new List<float>();

        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                if (meshHeightAtGrid[y, x].HasValue)
                {
                    vhgtValues.Add(vhgtHeights[y, x]);
                    meshValues.Add(meshHeightAtGrid[y, x].Value);
                    posDiffs.Add(vhgtHeights[y, x] - meshHeightAtGrid[y, x].Value);
                }
            }
        }

        if (posDiffs.Count > 0)
        {
            var diffMin = posDiffs.Min();
            var diffMax = posDiffs.Max();
            var diffMean = posDiffs.Average();
            var diffStdDev = MathF.Sqrt(posDiffs.Sum(d => (d - diffMean) * (d - diffMean)) / posDiffs.Count);
            AnsiConsole.WriteLine($"  Position-aware diff ({posDiffs.Count} matched):");
            AnsiConsole.WriteLine(
                $"    Min={diffMin:F2}  Max={diffMax:F2}  Mean={diffMean:F2}  StdDev={diffStdDev:F2}");
        }

        // Compute Pearson correlation on position-matched pairs
        if (vhgtValues.Count >= 3)
        {
            var meanV = vhgtValues.Average();
            var meanM = meshValues.Average();
            var sumProd = 0.0;
            var sumVSq = 0.0;
            var sumMSq = 0.0;

            for (var i = 0; i < vhgtValues.Count; i++)
            {
                var vDev = vhgtValues[i] - meanV;
                var mDev = meshValues[i] - meanM;
                sumProd += vDev * mDev;
                sumVSq += vDev * vDev;
                sumMSq += mDev * mDev;
            }

            var denom = Math.Sqrt(sumVSq * sumMSq);
            var posCorr = denom > 0 ? (float)(sumProd / denom) : 0;
            AnsiConsole.MarkupLine(
                $"  [green]Position-aware correlation: {posCorr:F4}[/] ({vhgtValues.Count} matched vertices)");
        }
        else
        {
            AnsiConsole.MarkupLine("  [yellow]Too few matched vertices for correlation.[/]");
        }

        // Show a few matched samples
        AnsiConsole.WriteLine("  Sample matched heights:");
        var shown = 0;
        for (var y = 0; y < 33 && shown < 8; y += 4)
        {
            for (var x = 0; x < 33 && shown < 8; x += 4)
            {
                if (meshHeightAtGrid[y, x].HasValue)
                {
                    var diff = vhgtHeights[y, x] - meshHeightAtGrid[y, x].Value;
                    AnsiConsole.WriteLine(
                        $"    grid[{y,2},{x,2}] VHGT={vhgtHeights[y, x],10:F2}  Mesh={meshHeightAtGrid[y, x].Value,10:F2}  Diff={diff:F2}");
                    shown++;
                }
            }
        }
    }

    private static void VerifyGridOrdering(DumpData data)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Vertex grid ordering verification[/]");

        var cellsWithMesh = data.ScanResult.LandRecords
            .Where(l => l.RuntimeTerrainMesh != null && l.BestCellX.HasValue)
            .Take(3)
            .ToList();

        if (cellsWithMesh.Count == 0)
        {
            AnsiConsole.MarkupLine("  [yellow]No cells with runtime terrain meshes.[/]");
            return;
        }

        foreach (var land in cellsWithMesh)
        {
            var mesh = land.RuntimeTerrainMesh!;
            var v = mesh.Vertices;

            AnsiConsole.MarkupLine(
                $"  Cell ({land.BestCellX},{land.BestCellY}) FormID 0x{land.Header.FormId:X8}:");

            // Check first row (vertices 0..32): should have constant Y, increasing X
            var row0Y = new float[33];
            var row0X = new float[33];
            for (var x = 0; x < 33; x++)
            {
                row0X[x] = v[x * 3];
                row0Y[x] = v[x * 3 + 1];
            }

            var xIncreasing = true;
            var yConstant = true;
            for (var x = 1; x < 33; x++)
            {
                if (row0X[x] <= row0X[x - 1])
                {
                    xIncreasing = false;
                }

                if (Math.Abs(row0Y[x] - row0Y[0]) > 1.0f)
                {
                    yConstant = false;
                }
            }

            AnsiConsole.WriteLine(
                $"    Row 0: X range [{row0X[0]:F1}..{row0X[32]:F1}], " +
                $"Y range [{row0Y[0]:F1}..{row0Y[32]:F1}]");
            AnsiConsole.MarkupLine(
                $"    Row 0: X increasing = {(xIncreasing ? "[green]YES[/]" : "[red]NO[/]")}, " +
                $"Y constant = {(yConstant ? "[green]YES[/]" : "[red]NO[/]")}");

            // Check row 1 vs row 0: Y should differ by ~128 (4096/32)
            var row1Y = v[33 * 3 + 1];
            var yStep = row1Y - row0Y[0];
            AnsiConsole.WriteLine(
                $"    Row 0 Y = {row0Y[0]:F1}, Row 1 Y = {row1Y:F1}, " +
                $"Y step = {yStep:F1} (expected ~128)");

            // Check overall X and Y direction
            var lastRowY = v[(32 * 33) * 3 + 1];
            AnsiConsole.WriteLine(
                $"    First vertex Y = {row0Y[0]:F1}, Last row Y = {lastRowY:F1} " +
                $"→ Y direction: {(lastRowY > row0Y[0] ? "south→north" : "north→south")}");
        }
    }

    private readonly record struct CorrelationStats(float MeanError, float MaxError, float Correlation);

    private sealed record CellComparison
    {
        public uint FormId { get; init; }
        public int CellX { get; init; }
        public int CellY { get; init; }
        public float VhgtOffset { get; init; }
        public float ExactOffset { get; init; }
        public float MeanError { get; init; }
        public float MaxError { get; init; }
        public float Correlation { get; init; }
        public float CleanCorrelation { get; init; }
        public float CleanPercent { get; init; }
        public int GarbageZ { get; init; }
        public float VhgtCorner { get; init; }
        public float ExactCorner { get; init; }
        public bool IsBigEndian { get; init; }
    }
}
