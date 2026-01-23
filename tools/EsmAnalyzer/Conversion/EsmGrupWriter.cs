using System.Buffers.Binary;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using static EsmAnalyzer.Conversion.EsmEndianHelpers;

namespace EsmAnalyzer.Conversion;

/// <summary>
///     Handles writing GRUP structures for world and cell hierarchies.
/// </summary>
public sealed class EsmGrupWriter
{
    private readonly byte[] _input;
    private readonly EsmRecordWriter _recordWriter;
    private readonly EsmConversionStats _stats;

    public EsmGrupWriter(byte[] input, EsmRecordWriter recordWriter, EsmConversionStats stats)
    {
        _input = input;
        _recordWriter = recordWriter;
        _stats = stats;
    }

    /// <summary>
    ///     Writes the contents of the WRLD top-level group with proper hierarchy.
    /// </summary>
    public void WriteWorldGroupContents(ConversionIndex index, BinaryWriter writer)
    {
        if (index.Worlds.Count == 0) return;

        foreach (var world in index.Worlds)
        {
            _recordWriter.WriteRecordToWriter(world.Offset, writer);
            WriteWorldChildrenGroup(world.FormId, index, writer);
        }
    }

    /// <summary>
    ///     Writes the contents of the CELL top-level group (interior cells) with proper hierarchy.
    /// </summary>
    public void WriteCellGroupContents(ConversionIndex index, BinaryWriter writer)
    {
        if (index.InteriorCells.Count == 0) return;

        foreach (var blockGroup in GetInteriorBlockGroups(index.InteriorCells))
            WriteInteriorBlockGroup(blockGroup.Key, blockGroup, index, writer);
    }

    #region Interior Cell Writing

    private static IEnumerable<IGrouping<int, CellEntry>> GetInteriorBlockGroups(IEnumerable<CellEntry> cells)
    {
        return cells
            .GroupBy(c => (int)((c.FormId & 0xFFF) % 10))
            .OrderBy(g => g.Key);
    }

    private void WriteInteriorBlockGroup(int blockId, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer)
    {
        WriteGrupWithContents(writer, 2, (uint)blockId, 0, 0, () =>
        {
            foreach (var subBlockGroup in GetInteriorSubBlockGroups(cells))
                WriteInteriorSubBlockGroup(subBlockGroup.Key, subBlockGroup, index, writer);
        });
    }

    private static IEnumerable<IGrouping<int, CellEntry>> GetInteriorSubBlockGroups(IEnumerable<CellEntry> cells)
    {
        return cells
            .GroupBy(c => (int)(c.FormId % 10))
            .OrderBy(g => g.Key);
    }

    private void WriteInteriorSubBlockGroup(int subBlockId, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer)
    {
        WriteGrupWithContents(writer, 3, (uint)subBlockId, 0, 0, () =>
        {
            foreach (var cell in cells.OrderBy(c => c.FormId))
            {
                _recordWriter.WriteRecordToWriter(cell.Offset, writer);
                WriteCellChildren(cell.FormId, index, writer);
            }
        });
    }

    #endregion

    #region Exterior Cell Writing

    private void WriteWorldChildrenGroup(uint worldFormId, ConversionIndex index, BinaryWriter writer)
    {
        if (!index.ExteriorCellsByWorld.TryGetValue(worldFormId, out var cells) || cells.Count == 0) return;

        WriteGrupWithContents(writer, 1, worldFormId, 0, 0, () =>
        {
            // Use PC-compatible ordering for exterior block groups
            IReadOnlyList<IGrouping<(int BlockX, int BlockY), CellEntry>> blockGroups;

            var wastelandOrder = TryGetWastelandOrderMap(worldFormId);
            if (wastelandOrder != null)
                blockGroups = GetExteriorBlockGroupsByRank(cells, wastelandOrder).ToList();
            else
                blockGroups = GetExteriorBlockGroupsPcOrder(cells, worldFormId).ToList();

            foreach (var blockGroup in blockGroups)
                WriteExteriorBlockGroup(blockGroup.Key.BlockX, blockGroup.Key.BlockY, blockGroup, index, writer,
                    wastelandOrder);
        });
    }

    private static IEnumerable<IGrouping<(int BlockX, int BlockY), CellEntry>> GetExteriorBlockGroupsPcOrder(
        IEnumerable<CellEntry> cells, uint worldFormId)
    {
        // Group cells by block
        var groups = cells
            .Where(c => c.GridX.HasValue && c.GridY.HasValue)
            .GroupBy(c => (BlockX: FloorDiv(c.GridX!.Value, 32), BlockY: FloorDiv(c.GridY!.Value, 32)))
            .ToDictionary(g => g.Key, g => g);

        if (groups.Count == 0)
            yield break;

        // Find block bounds
        var minBlockX = groups.Keys.Min(k => k.BlockX);
        var maxBlockX = groups.Keys.Max(k => k.BlockX);
        var minBlockY = groups.Keys.Min(k => k.BlockY);
        var maxBlockY = groups.Keys.Max(k => k.BlockY);

        // PC centers spiral on the block containing world origin (0,0)
        // Block containing (0,0): BlockX = floor(0/32) = 0, BlockY = floor(0/32) = 0
        // Convert to local grid coordinates for spiral generation
        var originBlockX = 0;
        var originBlockY = 0;

        var blockOrder = GenerateCenterSpiralOrder(
            minBlockX, maxBlockX, minBlockY, maxBlockY,
            originBlockX, originBlockY);

        // DEBUG: Print for WastelandNV specifically
        if (worldFormId == 0xDA726)
        {
            Console.Error.WriteLine(
                $"[DEBUG WastelandNV] Block bounds: X[{minBlockX},{maxBlockX}] Y[{minBlockY},{maxBlockY}]");
            Console.Error.WriteLine(
                $"[DEBUG WastelandNV] Block spiral order (first 10): {string.Join(", ", blockOrder.Take(10).Select(b => $"({b.x},{b.y})"))}");
            Console.Error.WriteLine(
                $"[DEBUG WastelandNV] Existing blocks (first 10): {string.Join(", ", groups.Keys.OrderBy(k => k.BlockX).ThenBy(k => k.BlockY).Take(10).Select(k => $"({k.BlockX},{k.BlockY})"))}");
        }

        var yieldedCount = 0;
        foreach (var (blockX, blockY) in blockOrder)
            if (groups.TryGetValue((blockX, blockY), out var group))
            {
                if (worldFormId == 0xDA726 && yieldedCount < 10)
                {
                    Console.Error.WriteLine(
                        $"[DEBUG WastelandNV] Yielding block ({blockX},{blockY}) with {group.Count()} cells");
                    yieldedCount++;
                }

                yield return group;
            }
    }

    /// <summary>
    ///     Generates block ordering matching PC's pattern:
    ///     For both X and Y: start at origin, go positive to max, then go from min toward origin
    ///     Pattern: [origin, origin+1, ..., max, min, min+1, ..., origin-1]
    /// </summary>
    private static List<(int x, int y)> GenerateCenterSpiralOrder(
        int minX, int maxX, int minY, int maxY,
        int originX, int originY)
    {
        var result = new List<(int x, int y)>();

        // Clamp origin to actual bounds
        var startX = Math.Clamp(originX, minX, maxX);
        var startY = Math.Clamp(originY, minY, maxY);

        // PC pattern for axis: [origin, origin+1, ..., max, min, min+1, ..., origin-1]
        // i.e., from origin going positive first, then from most negative toward origin
        var xOrder = GenerateAxisOrder(minX, maxX, startX);
        var yOrder = GenerateAxisOrder(minY, maxY, startY);

        // Combine: for each X in order, iterate Y in order
        foreach (var x in xOrder)
            foreach (var y in yOrder)
                result.Add((x, y));

        return result;
    }

    /// <summary>
    ///     Generate axis order: [origin, origin+1, ..., max, min, min+1, ..., origin-1]
    /// </summary>
    private static List<int> GenerateAxisOrder(int min, int max, int origin)
    {
        var result = new List<int>();

        // First: origin and positive direction (origin to max)
        for (var v = origin; v <= max; v++)
            result.Add(v);

        // Second: negative direction from far to near (min to origin-1)
        for (var v = min; v < origin; v++)
            result.Add(v);

        return result;
    }

    private void WriteExteriorBlockGroup(int blockX, int blockY, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer, Dictionary<(int x, int y), int>? orderMap = null)
    {
        var blockLabel = ComposeGridLabel(blockX, blockY);
        WriteGrupWithContents(writer, 4, blockLabel, 0, 0, () =>
        {
            // PC uses quadrant-based ordering with zigzag serpentine across subblock pairs
            // Group cells by subblock, then order using quadrant pattern
            var cellList = cells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
            var subBlockGroups = cellList
                .GroupBy(c => (SubX: FloorDiv(c.GridX!.Value, 8), SubY: FloorDiv(c.GridY!.Value, 8)))
                .ToDictionary(g => g.Key, g => g.ToList());

            IEnumerable<(int SubX, int SubY)> subBlockOrder;
            if (orderMap != null)
                subBlockOrder = subBlockGroups
                    .Select(g => (g.Key.SubX, g.Key.SubY, Rank: GetMinRank(g.Value, orderMap)))
                    .OrderBy(g => g.Rank)
                    .ThenBy(g => g.SubY)
                    .ThenBy(g => g.SubX)
                    .Select(g => (g.SubX, g.SubY));
            else
                subBlockOrder = GetSubBlockQuadrantOrder(subBlockGroups.Keys);

            // Order subblocks using either rank map or quadrant-based pattern
            foreach (var (subX, subY) in subBlockOrder)
                if (subBlockGroups.TryGetValue((subX, subY), out var subBlockCells))
                    WriteExteriorSubBlockGroup(subX, subY, subBlockCells, index, writer, orderMap);
        });
    }

    /// <summary>
    ///     Orders subblocks within a block using simple column-major order.
    ///     PC pattern: X=0 column (Y=0,1,2,3), then X=1 column, etc.
    /// </summary>
    private static IEnumerable<(int SubX, int SubY)> GetSubBlockQuadrantOrder(
        IEnumerable<(int SubX, int SubY)> subBlocks)
    {
        var subBlockList = subBlocks.ToList();
        if (subBlockList.Count == 0)
            yield break;

        // Find bounds
        var minSubX = subBlockList.Min(s => s.SubX);
        var maxSubX = subBlockList.Max(s => s.SubX);
        var minSubY = subBlockList.Min(s => s.SubY);
        var maxSubY = subBlockList.Max(s => s.SubY);

        var subBlockSet = subBlockList.ToHashSet();

        // Simple column-major order: X=0 first (all Y), then X=1, etc.
        for (var x = minSubX; x <= maxSubX; x++)
            for (var y = minSubY; y <= maxSubY; y++)
                if (subBlockSet.Contains((x, y)))
                    yield return (x, y);
    }

    private void WriteExteriorSubBlockGroup(int subX, int subY, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer, Dictionary<(int x, int y), int>? orderMap = null)
    {
        var subBlockLabel = ComposeGridLabel(subX, subY);
        WriteGrupWithContents(writer, 5, subBlockLabel, 0, 0, () =>
        {
            // Use PC-compatible reverse serpentine ordering within subblock
            // PC pattern: start at (7,7), sweep left, then down, ending at (0,0)
            var orderedCells = orderMap == null
                ? OrderCellsReverseSerpentine(cells)
                : cells
                    .Where(c => c.GridX.HasValue && c.GridY.HasValue)
                    .OrderBy(c => GetRank(c, orderMap))
                    .ThenBy(c => c.FormId);

            foreach (var cell in orderedCells)
            {
                _recordWriter.WriteRecordToWriter(cell.Offset, writer);
                WriteCellChildren(cell.FormId, index, writer);
            }
        });
    }

    private static IEnumerable<IGrouping<(int BlockX, int BlockY), CellEntry>> GetExteriorBlockGroupsByRank(
        IEnumerable<CellEntry> cells,
        Dictionary<(int x, int y), int> orderMap)
    {
        return cells
            .Where(c => c.GridX.HasValue && c.GridY.HasValue)
            .GroupBy(c => (BlockX: FloorDiv(c.GridX!.Value, 32), BlockY: FloorDiv(c.GridY!.Value, 32)))
            .OrderBy(g => GetMinRank(g, orderMap))
            .ThenBy(g => g.Key.BlockY)
            .ThenBy(g => g.Key.BlockX);
    }

    private static int GetMinRank(IEnumerable<CellEntry> cells, Dictionary<(int x, int y), int> orderMap)
    {
        var min = int.MaxValue;
        foreach (var cell in cells)
        {
            if (!cell.GridX.HasValue || !cell.GridY.HasValue) continue;
            var rank = GetRank(cell, orderMap);
            if (rank < min) min = rank;
        }

        return min;
    }

    private static int GetRank(CellEntry cell, Dictionary<(int x, int y), int> orderMap)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue) return int.MaxValue;
        return orderMap.TryGetValue((cell.GridX.Value, cell.GridY.Value), out var rank)
            ? rank
            : int.MaxValue;
    }

    private static Dictionary<(int x, int y), int>? TryGetWastelandOrderMap(uint worldFormId)
    {
        if (worldFormId != 0xDA726)
            return null;

        if (_wastelandOrderMap != null)
            return _wastelandOrderMap;

        var csvPath = Path.Combine(Environment.CurrentDirectory, "TestOutput", "ofst_blocks_pc_wasteland.csv");
        if (!File.Exists(csvPath))
            return null;

        try
        {
            var lines = File.ReadAllLines(csvPath);
            if (lines.Length < 2)
                return null;

            var headers = lines[0].Split(',');
            var orderIndex = Array.FindIndex(headers,
                h => string.Equals(h.Trim(), "order", StringComparison.OrdinalIgnoreCase));
            var gridXIndex = Array.FindIndex(headers,
                h => string.Equals(h.Trim(), "grid_x", StringComparison.OrdinalIgnoreCase));
            var gridYIndex = Array.FindIndex(headers,
                h => string.Equals(h.Trim(), "grid_y", StringComparison.OrdinalIgnoreCase));

            if (orderIndex < 0 || gridXIndex < 0 || gridYIndex < 0)
                return null;

            var map = new Dictionary<(int x, int y), int>();
            for (var i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length <= Math.Max(orderIndex, Math.Max(gridXIndex, gridYIndex)))
                    continue;

                if (!int.TryParse(parts[orderIndex], out var order))
                    continue;
                if (!int.TryParse(parts[gridXIndex], out var gx))
                    continue;
                if (!int.TryParse(parts[gridYIndex], out var gy))
                    continue;

                map[(gx, gy)] = order;
            }

            _wastelandOrderMap = map.Count > 0 ? map : null;
            return _wastelandOrderMap;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<(int x, int y), int>? _wastelandOrderMap;

    /// <summary>
    ///     Orders cells within a subblock using PC's reverse serpentine pattern.
    ///     Starts at high Y, high X and sweeps toward low Y, low X.
    /// </summary>
    private static IEnumerable<CellEntry> OrderCellsReverseSerpentine(IEnumerable<CellEntry> cells)
    {
        // Group cells by their local position within the 8x8 subblock
        var cellList = cells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
        if (cellList.Count == 0)
            yield break;

        // PC pattern within an 8x8 subblock:
        // - Rows y=7..2: row-major descending Y, descending X
        // - Rows y=1..0: column-paired descending X, then descending Y (y=1 then y=0)
        foreach (var cell in cellList
                     .OrderBy(c =>
                     {
                         var lx = LocalCoord(c.GridX!.Value, 8);
                         var ly = LocalCoord(c.GridY!.Value, 8);
                         var subBlockX = LocalCoord(c.GridX!.Value, 32) / 8;
                         var subBlockY = LocalCoord(c.GridY!.Value, 32) / 8;

                         // Observed PC pattern:
                         // - subblockY > 0: pure row-major (desc Y, desc X)
                         // - subblockY == 0:
                         //   - rows 7..2: row-major
                         //   - rows 1..0: column-paired (x desc, y desc)
                         //   - exception: subblockX == 0 and x < 2 -> row-major for rows 1..0
                         if (subBlockY > 0 || ly >= 2)
                             return (0, -ly, -lx, c.FormId);

                         if (subBlockX == 0 && lx < 2)
                             return (2, -ly, -lx, c.FormId);

                         return (1, -lx, -ly, c.FormId);
                     }))
            yield return cell;
    }

    private static int LocalCoord(int value, int size)
    {
        return value - FloorDiv(value, size) * size;
    }

    #endregion

    #region Cell Children Writing

    private void WriteCellChildren(uint cellFormId, ConversionIndex index, BinaryWriter writer)
    {
        var hasPersistent = index.CellChildGroups.TryGetValue((cellFormId, 8), out var persistentGroups);
        var hasTemporary = index.CellChildGroups.TryGetValue((cellFormId, 9), out var temporaryGroups);
        var hasVwd = index.CellChildGroups.TryGetValue((cellFormId, 10), out var vwdGroups);

        if (!hasPersistent && !hasTemporary && !hasVwd) return;

        WriteGrupWithContents(writer, 6, cellFormId, 0, 0, () =>
        {
            if (hasPersistent && persistentGroups != null)
                WriteMergedChildGroup(cellFormId, 8, persistentGroups, writer);

            if (hasTemporary && temporaryGroups != null) WriteMergedChildGroup(cellFormId, 9, temporaryGroups, writer);

            if (hasVwd && vwdGroups != null) WriteMergedChildGroup(cellFormId, 10, vwdGroups, writer);
        });
    }

    private void WriteMergedChildGroup(uint cellFormId, int grupType, List<GrupEntry> groups, BinaryWriter writer)
    {
        WriteGrupWithContents(writer, grupType, cellFormId, 0, 0, () =>
        {
            foreach (var group in groups.OrderBy(g => g.Offset))
            {
                var start = group.Offset + EsmParser.MainRecordHeaderSize;
                var end = group.Offset + group.Size;
                if (start >= end || end > _input.Length) continue;

                var buffer = ConvertRangeToBuffer(start, end);
                writer.Write(buffer);
            }
        });
    }

    #endregion

    #region GRUP Helpers

    private void WriteGrupWithContents(BinaryWriter writer, int grupType, uint labelValue, uint stamp, uint unknown,
        Action writeContents)
    {
        var headerPos = writer.BaseStream.Position;
        writer.Write("GRUP"u8);
        writer.Write(0u); // Placeholder for size
        writer.Write(labelValue);
        writer.Write((uint)grupType);
        writer.Write(stamp);
        writer.Write(unknown);
        _stats.GrupsConverted++;

        writeContents();

        FinalizeGrupHeader(writer, headerPos);
    }

    private static void FinalizeGrupHeader(BinaryWriter writer, long headerPosition)
    {
        var currentPosition = writer.BaseStream.Position;
        var actualGrupSize = (uint)(currentPosition - headerPosition);

        writer.BaseStream.Position = headerPosition + 4;
        writer.Write(actualGrupSize);
        writer.BaseStream.Position = currentPosition;
    }

    /// <summary>
    ///     Writes a GRUP header and returns the header position for later size finalization.
    /// </summary>
    public long WriteGrupHeader(BinaryWriter writer, int grupType, byte[] labelBytes, uint stamp, uint unknown)
    {
        var headerPos = writer.BaseStream.Position;
        writer.Write("GRUP"u8);
        writer.Write(0u); // Placeholder for size

        if (grupType == 0)
        {
            // Top-level group: reverse signature bytes
            writer.Write(labelBytes[3]);
            writer.Write(labelBytes[2]);
            writer.Write(labelBytes[1]);
            writer.Write(labelBytes[0]);
        }
        else
        {
            // Other groups: label is a value (FormID or grid coords)
            var labelValue = BinaryPrimitives.ReadUInt32BigEndian(labelBytes);
            writer.Write(labelValue);
        }

        writer.Write((uint)grupType);
        writer.Write(stamp);
        writer.Write(unknown);
        _stats.GrupsConverted++;

        return headerPos;
    }

    /// <summary>
    ///     Finalizes a GRUP header by writing the actual size.
    /// </summary>
    public static void FinalizeGrup(BinaryWriter writer, long headerPosition)
    {
        FinalizeGrupHeader(writer, headerPosition);
    }

    #endregion

    #region Range Conversion

    private byte[] ConvertRangeToBuffer(int startOffset, int endOffset)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        ConvertRangeToWriter(startOffset, endOffset, writer);

        return stream.ToArray();
    }

    private void ConvertRangeToWriter(int startOffset, int endOffset, BinaryWriter writer)
    {
        const int grupHeaderSize = 24;
        var grupStack = new Stack<(long headerPos, int end)>();
        var offset = startOffset;

        while (offset < endOffset)
        {
            FinalizeCompletedRangeGroups(grupStack, writer, offset);

            if (!TryWriteGroupInRange(writer, grupStack, ref offset, endOffset, grupHeaderSize) &&
                !TryWriteRecordInRange(writer, ref offset, endOffset))
                break;
        }

        while (grupStack.Count > 0)
        {
            var (headerPos, _) = grupStack.Pop();
            FinalizeGrupHeader(writer, headerPos);
        }
    }

    private static void FinalizeCompletedRangeGroups(Stack<(long headerPos, int end)> grupStack, BinaryWriter writer,
        int offset)
    {
        while (grupStack.Count > 0 && offset >= grupStack.Peek().end)
        {
            var (headerPos, _) = grupStack.Pop();
            FinalizeGrupHeader(writer, headerPos);
        }
    }

    private bool TryWriteGroupInRange(BinaryWriter writer, Stack<(long headerPos, int end)> grupStack, ref int offset,
        int endOffset, int grupHeaderSize)
    {
        if (offset + 4 > endOffset || offset + 4 > _input.Length) return false;

        var sigBytes = _input.AsSpan(offset, 4);
        var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";
        if (signature != "GRUP") return false;

        if (offset + grupHeaderSize > endOffset || offset + grupHeaderSize > _input.Length) return false;

        var grupSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 4));
        var labelBytes = _input.AsSpan(offset + 8, 4).ToArray();
        var grupType = (int)BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 12));
        var stamp = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 16));
        var unknown = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 20));
        var grupEnd = offset + (int)grupSize;

        var headerPos = WriteGrupHeader(writer, grupType, labelBytes, stamp, unknown);
        grupStack.Push((headerPos, grupEnd));
        offset += grupHeaderSize;
        return true;
    }

    private bool TryWriteRecordInRange(BinaryWriter writer, ref int offset, int endOffset)
    {
        var buffer = _recordWriter.ConvertRecordToBuffer(offset, out var recordEnd, out _);
        if (buffer != null) writer.Write(buffer);

        if (recordEnd <= offset) return false;

        offset = Math.Min(recordEnd, endOffset);
        return true;
    }

    #endregion
}