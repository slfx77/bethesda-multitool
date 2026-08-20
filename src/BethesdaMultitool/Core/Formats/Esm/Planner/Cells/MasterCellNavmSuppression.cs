using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     The master-cell NAVM suppression gate. Captured navmeshes whose (coord-alias-resolved)
///     parent cell exists in the retail master are removed from planning: engine RE shows the
///     cell's per-file temp-children merge keeps master's own complete meshes, while our
///     reconstructions carry empty NVCI door links and shadowing master caused the confirmed
///     Gomorrah01 cold-<c>coc</c> null-deref. Opt out via
///     <c>--emit-master-cell-navm-augmentation</c>.
///     <para>
///         The gate's yield is entirely a function of WHERE the capture's crash site was: a dump
///         that died in a proto-only worldspace keeps everything (xex21: 46/46), one that died in
///         retail-shipped territory loses nearly everything (xex44: 51 of 71). That made the silent
///         version of this gate indistinguishable from "the dump held no navmeshes" — hence the
///         mandatory aggregate diagnostic.
///     </para>
/// </summary>
internal static class MasterCellNavmSuppression
{
    /// <summary>
    ///     Apply the gate. Returns the surviving navmeshes and, when anything was suppressed,
    ///     exactly one aggregate Warning diagnostic (never per-record spam).
    /// </summary>
    public static IReadOnlyList<NavMeshRecord> Apply(
        IReadOnlyList<NavMeshRecord> dmpNavmeshes,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        Func<uint, uint> effectiveCellFormId,
        out ImmutableArray<PlanDiagnostic> diagnostics)
    {
        var kept = new List<NavMeshRecord>(dmpNavmeshes.Count);
        var suppressed = 0;
        var sampleCells = new List<uint>(3);
        foreach (var navm in dmpNavmeshes)
        {
            if (masterContexts.ContainsKey(effectiveCellFormId(navm.CellFormId)))
            {
                suppressed++;
                if (sampleCells.Count < 3)
                {
                    sampleCells.Add(effectiveCellFormId(navm.CellFormId));
                }
            }
            else
            {
                kept.Add(navm);
            }
        }

        if (suppressed == 0)
        {
            diagnostics = ImmutableArray<PlanDiagnostic>.Empty;
            return kept;
        }

        // Warning, not Decision: Decision-kind lines are verbose-gated, and a default run
        // previously produced ZERO console evidence of this drop while the summary read
        // failed=0. Same precedent as the "No encoder for {type}" warning — mass non-emission
        // must be visible by default.
        diagnostics =
        [
            new PlanDiagnostic
            {
                Kind = PlanDiagnosticKind.Warning,
                Phase = "Catalog",
                Code = "navm.master-cell-suppressed",
                RecordType = "NAVM",
                Message =
                    $"Suppressed {suppressed} captured NAVM(s) whose parent cells exist in the " +
                    $"master (policy: master keeps its own complete meshes; reconstructed ones " +
                    $"carry empty NVCI door links). {kept.Count} NAVM(s) in non-master cells kept. " +
                    $"Sample master cells: {string.Join(", ", sampleCells.Select(c => $"0x{c:X8}"))}. " +
                    "Opt in to emission with --emit-master-cell-navm-augmentation."
            }
        ];

        return kept;
    }
}
