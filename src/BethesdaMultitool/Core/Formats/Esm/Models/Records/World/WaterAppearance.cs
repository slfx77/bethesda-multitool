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
    WaterNoiseLayer Layer3)
{
    /// <summary>Fallback used when a record has no full 196-byte DNAM (proto/test water). Three
    /// gently-divergent layers so the procedural-free path still animates plausibly.</summary>
    public static readonly WaterSurfaceParams Default = new(
        NormalsUvScale: 1f,
        FresnelAmount: 0.5f,
        ReflectivityAmount: 1f,
        Shininess: 80f,
        SunPower: 1f,
        DepthFalloffStart: 0f,
        DepthFalloffEnd: 4096f,
        Layer1: new WaterNoiseLayer(1.0f, 0f, 1.0f, 1.0f),
        Layer2: new WaterNoiseLayer(1.7f, 120f, 0.8f, 0.6f),
        Layer3: new WaterNoiseLayer(2.3f, 240f, 0.6f, 0.4f));
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
    WaterSurfaceParams Surface)
{
    /// <summary>
    ///     Builds appearance from a <see cref="WaterRecord" />. Returns null when the record is
    ///     missing or has no usable DNAM colors (caller falls back to a default tint). A missing
    ///     Shallow/Deep endpoint mirrors the other; a missing Reflection falls back to Shallow.
    /// </summary>
    public static WaterAppearance? FromWaterRecord(WaterRecord? water)
    {
        if (water is null) return null;
        return FromVisualProperties(water.VisualProperties, water.NoiseTexture);
    }

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
        return new WaterAppearance(s, d, r, noiseTexture, ExtractSurface(props));
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
            Layer3: ExtractLayer(props, "NoiseLayer3", def.Layer3));
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
