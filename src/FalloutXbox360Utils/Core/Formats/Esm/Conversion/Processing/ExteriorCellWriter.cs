using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Indexing;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using static FalloutXbox360Utils.Core.Formats.Esm.Conversion.EsmEndianHelpers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;

/// <summary>
///     Handles writing exterior cell GRUP structures (world children, block/sub-block groups)
///     for the ESM conversion pipeline.
/// </summary>
internal sealed class ExteriorCellWriter(EsmGrupWriter grupWriter, EsmRecordWriter recordWriter)
{
    private static Dictionary<(int x, int y), int>? _wastelandOrderMap;
    private readonly EsmGrupWriter _grupWriter = grupWriter;
    private readonly EsmRecordWriter _recordWriter = recordWriter;

    /// <summary>
    ///     Writes the World Children group for a single worldspace, including the persistent cell
    ///     and all exterior cell block/sub-block hierarchies.
    /// </summary>
    internal void WriteWorldChildrenGroup(uint worldFormId, ConversionIndex index, BinaryWriter writer)
    {
        if (!index.ExteriorCellsByWorld.TryGetValue(worldFormId, out var cells) || cells.Count == 0)
        {
            // World Children may still contain the worldspace persistent cell even when there are no exterior cells.
            if (!index.WorldPersistentCellsByWorld.TryGetValue(worldFormId, out var persistentCell))
            {
                return;
            }

            _grupWriter.WriteGrupWithContents(writer, 1, worldFormId, 0, 0, () =>
            {
                _recordWriter.WriteRecordToWriter(persistentCell.Offset, writer);
                _grupWriter.WriteCellChildren(persistentCell.FormId, index, writer);
            });

            return;
        }

        _grupWriter.WriteGrupWithContents(writer, 1, worldFormId, 0, 0, () =>
        {
            // PC writes a worldspace persistent CELL (and its children) directly under World Children,
            // before any exterior cell block/sub-block groups.
            if (index.WorldPersistentCellsByWorld.TryGetValue(worldFormId, out var persistentCell))
            {
                _recordWriter.WriteRecordToWriter(persistentCell.Offset, writer);
                _grupWriter.WriteCellChildren(persistentCell.FormId, index, writer);
            }

            // Use PC-compatible ordering for exterior block groups
            IReadOnlyList<IGrouping<(int BlockX, int BlockY), CellEntry>> blockGroups;

            var wastelandOrder = TryGetWastelandOrderMap(worldFormId);
            blockGroups = wastelandOrder != null
                ? GetExteriorBlockGroupsByRank(cells, wastelandOrder).ToList()
                : GetExteriorBlockGroupsPcOrder(cells, worldFormId).ToList();

            foreach (var blockGroup in blockGroups)
            {
                WriteExteriorBlockGroup(blockGroup.Key.BlockX, blockGroup.Key.BlockY, blockGroup, index, writer,
                    wastelandOrder);
            }
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
        {
            yield break;
        }

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

        var blockOrder = CellSpiralOrderGenerator.GenerateCenterSpiralOrder(
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
        {
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
    }

    private void WriteExteriorBlockGroup(int blockX, int blockY, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer, Dictionary<(int x, int y), int>? orderMap = null)
    {
        var blockLabel = ComposeGridLabel(blockX, blockY);
        _grupWriter.WriteGrupWithContents(writer, 4, blockLabel, 0, 0, () =>
        {
            // PC uses quadrant-based ordering with zigzag serpentine across subblock pairs
            // Group cells by subblock, then order using quadrant pattern
            var cellList = cells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
            var subBlockGroups = cellList
                .GroupBy(c => (SubX: FloorDiv(c.GridX!.Value, 8), SubY: FloorDiv(c.GridY!.Value, 8)))
                .ToDictionary(g => g.Key, g => g.ToList());

            var subBlockOrder = orderMap != null
                ? subBlockGroups
                    .Select(g => (g.Key.SubX, g.Key.SubY, Rank: GetMinRank(g.Value, orderMap)))
                    .OrderBy(g => g.Rank)
                    .ThenBy(g => g.SubY)
                    .ThenBy(g => g.SubX)
                    .Select(g => (g.SubX, g.SubY))
                : GetSubBlockQuadrantOrder(subBlockGroups.Keys);

            // Order subblocks using either rank map or quadrant-based pattern
            foreach (var (subX, subY) in subBlockOrder)
            {
                if (subBlockGroups.TryGetValue((subX, subY), out var subBlockCells))
                {
                    WriteExteriorSubBlockGroup(subX, subY, subBlockCells, index, writer, orderMap);
                }
            }
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
        {
            yield break;
        }

        // Find bounds
        var minSubX = subBlockList.Min(s => s.SubX);
        var maxSubX = subBlockList.Max(s => s.SubX);
        var minSubY = subBlockList.Min(s => s.SubY);
        var maxSubY = subBlockList.Max(s => s.SubY);

        var subBlockSet = subBlockList.ToHashSet();

        // Simple column-major order: X=0 first (all Y), then X=1, etc.
        for (var x = minSubX; x <= maxSubX; x++)
        {
            for (var y = minSubY; y <= maxSubY; y++)
            {
                if (subBlockSet.Contains((x, y)))
                {
                    yield return (x, y);
                }
            }
        }
    }

    private void WriteExteriorSubBlockGroup(int subX, int subY, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer, Dictionary<(int x, int y), int>? orderMap = null)
    {
        var subBlockLabel = ComposeGridLabel(subX, subY);
        _grupWriter.WriteGrupWithContents(writer, 5, subBlockLabel, 0, 0, () =>
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
                _grupWriter.WriteCellChildren(cell.FormId, index, writer);
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
            if (!cell.GridX.HasValue || !cell.GridY.HasValue)
            {
                continue;
            }

            var rank = GetRank(cell, orderMap);
            if (rank < min)
            {
                min = rank;
            }
        }

        return min;
    }

    private static int GetRank(CellEntry cell, Dictionary<(int x, int y), int> orderMap)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            return int.MaxValue;
        }

        return orderMap.TryGetValue((cell.GridX.Value, cell.GridY.Value), out var rank)
            ? rank
            : int.MaxValue;
    }

    private static Dictionary<(int x, int y), int>? TryGetWastelandOrderMap(uint worldFormId)
    {
        if (worldFormId != 0xDA726)
        {
            return null;
        }

        if (_wastelandOrderMap != null)
        {
            return _wastelandOrderMap;
        }

        var csvPath = Path.Combine(Environment.CurrentDirectory, "TestOutput", "ofst_blocks_pc_wasteland.csv");
        if (!File.Exists(csvPath))
        {
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(csvPath);
            if (lines.Length < 2)
            {
                return null;
            }

            var headers = lines[0].Split(',');
            var orderIndex = Array.FindIndex(headers,
                h => string.Equals(h.Trim(), "order", StringComparison.OrdinalIgnoreCase));
            var gridXIndex = Array.FindIndex(headers,
                h => string.Equals(h.Trim(), "grid_x", StringComparison.OrdinalIgnoreCase));
            var gridYIndex = Array.FindIndex(headers,
                h => string.Equals(h.Trim(), "grid_y", StringComparison.OrdinalIgnoreCase));

            if (orderIndex < 0 || gridXIndex < 0 || gridYIndex < 0)
            {
                return null;
            }

            var map = new Dictionary<(int x, int y), int>();
            for (var i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length <= Math.Max(orderIndex, Math.Max(gridXIndex, gridYIndex)))
                {
                    continue;
                }

                if (!int.TryParse(parts[orderIndex], out var order))
                {
                    continue;
                }

                if (!int.TryParse(parts[gridXIndex], out var gx))
                {
                    continue;
                }

                if (!int.TryParse(parts[gridYIndex], out var gy))
                {
                    continue;
                }

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

    /// <summary>
    ///     Orders cells within a subblock using PC's reverse serpentine pattern.
    ///     Starts at high Y, high X and sweeps toward low Y, low X.
    /// </summary>
    private static IEnumerable<CellEntry> OrderCellsReverseSerpentine(IEnumerable<CellEntry> cells)
    {
        // Group cells by their local position within the 8x8 subblock
        var cellList = cells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
        if (cellList.Count == 0)
        {
            yield break;
        }

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
                         {
                             return (0, -ly, -lx, c.FormId);
                         }

                         if (subBlockX == 0 && lx < 2)
                         {
                             return (2, -ly, -lx, c.FormId);
                         }

                         return (1, -lx, -ly, c.FormId);
                     }))
        {
            yield return cell;
        }
    }

    private static int LocalCoord(int value, int size)
    {
        return value - FloorDiv(value, size) * size;
    }
}
