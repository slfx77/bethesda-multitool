namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

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
    ///     NAM0 "Weather Colors": one <see cref="WeatherColor" /> per color category (FNV ships 15 —
    ///     240 bytes = 15 × 4 time-bands × RGBA). Index meaning is <see cref="WeatherColorType" />
    ///     (provisional from xEdit, to be confirmed against the engine decompile before the atmosphere
    ///     renderer maps them). Empty when the record carries no NAM0.
    /// </summary>
    public IReadOnlyList<WeatherColor> Colors { get; init; } = [];

    /// <summary>
    ///     FNAM "Fog Distances" — 6 floats: Day Near, Day Far, Night Near, Night Far, Day Max, Night
    ///     Max (last two are FNV's two-float extension over FO3). Empty when absent.
    /// </summary>
    public IReadOnlyList<float> FogDistances { get; init; } = [];

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
///     One NAM0 color category sampled at the four times of day. The atmosphere renderer interpolates
///     between adjacent bands based on the game hour relative to the climate's sunrise/sunset.
/// </summary>
public sealed record WeatherColor(WeatherRgba Sunrise, WeatherRgba Day, WeatherRgba Sunset, WeatherRgba Night);

/// <summary>
///     Index meaning of the <see cref="WeatherRecord.Colors" /> array (FNV WTHR NAM0). Order per
///     xEdit's <c>wbWTHR</c> definition. PROVISIONAL — confirm against the engine decompile (Sky/
///     TESWeather color upload) in atmosphere Phase 2b before the renderer keys off these indices.
/// </summary>
public enum WeatherColorType
{
    SkyUpper = 0,
    Fog = 1,
    Unknown2 = 2,
    Ambient = 3,
    Sunlight = 4,
    Sun = 5,
    Stars = 6,
    SkyLower = 7,
    Horizon = 8,
    EffectLighting = 9,
    CloudLodDiffuse = 10,
    CloudLodAmbient = 11,
    FogFar = 12,
    SkyStatics = 13,
    WaterMultiplier = 14,
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
