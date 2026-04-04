namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     A single key-value field within a report section.
/// </summary>
/// <param name="Key">Display label (e.g., "Damage", "Fire Rate").</param>
/// <param name="Value">Typed value with display string.</param>
/// <param name="FormIdRef">Optional raw FormID hex string for cross-linking (e.g., "0x00012345").</param>
internal sealed record ReportField(string Key, ReportValue Value, string? FormIdRef = null);
