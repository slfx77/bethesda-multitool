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

        // USER RULING 2026-09-03 made these tiles REAL cells (see below), so they now have to
        // respect the one-cell-per-(worldspace, grid) rule that virtual buckets could ignore.
        // A captured cell at the same grid is the better home for the ref in every case: it
        // carries real cell data, while this pass only ever knew a grid coordinate.
        var realCellByGrid = new Dictionary<(uint WorldspaceFormId, int GridX, int GridY), CellRecord>();
        foreach (var cell in existingCells)
        {
            if (cell.IsInterior || cell.IsUnresolvedBucket || cell.IsPersistentCell
                || cell.WorldspaceFormId is not > 0
                || cell.GridX is not { } cellGridX || cell.GridY is not { } cellGridY)
            {
                continue;
            }

            realCellByGrid.TryAdd((cell.WorldspaceFormId.Value, cellGridX, cellGridY), cell);
        }

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
                if (realCellByGrid.TryGetValue(key, out var captured))
                {
                    // A real cell already owns this grid — adopt the ref rather than fabricating
                    // a second cell at the same coordinates, which is not a legal worldspace.
                    captured.PlacedObjects.Add(
                        CellLinkageHandler.ToPlacedReference(orphan, context, SourceOffsetCluster));
                    resolved++;
                    continue;
                }

                if (!virtualByKey.TryGetValue(key, out var vcell))
                {
                    vcell = new CellRecord
                    {
                        FormId = nextVirtualFormId++,
                        // USER RULING 2026-09-03: "We should aim to rescue all refs. If that
                        // requires a real cell, then that's what we should do." These were
                        // IsVirtual tiles, which the planner deletes as parse-time buckets, so
                        // this pass rescued refs into a cell that then took them down with it.
                        // Now a real exterior cell, identified by grid + worldspace with no
                        // EditorId per CK convention — the old "[Virtual x,y <ws>]" label would
                        // have been emitted into the plugin.
                        EditorId = null,
                        GridX = gx,
                        GridY = gy,
                        WorldspaceFormId = worldspaceFormId,
                        WorldspaceAssignmentSource = SourceOffsetCluster,
                        PlacedObjects = [],
                        IsVirtual = false,
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
