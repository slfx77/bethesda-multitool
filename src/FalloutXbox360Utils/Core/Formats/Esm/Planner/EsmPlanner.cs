using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Allocation;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Validation;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     Top-level coordinator that runs phases A–E and returns the immutable
///     <see cref="EmitPlan" />. The writer consumes the plan; nothing else allocates or
///     resolves.
/// </summary>
public sealed class EsmPlanner
{
    private readonly DispositionEngine _disposition;
    private readonly FormIdPlanner _allocation;
    private readonly ReferenceResolver _references;
    private readonly PlanValidator _validator = new();

    public EsmPlanner(
        DispositionEngine disposition,
        FormIdAllocator allocator,
        ReferenceResolver references)
    {
        _disposition = disposition ?? throw new ArgumentNullException(nameof(disposition));
        _allocation = new FormIdPlanner(allocator ?? throw new ArgumentNullException(nameof(allocator)));
        _references = references ?? throw new ArgumentNullException(nameof(references));
    }

    /// <summary>
    ///     Build the plan for the requested record-type subset.
    /// </summary>
    /// <param name="masterRecords">The full master ESM record list (TES4 + everything).</param>
    /// <param name="dmpRecords">Semantic DMP capture.</param>
    /// <param name="enabledTypes">
    ///     The record types the planner should handle on this run. Empty produces an empty
    ///     plan (the writer emits nothing, the legacy pipeline handles every type).
    /// </param>
    /// <param name="masterFormIds">
    ///     The full set of master FormIDs — used as the seed of the emit set so references
    ///     to master records always resolve.
    /// </param>
    /// <param name="masterPath">Master ESM path for plan metadata.</param>
    public EmitPlan Build(
        IReadOnlyList<ParsedMainRecord> masterRecords,
        RecordCollection dmpRecords,
        IReadOnlySet<string> enabledTypes,
        IReadOnlySet<uint> masterFormIds,
        string? masterPath,
        IReadOnlyDictionary<uint, PcEsmCellContext>? masterCellContexts = null,
        IReadOnlyDictionary<uint, ParsedMainRecord>? masterRecordsByFormId = null,
        FormIdAllocator? cellChildAllocator = null)
    {
        var coverage = enabledTypes.ToImmutableHashSet(StringComparer.Ordinal);

        if (enabledTypes.Count == 0)
        {
            return Empty(coverage, masterPath);
        }

        var masterSource = new MasterRecordSource(masterRecords);
        var dmpSource = new DmpRecordSource(dmpRecords);
        var catalog = RecordCatalog.Build(masterSource, dmpSource, enabledTypes);

        var cellSection = enabledTypes.Contains("CELL")
            ? RunCellSection(dmpRecords, masterFormIds, masterCellContexts, masterRecordsByFormId, cellChildAllocator)
            : null;

        if (catalog.Count == 0 && cellSection is null)
        {
            return Empty(coverage, masterPath);
        }

        var decisions = _disposition.Decide(catalog);
        var sourceToEmitted = _allocation.AllocateAll(decisions);

        // Merge cell-child allocations into the plan's source→emitted map so reference
        // resolution sees them as live FormIDs.
        if (cellSection is { } cs)
        {
            sourceToEmitted = sourceToEmitted
                .AddRange(cs.CellChildSourceToEmitted)
                .AddRange(cs.NavmSourceToEmitted);
        }

        var emittedFormIds = BuildEmittedFormIds(decisions, sourceToEmitted, masterFormIds);
        var resolvedRefsByIndex = _references.ResolveAll(decisions, emittedFormIds, sourceToEmitted);

        var records = BuildRecordPlans(decisions, sourceToEmitted, resolvedRefsByIndex);
        var (ordered, diagnostics) = _validator.Validate(records);

        var indexByFormId = ImmutableDictionary.CreateBuilder<uint, int>();
        for (var i = 0; i < ordered.Length; i++)
        {
            indexByFormId[ordered[i].FormId] = i;
        }

        var plan = new EmitPlan
        {
            Records = ordered,
            SourceToEmittedFormId = sourceToEmitted,
            EmittedFormIds = emittedFormIds,
            RecordIndexByEmittedFormId = indexByFormId.ToImmutable(),
            Diagnostics = diagnostics,
            Meta = new PlanMetadata
            {
                NextObjectId = _allocation.NextObjectId,
                MasterPath = masterPath,
                PlannerCoverage = coverage,
            },
        };

        if (cellSection is { } cellResult)
        {
            plan = plan with
            {
                CellsByFormId = cellResult.CellsByFormId,
                WorldspacesByFormId = cellResult.WorldspacesByFormId,
                NavmEntries = cellResult.NavmEntries,
            };
        }

        return plan;
    }

    private static CellSectionPlanner.CellSectionResult? RunCellSection(
        RecordCollection dmpRecords,
        IReadOnlySet<uint> masterFormIds,
        IReadOnlyDictionary<uint, PcEsmCellContext>? masterCellContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord>? masterRecordsByFormId,
        FormIdAllocator? cellChildAllocator)
    {
        if (masterCellContexts is null || masterRecordsByFormId is null || cellChildAllocator is null)
        {
            return null; // Cell-section planning requires the master cell index + an allocator.
        }

        return new CellSectionPlanner().Plan(
            masterCellContexts,
            masterRecordsByFormId,
            dmpRecords.Cells,
            dmpRecords.NavMeshes,
            dmpRecords.Worldspaces,
            masterFormIds,
            cellChildAllocator);
    }

    private static ImmutableHashSet<uint> BuildEmittedFormIds(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions,
        ImmutableDictionary<uint, uint> sourceToEmitted,
        IReadOnlySet<uint> masterFormIds)
    {
        var builder = ImmutableHashSet.CreateBuilder<uint>();
        foreach (var id in masterFormIds)
        {
            builder.Add(id);
        }

        foreach (var allocated in sourceToEmitted.Values)
        {
            builder.Add(allocated);
        }

        foreach (var (entry, decision) in decisions)
        {
            if (decision.Disposition == RecordDisposition.Skip)
            {
                continue;
            }

            if (entry.MasterFormId is { } masterId)
            {
                builder.Add(masterId);
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<RecordPlan> BuildRecordPlans(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions,
        ImmutableDictionary<uint, uint> sourceToEmitted,
        ImmutableDictionary<int, ImmutableArray<ResolvedRef>> refsByIndex)
    {
        var builder = ImmutableArray.CreateBuilder<RecordPlan>(decisions.Count);

        for (var i = 0; i < decisions.Count; i++)
        {
            var (entry, decision) = decisions[i];
            var formId = entry.MasterFormId
                ?? (entry.DmpFormId is { } src && sourceToEmitted.TryGetValue(src, out var allocated)
                    ? allocated
                    : entry.DmpFormId ?? 0u);
            var refs = refsByIndex.TryGetValue(i, out var resolved)
                ? resolved
                : ImmutableArray<ResolvedRef>.Empty;

            builder.Add(new RecordPlan
            {
                Type = entry.Type,
                Disposition = decision.Disposition,
                FormId = formId,
                SourceFormId = entry.DmpFormId,
                Model = entry.Model,
                Master = entry.Master,
                References = refs,
                OverrideSubrecords = null,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = decision.Provenance,
            });
        }

        return builder.ToImmutable();
    }

    private EmitPlan Empty(ImmutableHashSet<string> coverage, string? masterPath)
    {
        return new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet<uint>.Empty,
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = _allocation.NextObjectId,
                MasterPath = masterPath,
                PlannerCoverage = coverage,
            },
        };
    }
}
