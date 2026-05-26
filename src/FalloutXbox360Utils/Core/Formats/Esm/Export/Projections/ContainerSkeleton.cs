namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Minimal container projection: only the FormID is needed by the container placement index.
/// </summary>
internal readonly record struct ContainerSkeleton(uint FormId);
