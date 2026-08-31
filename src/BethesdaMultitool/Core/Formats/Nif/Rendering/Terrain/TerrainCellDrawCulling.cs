using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Exact vertical extent of a built terrain cell. Terrain triangles are linear interpolations
///     of the uploaded vertex heights, so their Z extent cannot exceed these two values.
/// </summary>
internal readonly record struct TerrainCellHeightBounds(float MinWorldZ, float MaxWorldZ)
{
    /// <summary>A deliberately unusable bound. Draw culling treats it as fail-open.</summary>
    public static TerrainCellHeightBounds Invalid => new(float.NaN, float.NaN);

    /// <summary>
    ///     Measures the exact extrema of the height stream that will be uploaded. A non-finite
    ///     height invalidates the whole bound: ignoring that vertex could make the remaining range
    ///     non-conservative, so the renderer must draw the cell instead of rejecting it.
    /// </summary>
    public static TerrainCellHeightBounds FromVertices(ReadOnlySpan<TerrainVertex> vertices)
    {
        if (vertices.IsEmpty)
        {
            return Invalid;
        }

        var min = vertices[0].Height;
        var max = min;
        if (!float.IsFinite(min))
        {
            return Invalid;
        }

        for (var i = 1; i < vertices.Length; i++)
        {
            var height = vertices[i].Height;
            if (!float.IsFinite(height))
            {
                return Invalid;
            }

            min = MathF.Min(min, height);
            max = MathF.Max(max, height);
        }

        return new TerrainCellHeightBounds(min, max);
    }
}

/// <summary>
///     A validated frustum plus the absolute world origin subtracted by the terrain vertex shader.
///     The frustum itself operates on coordinates relative to this origin.
/// </summary>
internal readonly record struct TerrainCellDrawFrustum(Frustum Frustum, Vector3 RenderOrigin);

/// <summary>
///     Conservative main-pass terrain-cell rejection. Invalid camera or cell data always returns
///     "draw"; culling is an optimization and must never turn malformed capture data into a hole.
/// </summary>
internal static class TerrainCellDrawCulling
{
    // Plane extraction and dot products are float32. One world unit is tiny beside even Starfield's
    // 100-unit cells, but large enough to keep a cell whose exact bound sits on a plane from being
    // false-rejected by accumulated rounding in the matrix -> plane -> AABB path.
    private const float ConservativeAabbMargin = 1f;

    /// <summary>
    ///     Builds the camera frustum once per pass and carries the exact absolute world origin the
    ///     caller used to construct its matrix. A nullable result makes malformed input fail open
    ///     instead of allowing NaNs to make <see cref="Frustum.IntersectsAabb" /> reject terrain.
    /// </summary>
    public static TerrainCellDrawFrustum? CreateFrustum(
        Matrix4x4 viewProjection,
        Vector3 renderOrigin)
    {
        var determinant = viewProjection.GetDeterminant();
        if (!IsFinite(viewProjection) ||
            !IsFinite(renderOrigin) ||
            !float.IsFinite(determinant) ||
            MathF.Abs(determinant) <= float.Epsilon)
        {
            return null;
        }

        var frustum = Frustum.FromViewProjection(viewProjection);
        if (!IsUsable(frustum))
        {
            return null;
        }

        return new TerrainCellDrawFrustum(frustum, renderOrigin);
    }

    /// <summary>
    ///     Returns false only when a valid cell AABB is wholly outside a valid frustum. Touching a
    ///     plane is an intersection, matching <see cref="Frustum.IntersectsAabb" />.
    /// </summary>
    public static bool ShouldDraw(
        TerrainCellDrawFrustum? cullFrustum,
        TerrainCellGrid grid,
        TerrainCellHeightBounds heightBounds)
    {
        if (cullFrustum is not { } validCullFrustum ||
            !TryGetAabb(grid, heightBounds, out var min, out var max))
        {
            return true;
        }

        var margin = new Vector3(ConservativeAabbMargin);
        min = min - validCullFrustum.RenderOrigin - margin;
        max = max - validCullFrustum.RenderOrigin + margin;
        if (!IsFinite(min) || !IsFinite(max))
        {
            return true;
        }

        return validCullFrustum.Frustum.IntersectsAabb(min, max);
    }

    private static bool TryGetAabb(
        TerrainCellGrid grid,
        TerrainCellHeightBounds heightBounds,
        out Vector3 min,
        out Vector3 max)
    {
        min = default;
        max = default;

        if (grid.GridSize <= 1 ||
            !float.IsFinite(grid.OriginX) ||
            !float.IsFinite(grid.OriginY) ||
            !float.IsFinite(grid.Spacing) ||
            grid.Spacing <= 0f ||
            !float.IsFinite(heightBounds.MinWorldZ) ||
            !float.IsFinite(heightBounds.MaxWorldZ) ||
            heightBounds.MinWorldZ > heightBounds.MaxWorldZ)
        {
            return false;
        }

        // Same operation order as TerrainCellGrid.PositionOf and the terrain vertex shader. For
        // normal game grids this is exact (TerrainCellGrid.IsExactlyReconstructible); the finite
        // check keeps unusual/corrupt grids fail-open if the multiplication overflows.
        var extent = (grid.GridSize - 1) * grid.Spacing;
        var maxX = grid.OriginX + extent;
        var maxY = grid.OriginY + extent;
        if (!float.IsFinite(extent) || !float.IsFinite(maxX) || !float.IsFinite(maxY))
        {
            return false;
        }

        min = new Vector3(grid.OriginX, grid.OriginY, heightBounds.MinWorldZ);
        max = new Vector3(maxX, maxY, heightBounds.MaxWorldZ);
        return true;
    }

    private static bool IsUsable(Frustum frustum) =>
        IsUsable(frustum.Left) &&
        IsUsable(frustum.Right) &&
        IsUsable(frustum.Bottom) &&
        IsUsable(frustum.Top) &&
        IsUsable(frustum.Near) &&
        IsUsable(frustum.Far);

    private static bool IsUsable(Plane plane)
    {
        var normal = plane.Normal;
        return float.IsFinite(normal.X) &&
               float.IsFinite(normal.Y) &&
               float.IsFinite(normal.Z) &&
               float.IsFinite(plane.D) &&
               normal.LengthSquared() > 1e-12f;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
        float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
        float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);
}
