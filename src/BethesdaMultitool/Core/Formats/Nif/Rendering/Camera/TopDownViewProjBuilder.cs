using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Builds the orthographic top-down view-projection (and a covering visibility cylinder) used by
///     the 2D map's "Rendered models" overlay. Bypasses the perspective <see cref="CameraState" /> —
///     the renderers treat the view-projection as an opaque matrix.
///     <para>
///         Convention: WORLD space is X = east, Y = north, Z = up. The camera sits high above the
///         rectangle center looking straight down −Z with up = +Y. With up = +Y and an ortho frustum
///         where top &gt; bottom, world +X maps to clip +X (east → right) and world +Y maps to clip
///         +Y (north → top row). A row-major GPU readback is therefore north→south, matching the 2D
///         map's canvas convention (X right, north up) with no flip. This is locked by
///         <c>TopDownViewProjBuilderTests</c>.
///     </para>
/// </summary>
internal static class TopDownViewProjBuilder
{
    /// <summary>Camera height above the ground plane. Large so all geometry sits within the
    /// [near, far] ortho depth range regardless of placement Z.</summary>
    public const float EyeHeight = 1_000_000f;

    /// <summary>Builds the view-projection for the given world rectangle (north-Y).</summary>
    public static Matrix4x4 BuildViewProj(float worldMinX, float worldMaxX, float worldMinY, float worldMaxY)
    {
        // View is a pure −Z translation (camera lifted by EyeHeight looking straight down), NOT a
        // look-at: a look-at would recenter X/Y on the camera, which wouldn't match an ortho frustum
        // expressed in world coordinates. Keeping X/Y untouched lets the off-center ortho map the
        // world rect directly — worldMinX→clip −1 (west/left), worldMaxX→+1 (east/right),
        // worldMinY→−1 (south/bottom), worldMaxY→+1 (north/top). Reversed-Z (CameraState.ReverseZ,
        // shared with the live perspective view so the offscreen depth test matches the scene PSOs'
        // GreaterEqual + depth-clear-0): higher world Z (taller geometry) lands at a LARGER clip Z,
        // so it wins the depth test. Only the Z row is touched — X/Y readback orientation is intact.
        var view = Matrix4x4.CreateTranslation(0f, 0f, -EyeHeight);
        var proj = Matrix4x4.CreateOrthographicOffCenter(
            worldMinX, worldMaxX, worldMinY, worldMaxY, 1f, 2f * EyeHeight);
        return view * proj * CameraState.ReverseZ;
    }

    /// <summary>
    ///     A visibility cylinder centered on the rectangle, with radius covering its corners plus
    ///     <paramref name="slack" />. <see cref="VisibilityCylinder" /> is an XY-circle cull
    ///     (camera-orientation-independent), so a generous radius simply admits every cell that could
    ///     be visible in the top-down view.
    /// </summary>
    public static VisibilityCylinder BuildCoverCylinder(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY, float slack)
    {
        var cx = (worldMinX + worldMaxX) * 0.5f;
        var cy = (worldMinY + worldMaxY) * 0.5f;
        var halfW = (worldMaxX - worldMinX) * 0.5f;
        var halfH = (worldMaxY - worldMinY) * 0.5f;
        var radius = MathF.Sqrt(halfW * halfW + halfH * halfH) + slack;
        return new VisibilityCylinder(new Vector3(cx, cy, EyeHeight), radius);
    }
}
