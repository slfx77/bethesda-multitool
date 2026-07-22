using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Classifies the bounded PC-final FNV PS1 basic bump-lighting family recovered as
///     SLS1009/SLS1013. The classifier is retained as shader-package audit evidence, but the family
///     is not activated at runtime: retail selects the PS2/PS3 pass builder and the engine labels
///     the SLS1009/SLS1013 pass IDs as unused.
/// </summary>
internal static class FnvClassicBasicShaderPolicy
{
    // Fallout_Release_MemDebug: GetRenderPasses selects GetRenderPasses_1x only for shader tier 1,
    // while the captured PC retail configuration reports BSSM_SV_2_A / PS 3.0 / 3.0 lighting.
    // Pass IDs 700/701 (SLS1009/SLS1013) are inside BSSM_UNUSEDPASSES_FIRST..LAST (673..755).
    // This audit identity never owns runtime material bits. The active retail ADT policy owns its
    // independently named flags and applies them only after its stricter frame/draw eligibility gate.
    internal const bool RetailRuntimeSupported = false;

    private const uint SpecularFlag = 1u << 0;
    private const uint SkinnedFlag = 1u << 1;
    private const uint LowDetailFlag = 1u << 2;
    private const uint ForcedSinglePassFlag = 1u << 5;
    private const uint EnvironmentMappingFlag = 1u << 7;
    private const uint FaceGenFlag = 1u << 10;
    private const uint ParallaxFlag = 1u << 11;
    private const uint RefractionFlags = (1u << 15) | (1u << 16);
    private const uint EyeEnvironmentMappingFlag = 1u << 17;
    private const uint HairFlag = 1u << 18;
    private const uint WindowEnvironmentMappingFlag = 1u << 21;
    private const uint DecalFlags = (1u << 26) | (1u << 27);
    private const uint ParallaxOcclusionFlag = 1u << 28;
    private const uint ExternalEmittanceFlag = 1u << 29;
    private const uint UnsupportedFlags =
        SpecularFlag | SkinnedFlag | LowDetailFlag | ForcedSinglePassFlag |
        EnvironmentMappingFlag | FaceGenFlag | ParallaxFlag |
        RefractionFlags | EyeEnvironmentMappingFlag | HairFlag | WindowEnvironmentMappingFlag |
        DecalFlags | ParallaxOcclusionFlag | ExternalEmittanceFlag;

    private const uint LodLandscapeFlag2 = 1u << 1;
    private const uint LodBuildingFlag2 = 1u << 2;
    private const uint UnsupportedFlags2 = LodLandscapeFlag2 | LodBuildingFlag2;
    private const float BasisLengthSquaredEpsilon = 1e-8f;

    /// <summary>
    ///     Resolves an audit/classifier identity from source metadata before it enters the decoded-mesh
    ///     cache. It never activates the dormant PS1 family; FO3 and FNV share BS34 NIF layouts and can
    ///     share decoded assets.
    /// </summary>
    internal static FnvClassicBasicShaderMode Resolve(NifInfo nif, RenderableSubmesh submesh) =>
        Resolve(nif, submesh, submesh.DiffuseTexturePath, submesh.NormalMapTexturePath);

    /// <summary>
    ///     Resolves against the effective draw paths. MODS/TXST alternate textures are applied after
    ///     NIF extraction, so the decoder must supply those paths instead of silently classifying the
    ///     pre-override material while uploading a different diffuse/normal pair.
    /// </summary>
    internal static FnvClassicBasicShaderMode Resolve(
        NifInfo nif,
        RenderableSubmesh submesh,
        string? effectiveDiffuseTexturePath,
        string? effectiveNormalMapTexturePath)
    {
        if (nif.BsVersion != 34 ||
            submesh.ShaderMetadata is not
            {
                PropertyType: "BSShaderPPLightingProperty",
                ShaderType: NifLighting30EmissionPolicy.StandardShaderType,
                ShaderFlags: { } flags,
                ShaderFlags2: { } flags2
            } ||
            (flags & UnsupportedFlags) != 0 ||
            (flags2 & UnsupportedFlags2) != 0 ||
            submesh.IsEmissive || submesh.IsFaceGen || submesh.IsBillboard ||
            submesh.IsLeafBillboard || submesh.IsParticleCloud || submesh.IsSpeedTreeBranch ||
            submesh.IsDecal ||
            submesh.BindPosePositions is not null ||
            !string.IsNullOrWhiteSpace(submesh.Lighting30GlowMapTexturePath) ||
            HasEmission(submesh.Lighting30EmissionColor) ||
            !string.IsNullOrWhiteSpace(submesh.ClassicEnvironmentMapTexturePath) ||
            !string.IsNullOrWhiteSpace(submesh.ClassicParallaxHeightMapTexturePath) ||
            string.IsNullOrWhiteSpace(effectiveDiffuseTexturePath) ||
            string.IsNullOrWhiteSpace(effectiveNormalMapTexturePath) ||
            !HasUsableBumpGeometry(submesh))
        {
            return FnvClassicBasicShaderMode.None;
        }

        return HasCompleteVertexColorData(submesh)
            ? FnvClassicBasicShaderMode.Sls1013VertexColor
            : FnvClassicBasicShaderMode.Sls1009;
    }

    /// <summary>CPU oracle for SLS1009/1013: <c>Ambient + rawSignedDp3 * PSLightColor</c>.</summary>
    internal static Vector3 EvaluateShade(Vector3 ambient, float rawSignedDp3, Vector3 psLightColor) =>
        ambient + psLightColor * rawSignedDp3;

    /// <summary>
    ///     CPU oracle for the final RGB instructions. SLS1009 returns BaseMap*shade; SLS1013
    ///     multiplies that result by the authored vertex RGB. Fog is a separate SLS permutation axis.
    /// </summary>
    internal static Vector3 Composite(
        FnvClassicBasicShaderMode mode,
        Vector3 baseMap,
        Vector3 shade,
        Vector3 vertexRgb)
    {
        var baseLit = Vector3.Multiply(baseMap, shade);
        return mode == FnvClassicBasicShaderMode.Sls1013VertexColor
            ? Vector3.Multiply(baseLit, vertexRgb)
            : baseLit;
    }

    private static bool HasEmission((float R, float G, float B)? emission) =>
#pragma warning disable S1244 // authored emission of exactly (0,0,0) means none; any non-zero component counts
        emission is { } value && (value.R != 0f || value.G != 0f || value.B != 0f);
#pragma warning restore S1244

    private static bool HasCompleteVertexColorData(RenderableSubmesh submesh) =>
        submesh.UseVertexColors &&
        submesh.VertexColors is { } colors &&
        submesh.Positions.Length % 3 == 0 &&
        colors.Length >= submesh.Positions.Length / 3 * 4;

    private static bool HasUsableBumpGeometry(RenderableSubmesh submesh)
    {
        var positionCount = submesh.Positions.Length;
        var vertexCount = positionCount / 3;
        if (positionCount == 0 || positionCount % 3 != 0 ||
            submesh.Triangles.Length % 3 != 0 ||
            submesh.Normals is not { } normals || normals.Length < positionCount ||
            submesh.Tangents is not { } tangents || tangents.Length < positionCount ||
            submesh.Bitangents is not { } bitangents || bitangents.Length < positionCount ||
            submesh.UVs is not { } uvs || uvs.Length < vertexCount * 2)
        {
            return false;
        }

        foreach (var index in submesh.Triangles)
        {
            if (index >= vertexCount)
            {
                return false;
            }
        }

        // This is deliberately conservative: reject bad unreferenced vertices too. The decoded mode
        // is persisted per submesh, while the recovered VSO has no finite/degenerate guard; keeping a
        // malformed tail vertex cannot improve fidelity and could become live after later index repair.
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var vectorOffset = vertex * 3;
            var uvOffset = vertex * 2;
            if (!float.IsFinite(uvs[uvOffset]) || !float.IsFinite(uvs[uvOffset + 1]) ||
                !IsUsable(normals, vectorOffset) ||
                !IsUsable(tangents, vectorOffset) ||
                !IsUsable(bitangents, vectorOffset))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUsable(float[] values, int offset)
    {
        var x = values[offset];
        var y = values[offset + 1];
        var z = values[offset + 2];
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
               x * x + y * y + z * z > BasisLengthSquaredEpsilon;
    }
}

internal enum FnvClassicBasicShaderMode : byte
{
    None = 0,
    Sls1009 = 1,
    Sls1013VertexColor = 2
}
