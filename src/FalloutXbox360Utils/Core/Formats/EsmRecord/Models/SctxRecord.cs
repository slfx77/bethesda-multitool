namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Script Source Text (SCTX) record.
/// </summary>
public record SctxRecord(string Text, long Offset, int Length);
