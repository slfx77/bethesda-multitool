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

    /// <summary>
    ///     True when no water surface drawn this frame can possibly occlude the submesh: both the
    ///     camera and the submesh's bounds BOTTOM sit above the highest surface actually queued for
    ///     draw. The sightline between two points above a plane never crosses it, so such a draw can
    ///     only ever be IN FRONT of water — it must be issued after every water batch, or the
    ///     (depth-write-free) surface composites over it. This is the complement of the submerged
    ///     partition, and like it, it is a CLASSIFICATION — the shared clip-w merge cannot express
    ///     it, because water sorts by a 4096-unit cell quad's centroid: the camera's own cell quad
    ///     sorts nearer than almost everything and would stamp over smoke plumes standing well above
    ///     the surface.
    ///     <para>
    ///         <paramref name="maxQueuedSurfaceZ" /> must be the maximum over the surfaces QUEUED
    ///         for this frame's stream (visible cell water + placed-NIF planes), not an unbounded
    ///         world gather — water that does not draw cannot occlude anything, and an unbounded
    ///         gather is what disabled the original single-plane submerged split (see
    ///         <see cref="IsWhollyBelow" />). A distant elevated body inside the view still only
    ///         degrades this test toward the interleaved status quo, never past it.
    ///     </para>
    /// </summary>
    /// <param name="worldMinZ">
    ///     The world-space BOTTOM of the submesh's bounds — transformed local-AABB minimum where
    ///     known, else the bounding sphere's lowest point. Same reasoning as
    ///     <see cref="IsWhollyBelow" />'s top: sphere extents alone misclassify the flat cards and
    ///     shallow plumes this exists to order.
    /// </param>
    internal static bool IsWhollyAboveAllWater(
        float worldMinZ, float cameraZ, float maxQueuedSurfaceZ)
    {
        if (!float.IsFinite(worldMinZ) || !float.IsFinite(cameraZ) || !float.IsFinite(maxQueuedSurfaceZ))
        {
            return false;
        }

        return worldMinZ > maxQueuedSurfaceZ && cameraZ > maxQueuedSurfaceZ;
    }
}
