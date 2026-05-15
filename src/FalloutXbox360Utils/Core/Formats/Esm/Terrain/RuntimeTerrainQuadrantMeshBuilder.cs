using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Terrain;

internal static class RuntimeTerrainQuadrantMeshBuilder
{
    public const int QuadrantCount = 4;
    public const int QuadrantVertexCount = 17 * 17;

    private const float LocalCoordinateLimit = TerrainConstants.LandCellWorldSize;
    private const float HeightLimit = 20_000f;
    private const int MinimumOccupiedVertexCount = 12;

    public static RuntimeTerrainMesh? TryBuild(
        IReadOnlyList<RuntimeTerrainFloatArraySlot> vertexArrays,
        IReadOnlyList<RuntimeTerrainFloatArraySlot> normalArrays,
        IReadOnlyList<RuntimeTerrainFloatArraySlot> colorArrays)
    {
        if (vertexArrays.Count == 0)
        {
            return null;
        }

        var normalArraysBySlot = normalArrays.ToDictionary(a => a.Slot);
        var colorArraysBySlot = colorArrays.ToDictionary(a => a.Slot);

        var vertices = new float[RuntimeTerrainMesh.VertexCount * 3];
        var normals = new float[RuntimeTerrainMesh.VertexCount * 3];
        var colors = new float[RuntimeTerrainMesh.VertexCount * 4];
        var occupied = new bool[RuntimeTerrainMesh.VertexCount];
        var fitErrors = new float[RuntimeTerrainMesh.VertexCount];
        Array.Fill(fitErrors, float.MaxValue);

        var hasNormals = false;
        var hasColors = false;
        long vertexDataOffset = 0;

        foreach (var vertexArray in vertexArrays)
        {
            if (vertexDataOffset == 0)
            {
                vertexDataOffset = vertexArray.FileOffset;
            }

            normalArraysBySlot.TryGetValue(vertexArray.Slot, out var normalArray);
            colorArraysBySlot.TryGetValue(vertexArray.Slot, out var colorArray);

            for (var i = 0; i < QuadrantVertexCount; i++)
            {
                var vertexOffset = i * 3;
                var x = vertexArray.Data[vertexOffset];
                var y = vertexArray.Data[vertexOffset + 1];
                var z = vertexArray.Data[vertexOffset + 2];
                if (!IsValidTerrainVertex(x, y, z))
                {
                    continue;
                }

                var mapped = TerrainCoordinateMapper.TryMapLocalVertexToCanonicalCell(x, y);
                if (mapped == null)
                {
                    continue;
                }

                var (gridX, gridY, fitError) = mapped.Value;
                var canonicalIndex = gridY * RuntimeTerrainMesh.GridSize + gridX;
                if (fitError >= fitErrors[canonicalIndex])
                {
                    continue;
                }

                fitErrors[canonicalIndex] = fitError;
                occupied[canonicalIndex] = true;
                var canonicalVertexOffset = canonicalIndex * 3;
                vertices[canonicalVertexOffset] = x;
                vertices[canonicalVertexOffset + 1] = y;
                vertices[canonicalVertexOffset + 2] = z;

                if (normalArray.Data != null)
                {
                    hasNormals |= TryCopyNormal(normalArray.Data, i, normals, canonicalVertexOffset);
                }

                if (colorArray.Data != null)
                {
                    hasColors |= TryCopyColor(colorArray.Data, i, colors, canonicalIndex);
                }
            }
        }

        if (CountOccupied(occupied) < MinimumOccupiedVertexCount)
        {
            return null;
        }

        var terrainMesh = new RuntimeTerrainMesh
        {
            Vertices = vertices,
            Normals = hasNormals ? normals : null,
            Colors = hasColors ? colors : null,
            VertexDataOffset = vertexDataOffset
        };

        return RuntimeTerrainGridReconstructionService.Reconstruct(terrainMesh) == null ? null : terrainMesh;
    }

    private static bool TryCopyNormal(
        float[] normalData,
        int sourceIndex,
        float[] normals,
        int canonicalVertexOffset)
    {
        var normalOffset = sourceIndex * 3;
        if (normalOffset + 2 >= normalData.Length ||
            !IsValidCompanionVector(normalData[normalOffset],
                normalData[normalOffset + 1],
                normalData[normalOffset + 2]))
        {
            return false;
        }

        normals[canonicalVertexOffset] = normalData[normalOffset];
        normals[canonicalVertexOffset + 1] = normalData[normalOffset + 1];
        normals[canonicalVertexOffset + 2] = normalData[normalOffset + 2];
        return true;
    }

    private static bool TryCopyColor(float[] colorData, int sourceIndex, float[] colors, int canonicalIndex)
    {
        var colorOffset = sourceIndex * 4;
        if (colorOffset + 2 >= colorData.Length ||
            !IsValidColor(colorData[colorOffset], colorData[colorOffset + 1], colorData[colorOffset + 2]))
        {
            return false;
        }

        var canonicalColorOffset = canonicalIndex * 4;
        colors[canonicalColorOffset] = colorData[colorOffset];
        colors[canonicalColorOffset + 1] = colorData[colorOffset + 1];
        colors[canonicalColorOffset + 2] = colorData[colorOffset + 2];
        colors[canonicalColorOffset + 3] = colorOffset + 3 < colorData.Length
            ? colorData[colorOffset + 3]
            : 1f;
        return true;
    }

    private static bool IsValidTerrainVertex(float x, float y, float z)
    {
        return IsNormalFinite(x) &&
               IsNormalFinite(y) &&
               IsNormalFinite(z) &&
               MathF.Abs(x) <= LocalCoordinateLimit &&
               MathF.Abs(y) <= LocalCoordinateLimit &&
               MathF.Abs(z) <= HeightLimit &&
               !(MathF.Abs(x) < 0.001f && MathF.Abs(y) < 0.001f && MathF.Abs(z) < 0.001f);
    }

    private static bool IsValidCompanionVector(float x, float y, float z)
    {
        return IsNormalFinite(x) && IsNormalFinite(y) && IsNormalFinite(z) &&
               MathF.Abs(x) <= 2f && MathF.Abs(y) <= 2f && MathF.Abs(z) <= 2f;
    }

    private static bool IsValidColor(float r, float g, float b)
    {
        return IsNormalFinite(r) && IsNormalFinite(g) && IsNormalFinite(b) &&
               r is >= 0f and <= 2f &&
               g is >= 0f and <= 2f &&
               b is >= 0f and <= 2f;
    }

    private static bool IsNormalFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static int CountOccupied(bool[] occupied)
    {
        var count = 0;
        foreach (var value in occupied)
        {
            if (value)
            {
                count++;
            }
        }

        return count;
    }
}
