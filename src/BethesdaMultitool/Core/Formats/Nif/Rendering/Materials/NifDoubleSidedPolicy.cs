namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>Combines legacy stencil state with the Skyrim-family shader-property cull flag.</summary>
internal static class NifDoubleSidedPolicy
{
    private const uint Slsf2DoubleSided = 1u << 4;

    internal static bool Resolve(
        bool stencilDrawBoth,
        NifShaderTextureMetadata? shaderMetadata)
    {
        if (stencilDrawBoth)
        {
            return true;
        }

        return shaderMetadata is
        {
            PropertyType: "BSLightingShaderProperty",
            ShaderFlags2: { } flags2
        } && (flags2 & Slsf2DoubleSided) != 0;
    }
}
