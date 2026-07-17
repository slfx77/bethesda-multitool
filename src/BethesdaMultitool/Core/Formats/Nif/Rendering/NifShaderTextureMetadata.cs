namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Shader and texture-slot metadata resolved from a NIF shader property block.
/// </summary>
internal sealed class NifShaderTextureMetadata
{
    public string PropertyType { get; init; } = "";
    public uint? ShaderType { get; init; }
    public uint? ShaderFlags { get; init; }
    public uint? ShaderFlags2 { get; init; }
    public float? EnvMapScale { get; init; }

    /// <summary>
    ///     Raw FO3/FNV <c>BSShaderPPLightingProperty</c> parallax-occlusion tail. These values are
    ///     retained for inspection/POM work only; PC-final simple parallax does not consume them.
    /// </summary>
    public float? ParallaxMaxPasses { get; init; }
    public float? ParallaxScale { get; init; }

    /// <summary>Static shader-property UV transform authored before the texture reference.</summary>
    public System.Numerics.Vector2 UvOffset { get; init; }
    public System.Numerics.Vector2 UvScale { get; init; } = System.Numerics.Vector2.One;

    /// <summary>
    ///     Inline Skyrim <c>BSEffectShaderProperty</c> base color. The engine uploads
    ///     <c>BaseColor.rgb * BaseColorScale</c> to the effect pixel shader; kept separate here so
    ///     the decoded values remain inspectable and the extractor can form the exact tint.
    /// </summary>
    public (float R, float G, float B, float A)? EffectBaseColor { get; init; }

    /// <summary>Inline Skyrim effect RGB multiplier (the float following Base Color).</summary>
    public float? EffectBaseColorScale { get; init; }

    /// <summary>
    ///     Inline Skyrim effect lighting influence, normalized from the packed misc byte to 0..1.
    ///     Retail's selected unlit permutation computes
    ///     <c>rgb *= lerp(1, externalEmittanceColor, influence)</c>; the dormant Lit permutation
    ///     substitutes scene lighting for that color.
    /// </summary>
    public float? EffectLightingInfluence { get; init; }

    /// <summary>
    ///     Inline Skyrim effect view-angle falloff values. Callers apply these only when
    ///     SLSF1_Use_Falloff is set; retaining the raw quad mirrors the on-disk property.
    /// </summary>
    public (float StartAngle, float StopAngle, float StartOpacity, float StopOpacity)? EffectFalloff { get; init; }

    /// <summary>
    ///     Inline Skyrim-family <c>BSEffectShaderProperty.Soft Falloff Depth</c>. It is consumed only
    ///     when SLSF1_Soft_Effect (flags1 bit 30) is authored; null for legacy FO3/FNV shader
    ///     properties and layouts whose tail is unavailable.
    /// </summary>
    public float? SoftEffectFalloffDepth { get; init; }

    /// <summary>
    ///     FO3/FNV BSShaderNoLightingProperty view-angle falloff (Start/Stop angle cosines +
    ///     Start/Stop opacity — the 4 floats after the shader's FileName, BS version &gt; 26).
    ///     The engine ramps effect opacity by |N·V| through these; without them, crossed-plane and
    ///     shell effects (dust storms, light-ray cones, glow fills) render as solid silhouettes.
    ///     Null when the block predates the fields or isn't a no-lighting shader.
    /// </summary>
    public (float StartAngle, float StopAngle, float StartOpacity, float StopOpacity)? NoLightingFalloff { get; init; }
    public IReadOnlyList<string?> TextureSlots { get; init; } = [];

    /// <summary>
    ///     FO4/FO76 external material path (<c>materials\….bgsm</c>/<c>.bgem</c>) from the shader's Name,
    ///     populated even when the inline texture set supplied a diffuse — the engine gives the material's
    ///     render state (alpha test/blend, two-sided, specular) priority over the NIF's inline properties.
    /// </summary>
    public string? MaterialPath { get; init; }

    public string? DiffusePath => GetTextureSlot(0);
    public string? NormalMapPath => GetTextureSlot(1);
    public string? GlowMapPath => GetTextureSlot(2);
    public string? HeightMapPath => GetTextureSlot(3);
    public string? EnvironmentMapPath => GetTextureSlot(4);
    public string? EnvironmentMaskPath => GetTextureSlot(5);

    public bool HasRemappableTextures =>
        ShaderFlags.HasValue && (ShaderFlags.Value & (1u << 25)) != 0;

    public string? GetTextureSlot(int index)
    {
        return index >= 0 && index < TextureSlots.Count
            ? TextureSlots[index]
            : null;
    }
}
