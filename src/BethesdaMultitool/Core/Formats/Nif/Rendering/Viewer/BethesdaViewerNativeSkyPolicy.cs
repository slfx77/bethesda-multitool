using BethesdaMultitool.Core.Formats.Nif;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Narrow admission policy for the native viewer's camera-centred sky pass. A sky shader tag
///     inside an assembled NPC/creature scene remains ordinary scene geometry: only a raw NIF can
///     opt into camera centring, and only the three geometry-layer types understood losslessly by
///     <c>SkyGeometryRenderer12</c> are admitted.
/// </summary>
internal static class BethesdaViewerNativeSkyPolicy
{
    internal static bool IsDedicatedRawNifLayer(
        BethesdaViewerScenePurpose purpose,
        SkyObjectType? type) =>
        purpose == BethesdaViewerScenePurpose.RawNif &&
        type is SkyObjectType.Sky or SkyObjectType.Stars or SkyObjectType.Clouds;

    /// <summary>
    ///     True only when every drawable part belongs to the dedicated camera-centred raw-NIF sky
    ///     path. This narrow gate keeps the sky inspection camera out of mixed raw assets and every
    ///     assembled NPC, creature, or world-reference scene.
    /// </summary>
    internal static bool ShouldUseDedicatedRawNifFraming(BethesdaViewerScene? scene)
    {
        if (scene is not { Purpose: BethesdaViewerScenePurpose.RawNif })
        {
            return false;
        }

        var hasDrawablePart = false;
        foreach (var part in scene.MeshParts)
        {
            if (part.Submesh.Triangles.Length == 0)
            {
                continue;
            }

            hasDrawablePart = true;
            if (!IsDedicatedRawNifLayer(scene.Purpose, part.Submesh.SkyType))
            {
                return false;
            }
        }

        return hasDrawablePart;
    }
}
