namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Individual dialogue response from NAM1 + TRDT subrecords.
/// </summary>
public record DialogueResponse
{
    /// <summary>Response text (NAM1 subrecord).</summary>
    public string? Text { get; init; }

    /// <summary>Emotion type (0=Neutral, 1=Anger, 2=Disgust, etc.).</summary>
    public uint EmotionType { get; init; }

    /// <summary>Emotion value (-100 to +100).</summary>
    public int EmotionValue { get; init; }

    /// <summary>Response number within the INFO record.</summary>
    public byte ResponseNumber { get; init; }

    /// <summary>Human-readable emotion name.</summary>
    public string EmotionName => EmotionType switch
    {
        0 => "Neutral",
        1 => "Anger",
        2 => "Disgust",
        3 => "Fear",
        4 => "Sad",
        5 => "Happy",
        6 => "Surprise",
        7 => "Pained",
        8 => "Puzzled",
        _ => $"Unknown ({EmotionType})"
    };
}
