using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Pure filter: given a cell's flat <c>LandTextureLayer</c> list, extract the BTXT base
///     plus the ATXT alpha layers for one quadrant, with alphas sorted by their
///     <see cref="LandTextureLayer.Layer" /> index. No D3D dependencies.
///
///     Returns <c>null</c> for the base when the quadrant has no BTXT — callers should bind
///     the engine-default landscape texture in that case (see
///     <see cref="EngineDefaultLandscapeTexture" />). Earlier the 3D renderer promoted the
///     first ATXT to base in this scenario, but that diverged from the 2D map's engine-default
///     fallback; both views now agree on the canonical behavior.
/// </summary>
internal static class QuadrantLayerSelector
{
    /// <summary>
    ///     Filters <paramref name="layers" /> to the given <paramref name="quadrant" />, sorted
    ///     such that the returned base (if any) is the BTXT for that quadrant and
    ///     <paramref name="alphasOutput" /> receives the ATXT layers in ascending
    ///     <see cref="LandTextureLayer.Layer" /> order. The output list is cleared by this
    ///     method before alphas are appended, so the caller can safely reuse a single instance
    ///     across calls.
    /// </summary>
    internal static LandTextureLayer? SelectBaseAndAlphas(
        IReadOnlyList<LandTextureLayer> layers,
        int quadrant,
        List<LandTextureLayer> alphasOutput)
    {
        alphasOutput.Clear();
        LandTextureLayer? baseLayer = null;

        for (var i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            if (l.Quadrant != quadrant) continue;
            if (l.Kind == LandTextureLayerKind.Base)
            {
                baseLayer = l;
            }
            else
            {
                alphasOutput.Add(l);
            }
        }

        // Stable sort by Layer ASC. Most cells have ≤3 alphas so InsertionSort is fine and
        // avoids List<T>.Sort's IComparer<T> allocation.
        for (var i = 1; i < alphasOutput.Count; i++)
        {
            var current = alphasOutput[i];
            var j = i - 1;
            while (j >= 0 && alphasOutput[j].Layer > current.Layer)
            {
                alphasOutput[j + 1] = alphasOutput[j];
                j--;
            }
            alphasOutput[j + 1] = current;
        }

        return baseLayer;
    }
}
