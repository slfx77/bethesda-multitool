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
    float AmpScale);

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
    // Color Shallow/Deep Range @12/@16 — multipliers of DepthAmount for the Shallow→Deep color ramp.
    float ColorShallowRange = 0f,
    float ColorDeepRange = 0f,
    // Depth Amount @0 — the world-unit water column at which the FO4 depth ramps saturate (the
    // color/alpha ranges above multiply it; retail authors those ≈1.0).
    float DepthAmount = 0f)
{
    /// <summary>Fallback when a record has no full 196-byte DNAM (proto/test water, or a worldspace whose
    /// WATR didn't resolve). These are the engine's shipped <c>NVCleanWater</c> preset values (FormID
    /// 0x178882, the most common FNV water), read straight from <c>FalloutNV.esm</c>'s DNAM — so an
    /// unresolved water body still renders like real Fallout water rather than a tuned guess. NormalsUvScale
    /// is the DNAM fUVScale (= the VS TexScale world tile); the three layers carry the real WindDir(°),
    /// WindSpeed, and fAmplitude blend weights; NoiseScale is the fNoiseScale detail multiplier.</summary>
    public static readonly WaterSurfaceParams Default = new(
        NormalsUvScale: 1000f,   // DNAM fUVScale @136 = noise world tile (TexScale)
        FresnelAmount: 0.025f,   // DNAM fFresnelAmount @24 (engine default; clean water authors ~this)
        ReflectivityAmount: 0.5f,
        Shininess: 500f,         // DNAM fShininess @156 (sharp sun glint)
        SunPower: 826f,          // DNAM fSunPower @16
        DepthFalloffStart: 0f,
        DepthFalloffEnd: 0.01f,  // DNAM DepthFalloffEnd @128 (NVCleanWater)
        // WaterNoiseLayer = (UvScale[displacement, unused for noise], WindDirDeg, WindSpeed, AmpScale=fAmplitude).
        Layer1: new WaterNoiseLayer(0f, 180f, 0.065f, 0.300f),
        Layer2: new WaterNoiseLayer(0f, 10f, 0.033f, 0.525f),
        Layer3: new WaterNoiseLayer(0f, 67f, 0.029f, 0.138f),
        NoiseScale: 13.41f);     // DNAM fNoiseScale @96
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
    // FO4 DNAM Silt "Dark Color" @196 — the FO4 water shader's unshadowed ambient-add term
    // (its PS adds this constant to the accumulated sun/point lighting before the body multiply).
    (byte R, byte G, byte B)? DarkSilt = null)
{
    /// <summary>
    ///     Builds appearance from a <see cref="WaterRecord" />. Returns null when the record is
    ///     missing or has no usable colors (caller falls back to a default tint). A missing
    ///     Shallow/Deep endpoint mirrors the other; a missing Reflection falls back to Shallow.
    ///     Also surfaces the WATR FNAM "Causes Damage" flag and whether this is lava, so the renderer
    ///     can give lava an emissive, Fresnel-free look (OBLIV-2) instead of drawing it as water.
    /// </summary>
    public static WaterAppearance? FromWaterRecord(WaterRecord? water)
    {
        if (water is null) return null;

        // FNAM flags (xEdit wbDefinitions: bit 0 = Causes Damage, bit 1 = Reflective). Damaging water
        // is lava OR oil in Oblivion (and radioactive water in Fallout), so "Causes Damage" alone does
        // not mean lava — distinguish lava by name (every shipped Oblivion lava WATR is editor-id'd
        // "…Lava…": OblivionCitadelLavaPlane / CamoranLava / OblivionLavaTest01; OblivionOil01 is not).
        var causesDamage = water.WaterFlags is { Length: > 0 } f && (f[0] & 0x01) != 0;
        var isLava = LooksLikeLava(water);

        var appearance = FromVisualProperties(water.VisualProperties, water.NoiseTexture);
        if (appearance is not null)
        {
            // ANAM is Required in shipped WATRs, so a raw 0 means the subrecord was absent
            // (proto/partial records) — keep the fully-floored default rather than a 0 floor.
            var opacity = water.Opacity > 0 ? water.Opacity / 100f : 1f;
            return appearance with
            {
                CausesDamage = causesDamage,
                IsLava = isLava,
                Surface = appearance.Surface with { Opacity = opacity },
            };
        }

        // No usable DATA/DNAM colors. The shipping Oblivion lava planes (OblivionCitadelLavaPlane,
        // CamoranLava) carry no vertex colors — the engine colors them from the TNAM texture, which the
        // viewer doesn't sample for water — so without a fallback they would render as default water (the
        // OBLIV-2 bug). Hand lava a default molten palette + the lava flag so the renderer still gives it
        // the emissive look. Ordinary color-less water returns null so the caller keeps its own tint.
        return isLava
            ? new WaterAppearance(DefaultLavaShallow, DefaultLavaDeep, DefaultLavaShallow,
                water.NoiseTexture, WaterSurfaceParams.Default, causesDamage, IsLava: true)
            : null;
    }

    // Fallback molten palette for lava records whose DATA carries no colors — bright orange crust grading
    // to dark red by depth (the shader's lava branch boosts + pulses these).
    private static readonly (byte R, byte G, byte B) DefaultLavaShallow = (255, 100, 30);
    private static readonly (byte R, byte G, byte B) DefaultLavaDeep = (140, 25, 10);

    // Name-based lava detection — game-agnostic and false-positive-free vs Fallout's damaging
    // (radioactive) water, which carries no "lava" token. Checks the identifying strings the record
    // carries (editor id, full name, noise/texture path).
    private static bool LooksLikeLava(WaterRecord water) =>
        ContainsLava(water.EditorId) || ContainsLava(water.FullName) || ContainsLava(water.NoiseTexture);

    private static bool ContainsLava(string? s) =>
        !string.IsNullOrEmpty(s) && s.Contains("lava", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Builds appearance from a WATR DNAM properties dictionary + the NNAM path. Returns null
    ///     when neither ShallowColor nor DeepColor is present (or both are zero).
    /// </summary>
    public static WaterAppearance? FromVisualProperties(
        IReadOnlyDictionary<string, object?>? props, string? noiseTexture)
    {
        if (props is null) return null;

        var shallow = ExtractColor(props, "ShallowColor");
        var deep = ExtractColor(props, "DeepColor");
        var reflection = ExtractColor(props, "ReflectionColor");
        if (shallow is null && deep is null) return null;

        var s = shallow ?? deep!.Value;
        var d = deep ?? shallow!.Value;
        var r = reflection ?? s;
        return new WaterAppearance(s, d, r, noiseTexture, ExtractSurface(props),
            DarkSilt: ExtractColor(props, "DarkSiltColor"));
    }

    /// <summary>
    ///     Reads the non-color shading scalars + the three noise layers from the DNAM dictionary,
    ///     falling back per-field to <see cref="WaterSurfaceParams.Default" /> when a key is absent
    ///     (so partial/proto DNAM still yields a sane surface).
    /// </summary>
    private static WaterSurfaceParams ExtractSurface(IReadOnlyDictionary<string, object?> props)
    {
        var def = WaterSurfaceParams.Default;
        return new WaterSurfaceParams(
            NormalsUvScale: ExtractFloat(props, "NormalsUVScale", def.NormalsUvScale),
            FresnelAmount: ExtractFloat(props, "FresnelAmount", def.FresnelAmount),
            ReflectivityAmount: ExtractFloat(props, "ReflectivityAmount", def.ReflectivityAmount),
            Shininess: ExtractFloat(props, "Shininess", def.Shininess),
            SunPower: ExtractFloat(props, "SunPower", def.SunPower),
            DepthFalloffStart: ExtractFloat(props, "DepthFalloffStart", def.DepthFalloffStart),
            DepthFalloffEnd: ExtractFloat(props, "DepthFalloffEnd", def.DepthFalloffEnd),
            Layer1: ExtractLayer(props, "NoiseLayer1", def.Layer1),
            Layer2: ExtractLayer(props, "NoiseLayer2", def.Layer2),
            Layer3: ExtractLayer(props, "NoiseLayer3", def.Layer3),
            NoiseScale: ExtractFloat(props, "NoiseScale", def.NoiseScale),
            SunSpecularMagnitude: ExtractFloat(props, "SunSpecularMagnitude", def.SunSpecularMagnitude),
            SiltAmount: ExtractFloat(props, "SiltAmount", def.SiltAmount),
            ShallowAlpha: ExtractFloat(props, "ShallowAlpha", def.ShallowAlpha),
            DeepAlpha: ExtractFloat(props, "DeepAlpha", def.DeepAlpha),
            AlphaShallowRange: ExtractFloat(props, "AlphaShallowRange", def.AlphaShallowRange),
            AlphaDeepRange: ExtractFloat(props, "AlphaDeepRange", def.AlphaDeepRange),
            ColorShallowRange: ExtractFloat(props, "ColorShallowRange", def.ColorShallowRange),
            ColorDeepRange: ExtractFloat(props, "ColorDeepRange", def.ColorDeepRange),
            DepthAmount: ExtractFloat(props, "DepthAmount", def.DepthAmount));
    }

    private static WaterNoiseLayer ExtractLayer(
        IReadOnlyDictionary<string, object?> props, string prefix, WaterNoiseLayer fallback)
        => new(
            UvScale: ExtractFloat(props, prefix + "UVScale", fallback.UvScale),
            WindDirDegrees: ExtractFloat(props, prefix + "WindDir", fallback.WindDirDegrees),
            WindSpeed: ExtractFloat(props, prefix + "WindSpeed", fallback.WindSpeed),
            AmpScale: ExtractFloat(props, prefix + "AmpScale", fallback.AmpScale));

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

    /// <summary>Reads a DNAM float scalar, accepting the boxed numeric forms the schema reader can
    /// produce. Falls back to <paramref name="fallback" /> when absent or non-finite.</summary>
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
}
