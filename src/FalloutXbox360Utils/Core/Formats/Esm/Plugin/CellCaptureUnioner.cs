using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

internal sealed record CellCaptureUnionResult(
    IReadOnlyList<CellRecord> Cells,
    int TotalUnionedPlacements,
    int CellGroupsWithMerges,
    IReadOnlyList<CellCaptureUnionDiagnostic> Diagnostics);

internal sealed record CellCaptureUnionDiagnostic(
    string CellKey,
    uint PrimaryFormId,
    int CaptureCount,
    int PrimaryPlacementCount,
    int UnionPlacementCount,
    int AddedPlacementCount);

/// <summary>
///     Unions repeated parser captures of the same logical cell into one placement list.
/// </summary>
internal static class CellCaptureUnioner
{
    public static CellCaptureUnionResult Union(IReadOnlyList<CellRecord> cells)
    {
        var groups = new Dictionary<string, List<CellRecord>>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();
        foreach (var cell in cells)
        {
            var key = ComputeCellIdentityKey(cell);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
                orderedKeys.Add(key);
            }

            list.Add(cell);
        }

        var merged = new List<CellRecord>(orderedKeys.Count);
        var diagnostics = new List<CellCaptureUnionDiagnostic>();
        var totalUnioned = 0;
        var totalGroupsWithMerges = 0;
        foreach (var key in orderedKeys)
        {
            var captures = groups[key];
            if (captures.Count == 1)
            {
                merged.Add(captures[0]);
                continue;
            }

            var primary = captures[0];
            var seen = new HashSet<uint>(capacity: primary.PlacedObjects.Count);
            var unionList = new List<PlacedReference>(primary.PlacedObjects.Count);
            foreach (var capture in captures)
            {
                foreach (var placed in capture.PlacedObjects)
                {
                    if (seen.Add(placed.FormId))
                    {
                        unionList.Add(placed);
                    }
                }
            }

            var added = unionList.Count - primary.PlacedObjects.Count;
            if (added > 0)
            {
                totalUnioned += added;
                totalGroupsWithMerges++;
                diagnostics.Add(new CellCaptureUnionDiagnostic(
                    key,
                    primary.FormId,
                    captures.Count,
                    primary.PlacedObjects.Count,
                    unionList.Count,
                    added));
            }

            merged.Add(primary with { PlacedObjects = unionList });
        }

        return new CellCaptureUnionResult(merged, totalUnioned, totalGroupsWithMerges, diagnostics);
    }

    /// <summary>
    ///     Stable identity key per logical cell. Cells with a real master-style FormID
    ///     are keyed by FormID. Virtual cells fall through to grid coords + worldspace.
    /// </summary>
    private static string ComputeCellIdentityKey(CellRecord cell)
    {
        if (cell.FormId != 0 && (cell.FormId & 0xFF000000u) != 0xFE000000u)
        {
            return $"FID:{cell.FormId:X8}";
        }

        if (cell.IsInterior)
        {
            return $"INT:{cell.EditorId ?? "(none)"}";
        }

        return $"EXT:{cell.WorldspaceFormId ?? 0:X8}:{cell.GridX ?? 0}:{cell.GridY ?? 0}";
    }
}
