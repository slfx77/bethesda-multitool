namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     A named group of fields within a record report (e.g., "Combat Stats", "Accuracy").
/// </summary>
internal sealed record ReportSection(string Name, List<ReportField> Fields);
