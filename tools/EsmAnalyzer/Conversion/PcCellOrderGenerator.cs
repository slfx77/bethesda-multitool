namespace EsmAnalyzer.Conversion;

/// <summary>
///     Generates the PC-compatible cell ordering for WRLD OFST tables.
///     PC uses an 8×8 block-based serpentine pattern:
///     - Blocks are processed column-by-column (all Y blocks before next X)
///     - Within each block: start at (7,7), sweep left 7 times, SE jump to next row
///     - Rows 7→2: left-sweep serpentine with SE(7,-1) jumps
///     - Rows 1→0: diagonal zigzag (S then NW alternating)
/// </summary>
public static class PcCellOrderGenerator
{
    /// <summary>
    ///     Generates the PC-compatible ordering for cells within a given bounds.
    /// </summary>
    /// <param name="minX">Minimum grid X coordinate</param>
    /// <param name="maxX">Maximum grid X coordinate</param>
    /// <param name="minY">Minimum grid Y coordinate</param>
    /// <param name="maxY">Maximum grid Y coordinate</param>
    /// <returns>Ordered list of (gridX, gridY) tuples in PC OFST order</returns>
    public static List<(int gridX, int gridY)> GeneratePcOrder(int minX, int maxX, int minY, int maxY)
    {
        var result = new List<(int gridX, int gridY)>();

        // Calculate bounds dimensions
        var width = maxX - minX + 1;
        var height = maxY - minY + 1;

        if (width <= 0 || height <= 0)
            return result;

        // Calculate number of 8×8 blocks needed
        var blocksX = (width + 7) / 8;
        var blocksY = (height + 7) / 8;

        // Process blocks column-by-column (column-major order)
        for (var blockX = 0; blockX < blocksX; blockX++)
        for (var blockY = 0; blockY < blocksY; blockY++)
        {
            // Generate cells for this block in serpentine order
            var blockCells = GenerateBlockOrder(
                minX + blockX * 8,
                minY + blockY * 8,
                minX, maxX, minY, maxY);

            result.AddRange(blockCells);
        }

        return result;
    }

    /// <summary>
    ///     Generates the ordering for a single 8×8 block.
    /// </summary>
    private static List<(int gridX, int gridY)> GenerateBlockOrder(
        int blockBaseX, int blockBaseY,
        int minX, int maxX, int minY, int maxY)
    {
        var result = new List<(int gridX, int gridY)>();

        // Block-local coordinates: start at (7,7) and work through the serpentine
        // Rows 7→2: left-sweep with SE jumps
        for (var localY = 7; localY >= 2; localY--)
            // Sweep left from x=7 to x=0
        for (var localX = 7; localX >= 0; localX--)
        {
            var gridX = blockBaseX + localX;
            var gridY = blockBaseY + localY;

            // Only include if within world bounds
            if (gridX >= minX && gridX <= maxX && gridY >= minY && gridY <= maxY)
                result.Add((gridX, gridY));
        }

        // Rows 1→0: diagonal zigzag pattern (S then NW alternating)
        // Starting at (7,1), go S to (7,0), NW to (6,1), S to (6,0), etc.
        for (var localX = 7; localX >= 0; localX--)
            // Down move: (x, 1) → (x, 0)
        for (var localY = 1; localY >= 0; localY--)
        {
            var gridX = blockBaseX + localX;
            var gridY = blockBaseY + localY;

            if (gridX >= minX && gridX <= maxX && gridY >= minY && gridY <= maxY)
                result.Add((gridX, gridY));
        }

        return result;
    }

    /// <summary>
    ///     Orders cells by their PC OFST index within a worldspace.
    /// </summary>
    /// <typeparam name="T">Cell type</typeparam>
    /// <param name="cells">Collection of cells to order</param>
    /// <param name="getGridX">Function to get grid X from cell</param>
    /// <param name="getGridY">Function to get grid Y from cell</param>
    /// <param name="minX">Minimum grid X of the worldspace</param>
    /// <param name="maxX">Maximum grid X of the worldspace</param>
    /// <param name="minY">Minimum grid Y of the worldspace</param>
    /// <param name="maxY">Maximum grid Y of the worldspace</param>
    /// <returns>Cells ordered by PC OFST sequence</returns>
    public static IEnumerable<T> OrderCellsByPcOfst<T>(
        IEnumerable<T> cells,
        Func<T, int?> getGridX,
        Func<T, int?> getGridY,
        int minX, int maxX, int minY, int maxY)
    {
        // Build a lookup from grid coords to cells
        var cellLookup = new Dictionary<(int x, int y), List<T>>();
        foreach (var cell in cells)
        {
            var x = getGridX(cell);
            var y = getGridY(cell);
            if (!x.HasValue || !y.HasValue) continue;

            var key = (x.Value, y.Value);
            if (!cellLookup.TryGetValue(key, out var list))
            {
                list = [];
                cellLookup[key] = list;
            }

            list.Add(cell);
        }

        // Generate PC order and emit cells in that sequence
        var pcOrder = GeneratePcOrder(minX, maxX, minY, maxY);
        foreach (var (gridX, gridY) in pcOrder)
            if (cellLookup.TryGetValue((gridX, gridY), out var matchingCells))
                foreach (var cell in matchingCells)
                    yield return cell;
    }

    /// <summary>
    ///     Computes the PC OFST index for a cell at the given grid coordinates.
    /// </summary>
    /// <param name="gridX">Cell grid X coordinate</param>
    /// <param name="gridY">Cell grid Y coordinate</param>
    /// <param name="minX">Minimum grid X of the worldspace</param>
    /// <param name="maxX">Maximum grid X of the worldspace</param>
    /// <param name="minY">Minimum grid Y of the worldspace</param>
    /// <param name="maxY">Maximum grid Y of the worldspace</param>
    /// <returns>OFST index, or -1 if out of bounds</returns>
    public static int GetPcOfstIndex(int gridX, int gridY, int minX, int maxX, int minY, int maxY)
    {
        // Calculate bounds dimensions
        var width = maxX - minX + 1;
        var height = maxY - minY + 1;

        if (width <= 0 || height <= 0)
            return -1;

        if (gridX < minX || gridX > maxX || gridY < minY || gridY > maxY)
            return -1;

        // Calculate which block this cell is in
        var relX = gridX - minX;
        var relY = gridY - minY;
        var blockX = relX / 8;
        var blockY = relY / 8;
        var localX = relX % 8;
        var localY = relY % 8;

        // Calculate number of blocks
        var blocksX = (width + 7) / 8;
        var blocksY = (height + 7) / 8;

        // Calculate cells in previous blocks (column-major block order)
        var cellsBeforeThisBlock = 0;

        // Full columns of blocks before this one
        for (var bx = 0; bx < blockX; bx++)
        for (var by = 0; by < blocksY; by++)
            cellsBeforeThisBlock += GetBlockCellCount(
                minX + bx * 8, minY + by * 8,
                minX, maxX, minY, maxY);

        // Blocks in the same column but before this one
        for (var by = 0; by < blockY; by++)
            cellsBeforeThisBlock += GetBlockCellCount(
                minX + blockX * 8, minY + by * 8,
                minX, maxX, minY, maxY);

        // Calculate position within this block
        var posInBlock = GetPositionInBlock(localX, localY,
            minX + blockX * 8, minY + blockY * 8,
            minX, maxX, minY, maxY);

        return cellsBeforeThisBlock + posInBlock;
    }

    /// <summary>
    ///     Gets the number of cells in a block that are within world bounds.
    /// </summary>
    private static int GetBlockCellCount(int blockBaseX, int blockBaseY, int minX, int maxX, int minY, int maxY)
    {
        var count = 0;
        for (var ly = 0; ly < 8; ly++)
        for (var lx = 0; lx < 8; lx++)
        {
            var gx = blockBaseX + lx;
            var gy = blockBaseY + ly;
            if (gx >= minX && gx <= maxX && gy >= minY && gy <= maxY)
                count++;
        }

        return count;
    }

    /// <summary>
    ///     Gets the position of a cell within its block in PC serpentine order.
    /// </summary>
    private static int GetPositionInBlock(int localX, int localY, int blockBaseX, int blockBaseY,
        int minX, int maxX, int minY, int maxY)
    {
        var position = 0;

        // Generate block order and find position
        var blockOrder = GenerateBlockOrder(blockBaseX, blockBaseY, minX, maxX, minY, maxY);
        var targetX = blockBaseX + localX;
        var targetY = blockBaseY + localY;

        foreach (var (gx, gy) in blockOrder)
        {
            if (gx == targetX && gy == targetY)
                return position;
            position++;
        }

        return -1; // Should not happen if inputs are valid
    }
}