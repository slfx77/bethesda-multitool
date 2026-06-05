using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

namespace FalloutXbox360Utils;

internal readonly record struct WorldViewStressBookmark(Vector2 CanvasCenter, int Score);

internal static class WorldViewStressBookmarkFinder
{
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
