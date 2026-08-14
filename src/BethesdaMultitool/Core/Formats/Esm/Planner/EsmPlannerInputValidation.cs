using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner;

/// <summary>
///     Precondition checks for <see cref="EsmPlanner" />'s optional cell-planning inputs.
///     <para>
///         Split out of <see cref="EsmPlanner" /> so that coordinator stays under the planner
///         line ceiling. The inputs are optional on the signature because callers that request
///         no CELL coverage legitimately omit them; once CELL IS requested they are all
///         mandatory, and failing here names every missing one at once rather than surfacing
///         later as a null-deref inside a cell pass.
///     </para>
/// </summary>
internal static class EsmPlannerInputValidation
{
    /// <summary>
    ///     Throws when <paramref name="coverage" /> includes CELL but any cell-planning input is
    ///     absent. A no-CELL run is a no-op.
    /// </summary>
    internal static void ValidateCellPlanningInputs(
        ImmutableHashSet<string> coverage,
        IReadOnlyDictionary<uint, PcEsmCellContext>? masterCellContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord>? masterRecordsByFormId,
        FormIdAllocator? cellChildAllocator,
        IReadOnlySet<uint>? masterRefFormIds,
        CellVerdictInputs? cellVerdictInputs)
    {
        if (!coverage.Contains("CELL"))
        {
            return;
        }

        var missing = new List<string>();
        if (masterCellContexts is null) missing.Add(nameof(masterCellContexts));
        if (masterRecordsByFormId is null) missing.Add(nameof(masterRecordsByFormId));
        if (cellChildAllocator is null) missing.Add(nameof(cellChildAllocator));
        if (masterRefFormIds is null) missing.Add(nameof(masterRefFormIds));
        if (cellVerdictInputs is null) missing.Add(nameof(cellVerdictInputs));

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "CELL planner coverage requires complete cell-planning inputs; missing: " +
                string.Join(", ", missing) + ".");
        }
    }
}
