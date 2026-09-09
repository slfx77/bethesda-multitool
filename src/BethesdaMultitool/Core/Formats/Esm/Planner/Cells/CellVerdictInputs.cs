using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Inputs for the plan-time per-ref verdict pass: the master index (child locations,
///     ref→cell parents, EditorID stems), the DMP base-type map, and the option switches
///     the decision chain consults. Deliberately NOT the whole options bag — the planner
///     stays decoupled from the writer's configuration surface.
/// </summary>
public sealed record CellVerdictInputs
{
    public required MasterRecordIndex MasterIndex { get; init; }
    public IReadOnlyDictionary<uint, string>? DmpBaseTypes { get; init; }
    public bool RecoverLeveledSpawnActors { get; init; }
    public bool EnableRefrBaseEditorIdRemap { get; init; }
    public bool DiagnosticSkipCellNewRefs { get; init; }
    public bool DiagnosticSkipCellNavm { get; init; }
}
