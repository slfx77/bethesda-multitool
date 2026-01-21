using System.Text;
using System.Text.Json;
using EsmAnalyzer.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Commands;

public static partial class ExportCommands
{
    private static int GenerateWorldmap(string filePath, string? worldspaceName, string outputDir, int scale,
        bool rawOutput)
    {
        // Resolve worldspace FormID
        uint targetWorldspaceFormId;
        if (string.IsNullOrEmpty(worldspaceName))
        {
            targetWorldspaceFormId = KnownWorldspaces["WastelandNV"];
            worldspaceName = "WastelandNV";
        }
        else if (KnownWorldspaces.TryGetValue(worldspaceName, out var knownId))
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
                foreach (var (name, formId) in KnownWorldspaces.DistinctBy(kvp => kvp.Value))
                    AnsiConsole.MarkupLine($"  {name}: 0x{formId:X8}");
                return 1;
            }

            targetWorldspaceFormId = parsed.Value;
        }

        Directory.CreateDirectory(outputDir);

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

        var bigEndian = esm.IsBigEndian;

        AnsiConsole.MarkupLine($"[blue]Generating worldmap for:[/] {worldspaceName} (0x{targetWorldspaceFormId:X8})");
        AnsiConsole.MarkupLine($"Source file: [cyan]{Path.GetFileName(filePath)}[/]");
        AnsiConsole.MarkupLine(
            $"Endianness: {(bigEndian ? "[yellow]Big-endian (Xbox 360)[/]" : "[green]Little-endian (PC)[/]")}");
        AnsiConsole.WriteLine();

        // Step 1: Find all CELL records using byte scan for reliable Xbox 360 detection
        Dictionary<(int x, int y), CellInfo> cellMap = [];
        List<AnalyzerRecordInfo> cellRecords = [];
        List<AnalyzerRecordInfo> landRecords = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning for CELL and LAND records...", ctx =>
            {
                ctx.Status("Scanning for CELL records...");
                cellRecords = EsmHelpers.ScanForRecordType(esm.Data, bigEndian, "CELL");

                ctx.Status("Scanning for LAND records...");
                landRecords = EsmHelpers.ScanForRecordType(esm.Data, bigEndian, "LAND");
            });

        AnsiConsole.MarkupLine($"Found [cyan]{cellRecords.Count}[/] CELL records");
        AnsiConsole.MarkupLine($"Found [cyan]{landRecords.Count}[/] LAND records");

        // Step 2: Extract cell grid positions from exterior cells
        var cellParseErrors = 0;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Extracting cell grid positions...", ctx =>
            {
                foreach (var cell in cellRecords)
                    try
                    {
                        var recordData = EsmHelpers.GetRecordData(esm.Data, cell, bigEndian);
                        var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);

                        // Look for XCLC subrecord (grid position - only exists for exterior cells)
                        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
                        if (xclc != null && xclc.Data.Length >= 8)
                        {
                            var gridX = bigEndian
                                ? (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan())
                                : (int)BinaryUtils.ReadUInt32LE(xclc.Data.AsSpan());
                            var gridY = bigEndian
                                ? (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan(), 4)
                                : (int)BinaryUtils.ReadUInt32LE(xclc.Data.AsSpan(), 4);

                            // Handle signed 32-bit values
                            if (gridX > 0x7FFFFFFF) gridX = (int)(gridX - 0x100000000);
                            if (gridY > 0x7FFFFFFF) gridY = (int)(gridY - 0x100000000);

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
                            AnsiConsole.MarkupLine(
                                $"[yellow]WARN:[/] Failed to parse CELL 0x{cell.FormId:X8}: {ex.Message}");
                    }
            });

        AnsiConsole.MarkupLine($"Found [cyan]{cellMap.Count}[/] exterior cells with grid positions");
        if (cellParseErrors > 0)
            AnsiConsole.MarkupLine($"[yellow]WARN:[/] {cellParseErrors} CELL records failed to parse.");

        if (cellMap.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No exterior cells found");
            return 1;
        }

        // Build FormID -> LAND record map
        var landByFormId = landRecords.ToDictionary(r => r.FormId, r => r);

        // Build cell position -> heightmap data
        Dictionary<(int x, int y), float[,]> heightmaps = [];

        var minX = cellMap.Keys.Min(k => k.x);
        var maxX = cellMap.Keys.Max(k => k.x);
        var minY = cellMap.Keys.Min(k => k.y);
        var maxY = cellMap.Keys.Max(k => k.y);

        AnsiConsole.MarkupLine($"Cell grid range: X=[[{minX}, {maxX}]], Y=[[{minY}, {maxY}]]");

        // Step 4: Match LAND records to CELLs
        // Strategy: For each exterior CELL with a grid position, find the nearest LAND record
        // that follows it in the file (LAND records come after CELL in GRUP hierarchy)

        // Build a sorted list of CELL records by offset for matching
        var sortedCells = cellMap.Values.OrderBy(c => c.CellRecord.Offset).ToList();
        var sortedLands = landRecords.OrderBy(r => r.Offset).ToList();

        // Build a sorted list of ALL cell offsets (including interior) for proximity checks
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

                // For each exterior CELL, find the LAND record that follows it closest
                foreach (var cell in sortedCells)
                {
                    var foundLand = false;

                    // Try to find a matching LAND - may need to skip ones that fail decompression
                    while (!foundLand)
                    {
                        // Find the first LAND record that comes after this CELL
                        var landAfterCell = sortedLands
                            .FirstOrDefault(l => l.Offset > cell.CellRecord.Offset);

                        if (landAfterCell == null)
                        {
                            noLandAfter++;
                            break;
                        }

                        // Check if this LAND is closer to this CELL than to the next CELL (any cell, not just exterior)
                        // This ensures we don't match an interior cell's LAND to an exterior cell
                        var nextCellOffset = allCellOffsets.FirstOrDefault(o => o > cell.CellRecord.Offset);
                        if (nextCellOffset != default && landAfterCell.Offset > nextCellOffset)
                        {
                            landBelongsToLaterCell++;
                            break; // LAND belongs to a later CELL (possibly interior)
                        }

                        try
                        {
                            // Extract heightmap from this LAND
                            var recordData = EsmHelpers.GetRecordData(esm.Data, landAfterCell, bigEndian);
                            var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);

                            var vhgt = subrecords.FirstOrDefault(s => s.Signature == "VHGT");
                            if (vhgt != null && vhgt.Data.Length >= 4 + CellGridSize * CellGridSize)
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

                            // Mark this LAND as used so we don't match it again
                            sortedLands.Remove(landAfterCell);
                            foundLand = true;
                        }
                        catch (InvalidDataException ex) when (ex.Message.Contains("Decompression"))
                        {
                            // Wrong LAND match - the decompressed size doesn't match
                            // This LAND probably belongs to an interior cell, skip it and try next
                            sortedLands.Remove(landAfterCell);
                            decompressionErrors++;
                            if (decompressionErrors <= 5)
                                AnsiConsole.MarkupLine(
                                    $"[yellow]WARN:[/] Decompression failed for LAND 0x{landAfterCell.FormId:X8}: {ex.Message}");
                            // Don't break - loop will try the next LAND
                        }
                        catch (Exception ex)
                        {
                            parseErrors++;
                            if (parseErrors <= 5)
                                AnsiConsole.MarkupLine(
                                    $"[red]Error parsing cell ({cell.GridX},{cell.GridY}): {ex.Message}[/]");
                            sortedLands.Remove(landAfterCell);
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

            // Show distribution of missing cells
            var edgeMissing = missingCells.Count(c =>
                c.x == cellMap.Keys.Min(k => k.x) || c.x == cellMap.Keys.Max(k => k.x) ||
                c.y == cellMap.Keys.Min(k => k.y) || c.y == cellMap.Keys.Max(k => k.y));
            AnsiConsole.MarkupLine(
                $"[grey]  Edge cells missing: {edgeMissing} (cells at world boundary often have no terrain)[/]");

            // Export missing cells to file for analysis
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
        var imageWidth = cellsWide * CellGridSize * scale;
        var imageHeight = cellsHigh * CellGridSize * scale;

        AnsiConsole.MarkupLine(
            $"Output dimensions: [cyan]{imageWidth}×{imageHeight}[/] pixels ({cellsWide}×{cellsHigh} cells)");

        // Step 5: Stitch heightmaps together
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Stitching heightmaps...", ctx =>
            {
                // Find global min/max heights for normalization
                var globalMin = float.MaxValue;
                var globalMax = float.MinValue;

                foreach (var (_, heights) in heightmaps)
                    for (var y = 0; y < CellGridSize; y++)
                    for (var x = 0; x < CellGridSize; x++)
                    {
                        globalMin = Math.Min(globalMin, heights[x, y]);
                        globalMax = Math.Max(globalMax, heights[x, y]);
                    }

                var range = globalMax - globalMin;
                if (range < 0.001f) range = 1f;

                // Create stitched image
                using var image = new Image<L8>(imageWidth, imageHeight);

                // Fill with middle gray for missing cells
                for (var y = 0; y < imageHeight; y++)
                for (var x = 0; x < imageWidth; x++)
                    image[x, y] = new L8(128);

                foreach (var ((cellX, cellY), heights) in heightmaps)
                {
                    // Calculate pixel position for this cell
                    // Cell grid: higher cellY = more north
                    // Image: Y=0 is top (north), Y increases going south
                    var basePixelX = (cellX - minX) * CellGridSize * scale;
                    var basePixelY = (maxY - cellY) * CellGridSize * scale;

                    for (var localY = 0; localY < CellGridSize; localY++)
                    for (var localX = 0; localX < CellGridSize; localX++)
                    {
                        var intensity = (byte)((heights[localX, localY] - globalMin) / range * 255);

                        // VHGT data layout: localY=0 is SOUTH edge, localY=32 is NORTH edge
                        // Image layout: lower Y is NORTH, higher Y is SOUTH
                        // So we need to flip localY within the cell:
                        // - localY=0 (south) should go to bottom of cell (basePixelY + 32)
                        // - localY=32 (north) should go to top of cell (basePixelY + 0)
                        var flippedLocalY = CellGridSize - 1 - localY;

                        // Apply to scaled pixels
                        for (var sy = 0; sy < scale; sy++)
                        for (var sx = 0; sx < scale; sx++)
                        {
                            var px = basePixelX + localX * scale + sx;
                            var py = basePixelY + flippedLocalY * scale + sy;

                            if (px >= 0 && px < imageWidth && py >= 0 && py < imageHeight)
                                image[px, py] = new L8(intensity);
                        }
                    }
                }

                var outputPath = Path.Combine(outputDir, $"{worldspaceName}_heightmap.png");
                image.SaveAsPng(outputPath);
                AnsiConsole.MarkupLine($"Saved heightmap: [cyan]{outputPath}[/]");

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
                    IsBigEndian = bigEndian
                };

                var jsonPath = Path.Combine(outputDir, $"{worldspaceName}_metadata.json");
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(metadata, s_jsonOptions));
                AnsiConsole.MarkupLine($"Saved metadata: [cyan]{jsonPath}[/]");
            });

        // Summary
        AnsiConsole.WriteLine();
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        summaryTable.AddRow("Worldspace", worldspaceName);
        summaryTable.AddRow("Cells Extracted", $"[green]{heightmaps.Count}[/]");
        summaryTable.AddRow("Image Size", $"{imageWidth}×{imageHeight} px");
        summaryTable.AddRow("Grid Range", $"X=[[{minX}, {maxX}]], Y=[[{minY}, {maxY}]]");
        summaryTable.AddRow("Output Directory", outputDir);

        AnsiConsole.Write(summaryTable);

        return 0;
    }

    /// <summary>
    ///     Scans the ESM file GRUP structure to build a map of LAND FormID -> parent CELL FormID.
    ///     LAND records are stored in GRUP type 9 (Cell Temporary Children) which has the parent CELL FormID as its label.
    /// </summary>
    private static Dictionary<uint, uint> BuildLandToCellMap(byte[] data, bool bigEndian)
    {
        var result = new Dictionary<uint, uint>();
        var header = EsmParser.ParseFileHeader(data);

        if (header == null) return result;

        // Skip TES4 header
        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null) return result;

        var startOffset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        ScanForLandRecords(data, bigEndian, startOffset, data.Length, 0, result);

        return result;
    }

    /// <summary>
    ///     Recursively scan GRUPs looking for LAND records and tracking parent CELL FormIDs.
    /// </summary>
    private static void ScanForLandRecords(byte[] data, bool bigEndian, int startOffset, int endOffset,
        uint currentCellFormId, Dictionary<uint, uint> landToCellMap)
    {
        var offset = startOffset;
        var maxIterations = 1_000_000;
        var iterations = 0;

        while (offset + EsmParser.MainRecordHeaderSize <= endOffset && iterations++ < maxIterations)
        {
            var headerData = data.AsSpan(offset);
            var sig = bigEndian
                ? new string([(char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0]])
                : Encoding.ASCII.GetString(headerData.Slice(0, 4));

            if (sig == "GRUP")
            {
                var grupHeader = EsmParser.ParseGroupHeader(headerData, bigEndian);
                if (grupHeader == null) break;

                var grupEnd = offset + (int)grupHeader.GroupSize;
                var innerStart = offset + EsmParser.MainRecordHeaderSize;

                // GRUP type 9 = Cell Temporary Children (contains LAND)
                // The label is the parent CELL FormID
                var parentCellFormId = currentCellFormId;
                if (grupHeader.GroupType == 9)
                    // Label contains parent CELL FormID
                    parentCellFormId = bigEndian
                        ? BinaryUtils.ReadUInt32BE(grupHeader.Label)
                        : BinaryUtils.ReadUInt32LE(grupHeader.Label);

                if (grupEnd > innerStart && grupEnd <= data.Length)
                    ScanForLandRecords(data, bigEndian, innerStart, grupEnd, parentCellFormId, landToCellMap);

                offset = grupEnd;
            }
            else
            {
                // Regular record
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                var formId = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 12)
                    : BinaryUtils.ReadUInt32LE(headerData, 12);

                var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)dataSize;

                if (sig == "LAND" && currentCellFormId != 0) landToCellMap[formId] = currentCellFormId;

                offset = recordEnd;
            }
        }
    }

    private sealed class CellInfo
    {
        public uint FormId { get; init; }
        public int GridX { get; init; }
        public int GridY { get; init; }
        public required AnalyzerRecordInfo CellRecord { get; init; }
    }
}