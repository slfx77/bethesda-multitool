namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Minimal key projection: only the FormID is needed by the key→locked-door reverse index.
/// </summary>
internal readonly record struct KeySkeleton(uint FormId);
