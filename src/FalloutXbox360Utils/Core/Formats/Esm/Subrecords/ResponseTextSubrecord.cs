namespace FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

/// <summary>
///     NAM1 subrecord - Dialogue response text.
///     Variable length null-terminated string in INFO records.
/// </summary>
public record ResponseTextSubrecord(string Text, long Offset);
