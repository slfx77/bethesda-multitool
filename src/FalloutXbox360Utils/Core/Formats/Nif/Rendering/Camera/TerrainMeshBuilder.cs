using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     v3 Phase 2a — converts a single <see cref="CellRecord" />'s heightmap into the
///     <see cref="GpuMeshUploader.GpuVertex" /> layout used by the rest of the GPU pipeline.
///     Pure CPU; no D3D11 dependency so the path can be exercised by ordinary unit tests
///     on the cross-platform TFM.
///     <para>
///         Data source preference: ESM <see cref="CellRecord.Heightmap" /> first, with
///         <see cref="CellRecord.RuntimeTerrainMesh" /> (converted via
///         <c>RuntimeTerrainMesh.ToLandHeightmap</c>) as a fallback for DMP-only loads.
///         Returns <c>null</c> / <c>false</c> when neither source is present or the cell
///         has no grid coordinates.
///     </para>
///     <para>
///         Hot-path callers (<c>TerrainRenderer</c>) should use <see cref="TryBuild" />
///         with scratch arrays they reuse across cells — building 16 cells/frame at ~94 KB
///         allocated per cell would otherwise produce ~85 MB/sec of garbage and trigger
///         a Gen 2 collection roughly every second.
///     </para>
/// </summary>
internal static class TerrainMeshBuilder
{
    /// <summary>33×33 grid (1089 vertices) per cell. Mirrors <see cref="TerrainConstants.LandGridSize" />.</summary>
    private const int Grid = TerrainConstants.LandGridSize;
    private const int LastIndex = Grid - 1;

    /// <summary>Vertex count for one cell mesh: 33×33 = 1089.</summary>
    public const int VertexCount = Grid * Grid;

    /// <summary>Index count for one cell mesh: 32×32 quads × 6 indices = 6144.</summary>
    public const int IndexCount = LastIndex * LastIndex * 6;

    /// <summary>Spacing between adjacent grid vertices in world units (4096 / 32 = 128).</summary>
    private const float VertexSpacing = TerrainConstants.LandVertexSpacing;

    public readonly record struct TerrainMesh(GpuMeshUploader.GpuVertex[] Vertices, ushort[] Indices);

    /// <summary>
    ///     Allocates fresh vertex + index arrays and builds the mesh into them. Convenient for
    ///     tests and one-off use; the renderer hot path should call <see cref="TryBuild" />
    ///     with pre-allocated scratch arrays.
    /// </summary>
    public static TerrainMesh? Build(CellRecord cell)
    {
        var vertices = new GpuMeshUploader.GpuVertex[VertexCount];
        var indices = new ushort[IndexCount];
        return TryBuild(cell, vertices, indices) ? new TerrainMesh(vertices, indices) : null;
    }

    /// <summary>
    ///     Builds the mesh into caller-provided buffers. Returns <c>true</c> on success.
    ///     <paramref name="vertices" /> must be at least <see cref="VertexCount" /> in length;
    ///     <paramref name="indices" /> at least <see cref="IndexCount" />.
    /// </summary>
    public static bool TryBuild(CellRecord cell, Span<GpuMeshUploader.GpuVertex> vertices, Span<ushort> indices)
    {
        if (vertices.Length < VertexCount)
            throw new ArgumentException($"Vertex span must be at least {VertexCount} long.", nameof(vertices));
        if (indices.Length < IndexCount)
            throw new ArgumentException($"Index span must be at least {IndexCount} long.", nameof(indices));

        if (cell.GridX is not int gx || cell.GridY is not int gy) return false;

        var heights = ResolveHeights(cell);
        if (heights is null) return false;

        var originX = gx * TerrainConstants.LandCellWorldSize;
        var originY = gy * TerrainConstants.LandCellWorldSize;

        FillVertices(heights, originX, originY, cell.LandVisualData?.VertexColors, vertices);
        FillIndices(indices);
        return true;
    }

    private static float[,]? ResolveHeights(CellRecord cell)
    {
        if (cell.Heightmap is { } esmHeights) return esmHeights.CalculateHeights();
        var runtime = cell.RuntimeTerrainMesh?.ToLandHeightmap();
        return runtime?.CalculateHeights();
    }

    private static void FillVertices(
        float[,] heights,
        float originX,
        float originY,
        byte[]? vertexColors,
        Span<GpuMeshUploader.GpuVertex> vertices)
    {
        var hasColors = vertexColors is { Length: >= VertexCount * 3 };

        for (var j = 0; j < Grid; j++)
        {
            for (var i = 0; i < Grid; i++)
            {
                var idx = j * Grid + i;
                var position = new Vector3(
                    originX + i * VertexSpacing,
                    originY + j * VertexSpacing,
                    heights[j, i]);

                vertices[idx] = new GpuMeshUploader.GpuVertex
                {
                    Position = position,
                    Normal = ComputeNormal(heights, i, j),
                    TexCoord = new Vector2(i / (float)LastIndex, j / (float)LastIndex),
                    VertexColor = hasColors ? ReadVertexColor(vertexColors!, idx) : Vector4.One,
                    Tangent = Vector3.Zero,
                    Bitangent = Vector3.Zero
                };
            }
        }
    }

    private static Vector3 ComputeNormal(float[,] heights, int i, int j)
    {
        // Central differences in cell-local units, falling back to forward/backward at edges.
        // dz/dx ≈ (h[i+1] - h[i-1]) / (2 * spacing); same for dz/dy. The surface normal of
        // z = f(x,y) is (-dz/dx, -dz/dy, 1) normalised — for a flat heightmap this is exactly +Z.
        float hxMinus = i > 0 ? heights[j, i - 1] : heights[j, i];
        float hxPlus = i < LastIndex ? heights[j, i + 1] : heights[j, i];
        float hyMinus = j > 0 ? heights[j - 1, i] : heights[j, i];
        float hyPlus = j < LastIndex ? heights[j + 1, i] : heights[j, i];

        // Span = 2 * spacing for interior, 1 * spacing at edges (forward/backward diff).
        var xSpan = (i > 0 && i < LastIndex) ? 2f * VertexSpacing : VertexSpacing;
        var ySpan = (j > 0 && j < LastIndex) ? 2f * VertexSpacing : VertexSpacing;

        var dx = (hxPlus - hxMinus) / xSpan;
        var dy = (hyPlus - hyMinus) / ySpan;

        var normal = new Vector3(-dx, -dy, 1f);
        var length = normal.Length();
        return length > 0 ? normal / length : Vector3.UnitZ;
    }

    private static Vector4 ReadVertexColor(byte[] colors, int vertexIndex)
    {
        var offset = vertexIndex * 3;
        return new Vector4(
            colors[offset] / 255f,
            colors[offset + 1] / 255f,
            colors[offset + 2] / 255f,
            1f);
    }

    private static void FillIndices(Span<ushort> indices)
    {
        var k = 0;
        for (var j = 0; j < LastIndex; j++)
        {
            for (var i = 0; i < LastIndex; i++)
            {
                // Quad corners: v00 = (i, j), v10 = (i+1, j), v01 = (i, j+1), v11 = (i+1, j+1).
                // CCW winding when viewed from +Z (matches CullMode.Back if/when we tighten it).
                var v00 = (ushort)(j * Grid + i);
                var v10 = (ushort)(j * Grid + i + 1);
                var v01 = (ushort)((j + 1) * Grid + i);
                var v11 = (ushort)((j + 1) * Grid + i + 1);

                indices[k++] = v00; indices[k++] = v10; indices[k++] = v01;
                indices[k++] = v01; indices[k++] = v10; indices[k++] = v11;
            }
        }
    }
}
