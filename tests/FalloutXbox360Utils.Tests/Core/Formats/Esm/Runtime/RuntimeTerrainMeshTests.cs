using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public class RuntimeTerrainMeshTests
{
    private const float OriginX = 8192f;
    private const float OriginY = -4096f;
    private const float CellSize = 4096f;

    [Fact]
    public void ToLandHeightmap_Full33Grid_ReconstructsUnchanged()
    {
        var mesh = CreateTerrainMesh(33, strided: false);

        var heightmap = mesh.ToLandHeightmap();
        var heights = heightmap.CalculateHeights();

        AssertHeightsMatchExpected(heights);
        Assert.InRange(heightmap.EncodedRoundTripMaxError, 0f, 4.01f);
        var lod = mesh.DetectLodLevel();
        Assert.Equal(33, lod.VerticesPerRow);
        Assert.Equal(0, lod.Level);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(16)]
    [InlineData(9)]
    [InlineData(8)]
    [InlineData(5)]
    [InlineData(4)]
    public void ToLandHeightmap_LowerLodDenseGrid_InterpolatesToCanonical33(int sourceGridSize)
    {
        var mesh = CreateTerrainMesh(sourceGridSize, strided: false);

        var heightmap = mesh.ToLandHeightmap();
        var heights = heightmap.CalculateHeights();

        AssertHeightsMatchExpected(heights);
        Assert.Equal(sourceGridSize, mesh.DetectLodLevel().VerticesPerRow);
        Assert.InRange(heightmap.EncodedRoundTripMaxError, 0f, 4.01f);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(16)]
    [InlineData(9)]
    [InlineData(8)]
    [InlineData(5)]
    [InlineData(4)]
    public void ToLandHeightmap_LowerLodStridedGrid_InterpolatesToCanonical33(int sourceGridSize)
    {
        var mesh = CreateTerrainMesh(sourceGridSize, strided: true);

        var heightmap = mesh.ToLandHeightmap();
        var heights = heightmap.CalculateHeights();

        AssertHeightsMatchExpected(heights);
        Assert.Equal(sourceGridSize, mesh.DetectLodLevel().VerticesPerRow);
    }

    [Fact]
    public void ToLandHeightmap_TrailingFiniteGarbage_IsIgnored()
    {
        var mesh = CreateTerrainMesh(9, strided: false, finiteTrailingGarbage: true);

        var heightmap = mesh.ToLandHeightmap();
        var heights = heightmap.CalculateHeights();

        AssertHeightsMatchExpected(heights);
        Assert.Equal(9, mesh.DetectLodLevel().VerticesPerRow);
    }

    [Fact]
    public void ToLandHeightmap_MidRangeZOutlier_IsIgnored()
    {
        var mesh = CreateTerrainMesh(17, strided: false);
        mesh.Vertices[(8 * 17 + 8) * 3 + 2] = 7_500f;

        var heightmap = mesh.ToLandHeightmap();
        var heights = heightmap.CalculateHeights();

        Assert.InRange(heights[16, 16], 1048f, 1064f);
        Assert.DoesNotContain(Flatten(heights), height => height > 2_500f);
    }

    [Fact]
    public void ToLandHeightmap_SanitizedLowerLodWithZeroTrailingSlots_Reconstructs()
    {
        var mesh = CreateTerrainMeshWithZeroTrailingSlots(9).SanitizeVertices();

        var heightmap = mesh.ToLandHeightmap();
        var heights = heightmap.CalculateHeights();

        AssertHeightsMatchExpected(heights);
        Assert.Equal(9, mesh.DetectLodLevel().VerticesPerRow);
    }

    [Fact]
    public void ToLandHeightmap_SanitizedFlatZeroFullGrid_RemainsReconstructable()
    {
        var mesh = CreateFlatZeroFullGrid().SanitizeVertices();

        var heights = mesh.ToLandHeightmap().CalculateHeights();

        Assert.All(Flatten(heights), h => Assert.Equal(0f, h));
        Assert.Equal(33, mesh.DetectLodLevel().VerticesPerRow);
    }

    [Fact]
    public void ToLandHeightmap_InsufficientCoverage_IsRejected()
    {
        var mesh = CreateSparseInvalidMesh();

        Assert.Throws<InvalidOperationException>(() => mesh.ToLandHeightmap());
        Assert.Equal(-1, mesh.DetectLodLevel().Level);
    }

    [Fact]
    public void ToLandHeightmap_BaseHeight_OffsetsReconstructedHeights()
    {
        var mesh = CreateTerrainMesh(17, strided: false);

        var heightmap = mesh.ToLandHeightmap(512f);
        var heights = heightmap.CalculateHeights();
        var diagnostic = mesh.DiagnoseQuality(baseHeight: 512f);

        for (var y = 0; y < RuntimeTerrainMesh.GridSize; y++)
        {
            for (var x = 0; x < RuntimeTerrainMesh.GridSize; x++)
            {
                var worldX = OriginX + x * 128f;
                var worldY = OriginY + y * 128f;
                Assert.InRange(MathF.Abs(heights[y, x] - (HeightAtWorld(worldX, worldY) + 512f)), 0f, 0.05f);
            }
        }

        Assert.InRange(diagnostic.MinZ, 1511f, 1513f);
        Assert.InRange(heightmap.EncodedRoundTripMaxError, 0f, 4.01f);
    }

    [Fact]
    public void ToLandHeightmap_LocalPartialCapture_KeepsSamplesAtCanonicalCellPositions()
    {
        var mesh = CreateLocalPartialTerrainMesh(17);

        var heightmap = mesh.ToLandHeightmap();
        var heights = heightmap.CalculateHeights();
        var diagnostic = mesh.DiagnoseQuality();

        Assert.Equal(17, diagnostic.DetectedGridSize);
        Assert.InRange(diagnostic.SourceCoveragePercent, 20f, 30f);
        Assert.InRange(MathF.Abs(heights[16, 16] - 1616f), 0f, 0.05f);
    }

    [Fact]
    public void DiagnoseQuality_ReportsRuntimeVertexColors()
    {
        var mesh = CreateTerrainMesh(17, strided: false) with
        {
            Colors = new float[RuntimeTerrainMesh.VertexCount * 4]
        };

        var diagnostic = mesh.DiagnoseQuality();

        Assert.True(diagnostic.HasRuntimeVertexColors);
        Assert.Equal("RuntimeMESH", diagnostic.HeightSource);
        Assert.Equal(17, diagnostic.DetectedGridSize);
    }

    [Fact]
    public void ToLandVertexColorBytes_LowerLodGrid_InterpolatesToCanonicalVclr()
    {
        var mesh = CreateTerrainMesh(17, strided: false) with
        {
            Colors = CreateTerrainColors(17, strided: false)
        };

        var vclr = mesh.ToLandVertexColorBytes();

        Assert.NotNull(vclr);
        Assert.Equal(RuntimeTerrainMesh.VertexCount * 3, vclr.Length);
        Assert.Equal(0, vclr[0]);
        Assert.Equal(0, vclr[1]);
        Assert.Equal(128, vclr[2]);

        var center = (16 * RuntimeTerrainMesh.GridSize + 16) * 3;
        Assert.InRange(vclr[center], 127, 128);
        Assert.InRange(vclr[center + 1], 127, 128);
        Assert.Equal(128, vclr[center + 2]);

        var last = (RuntimeTerrainMesh.VertexCount - 1) * 3;
        Assert.Equal(255, vclr[last]);
        Assert.Equal(255, vclr[last + 1]);
        Assert.Equal(128, vclr[last + 2]);
    }

    private static RuntimeTerrainMesh CreateTerrainMesh(
        int sourceGridSize,
        bool strided,
        bool finiteTrailingGarbage = false)
    {
        var vertices = new float[RuntimeTerrainMesh.VertexCount * 3];
        if (finiteTrailingGarbage)
        {
            FillFiniteGarbage(vertices);
        }
        else
        {
            Array.Fill(vertices, float.NaN);
        }

        for (var y = 0; y < sourceGridSize; y++)
        {
            for (var x = 0; x < sourceGridSize; x++)
            {
                var targetX = strided
                    ? (int)MathF.Round(x * (RuntimeTerrainMesh.GridSize - 1) / (float)(sourceGridSize - 1))
                    : x;
                var targetY = strided
                    ? (int)MathF.Round(y * (RuntimeTerrainMesh.GridSize - 1) / (float)(sourceGridSize - 1))
                    : y;
                var targetIndex = strided
                    ? targetY * RuntimeTerrainMesh.GridSize + targetX
                    : y * sourceGridSize + x;

                var worldX = OriginX + x * CellSize / (sourceGridSize - 1);
                var worldY = OriginY + y * CellSize / (sourceGridSize - 1);
                vertices[targetIndex * 3 + 0] = worldX;
                vertices[targetIndex * 3 + 1] = worldY;
                vertices[targetIndex * 3 + 2] = HeightAtWorld(worldX, worldY);
            }
        }

        return new RuntimeTerrainMesh { Vertices = vertices, VertexDataOffset = 0x1234 };
    }

    private static RuntimeTerrainMesh CreateLocalPartialTerrainMesh(int sourceGridSize)
    {
        var vertices = new float[RuntimeTerrainMesh.VertexCount * 3];
        Array.Fill(vertices, float.NaN);

        for (var y = 0; y < sourceGridSize; y++)
        {
            for (var x = 0; x < sourceGridSize; x++)
            {
                var targetIndex = y * sourceGridSize + x;
                vertices[targetIndex * 3 + 0] = -2048f + x * 128f;
                vertices[targetIndex * 3 + 1] = -2048f + y * 128f;
                vertices[targetIndex * 3 + 2] = x + y * 100f;
            }
        }

        return new RuntimeTerrainMesh { Vertices = vertices, VertexDataOffset = 0x5678 };
    }

    private static RuntimeTerrainMesh CreateTerrainMeshWithZeroTrailingSlots(int sourceGridSize)
    {
        var vertices = new float[RuntimeTerrainMesh.VertexCount * 3];

        for (var y = 0; y < sourceGridSize; y++)
        {
            for (var x = 0; x < sourceGridSize; x++)
            {
                var targetIndex = y * sourceGridSize + x;
                var worldX = OriginX + x * CellSize / (sourceGridSize - 1);
                var worldY = OriginY + y * CellSize / (sourceGridSize - 1);
                vertices[targetIndex * 3 + 0] = worldX;
                vertices[targetIndex * 3 + 1] = worldY;
                vertices[targetIndex * 3 + 2] = HeightAtWorld(worldX, worldY);
            }
        }

        return new RuntimeTerrainMesh { Vertices = vertices };
    }

    private static RuntimeTerrainMesh CreateFlatZeroFullGrid()
    {
        var vertices = new float[RuntimeTerrainMesh.VertexCount * 3];
        for (var y = 0; y < RuntimeTerrainMesh.GridSize; y++)
        {
            for (var x = 0; x < RuntimeTerrainMesh.GridSize; x++)
            {
                var index = y * RuntimeTerrainMesh.GridSize + x;
                vertices[index * 3 + 0] = OriginX + x * 128f;
                vertices[index * 3 + 1] = OriginY + y * 128f;
                vertices[index * 3 + 2] = 0f;
            }
        }

        return new RuntimeTerrainMesh { Vertices = vertices };
    }

    private static RuntimeTerrainMesh CreateSparseInvalidMesh()
    {
        var vertices = new float[RuntimeTerrainMesh.VertexCount * 3];
        Array.Fill(vertices, float.NaN);

        for (var i = 0; i < 5; i++)
        {
            var worldX = OriginX + i * 128f;
            var worldY = OriginY + i * 128f;
            vertices[i * 3 + 0] = worldX;
            vertices[i * 3 + 1] = worldY;
            vertices[i * 3 + 2] = HeightAtWorld(worldX, worldY);
        }

        return new RuntimeTerrainMesh { Vertices = vertices };
    }

    private static void FillFiniteGarbage(float[] vertices)
    {
        for (var i = 0; i < RuntimeTerrainMesh.VertexCount; i++)
        {
            vertices[i * 3 + 0] = 50_000f + i * 64f;
            vertices[i * 3 + 1] = -50_000f + i * 96f;
            vertices[i * 3 + 2] = 1200f + i % 19;
        }
    }

    private static float[] CreateTerrainColors(int sourceGridSize, bool strided)
    {
        var colors = new float[RuntimeTerrainMesh.VertexCount * 4];
        Array.Fill(colors, float.NaN);

        for (var y = 0; y < sourceGridSize; y++)
        {
            for (var x = 0; x < sourceGridSize; x++)
            {
                var targetX = strided
                    ? (int)MathF.Round(x * (RuntimeTerrainMesh.GridSize - 1) / (float)(sourceGridSize - 1))
                    : x;
                var targetY = strided
                    ? (int)MathF.Round(y * (RuntimeTerrainMesh.GridSize - 1) / (float)(sourceGridSize - 1))
                    : y;
                var targetIndex = strided
                    ? targetY * RuntimeTerrainMesh.GridSize + targetX
                    : y * sourceGridSize + x;

                colors[targetIndex * 4 + 0] = x / (float)(sourceGridSize - 1);
                colors[targetIndex * 4 + 1] = y / (float)(sourceGridSize - 1);
                colors[targetIndex * 4 + 2] = 0.5f;
                colors[targetIndex * 4 + 3] = 1.0f;
            }
        }

        return colors;
    }

    private static void AssertHeightsMatchExpected(float[,] heights)
    {
        for (var y = 0; y < RuntimeTerrainMesh.GridSize; y++)
        {
            for (var x = 0; x < RuntimeTerrainMesh.GridSize; x++)
            {
                var worldX = OriginX + x * 128f;
                var worldY = OriginY + y * 128f;
                Assert.InRange(MathF.Abs(heights[y, x] - HeightAtWorld(worldX, worldY)), 0f, 0.05f);
            }
        }
    }

    private static IEnumerable<float> Flatten(float[,] heights)
    {
        for (var y = 0; y < RuntimeTerrainMesh.GridSize; y++)
        {
            for (var x = 0; x < RuntimeTerrainMesh.GridSize; x++)
            {
                yield return heights[y, x];
            }
        }
    }

    private static float HeightAtWorld(float worldX, float worldY) =>
        1000f + (worldX - OriginX) * 0.01f + (worldY - OriginY) * 0.02f;
}
