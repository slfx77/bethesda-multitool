namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Resolves whether a decoded NIF submesh can enter the viewer's sun-specular shader branch.
/// </summary>
internal static class NifSpecularPolicy
{
    private const uint SpecularFlag = 1u << 0;

    internal static bool IsEnabled(RenderableSubmesh sub)
    {
        if (sub.IsEmissive || sub.MaterialGlossiness <= 0f)
        {
            return false;
        }

        // Retail TES4 SM3002/HAIR programs consume HairTint and the normal texture, but expose
        // neither NiMaterialProperty.SpecularColor nor Glossiness. Letting the generic fallback
        // reinterpret the stock 0.9 / 10 values as an additive white Blinn highlight turns the
        // authored normal-alpha strands into a metallic white crown. The NPC adapter supplies an
        // explicit semantic so metadata-less non-hair tints and every modern hair shader remain
        // outside this exception.
        if (sub.UsesClassicHairMaterial)
        {
            return false;
        }

        // FO3/FNV's specular SLS permutation is selected by BSShaderFlags.Specular. Its pixel
        // shader scales the highlight by NormalMap.a and the LIGHT color; it does not multiply by
        // NiMaterialProperty.SpecularColor. Retail assets consequently author black material
        // specular on valid specular shapes (CliffVerti_C2 is a representative example). Requiring
        // a non-black material tint silently vetoes the authored permutation. Keep the gate tied to
        // inputs this viewer can actually consume: flag + DXT5 normal/mask path + a valid TBN.
        if (sub.ShaderMetadata is
            {
                PropertyType: "BSShaderPPLightingProperty" or "Lighting30ShaderProperty",
                ShaderFlags: uint classicFlags
            })
        {
            return (classicFlags & SpecularFlag) != 0 &&
                   !string.IsNullOrEmpty(sub.NormalMapTexturePath) &&
                   sub.Tangents is not null &&
                   sub.Bitangents is not null;
        }

        // Other material families retain the conservative legacy fallback until their own shader
        // permutation is recovered: a usable tint/gloss pair, plus the flag when one is surfaced.
        var (r, g, b) = sub.SpecularColor;
        if (MathF.Max(r, MathF.Max(g, b)) < 0.04f)
        {
            return false;
        }

        return sub.ShaderMetadata?.ShaderFlags is not uint flags || (flags & SpecularFlag) != 0;
    }
}
