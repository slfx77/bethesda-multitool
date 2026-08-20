namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Decides whether refraction-flagged geometry must be omitted because the viewer has no
///     framebuffer-distortion pass. Fallout 4 also sets Fire_Refraction on an ordinary lit bottle
///     surface that has a parsed BGSM diffuse; it is renderable and must not be mistaken for a
///     distortion-only helper.
/// </summary>
internal static class NifRefractionShapePolicy
{
    private const uint RefractionFlags = (1u << 15) | (1u << 16);
    private const uint Fallout4BsVersion = 130;

    internal static bool ShouldSkipUnsupportedDistortion(
        uint bsVersion,
        NifShaderTextureMetadata? metadata,
        string? parsedLightingMaterialDiffuse = null)
    {
        if (metadata?.ShaderFlags is not { } flags || (flags & RefractionFlags) == 0)
        {
            return false;
        }

        if (metadata.PropertyType is not (
            "BSLightingShaderProperty" or
            "BSShaderPPLightingProperty" or
            "Lighting30ShaderProperty" or
            "BSShaderNoLightingProperty"))
        {
            return false;
        }

        return !IsRenderableFallout4LightingSurface(
            bsVersion,
            metadata,
            parsedLightingMaterialDiffuse);
    }

    private static bool IsRenderableFallout4LightingSurface(
        uint bsVersion,
        NifShaderTextureMetadata metadata,
        string? parsedLightingMaterialDiffuse)
    {
        return bsVersion == Fallout4BsVersion &&
               metadata.PropertyType == "BSLightingShaderProperty" &&
               metadata.MaterialPath?.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) == true &&
               parsedLightingMaterialDiffuse?.EndsWith("_d.dds", StringComparison.OrdinalIgnoreCase) == true;
    }
}
