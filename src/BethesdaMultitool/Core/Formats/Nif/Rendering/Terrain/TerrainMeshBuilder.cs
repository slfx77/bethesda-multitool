using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     v3 Phase 2a — converts a single <see cref="CellRecord" />'s heightmap into the
///     <see cref="GpuMeshUploader.GpuVertex" /> layout used by the rest of the GPU pipeline.
///     Pure CPU; no graphics-API dependency, so the path can be exercised by ordinary unit tests
///     on the cross-platform TFM.
///     <para>
///         Data source preference: ESM <see cref="CellRecord.Heightmap" /> first, with
///         <see cref="CellRecord.RuntimeTerrainMesh" /> (converted via
///         <c>RuntimeTerrainMesh.ToLandHeightmap</c>) as a fallback for DMP-only loads.
///         Returns <c>null</c> / <c>false</c> when neither source is present or the cell
///         has no grid coordinates.
///     </para>
///     <para>
///         The LAND grid resolution is <b>not</b> fixed at 33×33. Fallout/Oblivion/Skyrim use
///         33×33 over a 4096-unit cell; Morrowind uses 65×65 over an 8192-unit cell. The grid
///         size <c>N</c> is derived from the ESM heightmap's array dimensions
///         (<see cref="LandHeightmap.ExactHeights" />) so each game's terrain renders at its
///         native resolution. The const <see cref="VertexCount" /> / <see cref="IndexCount" />
///         and the no-arg <see cref="BuildSharedIndexBufferData()" /> remain the 33×33 defaults
///         (runtime DMP terrain is always 33×33); the <c>int gridSize</c> overloads serve the
///         variable-resolution path.
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
    /// <summary>33×33 grid (1089 vertices) per cell. Mirrors <see cref="TerrainConstants.LandGridSize" />.
    /// This is the default for Fallout/Oblivion/Skyrim and the runtime DMP path; Morrowind is 65×65.</summary>
    private const int Grid = TerrainConstants.LandGridSize;
    private const int LastIndex = Grid - 1;

    /// <summary>Vertex count for one 33×33 cell mesh: 33×33 = 1089 (the default-grid value).</summary>
    public const int VertexCount = Grid * Grid;

    /// <summary>Index count for one 33×33 cell mesh: 32×32 quads × 6 indices = 6144 (default grid).</summary>
    public const int IndexCount = LastIndex * LastIndex * 6;

    /// <summary>
    ///     Indices per quadrant for the default 33×33 grid: 16×16 quads × 6 indices = 1536. The full
    ///     index buffer is laid out as four contiguous quadrant ranges. Quadrant order matches the LAND
    ///     BTXT/ATXT <c>Quadrant</c> byte: <c>0 = bottom-left (SW)</c>, <c>1 = bottom-right (SE)</c>,
    ///     <c>2 = top-left (NW)</c>, <c>3 = top-right (NE)</c>.
    /// </summary>
    public const int IndicesPerQuadrant = 16 * 16 * 6;

    /// <summary>Spacing between adjacent grid vertices in world units for the default grid (4096 / 32 = 128).</summary>
    private const float VertexSpacing = TerrainConstants.LandVertexSpacing;

    /// <summary>A built terrain cell mesh: its GPU vertices and triangle index buffer.</summary>
    public readonly record struct TerrainMesh(GpuMeshUploader.GpuVertex[] Vertices, ushort[] Indices);

    /// <summary>Vertex count for an <paramref name="gridSize" />×<paramref name="gridSize" /> cell mesh.</summary>
    public static int VertexCountFor(int gridSize) => gridSize * gridSize;

    /// <summary>Index count for an <paramref name="gridSize" />×<paramref name="gridSize" /> cell mesh.</summary>
    public static int IndexCountFor(int gridSize) => (gridSize - 1) * (gridSize - 1) * 6;

    /// <summary>
    ///     The LAND grid resolution (vertices per edge) this cell's terrain will build at: the ESM
    ///     heightmap's array edge length when present (33 for Fallout-family, 65 for Morrowind), else
    ///     the default 33. Callers that pre-size shared GPU buffers (the terrain renderer's index
    ///     buffer + scratch) read this once per worldspace.
    /// </summary>
    public static int ResolveGridSize(CellRecord cell)
        => cell.Heightmap?.ExactHeights?.GetLength(0) ?? Grid;

    /// <summary>
    ///     Allocates fresh vertex + index arrays and builds the mesh into them. Convenient for
    ///     tests and one-off use; the renderer hot path should call <see cref="TryBuild" />
    ///     with pre-allocated scratch arrays.
    /// </summary>
    public static TerrainMesh? Build(CellRecord cell)
    {
        var n = ResolveGridSize(cell);
        var vertices = new GpuMeshUploader.GpuVertex[VertexCountFor(n)];
        var indices = new ushort[IndexCountFor(n)];
        return TryBuild(cell, vertices, indices) ? new TerrainMesh(vertices, indices) : null;
    }

    /// <summary>
    ///     Builds the invariant index buffer for a 33×33 LAND cell mesh (the default grid). Renderers
    ///     should upload this once and reuse it across all cells of the same grid size.
    /// </summary>
    public static ushort[] BuildSharedIndexBufferData() => BuildSharedIndexBufferData(Grid);

    /// <summary>
    ///     Builds the invariant index buffer for an <paramref name="gridSize" />×<paramref name="gridSize" />
    ///     LAND cell mesh. All cells of one worldspace share a single grid size, so this is built once
    ///     per worldspace load and reused across every cell.
    /// </summary>
    public static ushort[] BuildSharedIndexBufferData(int gridSize)
        => BuildSharedIndexBufferData(gridSize, TerrainTriangleTopology.FixedSouthEastNorthWest);

    /// <summary>
    ///     Builds the invariant index buffer for a LAND cell using the requested engine-family
    ///     triangle topology. The public/default overload remains the legacy fixed-diagonal path.
    /// </summary>
    internal static ushort[] BuildSharedIndexBufferData(
        int gridSize,
        TerrainTriangleTopology topology)
    {
        var indices = new ushort[IndexCountFor(gridSize)];
        FillIndices(indices, gridSize, topology);
        return indices;
    }

    /// <summary>
    ///     Builds the mesh into caller-provided buffers. Returns <c>true</c> on success. The buffers
    ///     must be at least <see cref="VertexCountFor" /> / <see cref="IndexCountFor" /> long for the
    ///     cell's grid size (33×33 for Fallout-family, 65×65 for Morrowind).
    /// </summary>
    public static bool TryBuild(CellRecord cell, Span<GpuMeshUploader.GpuVertex> vertices, Span<ushort> indices)
    {
        if (!TryBuildVertices(cell, vertices)) return false;
        FillIndices(indices, ResolveGridSize(cell));
        return true;
    }

    /// <summary>
    ///     Builds only the per-cell vertex data into a caller-provided buffer. The index buffer
    ///     is cell-invariant per grid size; hot-path renderers should use
    ///     <see cref="BuildSharedIndexBufferData(int)" /> once and call this method for each uploaded cell.
    /// </summary>
    public static bool TryBuildVertices(CellRecord cell, Span<GpuMeshUploader.GpuVertex> vertices)
        => TryBuildVertices(cell, vertices, cache: null);

    /// <summary>
    ///     Builds the per-cell terrain vertices into <paramref name="vertices" />, optionally pulling decoded
    ///     heightmap/blend data from <paramref name="cache" />. Returns false when the cell has no grid coordinates.
    /// </summary>
    public static bool TryBuildVertices(
        CellRecord cell,
        Span<GpuMeshUploader.GpuVertex> vertices,
        global::BethesdaMultitool.WorldRenderCache? cache)
    {
        if (cell.GridX is not int gx || cell.GridY is not int gy) return false;

        // Most games use the engine-default 4096-unit cell; Morrowind exterior cells are 8192. The
        // origin tracks the cell's absolute world coordinates (matching its placed objects), and the
        // vertex spacing scales with both the cell size and the grid resolution.
        var cellSize = cell.CellWorldSize > 0f ? cell.CellWorldSize : TerrainConstants.LandCellWorldSize;
        var originX = gx * cellSize;
        var originY = gy * cellSize;

        // The shared WorldRenderCache decodes terrain to a flat 33×33 grid (it also feeds the 2D map),
        // so it can only serve cells whose native grid IS 33×33 (Fallout/Oblivion/Skyrim + runtime
        // DMP). Morrowind's native 65×65 LAND bypasses the cache and builds from the float[,] heights
        // directly, so the 3D viewer renders it at full resolution instead of the cache's downsample.
        if (ResolveGridSize(cell) == Grid)
        {
            var terrain = cache?.GetTerrain(cell);
            if (terrain is not null)
            {
                if (vertices.Length < VertexCount)
                    throw new ArgumentException($"Vertex span must be at least {VertexCount} long.", nameof(vertices));
                if (!terrain.HasTerrain) return false;

                FillVertices(
                    terrain.Heights,
                    originX,
                    originY,
                    cell.LandVisualData?.VertexColors,
                    cell.LandVisualData?.VertexNormals,
                    vertices);
                return true;
            }
        }

        var heights = ResolveHeights(cell);
        if (heights is null) return false;

        // Grid resolution is intrinsic to the heightmap data: 33×33 (Fallout-family) or 65×65 (Morrowind).
        var n = heights.GetLength(0);
        if (vertices.Length < n * n)
            throw new ArgumentException($"Vertex span must be at least {n * n} long.", nameof(vertices));

        var spacing = cellSize / (n - 1);
        FillVertices(
            heights,
            originX,
            originY,
            spacing,
            cell.LandVisualData?.VertexColors,
            cell.LandVisualData?.VertexNormals,
            vertices,
            n);
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
        float spacing,
        byte[]? vertexColors,
        byte[]? vertexNormals,
        Span<GpuMeshUploader.GpuVertex> vertices,
        int n)
    {
        var lastIndex = n - 1;
        var hasColors = vertexColors is { } c && c.Length >= n * n * 3;
        var hasNormals = vertexNormals is { } vn && vn.Length >= n * n * 3;

        for (var j = 0; j < n; j++)
        {
            for (var i = 0; i < n; i++)
            {
                var idx = j * n + i;
                var position = new Vector3(
                    originX + i * spacing,
                    originY + j * spacing,
                    heights[j, i]);

                vertices[idx] = new GpuMeshUploader.GpuVertex
                {
                    Position = position,
                    Normal = hasNormals
                        ? ReadVertexNormal(vertexNormals!, idx)
                        : ComputeNormal(heights, i, j, spacing, n),
                    TexCoord = new Vector2(i / (float)lastIndex, j / (float)lastIndex),
                    VertexColor = hasColors ? ReadVertexColor(vertexColors!, idx) : Vector4.One,
                    Tangent = Vector3.Zero,
                    Bitangent = Vector3.Zero
                };
            }
        }
    }

    private static void FillVertices(
        float[] heights,
        float originX,
        float originY,
        byte[]? vertexColors,
        byte[]? vertexNormals,
        Span<GpuMeshUploader.GpuVertex> vertices)
    {
        var hasColors = vertexColors is { Length: >= VertexCount * 3 };
        var hasNormals = vertexNormals is { Length: >= VertexCount * 3 };

        for (var j = 0; j < Grid; j++)
        {
            for (var i = 0; i < Grid; i++)
            {
                var idx = j * Grid + i;
                var position = new Vector3(
                    originX + i * VertexSpacing,
                    originY + j * VertexSpacing,
                    heights[idx]);

                vertices[idx] = new GpuMeshUploader.GpuVertex
                {
                    Position = position,
                    Normal = hasNormals
                        ? ReadVertexNormal(vertexNormals!, idx)
                        : ComputeNormal(heights, i, j),
                    TexCoord = new Vector2(i / (float)LastIndex, j / (float)LastIndex),
                    VertexColor = hasColors ? ReadVertexColor(vertexColors!, idx) : Vector4.One,
                    Tangent = Vector3.Zero,
                    Bitangent = Vector3.Zero
                };
            }
        }
    }

    private static Vector3 ComputeNormal(float[,] heights, int i, int j, float spacing, int n)
    {
        var lastIndex = n - 1;
        // Central differences in cell-local units, falling back to forward/backward at edges.
        // dz/dx ≈ (h[i+1] - h[i-1]) / (2 * spacing); same for dz/dy. The surface normal of
        // z = f(x,y) is (-dz/dx, -dz/dy, 1) normalized — for a flat heightmap this is exactly +Z.
        float hxMinus = i > 0 ? heights[j, i - 1] : heights[j, i];
        float hxPlus = i < lastIndex ? heights[j, i + 1] : heights[j, i];
        float hyMinus = j > 0 ? heights[j - 1, i] : heights[j, i];
        float hyPlus = j < lastIndex ? heights[j + 1, i] : heights[j, i];

        // Span = 2 * spacing for interior, 1 * spacing at edges (forward/backward diff).
        var xSpan = (i > 0 && i < lastIndex) ? 2f * spacing : spacing;
        var ySpan = (j > 0 && j < lastIndex) ? 2f * spacing : spacing;

        var dx = (hxPlus - hxMinus) / xSpan;
        var dy = (hyPlus - hyMinus) / ySpan;

        var normal = new Vector3(-dx, -dy, 1f);
        var length = normal.Length();
        return length > 0 ? normal / length : Vector3.UnitZ;
    }

    private static Vector3 ComputeNormal(float[] heights, int i, int j)
    {
        float hxMinus = i > 0 ? heights[j * Grid + i - 1] : heights[j * Grid + i];
        float hxPlus = i < LastIndex ? heights[j * Grid + i + 1] : heights[j * Grid + i];
        float hyMinus = j > 0 ? heights[(j - 1) * Grid + i] : heights[j * Grid + i];
        float hyPlus = j < LastIndex ? heights[(j + 1) * Grid + i] : heights[j * Grid + i];

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

    private static Vector3 ReadVertexNormal(byte[] normals, int vertexIndex)
    {
        var offset = vertexIndex * 3;
        var normal = new Vector3(
            unchecked((sbyte)normals[offset]) / 127f,
            unchecked((sbyte)normals[offset + 1]) / 127f,
            unchecked((sbyte)normals[offset + 2]) / 127f);
        var lengthSquared = normal.LengthSquared();
        return lengthSquared > 1e-8f
            ? normal / MathF.Sqrt(lengthSquared)
            : Vector3.UnitZ;
    }

    private static void FillIndices(Span<ushort> indices, int n)
        => FillIndices(indices, n, TerrainTriangleTopology.FixedSouthEastNorthWest);

    private static void FillIndices(
        Span<ushort> indices,
        int n,
        TerrainTriangleTopology topology)
    {
        // Four quadrant ranges, in order 0=SW, 1=SE, 2=NW, 3=NE. Each quadrant emits (mid×mid) quads,
        // each quad = 2 CCW triangles (mid = (n-1)/2). Vertices on the center cross (i=mid or j=mid)
        // are NOT duplicated — adjacent quadrant ranges both reference the shared vertex indices, so
        // the seam is geometrically watertight. Since the renderer now issues a single DrawIndexed
        // over the whole buffer, the quadrant grouping is cosmetic, but it keeps N=33 byte-identical.
        var mid = (n - 1) / 2;
        var k = 0;
        for (var q = 0; q < 4; q++)
        {
            var iStart = (q & 1) == 0 ? 0 : mid;
            var jStart = (q & 2) == 0 ? 0 : mid;
            for (var j = jStart; j < jStart + mid; j++)
            {
                for (var i = iStart; i < iStart + mid; i++)
                {
                    // Quad corners: v00 = (i, j), v10 = (i+1, j), v01 = (i, j+1), v11 = (i+1, j+1).
                    // CCW winding when viewed from +Z (matches CullMode.Back if/when we tighten it).
                    var v00 = (ushort)(j * n + i);
                    var v10 = (ushort)(j * n + i + 1);
                    var v01 = (ushort)((j + 1) * n + i);
                    var v11 = (ushort)((j + 1) * n + i + 1);

                    if (TerrainSurfaceTopology.UsesSouthwestNortheastDiagonal(topology, i, j))
                    {
                        indices[k++] = v00; indices[k++] = v10; indices[k++] = v11;
                        indices[k++] = v00; indices[k++] = v11; indices[k++] = v01;
                    }
                    else
                    {
                        indices[k++] = v00; indices[k++] = v10; indices[k++] = v01;
                        indices[k++] = v01; indices[k++] = v10; indices[k++] = v11;
                    }
                }
            }
        }
    }
}
