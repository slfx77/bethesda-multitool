using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Materials;

/// <summary>
///     Effective CE2 material data needed to project one static base layer into core glTF's
///     occlusion/roughness/metallic channels. This is deliberately an export-fidelity policy, not a
///     claim that glTF's lighting model reproduces Starfield's deferred shader.
/// </summary>
internal readonly record struct StarfieldMaterialOrmPolicy(
    bool IsResolved,
    bool HasOnlyLayer0,
    bool HasBlenders,
    bool LayerUsesFlipbook,
    bool HasMalformedStaticComponents,
    StarfieldMaterialShaderRoute ShaderRoute,
    bool UsesHairRoughnessDefault,
    Vector2 UvScale,
    Vector2 UvOffset,
    StarfieldMaterialTextureAddressMode TextureAddressMode,
    StarfieldMaterialUvChannel UvChannel,
    StarfieldMaterialSlot RoughnessSlot,
    StarfieldMaterialSlot MetalnessSlot,
    StarfieldMaterialSlot AmbientOcclusionSlot)
{
    /// <summary>
    ///     Accept only the subset whose three scalar maps can share glTF TEXCOORD_0 without baking
    ///     animation, layer composition, alternate coordinates, or sampler transforms. Unsupported
    ///     combinations return no state so callers cannot silently export a plausible-looking but
    ///     semantically different ORM texture.
    /// </summary>
    internal bool TryResolveStaticLayer0Orm(out StarfieldMaterialOrmState state)
    {
        state = default;
        if (!IsResolved ||
            !HasOnlyLayer0 ||
            HasBlenders ||
            LayerUsesFlipbook ||
            HasMalformedStaticComponents ||
            ShaderRoute != StarfieldMaterialShaderRoute.Deferred ||
            UsesHairRoughnessDefault ||
            UvScale != Vector2.One ||
            UvOffset != Vector2.Zero ||
            TextureAddressMode != StarfieldMaterialTextureAddressMode.Wrap ||
            UvChannel != StarfieldMaterialUvChannel.One)
        {
            return false;
        }

        state = new StarfieldMaterialOrmState(
            RoughnessSlot,
            MetalnessSlot,
            AmbientOcclusionSlot);
        return true;
    }
}

/// <summary>The three independent red-channel CE2 inputs accepted by the static GLB export path.</summary>
internal readonly record struct StarfieldMaterialOrmState(
    StarfieldMaterialSlot RoughnessSlot,
    StarfieldMaterialSlot MetalnessSlot,
    StarfieldMaterialSlot AmbientOcclusionSlot);

internal enum StarfieldMaterialShaderRoute : byte
{
    Deferred,
    Effect,
    PlanetaryRing,
    PrecomputedScattering,
    Water
}

internal enum StarfieldMaterialTextureAddressMode : byte
{
    Wrap,
    Clamp,
    Mirror,
    Border
}

/// <remarks>CE2's UV stream names are zero-based words: <c>One</c> is its default UV0 stream.</remarks>
internal enum StarfieldMaterialUvChannel : byte
{
    Zero,
    One,
    Two,
    Three
}
