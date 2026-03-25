namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Best-effort raw record candidate for the differential (`0x34`) family
///     where the bytes match the current anchor pattern:
///     `u32 nameLen -> name -> float scale -> vertexCount * 3 * int16`.
/// </summary>
internal readonly record struct TriDifferentialRecordCandidate(
    string Name,
    int RecordOffset,
    int RecordLength,
    int NameLengthWithTerminator,
    float Scale,
    int PackedDeltaPayloadOffset,
    int PackedDeltaPayloadLength);