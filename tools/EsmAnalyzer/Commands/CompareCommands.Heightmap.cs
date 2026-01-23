using System.Buffers.Binary;
using System.Text;
using EsmAnalyzer.Core;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Compare heightmap data between two ESM files and generate teleport commands.
/// </summary>
public static partial class CompareCommands
{

    /// <summary>
    ///     Compares heightmaps between two ESM files and generates console commands for teleportation.
    /// </summary>
    public static int CompareHeightmaps(string file1Path, string file2Path, string? worldspaceName,
        string outputPath, int threshold, int maxResults, bool showStats)
    {
        AnsiConsole.MarkupLine("[blue]Comparing heightmaps between:[/]");
        AnsiConsole.MarkupLine($"  File 1: [cyan]{Path.GetFileName(file1Path)}[/]");
        AnsiConsole.MarkupLine($"  File 2: [cyan]{Path.GetFileName(file2Path)}[/]");
        AnsiConsole.WriteLine();

        // Load both ESM files
        var esm1 = EsmFileLoader.Load(file1Path, false);
        var esm2 = EsmFileLoader.Load(file2Path, false);
        if (esm1 == null || esm2 == null) return 1;

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
        var bounds = ExtractWorldspaceBounds(esm2.Data, esm2.IsBigEndian, targetFormId);
        if (bounds != null)
        {
            AnsiConsole.MarkupLine(
                $"Playable bounds: X=[cyan]{bounds.MinCellX}[/] to [cyan]{bounds.MaxCellX}[/], Y=[cyan]{bounds.MinCellY}[/] to [cyan]{bounds.MaxCellY}[/]");
            AnsiConsole.WriteLine();
        }

        // Extract heightmaps from both files (returns heightmaps and cell names)
        var (heightmaps1, cellNames1) =
            ExtractHeightmapsForComparison(esm1.Data, esm1.IsBigEndian, targetFormId, "File 1");
        var (heightmaps2, cellNames2) =
            ExtractHeightmapsForComparison(esm2.Data, esm2.IsBigEndian, targetFormId, "File 2");

        // Merge cell names from both files (prefer file 2 / final)
        var cellNames = new Dictionary<(int, int), string>(cellNames1);
        foreach (var kvp in cellNames2)
            cellNames[kvp.Key] = kvp.Value;

        if (heightmaps1.Count == 0 || heightmaps2.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Could not extract heightmaps from one or both files");
            return 1;
        }

        AnsiConsole.MarkupLine($"File 1: [cyan]{heightmaps1.Count}[/] cells with heightmap data");
        AnsiConsole.MarkupLine($"File 2: [cyan]{heightmaps2.Count}[/] cells with heightmap data");
        AnsiConsole.WriteLine();

        // Compare heightmaps and find differences
        var differences = CompareHeightmapData(heightmaps1, heightmaps2, threshold, cellNames);

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

        // Sort by impact score (magnitude × coverage)
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
            DisplayGroupTable(displayInBounds, worldspaceName);
            AnsiConsole.WriteLine();
        }

        // Display summary table for out-of-bounds groups
        if (displayOutBounds.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]OUT-OF-BOUNDS TERRAIN REGIONS:[/]");
            DisplayGroupTable(displayOutBounds, worldspaceName);
            AnsiConsole.WriteLine();
        }

        // Show statistics if requested
        if (showStats)
            ShowComparisonStats(heightmaps1, heightmaps2, differences, allGroups, inBoundsGroups, outOfBoundsGroups);

        // Generate output file with detailed information and console commands
        GenerateHeightmapComparisonOutput(outputPath, worldspaceName, file1Path, file2Path, inBoundsGroups,
            outOfBoundsGroups, bounds);

        AnsiConsole.MarkupLine($"[green]Output saved to:[/] [cyan]{outputPath}[/]");
        return 0;
    }

    /// <summary>
    ///     Displays a summary table of cell groups.
    /// </summary>
    private static void DisplayGroupTable(List<CellGroup> groups, string worldspaceName)
    {
        var table = new Table();
        table.AddColumn("Rank");
        table.AddColumn("Region");
        table.AddColumn("Size");
        table.AddColumn("Max Diff");
        table.AddColumn("Avg Diff");
        table.AddColumn("Points Changed");
        table.AddColumn("Console Command");

        var rank = 1;
        foreach (var group in groups)
        {
            var center = group.CenterCell;
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

            table.AddRow(
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
    ///     Extracts all heightmaps for a worldspace from ESM data.
    /// </summary>
    private static (Dictionary<(int x, int y), float[,]> heightmaps, Dictionary<(int x, int y), string> cellNames)
        ExtractHeightmapsForComparison(byte[] data, bool bigEndian, uint worldspaceFormId, string label)
    {
        var heightmaps = new Dictionary<(int x, int y), float[,]>();
        var cellNames = new Dictionary<(int x, int y), string>();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start($"Extracting heightmaps from {label}...", ctx =>
            {
                // Find all CELL and LAND records for this worldspace
                var (cellRecords, landRecords) =
                    FindCellsAndLandsForWorldspaceComparison(data, bigEndian, worldspaceFormId);

                // Build cell map with grid coordinates
                var cellMap = new Dictionary<(int x, int y), HeightmapCellInfo>();
                foreach (var cell in cellRecords)
                    try
                    {
                        var recordData = EsmHelpers.GetRecordData(data, cell, bigEndian);
                        var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);

                        // Check for EDID (editor ID)
                        var edid = subrecords.FirstOrDefault(s => s.Signature == "EDID");
                        string? editorId = null;
                        if (edid != null && edid.Data.Length > 0)
                        {
                            // EDID is a null-terminated string
                            var nullIdx = Array.IndexOf(edid.Data, (byte)0);
                            var len = nullIdx >= 0 ? nullIdx : edid.Data.Length;
                            editorId = Encoding.ASCII.GetString(edid.Data, 0, len);
                        }

                        // Check for XCLC (cell grid coordinates)
                        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
                        if (xclc != null && xclc.Data.Length >= 8)
                        {
                            var gridX = bigEndian
                                ? BinaryPrimitives.ReadInt32BigEndian(xclc.Data.AsSpan(0))
                                : BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0));
                            var gridY = bigEndian
                                ? BinaryPrimitives.ReadInt32BigEndian(xclc.Data.AsSpan(4))
                                : BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4));

                            cellMap[(gridX, gridY)] = new HeightmapCellInfo
                            {
                                FormId = cell.FormId,
                                GridX = gridX,
                                GridY = gridY,
                                EditorId = editorId,
                                CellRecord = cell
                            };

                            // Store editor ID if present
                            if (!string.IsNullOrEmpty(editorId))
                                cellNames[(gridX, gridY)] = editorId;
                        }
                    }
                    catch
                    {
                        // Skip cells that fail to parse
                    }

                // Get all cell offsets for boundary checking
                var allCellOffsets = cellRecords.Select(c => c.Offset).OrderBy(o => o).ToList();

                // Sort LANDs by offset
                var sortedLands = landRecords.OrderBy(l => l.Offset).ToList();

                // Match LANDs to cells
                foreach (var cell in cellMap.Values.OrderBy(c => c.CellRecord.Offset))
                {
                    // Find LAND records after this cell
                    var landsAfterCell = sortedLands.Where(l => l.Offset > cell.CellRecord.Offset).Take(5).ToList();

                    foreach (var land in landsAfterCell)
                    {
                        // Check if this LAND belongs to a later cell
                        var nextCellOffset = allCellOffsets.FirstOrDefault(o => o > cell.CellRecord.Offset);
                        if (nextCellOffset != default && land.Offset > nextCellOffset)
                            break;

                        try
                        {
                            var recordData = EsmHelpers.GetRecordData(data, land, bigEndian);
                            var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);

                            var vhgt = subrecords.FirstOrDefault(s => s.Signature == "VHGT");
                            if (vhgt != null && vhgt.Data.Length >= 4 + EsmConstants.LandGridArea)
                            {
                                var heights = ParseHeightmapData(vhgt.Data, bigEndian);
                                if (!heightmaps.ContainsKey((cell.GridX, cell.GridY)))
                                    heightmaps[(cell.GridX, cell.GridY)] = heights;
                            }

                            sortedLands.Remove(land);
                            break;
                        }
                        catch
                        {
                            sortedLands.Remove(land);
                        }
                    }
                }
            });

        return (heightmaps, cellNames);
    }

    /// <summary>
    ///     Finds CELL and LAND records belonging to a worldspace.
    /// </summary>
    private static (List<AnalyzerRecordInfo> cells, List<AnalyzerRecordInfo> lands)
        FindCellsAndLandsForWorldspaceComparison(
            byte[] data, bool bigEndian, uint worldspaceFormId)
    {
        var cells = new List<AnalyzerRecordInfo>();
        var lands = new List<AnalyzerRecordInfo>();

        var header = EsmParser.ParseFileHeader(data);
        if (header == null) return (cells, lands);

        // Skip TES4 header
        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null) return (cells, lands);

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        // Track if we're inside the target worldspace's GRUP
        var inTargetWorldspace = false;
        var grupEndOffset = 0;

        while (offset + 24 <= data.Length)
        {
            var headerData = data.AsSpan(offset);
            var sig = bigEndian
                ? new string([(char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0]])
                : Encoding.ASCII.GetString(data, offset, 4);

            if (sig == "GRUP")
            {
                var grupSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                var grupType = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 12)
                    : BinaryUtils.ReadUInt32LE(headerData, 12);
                var grupLabel = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 8)
                    : BinaryUtils.ReadUInt32LE(headerData, 8);

                // Type 1 = Worldspace children
                if (grupType == 1 && grupLabel == worldspaceFormId)
                {
                    inTargetWorldspace = true;
                    grupEndOffset = offset + (int)grupSize;
                }

                offset += EsmParser.MainRecordHeaderSize; // Enter GRUP
            }
            else
            {
                var recSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                var flags = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 8)
                    : BinaryUtils.ReadUInt32LE(headerData, 8);
                var formId = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 12)
                    : BinaryUtils.ReadUInt32LE(headerData, 12);

                if (inTargetWorldspace)
                {
                    if (sig == "CELL")
                        cells.Add(new AnalyzerRecordInfo
                        {
                            Signature = sig,
                            Offset = (uint)offset,
                            DataSize = recSize,
                            TotalSize = EsmParser.MainRecordHeaderSize + recSize,
                            FormId = formId,
                            Flags = flags
                        });
                    else if (sig == "LAND")
                        lands.Add(new AnalyzerRecordInfo
                        {
                            Signature = sig,
                            Offset = (uint)offset,
                            DataSize = recSize,
                            TotalSize = EsmParser.MainRecordHeaderSize + recSize,
                            FormId = formId,
                            Flags = flags
                        });
                }

                offset += EsmParser.MainRecordHeaderSize + (int)recSize;

                // Check if we've exited the target worldspace GRUP
                if (inTargetWorldspace && offset >= grupEndOffset) inTargetWorldspace = false;
            }
        }

        return (cells, lands);
    }

    /// <summary>
    ///     Parses VHGT heightmap data.
    /// </summary>
    private static float[,] ParseHeightmapData(byte[] data, bool bigEndian)
    {
        var baseHeight = bigEndian
            ? BitConverter.ToSingle([data[3], data[2], data[1], data[0]], 0)
            : BitConverter.ToSingle(data, 0);

        var heights = new float[EsmConstants.LandGridSize, EsmConstants.LandGridSize];
        var offset = baseHeight * 8f;
        var rowOffset = 0f;

        for (var i = 0; i < EsmConstants.LandGridArea; i++)
        {
            var idx = 4 + i;
            if (idx >= data.Length) continue;

            var value = (sbyte)data[idx] * 8f;
            var r = i / EsmConstants.LandGridSize;
            var c = i % EsmConstants.LandGridSize;

            if (c == 0)
            {
                rowOffset = 0;
                offset += value;
            }
            else
            {
                rowOffset += value;
            }

            heights[c, r] = offset + rowOffset;
        }

        return heights;
    }

    /// <summary>
    ///     Compares two sets of heightmaps and returns cells with significant differences.
    /// </summary>
    private static List<CellHeightDifference> CompareHeightmapData(
        Dictionary<(int x, int y), float[,]> heightmaps1,
        Dictionary<(int x, int y), float[,]> heightmaps2,
        int threshold,
        Dictionary<(int, int), string> cellNames)
    {
        var differences = new List<CellHeightDifference>();

        // Get all cells that exist in both maps
        var commonCells = heightmaps1.Keys.Intersect(heightmaps2.Keys).ToList();

        foreach (var cell in commonCells)
        {
            var h1 = heightmaps1[cell];
            var h2 = heightmaps2[cell];

            var maxDiff = 0f;
            var totalDiff = 0f;
            var totalHeight1 = 0f;
            var totalHeight2 = 0f;
            var diffCount = 0;
            var significantPoints = new List<(int x, int y, float diff)>();

            for (var y = 0; y < EsmConstants.LandGridSize; y++)
                for (var x = 0; x < EsmConstants.LandGridSize; x++)
                {
                    var diff = Math.Abs(h1[x, y] - h2[x, y]);
                    totalHeight1 += h1[x, y];
                    totalHeight2 += h2[x, y];

                    if (diff >= threshold)
                    {
                        diffCount++;
                        totalDiff += diff;
                        if (diff > maxDiff) maxDiff = diff;
                        significantPoints.Add((x, y, diff));
                    }
                }

            if (maxDiff >= threshold)
            {
                var avgHeight1 = totalHeight1 / EsmConstants.LandGridArea;
                var avgHeight2 = totalHeight2 / EsmConstants.LandGridArea;
                var avgDiff = diffCount > 0 ? totalDiff / diffCount : 0;

                // Find the point with maximum difference for more precise teleport
                var maxPoint = significantPoints.OrderByDescending(p => p.diff).FirstOrDefault();

                cellNames.TryGetValue(cell, out var editorId);
                differences.Add(new CellHeightDifference
                {
                    CellX = cell.x,
                    CellY = cell.y,
                    EditorId = editorId,
                    MaxDifference = maxDiff,
                    AvgDifference = avgDiff,
                    DiffPointCount = diffCount,
                    AvgHeight1 = avgHeight1,
                    AvgHeight2 = avgHeight2,
                    MaxDiffLocalX = maxPoint.x,
                    MaxDiffLocalY = maxPoint.y
                });
            }
        }

        // Also report cells that exist in only one file
        var onlyIn1 = heightmaps1.Keys.Except(heightmaps2.Keys).ToList();
        var onlyIn2 = heightmaps2.Keys.Except(heightmaps1.Keys).ToList();

        if (onlyIn1.Count > 0) AnsiConsole.MarkupLine($"[yellow]Cells only in File 1: {onlyIn1.Count}[/]");
        if (onlyIn2.Count > 0) AnsiConsole.MarkupLine($"[yellow]Cells only in File 2: {onlyIn2.Count}[/]");

        return differences;
    }

    /// <summary>
    ///     Shows additional comparison statistics.
    /// </summary>
    private static void ShowComparisonStats(
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
                AnsiConsole.MarkupLine(
                    $"  Largest region: {largestGroup.SizeDescription} at ({largestGroup.MinX},{largestGroup.MinY}) to ({largestGroup.MaxX},{largestGroup.MaxY})");
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>
    ///     Extracts worldspace bounds from the WRLD record's MNAM subrecord.
    /// </summary>
    private static WorldspaceBounds? ExtractWorldspaceBounds(byte[] data, bool bigEndian, uint worldspaceFormId)
    {
        var header = EsmParser.ParseFileHeader(data);
        if (header == null) return null;

        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null) return null;

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        while (offset + 24 <= data.Length)
        {
            var headerData = data.AsSpan(offset);
            var sig = bigEndian
                ? new string([(char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0]])
                : Encoding.ASCII.GetString(data, offset, 4);

            if (sig == "GRUP")
            {
                offset += EsmParser.MainRecordHeaderSize;
                continue;
            }

            var recSize = bigEndian
                ? BinaryUtils.ReadUInt32BE(headerData, 4)
                : BinaryUtils.ReadUInt32LE(headerData, 4);
            var formId = bigEndian
                ? BinaryUtils.ReadUInt32BE(headerData, 12)
                : BinaryUtils.ReadUInt32LE(headerData, 12);

            if (sig == "WRLD" && formId == worldspaceFormId)
            {
                // Found the target worldspace, parse its subrecords
                var recordData = data.AsSpan(offset + EsmParser.MainRecordHeaderSize, (int)recSize);
                var subOffset = 0;

                while (subOffset + 6 <= recordData.Length)
                {
                    var subSig = bigEndian
                        ? new string([
                            (char)recordData[subOffset + 3], (char)recordData[subOffset + 2],
                            (char)recordData[subOffset + 1], (char)recordData[subOffset]
                        ])
                        : Encoding.ASCII.GetString(recordData.Slice(subOffset, 4));
                    var subSize = bigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(recordData.Slice(subOffset + 4))
                        : BinaryPrimitives.ReadUInt16LittleEndian(recordData.Slice(subOffset + 4));

                    if (subSig == "MNAM" && subSize >= 16)
                    {
                        var mnamData = recordData.Slice(subOffset + 6, subSize);
                        // MNAM structure (16 bytes):
                        // int32 usableWidth, int32 usableHeight
                        // int16 nwCellX, int16 nwCellY, int16 seCellX, int16 seCellY
                        var nwCellX = bigEndian
                            ? BinaryPrimitives.ReadInt16BigEndian(mnamData.Slice(8))
                            : BinaryPrimitives.ReadInt16LittleEndian(mnamData.Slice(8));
                        var nwCellY = bigEndian
                            ? BinaryPrimitives.ReadInt16BigEndian(mnamData.Slice(10))
                            : BinaryPrimitives.ReadInt16LittleEndian(mnamData.Slice(10));
                        var seCellX = bigEndian
                            ? BinaryPrimitives.ReadInt16BigEndian(mnamData.Slice(12))
                            : BinaryPrimitives.ReadInt16LittleEndian(mnamData.Slice(12));
                        var seCellY = bigEndian
                            ? BinaryPrimitives.ReadInt16BigEndian(mnamData.Slice(14))
                            : BinaryPrimitives.ReadInt16LittleEndian(mnamData.Slice(14));

                        // NW is top-left (higher Y), SE is bottom-right (lower Y)
                        return new WorldspaceBounds
                        {
                            MinCellX = Math.Min(nwCellX, seCellX),
                            MaxCellX = Math.Max(nwCellX, seCellX),
                            MinCellY = Math.Min(nwCellY, seCellY),
                            MaxCellY = Math.Max(nwCellY, seCellY)
                        };
                    }

                    subOffset += 6 + subSize;
                }

                return null; // WRLD found but no MNAM
            }

            offset += EsmParser.MainRecordHeaderSize + (int)recSize;
        }

        return null;
    }

    /// <summary>
    ///     Generates the output file with console commands.
    /// </summary>
    private static void GenerateHeightmapComparisonOutput(string outputPath, string worldspaceName, string file1Path,
        string file2Path,
        List<CellGroup> inBoundsGroups, List<CellGroup> outOfBoundsGroups, WorldspaceBounds? bounds)
    {
        var sb = new StringBuilder();
        var totalCells = inBoundsGroups.Sum(g => g.Cells.Count) + outOfBoundsGroups.Sum(g => g.Cells.Count);

        sb.AppendLine("================================================================================");
        sb.AppendLine("FALLOUT: NEW VEGAS - TERRAIN DIFFERENCE ANALYSIS");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"File 1 (Proto): {Path.GetFileName(file1Path)}");
        sb.AppendLine($"File 2 (Final): {Path.GetFileName(file2Path)}");
        sb.AppendLine($"Worldspace: {worldspaceName}");
        if (bounds != null)
            sb.AppendLine(
                $"Playable Area: X=[{bounds.MinCellX} to {bounds.MaxCellX}], Y=[{bounds.MinCellY} to {bounds.MaxCellY}]");
        sb.AppendLine();
        sb.AppendLine(
            $"Total differences: {totalCells} cells in {inBoundsGroups.Count + outOfBoundsGroups.Count} contiguous regions");
        sb.AppendLine(
            $"  In playable area: {inBoundsGroups.Count} regions ({inBoundsGroups.Sum(g => g.Cells.Count)} cells)");
        sb.AppendLine(
            $"  Out of bounds: {outOfBoundsGroups.Count} regions ({outOfBoundsGroups.Sum(g => g.Cells.Count)} cells)");
        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("HOW TO USE THESE COMMANDS");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine("1. Open the game console with the ~ (tilde) key");
        sb.AppendLine("2. Copy and paste the command (or type it manually)");
        sb.AppendLine("3. Press Enter to teleport");
        sb.AppendLine();
        sb.AppendLine("Commands:");
        sb.AppendLine("  cow <worldspace> <x> <y>  - Center on World (teleport to cell)");
        sb.AppendLine("  player.setpos x <val>    - Fine-tune X position");
        sb.AppendLine("  player.setpos y <val>    - Fine-tune Y position");
        sb.AppendLine("  player.setpos z <val>    - Adjust height (if stuck underground)");
        sb.AppendLine("  tcl                      - Toggle collision (if stuck)");
        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("IN-BOUNDS TERRAIN REGIONS (playable area)");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        if (inBoundsGroups.Count == 0)
        {
            sb.AppendLine("No terrain differences found within the playable area.");
            sb.AppendLine();
        }
        else
        {
            var rank = 1;
            foreach (var group in inBoundsGroups) AppendGroupEntry(sb, group, worldspaceName, rank++);
        }

        if (outOfBoundsGroups.Count > 0)
        {
            sb.AppendLine("================================================================================");
            sb.AppendLine("OUT-OF-BOUNDS TERRAIN REGIONS (outside playable area)");
            sb.AppendLine("================================================================================");
            sb.AppendLine();

            var rank = 1;
            foreach (var group in outOfBoundsGroups) AppendGroupEntry(sb, group, worldspaceName, rank++);
        }

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        File.WriteAllText(outputPath, sb.ToString());
    }

    private static void AppendGroupEntry(StringBuilder sb, CellGroup group, string worldspaceName, int rank)
    {
        var maxDiffCell = group.MaxDiffCell;

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

        sb.AppendLine(header);
        sb.AppendLine($"Region Size: {group.SizeDescription}");
        sb.AppendLine(
            $"Max Height Difference: {group.MaxDifference:F0} units (at cell {maxDiffCell.CellX}, {maxDiffCell.CellY})");
        sb.AppendLine($"Avg Height Difference: {group.AvgDifference:F0} units");
        sb.AppendLine($"Affected Points: {group.TotalDiffPointCount} / {group.TotalPoints}");
        sb.AppendLine();

        // List cells if more than one
        if (group.Cells.Count > 1)
        {
            sb.AppendLine("Cells in this region:");
            foreach (var cell in group.Cells.OrderByDescending(c => c.MaxDifference))
            {
                var cellName = string.IsNullOrEmpty(cell.EditorId)
                    ? $"({cell.CellX}, {cell.CellY})"
                    : $"{cell.EditorId} ({cell.CellX}, {cell.CellY})";
                sb.AppendLine($"  {cellName}: max diff {cell.MaxDifference:F0}, {cell.DiffPointCount} points");
            }

            sb.AppendLine();
        }

        // Calculate world coordinates for the max diff location
        var (worldX, worldY) = CellUtils.CellToWorldCoordinates(
            maxDiffCell.CellX, maxDiffCell.CellY,
            maxDiffCell.MaxDiffLocalX, maxDiffCell.MaxDiffLocalY);
        var estimatedZ = (int)Math.Max(maxDiffCell.AvgHeight1, maxDiffCell.AvgHeight2) + 500;

        sb.AppendLine("Console Commands:");
        sb.AppendLine($"  cow {worldspaceName} {maxDiffCell.CellX} {maxDiffCell.CellY}");
        sb.AppendLine();
        sb.AppendLine("For precise location of max difference:");
        sb.AppendLine($"  player.setpos x {worldX}");
        sb.AppendLine($"  player.setpos y {worldY}");
        sb.AppendLine($"  player.setpos z {estimatedZ}");
        sb.AppendLine();
    }

    private sealed class HeightmapCellInfo
    {
        public uint FormId { get; init; }
        public int GridX { get; init; }
        public int GridY { get; init; }
        public string? EditorId { get; init; }
        public required AnalyzerRecordInfo CellRecord { get; init; }
    }
}
