using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using Spectre.Console;
using System.Text.Json;
using FalloutXbox360Utils.Core.Utils;

namespace EsmAnalyzer.Commands;

public static partial class ExportCommands
{
    private static int GenerateWorldmap(string filePath, string? worldspaceName, string outputDir, int scale,
        bool rawOutput, bool exportAll, bool analyzeOnly)
    {
        _ = Directory.CreateDirectory(outputDir);

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null)
        {
            return 1;
        }

        var bigEndian = esm.IsBigEndian;
        var sourceType = bigEndian ? "Xbox 360" : "PC";

        AnsiConsole.MarkupLine($"Source file: [cyan]{Path.GetFileName(filePath)}[/]");
        AnsiConsole.MarkupLine(
            $"Endianness: {(bigEndian ? "[yellow]Big-endian (Xbox 360)[/]" : "[green]Little-endian (PC)[/]")}");

        var outputMode = rawOutput ? "[cyan]16-bit grayscale PNG[/]" : "[green]Color gradient PNG[/]";
        AnsiConsole.MarkupLine($"Output mode: {outputMode}");
        AnsiConsole.WriteLine();

        if (exportAll)
        {
            var worldspaces = WrldGrupScanner.FindAllWorldspaces(esm.Data, bigEndian);
            AnsiConsole.MarkupLine($"Found [cyan]{worldspaces.Count}[/] worldspaces in file");
            AnsiConsole.WriteLine();

            var successCount = 0;
            foreach (var (wrldName, wrldFormId) in worldspaces)
            {
                AnsiConsole.MarkupLine("[blue]----------------------------------------[/]");
                var result = GenerateSingleWorldmap(esm.Data, bigEndian, wrldName, wrldFormId, outputDir, scale,
                    rawOutput, sourceType, analyzeOnly);
                if (result == 0)
                {
                    successCount++;
                }

                AnsiConsole.WriteLine();
            }

            AnsiConsole.MarkupLine($"[green]Exported {successCount}/{worldspaces.Count} worldspaces[/]");
            return successCount > 0 ? 0 : 1;
        }

        // Single worldspace mode
        uint targetWorldspaceFormId;
        if (string.IsNullOrEmpty(worldspaceName))
        {
            targetWorldspaceFormId = FalloutWorldspaces.KnownWorldspaces["WastelandNV"];
            worldspaceName = "WastelandNV";
        }
        else if (FalloutWorldspaces.KnownWorldspaces.TryGetValue(worldspaceName, out var knownId))
        {
            targetWorldspaceFormId = knownId;
        }
        else
        {
            var parsed = EsmFileLoader.ParseFormId(worldspaceName);
            if (parsed == null)
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] Unknown worldspace '{worldspaceName}'");
                AnsiConsole.MarkupLine("[yellow]Known worldspaces:[/]");
                foreach (var (name, formId) in FalloutWorldspaces.KnownWorldspaces.DistinctBy(kvp => kvp.Value))
                {
                    AnsiConsole.MarkupLine($"  {name}: 0x{formId:X8}");
                }

                return 1;
            }

            targetWorldspaceFormId = parsed.Value;
        }

        return GenerateSingleWorldmap(esm.Data, bigEndian, worldspaceName, targetWorldspaceFormId, outputDir, scale,
            rawOutput, sourceType, analyzeOnly);
    }

    /// <summary>
    ///     Generate heightmap for a single worldspace.
    /// </summary>
    private static int GenerateSingleWorldmap(byte[] data, bool bigEndian, string worldspaceName,
        uint targetWorldspaceFormId,
        string outputDir, int scale, bool rawOutput, string sourceType, bool analyzeOnly)
    {
        AnsiConsole.MarkupLine($"[blue]Generating worldmap for:[/] {worldspaceName} (0x{targetWorldspaceFormId:X8})");

        // Step 1: Find CELL and LAND records that belong to the target worldspace
        Dictionary<(int x, int y), CellInfo> cellMap = [];
        List<AnalyzerRecordInfo> cellRecords = [];
        List<AnalyzerRecordInfo> landRecords = [];
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning for CELL and LAND records in target worldspace...", ctx =>
            {
                _ = ctx.Status("Finding WRLD record and scanning child GRUPs...");
                var (worldCells, worldLands) = WrldGrupScanner.ScanWorldspaceCellsAndLands(data, bigEndian, targetWorldspaceFormId);
                cellRecords = worldCells;
                landRecords = worldLands;
            });

        AnsiConsole.MarkupLine($"Found [cyan]{cellRecords.Count}[/] CELL records in {worldspaceName}");
        AnsiConsole.MarkupLine($"Found [cyan]{landRecords.Count}[/] LAND records in {worldspaceName}");

        // Step 2: Extract cell grid positions from exterior cells
        var cellParseErrors = 0;
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Extracting cell grid positions...", ctx =>
            {
                foreach (var cell in cellRecords)
                {
                    try
                    {
                        var recordData = EsmHelpers.GetRecordData(data, cell, bigEndian);
                        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

                        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
                        if (xclc != null && xclc.Data.Length >= 8)
                        {
                            var gridX = bigEndian
                                ? (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan())
                                : (int)BinaryUtils.ReadUInt32LE(xclc.Data.AsSpan());
                            var gridY = bigEndian
                                ? (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan(), 4)
                                : (int)BinaryUtils.ReadUInt32LE(xclc.Data.AsSpan(), 4);

                            if (gridX > 0x7FFFFFFF) { gridX = (int)(gridX - 0x100000000); }
                            if (gridY > 0x7FFFFFFF) { gridY = (int)(gridY - 0x100000000); }

                            cellMap[(gridX, gridY)] = new CellInfo
                            {
                                FormId = cell.FormId,
                                GridX = gridX,
                                GridY = gridY,
                                CellRecord = cell
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        cellParseErrors++;
                        if (cellParseErrors <= 5)
                        {
                            AnsiConsole.MarkupLine(
                                $"[yellow]WARN:[/] Failed to parse CELL 0x{cell.FormId:X8}: {ex.Message}");
                        }
                    }
                }
            });

        AnsiConsole.MarkupLine(
            $"Found [cyan]{cellMap.Count}[/] exterior cells with grid positions in {worldspaceName}");
        if (cellParseErrors > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]WARN:[/] {cellParseErrors} CELL records failed to parse.");
        }

        if (cellMap.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No exterior cells found");
            return 1;
        }

        // Build cell position -> heightmap data
        Dictionary<(int x, int y), float[,]> heightmaps = [];

        var minX = cellMap.Keys.Min(k => k.x);
        var maxX = cellMap.Keys.Max(k => k.x);
        var minY = cellMap.Keys.Min(k => k.y);
        var maxY = cellMap.Keys.Max(k => k.y);

        AnsiConsole.MarkupLine($"Cell grid range: X=[[{minX}, {maxX}]], Y=[[{minY}, {maxY}]]");

        // Step 3: Match LAND records to CELLs
        var sortedCells = cellMap.Values.OrderBy(c => c.CellRecord.Offset).ToList();
        var sortedLands = landRecords.OrderBy(r => r.Offset).ToList();
        var allCellOffsets = cellRecords.Select(c => c.Offset).OrderBy(o => o).ToList();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Matching LAND records to CELLs...", ctx =>
            {
                var noLandAfter = 0;
                var landBelongsToLaterCell = 0;
                var noVhgt = 0;
                var parseErrors = 0;
                var matched = 0;
                var decompressionErrors = 0;

                foreach (var cell in sortedCells)
                {
                    var foundLand = false;
                    while (!foundLand)
                    {
                        var landAfterCell = sortedLands
                            .FirstOrDefault(l => l.Offset > cell.CellRecord.Offset);
                        if (landAfterCell == null) { noLandAfter++; break; }

                        var nextCellOffset = allCellOffsets.FirstOrDefault(o => o > cell.CellRecord.Offset);
                        if (nextCellOffset != default && landAfterCell.Offset > nextCellOffset)
                        {
                            landBelongsToLaterCell++;
                            break;
                        }

                        try
                        {
                            var recordData = EsmHelpers.GetRecordData(data, landAfterCell, bigEndian);
                            var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

                            var vhgt = subrecords.FirstOrDefault(s => s.Signature == "VHGT");
                            if (vhgt != null && vhgt.Data.Length >= 4 + (CellGridSize * CellGridSize))
                            {
                                var (heights, _) = ParseHeightmap(vhgt.Data, bigEndian);
                                if (!heightmaps.ContainsKey((cell.GridX, cell.GridY)))
                                {
                                    heightmaps[(cell.GridX, cell.GridY)] = heights;
                                    matched++;
                                }
                            }
                            else
                            {
                                noVhgt++;
                            }

                            _ = sortedLands.Remove(landAfterCell);
                            foundLand = true;
                        }
                        catch (InvalidDataException ex) when (ex.Message.Contains("Decompression"))
                        {
                            _ = sortedLands.Remove(landAfterCell);
                            decompressionErrors++;
                            if (decompressionErrors <= 5)
                            {
                                AnsiConsole.MarkupLine(
                                    $"[yellow]WARN:[/] Decompression failed for LAND 0x{landAfterCell.FormId:X8}: {ex.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            parseErrors++;
                            if (parseErrors <= 5)
                            {
                                AnsiConsole.MarkupLine(
                                    $"[red]Error parsing cell ({cell.GridX},{cell.GridY}): {ex.Message}[/]");
                            }
                            _ = sortedLands.Remove(landAfterCell);
                            break;
                        }
                    }
                }

                AnsiConsole.MarkupLine(
                    $"[grey]Matching stats: matched={matched}, noLandAfter={noLandAfter}, landBelongsToLater={landBelongsToLaterCell}, noVhgt={noVhgt}, decompressionSkipped={decompressionErrors}, errors={parseErrors}[/]");
            });

        AnsiConsole.MarkupLine($"Extracted [cyan]{heightmaps.Count}[/] heightmaps");

        // Report missing cells
        var missingCells = cellMap.Keys.Except(heightmaps.Keys).ToList();
        if (missingCells.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Missing {missingCells.Count} cells (exterior cells without LAND data)[/]");
            var edgeMissing = missingCells.Count(c =>
                c.x == cellMap.Keys.Min(k => k.x) || c.x == cellMap.Keys.Max(k => k.x) ||
                c.y == cellMap.Keys.Min(k => k.y) || c.y == cellMap.Keys.Max(k => k.y));
            AnsiConsole.MarkupLine(
                $"[grey]  Edge cells missing: {edgeMissing} (cells at world boundary often have no terrain)[/]");
            var missingCellsFile = Path.Combine(outputDir, "missing_cells.txt");
            File.WriteAllLines(missingCellsFile,
                missingCells.OrderBy(c => c.y).ThenBy(c => c.x).Select(c => $"{c.x},{c.y}"));
            AnsiConsole.MarkupLine($"[grey]  Missing cell coordinates saved to {missingCellsFile}[/]");
        }

        if (heightmaps.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No heightmaps could be extracted");
            return 1;
        }

        // Recalculate bounds based on actual heightmaps
        minX = heightmaps.Keys.Min(k => k.x);
        maxX = heightmaps.Keys.Max(k => k.x);
        minY = heightmaps.Keys.Min(k => k.y);
        maxY = heightmaps.Keys.Max(k => k.y);

        var cellsWide = maxX - minX + 1;
        var cellsHigh = maxY - minY + 1;

        if (analyzeOnly)
        {
            WorldmapHeightmapGenerator.AnalyzeHeightDistribution(heightmaps, minX, maxX, minY, maxY, worldspaceName, outputDir);
            return 0;
        }

        var imageWidth = cellsWide * CellGridSize * scale;
        var imageHeight = cellsHigh * CellGridSize * scale;

        AnsiConsole.MarkupLine(
            $"Output dimensions: [cyan]{imageWidth}x{imageHeight}[/] pixels ({cellsWide}x{cellsHigh} cells)");

        // Step 4: Stitch heightmaps together and render
        float savedGlobalMin = 0, savedGlobalMax = 0;
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Stitching heightmaps...", ctx =>
            {
                var globalMin = float.MaxValue;
                var globalMax = float.MinValue;

                foreach (var (_, heights) in heightmaps)
                {
                    for (var y = 0; y < CellGridSize; y++)
                    {
                        for (var x = 0; x < CellGridSize; x++)
                        {
                            globalMin = Math.Min(globalMin, heights[x, y]);
                            globalMax = Math.Max(globalMax, heights[x, y]);
                        }
                    }
                }

                var range = globalMax - globalMin;
                if (range < 0.001f) { range = 1f; }

                savedGlobalMin = globalMin;
                savedGlobalMax = globalMax;

                if (rawOutput)
                {
                    WorldmapHeightmapGenerator.RenderRawHeightmap(heightmaps, imageWidth, imageHeight, scale,
                        minX, maxY, globalMin, range, worldspaceName, sourceType, outputDir);
                }
                else
                {
                    WorldmapHeightmapGenerator.RenderColorHeightmap(heightmaps, imageWidth, imageHeight, scale,
                        minX, maxY, globalMin, range, worldspaceName, sourceType, outputDir);
                }

                // Export metadata
                var metadata = new WorldmapMetadata
                {
                    Worldspace = worldspaceName,
                    FormId = $"0x{targetWorldspaceFormId:X8}",
                    CellsExtracted = heightmaps.Count,
                    CellsTotal = cellMap.Count,
                    GridBounds = new GridBounds { MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY },
                    ImageWidth = imageWidth,
                    ImageHeight = imageHeight,
                    Scale = scale,
                    HeightRange = new HeightRange { Min = globalMin, Max = globalMax },
                    IsBigEndian = bigEndian,
                    SourceType = sourceType,
                    IsRaw16Bit = rawOutput
                };

                var jsonPath = Path.Combine(outputDir, $"{worldspaceName}_{sourceType}_metadata.json");
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(metadata, s_jsonOptions));
                AnsiConsole.MarkupLine($"Saved metadata: [cyan]{jsonPath}[/]");
            });

        // Summary
        AnsiConsole.WriteLine();
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        _ = summaryTable.AddRow("Worldspace", worldspaceName);
        _ = summaryTable.AddRow("Cells Extracted", $"[green]{heightmaps.Count}[/]");
        _ = summaryTable.AddRow("Image Size", $"{imageWidth}x{imageHeight} px");
        _ = summaryTable.AddRow("Grid Range", $"X=[[{minX}, {maxX}]], Y=[[{minY}, {maxY}]]");
        _ = summaryTable.AddRow("Output Directory", outputDir);

        AnsiConsole.Write(summaryTable);

        return 0;
    }

    private sealed class CellInfo
    {
        public uint FormId { get; init; }
        public int GridX { get; init; }
        public int GridY { get; init; }
        public required AnalyzerRecordInfo CellRecord { get; init; }
    }
}
