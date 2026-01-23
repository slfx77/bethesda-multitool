using System.Text;
using System.Text.Json;
using EsmAnalyzer.Helpers;
using ImageMagick;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Commands;

public static partial class ExportCommands
{
    private static int GenerateWorldmap(string filePath, string? worldspaceName, string outputDir, int scale,
        bool rawOutput, bool exportAll, bool analyzeOnly)
    {
        Directory.CreateDirectory(outputDir);

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

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
            // Find all WRLD records in the file
            var worldspaces = FindAllWorldspaces(esm.Data, bigEndian);
            AnsiConsole.MarkupLine($"Found [cyan]{worldspaces.Count}[/] worldspaces in file");
            AnsiConsole.WriteLine();

            var successCount = 0;
            foreach (var (wrldName, wrldFormId) in worldspaces)
            {
                AnsiConsole.MarkupLine("[blue]----------------------------------------[/]");
                var result = GenerateSingleWorldmap(esm.Data, bigEndian, wrldName, wrldFormId, outputDir, scale,
                    rawOutput, sourceType, analyzeOnly);
                if (result == 0) successCount++;
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
                    AnsiConsole.MarkupLine($"  {name}: 0x{formId:X8}");
                return 1;
            }

            targetWorldspaceFormId = parsed.Value;
        }

        return GenerateSingleWorldmap(esm.Data, bigEndian, worldspaceName, targetWorldspaceFormId, outputDir, scale,
            rawOutput, sourceType, analyzeOnly);
    }

    /// <summary>
    ///     Finds all WRLD records in the ESM file and returns their EditorID (if found) and FormID.
    /// </summary>
    private static List<(string name, uint formId)> FindAllWorldspaces(byte[] data, bool bigEndian)
    {
        var worldspaces = new List<(string name, uint formId)>();
        var offset = 0;

        // Skip TES4 header
        if (data.Length < 24) return worldspaces;
        var headerSig = Encoding.ASCII.GetString(data, 0, 4);
        if (headerSig != "TES4" && headerSig != "4SET") return worldspaces;

        var headerSize = bigEndian
            ? BinaryUtils.ReadUInt32BE(data.AsSpan(), 4)
            : BinaryUtils.ReadUInt32LE(data.AsSpan(), 4);
        offset = EsmParser.MainRecordHeaderSize + (int)headerSize;

        while (offset + 24 <= data.Length)
        {
            var headerData = data.AsSpan(offset, 24);
            var sig = bigEndian
                ? new string(new[]
                    { (char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0] })
                : Encoding.ASCII.GetString(data, offset, 4);

            if (sig == "GRUP")
            {
                var grupSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                var grupType = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 12)
                    : BinaryUtils.ReadUInt32LE(headerData, 12);

                // Top-level GRUP type 0 with label "WRLD"
                if (grupType == 0)
                {
                    var label = Encoding.ASCII.GetString(data, offset + 8, 4);
                    var labelBE = new string(new[]
                    {
                        (char)data[offset + 11], (char)data[offset + 10], (char)data[offset + 9], (char)data[offset + 8]
                    });

                    if (label == "WRLD" || labelBE == "WRLD")
                    {
                        // Scan inside this GRUP for WRLD records
                        var grupEnd = offset + (int)grupSize;
                        var innerOffset =
                            offset + EsmParser
                                .MainRecordHeaderSize; // GRUP header is same size as main record header (24 bytes)

                        while (innerOffset + 24 <= grupEnd)
                        {
                            var innerSig = bigEndian
                                ? new string(new[]
                                {
                                    (char)data[innerOffset + 3], (char)data[innerOffset + 2],
                                    (char)data[innerOffset + 1], (char)data[innerOffset]
                                })
                                : Encoding.ASCII.GetString(data, innerOffset, 4);

                            if (innerSig == "WRLD")
                            {
                                var wrldSize = bigEndian
                                    ? BinaryUtils.ReadUInt32BE(data.AsSpan(), innerOffset + 4)
                                    : BinaryUtils.ReadUInt32LE(data.AsSpan(), innerOffset + 4);
                                var wrldFormId = bigEndian
                                    ? BinaryUtils.ReadUInt32BE(data.AsSpan(), innerOffset + 12)
                                    : BinaryUtils.ReadUInt32LE(data.AsSpan(), innerOffset + 12);

                                // Try to extract EDID from the WRLD record
                                var wrldDataStart = innerOffset + EsmParser.MainRecordHeaderSize;
                                var wrldDataEnd = wrldDataStart + (int)wrldSize;
                                var editorId = ExtractEditorId(data, wrldDataStart, wrldDataEnd, bigEndian);

                                // Use EditorID if found, otherwise fallback to FormID string
                                var name = !string.IsNullOrEmpty(editorId) ? editorId : $"WRLD_0x{wrldFormId:X8}";
                                worldspaces.Add((name, wrldFormId));

                                innerOffset = wrldDataEnd;
                            }
                            else if (innerSig == "GRUP")
                            {
                                var innerGrupSize = bigEndian
                                    ? BinaryUtils.ReadUInt32BE(data.AsSpan(), innerOffset + 4)
                                    : BinaryUtils.ReadUInt32LE(data.AsSpan(), innerOffset + 4);
                                innerOffset += (int)innerGrupSize;
                            }
                            else
                            {
                                // Skip other records
                                var recSize = bigEndian
                                    ? BinaryUtils.ReadUInt32BE(data.AsSpan(), innerOffset + 4)
                                    : BinaryUtils.ReadUInt32LE(data.AsSpan(), innerOffset + 4);
                                innerOffset += EsmParser.MainRecordHeaderSize + (int)recSize;
                            }
                        }
                    }
                }

                offset += (int)grupSize;
            }
            else
            {
                // Non-GRUP record, skip
                var recSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)recSize;
            }
        }

        return worldspaces;
    }

    /// <summary>
    ///     Extracts the EDID (EditorID) from a record's data section.
    /// </summary>
    private static string? ExtractEditorId(byte[] data, int dataStart, int dataEnd, bool bigEndian)
    {
        var offset = dataStart;
        while (offset + 6 <= dataEnd)
        {
            var subSig = bigEndian
                ? new string(new[]
                    { (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset] })
                : Encoding.ASCII.GetString(data, offset, 4);
            var subSize = bigEndian
                ? BinaryUtils.ReadUInt16BE(data.AsSpan(), offset + 4)
                : BinaryUtils.ReadUInt16LE(data.AsSpan(), offset + 4);

            if (subSig == "EDID" && subSize > 0 && offset + 6 + subSize <= dataEnd)
            {
                // EDID is null-terminated string
                var edidLength = subSize - 1; // exclude null terminator
                if (edidLength > 0)
                    return Encoding.ASCII.GetString(data, offset + 6, edidLength);
            }

            offset += 6 + subSize;
        }

        return null;
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
                ctx.Status("Finding WRLD record and scanning child GRUPs...");
                // Find cells and lands that belong to the target worldspace by scanning GRUP hierarchy
                var (worldCells, worldLands) = ScanWorldspaceCellsAndLands(data, bigEndian, targetWorldspaceFormId);
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
                    try
                    {
                        var recordData = EsmHelpers.GetRecordData(data, cell, bigEndian);
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

        AnsiConsole.MarkupLine(
            $"Found [cyan]{cellMap.Count}[/] exterior cells with grid positions in {worldspaceName}");
        if (cellParseErrors > 0)
            AnsiConsole.MarkupLine($"[yellow]WARN:[/] {cellParseErrors} CELL records failed to parse.");

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
                            var recordData = EsmHelpers.GetRecordData(data, landAfterCell, bigEndian);
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

        // If analyze-only mode, run height analysis and return
        if (analyzeOnly)
        {
            AnalyzeHeightDistribution(heightmaps, minX, maxX, minY, maxY, worldspaceName, outputDir);
            return 0;
        }

        var imageWidth = cellsWide * CellGridSize * scale;
        var imageHeight = cellsHigh * CellGridSize * scale;

        AnsiConsole.MarkupLine(
            $"Output dimensions: [cyan]{imageWidth}×{imageHeight}[/] pixels ({cellsWide}×{cellsHigh} cells)");

        // Step 5: Stitch heightmaps together
        float savedGlobalMin = 0, savedGlobalMax = 0;
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

                savedGlobalMin = globalMin;
                savedGlobalMax = globalMax;

                if (rawOutput)
                {
                    // 16-bit grayscale PNG output (normalized)
                    var gray16Pixels = new ushort[imageWidth * imageHeight];

                    // Fill with middle gray for missing cells
                    Array.Fill(gray16Pixels, (ushort)32768);

                    foreach (var ((cellX, cellY), heights) in heightmaps)
                    {
                        var basePixelX = (cellX - minX) * CellGridSize * scale;
                        var basePixelY = (maxY - cellY) * CellGridSize * scale;

                        for (var localY = 0; localY < CellGridSize; localY++)
                            for (var localX = 0; localX < CellGridSize; localX++)
                            {
                                var normalizedHeight = (heights[localX, localY] - globalMin) / range;
                                var gray16 = (ushort)(normalizedHeight * 65535);
                                var flippedLocalY = CellGridSize - 1 - localY;

                                for (var sy = 0; sy < scale; sy++)
                                    for (var sx = 0; sx < scale; sx++)
                                    {
                                        var px = basePixelX + localX * scale + sx;
                                        var py = basePixelY + flippedLocalY * scale + sy;

                                        if (px >= 0 && px < imageWidth && py >= 0 && py < imageHeight)
                                            gray16Pixels[py * imageWidth + px] = gray16;
                                    }
                            }
                    }

                    // Create 16-bit grayscale PNG using Magick.NET
                    var settings = new MagickReadSettings
                    {
                        Width = (uint)imageWidth,
                        Height = (uint)imageHeight,
                        Format = MagickFormat.Gray,
                        Depth = 16
                    };

                    // Convert ushort array to bytes (little-endian)
                    var grayBytes = new byte[gray16Pixels.Length * 2];
                    Buffer.BlockCopy(gray16Pixels, 0, grayBytes, 0, grayBytes.Length);

                    using var image = new MagickImage(grayBytes, settings);

                    var outputPath = Path.Combine(outputDir, $"{worldspaceName}_{sourceType}_heightmap_raw.png");
                    image.Write(outputPath, MagickFormat.Png);
                    AnsiConsole.MarkupLine($"Saved 16-bit grayscale PNG: [cyan]{outputPath}[/]");
                }
                else
                {
                    // Color gradient PNG output
                    var rgbaPixels = new byte[imageWidth * imageHeight * 4];

                    // Fill with middle gray for missing cells
                    for (var i = 0; i < imageWidth * imageHeight; i++)
                    {
                        rgbaPixels[i * 4 + 0] = 128; // R
                        rgbaPixels[i * 4 + 1] = 128; // G
                        rgbaPixels[i * 4 + 2] = 128; // B
                        rgbaPixels[i * 4 + 3] = 255; // A
                    }

                    foreach (var ((cellX, cellY), heights) in heightmaps)
                    {
                        var basePixelX = (cellX - minX) * CellGridSize * scale;
                        var basePixelY = (maxY - cellY) * CellGridSize * scale;

                        for (var localY = 0; localY < CellGridSize; localY++)
                            for (var localX = 0; localX < CellGridSize; localX++)
                            {
                                var normalizedHeight = (heights[localX, localY] - globalMin) / range;
                                var (r, g, b) = HeightToColor(normalizedHeight);
                                var flippedLocalY = CellGridSize - 1 - localY;

                                for (var sy = 0; sy < scale; sy++)
                                    for (var sx = 0; sx < scale; sx++)
                                    {
                                        var px = basePixelX + localX * scale + sx;
                                        var py = basePixelY + flippedLocalY * scale + sy;

                                        if (px >= 0 && px < imageWidth && py >= 0 && py < imageHeight)
                                        {
                                            var idx = (py * imageWidth + px) * 4;
                                            rgbaPixels[idx + 0] = r;
                                            rgbaPixels[idx + 1] = g;
                                            rgbaPixels[idx + 2] = b;
                                            rgbaPixels[idx + 3] = 255;
                                        }
                                    }
                            }
                    }

                    // Create RGBA PNG using Magick.NET
                    var settings = new MagickReadSettings
                    {
                        Width = (uint)imageWidth,
                        Height = (uint)imageHeight,
                        Format = MagickFormat.Rgba,
                        Depth = 8
                    };

                    using var image = new MagickImage(rgbaPixels, settings);

                    var outputPath = Path.Combine(outputDir, $"{worldspaceName}_{sourceType}_heightmap.png");
                    image.Write(outputPath, MagickFormat.Png);
                    AnsiConsole.MarkupLine($"Saved color heightmap: [green]{outputPath}[/]");
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

        summaryTable.AddRow("Worldspace", worldspaceName);
        summaryTable.AddRow("Cells Extracted", $"[green]{heightmaps.Count}[/]");
        summaryTable.AddRow("Image Size", $"{imageWidth}×{imageHeight} px");
        summaryTable.AddRow("Grid Range", $"X=[[{minX}, {maxX}]], Y=[[{minY}, {maxY}]]");
        summaryTable.AddRow("Output Directory", outputDir);

        AnsiConsole.Write(summaryTable);

        return 0;
    }

    /// <summary>
    ///     Scans the ESM file to find the WRLD record with the given FormID, then scans its child GRUPs
    ///     to collect all CELL and LAND records that belong to that worldspace.
    /// </summary>
    private static (List<AnalyzerRecordInfo> cells, List<AnalyzerRecordInfo> lands) ScanWorldspaceCellsAndLands(
        byte[] data, bool bigEndian, uint worldFormId)
    {
        var cells = new List<AnalyzerRecordInfo>();
        var lands = new List<AnalyzerRecordInfo>();

        var header = EsmParser.ParseFileHeader(data);
        if (header == null) return (cells, lands);

        // Skip TES4 header
        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null) return (cells, lands);

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        // Scan for top-level WRLD GRUP (type 0 with label = 'WRLD')
        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var headerData = data.AsSpan(offset);
            var sig = bigEndian
                ? new string([(char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0]])
                : Encoding.ASCII.GetString(headerData.Slice(0, 4));

            if (sig != "GRUP")
            {
                // Skip non-GRUP record
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
                continue;
            }

            var grupHeader = EsmParser.ParseGroupHeader(headerData, bigEndian);
            if (grupHeader == null) break;

            var grupEnd = offset + (int)grupHeader.GroupSize;

            // Check if this is the top-level WRLD GRUP (type 0, label = 'WRLD')
            if (grupHeader.GroupType == 0)
            {
                var labelSig = bigEndian
                    ? new string([
                        (char)grupHeader.Label[3], (char)grupHeader.Label[2], (char)grupHeader.Label[1],
                        (char)grupHeader.Label[0]
                    ])
                    : Encoding.ASCII.GetString(grupHeader.Label);

                if (labelSig == "WRLD")
                    // Scan inside WRLD GRUP for our target worldspace
                    ScanWrldGrup(data, bigEndian, offset + EsmParser.MainRecordHeaderSize, grupEnd, worldFormId, cells,
                        lands);
            }

            offset = grupEnd;
        }

        return (cells, lands);
    }

    /// <summary>
    ///     Scan inside a WRLD GRUP looking for the target worldspace and its children.
    /// </summary>
    private static void ScanWrldGrup(byte[] data, bool bigEndian, int startOffset, int endOffset,
        uint targetWorldFormId, List<AnalyzerRecordInfo> cells, List<AnalyzerRecordInfo> lands)
    {
        var offset = startOffset;

        while (offset + EsmParser.MainRecordHeaderSize <= endOffset)
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

                // GRUP type 1 = World Children (contains cells for a specific world)
                // Label = parent WRLD FormID
                if (grupHeader.GroupType == 1)
                {
                    var parentWorldId = bigEndian
                        ? BinaryUtils.ReadUInt32BE(grupHeader.Label)
                        : BinaryUtils.ReadUInt32LE(grupHeader.Label);

                    if (parentWorldId == targetWorldFormId)
                        // This is our target worldspace's children - scan for cells and lands
                        ScanWorldChildren(data, bigEndian, offset + EsmParser.MainRecordHeaderSize, grupEnd, cells,
                            lands);
                }

                offset = grupEnd;
            }
            else if (sig == "WRLD")
            {
                // WRLD record - check if it's our target
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                var formId = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 12)
                    : BinaryUtils.ReadUInt32LE(headerData, 12);

                if (formId == targetWorldFormId)
                {
                    // Found our target worldspace record
                }

                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
            else
            {
                // Skip other records
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
        }
    }

    /// <summary>
    ///     Scan World Children GRUP (type 1) for CELL and LAND records.
    /// </summary>
    private static void ScanWorldChildren(byte[] data, bool bigEndian, int startOffset, int endOffset,
        List<AnalyzerRecordInfo> cells, List<AnalyzerRecordInfo> lands)
    {
        var offset = startOffset;

        while (offset + EsmParser.MainRecordHeaderSize <= endOffset)
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

                // Recursively scan child GRUPs (types 4-10 are cell-related)
                ScanWorldChildren(data, bigEndian, offset + EsmParser.MainRecordHeaderSize, grupEnd, cells, lands);

                offset = grupEnd;
            }
            else if (sig == "CELL")
            {
                var recordHeader = EsmParser.ParseRecordHeader(headerData, bigEndian);
                if (recordHeader != null)
                    cells.Add(new AnalyzerRecordInfo
                    {
                        Signature = "CELL",
                        Offset = (uint)offset,
                        DataSize = recordHeader.DataSize,
                        Flags = recordHeader.Flags,
                        FormId = recordHeader.FormId,
                        TotalSize = EsmParser.MainRecordHeaderSize + recordHeader.DataSize
                    });

                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
            else if (sig == "LAND")
            {
                var recordHeader = EsmParser.ParseRecordHeader(headerData, bigEndian);
                if (recordHeader != null)
                    lands.Add(new AnalyzerRecordInfo
                    {
                        Signature = "LAND",
                        Offset = (uint)offset,
                        DataSize = recordHeader.DataSize,
                        Flags = recordHeader.Flags,
                        FormId = recordHeader.FormId,
                        TotalSize = EsmParser.MainRecordHeaderSize + recordHeader.DataSize
                    });

                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
            else
            {
                // Skip other records
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
        }
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

    /// <summary>
    ///     Converts a normalized height value (0-1) to a color using data-driven transitions.
    ///     Based on height analysis: 80% of terrain is in 0.21-0.54 range, median at 0.37.
    ///     Uses HIGH SATURATION like test1 for best detail, rushes through greens.
    ///     Mountains: Brown → Red → Pink → White (traditional topo map style).
    ///     User color mappings applied:
    ///     #813F08 → #C46616 (orange-brown brighter)
    ///     #9A241E → #6E2D0D (red darker, more brown-toned)
    ///     #C33457 → #BD2210 (pink-red → pure red)
    ///     #C92282 → #E03071 (magenta → pink, brighter)
    /// </summary>
    private static (byte r, byte g, byte b) HeightToColor(float normalizedHeight)
    {
        // Clamp to 0-1 range
        normalizedHeight = Math.Clamp(normalizedHeight, 0f, 1f);

        float h, s, l;

        if (normalizedHeight < 0.10f)
        {
            // Deep areas: Dark blue
            var t = normalizedHeight / 0.10f;
            h = 220f;
            s = 0.90f;
            l = 0.25f + t * 0.10f; // 0.25 → 0.35
        }
        else if (normalizedHeight < 0.21f)
        {
            // Low areas: Blue → Cyan (bright)
            var t = (normalizedHeight - 0.10f) / 0.11f;
            h = 220f - t * 40f; // 220 → 180
            s = 0.90f;
            l = 0.35f + t * 0.13f; // 0.35 → 0.48
        }
        else if (normalizedHeight < 0.27f)
        {
            // Cyan → Lime: DARKER lime (#28d211 → #157009: L 44% → 24%)
            var t = (normalizedHeight - 0.21f) / 0.06f;
            h = 180f - t * 67f; // 180 → 113 (cyan → lime-green)
            s = 0.85f;
            l = 0.48f - t * 0.24f; // 0.48 → 0.24 (dark lime)
        }
        else if (normalizedHeight < 0.34f)
        {
            // Lime → Yellow: BRIGHTEN (#86840b → #c4c110: L 28% → 42%)
            var t = (normalizedHeight - 0.27f) / 0.07f;
            h = 113f - t * 54f; // 113 → 59 (lime → yellow)
            s = 0.85f;
            l = 0.24f + t * 0.18f; // 0.24 → 0.42 (brighten to yellow)
        }
        else if (normalizedHeight < 0.45f)
        {
            // Yellow → Orange: BRIGHTEN (#7d3f0e → #d75d0e: L 27% → 45%)
            var t = (normalizedHeight - 0.34f) / 0.11f;
            h = 59f - t * 35f; // 59 → 24
            s = 0.85f - t * 0.03f; // 0.85 → 0.82
            l = 0.42f + t * 0.03f; // 0.42 → 0.45 (brighter orange)
        }
        else if (normalizedHeight < 0.54f)
        {
            // Orange → Brown-red: darken
            var t = (normalizedHeight - 0.45f) / 0.09f;
            h = 24f - t * 8f; // 24 → 16
            s = 0.82f - t * 0.02f; // 0.82 → 0.80
            l = 0.45f - t * 0.15f; // 0.45 → 0.30
        }
        else if (normalizedHeight < 0.65f)
        {
            // Brown-red → Red: DARKEN (#c71415 → #8a310f: L 43% → 30%)
            var t = (normalizedHeight - 0.54f) / 0.11f;
            h = 16f - t * 11f; // 16 → 5 (toward red)
            s = 0.80f + t * 0.05f; // 0.80 → 0.85
            l = 0.30f + t * 0.14f; // 0.30 → 0.44 (builds up to red zone)
        }
        else if (normalizedHeight < 0.78f)
        {
            // Red → Pink: stay more red (#e02258 → #cf1f10: H 345 → 5)
            var t = (normalizedHeight - 0.65f) / 0.13f;
            h = 5f - t * 1f; // 5 → 4 (stay red, slight shift)
            s = 0.85f - t * 0.08f; // 0.85 → 0.77
            l = 0.44f + t * 0.11f; // 0.44 → 0.55
        }
        else if (normalizedHeight < 0.90f)
        {
            // Pink → Light pink: go SHORT way (4 → 324 via 360, subtract 40)
            var t = (normalizedHeight - 0.78f) / 0.12f;
            h = 4f - t * 40f; // 4 → -36 (wraps to 324)
            if (h < 0f) h += 360f;
            s = 0.77f - t * 0.17f; // 0.77 → 0.60
            l = 0.55f + t * 0.10f; // 0.55 → 0.65
        }
        else
        {
            // Peaks: Light pink → White (continuous from previous zone)
            var t = (normalizedHeight - 0.90f) / 0.10f;
            h = 324f - t * 4f; // 324 → 320 (continue pink hue)
            s = 0.60f - t * 0.55f; // 0.60 → 0.05 (fade to white)
            l = 0.65f + t * 0.30f; // 0.65 → 0.95 (brighten to white)
        }

        return HslToRgb(h, s, l);
    }

    /// <summary>
    ///     Converts HSL color to RGB.
    /// </summary>
    /// <param name="h">Hue in degrees (0-360)</param>
    /// <param name="s">Saturation (0-1)</param>
    /// <param name="l">Lightness (0-1)</param>
    private static (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
    {
        if (s < 0.001f)
        {
            // Achromatic (gray)
            var gray = (byte)(l * 255);
            return (gray, gray, gray);
        }

        h /= 360f; // Normalize hue to 0-1

        var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        var r = HueToRgb(p, q, h + 1f / 3f);
        var g = HueToRgb(p, q, h);
        var b = HueToRgb(p, q, h - 1f / 3f);

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1f / 6f) return p + (q - p) * 6 * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6;
        return p;
    }

    /// <summary>
    ///     Analyzes the height distribution of the heightmap and outputs statistics.
    ///     Samples different regions to identify terrain features and their height ranges.
    /// </summary>
    private static void AnalyzeHeightDistribution(
        Dictionary<(int x, int y), float[,]> heightmaps,
        int minX, int maxX, int minY, int maxY,
        string worldspaceName, string outputDir)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
        AnsiConsole.MarkupLine("[bold]Height Distribution Analysis[/]");
        AnsiConsole.MarkupLine("[blue]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
        AnsiConsole.WriteLine();

        // Collect all height values
        var allHeights = new List<float>();
        foreach (var (_, heights) in heightmaps)
            for (var y = 0; y < CellGridSize; y++)
                for (var x = 0; x < CellGridSize; x++)
                    allHeights.Add(heights[x, y]);

        allHeights.Sort();
        var totalPoints = allHeights.Count;

        var globalMin = allHeights[0];
        var globalMax = allHeights[^1];
        var range = globalMax - globalMin;
        var median = allHeights[totalPoints / 2];
        var mean = allHeights.Average();

        // Calculate percentiles
        var p10 = allHeights[(int)(totalPoints * 0.10)];
        var p25 = allHeights[(int)(totalPoints * 0.25)];
        var p50 = allHeights[(int)(totalPoints * 0.50)];
        var p75 = allHeights[(int)(totalPoints * 0.75)];
        var p90 = allHeights[(int)(totalPoints * 0.90)];
        var p95 = allHeights[(int)(totalPoints * 0.95)];
        var p99 = allHeights[(int)(totalPoints * 0.99)];

        // Display global statistics
        var statsTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Global Height Statistics[/]")
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Raw Height[/]")
            .AddColumn("[bold]Normalized (0-1)[/]");

        statsTable.AddRow("Minimum", $"{globalMin:F2}", "0.00");
        statsTable.AddRow("10th %ile", $"{p10:F2}", $"{(p10 - globalMin) / range:F3}");
        statsTable.AddRow("25th %ile", $"{p25:F2}", $"{(p25 - globalMin) / range:F3}");
        statsTable.AddRow("Median (50th)", $"{p50:F2}", $"{(p50 - globalMin) / range:F3}");
        statsTable.AddRow("Mean", $"{mean:F2}", $"{(mean - globalMin) / range:F3}");
        statsTable.AddRow("75th %ile", $"{p75:F2}", $"{(p75 - globalMin) / range:F3}");
        statsTable.AddRow("90th %ile", $"{p90:F2}", $"{(p90 - globalMin) / range:F3}");
        statsTable.AddRow("95th %ile", $"{p95:F2}", $"{(p95 - globalMin) / range:F3}");
        statsTable.AddRow("99th %ile", $"{p99:F2}", $"{(p99 - globalMin) / range:F3}");
        statsTable.AddRow("Maximum", $"{globalMax:F2}", "1.00");
        statsTable.AddRow("[grey]Range[/]", $"[grey]{range:F2}[/]", "[grey]—[/]");

        AnsiConsole.Write(statsTable);
        AnsiConsole.WriteLine();

        // Create histogram (20 bins)
        const int numBins = 20;
        var binCounts = new int[numBins];
        foreach (var h in allHeights)
        {
            var normalized = (h - globalMin) / range;
            var bin = Math.Min((int)(normalized * numBins), numBins - 1);
            binCounts[bin]++;
        }

        var maxCount = binCounts.Max();

        AnsiConsole.MarkupLine("[bold]Height Histogram (normalized 0-1):[/]");
        for (var i = 0; i < numBins; i++)
        {
            var binStart = (float)i / numBins;
            var binEnd = (float)(i + 1) / numBins;
            var barLength = (int)(50.0 * binCounts[i] / maxCount);
            var bar = new string('█', barLength);
            var pct = 100.0 * binCounts[i] / totalPoints;
            AnsiConsole.MarkupLine($"[grey]{binStart:F2}-{binEnd:F2}[/] [green]{bar,-50}[/] {pct:F1}%");
        }

        AnsiConsole.WriteLine();

        // Sample specific regions of the map
        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        var quarterW = (maxX - minX) / 4;
        var quarterH = (maxY - minY) / 4;

        var regions = new (string name, int x1, int y1, int x2, int y2)[]
        {
            ("North Center (flat?)", centerX - quarterW / 2, centerY + quarterH, centerX + quarterW / 2, maxY),
            ("South Center (mountains?)", centerX - quarterW / 2, minY, centerX + quarterW / 2, centerY - quarterH),
            ("West (mountains?)", minX, centerY - quarterH / 2, centerX - quarterW, centerY + quarterH / 2),
            ("East (lake?)", centerX + quarterW, centerY - quarterH / 2, maxX, centerY + quarterH / 2),
            ("Southeast (river?)", centerX, minY, maxX, centerY - quarterH),
            ("Center", centerX - quarterW / 2, centerY - quarterH / 2, centerX + quarterW / 2, centerY + quarterH / 2)
        };

        var regionTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Regional Height Analysis[/]")
            .AddColumn("[bold]Region[/]")
            .AddColumn("[bold]Cells[/]")
            .AddColumn("[bold]Min[/]")
            .AddColumn("[bold]Max[/]")
            .AddColumn("[bold]Mean[/]")
            .AddColumn("[bold]Norm Min[/]")
            .AddColumn("[bold]Norm Max[/]")
            .AddColumn("[bold]Norm Mean[/]");

        foreach (var (name, x1, y1, x2, y2) in regions)
        {
            var regionHeights = new List<float>();
            var cellCount = 0;

            foreach (var ((cx, cy), heights) in heightmaps)
                if (cx >= x1 && cx <= x2 && cy >= y1 && cy <= y2)
                {
                    cellCount++;
                    for (var y = 0; y < CellGridSize; y++)
                        for (var x = 0; x < CellGridSize; x++)
                            regionHeights.Add(heights[x, y]);
                }

            if (regionHeights.Count > 0)
            {
                var rMin = regionHeights.Min();
                var rMax = regionHeights.Max();
                var rMean = regionHeights.Average();
                var nMin = (rMin - globalMin) / range;
                var nMax = (rMax - globalMin) / range;
                var nMean = (rMean - globalMin) / range;

                regionTable.AddRow(
                    name,
                    $"{cellCount}",
                    $"{rMin:F1}",
                    $"{rMax:F1}",
                    $"{rMean:F1}",
                    $"[cyan]{nMin:F3}[/]",
                    $"[cyan]{nMax:F3}[/]",
                    $"[yellow]{nMean:F3}[/]"
                );
            }
        }

        AnsiConsole.Write(regionTable);
        AnsiConsole.WriteLine();

        // Suggest gradient transition points
        AnsiConsole.MarkupLine("[bold]Suggested Gradient Transition Points:[/]");
        AnsiConsole.MarkupLine(
            "[grey]Based on height distribution, place major color transitions at these normalized values:[/]");
        AnsiConsole.WriteLine();

        // Find where most of the data lies and suggest transition points
        var transitionPoints = new List<(float norm, string desc)>
        {
            (0.00f, "Minimum (deepest)"),
            ((p10 - globalMin) / range, "10th percentile (low areas)"),
            ((p25 - globalMin) / range, "25th percentile"),
            ((p50 - globalMin) / range, "Median (most common height)"),
            ((p75 - globalMin) / range, "75th percentile"),
            ((p90 - globalMin) / range, "90th percentile (high areas)"),
            (1.00f, "Maximum (peaks)")
        };

        foreach (var (norm, desc) in transitionPoints)
            AnsiConsole.MarkupLine($"  [cyan]{norm:F3}[/] - {desc}");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Key insight:[/] Most terrain is between the 10th and 90th percentile.");
        AnsiConsole.MarkupLine(
            $"  That's the normalized range [cyan]{(p10 - globalMin) / range:F3}[/] to [cyan]{(p90 - globalMin) / range:F3}[/].");
        AnsiConsole.MarkupLine("  Your gradient should have the most color variation in this range.");

        // Save analysis to JSON
        var analysisPath = Path.Combine(outputDir, $"{worldspaceName}_height_analysis.json");
        var analysis = new
        {
            Worldspace = worldspaceName,
            TotalDataPoints = totalPoints,
            GlobalMin = globalMin,
            GlobalMax = globalMax,
            Range = range,
            Mean = mean,
            Median = median,
            Percentiles = new { P10 = p10, P25 = p25, P50 = p50, P75 = p75, P90 = p90, P95 = p95, P99 = p99 },
            NormalizedPercentiles = new
            {
                P10 = (p10 - globalMin) / range,
                P25 = (p25 - globalMin) / range,
                P50 = (p50 - globalMin) / range,
                P75 = (p75 - globalMin) / range,
                P90 = (p90 - globalMin) / range,
                P95 = (p95 - globalMin) / range,
                P99 = (p99 - globalMin) / range
            },
            Histogram = binCounts.Select((count, i) => new
            {
                BinStart = (float)i / numBins,
                BinEnd = (float)(i + 1) / numBins,
                Count = count,
                Percentage = 100.0 * count / totalPoints
            }).ToArray()
        };

        File.WriteAllText(analysisPath, JsonSerializer.Serialize(analysis, s_jsonOptions));
        AnsiConsole.MarkupLine($"Analysis saved to: [cyan]{analysisPath}[/]");
    }

    private sealed class CellInfo
    {
        public uint FormId { get; init; }
        public int GridX { get; init; }
        public int GridY { get; init; }
        public required AnalyzerRecordInfo CellRecord { get; init; }
    }
}
