using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public class RuntimeTerrainQuadrantMeshBuilderTests
{
    [Fact]
    public void TryBuild_FourTiledQuadrants_PreservesCompleteGridIncludingZeroOrigin()
    {
        var mesh = RuntimeTerrainQuadrantMeshBuilder.TryBuild(
            CreateTiledQuadrants(),
            [],
            []);

        Assert.NotNull(mesh);
        var centerOffset = (16 * RuntimeTerrainMesh.GridSize + 16) * 3;
        Assert.Equal(0f, mesh.Vertices[centerOffset]);
        Assert.Equal(0f, mesh.Vertices[centerOffset + 1]);
        Assert.Equal(0f, mesh.Vertices[centerOffset + 2]);

        var diagnostic = mesh.DiagnoseQuality();
        Assert.Equal(RuntimeTerrainMesh.VertexCount, diagnostic.SourceSampleCount);
        Assert.Equal(100f, diagnostic.SourceCoveragePercent);
        Assert.Equal("Complete", diagnostic.Classification);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TryBuild_IncompleteQuadrantCapture_IsRejected(int capturedQuadrants)
    {
        var quadrants = CreateTiledQuadrants().Take(capturedQuadrants).ToArray();

        var mesh = RuntimeTerrainQuadrantMeshBuilder.TryBuild(quadrants, [], []);

        Assert.Null(mesh);
    }

    [Fact]
    public void TryBuild_ZeroFilledQuadrants_AreRejectedAsUninitialized()
    {
        var quadrants = Enumerable.Range(0, RuntimeTerrainQuadrantMeshBuilder.QuadrantCount)
            .Select(slot => new RuntimeTerrainFloatArraySlot(
                slot,
                new float[RuntimeTerrainQuadrantMeshBuilder.QuadrantVertexCount * 3],
                0x1000 + slot * 0x1000))
            .ToArray();

        var mesh = RuntimeTerrainQuadrantMeshBuilder.TryBuild(quadrants, [], []);

        Assert.Null(mesh);
    }

    private static RuntimeTerrainFloatArraySlot[] CreateTiledQuadrants()
    {
        return
        [
            CreateQuadrant(0, -2048f, -2048f),
            CreateQuadrant(1, 0f, -2048f),
            CreateQuadrant(2, -2048f, 0f),
            CreateQuadrant(3, 0f, 0f)
        ];
    }

    private static RuntimeTerrainFloatArraySlot CreateQuadrant(int slot, float minX, float minY)
    {
        const int size = 17;
        const float spacing = 128f;
        var data = new float[RuntimeTerrainQuadrantMeshBuilder.QuadrantVertexCount * 3];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var offset = (y * size + x) * 3;
                var localX = minX + x * spacing;
                var localY = minY + y * spacing;
                data[offset] = localX;
                data[offset + 1] = localY;
                data[offset + 2] = localX * 0.25f + localY * 0.5f;
            }
        }

        return new RuntimeTerrainFloatArraySlot(slot, data, 0x1000 + slot * 0x1000);
    }
}