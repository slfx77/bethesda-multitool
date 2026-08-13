namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Result of <see cref="PlanCellSectionBuilder.BuildCellSectionCore" />: the assembled
///     cell-section bytes (null when every bundle was suppressed), the NAVM FormIDs actually
///     written, and the placed-ref/child FormIDs the bundles ended up containing.
/// </summary>
/// <param name="EmittedNavmFormIds">
///     Navmeshes this pass wrote. Used for NVEX sanitation within the section. NAVI rows are
///     no longer built from it — <c>PlanNavmEmission</c> settles the same set at plan time
///     (retirement Stage H6), which is what decoupled NAVI from cell-section ordering.
/// </param>
internal sealed record CellSectionBuildResult(
    byte[]? SectionBytes,
    IReadOnlySet<uint> EmittedNavmFormIds,
    IReadOnlySet<uint> EmittedPlacedReferenceFormIds,
    IReadOnlySet<uint> OverriddenChildFormIds);
