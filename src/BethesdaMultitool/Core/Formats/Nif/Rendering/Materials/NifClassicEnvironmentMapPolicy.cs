namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Resolves the separate FO3/FNV <c>BSShaderPPLightingProperty</c> environment pass.
///     Classic slot 4 is a cubemap and slot 5 is an optional red-channel mask; this is not the
///     FO4/FO76 BGSM <c>_s</c> material route.
/// </summary>
internal static class NifClassicEnvironmentMapPolicy
{
    private const uint EnvironmentMappingFlag = 1u << 7;
    private const uint EyeEnvironmentMappingFlag = 1u << 17;
    private const uint WindowEnvironmentMappingFlag = 1u << 21;

    internal static NifClassicEnvironmentMapMaterial? Resolve(NifShaderTextureMetadata? metadata)
    {
        if (metadata is not
            {
                PropertyType: "BSShaderPPLightingProperty" or "Lighting30ShaderProperty",
                ShaderFlags: { } flags,
                EnvMapScale: { } scale
            } ||
            (flags & EnvironmentMappingFlag) == 0 ||
            (flags & EyeEnvironmentMappingFlag) != 0 ||
            !float.IsFinite(scale) || scale <= 0f ||
            string.IsNullOrWhiteSpace(metadata.EnvironmentMapPath))
        {
            return null;
        }

        return new NifClassicEnvironmentMapMaterial(
            metadata.EnvironmentMapPath,
            string.IsNullOrWhiteSpace(metadata.EnvironmentMaskPath) ? null : metadata.EnvironmentMaskPath,
            scale,
            (flags & WindowEnvironmentMappingFlag) != 0);
    }
}

internal readonly record struct NifClassicEnvironmentMapMaterial(
    string CubeMapTexturePath,
    string? MaskTexturePath,
    float Scale,
    bool UsesWindowReflection);
