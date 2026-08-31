using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Why a requested placed-light tile build fell back to the conservative all-active mask.
///     A fallback can cost shader work, but it never removes a light that the unculled forward
///     loop would have evaluated.
/// </summary>
internal enum PlacedLightTileCullFallbackReason
{
    None,
    InvalidViewport,
    InvalidViewProjection,
    InvalidRenderOrigin,
    InvalidLight,
    DegenerateTileFrustum
}

/// <summary>Shape and status of one frame's screen-space placed-light mask grid.</summary>
internal readonly record struct PlacedLightTileCullResult(
    int TileCountX,
    int TileCountY,
    int LightCount,
    PlacedLightTileCullFallbackReason FallbackReason)
{
    internal int TileCount => checked(TileCountX * TileCountY);

    internal bool UsedFallback => FallbackReason != PlacedLightTileCullFallbackReason.None;
}

/// <summary>
///     Builds an exact-superset 64-bit placed-light mask for each 16x16 screen tile.
///     <para>
///         Bit <c>i</c> always names placed light <c>i</c>; the shader can therefore visit set bits
///         from least to most significant and preserve the existing forward loop's floating-point
///         summation order, including negative lights. The test is deliberately sphere-versus-four
///         SIDE planes only. Omitting near/far planes can retain unnecessary lights at other depths,
///         but cannot remove a light capable of affecting a visible fragment.
///     </para>
///     <para>
///         Each tile frustum is extracted with the renderer's established
///         <see cref="Frustum.FromViewProjection" /> path. A post-projection crop maps the tile's NDC
///         rectangle back to the full clip rectangle, so this works under perspective, orthographic,
///         reversed-Z, camera-relative, and mirrored view-projection matrices without reconstructing
///         camera rays. Invalid inputs produce all-active masks rather than risking a false negative.
///     </para>
/// </summary>
internal static class PlacedLightTileCuller
{
    internal const int TileSizePixels = 16;
    internal const int MaxLights = 64;

    // Plane extraction and the light positions are float32. Growing the influence sphere only for
    // the tile test keeps boundary rounding conservative; the pixel shader still applies the exact
    // authored radius, so this can create false positives but cannot alter illumination.
    private const float AbsoluteRadiusMargin = 1f;
    private const float RelativeRadiusMargin = 1e-4f;

    /// <summary>
    ///     Number of <see cref="ulong" /> tile masks the caller must provide. Invalid or unrepresentable
    ///     viewport dimensions use a single all-active fallback tile.
    /// </summary>
    internal static int RequiredMaskCount(int viewportWidth, int viewportHeight)
    {
        return TryResolveTileDimensions(viewportWidth, viewportHeight, out var tileCountX, out var tileCountY)
            ? checked(tileCountX * tileCountY)
            : 1;
    }

    /// <summary>
    ///     Writes row-major tile masks into <paramref name="destination" />. Placed-light positions
    ///     are absolute; <paramref name="renderOrigin" /> is subtracted before the side-plane tests so
    ///     they occupy the same coordinate space as <paramref name="viewProjection" /> and the GPU
    ///     <c>GpuPointLight</c> upload.
    /// </summary>
    internal static PlacedLightTileCullResult Build(
        Matrix4x4 viewProjection,
        int viewportWidth,
        int viewportHeight,
        Vector3 renderOrigin,
        ReadOnlySpan<PlacedLight> lights,
        Span<ulong> destination)
    {
        if (lights.Length > MaxLights)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lights), lights.Length,
                $"A {MaxLights}-bit tile mask cannot represent more than {MaxLights} placed lights.");
        }

        var activeMask = ActiveMask(lights.Length);
        if (!TryResolveTileDimensions(
                viewportWidth, viewportHeight, out var tileCountX, out var tileCountY))
        {
            RequireDestination(destination, 1);
            destination[0] = activeMask;
            return new PlacedLightTileCullResult(
                1, 1, lights.Length, PlacedLightTileCullFallbackReason.InvalidViewport);
        }

        var tileCount = checked(tileCountX * tileCountY);
        RequireDestination(destination, tileCount);
        var masks = destination[..tileCount];

        if (!IsFinite(viewProjection))
        {
            return FillFallback(
                masks, tileCountX, tileCountY, lights.Length, activeMask,
                PlacedLightTileCullFallbackReason.InvalidViewProjection);
        }

        if (!IsFinite(renderOrigin))
        {
            return FillFallback(
                masks, tileCountX, tileCountY, lights.Length, activeMask,
                PlacedLightTileCullFallbackReason.InvalidRenderOrigin);
        }

        Span<Vector3> centers = stackalloc Vector3[MaxLights];
        Span<float> radii = stackalloc float[MaxLights];
        for (var lightIndex = 0; lightIndex < lights.Length; lightIndex++)
        {
            var light = lights[lightIndex];
            if (!float.IsFinite(light.Radius))
            {
                return FillFallback(
                    masks, tileCountX, tileCountY, lights.Length, activeMask,
                    PlacedLightTileCullFallbackReason.InvalidLight);
            }

            // The shader rejects non-positive radii before they can contribute. Leave their bits
            // clear without inspecting a position that the shader likewise need not consume.
            if (light.Radius <= 0f)
            {
                centers[lightIndex] = default;
                radii[lightIndex] = 0f;
                continue;
            }

            var center = light.Position - renderOrigin;
            if (!IsFinite(center))
            {
                return FillFallback(
                    masks, tileCountX, tileCountY, lights.Length, activeMask,
                    PlacedLightTileCullFallbackReason.InvalidLight);
            }

            centers[lightIndex] = center;
            var margin = MathF.Max(AbsoluteRadiusMargin, light.Radius * RelativeRadiusMargin);
            var conservativeRadius = light.Radius + margin;
            radii[lightIndex] = float.IsFinite(conservativeRadius)
                ? conservativeRadius
                : float.PositiveInfinity;
        }

        masks.Clear();
        for (var tileY = 0; tileY < tileCountY; tileY++)
        {
            for (var tileX = 0; tileX < tileCountX; tileX++)
            {
                var crop = BuildTileCrop(
                    tileX, tileY, viewportWidth, viewportHeight);
                var tileViewProjection = viewProjection * crop;
                if (!IsFinite(tileViewProjection))
                {
                    return FillFallback(
                        masks, tileCountX, tileCountY, lights.Length, activeMask,
                        PlacedLightTileCullFallbackReason.DegenerateTileFrustum);
                }

                var frustum = Frustum.FromViewProjection(tileViewProjection);
                if (!IsValidSidePlane(frustum.Left) || !IsValidSidePlane(frustum.Right) ||
                    !IsValidSidePlane(frustum.Bottom) || !IsValidSidePlane(frustum.Top))
                {
                    return FillFallback(
                        masks, tileCountX, tileCountY, lights.Length, activeMask,
                        PlacedLightTileCullFallbackReason.DegenerateTileFrustum);
                }

                ulong mask = 0;
                for (var lightIndex = 0; lightIndex < lights.Length; lightIndex++)
                {
                    var radius = radii[lightIndex];
                    if (radius <= 0f)
                    {
                        continue;
                    }

                    var center = centers[lightIndex];
                    if (IntersectsSidePlanes(frustum, center, radius))
                    {
                        mask |= 1UL << lightIndex;
                    }
                }

                masks[tileY * tileCountX + tileX] = mask;
            }
        }

        return new PlacedLightTileCullResult(
            tileCountX, tileCountY, lights.Length, PlacedLightTileCullFallbackReason.None);
    }

    private static PlacedLightTileCullResult FillFallback(
        Span<ulong> masks,
        int tileCountX,
        int tileCountY,
        int lightCount,
        ulong activeMask,
        PlacedLightTileCullFallbackReason reason)
    {
        masks.Fill(activeMask);
        return new PlacedLightTileCullResult(tileCountX, tileCountY, lightCount, reason);
    }

    private static bool TryResolveTileDimensions(
        int viewportWidth,
        int viewportHeight,
        out int tileCountX,
        out int tileCountY)
    {
        tileCountX = 1;
        tileCountY = 1;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return false;
        }

        var x = ((long)viewportWidth + TileSizePixels - 1) / TileSizePixels;
        var y = ((long)viewportHeight + TileSizePixels - 1) / TileSizePixels;
        if (x <= 0 || y <= 0 || x > int.MaxValue || y > int.MaxValue || x * y > int.MaxValue)
        {
            return false;
        }

        tileCountX = (int)x;
        tileCountY = (int)y;
        return true;
    }

    /// <summary>
    ///     Maps this tile's NDC rectangle to the full [-1,+1] clip rectangle. With row-vector
    ///     System.Numerics matrices the translation occupies M41/M42, and the crop post-multiplies
    ///     the existing view-projection.
    /// </summary>
    private static Matrix4x4 BuildTileCrop(
        int tileX,
        int tileY,
        int viewportWidth,
        int viewportHeight)
    {
        var pixelLeft = tileX * TileSizePixels;
        var pixelRight = Math.Min(pixelLeft + TileSizePixels, viewportWidth);
        var pixelTop = tileY * TileSizePixels;
        var pixelBottom = Math.Min(pixelTop + TileSizePixels, viewportHeight);

        // Compute in double so odd-sized viewports and the final partial tile do not lose their
        // conservative edge before the matrix is ultimately represented in float32.
        var ndcLeft = 2.0 * pixelLeft / viewportWidth - 1.0;
        var ndcRight = 2.0 * pixelRight / viewportWidth - 1.0;
        var ndcTop = 1.0 - 2.0 * pixelTop / viewportHeight;
        var ndcBottom = 1.0 - 2.0 * pixelBottom / viewportHeight;
        var scaleX = 2.0 / (ndcRight - ndcLeft);
        var scaleY = 2.0 / (ndcTop - ndcBottom);
        var translateX = -(ndcRight + ndcLeft) / (ndcRight - ndcLeft);
        var translateY = -(ndcTop + ndcBottom) / (ndcTop - ndcBottom);

        return new Matrix4x4(
            (float)scaleX, 0f, 0f, 0f,
            0f, (float)scaleY, 0f, 0f,
            0f, 0f, 1f, 0f,
            (float)translateX, (float)translateY, 0f, 1f);
    }

    private static bool IntersectsSidePlanes(Frustum frustum, Vector3 center, float radius)
    {
        return IntersectsPlane(frustum.Left, center, radius) &&
               IntersectsPlane(frustum.Right, center, radius) &&
               IntersectsPlane(frustum.Bottom, center, radius) &&
               IntersectsPlane(frustum.Top, center, radius);
    }

    private static bool IntersectsPlane(Plane plane, Vector3 center, float radius)
    {
        return Vector3.Dot(plane.Normal, center) + plane.D >= -radius;
    }

    private static bool IsValidSidePlane(Plane plane)
    {
        var lengthSquared = plane.Normal.LengthSquared();
        return IsFinite(plane.Normal) && float.IsFinite(plane.D) &&
               lengthSquared is >= 0.5f and <= 2f;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(Matrix4x4 value)
    {
        return float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
               float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
               float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
               float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
               float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
               float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
               float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
               float.IsFinite(value.M43) && float.IsFinite(value.M44);
    }

    private static ulong ActiveMask(int lightCount)
    {
        return lightCount == MaxLights
            ? ulong.MaxValue
            : (1UL << lightCount) - 1UL;
    }

    private static void RequireDestination(Span<ulong> destination, int required)
    {
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"The tile-mask destination has {destination.Length} elements; {required} are required.",
                nameof(destination));
        }
    }
}
