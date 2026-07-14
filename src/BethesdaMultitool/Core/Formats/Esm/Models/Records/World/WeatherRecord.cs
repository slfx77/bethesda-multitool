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
    ///     Cloud-layer texture paths in ascending SOURCE-layer order: DNAM = layer 0, CNAM = 1,
    ///     ANAM = 2, BNAM = 3; Skyrim+ uses sparse <c>?0TX</c> indices. The engine swaps these onto the
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

    /// <summary>Maps a dense rendered cloud ordinal back to its authored WTHR array index.</summary>
    public int GetCloudLayerSourceIndex(int ordinal) =>
        ordinal >= 0 && ordinal < CloudLayerSourceIndices.Count ? CloudLayerSourceIndices[ordinal] : ordinal;

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
    ///     QNAM "X Cloud Speeds": per-layer U-axis scroll rate the engine accumulates in
    ///     <c>Clouds::Update</c>, normalized around the byte midpoint (127 = still). Authored as one
    ///     unsigned, biased byte per layer on FNV/FO3/Skyrim and one float per layer on FO4/FO76 — the
    ///     handler normalizes both forms so the renderer never re-interprets raw bytes. Empty when absent.
    /// </summary>
    public IReadOnlyList<float> CloudSpeedsX { get; init; } = [];

    /// <summary>RNAM "Y Cloud Speeds": per-layer V-axis scroll rate, normalized like <see cref="CloudSpeedsX" />.</summary>
    public IReadOnlyList<float> CloudSpeedsY { get; init; } = [];

    /// <summary>
    ///     DALC "Directional Ambient Lighting Colors" reduced to the per-time-band MEAN of the
    ///     six-direction ambient cube (X±/Y±/Z±). Skyrim+ engines light exteriors from this cube,
    ///     NOT the NAM0 Ambient row — FO4 authors that row near-black (CommonwealthClear Night =
    ///     (2,2,2) vs its DALC night mean ≈ (18,26,34)), so sourcing ambient from NAM0 rendered
    ///     FO4 nights pitch black. One DALC subrecord per time band (Skyrim 4; FO4/FO76 8 at form
    ///     version 111+ — the 4 base bands + interpolation aids, same order as NAM0). Null when
    ///     the record carries no DALC (FO3/FNV always). Directional (full-cube) shading is a
    ///     possible future upgrade; the mean is the single-ambient reduction.
    /// </summary>
    public WeatherColor? DirectionalAmbient { get; init; }

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
///     One cloud layer's JNAM "Cloud Alphas" opacity sampled at the four times of day (Sunrise / Day /
///     Sunset / Night), each a float in [0, 1]. FO4/FO76/SF1 form version 111+ author four extra
///     interpolation bands (Early/Late Sunrise/Sunset) after these; they're interpolation aids with no
///     distinct time slot, so only the four base bands are kept (blended the same windowed way as the
///     sky/cloud colors). Default 1.0 (fully opaque) per xEdit's wbWeatherCloudAlphas.
/// </summary>
public readonly record struct WeatherCloudAlpha(float Sunrise, float Day, float Sunset, float Night);

/// <summary>
///     One NAM0 color category sampled at the times of day. Band order is Sunrise / Day / Sunset /
///     Night / HighNoon / Midnight — CONFIRMED by the fopdoc FalloutNV WTHR definition (each category is
///     a 24-byte "Time of Day Colors" struct of SIX RGBA colors). FO3 carried only the first four;
///     FNV added the solar-noon (<see cref="HighNoon" />) and solar-midnight (<see cref="Midnight" />)
///     peaks. <see cref="HighNoon" /> / <see cref="Midnight" /> are frequently authored as zero (the
///     engine then falls back to Day / Night). The atmosphere renderer blends all six: Day↔HighNoon
///     pivoting at solar noon and Night↔Midnight pivoting at solar midnight when the peak is authored,
///     the four primary bands otherwise (AtmosphereState.SampleBandV).
/// </summary>
public sealed record WeatherColor(
    WeatherRgba Sunrise, WeatherRgba Day, WeatherRgba Sunset, WeatherRgba Night,
    WeatherRgba HighNoon = default, WeatherRgba Midnight = default);

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
}

/// <summary>WTHR DATA block (15 bytes) — see the converter's DATA/WTHR schema for the byte layout.</summary>
public sealed record WeatherData
{
    public byte WindSpeed { get; init; }
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
