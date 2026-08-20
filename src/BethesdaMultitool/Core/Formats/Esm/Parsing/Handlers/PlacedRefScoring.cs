using BethesdaMultitool.Core.Formats.Esm.Analysis.Cells;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     One occurrence of a placed ref within a cell, used by best-cell deduplication to score
///     and rank candidate placements.
/// </summary>
internal readonly record struct RefOccurrence(
    int CellIndex,
    int ObjectIndex,
    CellRecord Cell,
    PlacedReference Ref);

/// <summary>
///     Scoring heuristics for selecting the single best cell to keep a placed ref in when the
///     same FormID survives in multiple cells. Extracted from <see cref="PersistentRefRedistributor" />,
///     whose <c>DeduplicatePlacedRefsToBestCell</c> ranks occurrences via these helpers.
/// </summary>
internal static class PlacedRefScoring
{
    internal static int ScoreRefOccurrence(CellRecord cell, PlacedReference pref)
    {
        var score = 0;

        if (CellContainsRefGrid(cell, pref))
        {
            score += 1000;
        }

        if (!cell.IsVirtual && !cell.IsUnresolvedBucket && !cell.IsPersistentCell)
        {
            score += 500;
        }

        if (!cell.IsVirtual)
        {
            score += 120;
        }

        if (!cell.IsUnresolvedBucket)
        {
            score += 80;
        }

        if (!cell.IsPersistentCell)
        {
            score += 40;
        }

        if (cell.WorldspaceFormId.HasValue)
        {
            score += 20;
        }

        score += pref.AssignmentSource switch
        {
            "ParentCell" or "CellGrup" => 90,
            "PersistentRedistributed" => 80,
            "GridMap" or "RuntimeCellList" => 70,
            "PersistentRedistributedSynthetic" => 25,
            "Proximity" => 10,
            _ => 0
        };

        return score;
    }

    private static bool CellContainsRefGrid(CellRecord cell, PlacedReference pref)
    {
        if (cell.IsInterior || !cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            return false;
        }

        if (MathF.Abs(pref.X) <= 1f && MathF.Abs(pref.Y) <= 1f)
        {
            return false;
        }

        var (gridX, gridY) = CellUtils.WorldToCellCoordinates(pref.X, pref.Y);
        return cell.GridX.Value == gridX && cell.GridY.Value == gridY;
    }
}
