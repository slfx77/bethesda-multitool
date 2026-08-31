namespace BethesdaMultitool.Core.Formats.Nif.Materials;

/// <summary>CE2 <c>BSMaterial::AlphaBlenderSettings.Mode</c> values.</summary>
internal enum StarfieldMaterialAlphaBlenderMode : byte
{
    Linear = 0,
    Additive = 1,
    PositionContrast = 2,
    None = 3
}

/// <summary>Persistent renderer operation selected from CE2's broader AlphaSettings component.</summary>
internal enum StarfieldMaterialAlphaRenderMode : byte
{
    None = 0,
    Layer0OpacityCutout = 1
}

/// <summary>
///     First-class CE2 coverage state carried independently of material-colour Lerp. The threshold
///     is the authored linear comparison value; it is not material opacity and never enables blend.
/// </summary>
internal readonly record struct StarfieldMaterialAlphaRenderState(
    StarfieldMaterialAlphaRenderMode Mode,
    float AlphaTestThreshold)
{
    internal bool IsLayer0OpacityCutout => Mode == StarfieldMaterialAlphaRenderMode.Layer0OpacityCutout;
}

/// <summary>
///     Effective root-material <c>BSMaterial::AlphaSettingsComponent</c>. Every member is resolved
///     independently through the CE2 object inheritance chain because DIFF components may override
///     only one nested field.
/// </summary>
internal readonly record struct StarfieldMaterialAlphaPolicy(
    bool IsResolved,
    bool HasOpacity,
    float AlphaTestThreshold,
    int OpacitySourceLayer,
    StarfieldMaterialAlphaBlenderMode BlenderMode,
    bool UsesDetailBlendMask,
    bool UsesVertexColor,
    StarfieldMaterialColorChannel VertexColorChannel,
    uint OpacityUvStreamId,
    bool OpacityUvUsesIdentityUv0,
    bool HasMalformedSettings,
    float HeightBlendThreshold,
    float HeightBlendFactor,
    float Position,
    float Contrast,
    bool UsesDitheredTransparency,
    bool OpacityLayerUsesFlipbook,
    StarfieldMaterialSlot OpacitySlot)
{
    /// <summary>
    ///     Selects the exact static BSGeometry subset represented by the current renderer: layer-0
    ///     slot-2 red coverage, identity wrapped UV0 sampling (whether the UVStream link is null or
    ///     points at a default-equivalent object), no secondary mask/vertex/dither inputs, and a
    ///     GREATER cutout. Unsupported AlphaSettings fail closed as opaque; notably this never
    ///     selects alpha blending, and tint-Lerp alpha is not consulted.
    /// </summary>
    internal bool TryResolveStaticCutout(out StarfieldMaterialAlphaRenderState state)
    {
        state = default;
        if (!IsResolved ||
            !HasOpacity ||
            !float.IsFinite(AlphaTestThreshold) ||
            AlphaTestThreshold <= 0f ||
            AlphaTestThreshold >= 1f ||
            OpacitySourceLayer != 0 ||
            BlenderMode != StarfieldMaterialAlphaBlenderMode.Linear ||
            UsesDetailBlendMask ||
            UsesVertexColor ||
            !OpacityUvUsesIdentityUv0 ||
            HasMalformedSettings ||
            UsesDitheredTransparency ||
            OpacityLayerUsesFlipbook ||
            !OpacitySlot.IsResolved)
        {
            return false;
        }

        state = new StarfieldMaterialAlphaRenderState(
            StarfieldMaterialAlphaRenderMode.Layer0OpacityCutout,
            AlphaTestThreshold);
        return true;
    }

    internal StarfieldMaterialAlphaRenderState ResolveRenderState()
    {
        return TryResolveStaticCutout(out var state) ? state : default;
    }
}

/// <summary>Retail-database census for bounded AlphaSettings admission.</summary>
internal readonly record struct StarfieldMaterialAlphaCensus(
    int ComponentObjectCount,
    int ResourceMaterialCount,
    int ResourceMaterialsWithOpacity,
    int SupportedStaticCutouts,
    int MissingOpacitySlot,
    int NonLayer0,
    int UnsupportedUv,
    int MalformedSettings,
    int VertexOrDetailMask,
    int Dithered,
    int FlipbookOpacityLayer,
    int NonLinearMode,
    int NonCuttingThreshold);
