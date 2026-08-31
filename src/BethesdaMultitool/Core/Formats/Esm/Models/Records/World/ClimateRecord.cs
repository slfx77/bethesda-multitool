namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Climate (CLMT) record. A worldspace references one CLMT (via WRLD CNAM); it defines which
///     weathers can occur (and their chances), the sun / sun-glare textures, and the day's sunrise /
///     sunset / moon timing. Starfield additionally carries its Creation Engine 2 weather-settings
///     choices in WSLT; those references are retained separately from legacy WTHR choices so callers
///     cannot accidentally treat a WTHS FormID as a WTHR FormID.
/// </summary>
public record ClimateRecord
{
    /// <summary>FormID of the climate record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>FNAM — sun texture path (e.g. <c>textures\sky\sun.dds</c>).</summary>
    public string? SunTexture { get; init; }

    /// <summary>GNAM — sun-glare texture path.</summary>
    public string? SunGlareTexture { get; init; }

    /// <summary>MODL — celestial / sky model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>WLST — the weather list (weather FormID + chance + optional global) this climate cycles.</summary>
    public IReadOnlyList<ClimateWeatherEntry> WeatherTypes { get; init; } = [];

    /// <summary>
    ///     WSLT — Starfield weather-settings choices (WTHS FormID + chance + optional global).
    ///     WTHS uses CE2 reflected REFL/RDIF payloads and is deliberately kept distinct from
    ///     <see cref="WeatherTypes" /> until that payload has a source-backed decoder.
    /// </summary>
    public IReadOnlyList<ClimateWeatherSettingsEntry> WeatherSettingsTypes { get; init; } = [];

    /// <summary>TNAM — sunrise / sunset / moon timing.</summary>
    public ClimateTimingData? Timing { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>One CLMT WLST entry: a candidate weather, its selection chance, and an optional gating global.</summary>
public sealed record ClimateWeatherEntry(uint WeatherFormId, int Chance, uint GlobalFormId);

/// <summary>One Starfield CLMT WSLT entry: a candidate WTHS record, its chance, and an optional gating global.</summary>
public sealed record ClimateWeatherSettingsEntry(uint WeatherSettingsFormId, int Chance, uint GlobalFormId);

/// <summary>
///     CLMT TNAM timing block (five bytes in Starfield, six in legacy games). The four time fields are
///     stored RAW (one byte each) exactly as in the record. Byte→hours = value × 1/6 (10-minute units) —
///     CONFIRMED by the engine decompile:
///     <c>TESClimate::Load</c> reads the 6-byte TNAM into <c>climate+0x60</c> and <c>Sky::GetSunriseBegin</c>
///     reads <c>climate+0x60</c> and multiplies by <c>0.16666667</c>. See
///     <c>AtmosphereState.ClimateTiming.FromClimateData</c>.
/// </summary>
public sealed record ClimateTimingData(
    byte SunriseBegin,
    byte SunriseEnd,
    byte SunsetBegin,
    byte SunsetEnd,
    byte Volatility,
    byte MoonPhaseLength)
{
    /// <summary>
    ///     Whether TNAM actually carried the sixth moon/phase byte. Starfield's xEdit definition passes
    ///     no phase callback and retail Starfield.esm authors five-byte TNAM blocks; legacy games author
    ///     six. Keeping this bit avoids inventing a byte when a parsed record is emitted again.
    /// </summary>
    public bool HasMoonPhaseLength { get; init; } = true;
}
