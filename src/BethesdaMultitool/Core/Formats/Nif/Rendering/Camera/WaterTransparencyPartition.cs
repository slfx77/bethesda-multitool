namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Splits deferred translucent reference geometry around a horizontal generated-water plane.
///     Geometry wholly below the plane belongs in the opaque-scene/refraction source and is drawn
///     before water; intersecting or above-water geometry remains after the surface.  A conservative
///     bounding sphere prevents a card that crosses the surface from being hidden wholesale.
/// </summary>
internal static class WaterTransparencyPartition
{
    internal static bool IsWhollyBelow(float worldCenterZ, float worldRadius, float planeHeight)
    {
        if (!float.IsFinite(worldCenterZ) || !float.IsFinite(worldRadius) ||
            !float.IsFinite(planeHeight) || worldRadius < 0f)
        {
            return false;
        }

        return worldCenterZ + worldRadius < planeHeight;
    }
}
