using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Materials;

/// <summary>CE2 <c>BSMaterial::EffectSettingsComponent.BlendingMode</c> values.</summary>
internal enum StarfieldMaterialEffectBlendMode : byte
{
    AlphaBlend = 0,
    Additive = 1,
    SourceSoftAdditive = 2,
    Multiply = 3,
    DestinationSoftAdditive = 4,
    DestinationInvertedSoftAdditive = 5,
    TakeSmaller = 6,
    None = 7
}

/// <summary>
///     The bounded CE2 effect-material subset that core glTF can represent without substituting a
///     different blend equation or inventing optical constants.
/// </summary>
internal readonly record struct StarfieldMaterialEffectAlphaState(
    float MaterialOverallAlpha,
    StarfieldMaterialSlot OpacitySlot);

/// <summary>
///     Effective CE2 effect settings plus the static base-layer facts needed by Mesh Viewer export.
///     Unsupported settings remain visible on the policy for diagnostics and fail closed in
///     <see cref="TryResolveStaticGlassAlphaBlend" />.
/// </summary>
internal readonly record struct StarfieldMaterialEffectPolicy(
    bool IsResolved,
    bool HasEffectSettings,
    bool HasMalformedSettings,
    StarfieldMaterialShaderRoute ShaderRoute,
    bool IsGlass,
    bool HasFrosting,
    bool UsesVertexColor,
    bool HasLayeredEdgeFalloff,
    float MaterialOverallAlpha,
    StarfieldMaterialEffectBlendMode BlendingMode,
    bool HasOnlyLayer0,
    bool HasBlenders,
    bool LayerUsesFlipbook,
    Vector2 UvScale,
    Vector2 UvOffset,
    StarfieldMaterialTextureAddressMode TextureAddressMode,
    StarfieldMaterialUvChannel UvChannel,
    int OpacitySourceLayer,
    bool HasSecondaryOpacityLayers,
    StarfieldMaterialSlot OpacitySlot)
{
    /// <summary>
    ///     Admits only authored Effect-route glass using the ordinary source-alpha blend equation.
    ///     Additive/multiply effects, animated or layered composition, view-dependent edge falloff,
    ///     frosting and vertex-driven alpha cannot be represented by core glTF and remain opaque.
    /// </summary>
    internal bool TryResolveStaticGlassAlphaBlend(out StarfieldMaterialEffectAlphaState state)
    {
        state = default;
        if (!IsResolved ||
            !HasEffectSettings ||
            HasMalformedSettings ||
            ShaderRoute != StarfieldMaterialShaderRoute.Effect ||
            !IsGlass ||
            HasFrosting ||
            UsesVertexColor ||
            HasLayeredEdgeFalloff ||
            BlendingMode != StarfieldMaterialEffectBlendMode.AlphaBlend ||
            !float.IsFinite(MaterialOverallAlpha) ||
            MaterialOverallAlpha is < 0f or > 1f ||
            !HasOnlyLayer0 ||
            HasBlenders ||
            LayerUsesFlipbook ||
            UvScale != Vector2.One ||
            UvOffset != Vector2.Zero ||
            TextureAddressMode != StarfieldMaterialTextureAddressMode.Wrap ||
            UvChannel != StarfieldMaterialUvChannel.One ||
            OpacitySourceLayer != 0 ||
            HasSecondaryOpacityLayers)
        {
            return false;
        }

        state = new StarfieldMaterialEffectAlphaState(MaterialOverallAlpha, OpacitySlot);
        return true;
    }
}
