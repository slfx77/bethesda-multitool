namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Supplies the water surface height covering a world XY. Implemented by the water renderer;
///     abstracted so the reference renderer can classify translucent draws around water without
///     depending on D3D12 water resources.
/// </summary>
internal interface IWaterHeightProbe
{
    /// <summary>The water surface height at this world XY, or false where there is no water.</summary>
    bool TryGetWaterHeightAt(float worldX, float worldY, out float height);
}

/// <summary>
///     Splits deferred translucent reference geometry around the water surface. Geometry wholly below
///     the surface belongs in the opaque-scene/refraction source and is drawn before water;
///     intersecting or above-water geometry remains after the surface, so a card that crosses the
///     surface is never hidden wholesale.
/// </summary>
internal static class WaterTransparencyPartition
{
    /// <summary>
    ///     Classifies a submesh against the water surface LOCAL to its own XY.
    ///     <para>
    ///         The local lookup replaced a single global plane taken as the maximum height over every
    ///         gathered water cell. That could not work: the gather spans the whole render distance
    ///         with no frustum or Z bound, so one distant elevated body decided the plane for every
    ///         draw, and the accompanying camera-above-plane guard then disabled the split outright
    ///         wherever any visible water sat above the camera. Measured at Lake Mead: local surface
    ///         3000, global max 5600, camera 3200 — split disabled, decals composited over the water.
    ///     </para>
    ///     <para>
    ///         <paramref name="cameraZ" /> is compared against that same local surface, so a camera
    ///         under one body still partitions correctly against another. Without it, a submerged
    ///         camera classifies everything as "below" and a distant water quad composites over
    ///         near-camera underwater effects — the mirror image of the bug this fixes.
    ///     </para>
    /// </summary>
    /// <param name="worldMaxZ">
    ///     The world-space TOP of the submesh's bounds — the transformed local-AABB maximum where one
    ///     is known, else the bounding sphere's apex. A sphere apex alone is far too pessimistic for
    ///     the flat decal cards this exists to classify (measured radii 70-125 units), which is why
    ///     the test takes a top rather than a centre and a radius.
    /// </param>
    internal static bool IsWhollyBelow(
        IWaterHeightProbe probe, float worldX, float worldY, float worldMaxZ, float cameraZ)
    {
        if (!float.IsFinite(worldMaxZ) || !float.IsFinite(worldX) || !float.IsFinite(worldY))
        {
            return false;
        }

        if (!probe.TryGetWaterHeightAt(worldX, worldY, out var surface) || !float.IsFinite(surface))
        {
            return false;
        }

        return worldMaxZ < surface && cameraZ > surface;
    }
}
