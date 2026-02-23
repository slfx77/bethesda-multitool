using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using Spectre.Console;
using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Heightmap data extraction, parsing, and comparison logic for ESM files.
/// </summary>
internal static class HeightmapDataParser
{
    /// <summary>
    ///     Extracts all heightmaps for a worldspace from ESM data.
    /// </summary>
    internal static (Dictionary<(int x, int y), float[,]> heightmaps, Dictionary<(int x, int y), string> cellNames)
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
                {
                    try
                    {
                        var recordData = EsmHelpers.GetRecordData(data, cell, bigEndian);
                        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

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
                            {
                                cellNames[(gridX, gridY)] = editorId;
                            }
                        }
                    }
                    catch
                    {
                        // Skip cells that fail to parse
                    }
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
                        {
                            break;
                        }

                        try
                        {
                            var recordData = EsmHelpers.GetRecordData(data, land, bigEndian);
                            var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

                            var vhgt = subrecords.FirstOrDefault(s => s.Signature == "VHGT");
                            if (vhgt != null && vhgt.Data.Length >= 4 + EsmConstants.LandGridArea)
                            {
                                var heights = ParseHeightmapData(vhgt.Data, bigEndian);
                                if (!heightmaps.ContainsKey((cell.GridX, cell.GridY)))
                                {
                                    heightmaps[(cell.GridX, cell.GridY)] = heights;
                                }
                            }

                            _ = sortedLands.Remove(land);
                            break;
                        }
                        catch
                        {
                            _ = sortedLands.Remove(land);
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
        if (header == null)
        {
            return (cells, lands);
        }

        // Skip TES4 header
        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null)
        {
            return (cells, lands);
        }

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
                    {
                        cells.Add(new AnalyzerRecordInfo
                        {
                            Signature = sig,
                            Offset = (uint)offset,
                            DataSize = recSize,
                            TotalSize = EsmParser.MainRecordHeaderSize + recSize,
                            FormId = formId,
                            Flags = flags
                        });
                    }
                    else if (sig == "LAND")
                    {
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
                }

                offset += EsmParser.MainRecordHeaderSize + (int)recSize;

                // Check if we've exited the target worldspace GRUP
                if (inTargetWorldspace && offset >= grupEndOffset)
                {
                    inTargetWorldspace = false;
                }
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
            if (idx >= data.Length)
            {
                continue;
            }

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
    internal static List<CellHeightDifference> CompareHeightmapData(
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
            {
                for (var x = 0; x < EsmConstants.LandGridSize; x++)
                {
                    var diff = Math.Abs(h1[x, y] - h2[x, y]);
                    totalHeight1 += h1[x, y];
                    totalHeight2 += h2[x, y];

                    if (diff >= threshold)
                    {
                        diffCount++;
                        totalDiff += diff;
                        if (diff > maxDiff)
                        {
                            maxDiff = diff;
                        }

                        significantPoints.Add((x, y, diff));
                    }
                }
            }

            if (maxDiff >= threshold)
            {
                var avgHeight1 = totalHeight1 / EsmConstants.LandGridArea;
                var avgHeight2 = totalHeight2 / EsmConstants.LandGridArea;
                var avgDiff = diffCount > 0 ? totalDiff / diffCount : 0;

                // Find the point with maximum difference for more precise teleport
                var (x, y, diff) = significantPoints.OrderByDescending(p => p.diff).FirstOrDefault();

                _ = cellNames.TryGetValue(cell, out var editorId);
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
                    MaxDiffLocalX = x,
                    MaxDiffLocalY = y
                });
            }
        }

        // Also report cells that exist in only one file
        var onlyIn1 = heightmaps1.Keys.Except(heightmaps2.Keys).ToList();
        var onlyIn2 = heightmaps2.Keys.Except(heightmaps1.Keys).ToList();

        if (onlyIn1.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Cells only in File 1: {onlyIn1.Count}[/]");
        }

        if (onlyIn2.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Cells only in File 2: {onlyIn2.Count}[/]");
        }

        return differences;
    }

    /// <summary>
    ///     Extracts worldspace bounds from the WRLD record's MNAM subrecord.
    /// </summary>
    internal static WorldspaceBounds? ExtractWorldspaceBounds(byte[] data, bool bigEndian, uint worldspaceFormId)
    {
        var header = EsmParser.ParseFileHeader(data);
        if (header == null)
        {
            return null;
        }

        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null)
        {
            return null;
        }

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
                        ? BinaryPrimitives.ReadUInt16BigEndian(recordData[(subOffset + 4)..])
                        : BinaryPrimitives.ReadUInt16LittleEndian(recordData[(subOffset + 4)..]);

                    if (subSig == "MNAM" && subSize >= 16)
                    {
                        var mnamData = recordData.Slice(subOffset + 6, subSize);
                        // MNAM structure (16 bytes):
                        // int32 usableWidth, int32 usableHeight
                        // int16 nwCellX, int16 nwCellY, int16 seCellX, int16 seCellY
                        var nwCellX = bigEndian
                            ? BinaryPrimitives.ReadInt16BigEndian(mnamData[8..])
                            : BinaryPrimitives.ReadInt16LittleEndian(mnamData[8..]);
                        var nwCellY = bigEndian
                            ? BinaryPrimitives.ReadInt16BigEndian(mnamData[10..])
                            : BinaryPrimitives.ReadInt16LittleEndian(mnamData[10..]);
                        var seCellX = bigEndian
                            ? BinaryPrimitives.ReadInt16BigEndian(mnamData[12..])
                            : BinaryPrimitives.ReadInt16LittleEndian(mnamData[12..]);
                        var seCellY = bigEndian
                            ? BinaryPrimitives.ReadInt16BigEndian(mnamData[14..])
                            : BinaryPrimitives.ReadInt16LittleEndian(mnamData[14..]);

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

    // ──────────────────────────────────────────────────────────────────────
    //  Internal types
    // ──────────────────────────────────────────────────────────────────────

    internal sealed class HeightmapCellInfo
    {
        public uint FormId { get; init; }
        public int GridX { get; init; }
        public int GridY { get; init; }
        public string? EditorId { get; init; }
        public required AnalyzerRecordInfo CellRecord { get; init; }
    }
}
