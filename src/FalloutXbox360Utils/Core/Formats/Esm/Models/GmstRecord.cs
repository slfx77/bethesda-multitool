namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Game Setting (GMST) record.
/// </summary>
public record GmstRecord(string Name, long Offset, int Length);
