namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Decides how a NIF shape's texture channels map onto glTF PBR material channels (e.g. whether a glow map
///     becomes emissive).
/// </summary>
internal static class NpcGlbMaterialChannelDecider
{
    internal static bool ShouldExportGlowAsEmissive(
        RenderableSubmesh submesh,
        NifShaderTextureMetadata? shaderMetadata)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        var glowPath = shaderMetadata?.GlowMapPath;
        if (string.IsNullOrWhiteSpace(glowPath))
        {
            return false;
        }

        // FO3/NV hair, eyebrows, and facial hair commonly use the third texture slot
        // for highlight maps ("*_hl"), not self-illumination.
        if (submesh.TintColor.HasValue ||
            ContainsHairHint(submesh.ShapeName) ||
            ContainsHairHint(submesh.DiffuseTexturePath) ||
            ContainsHairHint(glowPath))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsHairHint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("brow", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("lash", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("beard", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("mustache", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("goatee", StringComparison.OrdinalIgnoreCase);
    }
}
