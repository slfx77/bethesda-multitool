namespace FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

/// <summary>
///     TRDT subrecord - Dialogue response data.
///     20 bytes: emotionType(4) + emotionValue(4) + unused(4) + responseNumber(1) + unused(3) + soundFile(4)
/// </summary>
public record ResponseDataSubrecord(
    uint EmotionType,
    int EmotionValue,
    byte ResponseNumber,
    long Offset);
