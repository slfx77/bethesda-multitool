using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>Exact identity check for reusing a previously built placed-light tile mask.</summary>
internal static class PlacedLightTileCachePolicy
{
    internal static bool Matches(
        Matrix4x4 cachedViewProjection,
        int cachedViewportWidth,
        int cachedViewportHeight,
        Vector3 cachedRenderOrigin,
        ReadOnlySpan<PlacedLight> cachedLights,
        Matrix4x4 viewProjection,
        int viewportWidth,
        int viewportHeight,
        Vector3 renderOrigin,
        ReadOnlySpan<PlacedLight> lights)
    {
        return cachedViewportWidth == viewportWidth &&
               cachedViewportHeight == viewportHeight &&
               cachedViewProjection.Equals(viewProjection) &&
               cachedRenderOrigin.Equals(renderOrigin) &&
               cachedLights.SequenceEqual(lights);
    }
}
