namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Result of <see cref="PlanCellSectionBuilder.BuildCellSectionCore" />: the assembled
///     cell-section bytes (null when every bundle was suppressed) plus the NAVM FormIDs that
///     were ACTUALLY written. NAVI rows must be built from this set, not from the plan —
///     a planned NAVM whose parent bundle got suppressed would otherwise leave a dangling
///     NVMI entry that null-derefs NavMeshInfoMap at load.
/// </summary>
internal sealed record CellSectionBuildResult(
    byte[]? SectionBytes,
    IReadOnlySet<uint> EmittedNavmFormIds,
    IReadOnlySet<uint> OverriddenChildFormIds);
