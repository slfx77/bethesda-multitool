using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;

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

            var primary = MergeCellMetadata(captures);
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

    private static CellRecord MergeCellMetadata(List<CellRecord> captures)
    {
        var primary = captures[0];
        var gridSource = captures.FirstOrDefault(c => c.GridX.HasValue && c.GridY.HasValue);
        var worldspaceSource = captures.FirstOrDefault(c => c.WorldspaceFormId is > 0);

        return primary with
        {
            EditorId = primary.EditorId ??
                       captures.Select(c => c.EditorId).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            FullName = primary.FullName ??
                       captures.Select(c => c.FullName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            GridX = primary.GridX ?? gridSource?.GridX,
            GridY = primary.GridY ?? gridSource?.GridY,
            WorldspaceFormId = primary.WorldspaceFormId ?? worldspaceSource?.WorldspaceFormId,
            WorldspaceAssignmentSource = primary.WorldspaceAssignmentSource ?? worldspaceSource?.WorldspaceAssignmentSource,
            CandidateWorldspaceFormIds = MergeDistinct(captures.SelectMany(c => c.CandidateWorldspaceFormIds)),
            Flags = primary.Flags != 0 ? primary.Flags : captures.Select(c => c.Flags).FirstOrDefault(f => f != 0),
            WaterHeight = primary.WaterHeight ?? captures.Select(c => c.WaterHeight).FirstOrDefault(v => v.HasValue),
            EncounterZoneFormId = primary.EncounterZoneFormId ??
                                  captures.Select(c => c.EncounterZoneFormId).FirstOrDefault(v => v is > 0),
            MusicTypeFormId = primary.MusicTypeFormId ??
                              captures.Select(c => c.MusicTypeFormId).FirstOrDefault(v => v is > 0),
            AcousticSpaceFormId = primary.AcousticSpaceFormId ??
                                  captures.Select(c => c.AcousticSpaceFormId).FirstOrDefault(v => v is > 0),
            ImageSpaceFormId = primary.ImageSpaceFormId ??
                               captures.Select(c => c.ImageSpaceFormId).FirstOrDefault(v => v is > 0),
            LightingTemplateFormId = primary.LightingTemplateFormId ??
                                     captures.Select(c => c.LightingTemplateFormId).FirstOrDefault(v => v is > 0),
            LightingTemplateInheritanceFlags = primary.LightingTemplateInheritanceFlags ??
                                               captures.Select(c => c.LightingTemplateInheritanceFlags)
                                                   .FirstOrDefault(v => v.HasValue),
            LightingData = primary.LightingData ??
                           captures.Select(c => c.LightingData).FirstOrDefault(v => v is { Count: > 0 }),
            RadiationRegionFormIds = MergeDistinct(captures.SelectMany(c => c.RadiationRegionFormIds)),
            LinkedCellFormIds = MergeDistinct(captures.SelectMany(c => c.LinkedCellFormIds)),
            Heightmap = primary.Heightmap ?? captures.Select(c => c.Heightmap).FirstOrDefault(v => v is not null),
            LandVisualData = primary.LandVisualData ??
                             captures.Select(c => c.LandVisualData).FirstOrDefault(v => v is not null),
            RuntimeTerrainMesh = primary.RuntimeTerrainMesh ??
                                 captures.Select(c => c.RuntimeTerrainMesh).FirstOrDefault(v => v is not null),
            HasPersistentObjects = captures.Any(c => c.HasPersistentObjects),
            IsVirtual = captures.Any(c => c.IsVirtual),
            IsPersistentCell = captures.Any(c => c.IsPersistentCell),
            IsUnresolvedBucket = captures.All(c => c.IsUnresolvedBucket),
            IsBigEndian = captures.Any(c => c.IsBigEndian)
        };
    }

    private static List<uint> MergeDistinct(IEnumerable<uint> values)
    {
        var seen = new HashSet<uint>();
        var merged = new List<uint>();
        foreach (var value in values)
        {
            if (value != 0 && seen.Add(value))
            {
                merged.Add(value);
            }
        }

        return merged;
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
