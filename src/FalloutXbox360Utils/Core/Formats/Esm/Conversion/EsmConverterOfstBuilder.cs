using System.Buffers.Binary;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     Rebuilds OFST (cell offset) tables in converted ESM output.
///     The OFST subrecord inside each WRLD record maps grid positions to CELL record offsets.
/// </summary>
internal static class EsmConverterOfstBuilder
{
    /// <summary>
    ///     Rebuilds all OFST tables in the converted output using the conversion index.
    /// </summary>
    public static void RebuildOfstTables(byte[] output, ConversionIndex index, bool verbose)
    {
        var outputHeader = EsmParser.ParseFileHeader(output);
        if (outputHeader == null || outputHeader.IsBigEndian)
        {
            return;
        }

        var outputRecords = EsmRecordParser.ScanAllRecords(output, outputHeader.IsBigEndian);
        var outputWrlds = outputRecords
            .Where(r => r.Signature == "WRLD")
            .ToList();

        var cellRecordOffsets = outputRecords
            .Where(r => r.Signature == "CELL")
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Offset).First().Offset);

        var outputExteriorCellsByWorld = BuildExteriorCellsByWorldFromOutput(output, outputHeader.IsBigEndian);
        var fallbackExteriorCells = BuildExteriorCellsFromAllCells(outputRecords, output, outputHeader.IsBigEndian);
        var indexExteriorCellsByWorld = index.ExteriorCellsByWorld
            .Where(kvp => kvp.Value.Count > 0)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
                    .Where(c => c.GridX.HasValue && c.GridY.HasValue)
                    .Select(c => new CellGrid(c.FormId, c.GridX!.Value, c.GridY!.Value))
                    .ToList());

        if (verbose)
        {
            Console.WriteLine(
                $"OFST Rebuild: {outputWrlds.Count} WRLD records, {cellRecordOffsets.Count} CELL records");
            Console.WriteLine($"  outputExteriorCellsByWorld entries: {outputExteriorCellsByWorld.Count}");
            foreach (var kvp in outputExteriorCellsByWorld.Take(3))
            {
                Console.WriteLine($"    World 0x{kvp.Key:X8}: {kvp.Value.Count} cells");
            }

            Console.WriteLine($"  fallbackExteriorCells: {fallbackExteriorCells.Count}");
        }

        foreach (var wrld in outputWrlds)
        {
            RebuildOfstForWorld(output, wrld, outputHeader.IsBigEndian,
                indexExteriorCellsByWorld, outputExteriorCellsByWorld,
                fallbackExteriorCells, cellRecordOffsets);
        }
    }

    /// <summary>
    ///     Rebuilds the OFST (cell offset table) for a single WRLD record.
    /// </summary>
    private static void RebuildOfstForWorld(
        byte[] output,
        AnalyzerRecordInfo wrld,
        bool bigEndian,
        Dictionary<uint, List<CellGrid>> indexExteriorCellsByWorld,
        Dictionary<uint, List<CellGrid>> outputExteriorCellsByWorld,
        List<CellGrid> fallbackExteriorCells,
        Dictionary<uint, uint> cellRecordOffsets)
    {
        var exteriorCells = ResolveExteriorCellsForWorld(
            wrld.FormId, indexExteriorCellsByWorld, outputExteriorCellsByWorld, fallbackExteriorCells);

        if (exteriorCells.Count == 0 || (wrld.Flags & 0x00040000) != 0)
        {
            return;
        }

        var wrldData = EsmHelpers.GetRecordData(output, wrld, bigEndian);
        var subs = EsmRecordParser.ParseSubrecords(wrldData, bigEndian);
        var ofst = subs.FirstOrDefault(s => s.Signature == "OFST");
        if (ofst == null || ofst.Data.Length == 0)
        {
            return;
        }

        if (!TryGetWorldBounds(subs, bigEndian, out var minX, out var maxX, out var minY, out var maxY))
        {
            return;
        }

        if (!TryCalculateGridDimensions(ofst.Data.Length, maxX - minX + 1, maxY - minY + 1,
                out var columns, out var rows, out var count))
        {
            return;
        }

        var offsets = BuildOfstOffsetArray(exteriorCells, cellRecordOffsets, wrld.Offset,
            minX, minY, columns, rows, count);

        var ofstDataOffset = (long)wrld.Offset + EsmParser.MainRecordHeaderSize + ofst.Offset + 6;
        if (ofstDataOffset < 0 || ofstDataOffset + (long)count * 4 > output.Length)
        {
            return;
        }

        for (var i = 0; i < offsets.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan((int)ofstDataOffset + i * 4, 4),
                offsets[i]);
        }
    }

    /// <summary>
    ///     Resolves the exterior cells for a world by merging index, output, and fallback sources.
    /// </summary>
    private static List<CellGrid> ResolveExteriorCellsForWorld(
        uint worldFormId,
        Dictionary<uint, List<CellGrid>> indexExteriorCellsByWorld,
        Dictionary<uint, List<CellGrid>> outputExteriorCellsByWorld,
        List<CellGrid> fallbackExteriorCells)
    {
        if (!indexExteriorCellsByWorld.TryGetValue(worldFormId, out var exteriorCells)
            && !outputExteriorCellsByWorld.TryGetValue(worldFormId, out exteriorCells))
        {
            exteriorCells = [];
        }

        if (fallbackExteriorCells.Count == 0 && exteriorCells.Count == 0)
        {
            return [];
        }

        if (fallbackExteriorCells.Count > 0)
        {
            var merged = new Dictionary<uint, CellGrid>();
            foreach (var cell in exteriorCells)
            {
                merged[cell.FormId] = cell;
            }

            foreach (var cell in fallbackExteriorCells)
            {
                _ = merged.TryAdd(cell.FormId, cell);
            }

            return merged.Values.ToList();
        }

        return exteriorCells;
    }

    /// <summary>
    ///     Calculates grid dimensions from OFST data, adjusting if the expected count doesn't match.
    /// </summary>
    private static bool TryCalculateGridDimensions(
        int ofstDataLength, int initialColumns, int initialRows,
        out int columns, out int rows, out int count)
    {
        columns = initialColumns;
        rows = initialRows;
        count = ofstDataLength / 4;

        if (columns <= 0 || rows <= 0 || count <= 0)
        {
            return false;
        }

        var expected = columns * rows;
        if (expected == count)
        {
            return true;
        }

        if (count % columns == 0)
        {
            rows = count / columns;
        }
        else if (count % rows == 0)
        {
            columns = count / rows;
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Builds the OFST offset array mapping grid positions to cell record offsets relative to the WRLD record.
    /// </summary>
    private static uint[] BuildOfstOffsetArray(
        List<CellGrid> exteriorCells,
        Dictionary<uint, uint> cellRecordOffsets,
        uint wrldOffset,
        int minX, int minY,
        int columns, int rows, int count)
    {
        var offsets = new uint[count];
        var bestByIndex = new Dictionary<int, uint>();

        foreach (var cell in exteriorCells)
        {
            var col = cell.GridX - minX;
            var row = cell.GridY - minY;
            if (col < 0 || col >= columns || row < 0 || row >= rows)
            {
                continue;
            }

            var ofstIndex = row * columns + col;
            if (ofstIndex < 0 || ofstIndex >= count)
            {
                continue;
            }

            if (!cellRecordOffsets.TryGetValue(cell.FormId, out var cellOffset))
            {
                continue;
            }

            var rel = cellOffset - (long)wrldOffset;
            if (rel is <= 0 or > uint.MaxValue)
            {
                continue;
            }

            var relValue = (uint)rel;
            if (!bestByIndex.TryGetValue(ofstIndex, out var existing) || relValue < existing)
            {
                bestByIndex[ofstIndex] = relValue;
                offsets[ofstIndex] = relValue;
            }
        }

        return offsets;
    }

    private static bool TryGetWorldBounds(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian,
        out int minX, out int maxX, out int minY, out int maxY)
    {
        minX = 0;
        maxX = 0;
        minY = 0;
        maxY = 0;

        var nam0 = subrecords.FirstOrDefault(s => s.Signature == "NAM0");
        var nam9 = subrecords.FirstOrDefault(s => s.Signature == "NAM9");
        if (nam0 == null || nam9 == null || nam0.Data.Length < 8 || nam9.Data.Length < 8)
        {
            return false;
        }

        var minXf = ReadFloat(nam0.Data, 0, bigEndian);
        var minYf = ReadFloat(nam0.Data, 4, bigEndian);
        var maxXf = ReadFloat(nam9.Data, 0, bigEndian);
        var maxYf = ReadFloat(nam9.Data, 4, bigEndian);

        if (IsUnsetFloat(minXf))
        {
            minXf = 0;
        }

        if (IsUnsetFloat(minYf))
        {
            minYf = 0;
        }

        if (IsUnsetFloat(maxXf))
        {
            maxXf = 0;
        }

        if (IsUnsetFloat(maxYf))
        {
            maxYf = 0;
        }

        const float cellScale = 4096f;
        minX = (int)Math.Round(minXf / cellScale);
        minY = (int)Math.Round(minYf / cellScale);
        maxX = (int)Math.Round(maxXf / cellScale);
        maxY = (int)Math.Round(maxYf / cellScale);

        return true;
    }

    private static float ReadFloat(byte[] data, int offset, bool bigEndian)
    {
        if (offset + 4 > data.Length)
        {
            return 0;
        }

        var value = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        return BitConverter.Int32BitsToSingle((int)value);
    }

    private static bool IsUnsetFloat(float value)
    {
        const float unsetFloatThreshold = 1e20f;
        return float.IsNaN(value) || value <= -unsetFloatThreshold || value >= unsetFloatThreshold;
    }

    private static Dictionary<uint, List<CellGrid>> BuildExteriorCellsByWorldFromOutput(byte[] data, bool bigEndian)
    {
        var map = new Dictionary<uint, List<CellGrid>>();
        var header = EsmParser.ParseFileHeader(data);
        if (header == null)
        {
            return map;
        }

        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null)
        {
            return map;
        }

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;
        var stack = new Stack<(int end, int type, uint label)>();

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            while (stack.Count > 0 && offset >= stack.Peek().end)
            {
                _ = stack.Pop();
            }

            var groupHeader = EsmParser.ParseGroupHeader(data.AsSpan(offset), bigEndian);
            if (groupHeader != null)
            {
                var groupEnd = offset + (int)groupHeader.GroupSize;
                if (groupEnd <= offset || groupEnd > data.Length)
                {
                    break;
                }

                var labelValue = BinaryPrimitives.ReadUInt32LittleEndian(groupHeader.Label);
                stack.Push((groupEnd, groupHeader.GroupType, labelValue));
                offset += EsmParser.MainRecordHeaderSize;
                continue;
            }

            var recordHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recordHeader == null)
            {
                break;
            }

            if (recordHeader.Signature == "CELL")
            {
                var worldId = GetCurrentWorldId(stack);
                if (worldId.HasValue)
                {
                    var recInfo = new AnalyzerRecordInfo
                    {
                        Signature = recordHeader.Signature,
                        FormId = recordHeader.FormId,
                        Flags = recordHeader.Flags,
                        DataSize = recordHeader.DataSize,
                        Offset = (uint)offset,
                        TotalSize = EsmParser.MainRecordHeaderSize + recordHeader.DataSize
                    };

                    var recordData = EsmHelpers.GetRecordData(data, recInfo, bigEndian);
                    var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
                    var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");

                    if (xclc != null && xclc.Data.Length >= 8)
                    {
                        var gridX = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0, 4));
                        var gridY = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4, 4));

                        if (!map.TryGetValue(worldId.Value, out var list))
                        {
                            list = [];
                            map[worldId.Value] = list;
                        }

                        list.Add(new CellGrid(recordHeader.FormId, gridX, gridY));
                    }
                }
            }

            offset += EsmParser.MainRecordHeaderSize + (int)recordHeader.DataSize;
        }

        return map;
    }

    private static List<CellGrid> BuildExteriorCellsFromAllCells(
        List<AnalyzerRecordInfo> records,
        byte[] data,
        bool bigEndian)
    {
        var list = new List<CellGrid>();

        foreach (var record in records)
        {
            if (record.Signature != "CELL")
            {
                continue;
            }

            var recordData = EsmHelpers.GetRecordData(data, record, bigEndian);
            var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
            var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
            if (xclc == null || xclc.Data.Length < 8)
            {
                continue;
            }

            var gridX = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0, 4));
            var gridY = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4, 4));

            list.Add(new CellGrid(record.FormId, gridX, gridY));
        }

        return list;
    }

    private static uint? GetCurrentWorldId(Stack<(int end, int type, uint label)> stack)
    {
        foreach (var (_, type, label) in stack.Reverse())
        {
            if (type == 1)
            {
                return label;
            }
        }

        return null;
    }

    internal sealed record CellGrid(uint FormId, int GridX, int GridY);
}
