using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     The planner-side equivalent of legacy <see cref="CellGrupBuilder.BuildCellSection" />.
///     Walks <see cref="EmitPlan.CellsByFormId" />, converts each <see cref="Planner.Cells.CellPlan" />
///     into a <see cref="CellOverrideBundle" />, then delegates the GRUP framing to the
///     legacy builder. Reusing the legacy framing means the GRUP nesting / labels match
///     byte-for-byte by construction; once the legacy pipeline is deleted in the final
///     bulk-removal PR the framing logic absorbs into this namespace.
/// </summary>
public sealed class PlanCellSectionBuilder
{
    /// <summary>
    ///     Build the cell section bytes from the planner's cell hierarchy. Returns null
    ///     when the plan has no cells the writer can emit (which is the default in Tier 5b
    ///     before the EsmPlanner is extended to populate <see cref="EmitPlan.CellsByFormId" />).
    /// </summary>
    public byte[]? BuildCellSection(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        PluginBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(masterByFormId);
        ArgumentNullException.ThrowIfNull(options);

        var bundles = ConvertCellsToBundles(plan);
        if (bundles.Count == 0)
        {
            return null;
        }

        return CellGrupBuilder.BuildCellSection(
            bundles, masterByFormId, newWorldspacesByDmpFormId: null);
    }

    /// <summary>
    ///     Convert each <see cref="Planner.Cells.CellPlan" /> entry to a bundle the legacy
    ///     builder consumes. Tier 5b.4 ships the conversion shell — only cells with master
    ///     records (KeepMaster / Override against master) round-trip cleanly. New CELLs
    ///     (no master) require fresh anchor encoding that ships in a follow-up sub-tier.
    /// </summary>
    private static IReadOnlyList<CellOverrideBundle> ConvertCellsToBundles(EmitPlan plan)
    {
        var bundles = new List<CellOverrideBundle>(plan.CellsByFormId.Count);

        foreach (var (cellFormId, cellPlan) in plan.CellsByFormId)
        {
            if (cellPlan.CellRecordPlan.Disposition == RecordDisposition.Skip)
            {
                continue;
            }

            if (cellPlan.CellRecordPlan.Master is null)
            {
                continue; // New CELLs deferred; require fresh anchor encoding.
            }

            var cellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(cellPlan.CellRecordPlan.Master);

            bundles.Add(new CellOverrideBundle
            {
                CellFormId = cellFormId,
                Context = cellPlan.Context,
                CellRecordBytes = cellRecordBytes,
                PersistentChildRecords = [],
                VwdChildRecords = [],
                TemporaryChildRecords = [],
            });
        }

        return bundles;
    }
}
