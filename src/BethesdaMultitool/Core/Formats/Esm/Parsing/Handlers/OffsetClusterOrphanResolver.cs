using BethesdaMultitool.Core.Formats.Esm.Analysis.Cells;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Offset-cluster orphan resolution for <see cref="CellLinkageHandler" />. Some DMP fragments
///     preserve REFR runs immediately before the first CELL record in the same authored worldspace
///     fragment; when a whole orphan offset cluster sits next to exactly one worldspace's parsed
///     cells, this resolver creates worldspace-scoped virtual tiles at the refs' actual grid instead
///     of leaving them in the global unresolved bucket.
/// </summary>
internal static class OffsetClusterOrphanResolver
{
    private const string SourceOffsetCluster = "OffsetCluster";
    private const int OffsetClusterGapBytes = 0x1000;
    private const int OffsetClusterWindowBytes = 0x20000;
    private const int OffsetClusterGridExpansion = 2;

    internal static int ResolveOffsetClusteredExteriorOrphans(
        List<ExtractedRefrRecord> trueOrphans,
        List<CellRecord> existingCells,
        RecordParserContext context,
        out List<CellRecord> virtualCells)
    {
        virtualCells = [];
        if (trueOrphans.Count == 0)
        {
            return 0;
        }

        var offsetAnchors = existingCells
            .Where(cell => !cell.IsInterior &&
                           !cell.IsVirtual &&
                           !cell.IsUnresolvedBucket &&
                           cell.WorldspaceFormId is > 0 &&
                           cell.GridX.HasValue &&
                           cell.GridY.HasValue &&
                           cell.Offset > 0)
            .OrderBy(cell => cell.Offset)
            .ToList();

        if (offsetAnchors.Count == 0)
        {
            return 0;
        }

        var virtualByKey = new Dictionary<(uint WorldspaceFormId, int GridX, int GridY), CellRecord>();
        var nextVirtualFormId = 0xFE900001u;
        var resolved = 0;

        foreach (var cluster in BuildOffsetClusters(trueOrphans))
        {
            if (!TryInferOffsetClusterWorldspace(cluster, offsetAnchors, out var worldspaceFormId))
            {
                continue;
            }

            foreach (var orphan in cluster)
            {
                var pos = orphan.Position;
                if (pos == null)
                {
                    continue;
                }

                var (gx, gy) = CellUtils.WorldToCellCoordinates(pos.X, pos.Y);
                var key = (worldspaceFormId, gx, gy);
                if (!virtualByKey.TryGetValue(key, out var vcell))
                {
                    var wsName = context.GetEditorId(worldspaceFormId) ?? $"0x{worldspaceFormId:X8}";
                    vcell = new CellRecord
                    {
                        FormId = nextVirtualFormId++,
                        EditorId = $"[Virtual {gx},{gy} {wsName}]",
                        GridX = gx,
                        GridY = gy,
                        WorldspaceFormId = worldspaceFormId,
                        WorldspaceAssignmentSource = SourceOffsetCluster,
                        PlacedObjects = [],
                        IsVirtual = true,
                        IsBigEndian = orphan.Header.IsBigEndian
                    };
                    virtualByKey[key] = vcell;
                    virtualCells.Add(vcell);
                }

                vcell.PlacedObjects.Add(CellLinkageHandler.ToPlacedReference(orphan, context, SourceOffsetCluster));
                resolved++;
            }
        }

        return resolved;
    }

    private static IEnumerable<List<ExtractedRefrRecord>> BuildOffsetClusters(
        List<ExtractedRefrRecord> orphans)
    {
        List<ExtractedRefrRecord>? cluster = null;
        long lastOffset = 0;
        foreach (var orphan in orphans
                     .Where(orphan => orphan.Position != null && orphan.Header.Offset > 0)
                     .OrderBy(orphan => orphan.Header.Offset))
        {
            if (cluster == null || orphan.Header.Offset - lastOffset > OffsetClusterGapBytes)
            {
                if (cluster is { Count: > 0 })
                {
                    yield return cluster;
                }

                cluster = [];
            }

            cluster.Add(orphan);
            lastOffset = orphan.Header.Offset;
        }

        if (cluster is { Count: > 0 })
        {
            yield return cluster;
        }
    }

    private static bool TryInferOffsetClusterWorldspace(
        List<ExtractedRefrRecord> cluster,
        List<CellRecord> offsetAnchors,
        out uint worldspaceFormId)
    {
        worldspaceFormId = 0;
        var minOffset = cluster.Min(orphan => orphan.Header.Offset);
        var maxOffset = cluster.Max(orphan => orphan.Header.Offset);
        var nearbyAnchors = offsetAnchors
            .Where(cell => cell.Offset >= minOffset - OffsetClusterWindowBytes &&
                           cell.Offset <= maxOffset + OffsetClusterWindowBytes)
            .ToList();

        var worldspaces = nearbyAnchors
            .Select(cell => cell.WorldspaceFormId!.Value)
            .Distinct()
            .ToList();
        if (worldspaces.Count != 1)
        {
            return false;
        }

        var anchorMinX = nearbyAnchors.Min(cell => cell.GridX!.Value) - OffsetClusterGridExpansion;
        var anchorMaxX = nearbyAnchors.Max(cell => cell.GridX!.Value) + OffsetClusterGridExpansion;
        var anchorMinY = nearbyAnchors.Min(cell => cell.GridY!.Value) - OffsetClusterGridExpansion;
        var anchorMaxY = nearbyAnchors.Max(cell => cell.GridY!.Value) + OffsetClusterGridExpansion;

        foreach (var orphan in cluster)
        {
            var pos = orphan.Position;
            if (pos == null)
            {
                return false;
            }

            var (gx, gy) = CellUtils.WorldToCellCoordinates(pos.X, pos.Y);
            if (gx < anchorMinX || gx > anchorMaxX || gy < anchorMinY || gy > anchorMaxY)
            {
                return false;
            }
        }

        worldspaceFormId = worldspaces[0];
        return true;
    }
}
