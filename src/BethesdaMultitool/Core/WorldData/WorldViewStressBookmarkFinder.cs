using System.Numerics;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>A candidate camera-focus point for stress profiling, scored by nearby placed-object count.</summary>
internal readonly record struct WorldViewStressBookmark(Vector2 CanvasCenter, int Score);

/// <summary>Finds the densest area of a worldspace to use as a render stress-test camera bookmark.</summary>
internal static class WorldViewStressBookmarkFinder
{
    /// <summary>Scans every cell and returns the center whose render radius contains the most placed objects.</summary>
    internal static WorldViewStressBookmark? FindWastelandNvHeavyBookmark(
        WorldSpatialIndex index,
        WorldRenderCache renderCache,
        float renderRadius)
    {
        if (index.CellCount == 0)
        {
            return null;
        }

        var nearbyCells = new List<WorldSpatialCell>();
        var bestScore = 0;
        var bestCenter = Vector2.Zero;

        foreach (var (key, _) in index.CellsByGrid)
        {
            var center = new Vector2(
                (key.gx + 0.5f) * WorldGridConstants.CellSize,
                -(key.gy + 0.5f) * WorldGridConstants.CellSize);

            index.QueryCellsInRadius(center.X, center.Y, renderRadius, nearbyCells);
            var score = 0;
            foreach (var cell in nearbyCells)
            {
                score += renderCache.GetPlacementList(cell.Cell).Count;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestCenter = center;
            }
        }

        return bestScore > 0
            ? new WorldViewStressBookmark(bestCenter, bestScore)
            : null;
    }
}
