using System.Numerics;
using BethesdaMultitool.Core.EsmView;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     Spatial buckets for a selected exterior worldspace/unlinked-exterior set.
///     Coordinates are bucketed by exterior cell size, using canvas Y convention for refs
///     (<c>PlacedReference.Y</c> is stored as <c>-Y</c> for 2D view queries).
/// </summary>
internal sealed class WorldSpatialIndex
{
    internal const int ChunkCellSize = 8;
    private readonly Dictionary<(int bx, int by), List<PlacedReference>> _actorsByBucket = new();

    private readonly Dictionary<(int gx, int gy), CellRecord> _cellsByGrid = new();
    private readonly Dictionary<(int cx, int cy), WorldGridChunk> _chunksByGrid = new();
    private readonly Dictionary<(int bx, int by), List<DanglingRefPosition>> _danglingByBucket = new();
    private readonly List<PlacedReference> _mapMarkers = [];
    private readonly Dictionary<(int bx, int by), List<PlacedReference>> _markersByBucket = new();
    private readonly Dictionary<(int gx, int gy), List<NavMeshRecord>> _navMeshesByGrid = new();
    private readonly List<CellRecord> _persistentCells = [];
    private readonly List<PlacedReference> _persistentRefs = [];
    private readonly Dictionary<(int bx, int by), List<PlacedReference>> _refsByBucket = new();
    private readonly Dictionary<(int bx, int by), List<PlacedReference>> _saveRefsByBucket = new();
    private readonly List<WorldWaterCell> _waterCells = [];

    private WorldSpatialIndex(float cellSize, bool includesMapOverlays)
    {
        CellSize = cellSize;
        IncludesMapOverlays = includesMapOverlays;
    }

    /// <summary>
    ///     World units per exterior-cell edge for this worldspace (4096 Fallout-family, 8192
    ///     Morrowind). All bucketing / canvas mapping keys off this so the index matches the geometry's
    ///     absolute coordinates.
    /// </summary>
    internal float CellSize { get; }

    /// <summary>
    ///     Whether this instance contains the placed-reference/actor/marker/dangling buckets used by
    ///     the 2D map. The 3D viewer consumes cells and water from this index, then performs its own
    ///     per-cell reference broadphase through <see cref="WorldRenderCache" />; duplicating every
    ///     world reference here is both unused and prohibitively expensive for FO76-sized worlds.
    /// </summary>
    internal bool IncludesMapOverlays { get; }

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
        float? defaultWaterHeight,
        bool defaultWaterRequiresCellHasWater = false)
    {
        return BuildCore(
            data,
            activeCells,
            filteredMarkers,
            activeWorldspaceFormId,
            defaultWaterHeight,
            defaultWaterRequiresCellHasWater,
            true);
    }

    /// <summary>
    ///     Builds the cell/chunk/water index consumed by the 3D renderers without also duplicating
    ///     every placed reference into the 2D map's buckets. Reference rendering already starts from
    ///     the visible <see cref="CellRecord" /> set and uses <see cref="WorldRenderCache" />'s tighter
    ///     sub-cell broadphase, so the omitted buckets have no 3D consumer.
    /// </summary>
    internal static WorldSpatialIndex BuildFor3D(
        WorldViewData data,
        IReadOnlyList<CellRecord> activeCells,
        float? defaultWaterHeight,
        bool defaultWaterRequiresCellHasWater = false)
    {
        return BuildCore(
            data,
            activeCells,
            Array.Empty<PlacedReference>(),
            null,
            defaultWaterHeight,
            defaultWaterRequiresCellHasWater,
            false);
    }

    private static WorldSpatialIndex BuildCore(
        WorldViewData data,
        IReadOnlyList<CellRecord> activeCells,
        IReadOnlyList<PlacedReference> filteredMarkers,
        uint? activeWorldspaceFormId,
        float? defaultWaterHeight,
        bool defaultWaterRequiresCellHasWater,
        bool includeMapOverlays)
    {
        var index = new WorldSpatialIndex(data.CellWorldSize, includeMapOverlays);
        index._cellsByGrid.EnsureCapacity(activeCells.Count);
        if (includeMapOverlays)
        {
            // A well-formed exterior usually produces about one reference bucket per CELL. Reserving
            // that shape avoids repeated dictionary growth without making assumptions about the
            // individual reference coordinates (out-of-cell refs still bucket by their true position).
            index._refsByBucket.EnsureCapacity(activeCells.Count);
        }

        foreach (var cell in activeCells)
        {
            if (cell.GridX is not int gx || cell.GridY is not int gy)
            {
                index._persistentCells.Add(cell);
                if (includeMapOverlays)
                {
                    foreach (var obj in cell.PlacedObjects)
                    {
                        index._persistentRefs.Add(obj);
                    }
                }

                continue;
            }

            var key = (gx, gy);
            if (!index._cellsByGrid.TryGetValue(key, out var existing) || PreferGridLookupCell(cell, existing))
            {
                index._cellsByGrid[key] = cell;
            }

            if (includeMapOverlays &&
                data.NavMeshesByCell.TryGetValue(cell.FormId, out var navMeshes) &&
                navMeshes.Count > 0)
            {
                index._navMeshesByGrid[key] = navMeshes;
            }

            if (includeMapOverlays)
            {
                foreach (var obj in cell.PlacedObjects)
                {
                    if (cell.HasPersistentObjects || cell.IsPersistentCell)
                    {
                        index._persistentRefs.Add(obj);
                        continue;
                    }

                    index.AddBucketed(index._refsByBucket, obj);
                    if (obj.RecordType is "ACHR" or "ACRE")
                    {
                        index.AddBucketed(index._actorsByBucket, obj);
                    }
                }
            }
        }

        if (includeMapOverlays)
        {
            foreach (var marker in filteredMarkers)
            {
                index._mapMarkers.Add(marker);
                index.AddBucketed(index._markersByBucket, marker);
            }

            if (data.SaveOverlayMarkers is { Count: > 0 } saveRefs)
            {
                foreach (var saveRef in saveRefs)
                {
                    index.AddBucketed(index._saveRefsByBucket, saveRef);
                }
            }

            foreach (var dangling in data.DanglingRefs.Positions)
            {
                if (!WorldspaceMatches(dangling.WorldspaceFormId, activeWorldspaceFormId))
                {
                    continue;
                }

                index.AddBucketed(index._danglingByBucket, dangling);
            }
        }

        var worldspacesById = new Dictionary<uint, WorldspaceRecord>();
        foreach (var ws in data.Worldspaces)
        {
            if (ws.FormId != 0) worldspacesById.TryAdd(ws.FormId, ws);
        }

        foreach (var (key, cell) in index._cellsByGrid)
        {
            var chunk = index.GetOrCreateChunk(key.gx, key.gy);
            chunk.Cells.Add(new WorldSpatialCell(key, cell, index.CellCenterCanvas(key.gx, key.gy)));

            var waterHeight = WorldRenderCache.ResolveEffectiveWaterHeight(
                cell, defaultWaterHeight, defaultWaterRequiresCellHasWater);

            // Memory dumps: exterior water exists only where TERRAIN exists to occlude it, and a
            // per-cell override must be plausible against that terrain. This is the engine's own
            // model made explicit — retail city worldspaces run the default plane under EVERY cell
            // and hide it below the ground (TheStripWorldNew: land 1000, water 0, and 0 of 4096
            // cells carry an in-range XCLW), so on a dump with partial terrain a quad in a
            // terrain-less cell can only ever render as a bare floating sheet. The previous gate
            // also admitted quads on the CELL water flag or an in-range WaterHeight — precisely the
            // two runtime fields that are routinely stale on captured cells (xex21's Strip carried
            // 27 garbage in-range overrides at heights from 6 to 18,070, the "blue quads floating
            // at random levels" render). ESM/ESP views are unchanged.
            if (data.IsMemoryDump)
            {
                if (cell.Heightmap is null && cell.RuntimeTerrainMesh is null)
                {
                    waterHeight = null;
                }
                else if (cell.WaterHeight is > -1e6f and < 1e6f &&
                         (!WorldspaceAuthorsCellWater(worldspacesById, cell) ||
                          !IsPlausibleDumpCellWaterOverride(cell.WaterHeight.Value, cell.Heightmap)))
                {
                    // Corrupt per-cell override — fall back to the worldspace default, exactly what
                    // the engine does for a cell without an authored XCLW.
                    if (defaultWaterRequiresCellHasWater && !cell.HasWater)
                    {
                        waterHeight = null;
                    }
                    else if (defaultWaterHeight is { } dflt &&
                             WorldHeightNormalizer.IsReportableHeight(dflt) &&
                             !WorldHeightNormalizer.IsNoWaterSentinel(dflt))
                    {
                        waterHeight = dflt;
                    }
                    else
                    {
                        waterHeight = null;
                    }
                }

                // Emit only where the plane would actually SURFACE through the cell's captured
                // terrain. Retail runs the default plane under every city cell and the ground
                // hides it completely (Strip: land 1000 / water 0); a dump's partial terrain
                // leaks that never-visible plane at capture borders instead. A plane at or below
                // the cell's terrain floor cannot be legitimately visible anywhere in the cell.
                if (waterHeight is { } visible && TryGetTerrainMinHeight(cell) is { } terrainMin &&
                    visible <= terrainMin)
                {
                    waterHeight = null;
                }
            }

            if (waterHeight is > -1e6f and < 1e6f)
            {
                // Exterior water: one cell-sized quad at the grid origin. The OriginXY/FootprintSize
                // fields carry that explicitly so the renderer is grid-agnostic (interiors supply
                // their own footprint — see BuildInterior).
                var water = new WorldWaterCell(
                    key,
                    cell,
                    waterHeight.Value,
                    new Vector2(key.gx * index.CellSize, key.gy * index.CellSize),
                    index.CellSize);
                index._waterCells.Add(water);
                chunk.WaterCells.Add(water);
            }
        }

        foreach (var chunk in index._chunksByGrid.Values)
        {
            chunk.Seal(index.CellSize);
        }

        return index;
    }

    /// <summary>
    ///     Synthetic grid key for an interior cell (which has no real grid coords). Placed on the
    ///     tile at its placed-object centroid, in the same game-Y grid convention as exterior
    ///     <see cref="CellRecord.GridY" /> and the ref buckets — so the cylinder cell enumeration
    ///     (<see cref="QueryCellsInRadius" />) yields it once the camera is framed on the cell.
    /// </summary>
    internal static (int gx, int gy) SyntheticInteriorKey(CellRecord interior,
        float cellSize = WorldGridConstants.CellSize)
    {
        double sumX = 0, sumY = 0;
        var count = 0;
        foreach (var obj in interior.PlacedObjects)
        {
            if (!float.IsFinite(obj.X) || !float.IsFinite(obj.Y)) continue;
            sumX += obj.X;
            sumY += obj.Y;
            count++;
        }

        if (count == 0) return (0, 0);
        var cx = (float)(sumX / count);
        var cy = (float)(sumY / count);
        return ((int)MathF.Floor(cx / cellSize), (int)MathF.Floor(cy / cellSize));
    }

    /// <summary>
    ///     Builds a single-cell index for an interior. Interiors have no LAND/grid and live in
    ///     their own absolute coordinate space, so this bypasses <see cref="Build" />'s
    ///     null-grid→persistent shunt: the cell is placed on a synthetic grid key, all placed
    ///     objects are bucketed, navmeshes are stashed by that key, and (if the cell has water)
    ///     one water cell is added with a footprint derived from the placed-object AABB. The 3D
    ///     reference broadphase already works in absolute space, so references render unchanged.
    /// </summary>
    internal static WorldSpatialIndex BuildInterior(WorldViewData data, CellRecord interior)
    {
        var index = new WorldSpatialIndex(data.CellWorldSize, false);
        var key = SyntheticInteriorKey(interior, index.CellSize);
        index._cellsByGrid[key] = interior;

        var chunk = index.GetOrCreateChunk(key.gx, key.gy);
        chunk.Cells.Add(new WorldSpatialCell(key, interior, index.CellCenterCanvas(key.gx, key.gy)));

        // Interior water height comes from XCLW directly (no worldspace DNAM fallback).
        var waterHeight = WorldRenderCache.ResolveEffectiveWaterHeight(interior, null);
        if (waterHeight is > -1e6f and < 1e6f && HasCredibleInteriorWater(interior, data.IsMemoryDump))
        {
            var (originXY, footprint) = ComputeInteriorWaterFootprint(interior);
            var water = new WorldWaterCell(key, interior, waterHeight.Value, originXY, footprint);
            index._waterCells.Add(water);
            chunk.WaterCells.Add(water);
        }

        foreach (var c in index._chunksByGrid.Values)
        {
            c.Seal(index.CellSize);
        }

        return index;
    }

    /// <summary>
    ///     Widest plausible interior water level, in absolute world units. Mirrors the bound
    ///     CellEncoder.IsPlausibleCellWater applies on the plugin-export path: no room is 100k
    ///     units deep, so a height beyond this is a stale runtime float, not authored water.
    /// </summary>
    private const float MaxPlausibleInteriorWaterAbsHeight = 10_000f;

    /// <summary>
    ///     Whether a dump exterior cell's own water override is credible: at or below the cell's
    ///     captured terrain crest plus shoreline slack — the same test the plugin encoder applies
    ///     (CellEncoder.IsPlausibleCellWater). Without an authored heightmap the override is
    ///     rejected outright: retail city worldspaces carry NO in-range per-cell water at all
    ///     (measured on TheStripWorldNew: 0 of 4096 cells), so a runtime float with no terrain to
    ///     justify it is noise, and the cell falls back to the worldspace default.
    /// </summary>
    /// <summary>
    ///     Whether a dump cell's parent worldspace plausibly authors per-cell water at all.
    ///     City-pattern worldspaces — default water BELOW default land, so the default plane never
    ///     surfaces — author none in retail (TheStripWorldNew: 0 of 4096 cells; measured), while
    ///     basin worldspaces (WastelandNV: water −2300 over land −2500) author hundreds of lake
    ///     overrides. A runtime per-cell float in a city worldspace is therefore noise even when it
    ///     happens to land near ground level, where terrain plausibility alone cannot reject it.
    /// </summary>
    private static bool WorldspaceAuthorsCellWater(
        Dictionary<uint, WorldspaceRecord> worldspacesById, CellRecord cell)
    {
        if (cell.WorldspaceFormId is not { } wsId ||
            !worldspacesById.TryGetValue(wsId, out var ws) ||
            ws.DefaultWaterHeight is not { } water ||
            WorldHeightNormalizer.IsNoWaterSentinel(water) ||
            ws.DefaultLandHeight is not { } land)
        {
            return true; // unknown pattern — the terrain-plausibility test stays the only guard
        }

        return water >= land;
    }

    private static bool IsPlausibleDumpCellWaterOverride(float waterHeight, LandHeightmap? heightmap)
    {
        if (heightmap is null) return false;

        var heights = heightmap.CalculateHeights();
        var maxTerrain = float.NegativeInfinity;
        for (var y = 0; y < heights.GetLength(0); y++)
        {
            for (var x = 0; x < heights.GetLength(1); x++)
            {
                if (heights[y, x] > maxTerrain) maxTerrain = heights[y, x];
            }
        }

        return waterHeight <= maxTerrain + 256f;
    }

    /// <summary>
    ///     Lowest captured terrain elevation in the cell, from the authored heightmap when present,
    ///     else from the runtime terrain mesh's diagnostics. Null when neither carries a finite
    ///     floor.
    /// </summary>
    private static float? TryGetTerrainMinHeight(CellRecord cell)
    {
        if (cell.Heightmap is { } heightmap)
        {
            var min = float.PositiveInfinity;
            foreach (var h in heightmap.CalculateHeights())
            {
                if (h < min) min = h;
            }

            return float.IsFinite(min) ? min : null;
        }

        if (cell.RuntimeTerrainMesh is { } mesh)
        {
            var minZ = mesh.DiagnoseQuality().MinZ;
            return float.IsFinite(minZ) ? minZ : null;
        }

        return null;
    }

    /// <summary>
    ///     Whether an interior cell's water claim is credible enough to render. ESM/ESP data is
    ///     authored, so the CELL DATA flag is the engine's own truth and is used as-is. Memory-dump
    ///     cells are different: the runtime flags byte and fWaterHeight are routinely stale or
    ///     garbage on captured cells (CellEncoder.ShouldEmitCellWater learned this first, on the
    ///     export path — dry cells rendered flooded), so a dump interior must tell a coherent
    ///     story: water flag set, the engine's own bAutoWaterLoaded not vetoing, and a level
    ///     plausible for a room.
    /// </summary>
    public static bool HasCredibleInteriorWater(CellRecord interior, bool isMemoryDump)
    {
        if (!isMemoryDump) return interior.HasWater;
        if (!interior.HasWater) return false;
        if (interior.AutoWaterLoaded == false) return false;
        if (interior.WaterHeight is not { } height ||
            !WorldHeightNormalizer.IsReportableHeight(height) ||
            MathF.Abs(height) > MaxPlausibleInteriorWaterAbsHeight)
        {
            return false;
        }

        // Bit-exact +0.0 is the TESObjectCELL stale-slot value, not an authored level. Measured
        // across three dumps: ALL 463 water-flagged interiors read exactly 0x00000000 (and their
        // retail counterparts are often not even water-flagged — NellisGenerator authors 0x21),
        // while ZERO retail interiors author XCLW as 0.0 (minimum authored |h| is 204; the rest
        // use the FLT_MAX sentinel). A cell genuinely flooded at the engine-default level would
        // carry bAutoWaterLoaded == 1, which the corroboration clause preserves.
        return BitConverter.SingleToUInt32Bits(height) != 0u || interior.AutoWaterLoaded == true;
    }

    /// <summary>
    ///     Smallest side an interior water quad is given, in world units. Enough to read as a water
    ///     surface in a small room, and far below the one-CELL floor this replaced.
    /// </summary>
    private const float MinInteriorWaterSide = 512f;

    /// <summary>
    ///     Square water footprint covering an interior's placed-object XY extent (padded), so the
    ///     water plane reaches the room walls instead of the exterior cell-sized quad.
    ///     <para>
    ///         Floored at <see cref="MinInteriorWaterSide" />, NOT at a full cell. A cell is 4096
    ///         units; most interiors are a small fraction of that, so the old floor handed every
    ///         modest room a plane many times its own size. Inside the live first-person view the
    ///         walls hide the overhang, which is why it went unnoticed — but any view from outside
    ///         the room shows it, and under a tilted camera the quad also sits at the cell's XCLW
    ///         height, so the overhang is thrown well clear of the room rather than tucked beneath
    ///         it (Hoover Dam's power plant rendered a 4096-unit sheet far below and away from a
    ///         room a few hundred units across).
    ///     </para>
    /// </summary>
    private static (Vector2 OriginXY, float FootprintSize) ComputeInteriorWaterFootprint(CellRecord interior)
    {
        // Percentile extent, not min/max: dump-recovered interiors carry orphan refs attributed to
        // the cell from far outside it, and one such placement sizes the water sheet to the orphan
        // rather than to the room (HooverDamIntPowerPlant04's quad spanned tens of thousands of
        // units). Trimming the 2% coordinate tails sizes the quad to where the room actually is.
        var xs = new List<float>(interior.PlacedObjects.Count);
        var ys = new List<float>(interior.PlacedObjects.Count);
        foreach (var obj in interior.PlacedObjects)
        {
            if (!float.IsFinite(obj.X) || !float.IsFinite(obj.Y)) continue;
            xs.Add(obj.X);
            ys.Add(obj.Y);
        }

        if (xs.Count == 0) return (Vector2.Zero, MinInteriorWaterSide);

        var (minX, maxX) = PercentileRange(xs);
        var (minY, maxY) = PercentileRange(ys);

        var side = MathF.Max(MathF.Max(maxX - minX, maxY - minY) * 1.2f, MinInteriorWaterSide);
        var centerX = (minX + maxX) * 0.5f;
        var centerY = (minY + maxY) * 0.5f;
        return (new Vector2(centerX - side * 0.5f, centerY - side * 0.5f), side);
    }

    /// <summary>
    ///     The [2nd, 98th] percentile range of <paramref name="values" />. Small sets keep plain
    ///     min/max — with a handful of refs, every placement is load-bearing.
    /// </summary>
    private static (float Min, float Max) PercentileRange(List<float> values)
    {
        values.Sort();
        if (values.Count < 20) return (values[0], values[^1]);

        var lo = (int)(values.Count * 0.02f);
        return (values[lo], values[values.Count - 1 - lo]);
    }

    internal bool TryGetCell(int gx, int gy, out CellRecord cell)
    {
        return _cellsByGrid.TryGetValue((gx, gy), out cell!);
    }

    internal bool TryGetCellAtCanvasPoint(Vector2 canvasWorldPos, out CellRecord cell)
    {
        var key = BucketFromCanvasPoint(canvasWorldPos.X, canvasWorldPos.Y);
        return _cellsByGrid.TryGetValue(key, out cell!);
    }

    internal void QueryCellsInViewport(Vector2 tlWorld, Vector2 brWorld, List<CellRecord> destination)
    {
        destination.Clear();
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(tlWorld, brWorld, 0f);
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

    /// <summary>
    ///     Enumerates cells whose XY footprint clips a <b>square</b> of half-extent
    ///     <paramref name="radius" /> centered at (<paramref name="canvasX" />, <paramref name="canvasY" />)
    ///     — the 3D viewer's "Dist" loads a square of cells, not a circle. A cell counts as inside
    ///     iff its closest point is within <paramref name="radius" /> of the center along both axes
    ///     (Chebyshev distance).
    /// </summary>
    internal void QueryCellsInRadius(float canvasX, float canvasY, float radius, List<WorldSpatialCell> destination)
    {
        destination.Clear();
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(
            new Vector2(canvasX - radius, canvasY - radius),
            new Vector2(canvasX + radius, canvasY + radius),
            0f);

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
                if (MathF.Abs(dx) < radius && MathF.Abs(dy) < radius)
                {
                    destination.Add(new WorldSpatialCell((gx, gy), cell, CellCenterCanvas(gx, gy)));
                }
            }
        }
    }

    internal void QueryWaterCellsInRadius(float canvasX, float canvasY, float radius, List<WorldWaterCell> destination)
    {
        destination.Clear();
        var chunkStartX = FloorDiv((int)MathF.Floor((canvasX - radius) / CellSize), ChunkCellSize);
        var chunkEndX = FloorDiv((int)MathF.Floor((canvasX + radius) / CellSize), ChunkCellSize);
        var gameYMin = -(canvasY + radius);
        var gameYMax = -(canvasY - radius);
        var chunkStartY = FloorDiv((int)MathF.Floor(gameYMin / CellSize), ChunkCellSize);
        var chunkEndY = FloorDiv((int)MathF.Floor(gameYMax / CellSize), ChunkCellSize);

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
                    // Square (Chebyshev) test — match QueryCellsInRadius / VisibilityCylinder.ContainsCell
                    // so water streams in the same square footprint as terrain + refs, not a circle.
                    if (MathF.Abs(dx) < radius && MathF.Abs(dy) < radius)
                    {
                        destination.Add(water);
                    }
                }
            }
        }
    }

    internal void QueryRefsInViewport(Vector2 tlWorld, Vector2 brWorld, List<PlacedReference> destination,
        float margin = 0f)
    {
        destination.Clear();
        QueryPlacedBucket(_refsByBucket, tlWorld, brWorld, margin, destination);
        AddPersistentRefsInViewport(tlWorld, brWorld, destination, margin: margin);
    }

    internal void QueryActorsInViewport(Vector2 tlWorld, Vector2 brWorld, List<PlacedReference> destination,
        float margin = 0f)
    {
        destination.Clear();
        QueryPlacedBucket(_actorsByBucket, tlWorld, brWorld, margin, destination);
        AddPersistentRefsInViewport(tlWorld, brWorld, destination, true, margin);
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

    internal void QuerySaveRefsInViewport(Vector2 tlWorld, Vector2 brWorld, List<PlacedReference> destination,
        float margin = 0f)
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
            0f);

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

    internal void QueryDanglingInViewport(Vector2 tlWorld, Vector2 brWorld, List<DanglingRefPosition> destination,
        float margin = 0f)
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
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(tlWorld, brWorld, 0f);
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

    internal (int gx, int gy) BucketFromCanvasPoint(float x, float canvasY)
    {
        var gx = (int)MathF.Floor(x / CellSize);
        var gy = (int)MathF.Floor(-canvasY / CellSize);
        return (gx, gy);
    }

    private void QueryPlacedBucket(
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

    private void QueryPlacedBucketNear(
        Dictionary<(int bx, int by), List<PlacedReference>> buckets,
        Vector2 canvasWorldPos,
        float radius,
        List<PlacedReference> destination)
    {
        var (startX, endX, startY, endY) = BucketRangeForCanvasRect(
            new Vector2(canvasWorldPos.X - radius, canvasWorldPos.Y - radius),
            new Vector2(canvasWorldPos.X + radius, canvasWorldPos.Y + radius),
            0f);

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

    private (int startX, int endX, int startY, int endY) BucketRangeForCanvasRect(
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
            (int)MathF.Floor(minX / CellSize),
            (int)MathF.Floor(maxX / CellSize),
            (int)MathF.Floor(minGameY / CellSize),
            (int)MathF.Floor(maxGameY / CellSize));
    }

    private void AddBucketed(Dictionary<(int bx, int by), List<PlacedReference>> buckets, PlacedReference obj)
    {
        var key = BucketFromCanvasPoint(obj.X, -obj.Y);
        if (!buckets.TryGetValue(key, out var list))
        {
            list = [];
            buckets[key] = list;
        }

        list.Add(obj);
    }

    private void AddBucketed(Dictionary<(int bx, int by), List<DanglingRefPosition>> buckets, DanglingRefPosition obj)
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
            var dy = canvasWorldPos.Y - -obj.Y;
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

    private Vector2 CellCenterCanvas(int gx, int gy)
    {
        return new Vector2((gx + 0.5f) * CellSize, -(gy + 0.5f) * CellSize);
    }

    private (float minX, float minY, float maxX, float maxY) CellCanvasBounds(int gx, int gy)
    {
        var minX = gx * CellSize;
        var maxX = minX + CellSize;
        var minY = -(gy + 1) * CellSize;
        var maxY = -gy * CellSize;
        return (minX, minY, maxX, maxY);
    }

    internal static bool PreferGridLookupCell(CellRecord candidate, CellRecord existing)
    {
        // A worldspace persistent dummy that reached the grid (TES4 dummies can carry XCLC (0,0))
        // must never evict the real exterior cell, however many refs it holds.
        if (candidate.IsPersistentCell != existing.IsPersistentCell)
        {
            return existing.IsPersistentCell;
        }

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

    private static bool HasTerrain(CellRecord cell)
    {
        return cell.Heightmap is not null ||
               cell.LandVisualData?.HasAny == true ||
               cell.RuntimeTerrainMesh is not null;
    }

    private static bool WorldspaceMatches(uint? attributionWorldspace, uint? activeWorldspace)
    {
        return activeWorldspace is null ||
               (attributionWorldspace.HasValue && attributionWorldspace.Value == activeWorldspace.Value);
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder != 0 && remainder < 0 != divisor < 0 ? quotient - 1 : quotient;
    }
}

/// <summary>A cell positioned at a grid key, with its precomputed canvas-space center.</summary>
internal readonly record struct WorldSpatialCell(
    (int gx, int gy) Key,
    CellRecord Cell,
    Vector2 CenterCanvas);

/// <summary>A cell's water plane: surface height plus the XY origin and side length of its footprint quad.</summary>
internal readonly record struct WorldWaterCell(
    (int gx, int gy) Key,
    CellRecord Cell,
    float Height,
    Vector2 OriginXY,
    float FootprintSize);

/// <summary>A cell paired with the navmeshes that belong to it.</summary>
internal readonly record struct NavMeshCellEntry(
    CellRecord Cell,
    IReadOnlyList<NavMeshRecord> NavMeshes);

/// <summary>A fixed-size block of grid cells (and their water cells) used as a coarse broadphase tier.</summary>
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

    /// <summary>Finalizes the chunk by computing its canvas-space bounding box from the cell-grid extent.</summary>
    internal void Seal(float cellSize)
    {
        MinCanvas = new Vector2(MinGridX * cellSize, -(MaxGridY + 1) * cellSize);
        MaxCanvas = new Vector2((MaxGridX + 1) * cellSize, -MinGridY * cellSize);
    }
}
