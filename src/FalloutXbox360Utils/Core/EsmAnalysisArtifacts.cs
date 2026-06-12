namespace FalloutXbox360Utils.Core;

/// <summary>
///     Result of ESM analysis plus an optional transient file buffer for immediate semantic parsing.
/// </summary>
internal sealed record EsmAnalysisArtifacts(AnalysisResult Result, byte[]? FileBuffer);
