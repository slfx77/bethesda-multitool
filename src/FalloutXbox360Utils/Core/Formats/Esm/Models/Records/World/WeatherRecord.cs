namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

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

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     A sound associated with a weather type.
/// </summary>
public record WeatherSound
{
    /// <summary>Sound FormID.</summary>
    public uint SoundFormId { get; init; }

    /// <summary>Sound type (0=Default, 1=Precipitation, 2=Wind, 3=Thunder).</summary>
    public uint Type { get; init; }
}
