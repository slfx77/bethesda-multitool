using System.CommandLine;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace TerrainAnalyzer.Commands;

internal static class ScanCommands
{
    internal static Command CreateScanCommand()
    {
        var command = new Command("scan",
            "Scan all DMP files in a directory for VHGT + terrain mesh overlap");

        var dirArg = new Argument<string>("directory") { Description = "Directory containing .dmp files" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };

        command.Arguments.Add(dirArg);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var dir = parseResult.GetValue(dirArg)!;
            var verbose = parseResult.GetValue(verboseOpt);
            await ExecuteAsync(dir, verbose);
        });

        return command;
    }

    private static async Task ExecuteAsync(string directory, bool verbose)
    {
        if (!Directory.Exists(directory))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {directory}");
            return;
        }

        var dmpFiles = Directory.GetFiles(directory, "*.dmp")
            .OrderBy(f => f)
            .ToArray();

        if (dmpFiles.Length == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No .dmp files found in:[/] {directory}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Scanning {dmpFiles.Length} DMP files for terrain overlap...[/]");
        AnsiConsole.WriteLine();

        var results = new List<DumpOverlapResult>();

        foreach (var dmpFile in dmpFiles)
        {
            var filename = Path.GetFileName(dmpFile);

            try
            {
                using var data = await DumpLoader.LoadAsync(dmpFile, preserveVhgt: true, verbose: verbose);

                var vhgtOnly = 0;
                var meshOnly = 0;
                var both = 0;
                var totalGarbageZ = 0;
                var minGarbageZ = int.MaxValue;
                var maxGarbageZ = 0;

                var cellDetails = new List<CellDetail>();

                foreach (var land in data.ScanResult.LandRecords)
                {
                    var hasVhgt = data.OriginalVhgtHeightmaps.ContainsKey(land.Header.FormId);
                    var hasMesh = land.RuntimeTerrainMesh != null;

                    if (hasVhgt && hasMesh)
                    {
                        both++;
                        var garbZ = land.RuntimeTerrainMesh!.SanitizedZCount;
                        totalGarbageZ += garbZ;
                        minGarbageZ = Math.Min(minGarbageZ, garbZ);
                        maxGarbageZ = Math.Max(maxGarbageZ, garbZ);

                        var lod = land.RuntimeTerrainMesh.DetectLodLevel();
                        cellDetails.Add(new CellDetail
                        {
                            FormId = land.Header.FormId,
                            CellX = land.BestCellX ?? 0,
                            CellY = land.BestCellY ?? 0,
                            GarbageZ = garbZ,
                            LodLevel = lod.Level,
                            VerticesPerRow = lod.VerticesPerRow,
                            Spacing = lod.Spacing,
                            HasVhgt = true
                        });
                    }
                    else if (hasVhgt)
                    {
                        vhgtOnly++;
                    }
                    else if (hasMesh)
                    {
                        meshOnly++;

                        var lod = land.RuntimeTerrainMesh!.DetectLodLevel();
                        cellDetails.Add(new CellDetail
                        {
                            FormId = land.Header.FormId,
                            CellX = land.BestCellX ?? 0,
                            CellY = land.BestCellY ?? 0,
                            GarbageZ = land.RuntimeTerrainMesh.SanitizedZCount,
                            LodLevel = lod.Level,
                            VerticesPerRow = lod.VerticesPerRow,
                            Spacing = lod.Spacing,
                            HasVhgt = false
                        });
                    }
                }

                if (minGarbageZ == int.MaxValue)
                {
                    minGarbageZ = 0;
                }

                var lodCounts = cellDetails
                    .GroupBy(c => c.LodLevel)
                    .ToDictionary(g => g.Key, g => g.Count());

                results.Add(new DumpOverlapResult
                {
                    Filename = filename,
                    TotalLand = data.ScanResult.LandRecords.Count,
                    VhgtOnly = vhgtOnly,
                    MeshOnly = meshOnly,
                    Both = both,
                    MinGarbageZ = minGarbageZ,
                    MaxGarbageZ = maxGarbageZ,
                    AvgGarbageZ = both > 0 ? totalGarbageZ / (float)both : 0,
                    LodCounts = lodCounts,
                    CellDetails = cellDetails
                });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]Error processing {Markup.Escape(filename)}:[/] {Markup.Escape(ex.Message)}");
            }
        }

        // Render summary table sorted by overlap count descending
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Overlap Summary[/]");

        var table = new Table();
        table.AddColumn("Dump File");
        table.AddColumn(new TableColumn("Total LAND").RightAligned());
        table.AddColumn(new TableColumn("VHGT Only").RightAligned());
        table.AddColumn(new TableColumn("Mesh Only").RightAligned());
        table.AddColumn(new TableColumn("Both").RightAligned());
        table.AddColumn(new TableColumn("LOD 0").RightAligned());
        table.AddColumn(new TableColumn("LOD 1").RightAligned());
        table.AddColumn(new TableColumn("LOD 2+").RightAligned());
        table.AddColumn(new TableColumn("Avg Garbage Z").RightAligned());

        foreach (var r in results.OrderByDescending(r => r.Both).ThenByDescending(r => r.Lod0Count).ThenBy(r => r.AvgGarbageZ))
        {
            if (r.TotalLand == 0)
            {
                continue;
            }

            var bothColor = r.Both > 0 ? "green" : "grey";
            var lod0Color = r.Lod0Count > 0 ? "green" : "grey";
            var garbColor = r.AvgGarbageZ < 100 ? "green" : r.AvgGarbageZ < 300 ? "yellow" : "red";

            var meshCount = r.Both + r.MeshOnly;
            table.AddRow(
                Markup.Escape(r.Filename),
                $"{r.TotalLand}",
                $"{r.VhgtOnly}",
                $"{r.MeshOnly}",
                $"[{bothColor}]{r.Both}[/]",
                meshCount > 0 ? $"[{lod0Color}]{r.Lod0Count}[/]" : "-",
                meshCount > 0 ? $"{r.Lod1Count}" : "-",
                meshCount > 0 ? $"{r.Lod2PlusCount}" : "-",
                r.Both > 0 ? $"[{garbColor}]{r.AvgGarbageZ:F0}[/]" : "-");
        }

        AnsiConsole.Write(table);

        // LOD 0 summary
        var totalLod0 = results.Sum(r => r.Lod0Count);
        AnsiConsole.WriteLine();
        if (totalLod0 > 0)
        {
            AnsiConsole.MarkupLine($"[green]Found {totalLod0} LOD 0 cells across all dumps![/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No LOD 0 (33x33) terrain meshes found. All meshes are LOD 1+ (17x17 or smaller).[/]");
        }

        // Best candidate
        var best = results
            .Where(r => r.Both > 0)
            .OrderByDescending(r => r.Lod0Count)
            .ThenByDescending(r => r.Both)
            .ThenBy(r => r.AvgGarbageZ)
            .FirstOrDefault();

        if (best != null)
        {
            AnsiConsole.MarkupLine(
                $"[green]Best candidate:[/] {Markup.Escape(best.Filename)} " +
                $"({best.Both} overlapping, {best.Lod0Count} LOD 0, avg garbage Z: {best.AvgGarbageZ:F0})");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No dumps found with both VHGT and terrain mesh data.[/]");
        }

        // Per-cell detail table for dumps with mesh data
        var dumpsWithCells = results.Where(r => r.CellDetails.Count > 0).ToList();
        if (dumpsWithCells.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Per-Cell Detail (all mesh cells)[/]");

            var detailTable = new Table();
            detailTable.AddColumn("Dump File");
            detailTable.AddColumn("FormID");
            detailTable.AddColumn(new TableColumn("Cell").RightAligned());
            detailTable.AddColumn(new TableColumn("LOD").RightAligned());
            detailTable.AddColumn(new TableColumn("Vertices/Row").RightAligned());
            detailTable.AddColumn(new TableColumn("Spacing").RightAligned());
            detailTable.AddColumn(new TableColumn("Garbage Z").RightAligned());
            detailTable.AddColumn("VHGT");

            foreach (var r in dumpsWithCells.OrderByDescending(r => r.Lod0Count))
            {
                foreach (var cell in r.CellDetails.OrderBy(c => c.LodLevel))
                {
                    var lodColor = cell.LodLevel == 0 ? "green" : cell.LodLevel == 1 ? "yellow" : cell.LodLevel < 0 ? "grey" : "red";
                    var lodText = cell.LodLevel >= 0 ? $"{cell.LodLevel}" : "?";
                    detailTable.AddRow(
                        Markup.Escape(r.Filename),
                        $"0x{cell.FormId:X8}",
                        $"({cell.CellX}, {cell.CellY})",
                        $"[{lodColor}]{lodText}[/]",
                        $"{cell.VerticesPerRow}",
                        $"{cell.Spacing:F0}",
                        $"{cell.GarbageZ}",
                        cell.HasVhgt ? "[green]Yes[/]" : "[grey]No[/]");
                }
            }

            AnsiConsole.Write(detailTable);
        }
    }

    private sealed record DumpOverlapResult
    {
        public required string Filename { get; init; }
        public int TotalLand { get; init; }
        public int VhgtOnly { get; init; }
        public int MeshOnly { get; init; }
        public int Both { get; init; }
        public int MinGarbageZ { get; init; }
        public int MaxGarbageZ { get; init; }
        public float AvgGarbageZ { get; init; }
        public Dictionary<int, int> LodCounts { get; init; } = [];
        public List<CellDetail> CellDetails { get; init; } = [];

        public int Lod0Count => LodCounts.GetValueOrDefault(0);
        public int Lod1Count => LodCounts.GetValueOrDefault(1);
        public int Lod2PlusCount => LodCounts.Where(kv => kv.Key >= 2).Sum(kv => kv.Value);
    }

    private sealed record CellDetail
    {
        public uint FormId { get; init; }
        public int CellX { get; init; }
        public int CellY { get; init; }
        public int GarbageZ { get; init; }
        public int LodLevel { get; init; }
        public int VerticesPerRow { get; init; }
        public float Spacing { get; init; }
        public bool HasVhgt { get; init; }
    }
}
