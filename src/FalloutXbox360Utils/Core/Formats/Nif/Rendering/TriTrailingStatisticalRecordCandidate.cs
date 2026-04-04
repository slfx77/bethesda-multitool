namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Best-effort raw record candidate for the trailing `0x38` statistical
///     region found at EOF in the current anchor samples. This is inferred from
///     the raw bytes, not from a full byte-perfect parser for the whole tail.
/// </summary>
internal readonly record struct TriTrailingStatisticalRecordCandidate(
    string Name,
    int RecordOffset,
    int RecordLength,
    int NameLengthWithTerminator,
    int PayloadCount,
    int PayloadOffset,
    int PayloadLength);
