namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Weather (WTHR) record.
///     Defines weather conditions including sky colors, fog, sounds, and image space modifiers.
/// </summary>
public record WeatherRecord
{
    /// <summary>FormID of the weather record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Image space modifier FormID (for daylight).</summary>
    public uint? ImageSpaceModifier { get; init; }

    /// <summary>
    ///     Authored time-of-day image-space references. FO3/FNV store these as the four/six
    ///     <c>\0IAD</c>..<c>\5IAD</c> subrecords; Skyrim+ stores four or eight FormIDs in IMSP.
    ///     <see cref="ImageSpaceModifier"/> remains as the Day-band compatibility projection.
    /// </summary>
    public WeatherTimeBands<uint>? ImageSpaceModifiers { get; init; }

    /// <summary>Weather-related sounds (SNAM entries: FormID + type).</summary>
    public List<WeatherSound> Sounds { get; init; } = [];

    /// <summary>
    ///     NAM0 "Weather Colors": one <see cref="WeatherColor" /> per color category. FNV ships 10
    ///     categories (240 bytes = 10 × 24-byte categories; each category is SIX RGBA time bands —
    ///     Sunrise/Day/Sunset/Night/HighNoon/Midnight — per the fopdoc FalloutNV WTHR definition, NOT the
    ///     four FO3 carries). Index meaning is <see cref="WeatherColorType" />. Empty when no NAM0.
    /// </summary>
    public IReadOnlyList<WeatherColor> Colors { get; init; } = [];

    /// <summary>
    ///     FNAM "Fog Distances" — at least Day Near, Day Far, Night Near, Night Far. The next pair is
    ///     Day/Night Power; Skyrim's final pair is Day/Night Max Opacity. <c>Sky::UpdateFog</c> resolves
    ///     all authored day/night pairs and its lighting shader clamps the powered fog amount by the max.
    ///     Empty when absent.
    /// </summary>
    public IReadOnlyList<float> FogDistances { get; init; } = [];

    /// <summary>
    ///     Cloud-layer texture paths in ascending SOURCE-layer order. Oblivion uses CNAM lower = 0 and
    ///     DNAM upper = 1; FO3/FNV use DNAM = 0, CNAM = 1, ANAM = 2, BNAM = 3; Skyrim+ uses sparse
    ///     <c>?0TX</c> indices. The engine swaps these onto the
    ///     sky-dome cloud nodes for the active weather, so they are the grounded source of a worldspace's
    ///     clouds (vs. a hardcoded per-game cloud path). An unused layer is authored as the transparent
    ///     placeholder <c>sky\alpha.dds</c>. Empty when the record carries no cloud subrecords.
    /// </summary>
    public IReadOnlyList<string> CloudLayerTextures { get; init; } = [];

    /// <summary>
    ///     Original WTHR source index for each entry in <see cref="CloudLayerTextures" />. Skyrim weather
    ///     texture signatures are sparse (for example <c>?0TX</c>/<c>C0TX</c>/<c>D0TX</c> are source
    ///     layers 15/19/20), while QNAM/RNAM/PNAM/JNAM remain arrays indexed by those original numbers.
    ///     Dropping the gaps therefore assigns the wrong speed/tint/opacity to every later texture.
    ///     Legacy and synthetic weather records may leave this empty; consumers then use the dense
    ///     ordinal for backward compatibility.
    /// </summary>
    public IReadOnlyList<int> CloudLayerSourceIndices { get; init; } = [];

    /// <summary>
    ///     Authoritative cloud-layer view keyed by authored source index. Unlike the legacy parallel
    ///     arrays, a layer keeps its texture, both motion axes, color bands, and opacity bands together.
    ///     Texture may be null for an authored speed/color slot that has no texture in this weather.
    /// </summary>
    public IReadOnlyList<WeatherCloudLayer> CloudLayers { get; init; } = [];

    /// <summary>Maps a dense rendered cloud ordinal back to its authored WTHR array index.</summary>
    public int GetCloudLayerSourceIndex(int ordinal) =>
        ordinal >= 0 && ordinal < CloudLayerSourceIndices.Count ? CloudLayerSourceIndices[ordinal] : ordinal;

    /// <summary>
    ///     Resolves the authored cloud slot attached to a clouds-NIF source shape. Modern WTHR texture
    ///     signatures are sparse, so source shape 15 must receive layer 15 rather than the first dense
    ///     texture entry. The synthesized projection keeps legacy tests/callers that populate only the
    ///     parallel arrays working while <see cref="CloudLayers" /> becomes authoritative.
    /// </summary>
    public WeatherCloudLayer? FindCloudLayerBySourceIndex(int sourceIndex)
    {
        if (sourceIndex < 0) return null;

        if (CloudLayers.Count > 0)
        {
            return CloudLayers.FirstOrDefault(layer => layer.SourceIndex == sourceIndex);
        }

        var denseTextureIndex = sourceIndex;
        if (CloudLayerSourceIndices.Count > 0)
        {
            denseTextureIndex = -1;
            for (var i = 0; i < CloudLayerSourceIndices.Count; i++)
            {
                if (CloudLayerSourceIndices[i] == sourceIndex)
                {
                    denseTextureIndex = i;
                    break;
                }
            }
        }

        if (denseTextureIndex < 0 || denseTextureIndex >= CloudLayerTextures.Count)
        {
            return null;
        }

        return new WeatherCloudLayer
        {
            SourceIndex = sourceIndex,
            Texture = CloudLayerTextures[denseTextureIndex],
            SpeedU = sourceIndex < CloudSpeedsX.Count ? CloudSpeedsX[sourceIndex] : 0f,
            SpeedV = sourceIndex < CloudSpeedsY.Count ? CloudSpeedsY[sourceIndex] : 0f,
            Color = sourceIndex < CloudColors.Count ? CloudColors[sourceIndex] : null,
            Opacity = sourceIndex < CloudLayerAlphas.Count ? CloudLayerAlphas[sourceIndex] : null,
        };
    }

    /// <summary>
    ///     PNAM "Cloud Colors" (xEdit wbWeatherCloudColors): one <see cref="WeatherColor" /> PER CLOUD
    ///     LAYER (same 6-band Time-of-Day RGBA struct as <see cref="Colors" />). The engine uploads this
    ///     per layer as the cloud shader's per-draw color uniform (RGB tint + A opacity) — verified in
    ///     <c>SkyShader::SetupGeometryConstants</c>. Indexed by authored source layer; use
    ///     <see cref="GetCloudLayerSourceIndex" /> while walking the dense texture list.
    ///     Empty when the record carries no PNAM.
    /// </summary>
    public IReadOnlyList<WeatherColor> CloudColors { get; init; } = [];

    /// <summary>
    ///     JNAM "Cloud Alphas" (Skyrim/FO4/FO76/SF1; xEdit wbWeatherCloudAlphas): one <see cref="WeatherCloudAlpha" />
    ///     PER CLOUD LAYER — the per-layer, per-time-of-day OPACITY (float, default 1.0) the engine applies to
    ///     each cloud sheet. This is the layer-opacity channel the modern weather authors SEPARATELY from the
    ///     PNAM cloud color (whose alpha byte is unused, hence the 0s). A weather hides a layer by authoring 0
    ///     and thins others with fractional values — so a CLEAR weather (e.g. CommonwealthClear: layers at
    ///     0.0/0.2/0.4/0.75/…) shows mostly sky, while a CLOUDY one (SkyrimCloudy: all 1.0) fully overcasts.
    ///     Indexed by authored source layer (which can be sparse relative to <see cref="CloudLayerTextures" />).
    ///     Empty for FO3/FNV (which carry no JNAM —
    ///     they use a single cloud sheet, so the renderer's flat opacity is correct there).
    /// </summary>
    public IReadOnlyList<WeatherCloudAlpha> CloudLayerAlphas { get; init; } = [];

    /// <summary>
    ///     ONAM/QNAM cloud speeds: per-layer scroll rate the engine accumulates in
    ///     <c>Clouds::Update</c>. FO3/FNV scalar ONAM values are unsigned fractions; Skyrim+ QNAM axes
    ///     are normalized around the byte midpoint (127 = still). The handler normalizes the authored
    ///     bytes so the renderer never re-interprets their storage. Empty when absent.
    /// </summary>
    public IReadOnlyList<float> CloudSpeedsX { get; init; } = [];

    /// <summary>RNAM "Y Cloud Speeds": per-layer V-axis scroll rate, normalized like <see cref="CloudSpeedsX" />.</summary>
    public IReadOnlyList<float> CloudSpeedsY { get; init; } = [];

    /// <summary>
    ///     DALC "Directional Ambient Lighting Colors" reduced to the per-time-band MEAN of the
    ///     six-direction ambient cube (X±/Y±/Z±). Skyrim+ engines light exteriors from this cube,
    ///     NOT the NAM0 Ambient row — FO4 authors that row near-black (CommonwealthClear Night =
    ///     (2,2,2) vs its DALC night mean ≈ (18,26,34)), so sourcing ambient from NAM0 rendered
    ///     FO4 nights pitch black. This mean remains a compatibility projection for consumers that
    ///     cannot shade by direction; the 3D GPU path uses <see cref="DirectionalAmbientCubes"/>.
    ///     One DALC subrecord per time band (Skyrim 4; FO4/FO76 8 at form
    ///     version 111+ — the 4 base bands + interpolation aids, same order as NAM0). Null when
    ///     the record carries no DALC (FO3/FNV always). Directional (full-cube) shading is a
    ///     full-cube shading is retained separately below.
    /// </summary>
    public WeatherColor? DirectionalAmbient { get; init; }

    /// <summary>
    ///     Lossless DALC ambient cubes by authored time band. The 3D renderer samples these without
    ///     averaging; <see cref="DirectionalAmbient"/> is retained as a compatibility mean.
    /// </summary>
    public WeatherTimeBands<WeatherAmbientCube>? DirectionalAmbientCubes { get; init; }

    /// <summary>Oblivion WTHR HNAM HDR parameters (14 endian-aware floats).</summary>
    public WeatherHdr? Hdr { get; init; }

    /// <summary>
    ///     Skyrim WTHR NAM2 authored Sun Glare RGB row. Later games may instead carry the same
    ///     semantic channel in their widened NAM0 color table; consumers prefer this explicit row
    ///     when present and retain the color-table form as a compatibility fallback.
    /// </summary>
    public WeatherColor? SunGlareColor { get; init; }

    /// <summary>
    ///     Skyrim WTHR NAM3 authored Moon Glare RGB row. See <see cref="SunGlareColor"/> for the
    ///     modern color-table compatibility path.
    /// </summary>
    public WeatherColor? MoonGlareColor { get; init; }

    /// <summary>DATA block (wind speed, sun glare, precipitation timing, flags, lightning color).</summary>
    public WeatherData? Data { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>An 8-bit RGBA color as stored in a WTHR NAM0 entry.</summary>
public readonly record struct WeatherRgba(byte R, byte G, byte B, byte A);

/// <summary>
///     Semantic weather time bands shared by colors, opacity, ambient cubes, and image-space references.
///     The base four bands always exist. Fallout New Vegas may add HighNoon/Midnight; Creation form
///     version 111+ may instead add the four explicit early/late transition bands.
/// </summary>
public sealed record WeatherTimeBands<T>(T Sunrise, T Day, T Sunset, T Night) where T : struct
{
    public T? HighNoon { get; init; }
    public T? Midnight { get; init; }
    public T? EarlySunrise { get; init; }
    public T? LateSunrise { get; init; }
    public T? EarlySunset { get; init; }
    public T? LateSunset { get; init; }

    public bool HasModernTransitions =>
        EarlySunrise.HasValue || LateSunrise.HasValue || EarlySunset.HasValue || LateSunset.HasValue;
}

/// <summary>One authored WTHR cloud slot with all parallel-array data joined by source index.</summary>
public sealed record WeatherCloudLayer
{
    public int SourceIndex { get; init; }
    public string? Texture { get; init; }
    public float SpeedU { get; init; }
    public float SpeedV { get; init; }
    public WeatherColor? Color { get; init; }
    public WeatherCloudAlpha? Opacity { get; init; }
}

/// <summary>
///     One cloud layer's JNAM "Cloud Alphas" opacity sampled at the four base times of day (Sunrise / Day /
///     Sunset / Night), each a float in [0, 1]. FO4/FO76/SF1 form version 111+ author four additional
///     Early/Late Sunrise/Sunset transition bands; all eight are retained semantically. Default 1.0
///     (fully opaque) per xEdit's wbWeatherCloudAlphas.
/// </summary>
public sealed record WeatherCloudAlpha
{
    public WeatherCloudAlpha(float sunrise, float day, float sunset, float night)
        : this(new WeatherTimeBands<float>(sunrise, day, sunset, night))
    {
    }

    public WeatherCloudAlpha(WeatherTimeBands<float> bands) => Bands = bands;

    public WeatherTimeBands<float> Bands { get; }
    public float Sunrise => Bands.Sunrise;
    public float Day => Bands.Day;
    public float Sunset => Bands.Sunset;
    public float Night => Bands.Night;
    public float? EarlySunrise => Bands.EarlySunrise;
    public float? LateSunrise => Bands.LateSunrise;
    public float? EarlySunset => Bands.EarlySunset;
    public float? LateSunset => Bands.LateSunset;
}

/// <summary>
///     One NAM0 color category sampled at the times of day. Band order is Sunrise / Day / Sunset /
///     Night / HighNoon / Midnight for FNV, or the four base bands followed by Early/Late
///     Sunrise/Sunset for modern form version 111+ records. HighNoon is a genuine FNV daytime color;
///     Midnight is retained losslessly but is not a color interpolation band.
/// </summary>
public sealed record WeatherColor
{
    public WeatherColor(WeatherRgba sunrise, WeatherRgba day, WeatherRgba sunset, WeatherRgba night)
        : this(new WeatherTimeBands<WeatherRgba>(sunrise, day, sunset, night))
    {
    }

    public WeatherColor(WeatherRgba sunrise, WeatherRgba day, WeatherRgba sunset, WeatherRgba night,
        WeatherRgba highNoon, WeatherRgba midnight)
        : this(new WeatherTimeBands<WeatherRgba>(sunrise, day, sunset, night)
        {
            HighNoon = highNoon,
            Midnight = midnight,
        })
    {
    }

    public WeatherColor(WeatherTimeBands<WeatherRgba> bands) => Bands = bands;

    public WeatherTimeBands<WeatherRgba> Bands { get; }
    public WeatherRgba Sunrise => Bands.Sunrise;
    public WeatherRgba Day => Bands.Day;
    public WeatherRgba Sunset => Bands.Sunset;
    public WeatherRgba Night => Bands.Night;

    // Compatibility projections retain the old four-band fallback behavior while Bands preserves
    // whether the optional values were genuinely authored.
    public WeatherRgba HighNoon => Bands.HighNoon ?? Day;
    public WeatherRgba Midnight => Bands.Midnight ?? Night;
    public WeatherRgba? EarlySunrise => Bands.EarlySunrise;
    public WeatherRgba? LateSunrise => Bands.LateSunrise;
    public WeatherRgba? EarlySunset => Bands.EarlySunset;
    public WeatherRgba? LateSunset => Bands.LateSunset;
}

/// <summary>
///     Index meaning of the <see cref="WeatherRecord.Colors" /> array (FNV WTHR NAM0). The FNV NAM0 holds
///     exactly TEN categories (per the fopdoc FalloutNV WTHR definition), in this order. <see cref="Ambient" />
///     (3) and <see cref="Sunlight" /> (4) are also CONFIRMED by the engine decompile —
///     <c>Sky::UpdateColors</c> special-cases those two indices for the directional-light intensity scale.
/// </summary>
public enum WeatherColorType
{
    SkyUpper = 0,
    Fog = 1,
    Unused2 = 2,
    Ambient = 3,
    Sunlight = 4,
    Sun = 5,
    Stars = 6,
    SkyLower = 7,
    Horizon = 8,
    Unused9 = 9,
    /// <summary>Skyrim+ NAM0 far-fog color; Sky::GetFogColorFar resolves category 12.</summary>
    FogFar = 12,
    /// <summary>FO4-family widened NAM0 sun-glare color row; Skyrim uses explicit NAM2 instead.</summary>
    SunGlare = 15,
    /// <summary>FO4-family widened NAM0 moon-glare color row; Skyrim uses explicit NAM3 instead.</summary>
    MoonGlare = 16,
}

/// <summary>One lossless DALC directional ambient cube.</summary>
public readonly record struct WeatherAmbientCube
{
    public WeatherRgba PositiveX { get; init; }
    public WeatherRgba NegativeX { get; init; }
    public WeatherRgba PositiveY { get; init; }
    public WeatherRgba NegativeY { get; init; }
    public WeatherRgba PositiveZ { get; init; }
    public WeatherRgba NegativeZ { get; init; }
    public WeatherRgba? Specular { get; init; }
    public float? FresnelPower { get; init; }

    public WeatherRgba Mean => new(
        (byte)((PositiveX.R + NegativeX.R + PositiveY.R + NegativeY.R + PositiveZ.R + NegativeZ.R) / 6),
        (byte)((PositiveX.G + NegativeX.G + PositiveY.G + NegativeY.G + PositiveZ.G + NegativeZ.G) / 6),
        (byte)((PositiveX.B + NegativeX.B + PositiveY.B + NegativeY.B + PositiveZ.B + NegativeZ.B) / 6),
        255);
}

/// <summary>Oblivion WTHR HNAM HDR data (14 floats, disk/runtime order).</summary>
public sealed record WeatherHdr
{
    public float EyeAdaptSpeed { get; init; }
    public float BlurRadius { get; init; }
    public float BlurPasses { get; init; }
    public float EmissiveMult { get; init; }
    public float TargetLum { get; init; }
    public float UpperLumClamp { get; init; }
    public float BrightScale { get; init; }
    public float BrightClamp { get; init; }
    public float LumRampNoTex { get; init; }
    public float LumRampMin { get; init; }
    public float LumRampMax { get; init; }
    public float SunlightDimmer { get; init; }
    public float GrassDimmer { get; init; }
    public float TreeDimmer { get; init; }
}

/// <summary>WTHR DATA block (15 bytes) — see the converter's DATA/WTHR schema for the byte layout.</summary>
public sealed record WeatherData
{
    public byte WindSpeed { get; init; }
    /// <summary>Oblivion DATA byte 1; null for later formats where the byte is unused.</summary>
    public byte? CloudSpeedLower { get; init; }
    /// <summary>Oblivion DATA byte 2; null for later formats where the byte is unused.</summary>
    public byte? CloudSpeedUpper { get; init; }
    public byte TransDelta { get; init; }
    public byte SunGlare { get; init; }
    public byte SunDamage { get; init; }
    public byte PrecipitationBeginFadeIn { get; init; }
    public byte PrecipitationEndFadeOut { get; init; }
    public byte ThunderLightningBeginFadeIn { get; init; }
    public byte ThunderLightningEndFadeOut { get; init; }
    public byte ThunderLightningFrequency { get; init; }
    public byte Flags { get; init; }
    public WeatherRgba LightningColor { get; init; }
}
