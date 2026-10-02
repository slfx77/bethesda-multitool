using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>The original 35-field material identity plus the source tint baked into a resolved diffuse image.</summary>
/// <remarks>The appended tint corrects inherited aliasing between different baked pixels whose textured
/// base-color factors are both white. The original fields retain their order and equality semantics.</remarks>
internal readonly record struct NifMaterialCacheKey(
    string? DiffusePath,
    string? NormalPath,
    string? EmissivePath,
    bool HasActiveBgsmEmission,
    Vector3 EmissiveFactor,
    float EmissiveStrength,
    string? HeightPath,
    string? EnvironmentMaskPath,
    bool IsEmissive,
    bool IsStarfieldWater,
    bool IsAuthoredSkyPreview,
    bool UseVertexColors,
    bool IsDoubleSided,
    NifAlphaRenderMode AlphaMode,
    byte AlphaThreshold,
    byte AlphaFunction,
    bool HasPreparedAlphaTexture,
    bool ClampTextureU,
    bool ClampTextureV,
    float MetallicFactor,
    float RoughnessFactor,
    float SpecularFactor,
    StarfieldMaterialColorRenderState StarfieldColor,
    string? StarfieldMaterialPath,
    StarfieldMaterialAlphaRenderState StarfieldAlpha,
    bool HasStarfieldOpacityTexture,
    StarfieldMaterialEffectPolicy StarfieldEffectPolicy,
    bool HasStarfieldEffectAlpha,
    StarfieldMaterialOrmPolicy StarfieldOrmPolicy,
    bool HasStaticStarfieldOrmPolicy,
    bool HasStarfieldOrm,
    bool HasStarfieldOrmTexture,
    bool HasStarfieldAmbientOcclusion,
    StarfieldGlbVertexLerpProjectionResult StarfieldVertexLerpProjection,
    Vector4 BaseColor,
    (float R, float G, float B)? BakedDiffuseTint);
