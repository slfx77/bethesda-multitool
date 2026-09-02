namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     One scrolling normal-map layer of the FNV water surface. The engine composites three of
///     these (decompile of <c>WaterShader::SetupGeometryConstants</c> iterates a 3-entry noise
///     layer list), each sampling the same NNAM tile at its own UV scale, wind direction, speed,
///     and amplitude. <see cref="WindDirDegrees" /> is a compass-style angle; the renderer turns
///     it into a 2D scroll direction.
/// </summary>
public readonly record struct WaterNoiseLayer(
    float UvScale,
    float WindDirDegrees,
    float WindSpeed,
    float AmpScale,
    // FO4 DNAM's per-layer Noise Falloff. Classic WATR layouts do not carry it, so zero is
    // the lossless compatibility default and the modern prepass leaves it uninterpreted until
    // TESWaterNormals' exact falloff equation is recovered.
    float Falloff = 0f);

/// <summary>
///     FO76's distinct 148-byte WATR optical/normal model. Unlike FO4, it authors one float RGB
///     base color plus a separate float RGB channel-opacity vector; it does not author shallow and
///     deep byte-color endpoints. Keeping this typed prevents the compatibility projection used by
///     the current FO4-family shader from erasing that distinction.
/// </summary>
public readonly record struct Fallout76WaterVisualData(
    float DepthAmount,
    (float R, float G, float B) ChannelOpacity,
    (float R, float G, float B) BaseColor,
    float Unknown28,
    (byte R, byte G, byte B) UnderwaterColor,
    float UnderwaterFogAmount,
    float UnderwaterFogNear,
    float UnderwaterFogFar,
    float NormalMagnitude,
    float ShallowNormalFalloff,
    float DeepNormalFalloff,
    // Strong structural inferences from the shifted FO4 physical block. They stay explicitly
    // qualified until FO76 BSWaterShader::SetupMaterial is recovered.
    float ReflectivityCandidate,
    float FresnelCandidate,
    float SurfaceEffectFalloff,
    float DisplacementForce,
    float DisplacementVelocity,
    float DisplacementFalloff,
    WaterNoiseLayer Layer1,
    WaterNoiseLayer Layer2,
    WaterNoiseLayer Layer3,
    float Unknown144);

/// <summary>
///     The non-color WATR DNAM shading parameters that drive the water surface shader, parsed
///     from the same <c>DNAM/WATR</c> schema the converter uses (field names match
///     <c>SubrecordCellAndMiscSchemas</c>). These are the engine's real per-water values, so the
///     3D renderer reproduces Bethesda's 3-layer normal-map water with the actual scales/fresnel/
///     specular instead of hardcoded guesses. Absolute tile size + scroll-speed units live in the
///     (un-recovered) vertex shader, so the renderer keeps those as tunable constants and treats
///     these values as relative/structural.
/// </summary>
public sealed record WaterSurfaceParams(
    float NormalsUvScale,
    float FresnelAmount,
    float ReflectivityAmount,
    float Shininess,
    float SunPower,
    float DepthFalloffStart,
    float DepthFalloffEnd,
    WaterNoiseLayer Layer1,
    WaterNoiseLayer Layer2,
    WaterNoiseLayer Layer3,
    // DNAM fNoiseScale (@96). Per the MemDebug decompile (ISNOISENORMALMAP.pso + WaterShader RE) this is
    // the noise-map detail scale: the noise repeats ~NoiseScale times across each NormalsUvScale world-tile,
    // so the effective ripple wavelength ≈ NormalsUvScale / NoiseScale. Engine default 1.0; real water ~13.
    float NoiseScale = 1f,
    // WATR ANAM Opacity / 100. Oblivion feeds this into VarAmounts.z — the fresnel/alpha FLOOR of its
    // water shader (decompiled: FUN_00499570 filler, FUN_004ed660 getter = ANAM byte / 100, and the
    // console "set water opacity" handler writes the same global). Vanilla Oblivion: DefaultWater = 100,
    // dungeon/sewer/oil = 85. Only the Oblivion shader variant consumes it; 1.0 when ANAM is absent.
    float Opacity = 1f,
    // ---- FO4-only DNAM fields (WaterShaderVariant.Fo4Water; defaults keep every other game's
    // params byte-identical — see tools/GhidraProject/fo4_water_pixel_shader_decompiled.txt). ----
    // Sun Specular Magnitude @104 — the FO4 specular amplitude (pairs with SunPower = Sun Specular
    // Power @100 as the normalized-Blinn exponent).
    float SunSpecularMagnitude = 0f,
    // Silt Amount @188 — stands in for the engine's gloss-map input to the transmission/backscatter
    // sigmoid (labeled stand-in until BSWaterShader::SetupMaterial is decompiled).
    float SiltAmount = 0f,
    // Shallow/Deep Alpha @20/@24 + their column ranges @28/@32 (world units): the authored
    // alpha-by-water-depth ramp the engine bakes into its water LUT; the FO4 shader evaluates it
    // analytically from the sampled scene-depth column.
    float ShallowAlpha = 1f,
    float DeepAlpha = 1f,
    float AlphaShallowRange = 0f,
    float AlphaDeepRange = 0f,
    // Color Shallow/Deep Range @12/@16 — separately-authored LUT range controls. The exact generator
    // is open, so retain their units without folding them into DepthAmount.
    float ColorShallowRange = 0f,
    float ColorDeepRange = 0f,
    // Depth Amount @0 — authored water-column scale used to address the dynamic depth LUT.
    float DepthAmount = 0f,
    // ---- Oblivion WATR DATA fields. These are preserved separately from the FO3/FNV noise-layer
    // contract because TES4's WATER000 takes a direct XY Scroll constant and WATR fog distances.
    // FogNear/FogFar double as FO3/FNV DNAM@32/@36 "Above Water" fog planes — the constants the
    // engine feeds WATER003's alpha law whenever the camera is above the surface.
    float WaveAmplitude = 0f,
    float WaveFrequency = 1f,
    float ScrollXSpeed = 0f,
    float ScrollYSpeed = 0f,
    float FogNear = 0f,
    float FogFar = 0f,
    float TextureBlend = 0f,
    float WindVelocity = 0f,
    float WindDirection = 90f,
    float RainForce = 0f,
    float RainVelocity = 0f,
    float RainFalloff = 0f,
    float RainDampener = 0f,
    float RainStartingSize = 0f,
    float DisplacementForce = 0f,
    float DisplacementVelocity = 0f,
    float DisplacementFalloff = 0f,
    float DisplacementDampener = 0f,
    float DisplacementStartingSize = 0f,
    // ---- Classic/Creation fog + refraction inputs retained losslessly for the opt-in dynamic-water
    // pipeline. Classic FO3/FNV DNAM authors AboveWaterFogAmount @132, UnderWaterFog* @140..148,
    // and DistortionAmount @152. Creation records retain the underwater trio under the spelling
    // "UnderwaterFog*"; extraction accepts both schema spellings without conflating them. ----
    float AboveWaterFogAmount = 0f,
    float UnderwaterFogAmount = 0f,
    float UnderwaterFogNear = 0f,
    float UnderwaterFogFar = 0f,
    float RefractionDistortionAmount = 0f,
    // True only when every classic WATER001 input was explicitly present and finite in the source
    // dictionary. Retail fallback values below deliberately leave this false.
    bool HasAuthoredClassicRefractionInputs = false,
    // ---- Remaining Creation-era DNAM prefix retained losslessly for dynamic water. ----
    float NormalMagnitude = 1f,
    float ShallowNormalFalloff = 0f,
    float DeepNormalFalloff = 0f,
    float SurfaceEffectFalloff = 0f,
    float SunSparklePower = 0f,
    float SunSparkleMagnitude = 0f,
    float InteriorSpecularRadius = 0f,
    float InteriorSpecularBrightness = 0f,
    float InteriorSpecularPower = 0f,
    bool ScreenSpaceReflections = false,
    bool HasAuthoredNoiseLayers = false,
    // FO76's five post-specular floats do not yet have recovered semantics. Preserve them under
    // neutral names rather than discarding the bytes or assigning guessed meanings.
    float ModernUnknown1 = 0f,
    float ModernUnknown2 = 0f,
    float ModernUnknown3 = 0f,
    float ModernUnknown4 = 0f,
    float ModernUnknown5 = 0f)
{
    /// <summary>
    ///     Fallback when a record has no full 196-byte DNAM (proto/test water, or a worldspace whose
    ///     WATR didn't resolve). These are the engine's shipped <c>NVCleanWater</c> preset values (FormID
    ///     0x001009CA, the most common FNV water), read straight from <c>FalloutNV.esm</c>'s DNAM — so an
    ///     unresolved water body still renders like real Fallout water rather than a tuned guess. NormalsUvScale
    ///     is the DNAM fUVScale (= the VS TexScale world tile); the three layers carry the real WindDir(°),
    ///     WindSpeed, and fAmplitude blend weights; NoiseScale is the fNoiseScale detail multiplier.
    /// </summary>
    public static readonly WaterSurfaceParams Default = new(
        1000f, // DNAM fUVScale @136 = noise world tile (TexScale)
        0.025f, // DNAM fFresnelAmount @24 (engine default; clean water authors ~this)
        0.5f,
        500f, // DNAM fShininess @156 (sharp sun glint)
        826f, // DNAM fSunPower @16
        0f,
        0.01f, // DNAM DepthFalloffEnd @128 (NVCleanWater)
        FogNear: -80f, // DNAM Above Water FogNear @32 — the WATER003 alpha fog ramp
        FogFar: 850f, // DNAM Above Water FogFar @36 (ramp completes ~12 m down)
        // WaterNoiseLayer = (fHeightUVScale -> prepass fTexScale=max(1,ceil(x*.01)),
        // WindDirDeg, WindSpeed, AmpScale=fAmplitude).
        Layer1: new WaterNoiseLayer(0f, 180f, 0.065f, 0.300f),
        Layer2: new WaterNoiseLayer(0f, 10f, 0.033f, 0.525f),
        Layer3: new WaterNoiseLayer(0f, 67f, 0.029f, 0.138f),
        NoiseScale: 13.41f, // DNAM fNoiseScale @96
        AboveWaterFogAmount: 0.75f,
        UnderwaterFogAmount: 1f,
        UnderwaterFogNear: -2500f,
        UnderwaterFogFar: 5500f,
        RefractionDistortionAmount: 600f);
}

/// <summary>
///     Shared decode of a WATR record's visual appearance — the three DNAM colors, the NNAM
///     noise/normal-map path, and the <see cref="WaterSurfaceParams" /> shading parameters —
///     consumed by both the 2D map water overlay (<c>WaterColorPalette</c> delegates here) and the
///     3D <c>WaterRenderer12</c>.
///     <para>
///         The DNAM colors are packed uint32s where, per the decompile of
///         <c>TESWaterSystem::UpdateWaterShaderProperties</c>, the bytes map to RGB as
///         <c>R = packed &amp; 0xFF</c>, <c>G = (packed &gt;&gt; 8) &amp; 0xFF</c>,
///         <c>B = (packed &gt;&gt; 16) &amp; 0xFF</c> (the alpha byte is forced to 1.0 at upload).
///         The schema reader normalizes to canonical little-endian uints regardless of source
///         platform, so this mapping holds for Xbox and PC alike.
///     </para>
/// </summary>
public sealed record WaterAppearance(
    (byte R, byte G, byte B) Shallow,
    (byte R, byte G, byte B) Deep,
    (byte R, byte G, byte B) Reflection,
    string? NoiseTexture,
    WaterSurfaceParams Surface,
    bool CausesDamage = false,
    bool IsLava = false,
    // WATR FNAM bit 1. The default preserves the former reflective route for callers that build an
    // appearance from colors alone (or for partial records whose required FNAM is unavailable).
    bool IsReflective = true,
    // FO4 DNAM Silt "Dark Color" @196 — the FO4 water shader's unshadowed ambient-add term
    // (its PS adds this constant to the accumulated sun/point lighting before the body multiply).
    (byte R, byte G, byte B)? DarkSilt = null,
    // Authoritative ordered texture set. Skyrim carries three repeated NNAMs; keeping only the
    // compatibility NoiseTexture silently collapsed all three layers onto the first map.
    IReadOnlyList<string>? NormalTextures = null,
    // FO4 DNAM Silt Light Color and the Creation-prefix underwater color are retained separately.
    // SetupMaterial mappings are still open, so neither is silently substituted for DarkSilt.
    (byte R, byte G, byte B)? LightSilt = null,
    (byte R, byte G, byte B)? Underwater = null,
    // TES4 WATR TNAM. This is WATER000's per-water detail/surface input, not its global animated
    // water00..31 normal sequence, so it must never enter NormalTextures as a compatibility fallback.
    string? SurfaceTexture = null,
    // Exact FO76 optical model retained alongside the byte-color compatibility projection.
    Fallout76WaterVisualData? Fallout76VisualData = null,
    // Exact Starfield WATR data retained only when an appearance already has independently authored
    // compatibility colors. Starfield DNAM itself has no shallow/deep endpoints, so its presence
    // never manufactures colors or changes the current flat-fallback rendering policy.
    StarfieldWaterVisualData? StarfieldVisualData = null)
{
    // Fallback molten palette for lava records whose DATA carries no colors — bright orange crust grading
    // to dark red by depth (the shader's lava branch boosts + pulses these).
    private static readonly (byte R, byte G, byte B) DefaultLavaShallow = (255, 100, 30);
    private static readonly (byte R, byte G, byte B) DefaultLavaDeep = (140, 25, 10);

    /// <summary>
    ///     Builds appearance from a <see cref="WaterRecord" />. Returns null when the record is
    ///     missing or has no usable colors (caller falls back to a default tint). A missing
    ///     Shallow/Deep endpoint mirrors the other; a missing Reflection falls back to Shallow.
    ///     Also surfaces the WATR FNAM behavior flags and whether this is lava, so the renderer can
    ///     select the reflective WATER007 route and give lava an emissive, Fresnel-free look
    ///     (OBLIV-2) instead of drawing it as water.
    /// </summary>
    public static WaterAppearance? FromWaterRecord(WaterRecord? water)
    {
        if (water is null) return null;

        // FNAM flags (xEdit wbDefinitions: bit 0 = Causes Damage, bit 1 = Reflective). Damaging water
        // is lava OR oil in Oblivion (and radioactive water in Fallout), so "Causes Damage" alone does
        // not mean lava — distinguish lava by name (every shipped Oblivion lava WATR is editor-id'd
        // "…Lava…": OblivionCitadelLavaPlane / CamoranLava / OblivionLavaTest01; OblivionOil01 is not).
        var flags = water.WaterFlags is { Length: > 0 } waterFlags
            ? waterFlags[0]
            : (byte?)null;
        var causesDamage = (flags.GetValueOrDefault() & 0x01) != 0;
        // FNAM is required by the TES4 schema. Missing data means "unknown", not an authored OFF:
        // retain the pre-flag behavior for partial/proto records and color-only construction.
        var isReflective = flags is null || (flags.Value & 0x02) != 0;
        var isLava = LooksLikeLava(water);

        var textures = water.NormalTextures;
        if (textures.Count == 0)
        {
            textures = water.NoiseTexture is { Length: > 0 } noise
                ? new[] { noise }
                : Array.Empty<string>();
        }

        var firstTexture = textures.Count > 0 ? textures[0] : water.NoiseTexture;
        var appearance = FromVisualProperties(water.VisualProperties, firstTexture, textures);
        if (appearance is not null)
        {
            // ANAM is Required in shipped WATRs, so a raw 0 means the subrecord was absent
            // (proto/partial records) — keep the fully-floored default rather than a 0 floor.
            var opacity = water.Opacity > 0 ? water.Opacity / 100f : 1f;
            return appearance with
            {
                CausesDamage = causesDamage,
                IsLava = isLava,
                IsReflective = isReflective,
                Surface = appearance.Surface with { Opacity = opacity },
                SurfaceTexture = water.SurfaceTexture
            };
        }

        // No usable DATA/DNAM colors. The shipping Oblivion lava planes (OblivionCitadelLavaPlane,
        // CamoranLava) carry no vertex colors; TNAM now reaches the recovered DetailMap path, but it does
        // not replace the missing authored body-color constants. Without a fallback they would still use
        // default water colors (the OBLIV-2 bug). Hand lava a default molten palette + the lava flag so the
        // renderer gives it the emissive look. Ordinary color-less water returns null for caller fallback.
        return isLava
            ? new WaterAppearance(DefaultLavaShallow, DefaultLavaDeep, DefaultLavaShallow,
                firstTexture, WaterSurfaceParams.Default, causesDamage, true,
                IsReflective: isReflective, NormalTextures: textures,
                SurfaceTexture: water.SurfaceTexture)
            : null;
    }

    // Name-based lava detection — game-agnostic and false-positive-free vs Fallout's damaging
    // (radioactive) water, which carries no "lava" token. Checks the identifying strings the record
    // carries (editor id, full name, noise/texture path).
    private static bool LooksLikeLava(WaterRecord water)
    {
        return ContainsLava(water.EditorId) || ContainsLava(water.FullName) || ContainsLava(water.NoiseTexture);
    }

    private static bool ContainsLava(string? s)
    {
        return !string.IsNullOrEmpty(s) && s.Contains("lava", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Builds appearance from a WATR DNAM properties dictionary + the NNAM path. Returns null
    ///     when neither ShallowColor nor DeepColor is present (or both are zero), unless a typed
    ///     FO76 payload establishes that an authored black base color is genuinely present.
    ///     Starfield's typed optical payload does not author those endpoints and therefore does not
    ///     create an appearance by itself.
    /// </summary>
    public static WaterAppearance? FromVisualProperties(
        IReadOnlyDictionary<string, object?>? props, string? noiseTexture,
        IReadOnlyList<string>? normalTextures = null)
    {
        if (props is null) return null;

        var fallout76 = props.TryGetValue("Fallout76VisualData", out var fallout76Value) &&
                        fallout76Value is Fallout76WaterVisualData decoded
            ? decoded
            : (Fallout76WaterVisualData?)null;
        var starfield = props.TryGetValue("StarfieldVisualData", out var starfieldValue) &&
                        starfieldValue is StarfieldWaterVisualData starfieldDecoded
            ? starfieldDecoded
            : null;
        var shallow = ExtractColor(props, "ShallowColor");
        var deep = ExtractColor(props, "DeepColor");
        var reflection = ExtractColor(props, "ReflectionColor");
        if (fallout76 is { } f76)
        {
            // Packed zero is the legacy "missing color" sentinel, but black is valid in FO76's
            // typed float base color. Derive the compatibility endpoints from typed presence so a
            // valid black water never disappears merely because it quantizes to 0x000000.
            var baseColor = QuantizeNormalizedColor(f76.BaseColor);
            shallow ??= baseColor;
            deep ??= baseColor;
        }

        if (shallow is null && deep is null) return null;

        var s = shallow ?? deep!.Value;
        var d = deep ?? shallow!.Value;
        var r = reflection ?? s;
        return new WaterAppearance(s, d, r, noiseTexture, ExtractSurface(props, fallout76),
            DarkSilt: ExtractColor(props, "DarkSiltColor"), NormalTextures: normalTextures,
            LightSilt: ExtractColor(props, "LightSiltColor"),
            Underwater: ExtractColor(props, "UnderwaterColor"),
            Fallout76VisualData: fallout76,
            StarfieldVisualData: starfield);
    }

    /// <summary>
    ///     Reads the non-color shading scalars + the three noise layers from the DNAM dictionary,
    ///     falling back per-field to <see cref="WaterSurfaceParams.Default" /> when a key is absent
    ///     (so partial/proto DNAM still yields a sane surface).
    /// </summary>
    private static WaterSurfaceParams ExtractSurface(
        IReadOnlyDictionary<string, object?> props,
        Fallout76WaterVisualData? fallout76)
    {
        var def = WaterSurfaceParams.Default;
        // FO76's exact optical composite has three independent transmission channels and therefore
        // cannot be represented by fixed-function scalar alpha blending. Until the renderer samples
        // the scene color, use an explicitly heuristic arithmetic-mean coverage while preserving
        // the exact vector on WaterAppearance. fo76utils establishes the 0.5/0.9375 alpha endpoints,
        // but not this scalar aggregation; the latter is only a compatibility projection.
        var fallout76Opacity = fallout76 is { } f76
            ? (f76.ChannelOpacity.R + f76.ChannelOpacity.G + f76.ChannelOpacity.B) / 3f
            : (float?)null;
        var projectedShallowAlpha = fallout76Opacity is { } shallowOpacity
            ? 0.5f + (0.5f * shallowOpacity)
            : def.ShallowAlpha;
        var projectedDeepAlpha = fallout76Opacity is { } deepOpacity
            ? 0.9375f + (0.0625f * deepOpacity)
            : def.DeepAlpha;
        return new WaterSurfaceParams(
            ExtractFloat(props, "NormalsUVScale", def.NormalsUvScale),
            ExtractFloat(props, "FresnelAmount", def.FresnelAmount),
            ExtractFloat(props, "ReflectivityAmount", def.ReflectivityAmount),
            ExtractFloat(props, "Shininess", def.Shininess),
            ExtractFloat(props, "SunPower", def.SunPower),
            ExtractFloat(props, "DepthFalloffStart", def.DepthFalloffStart),
            ExtractFloat(props, "DepthFalloffEnd", def.DepthFalloffEnd),
            ExtractLayer(props, "NoiseLayer1", def.Layer1),
            ExtractLayer(props, "NoiseLayer2", def.Layer2),
            ExtractLayer(props, "NoiseLayer3", def.Layer3),
            ExtractFloat(props, "NoiseScale", def.NoiseScale),
            SunSpecularMagnitude: ExtractFloat(props, "SunSpecularMagnitude", def.SunSpecularMagnitude),
            SiltAmount: ExtractFloat(props, "SiltAmount", def.SiltAmount),
            ShallowAlpha: ExtractFloat(props, "ShallowAlpha", projectedShallowAlpha),
            DeepAlpha: ExtractFloat(props, "DeepAlpha", projectedDeepAlpha),
            AlphaShallowRange: ExtractFloat(props, "AlphaShallowRange", def.AlphaShallowRange),
            AlphaDeepRange: ExtractFloat(props, "AlphaDeepRange", fallout76 is not null ? 1f : def.AlphaDeepRange),
            ColorShallowRange: ExtractFloat(props, "ColorShallowRange", def.ColorShallowRange),
            ColorDeepRange: ExtractFloat(props, "ColorDeepRange", def.ColorDeepRange),
            DepthAmount: ExtractFloat(props, "DepthAmount", def.DepthAmount),
            WaveAmplitude: ExtractFloat(props, "WaveAmplitude", def.WaveAmplitude),
            WaveFrequency: ExtractFloat(props, "WaveFrequency", def.WaveFrequency),
            ScrollXSpeed: ExtractFloat(props, "ScrollXSpeed", def.ScrollXSpeed),
            ScrollYSpeed: ExtractFloat(props, "ScrollYSpeed", def.ScrollYSpeed),
            FogNear: ExtractFloat(props, "FogNear", def.FogNear),
            FogFar: ExtractFloat(props, "FogFar", def.FogFar),
            TextureBlend: ExtractFloat(props, "TextureBlend", def.TextureBlend),
            WindVelocity: ExtractFloat(props, "WindVelocity", def.WindVelocity),
            WindDirection: ExtractFloat(props, "WindDirection", def.WindDirection),
            RainForce: ExtractFloat(props, "RainForce", def.RainForce),
            RainVelocity: ExtractFloat(props, "RainVelocity", def.RainVelocity),
            RainFalloff: ExtractFloat(props, "RainFalloff", def.RainFalloff),
            RainDampener: ExtractFloat(props, "RainDampener", def.RainDampener),
            RainStartingSize: ExtractFloat(props, "RainStartingSize", def.RainStartingSize),
            DisplacementForce: ExtractFloat(props, "DisplacementForce", def.DisplacementForce),
            DisplacementVelocity: ExtractFloat(props, "DisplacementVelocity", def.DisplacementVelocity),
            DisplacementFalloff: ExtractFloat(props, "DisplacementFalloff", def.DisplacementFalloff),
            DisplacementDampener: ExtractFloat(props, "DisplacementDampener", def.DisplacementDampener),
            DisplacementStartingSize: ExtractFloat(props, "DisplacementStartingSize", def.DisplacementStartingSize),
            AboveWaterFogAmount: ExtractFloat(props, "AboveWaterFogAmount", def.AboveWaterFogAmount),
            UnderwaterFogAmount: ExtractAliasedFloat(
                props, "UnderwaterFogAmount", "UnderWaterFogAmount", def.UnderwaterFogAmount),
            UnderwaterFogNear: ExtractAliasedFloat(
                props, "UnderwaterFogNear", "UnderWaterFogNear", def.UnderwaterFogNear),
            UnderwaterFogFar: ExtractAliasedFloat(
                props, "UnderwaterFogFar", "UnderWaterFogFar", def.UnderwaterFogFar),
            RefractionDistortionAmount: ExtractFloat(
                props, "DistortionAmount", def.RefractionDistortionAmount),
            HasAuthoredClassicRefractionInputs: HasAuthoredClassicRefractionInputs(props),
            NormalMagnitude: ExtractFloat(props, "NormalMagnitude", def.NormalMagnitude),
            ShallowNormalFalloff: ExtractFloat(props, "ShallowNormalFalloff", def.ShallowNormalFalloff),
            DeepNormalFalloff: ExtractFloat(props, "DeepNormalFalloff", def.DeepNormalFalloff),
            SurfaceEffectFalloff: ExtractFloat(props, "SurfaceEffectFalloff", def.SurfaceEffectFalloff),
            SunSparklePower: ExtractFloat(props, "SunSparklePower", def.SunSparklePower),
            SunSparkleMagnitude: ExtractFloat(props, "SunSparkleMagnitude", def.SunSparkleMagnitude),
            InteriorSpecularRadius: ExtractFloat(props, "InteriorSpecularRadius", def.InteriorSpecularRadius),
            InteriorSpecularBrightness: ExtractFloat(props, "InteriorSpecularBrightness",
                def.InteriorSpecularBrightness),
            InteriorSpecularPower: ExtractFloat(props, "InteriorSpecularPower", def.InteriorSpecularPower),
            ScreenSpaceReflections: ExtractBool(props, "ScreenSpaceReflections", def.ScreenSpaceReflections),
            HasAuthoredNoiseLayers: HasAnyNoiseLayer(props),
            ModernUnknown1: ExtractFloat(props, "ModernUnknown1", def.ModernUnknown1),
            ModernUnknown2: ExtractFloat(props, "ModernUnknown2", def.ModernUnknown2),
            ModernUnknown3: ExtractFloat(props, "ModernUnknown3", def.ModernUnknown3),
            ModernUnknown4: ExtractFloat(props, "ModernUnknown4", def.ModernUnknown4),
            ModernUnknown5: ExtractFloat(props, "ModernUnknown5", def.ModernUnknown5));
    }

    private static WaterNoiseLayer ExtractLayer(
        IReadOnlyDictionary<string, object?> props, string prefix, WaterNoiseLayer fallback)
    {
        return new WaterNoiseLayer(
            ExtractFloat(props, prefix + "UVScale", fallback.UvScale),
            ExtractFloat(props, prefix + "WindDir", fallback.WindDirDegrees),
            ExtractFloat(props, prefix + "WindSpeed", fallback.WindSpeed),
            ExtractFloat(props, prefix + "AmpScale", fallback.AmpScale),
            ExtractFloat(props, prefix + "Falloff", fallback.Falloff));
    }

    private static bool HasAnyNoiseLayer(IReadOnlyDictionary<string, object?> props)
    {
        return props.ContainsKey("NoiseLayer1WindDir") || props.ContainsKey("NoiseLayer1WindSpeed") ||
               props.ContainsKey("NoiseLayer1AmpScale") || props.ContainsKey("NoiseLayer1UVScale");
    }

    private static bool ExtractBool(
        IReadOnlyDictionary<string, object?> props, string key, bool fallback)
    {
        if (!props.TryGetValue(key, out var value) || value is null) return fallback;
        return value switch
        {
            bool b => b,
            byte b => b != 0,
            int i => i != 0,
            uint u => u != 0,
            _ => fallback
        };
    }

    /// <summary>
    ///     Decodes a packed-uint32 DNAM color key. A packed value of 0 is treated as "missing"
    ///     (ambiguous with black) so a record with only one valid color falls back to that one
    ///     rather than lerping toward black.
    /// </summary>
    private static (byte R, byte G, byte B)? ExtractColor(
        IReadOnlyDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var value) || value is null) return null;
        var packed = value switch
        {
            uint u => u,
            int i => (uint)i,
            _ => 0u
        };
        if (packed == 0) return null;
        return ((byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)((packed >> 16) & 0xFF));
    }

    private static (byte R, byte G, byte B) QuantizeNormalizedColor(
        (float R, float G, float B) color)
    {
        static byte ToByte(float value) =>
            (byte)Math.Clamp((int)MathF.Round(value * byte.MaxValue), byte.MinValue, byte.MaxValue);

        return (ToByte(color.R), ToByte(color.G), ToByte(color.B));
    }

    /// <summary>
    ///     Reads a DNAM float scalar, accepting the boxed numeric forms the schema reader can
    ///     produce. Falls back to <paramref name="fallback" /> when absent or non-finite.
    /// </summary>
    private static float ExtractFloat(
        IReadOnlyDictionary<string, object?> props, string key, float fallback)
    {
        if (!props.TryGetValue(key, out var value) || value is null) return fallback;
        var f = value switch
        {
            float fl => fl,
            double d => (float)d,
            int i => i,
            uint u => u,
            _ => fallback
        };
        return float.IsFinite(f) ? f : fallback;
    }

    /// <summary>
    ///     Reads one of the two case-sensitive schema spellings used for underwater fog. The
    ///     Creation spelling is authoritative when both keys are present; a present-but-invalid
    ///     authoritative value falls back instead of silently borrowing the classic alias and
    ///     hiding malformed input.
    /// </summary>
    private static float ExtractAliasedFloat(
        IReadOnlyDictionary<string, object?> props,
        string creationKey,
        string classicKey,
        float fallback)
    {
        return props.ContainsKey(creationKey)
            ? ExtractFloat(props, creationKey, fallback)
            : ExtractFloat(props, classicKey, fallback);
    }

    private static bool HasAuthoredClassicRefractionInputs(
        IReadOnlyDictionary<string, object?> props)
    {
        return HasFiniteFloat(props, "FogNear") &&
               HasFiniteFloat(props, "FogFar") &&
               HasFiniteFloat(props, "DepthFalloffStart") &&
               HasFiniteFloat(props, "DepthFalloffEnd") &&
               HasFiniteFloat(props, "AboveWaterFogAmount") &&
               HasFiniteAliasedFloat(props, "UnderwaterFogAmount", "UnderWaterFogAmount") &&
               HasFiniteAliasedFloat(props, "UnderwaterFogNear", "UnderWaterFogNear") &&
               HasFiniteAliasedFloat(props, "UnderwaterFogFar", "UnderWaterFogFar") &&
               HasFiniteFloat(props, "DistortionAmount");
    }

    private static bool HasFiniteAliasedFloat(
        IReadOnlyDictionary<string, object?> props,
        string creationKey,
        string classicKey)
    {
        var hasCreation = props.ContainsKey(creationKey);
        var hasClassic = props.ContainsKey(classicKey);
        if (!hasCreation && !hasClassic) return false;

        // Duplicate spellings are legal compatibility input, but neither one may conceal malformed
        // data. Extraction remains Creation-authoritative; eligibility is deliberately stricter.
        return (!hasCreation || HasFiniteFloat(props, creationKey)) &&
               (!hasClassic || HasFiniteFloat(props, classicKey));
    }

    private static bool HasFiniteFloat(
        IReadOnlyDictionary<string, object?> props,
        string key)
    {
        if (!props.TryGetValue(key, out var value) || value is null) return false;
        var numeric = value switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            uint u => u,
            _ => float.NaN
        };
        return float.IsFinite(numeric);
    }
}
