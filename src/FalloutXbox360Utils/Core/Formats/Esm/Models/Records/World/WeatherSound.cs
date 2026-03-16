namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

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