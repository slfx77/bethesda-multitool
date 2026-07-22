using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class TerrainRaycasterTests
{
    [Fact]
    public void RaycastNearest_VerticalRayHitsCachedFlatTerrain()
    {
        var cell = FlatCell(33, 125f);
        var cache = new WorldRenderCache();

        var hit = TerrainRaycaster.RaycastNearest(
            cell, new Vector3(2048f, 2048f, 1000f), -Vector3.UnitZ, out var t, cache);

        Assert.True(hit);
        Assert.InRange(t, 874.999f, 875.001f);
    }

    [Fact]
    public void RaycastNearest_VerticalRayFromBelowMissesCulledUnderside()
    {
        var cell = FlatCell(33, 125f);
        var cache = new WorldRenderCache();

        var hit = TerrainRaycaster.RaycastNearest(
            cell, new Vector3(2048f, 2048f, 0f), Vector3.UnitZ, out _, cache);

        Assert.False(hit);
    }

    [Fact]
    public void RaycastNearest_UsesSameQuadDiagonalAsTerrainMeshBuilder()
    {
        var heights = new float[33, 33];
        // First quad's v11 only. The renderer's second triangle is (v01,v10,v11), whose plane is
        // z=x+y-128. At (96,96), the exact rendered height is therefore 64.
        heights[1, 1] = 128f;
        var cell = Cell(heights, 4096f);

        Assert.True(TerrainRaycaster.RaycastNearest(
            cell, new Vector3(96f, 96f, 200f), -Vector3.UnitZ, out var t));

        Assert.InRange(t, 135.999f, 136.001f);
    }

    [Fact]
    public void RaycastNearest_FnvCacheUsesRecoveredCheckerboardAndOtherProfilesStayFixed()
    {
        var heights = new float[33, 33];
        heights[1, 1] = 128f; // first quad's v11 only
        var cell = Cell(heights, 4096f);
        var fnvCache = new WorldRenderCache { Game = BethesdaGame.FalloutNewVegas };
        var skyrimCache = new WorldRenderCache { Game = BethesdaGame.Skyrim };
        var origin = new Vector3(32f, 32f, 200f);

        Assert.True(TerrainRaycaster.RaycastNearest(cell, origin, -Vector3.UnitZ, out var fnvT, fnvCache));
        Assert.True(TerrainRaycaster.RaycastNearest(cell, origin, -Vector3.UnitZ, out var skyrimT, skyrimCache));

        // FNV's v00-v11 plane is z=32 here. The fixed/default split's south-west plane stays z=0.
        Assert.InRange(fnvT, 167.999f, 168.001f);
        Assert.InRange(skyrimT, 199.999f, 200.001f);
    }

    [Fact]
    public void RaycastNearest_RespectsMaximumDistance()
    {
        var cell = FlatCell(33, 0f);

        Assert.False(TerrainRaycaster.RaycastNearest(
            cell, new Vector3(512f, 512f, 100f), -Vector3.UnitZ, out _, maxDistance: 99f));
        Assert.True(TerrainRaycaster.RaycastNearest(
            cell, new Vector3(512f, 512f, 100f), -Vector3.UnitZ, out var t, maxDistance: 100f));
        Assert.InRange(t, 99.999f, 100.001f);
    }

    [Fact]
    public void RaycastNearest_RayPointingAwayFromCellMissesFootprint()
    {
        var cell = FlatCell(33, 0f);
        var direction = Vector3.Normalize(new Vector3(-1f, 0f, -1f));

        Assert.False(TerrainRaycaster.RaycastNearest(
            cell, new Vector3(-10f, 100f, 100f), direction, out _));
    }

    [Fact]
    public void RaycastNearest_PreservesNative65GridAndCellSize()
    {
        var cell = FlatCell(65, 75f, 8192f);

        Assert.True(TerrainRaycaster.RaycastNearest(
            cell, new Vector3(4096f, 4096f, 500f), -Vector3.UnitZ, out var t));

        Assert.InRange(t, 424.999f, 425.001f);
    }

    [Fact]
    public void RaycastNearest_Cached33GridKeepsRendererFixed128Spacing()
    {
        // TerrainMeshBuilder's cached 33x33 overload spans 4096 units at 128 units/vertex even when
        // CellWorldSize is anomalously larger. The raycaster must not invent an 8192-unit GPU mesh.
        var cell = FlatCell(33, 75f, 8192f);
        var cache = new WorldRenderCache();

        Assert.True(TerrainRaycaster.RaycastNearest(
            cell, new Vector3(4000f, 4000f, 500f), -Vector3.UnitZ, out var insideT, cache));
        Assert.InRange(insideT, 424.999f, 425.001f);
        Assert.False(TerrainRaycaster.RaycastNearest(
            cell, new Vector3(6000f, 6000f, 500f), -Vector3.UnitZ, out _, cache));
    }

    [Fact]
    public void IsHitBehindTerrain_TransformedContactNear100kAllowsUlpDrift()
    {
        const float terrainHeight = 100_000f;
        const int grid = 24;
        var heights = new float[33, 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                heights[y, x] = terrainHeight;
            }
        }
        var cell = Cell(heights, 4096f, grid, grid);
        var cache = new WorldRenderCache();
        var worldXy = grid * 4096f + 2048f;
        var rayOrigin = new Vector3(worldXy, worldXy, 200_000f);
        var rayDirection = -Vector3.UnitZ;

        Assert.True(TerrainRaycaster.RaycastNearest(
            cell, rayOrigin, rayDirection, out var terrainT, cache));

        // Put the transformed collision plane one representable world-space float below LAND. This
        // models adjacent-float drift between the inverse placement path and the world LAND path.
        var collisionHeight = MathF.BitDecrement(terrainHeight);
        var world = PlacedReferenceTransform.ComposeWorldMatrix(
            worldXy, worldXy, collisionHeight, 0f, 0f, 0f, 1.25f);
        Assert.True(Matrix4x4.Invert(world, out var inverseWorld));
        var collision = new CollisionMesh(
        [
            new Vector3(-512f, -512f, 0f),
            new Vector3(512f, -512f, 0f),
            new Vector3(512f, 512f, 0f),
            new Vector3(-512f, 512f, 0f)
        ], [0, 1, 2, 0, 2, 3]);
        var localOrigin = Vector3.Transform(rayOrigin, inverseWorld);
        var localDirection = Vector3.TransformNormal(rayDirection, inverseWorld);
        Assert.True(collision.RaycastNearest(localOrigin, localDirection, out var collisionT));

        Assert.True(collisionT > terrainT, $"expected adjacent-float drift: collision={collisionT}, terrain={terrainT}");
        Assert.False(TerrainRaycaster.IsHitBehindTerrain(collisionT, terrainT));
        Assert.True(TerrainRaycaster.IsHitBehindTerrain(terrainT + 1f, terrainT));
    }

    private static CellRecord FlatCell(int gridSize, float height, float cellSize = 4096f)
    {
        var heights = new float[gridSize, gridSize];
        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                heights[y, x] = height;
            }
        }
        return Cell(heights, cellSize);
    }

    private static CellRecord Cell(float[,] heights, float cellSize, int gridX = 0, int gridY = 0) => new()
    {
        GridX = gridX,
        GridY = gridY,
        CellWorldSize = cellSize,
        Heightmap = new LandHeightmap
        {
            HeightDeltas = new sbyte[heights.Length],
            ExactHeights = heights
        }
    };
}
