using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Verifies the per-worldspace cell-size unification: <see cref="WorldSpatialIndex" /> buckets and
///     queries by <see cref="WorldViewData.CellWorldSize" /> (4096 Fallout-family, 8192 Morrowind), so
///     a placed reference at a worldspace's native absolute coordinates resolves to the correct cell.
///     With the previous hardcoded 4096, an 8192-based Morrowind ref bucketed into the wrong cell and
///     never rendered with its terrain.
/// </summary>
public sealed class WorldSpatialIndexCellSizeTests
{
    private const float Morrowind = 8192f;
    private const float Fallout = 4096f;

    [Theory]
    [InlineData(Fallout)]
    [InlineData(Morrowind)]
    public void Index_AdoptsWorldspaceCellSize(float cellSize)
    {
        var (index, _, _) = BuildSingleCellIndex(cellSize, 5, 5);
        Assert.Equal(cellSize, index.CellSize);
    }

    [Fact]
    public void RefAtNativeCoords_ResolvesToItsCell_At8192()
    {
        // Cell (5,5) at Morrowind scale spans world X/Y [40960, 49152]; place a ref inside it.
        var (index, cell, placedRef) = BuildSingleCellIndex(Morrowind, 5, 5);

        // The cell is found by a radius query at its canvas center.
        var center = new Vector2((5 + 0.5f) * Morrowind, -(5 + 0.5f) * Morrowind);
        var cells = new List<WorldSpatialCell>();
        index.QueryCellsInRadius(center.X, center.Y, Morrowind * 0.25f, cells);
        Assert.Single(cells);
        Assert.Same(cell, cells[0].Cell);

        // The ref buckets into that same cell's footprint (canvas Y = -gameY).
        var tl = new Vector2(5 * Morrowind, -6 * Morrowind);
        var br = new Vector2(6 * Morrowind, -5 * Morrowind);
        var refs = new List<PlacedReference>();
        index.QueryRefsInViewport(tl, br, refs);
        Assert.Contains(placedRef, refs);
    }

    [Fact]
    public void SameRef_BucketsDifferently_BetweenScales()
    {
        // Identical 8192-based ref coords: at 8192 the ref is in cell (5,5)'s footprint; at 4096 the
        // same world point falls in grid (10,10), so a 4096 index would never pair it with cell (5,5).
        var (idx8192, _, ref8192) = BuildSingleCellIndex(Morrowind, 5, 5);
        var (idx4096, _, _) = BuildSingleCellIndex(Fallout, 5, 5);

        var refs = new List<PlacedReference>();
        idx8192.QueryRefsInViewport(new Vector2(5 * Morrowind, -6 * Morrowind),
            new Vector2(6 * Morrowind, -5 * Morrowind), refs);
        Assert.Contains(ref8192, refs);

        // At 4096 the cell (5,5) footprint is [20480, 24576] — the 41060-coord ref is nowhere near it.
        refs.Clear();
        idx4096.QueryRefsInViewport(new Vector2(5 * Fallout, -6 * Fallout), new Vector2(6 * Fallout, -5 * Fallout),
            refs);
        Assert.DoesNotContain(ref8192, refs);
    }

    private static (WorldSpatialIndex Index, CellRecord Cell, PlacedReference Ref) BuildSingleCellIndex(
        float cellSize, int gx, int gy)
    {
        // Ref near the SW corner of the (gx,gy) cell at the Morrowind scale, so its absolute coords are
        // unambiguously 8192-based.
        var placedRef = new PlacedReference
        {
            FormId = 0x201,
            BaseFormId = 0x301,
            X = gx * Morrowind + 100f,
            Y = gy * Morrowind + 100f
        };
        var cell = new CellRecord
        {
            FormId = 0x101,
            GridX = gx,
            GridY = gy,
            CellWorldSize = cellSize,
            PlacedObjects = [placedRef]
        };
        var worldspace = new WorldspaceRecord { FormId = 0x400, Cells = [cell] };
        var data = new WorldViewData
        {
            Worldspaces = [worldspace],
            InteriorCells = [],
            BoundsIndex = [],
            CategoryIndex = [],
            Resolver = FormIdResolver.Empty,
            MapMarkers = [],
            MarkersByWorldspace = new Dictionary<uint, List<PlacedReference>> { [worldspace.FormId] = [] },
            AllCells = [cell],
            CellWorldSize = cellSize,
            CellByFormId = new Dictionary<uint, CellRecord> { [cell.FormId] = cell },
            PlacedRefs = PlacedRefIndex.Empty,
            UnlinkedExteriorCells = [],
            UnlinkedMapMarkers = []
        };

        var index = WorldSpatialIndex.Build(data, [cell], [], worldspace.FormId, null);
        return (index, cell, placedRef);
    }
}