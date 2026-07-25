using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class WorldSpatialIndex3DTests
{
    [Fact]
    public void BuildFor3D_PreservesCellAndWaterQueriesWithoutBuildingMapOverlayBuckets()
    {
        var placedRef = new PlacedReference { FormId = 0x201, X = 256f, Y = 256f };
        var actor = new PlacedReference { FormId = 0x202, X = 512f, Y = 512f, RecordType = "ACHR" };
        var marker = new PlacedReference { FormId = 0x203, X = 600f, Y = 600f, IsMapMarker = true };
        var origin = new CellRecord
        {
            FormId = 0x101,
            GridX = 0,
            GridY = 0,
            WaterHeight = 100f,
            PlacedObjects = [placedRef, actor]
        };
        var far = new CellRecord
        {
            FormId = 0x102,
            GridX = 10,
            GridY = 10,
            PlacedObjects = [new PlacedReference { FormId = 0x204, X = 41_000f, Y = 41_000f }]
        };
        var cells = new List<CellRecord> { origin, far };
        var data = CreateData(cells);

        var mapIndex = WorldSpatialIndex.Build(data, cells, [marker], null, null);
        var index3D = WorldSpatialIndex.BuildFor3D(data, cells, null);

        Assert.True(mapIndex.IncludesMapOverlays);
        Assert.False(index3D.IncludesMapOverlays);
        Assert.Equal(mapIndex.CellCount, index3D.CellCount);

        var mapCells = new List<WorldSpatialCell>();
        var cells3D = new List<WorldSpatialCell>();
        mapIndex.QueryCellsInRadius(2048f, -2048f, 256f, mapCells);
        index3D.QueryCellsInRadius(2048f, -2048f, 256f, cells3D);
        Assert.Equal(mapCells.Select(c => c.Cell.FormId), cells3D.Select(c => c.Cell.FormId));

        var mapWater = new List<WorldWaterCell>();
        var water3D = new List<WorldWaterCell>();
        mapIndex.QueryWaterCellsInRadius(2048f, -2048f, 256f, mapWater);
        index3D.QueryWaterCellsInRadius(2048f, -2048f, 256f, water3D);
        Assert.Equal(mapWater, water3D);
        Assert.Single(water3D);

        // Performance invariant: the specialized 3D index must not retain a second global copy of
        // placed references (or the actor subset). ReferenceRenderer12 starts from the cells above
        // and asks WorldRenderCache for its tighter, lazy per-cell candidate index instead.
        var refs = new List<PlacedReference>();
        index3D.QueryRefsInViewport(Vector2.Zero, new Vector2(4096f, -4096f), refs);
        Assert.Empty(refs);
        index3D.QueryActorsInViewport(Vector2.Zero, new Vector2(4096f, -4096f), refs);
        Assert.Empty(refs);
        index3D.QueryMarkersNear(new Vector2(600f, -600f), 64f, refs);
        Assert.Empty(refs);

        mapIndex.QueryRefsInViewport(Vector2.Zero, new Vector2(4096f, -4096f), refs);
        Assert.Equal(2, refs.Count);
        mapIndex.QueryActorsInViewport(Vector2.Zero, new Vector2(4096f, -4096f), refs);
        Assert.Single(refs);
        Assert.Same(actor, refs[0]);
        mapIndex.QueryMarkersNear(new Vector2(600f, -600f), 64f, refs);
        Assert.Single(refs);
        Assert.Same(marker, refs[0]);
    }

    [Fact]
    public void BuildInterior_UsesTheSameCellOnly3DContract()
    {
        var placedRef = new PlacedReference { FormId = 0x301, X = 5000f, Y = 9000f };
        var actor = new PlacedReference
        {
            FormId = 0x302,
            X = 7000f,
            Y = 11_000f,
            RecordType = "ACHR"
        };
        var interior = new CellRecord
        {
            FormId = 0x103,
            Flags = 0x03,
            WaterHeight = 50f,
            PlacedObjects = [placedRef, actor]
        };
        var data = CreateData([interior]);

        var index = WorldSpatialIndex.BuildInterior(data, interior);

        Assert.False(index.IncludesMapOverlays);
        var key = WorldSpatialIndex.SyntheticInteriorKey(interior);
        var centerX = (key.gx + 0.5f) * index.CellSize;
        var centerCanvasY = -(key.gy + 0.5f) * index.CellSize;
        var cells = new List<WorldSpatialCell>();
        index.QueryCellsInRadius(centerX, centerCanvasY, 256f, cells);
        Assert.Single(cells);
        Assert.Same(interior, cells[0].Cell);
        Assert.Single(index.WaterCells);

        var refs = new List<PlacedReference>();
        index.QueryRefsInViewport(
            new Vector2(0f, -16_000f),
            new Vector2(16_000f, 0f),
            refs);
        Assert.Empty(refs);
        index.QueryActorsInViewport(
            new Vector2(0f, -16_000f),
            new Vector2(16_000f, 0f),
            refs);
        Assert.Empty(refs);
    }

    private static WorldViewData CreateData(List<CellRecord> cells)
    {
        return new WorldViewData
        {
            Worldspaces = [],
            InteriorCells = [],
            BoundsIndex = [],
            CategoryIndex = [],
            Resolver = FormIdResolver.Empty,
            MapMarkers = [],
            MarkersByWorldspace = [],
            AllCells = cells,
            CellByFormId = cells.ToDictionary(c => c.FormId),
            RefrToCellIndex = [],
            UnlinkedExteriorCells = [],
            UnlinkedMapMarkers = []
        };
    }
}