namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis;

/// <summary>
///     A base record (STAT, ACTI, DOOR, etc.) that references a model path.
/// </summary>
internal sealed record BaseRecordRef(uint FormId, string? EditorId, string RecordType);