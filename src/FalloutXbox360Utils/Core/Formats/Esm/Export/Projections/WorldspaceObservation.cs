namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     A single dump's view of a worldspace's identity, retained for chronological replay
///     during cross-dump aggregation. The aggregator builds the rename-history label
///     (<c>"Old → New (EditorID)"</c>) by walking observations in chronological dump order.
///     If observations were folded together at projection time they'd lose the per-dump
///     temporal slot the rename detection needs.
/// </summary>
internal readonly record struct WorldspaceObservation(
    uint FormId,
    string? DisplayName,
    string? EditorId);
