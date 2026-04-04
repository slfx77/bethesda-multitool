namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     A structured representation of a record's reportable fields.
///     All output formats (TXT, JSON, CSV) derive from this single model.
/// </summary>
internal sealed record RecordReport(
    string RecordType,
    uint FormId,
    string? EditorId,
    string? DisplayName,
    List<ReportSection> Sections);
