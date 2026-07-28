using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Selects the classic FO3/FNV Lighting30 material-emittance route. Texture-set slot 2 is
///     overloaded by the engine: ordinary PP lighting treats it as a glow map, the Hair permutation
///     treats it as a highlight/layer map, and shader type 14 treats it as FaceGen skin data.
/// </summary>
internal static class NifLighting30EmissionPolicy
{
    internal const uint StandardShaderType = 1u;
    internal const uint Lighting30ShaderType = 29u;
    internal const uint LowDetailShaderFlag = 1u << 2;
    internal const uint FaceGenShaderFlag = 1u << 10;
    internal const uint HairShaderFlag = 0x00040000u;
    internal const uint LodLandscapeShaderFlag2 = 1u << 1;
    internal const uint LodBuildingShaderFlag2 = 1u << 2;

    private const uint UnsupportedShaderFlags =
        LowDetailShaderFlag | FaceGenShaderFlag | HairShaderFlag;
    private const uint UnsupportedShaderFlags2 =
        LodLandscapeShaderFlag2 | LodBuildingShaderFlag2;

    /// <summary>
    ///     True only for the standard BSShaderPPLightingProperty family that consumes material
    ///     emittance and an optional GlowMap. Missing raw metadata fails closed.
    /// </summary>
    internal static bool IsStandardLighting30(NifInfo nif, NifShaderTextureMetadata? metadata)
    {
        // FO3/FNV's classic PP layout. Later BSLightingShaderProperty families overload the same
        // numeric shader types and texture slots with unrelated data.
        if (nif.BsVersion <= 26 || nif.BsVersion > 34 ||
            metadata is not { ShaderFlags: { } flags, ShaderFlags2: { } flags2 })
        {
            return false;
        }

        var standardProperty = metadata.PropertyType == "BSShaderPPLightingProperty" &&
                               metadata.ShaderType is StandardShaderType or Lighting30ShaderType;
        var explicitLighting30Property = metadata.PropertyType == "Lighting30ShaderProperty";
        return (standardProperty || explicitLighting30Property) &&
               (flags & UnsupportedShaderFlags) == 0 &&
               (flags2 & UnsupportedShaderFlags2) == 0;
    }

    /// <summary>Returns slot 2 only when it has standard Lighting30 glow semantics.</summary>
    internal static string? ResolveGlowMapPath(NifInfo nif, NifShaderTextureMetadata? metadata)
    {
        if (!IsStandardLighting30(nif, metadata) || string.IsNullOrWhiteSpace(metadata!.GlowMapPath))
        {
            return null;
        }

        return metadata.GlowMapPath;
    }

    /// <summary>
    ///     Selects the raw RGB source exactly as Lighting30 does. Resolved XEMI replaces only RGB;
    ///     an unresolved external link falls back to NiMaterial emission, and either path retains
    ///     the material multiplier for the HDR branch.
    /// </summary>
    internal static (Vector3? Color, float MaterialMultiplier) SelectEmission(
        Vector3? materialColor,
        float materialMultiplier,
        bool externalEmittance,
        Vector3? resolvedExternalColor) =>
        (externalEmittance && resolvedExternalColor is { } external ? external : materialColor,
            materialMultiplier);
}
