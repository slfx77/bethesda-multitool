namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>Decides whether and how a submesh's vertex colors should be applied during rendering.</summary>
internal static class NifVertexColorPolicy
{
    /// <summary>
    ///     Resolves whether a shader family treats vertex alpha as opacity. The FNV retail grass
    ///     shaders use it only as a squared wind-displacement weight: GRASS2000.vso multiplies the
    ///     wind offset by <c>vColor.a^2</c>, while GRASS2000.pso compares <c>DiffuseMap.a</c>
    ///     directly with <c>AlphaTestRef</c>. Skyrim-family lighting shaders instead declare the
    ///     choice through SLSF1_Vertex_Alpha (flags1 bit 3).
    /// </summary>
    internal static bool UsesAlphaForOpacity(NifShaderTextureMetadata? shaderMetadata)
    {
        if (shaderMetadata?.PropertyType == "TallGrassShaderProperty")
        {
            return false;
        }

        return shaderMetadata is not
                   { PropertyType: "BSLightingShaderProperty", ShaderFlags: { } flags1 } ||
               (flags1 & 0x8u) != 0;
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
