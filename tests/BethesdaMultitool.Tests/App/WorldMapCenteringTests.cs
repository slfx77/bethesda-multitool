using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     2D-map centring contracts (<see cref="WorldMapViewportMath" />):
///     <list type="bullet">
///         <item>a resize/maximize keeps the world point that was centred in the centre;</item>
///         <item>initial framing uses the bounds of cells that hold DATA, not the WRLD's declared
///         extents — WastelandNV declares cells well past its authored region, and framing those
///         puts the centre near the data's top edge;</item>
///         <item>the 2D→3D handoff clamps to those same bounds so the 3D camera lands in-bounds.</item>
///     </list>
///     The wiring lives in the App TFM (not built by the test target), so the handler/clamp call
///     sites are pinned as source contracts.
/// </summary>
public sealed class WorldMapCenteringTests
{
    private const float CellSize = 4096f;

    private static CellRecord Cell(int gridX, int gridY, bool withTerrain = false, int placedObjects = 0)
    {
        var cell = new CellRecord
        {
            GridX = gridX,
            GridY = gridY,
            CellWorldSize = CellSize,
            PlacedObjects = [.. Enumerable.Range(0, placedObjects).Select(i => new PlacedReference
            {
                FormId = (uint)(0x1000 + i)
            })]
        };

        if (withTerrain)
        {
            cell.Heightmap = new LandHeightmap { HeightDeltas = new sbyte[33 * 33] };
        }

        return cell;
    }

    /// <summary>Canvas-world point currently under the view centre (screen = world × zoom + pan).</summary>
    private static Vector2 CenterWorld(Vector2 pan, float zoom, float width, float height) =>
        new((width * 0.5f - pan.X) / zoom, (height * 0.5f - pan.Y) / zoom);

    private static WorldMapViewportMath.CanvasBounds OccupiedBounds(List<CellRecord> cells)
    {
        var bounds = WorldMapViewportMath.TryGetOccupiedCellBounds(cells);
        Assert.True(bounds.HasValue, "Expected occupied-cell bounds.");
        return bounds!.Value;
    }

    // --- (a) centre-preserving resize -----------------------------------------------------------

    [Fact]
    public void Resize_KeepsTheCentredWorldPointCentred()
    {
        const float zoom = 0.01f;
        var pan = new Vector2(120f, -340f);
        var before = CenterWorld(pan, zoom, 1200f, 800f);

        var resized = WorldMapViewportMath.PreserveCenterOnResize(pan, zoom, 1200f, 800f, 2560f, 1400f);
        var after = CenterWorld(resized, zoom, 2560f, 1400f);

        // Tolerance in WORLD units at this zoom: 0.05 world units is 5e-4 screen px.
        Assert.Equal(before.X, after.X, 0.05f);
        Assert.Equal(before.Y, after.Y, 0.05f);
    }

    [Fact]
    public void Resize_ShiftsPanByHalfTheSizeDelta()
    {
        // Algebraic form of the same rule: pan' = pan + (newSize - oldSize) / 2. Pinning it here
        // catches a regression to "leave pan alone", which pins the top-left corner instead.
        var resized = WorldMapViewportMath.PreserveCenterOnResize(
            new Vector2(50f, 50f), 0.02f, 800f, 600f, 1600f, 1000f);

        Assert.Equal(50f + 400f, resized.X, 0.001f);
        Assert.Equal(50f + 200f, resized.Y, 0.001f);
    }

    [Fact]
    public void Resize_ShrinkingAlsoPreservesTheCentre()
    {
        const float zoom = 0.005f;
        var pan = new Vector2(-2000f, 900f);
        var before = CenterWorld(pan, zoom, 2560f, 1400f);

        var resized = WorldMapViewportMath.PreserveCenterOnResize(pan, zoom, 2560f, 1400f, 900f, 640f);
        var after = CenterWorld(resized, zoom, 900f, 640f);

        Assert.Equal(before.X, after.X, 0.5f);
        Assert.Equal(before.Y, after.Y, 0.5f);
    }

    [Theory]
    [InlineData(0f, 800f, 600f, 1600f, 1200f)] // degenerate zoom
    [InlineData(0.01f, 0f, 600f, 1600f, 1200f)] // first layout: no previous size
    [InlineData(0.01f, 800f, 0f, 1600f, 1200f)]
    [InlineData(0.01f, 800f, 600f, 0f, 1200f)] // collapsed (cell-browser mode)
    [InlineData(0.01f, 800f, 600f, 1600f, 0f)]
    public void Resize_DegenerateSizesLeaveThePanUntouched(
        float zoom, float oldWidth, float oldHeight, float newWidth, float newHeight)
    {
        var pan = new Vector2(17f, -23f);

        Assert.Equal(
            pan,
            WorldMapViewportMath.PreserveCenterOnResize(pan, zoom, oldWidth, oldHeight, newWidth, newHeight));
    }

    // --- (b) occupied-cell bounds ---------------------------------------------------------------

    [Fact]
    public void OccupiedBounds_IgnoreDeclaredCellsWithNoData()
    {
        // Data occupies grid rows 0..3; the worldspace also declares empty stub cells up to row 40
        // (the WastelandNV shape). Framing the declared rect would centre ~row 20 — off the data.
        var cells = new List<CellRecord>();
        for (var y = 0; y <= 3; y++)
        {
            cells.Add(Cell(0, y, withTerrain: true));
        }

        for (var y = 4; y <= 40; y++)
        {
            cells.Add(Cell(0, y));
        }

        var bounds = OccupiedBounds(cells);

        Assert.Equal(-4 * CellSize, bounds.MinY, 0.001f); // -(maxGridY + 1) * cellSize
        Assert.Equal(0f, bounds.MaxY, 0.001f);
        Assert.Equal(-2 * CellSize, bounds.Center.Y, 0.001f); // centre of rows 0..3, not of rows 0..40
    }

    [Fact]
    public void OccupiedBounds_CountPlacedReferencesAsData()
    {
        // A cell can be authored without terrain (references only) — it must still frame in.
        var cells = new List<CellRecord>
        {
            Cell(0, 0, withTerrain: true),
            Cell(5, 0, placedObjects: 3),
            Cell(60, 0)
        };

        var bounds = OccupiedBounds(cells);

        Assert.Equal(0f, bounds.MinX, 0.001f);
        Assert.Equal(6 * CellSize, bounds.MaxX, 0.001f);
    }

    [Fact]
    public void OccupiedBounds_FallBackToEveryGridCellWhenNoneCarryData()
    {
        var cells = new List<CellRecord> { Cell(-2, -2), Cell(1, 1) };

        var bounds = OccupiedBounds(cells);

        Assert.Equal(-2 * CellSize, bounds.MinX, 0.001f);
        Assert.Equal(2 * CellSize, bounds.MaxX, 0.001f);
    }

    [Fact]
    public void OccupiedBounds_NullWhenNoCellHasGridCoordinates()
    {
        // Interiors carry no grid coordinates — there is nothing to frame.
        Assert.Null(WorldMapViewportMath.TryGetOccupiedCellBounds([]));
        Assert.Null(WorldMapViewportMath.TryGetOccupiedCellBounds([new CellRecord()]));
    }

    [Fact]
    public void OccupiedBounds_UseTheCellsOwnWorldSize()
    {
        // Morrowind cells are 8192 world units; the bounds must not assume the Fallout default.
        var cells = new List<CellRecord>
        {
            new()
            {
                GridX = 0,
                GridY = 0,
                CellWorldSize = 8192f,
                Heightmap = new LandHeightmap { HeightDeltas = new sbyte[33 * 33] }
            }
        };

        var bounds = OccupiedBounds(cells);

        Assert.Equal(8192f, bounds.MaxX, 0.001f);
        Assert.Equal(-8192f, bounds.MinY, 0.001f);
    }

    // --- (c) 2D→3D handoff clamp ----------------------------------------------------------------

    [Fact]
    public void Clamp_PullsAnOutOfBoundsCentreOntoTheDataRect()
    {
        var bounds = new WorldMapViewportMath.CanvasBounds(0f, -4 * CellSize, 2 * CellSize, 0f);

        var clamped = bounds.Clamp(new Vector2(50 * CellSize, 30 * CellSize));

        Assert.Equal(2 * CellSize, clamped.X, 0.001f);
        Assert.Equal(0f, clamped.Y, 0.001f);
    }

    [Fact]
    public void Clamp_LeavesAnInBoundsCentreAlone()
    {
        var bounds = new WorldMapViewportMath.CanvasBounds(0f, -4 * CellSize, 2 * CellSize, 0f);
        var inside = new Vector2(CellSize, -CellSize);

        Assert.Equal(inside, bounds.Clamp(inside));
    }

    // --- App wiring (source contracts) ----------------------------------------------------------

    [Fact]
    public void MapCanvasResizeIsHookedAndReDerivesThePan()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.xaml.cs");

        SourceContract.AssertOrder(source,
            "MapCanvas.SizeChanged += MapCanvas_SizeChanged;",
            "private void MapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)",
            "_panOffset = WorldMapViewportMath.PreserveCenterOnResize(");
    }

    [Fact]
    public void WorldspaceFramingGoesThroughTheOccupiedCellBounds()
    {
        var source = SourceContract.ReadAppSource("WorldMapViewportHelper.cs");
        var body = SourceContract.Extract(source,
            "internal static void ZoomToFitWorldspace(",
            "internal static void ZoomToFitCell(");

        Assert.Contains("WorldMapViewportMath.TryGetOccupiedCellBounds(cells)", body, StringComparison.Ordinal);
        // The declared-extent bbox is exactly what this replaced; a reintroduced min/max over every
        // grid cell would silently restore the off-centre framing.
        Assert.DoesNotContain("cellsWithGrid", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoDToThreeDHandoffClampsToTheSameBounds()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Navigation.cs");
        var body = SourceContract.Extract(source,
            "internal WorldViewFocus CaptureViewFocus()",
            "internal void ApplyViewFocus(WorldViewFocus focus)");

        SourceContract.AssertOrder(body,
            "WorldMapViewportMath.TryGetOccupiedCellBounds(GetActiveCells())",
            "dataBounds.Clamp(center2D)");
    }
}
