namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Per-cell grouping data captured during projection. The aggregator's chronological-replay
///     loop consumes this to compute the cell's worldspace group key, track the grid coordinates,
///     and decide whether ESM authority is overriding a prior DMP-sourced group.
/// </summary>
/// <param name="CellFormId">The (post-virtual-canonicalization) cell FormID.</param>
/// <param name="IsInterior">Mirror of <c>CellRecord.IsInterior</c>.</param>
/// <param name="WorldspaceFormId">The exterior cell's parent worldspace, when set.</param>
/// <param name="GridX">Grid X coordinate (exterior cells only).</param>
/// <param name="GridY">Grid Y coordinate (exterior cells only).</param>
internal readonly record struct CellGroupObservation(
    uint CellFormId,
    bool IsInterior,
    uint? WorldspaceFormId,
    int? GridX,
    int? GridY);
