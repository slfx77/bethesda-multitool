namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>Decides whether and how a submesh's vertex colors should be applied during rendering.</summary>
internal static class NifVertexColorPolicy
{
    private const uint TreeAnimationShaderFlag2 = 1u << 29;

    /// <summary>
    ///     Identifies Bethesda's TREE_ANIM shader family. Skyrim-family BSLighting properties
    ///     declare it through SLSF2_Tree_Anim; graph ancestry is the fallback for streams whose
    ///     shader-flags layout is not exposed by the current parser.
    /// </summary>
    internal static bool IsTreeAnimation(
        NifShaderTextureMetadata? shaderMetadata,
        bool hasTreeAnimationAncestry = false)
    {
        return hasTreeAnimationAncestry ||
               (shaderMetadata is
               {
                   PropertyType: "BSLightingShaderProperty",
                   ShaderFlags2: { } flags2
               } && (flags2 & TreeAnimationShaderFlag2) != 0);
    }

    /// <summary>
    ///     Resolves whether a shader family treats vertex alpha as opacity. The FNV retail grass
    ///     shaders use it only as a squared wind-displacement weight: GRASS2000.vso multiplies the
    ///     wind offset by <c>vColor.a^2</c>, while GRASS2000.pso compares <c>DiffuseMap.a</c>
    ///     directly with <c>AlphaTestRef</c>. Skyrim's retail TREE_ANIM vertex shader likewise uses
    ///     vertex alpha as its wind displacement weight, while the paired pixel shader tests only
    ///     diffuse alpha times material alpha. TREE_ANIM identity therefore takes precedence
    ///     over SLSF1_Vertex_Alpha: that bit is set on the shipped Snow01-05 foliage even though its
    ///     low vertex-alpha values are animation data, not coverage. Outside those explicit shader
    ///     families, a readable SLSF1_Vertex_Alpha flag remains authoritative.
    /// </summary>
    internal static bool UsesAlphaForOpacity(
        NifShaderTextureMetadata? shaderMetadata,
        bool isTreeAnimationShape = false)
    {
        if (shaderMetadata?.PropertyType == "TallGrassShaderProperty")
        {
            return false;
        }

        if (IsTreeAnimation(shaderMetadata, isTreeAnimationShape))
        {
            return false;
        }

        if (shaderMetadata is
            { PropertyType: "BSLightingShaderProperty", ShaderFlags: { } flags1 })
        {
            return (flags1 & 0x8u) != 0;
        }

        return true;
    }

    internal static bool HasVertexColorData(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        return submesh.VertexColors != null && (submesh.UseVertexColors || submesh.IsEmissive);
    }

    internal static (byte R, byte G, byte B, byte A) Read(RenderableSubmesh submesh, int vertexIndex)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        if (!HasVertexColorData(submesh))
        {
            return (255, 255, 255, 255);
        }

        var offset = vertexIndex * 4;
        var colors = submesh.VertexColors!;
        var alpha = submesh.UseVertexAlphaForOpacity ? colors[offset + 3] : byte.MaxValue;

        // BSShaderNoLighting effect meshes commonly store per-vertex alpha fades without
        // enabling Vertex_Colors RGB modulation. Preserve the alpha ramp, but keep RGB neutral.
        if (!submesh.UseVertexColors)
        {
            return (255, 255, 255, alpha);
        }

        return (colors[offset], colors[offset + 1], colors[offset + 2], alpha);
    }
}
