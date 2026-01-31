namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;

/// <summary>
///     NAM1 subrecord - Dialogue response text.
///     Variable length null-terminated string in INFO records.
/// </summary>
public record ResponseTextSubrecord(string Text, long Offset);
