using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Verifies the grid-size-aware terrain path that renders Morrowind's native 65×65 LAND at full
///     resolution (instead of downsampling to 33×33). The Fallout-family 33×33 path stays the default;
///     these tests cover the 65×65 mesh geometry, index buffer, and per-vertex blend table so the
///     vertex / index / blend-weight stream lengths all agree before they reach the (GPU-only) renderer.
/// </summary>
public sealed class TerrainNativeGridSizeTests
{
    private const int N = 65; // Morrowind LAND grid edge
    private const float CellSize = 8192f;

    private static CellRecord MorrowindCell(int gx, int gy, float flatHeight)
    {
        var exact = new float[N, N];
        for (var j = 0; j < N; j++)
        {
            for (var i = 0; i < N; i++)
            {
                exact[j, i] = flatHeight;
            }
        }

        return new CellRecord
        {
            FormId = 0x1000,
            GridX = gx,
            GridY = gy,
            CellWorldSize = CellSize,
            Heightmap = new LandHeightmap { HeightDeltas = [], ExactHeights = exact }
        };
    }

    [Fact]
    public void ResolveGridSize_Is65_ForMorrowindHeightmap_And33_Otherwise()
    {
        Assert.Equal(65, TerrainMeshBuilder.ResolveGridSize(MorrowindCell(0, 0, 0f)));

        var fnv = new CellRecord
        {
            FormId = 1,
            GridX = 0,
            GridY = 0,
            Heightmap = new LandHeightmap { HeightDeltas = new sbyte[33 * 33] }
        };
        Assert.Equal(33, TerrainMeshBuilder.ResolveGridSize(fnv));
    }

    [Fact]
    public void Build_NativeMorrowindCell_ProducesFullResolutionMesh()
    {
        var mesh = TerrainMeshBuilder.Build(MorrowindCell(3, 4, 500f));

        Assert.NotNull(mesh);
        Assert.Equal(N * N, mesh.Value.Vertices.Length); // 4225, not 1089
        Assert.Equal((N - 1) * (N - 1) * 6, mesh.Value.Indices.Length); // 24576

        var verts = mesh.Value.Vertices;
        const float spacing = CellSize / (N - 1); // 128

        // SW corner at the cell's absolute world origin.
        Assert.Equal(new Vector3(3 * CellSize, 4 * CellSize, 500f), verts[0].Position);

        // NE corner one full (8192-unit) cell away.
        var last = N * N - 1;
        Assert.Equal(new Vector3(4 * CellSize, 5 * CellSize, 500f), verts[last].Position);

        // A native interior vertex that does NOT exist on the 33×33 grid (odd column/row) — proves the
        // mesh is built at native resolution, not the downsampled grid.
        var idx = 1 * N + 1;
        Assert.Equal(new Vector3(3 * CellSize + spacing, 4 * CellSize + spacing, 500f), verts[idx].Position);

        // Flat surface → +Z normals everywhere.
        foreach (var v in verts)
        {
            Assert.Equal(1f, v.Normal.Z, 5);
        }
    }

    [Fact]
    public void BuildSharedIndexBufferData_65_HasValidTopology()
    {
        var indices = TerrainMeshBuilder.BuildSharedIndexBufferData(N);

        Assert.Equal((N - 1) * (N - 1) * 6, indices.Length);
        Assert.Equal(TerrainMeshBuilder.IndexCountFor(N), indices.Length);

        // Every index addresses a valid vertex in the 65×65 grid, and all four corners are referenced.
        var max = 0;
        foreach (var i in indices)
        {
            Assert.True(i < N * N);
            if (i > max) max = i;
        }

        Assert.Equal(N * N - 1, max); // NE corner vertex is referenced
    }

    [Fact]
    public void CellLayerWeightTable_At65_CoversNativeGrid()
    {
        var table = new CellLayerWeightTable(N);
        Assert.Equal(N, table.GridSize);
        Assert.Equal((N + 1) / 2, table.QuadEdge); // 33
        Assert.Equal(N * N, table.Vertices.Length);

        // At(vx, vy) maps to the row-major native grid.
        table.At(10, 20).Add(0x10, 1f);
        Assert.Equal(1, table.Vertices[20 * N + 10].Count);
    }

    [Fact]
    public void Project_At65_ProducesMatchingVertexWeightLength()
    {
        // Four per-quadrant base layers (Morrowind's collapsed VTEX), built at native resolution.
        var layers = new List<LandTextureLayer>
        {
            new() { Kind = LandTextureLayerKind.Base, TextureFormId = 0x10, Quadrant = 0 },
            new() { Kind = LandTextureLayerKind.Base, TextureFormId = 0x11, Quadrant = 1 },
            new() { Kind = LandTextureLayerKind.Base, TextureFormId = 0x12, Quadrant = 2 },
            new() { Kind = LandTextureLayerKind.Base, TextureFormId = 0x13, Quadrant = 3 }
        };

        var table = CellLayerWeightTable.Build(N, layers);
        Assert.NotNull(table);
        Assert.Equal(N * N, table.Vertices.Length);

        var set = CellTerrainTextureSet.Project(table);
        Assert.NotNull(set);
        Assert.Equal(N * N, set.CellVertexCount);
        // The per-vertex blend stream must be exactly one entry per native vertex × SlotVectors,
        // so it lines up 1:1 with the 65×65 terrain vertex buffer in the renderer.
        Assert.Equal(N * N * CellTerrainTextureSet.SlotVectors, set.VertexWeights.Length);
    }

    [Fact]
    public void BuildFromVtexGrid_GivesPerVtexGranularity_NotFourQuadrants()
    {
        // 16 distinct textures, one per VTEX column — the 4-quadrant collapse could surface at most 4.
        var grid = new uint[16 * 16];
        for (var r = 0; r < 16; r++)
        {
            for (var c = 0; c < 16; c++)
            {
                grid[r * 16 + c] = (uint)(0x100 + c);
            }
        }

        var table = CellLayerWeightTable.BuildFromVtexGrid(N, grid);
        Assert.Equal(N * N, table.Vertices.Length);

        var set = CellTerrainTextureSet.Project(table);
        Assert.NotNull(set);
        Assert.Equal(N * N * CellTerrainTextureSet.SlotVectors, set.VertexWeights.Length);
        // Far more than the 4 a quadrant collapse would yield — proves textures follow the 16×16 grid.
        Assert.True(set.ActiveSlotCount > 4, $"expected >4 texture slots, got {set.ActiveSlotCount}");
    }

    [Fact]
    public void BuildFromVtexGrid_AllDefault_ResolvesToSingleDefaultSlot()
    {
        var grid = new uint[16 * 16]; // all zero = engine-default land texture
        var set = CellTerrainTextureSet.Project(CellLayerWeightTable.BuildFromVtexGrid(N, grid));

        Assert.NotNull(set);
        Assert.Equal(1, set.ActiveSlotCount);
        Assert.Equal(CellLayerWeightTable.EngineDefaultSentinelFormId, set.SlotFormIds[0]);
    }
}