using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     The set of NAVM FormIDs the writer will actually put in the file, derived from the
///     plan alone.
///     <para>
///     NAVI rows must describe written navmeshes and nothing else: an NVMI entry for a NAVM
///     that never reached the file null-derefs <c>NavMeshInfoMap</c> at load. That guarantee
///     used to require building the cell section first and reading the answer back out of the
///     emitted bytes, which forced NAVI emission to trail cell emission. Every condition the
///     writer applies is a plan fact, so the set can be settled up front instead:
///     </para>
///     <list type="number">
///         <item>the containing cell is not <see cref="RecordDisposition.Skip" />;</item>
///         <item>the cell can be anchored (it has a master record or a captured model);</item>
///         <item>the cell's emission gate passed (<see cref="CellPlan.Emits" />);</item>
///         <item>the cell is not navmesh-only-suppressed, which discards the NAVM prefix;</item>
///         <item>
///             the child is a NEW NAVM carrying a <see cref="NavMeshRecord" /> — master NAVMs
///             are deliberately never re-emitted, and the navmesh diagnostic skip drops all.
///         </item>
///     </list>
///     <para>
///     Serialization itself cannot fail (<c>PlannedNavmEncoder.EncodeRecord</c> returns a
///     non-nullable array), so this set is exact rather than an upper bound. The writer still
///     cross-checks it against what it emitted and warns on any divergence.
///     </para>
/// </summary>
internal static class PlanNavmEmission
{
    public static ImmutableHashSet<uint> Compute(
        ImmutableDictionary<uint, CellPlan> cells,
        bool diagnosticSkipCellNavm)
    {
        if (diagnosticSkipCellNavm)
        {
            return ImmutableHashSet<uint>.Empty;
        }

        var emitted = ImmutableHashSet.CreateBuilder<uint>();
        foreach (var cell in cells.Values)
        {
            if (cell.CellRecordPlan.Disposition == RecordDisposition.Skip
                || cell.Emits != true
                || cell.NavmOnlySuppressed
                || !IsAnchorable(cell))
            {
                continue;
            }

            foreach (var child in cell.TemporaryChildren)
            {
                if (child.Type == "NAVM"
                    && child.Disposition == RecordDisposition.New
                    && child.Model is NavMeshRecord)
                {
                    emitted.Add(child.FormId);
                }
            }
        }

        return emitted.ToImmutable();
    }

    private static bool IsAnchorable(CellPlan cell) =>
        cell.CellRecordPlan.Master is not null || cell.CellRecordPlan.Model is CellRecord;
}
