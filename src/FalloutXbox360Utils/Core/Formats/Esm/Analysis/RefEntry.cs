namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis;

/// <summary>
///     A placed reference (REFR/ACHR/ACRE) with optional editor ID.
/// </summary>
internal sealed record RefEntry(uint FormId, string? EditorId);
