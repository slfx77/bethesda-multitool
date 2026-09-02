using BethesdaMultitool.Core.Formats.Nif.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Bounded bridge from a decoded CE2 material route to the viewer's existing placed-water
///     classification. It intentionally carries no Starfield optics: World Viewer consumes the
///     sentinel with its dedicated WATR approximation, while standalone/GLB consumers receive a
///     separately marked physical preview whose neutral optics and global normal remain explicit
///     approximations.
/// </summary>
internal static class StarfieldWaterMaterialRoute
{
    /// <summary>
    ///     glTF material marker consumed only by the embedded Mesh Viewer. Portable viewers still
    ///     receive standard transmission, IOR, clear-coat, roughness, and normal-map channels.
    /// </summary>
    internal const string MeshViewerMaterialExtrasKey = "bethesdaStarfieldWaterApproxV1";

    /// <summary>
    ///     Shipped Starfield global water normal used by the existing source-backed World Viewer
    ///     approximation. Standalone NIFs carry no WATR record, so Mesh Viewer deliberately uses
    ///     only this primary global asset and labels the resulting material as an approximation.
    /// </summary>
    internal const string MeshViewerPrimaryNormalTexturePath =
        @"textures\water\defaultwater_normal.dds";

    // Neutral physical-preview constants. IOR is the standard water value; the remaining values
    // are intentionally conservative viewer defaults because standalone NIFs provide neither WATR
    // roughness nor recovered CE2 transmission/clear-coat bindings.
    internal const float MeshViewerIndexOfRefraction = 1.333f;
    internal const float MeshViewerRoughness = 0.12f;
    internal const float MeshViewerTransmission = 0.72f;
    internal const float MeshViewerClearCoat = 1f;
    internal const float MeshViewerClearCoatRoughness = 0.08f;

    /// <summary>
    ///     Applies the extraction state selected by an explicit effective ShaderRoute::Water.
    ///     Unresolved, malformed, Deferred, and every other route leave the caller's ordinary
    ///     material state byte-for-byte unchanged.
    /// </summary>
    internal static bool TryApply(
        string materialPath,
        NifTextureResolver textureResolver,
        ref string? diffuseTexturePath,
        ref StarfieldMaterialColorPolicy colorPolicy,
        ref StarfieldMaterialAlphaPolicy alphaPolicy,
        ref bool hasAlphaBlend,
        ref bool hasAlphaTest,
        ref float materialAlpha)
    {
        ArgumentNullException.ThrowIfNull(textureResolver);
        if (textureResolver.ResolveStarfieldShaderRoute(materialPath) !=
            StarfieldMaterialShaderRoute.Water)
        {
            return false;
        }

        diffuseTexturePath = RenderableSubmesh.WaterSurfaceTexturePath;
        // Generic CE2 layer colour and AlphaSettings belong to the ordinary material route. They
        // cannot override the dedicated water renderer or its neutral translucent fallback.
        colorPolicy = default;
        alphaPolicy = default;
        hasAlphaBlend = true;
        hasAlphaTest = false;
        if (materialAlpha >= 0.999f)
        {
            materialAlpha = 0.5f;
        }

        return true;
    }
}
