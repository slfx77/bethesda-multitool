namespace FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

/// <summary>
///     TRDT subrecord - Dialogue response data.
///     24 bytes: emotionType(4) + emotionValue(4) + conversationTopic(4) + responseNumber(1) +
///     padding(3) + soundFile(4) + useEmotionAnim(1) + padding(3).
/// </summary>
public record ResponseDataSubrecord(
    uint EmotionType,
    int EmotionValue,
    byte ResponseNumber,
    uint? SoundFormId,
    long Offset);
