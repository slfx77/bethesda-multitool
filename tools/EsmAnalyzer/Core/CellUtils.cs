namespace EsmAnalyzer.Core;

/// <summary>
///     Utility methods for cell operations.
/// </summary>
public static class CellUtils
{
    /// <summary>
    ///     Groups adjacent cells together using flood-fill algorithm.
    ///     Cells are considered adjacent if they share an edge (4-connectivity).
    /// </summary>
    public static List<CellGroup> GroupAdjacentCells(List<CellHeightDifference> differences)
    {
        if (differences.Count == 0)
            return [];

        var groups = new List<CellGroup>();
        var cellLookup = differences.ToDictionary(d => (d.CellX, d.CellY));
        var visited = new HashSet<(int, int)>();

        foreach (var diff in differences)
        {
            var key = (diff.CellX, diff.CellY);
            if (visited.Contains(key))
                continue;

            // Start a new group with flood-fill
            var group = new CellGroup();
            var queue = new Queue<(int x, int y)>();
            queue.Enqueue(key);
            visited.Add(key);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                group.Cells.Add(cellLookup[current]);

                // Check 4 adjacent cells (up, down, left, right)
                var neighbors = new[]
                {
                    (current.x - 1, current.y),
                    (current.x + 1, current.y),
                    (current.x, current.y - 1),
                    (current.x, current.y + 1)
                };

                foreach (var neighbor in neighbors)
                {
                    if (!visited.Contains(neighbor) && cellLookup.ContainsKey(neighbor))
                    {
                        visited.Add(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    ///     Converts cell grid coordinates to world coordinates.
    /// </summary>
    /// <param name="cellX">Cell X coordinate.</param>
    /// <param name="cellY">Cell Y coordinate.</param>
    /// <param name="localX">Local X position within cell (0-32).</param>
    /// <param name="localY">Local Y position within cell (0-32).</param>
    /// <returns>World coordinates (X, Y).</returns>
    public static (int worldX, int worldY) CellToWorldCoordinates(int cellX, int cellY, int localX = 0, int localY = 0)
    {
        var worldX = cellX * EsmConstants.CellWorldUnits +
                     localX * EsmConstants.CellWorldUnits / EsmConstants.LandGridSize;
        var worldY = cellY * EsmConstants.CellWorldUnits +
                     localY * EsmConstants.CellWorldUnits / EsmConstants.LandGridSize;
        return (worldX, worldY);
    }

    /// <summary>
    ///     Converts world coordinates to cell grid coordinates.
    /// </summary>
    public static (int cellX, int cellY) WorldToCellCoordinates(float worldX, float worldY)
    {
        var cellX = (int)Math.Floor(worldX / EsmConstants.CellWorldUnits);
        var cellY = (int)Math.Floor(worldY / EsmConstants.CellWorldUnits);
        return (cellX, cellY);
    }
}
