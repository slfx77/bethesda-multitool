using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils;

/// <summary>
///     Spatial buckets for a selected exterior worldspace/unlinked-exterior set.
///     Coordinates are bucketed by exterior cell size, using canvas Y convention for refs
///     (<c>PlacedReference.Y</c> is stored as <c>-Y</c> for 2D view queries).
/// </summary>
internal sealed class WorldSpatialIndex
{
    internal const int ChunkCellSize = 8;

    private readonly Dictionary<(int gx, int gy), CellRecord> _cellsByGrid = new();
    private readonly Dictionary<(int bx, int by), List<PlacedReference>> _refsByBucket = new();
    private readonly Dictionary<(int bx, int by), List<PlacedReference>> _actorsByBucket = new();
    private readonly Dictionary<(int bx, int by), List<PlacedReference>> _markersByBucket = new();
    private readonly Dictionary<(int bx, int by), List<PlacedReference>> _saveRefsByBucket = new();
    private readonly Dictionary<(int bx, int by), List<DanglingRefPosition>> _danglingByBucket = new();
    private readonly Dictionary<(int gx, int gy), List<NavMeshRecord>> _navMeshesByGrid = new();
    private readonly Dictionary<(int cx, int cy), WorldGridChunk> _chunksByGrid = new();
    private readonly List<CellRecord> _persistentCells = [];
    private readonly List<PlacedReference> _persistentRefs = [];
    private readonly List<PlacedReference> _mapMarkers = [];
    private readonly List<WorldWaterCell> _waterCells = [];

    private WorldSpatialIndex()
    {
    }

    internal IReadOnlyDictionary<(int gx, int gy), CellRecord> CellsByGrid => _cellsByGrid;
    internal IReadOnlyList<CellRecord> PersistentCells => _persistentCells;
    internal IReadOnlyList<PlacedReference> PersistentRefs => _persistentRefs;
    internal IReadOnlyList<PlacedReference> MapMarkers => _mapMarkers;
    internal IReadOnlyList<WorldWaterCell> WaterCells => _waterCells;
    internal IReadOnlyCollection<WorldGridChunk> Chunks => _chunksByGrid.Values;
    internal int CellCount => _cellsByGrid.Count;

    internal static WorldSpatialIndex Build(
        WorldViewData data,
        IReadOnlyList<CellRecord> activeCells,
        IReadOnlyList<PlacedReference> filteredMarkers,
        uint? activeWorldspaceFormId,
        float? defaultWaterHeight)
    {
        var index = new WorldSpatialIndex();

        foreach (var cell in activeCells)
        {
            if (cell.GridX is not int gx || cell.GridY is not int gy)
            {
                index._persistentCells.Add(cell);
                foreach (var obj in cell.PlacedObjects)
                {
                    index._persistentRefs.Add(obj);
                }
                continue;
            }

            var key = (gx, gy);
            if (!index._cellsByGrid.TryGetValue(key, out var existing) || PreferGridLookupCell(cell, existing))
            {
                index._cellsByGrid[key] = cell;
            }

            if (data.NavMeshesByCell.TryGetValue(cell.FormId, out var navMeshes) && navMeshes.Count > 0)
            {
                index._navMeshesByGrid[key] = navMeshes;
            }

            foreach (var obj in cell.PlacedObjects)
            {
                if (cell.HasPersistentObjects || cell.IsPersistentCell)
                {
                    index._persistentRefs.Add(obj);
                    continue;
                }

                AddBucketed(index._refsByBucket, obj);
                if (obj.RecordType is "ACHR" or "ACRE")
                {
                    AddBucketed(index._actorsByBucket, obj);
                }
            }
        }

        foreach (var marker in filteredMarkers)
        {
            index._mapMarkers.Add(marker);
            AddBucketed(index._markersByBucket, marker);
        }

        if (data.SaveOverlayMarkers is { Count: > 0 } saveRefs)
        {
            foreach (var saveRef in saveRefs)
            {
                AddBucketed(index._saveRefsByBucket, saveRef);
            }
        }

        foreach (var dangling in data.DanglingRefs.Positions)
        {
            if (!WorldspaceMatches(dangling.WorldspaceFormId, activeWorldspaceFormId))
            {
                continue;
            }

            AddBucketed(index._danglingByBucket, dangling);
        }

        foreach (var (key, cell) in index._cellsByGrid)
        {
            var chunk = index.GetOrCreateChunk(key.gx, key.gy);
            chunk.Cells.Add(new WorldSpatialCell(key, cell, CellCenterCanvas(key.gx, key.gy)));

            var waterHeight = WorldRenderCache.ResolveEffectiveWaterHeight(cell, defaultWaterHeight);
            if (waterHeight is (> -1e6f and < 1e6f))
            {
                var water = new WorldWaterCell(key, cell, waterHeight.Value);
                index._waterCells.Add(water);
                chunk.WaterCells.Add(water);
            }
        }

        foreach (var chunk in index._chunksByGrid.Values)
        {
            chunk.Seal();
        }

        return index;
    }

    internal bool TryGetCell(int gx, int gy, out CellRecord cell) =>
        _cellsByGrid.TryGetValue((gx, gy), out cell!);

    internal bool TryGetCellAtCanvasPoint(Vector2 canvasWorldPos, out CellRecord cell)
    {
        var key = BucketFromCanvasPoint(canvasWorldPos.X, canvasWorldPos.Y);
        return _cellsByGrid.TryGetValue(key, out cell!);
    }

    internal void QueryCellsInViewport(Vector2 tlWorld, Vector2 brWorld, List<CellRecord> destination)
    {
        destination.Clear();
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(tlWorld, brWorld, margin: 0f);
        for (var gy = startY; gy <= endY; gy++)
        {
            for (var gx = startX; gx <= endX; gx++)
            {
                if (_cellsByGrid.TryGetValue((gx, gy), out var cell))
                {
                    destination.Add(cell);
                }
            }
        }
    }

    internal void QueryCellsInRadius(float canvasX, float canvasY, float radius, List<WorldSpatialCell> destination)
    {
        destination.Clear();
        var radiusSq = radius * radius;
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(
            new Vector2(canvasX - radius, canvasY - radius),
            new Vector2(canvasX + radius, canvasY + radius),
            margin: 0f);

        for (var gy = startY; gy <= endY; gy++)
        {
            for (var gx = startX; gx <= endX; gx++)
            {
                if (!_cellsByGrid.TryGetValue((gx, gy), out var cell))
                {
                    continue;
                }

                var (minX, minY, maxX, maxY) = CellCanvasBounds(gx, gy);
                var closestX = Math.Clamp(canvasX, minX, maxX);
                var closestY = Math.Clamp(canvasY, minY, maxY);
                var dx = canvasX - closestX;
                var dy = canvasY - closestY;
                if (dx * dx + dy * dy < radiusSq)
                {
                    destination.Add(new WorldSpatialCell((gx, gy), cell, CellCenterCanvas(gx, gy)));
                }
            }
        }
    }

    internal void QueryWaterCellsInRadius(float canvasX, float canvasY, float radius, List<WorldWaterCell> destination)
    {
        destination.Clear();
        var chunkStartX = FloorDiv((int)MathF.Floor((canvasX - radius) / WorldGridConstants.CellSize), ChunkCellSize);
        var chunkEndX = FloorDiv((int)MathF.Floor((canvasX + radius) / WorldGridConstants.CellSize), ChunkCellSize);
        var gameYMin = -(canvasY + radius);
        var gameYMax = -(canvasY - radius);
        var chunkStartY = FloorDiv((int)MathF.Floor(gameYMin / WorldGridConstants.CellSize), ChunkCellSize);
        var chunkEndY = FloorDiv((int)MathF.Floor(gameYMax / WorldGridConstants.CellSize), ChunkCellSize);
        var radiusSq = radius * radius;

        for (var cy = chunkStartY; cy <= chunkEndY; cy++)
        {
            for (var cx = chunkStartX; cx <= chunkEndX; cx++)
            {
                if (!_chunksByGrid.TryGetValue((cx, cy), out var chunk))
                {
                    continue;
                }

                foreach (var water in chunk.WaterCells)
                {
                    var key = water.Key;
                    var (minX, minY, maxX, maxY) = CellCanvasBounds(key.gx, key.gy);
                    var closestX = Math.Clamp(canvasX, minX, maxX);
                    var closestY = Math.Clamp(canvasY, minY, maxY);
                    var dx = canvasX - closestX;
                    var dy = canvasY - closestY;
                    if (dx * dx + dy * dy < radiusSq)
                    {
                        destination.Add(water);
                    }
                }
            }
        }
    }

    internal void QueryRefsInViewport(Vector2 tlWorld, Vector2 brWorld, List<PlacedReference> destination, float margin = 0f)
    {
        destination.Clear();
        QueryPlacedBucket(_refsByBucket, tlWorld, brWorld, margin, destination);
        AddPersistentRefsInViewport(tlWorld, brWorld, destination, margin: margin);
    }

    internal void QueryActorsInViewport(Vector2 tlWorld, Vector2 brWorld, List<PlacedReference> destination, float margin = 0f)
    {
        destination.Clear();
        QueryPlacedBucket(_actorsByBucket, tlWorld, brWorld, margin, destination);
        AddPersistentRefsInViewport(tlWorld, brWorld, destination, actorsOnly: true, margin: margin);
    }

    internal void QueryMarkersNear(Vector2 canvasWorldPos, float radius, List<PlacedReference> destination)
    {
        destination.Clear();
        QueryPlacedBucketNear(_markersByBucket, canvasWorldPos, radius, destination);
    }

    internal void QueryRefsNear(Vector2 canvasWorldPos, float radius, List<PlacedReference> destination)
    {
        destination.Clear();
        QueryPlacedBucketNear(_refsByBucket, canvasWorldPos, radius, destination);
        QueryPersistentRefsNear(canvasWorldPos, radius, destination);
    }

    internal void QuerySaveRefsInViewport(Vector2 tlWorld, Vector2 brWorld, List<PlacedReference> destination, float margin = 0f)
    {
        destination.Clear();
        QueryPlacedBucket(_saveRefsByBucket, tlWorld, brWorld, margin, destination);
    }

    internal void QueryDanglingNear(Vector2 canvasWorldPos, float radius, List<DanglingRefPosition> destination)
    {
        destination.Clear();
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(
            new Vector2(canvasWorldPos.X - radius, canvasWorldPos.Y - radius),
            new Vector2(canvasWorldPos.X + radius, canvasWorldPos.Y + radius),
            margin: 0f);

        for (var gy = startY; gy <= endY; gy++)
        {
            for (var gx = startX; gx <= endX; gx++)
            {
                if (!_danglingByBucket.TryGetValue((gx, gy), out var bucket))
                {
                    continue;
                }

                destination.AddRange(bucket);
            }
        }
    }

    internal void QueryDanglingInViewport(Vector2 tlWorld, Vector2 brWorld, List<DanglingRefPosition> destination, float margin = 0f)
    {
        destination.Clear();
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(tlWorld, brWorld, margin);
        var minX = Math.Min(tlWorld.X, brWorld.X) - margin;
        var maxX = Math.Max(tlWorld.X, brWorld.X) + margin;
        var minY = Math.Min(tlWorld.Y, brWorld.Y) - margin;
        var maxY = Math.Max(tlWorld.Y, brWorld.Y) + margin;

        for (var gy = startY; gy <= endY; gy++)
        {
            for (var gx = startX; gx <= endX; gx++)
            {
                if (!_danglingByBucket.TryGetValue((gx, gy), out var bucket))
                {
                    continue;
                }

                foreach (var p in bucket)
                {
                    var canvasY = -p.Y;
                    if (p.X >= minX && p.X <= maxX && canvasY >= minY && canvasY <= maxY)
                    {
                        destination.Add(p);
                    }
                }
            }
        }
    }

    internal void QueryNavMeshCellsInViewport(Vector2 tlWorld, Vector2 brWorld, List<NavMeshCellEntry> destination)
    {
        destination.Clear();
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(tlWorld, brWorld, margin: 0f);
        for (var gy = startY; gy <= endY; gy++)
        {
            for (var gx = startX; gx <= endX; gx++)
            {
                if (_navMeshesByGrid.TryGetValue((gx, gy), out var navMeshes) &&
                    _cellsByGrid.TryGetValue((gx, gy), out var cell))
                {
                    destination.Add(new NavMeshCellEntry(cell, navMeshes));
                }
            }
        }
    }

    internal static float DistanceSquared(Vector2 a, Vector2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    internal static (int gx, int gy) BucketFromCanvasPoint(float x, float canvasY)
    {
        var gx = (int)MathF.Floor(x / WorldGridConstants.CellSize);
        var gy = (int)MathF.Floor(-canvasY / WorldGridConstants.CellSize);
        return (gx, gy);
    }

    private static void QueryPlacedBucket(
        Dictionary<(int bx, int by), List<PlacedReference>> buckets,
        Vector2 tlWorld,
        Vector2 brWorld,
        float margin,
        List<PlacedReference> destination)
    {
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(tlWorld, brWorld, margin);
        var minX = Math.Min(tlWorld.X, brWorld.X) - margin;
        var maxX = Math.Max(tlWorld.X, brWorld.X) + margin;
        var minY = Math.Min(tlWorld.Y, brWorld.Y) - margin;
        var maxY = Math.Max(tlWorld.Y, brWorld.Y) + margin;

        for (var gy = startY; gy <= endY; gy++)
        {
            for (var gx = startX; gx <= endX; gx++)
            {
                if (!buckets.TryGetValue((gx, gy), out var bucket))
                {
                    continue;
                }

                foreach (var obj in bucket)
                {
                    var canvasY = -obj.Y;
                    if (obj.X >= minX && obj.X <= maxX && canvasY >= minY && canvasY <= maxY)
                    {
                        destination.Add(obj);
                    }
                }
            }
        }
    }

    private static void QueryPlacedBucketNear(
        Dictionary<(int bx, int by), List<PlacedReference>> buckets,
        Vector2 canvasWorldPos,
        float radius,
        List<PlacedReference> destination)
    {
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(
            new Vector2(canvasWorldPos.X - radius, canvasWorldPos.Y - radius),
            new Vector2(canvasWorldPos.X + radius, canvasWorldPos.Y + radius),
            margin: 0f);

        for (var gy = startY; gy <= endY; gy++)
        {
            for (var gx = startX; gx <= endX; gx++)
            {
                if (buckets.TryGetValue((gx, gy), out var bucket))
                {
                    destination.AddRange(bucket);
                }
            }
        }
    }

    private static (int startX, int endX, int startY, int endY) BucketRangeForCanvasRect(
        Vector2 a,
        Vector2 b,
        float margin)
    {
        var minX = Math.Min(a.X, b.X) - margin;
        var maxX = Math.Max(a.X, b.X) + margin;
        var minCanvasY = Math.Min(a.Y, b.Y) - margin;
        var maxCanvasY = Math.Max(a.Y, b.Y) + margin;
        var minGameY = -maxCanvasY;
        var maxGameY = -minCanvasY;

        return (
            (int)MathF.Floor(minX / WorldGridConstants.CellSize),
            (int)MathF.Floor(maxX / WorldGridConstants.CellSize),
            (int)MathF.Floor(minGameY / WorldGridConstants.CellSize),
            (int)MathF.Floor(maxGameY / WorldGridConstants.CellSize));
    }

    private static void AddBucketed(Dictionary<(int bx, int by), List<PlacedReference>> buckets, PlacedReference obj)
    {
        var key = BucketFromCanvasPoint(obj.X, -obj.Y);
        if (!buckets.TryGetValue(key, out var list))
        {
            list = [];
            buckets[key] = list;
        }

        list.Add(obj);
    }

    private static void AddBucketed(Dictionary<(int bx, int by), List<DanglingRefPosition>> buckets, DanglingRefPosition obj)
    {
        var key = BucketFromCanvasPoint(obj.X, -obj.Y);
        if (!buckets.TryGetValue(key, out var list))
        {
            list = [];
            buckets[key] = list;
        }

        list.Add(obj);
    }

    private void AddPersistentRefsInViewport(
        Vector2 tlWorld,
        Vector2 brWorld,
        List<PlacedReference> destination,
        bool actorsOnly = false,
        float margin = 0f)
    {
        var minX = Math.Min(tlWorld.X, brWorld.X) - margin;
        var maxX = Math.Max(tlWorld.X, brWorld.X) + margin;
        var minY = Math.Min(tlWorld.Y, brWorld.Y) - margin;
        var maxY = Math.Max(tlWorld.Y, brWorld.Y) + margin;

        foreach (var obj in _persistentRefs)
        {
            if (actorsOnly && obj.RecordType is not ("ACHR" or "ACRE"))
            {
                continue;
            }

            var canvasY = -obj.Y;
            if (obj.X >= minX && obj.X <= maxX && canvasY >= minY && canvasY <= maxY)
            {
                destination.Add(obj);
            }
        }
    }

    private void QueryPersistentRefsNear(Vector2 canvasWorldPos, float radius, List<PlacedReference> destination)
    {
        var radiusSq = radius * radius;
        foreach (var obj in _persistentRefs)
        {
            var dx = canvasWorldPos.X - obj.X;
            var dy = canvasWorldPos.Y - (-obj.Y);
            if (dx * dx + dy * dy <= radiusSq)
            {
                destination.Add(obj);
            }
        }
    }

    private WorldGridChunk GetOrCreateChunk(int gx, int gy)
    {
        var key = (FloorDiv(gx, ChunkCellSize), FloorDiv(gy, ChunkCellSize));
        if (_chunksByGrid.TryGetValue(key, out var chunk))
        {
            return chunk;
        }

        chunk = new WorldGridChunk(key, key.Item1 * ChunkCellSize, key.Item2 * ChunkCellSize);
        _chunksByGrid[key] = chunk;
        return chunk;
    }

    private static Vector2 CellCenterCanvas(int gx, int gy) =>
        new((gx + 0.5f) * WorldGridConstants.CellSize, -(gy + 0.5f) * WorldGridConstants.CellSize);

    private static (float minX, float minY, float maxX, float maxY) CellCanvasBounds(int gx, int gy)
    {
        var minX = gx * WorldGridConstants.CellSize;
        var maxX = minX + WorldGridConstants.CellSize;
        var minY = -(gy + 1) * WorldGridConstants.CellSize;
        var maxY = -gy * WorldGridConstants.CellSize;
        return (minX, minY, maxX, maxY);
    }

    private static bool PreferGridLookupCell(CellRecord candidate, CellRecord existing)
    {
        if (candidate.PlacedObjects.Count != existing.PlacedObjects.Count)
        {
            return candidate.PlacedObjects.Count > existing.PlacedObjects.Count;
        }

        if (candidate.IsVirtual != existing.IsVirtual)
        {
            return !candidate.IsVirtual;
        }

        if (candidate.IsUnresolvedBucket != existing.IsUnresolvedBucket)
        {
            return !candidate.IsUnresolvedBucket;
        }

        var candidateHasTerrain = HasTerrain(candidate);
        var existingHasTerrain = HasTerrain(existing);
        if (candidateHasTerrain != existingHasTerrain)
        {
            return candidateHasTerrain;
        }

        return candidate.FormId < existing.FormId;
    }

    private static bool HasTerrain(CellRecord cell) =>
        cell.Heightmap is not null ||
        cell.LandVisualData?.HasAny == true ||
        cell.RuntimeTerrainMesh is not null;

    private static bool WorldspaceMatches(uint? attributionWorldspace, uint? activeWorldspace) =>
        activeWorldspace is null || (attributionWorldspace.HasValue && attributionWorldspace.Value == activeWorldspace.Value);

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder != 0 && ((remainder < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }
}

internal readonly record struct WorldSpatialCell(
    (int gx, int gy) Key,
    CellRecord Cell,
    Vector2 CenterCanvas);

internal readonly record struct WorldWaterCell(
    (int gx, int gy) Key,
    CellRecord Cell,
    float Height);

internal readonly record struct NavMeshCellEntry(
    CellRecord Cell,
    IReadOnlyList<NavMeshRecord> NavMeshes);

internal sealed class WorldGridChunk
{
    internal WorldGridChunk((int cx, int cy) key, int minGridX, int minGridY)
    {
        Key = key;
        MinGridX = minGridX;
        MinGridY = minGridY;
        MaxGridX = minGridX + WorldSpatialIndex.ChunkCellSize - 1;
        MaxGridY = minGridY + WorldSpatialIndex.ChunkCellSize - 1;
    }

    internal (int cx, int cy) Key { get; }
    internal int MinGridX { get; }
    internal int MinGridY { get; }
    internal int MaxGridX { get; }
    internal int MaxGridY { get; }
    internal List<WorldSpatialCell> Cells { get; } = [];
    internal List<WorldWaterCell> WaterCells { get; } = [];
    internal Vector2 MinCanvas { get; private set; }
    internal Vector2 MaxCanvas { get; private set; }

    internal void Seal()
    {
        MinCanvas = new Vector2(MinGridX * WorldGridConstants.CellSize, -(MaxGridY + 1) * WorldGridConstants.CellSize);
        MaxCanvas = new Vector2((MaxGridX + 1) * WorldGridConstants.CellSize, -MinGridY * WorldGridConstants.CellSize);
    }
}
