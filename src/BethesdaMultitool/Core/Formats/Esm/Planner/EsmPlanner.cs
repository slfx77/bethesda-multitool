using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Allocation;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner.Validation;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner;

/// <summary>
///     Top-level coordinator that runs phases A–E and returns the immutable
///     <see cref="EmitPlan" />. The writer consumes the plan; nothing else allocates or
///     resolves.
/// </summary>
public sealed class EsmPlanner
{
    /// <summary>
    ///     Types whose plan + emission is owned by the cell-section pipeline when CELL is
    ///     enabled. They must be excluded from the top-level RecordCatalog to avoid
    ///     double-allocating FormIDs for the same source record.
    /// </summary>
    private static readonly HashSet<string> CellPipelineOwnedTypes =
        new(StringComparer.Ordinal) { "CELL", "WRLD", "REFR", "ACHR", "ACRE", "PGRE", "LAND" };

    private readonly DispositionEngine _disposition;
    private readonly FormIdPlanner _allocation;
    private readonly ReferenceResolver _references;

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
    /// <param name="masterRefFormIds">
    ///     Master placed-ref FormIDs (the <c>MasterRecordIndex.RefToCell</c> key set). When
    ///     supplied, the cell-section planner settles each cell's merge mode at plan time
    ///     (<see cref="Cells.CellPlan.Mode" />); when null the writer computes it.
    /// </param>
    public EmitPlan Build(
        IReadOnlyList<ParsedMainRecord> masterRecords,
        RecordCollection dmpRecords,
        IReadOnlySet<string> enabledTypes,
        IReadOnlySet<uint> masterFormIds,
        string? masterPath,
        IReadOnlyDictionary<uint, PcEsmCellContext>? masterCellContexts = null,
        IReadOnlyDictionary<uint, ParsedMainRecord>? masterRecordsByFormId = null,
        FormIdAllocator? cellChildAllocator = null,
        bool emitMasterCellNavmAugmentation = false,
        IReadOnlySet<uint>? masterRefFormIds = null,
        bool replaceCellTemporariesOnOverride = false,
        CellVerdictInputs? cellVerdictInputs = null,
        ImmutableHashSet<uint>? diagnosticKeepMasterFormIds = null,
        ImmutableDictionary<uint, ImmutableHashSet<string>>? diagnosticRetainMasterSubrecords = null)
    {
        var coverage = enabledTypes.ToImmutableHashSet(StringComparer.Ordinal);
        var keepMasterFormIds = diagnosticKeepMasterFormIds ?? ImmutableHashSet<uint>.Empty;
        var retainMasterSubrecords = diagnosticRetainMasterSubrecords
                                     ?? ImmutableDictionary<uint, ImmutableHashSet<string>>.Empty;

        if (enabledTypes.Count == 0)
        {
            if (keepMasterFormIds.Count > 0 || retainMasterSubrecords.Count > 0)
            {
                throw new InvalidOperationException(
                    "Diagnostic FormID directives require at least one planner-enabled record type.");
            }

            return Empty(coverage, masterPath);
        }

        var masterSource = new MasterRecordSource(masterRecords);
        var dmpSource = new DmpRecordSource(dmpRecords);

        // When CELL is enabled the cell-section pipeline owns the entire cell hierarchy
        // (CELL/WRLD/REFR/ACHR/ACRE/PGRE) — emission, FormID allocation, and the
        // source→emitted map. Including those types in the top-level RecordCatalog would
        // double-allocate, producing two different emit FormIDs for the same source and
        // crashing the AddRange merge below.
        var catalogTypes = enabledTypes.Contains("CELL")
            ? (IReadOnlySet<string>)enabledTypes
                .Where(t => !CellPipelineOwnedTypes.Contains(t))
                .ToHashSet(StringComparer.Ordinal)
            : enabledTypes;
        var catalog = RecordCatalog.Build(masterSource, dmpSource, catalogTypes);

        ValidateDiagnosticDirectives(
            masterRecords, catalog, enabledTypes, keepMasterFormIds, retainMasterSubrecords);

        var cellSection = enabledTypes.Contains("CELL")
            ? RunCellSection(
                dmpRecords, masterFormIds, masterCellContexts, masterRecordsByFormId,
                cellChildAllocator, emitMasterCellNavmAugmentation,
                masterRefFormIds, replaceCellTemporariesOnOverride,
                cellVerdictInputs?.MasterIndex.RefToCell)
            : null;

        if (catalog.Count == 0 && cellSection is null)
        {
            return Empty(coverage, masterPath);
        }

        var decisions = EngineMarkerAliasPass.Apply(_disposition.Decide(catalog), out var markerAliases);
        var sourceToEmitted = _allocation.AllocateAll(decisions).AddRange(markerAliases);

        // No phantom-master invariant at this layer: the top-level FormIdPlanner allocates
        // for EVERY DmpNew regardless of whether the source FormID happens to be in master
        // (e.g. DMP-duplicate captures where the second one falls through to DmpNew). Each
        // gets a fresh plugin-range FormID, so emission is safe by construction. The
        // phantom-master risk lived in the CellChildAllocator's per-type skip conditions
        // (now fixed there). The post-write PhantomMasterFormIdRegressionTests guard
        // catches any future leak.

        // Merge cell-child allocations into the plan's source→emitted map so reference
        // resolution sees them as live FormIDs. New worldspaces likewise contribute their
        // source→emitted entries so any cell still pointing at the source FormID resolves
        // to the allocated WRLD anchor at write time.
        if (cellSection is { } cs)
        {
            sourceToEmitted = sourceToEmitted
                .AddRange(cs.CellSourceToEmitted)
                .AddRange(cs.CellChildSourceToEmitted)
                .AddRange(cs.NavmSourceToEmitted)
                .AddRange(cs.WorldspaceSourceToEmitted);
        }
        var containedAllocations = cellSection?.LandByCellSourceToEmitted.Values
            .Concat(cellSection.AdditionalEmittedFormIds);
        var emittedFormIds = BuildEmittedFormIds(
            decisions, sourceToEmitted, masterFormIds, containedAllocations)
            .Union(RuntimeStateRecordPolicy.EngineFormIds);
        var resolvedRefsByIndex = _references.ResolveAll(decisions, emittedFormIds, sourceToEmitted);
        var scriptSanitation = ScriptReferenceSafetyPlanner.Apply(
            decisions, sourceToEmitted, emittedFormIds, resolvedRefsByIndex, _references);
        decisions = scriptSanitation.Decisions;
        sourceToEmitted = scriptSanitation.SourceToEmitted;
        emittedFormIds = scriptSanitation.EmittedFormIds;
        resolvedRefsByIndex = scriptSanitation.References;

        var records = BuildRecordPlans(
            decisions, sourceToEmitted, resolvedRefsByIndex, retainMasterSubrecords);
        var packageSanitation = PackagePlanSanitizer.Apply(
            records, emittedFormIds, sourceToEmitted, masterRecords, cellSection);
        records = packageSanitation.Records;
        if (packageSanitation.SuppressedNewFormIds.Count > 0)
        {
            emittedFormIds = emittedFormIds
                .Except(packageSanitation.SuppressedNewFormIds)
                .ToImmutableHashSet();
        }

        var (ordered, diagnostics) = PlanValidator.Validate(records);
        var validPackageFormIds = PackagePlanSanitizer.BuildValidPackageFormIds(masterRecords, ordered);
        var packageDiagnostics = BuildPackageReferenceDiagnostics(ordered, validPackageFormIds);
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
            ValidPackageFormIds = validPackageFormIds,
            ValidScriptFormIds = ScriptReferenceSafetyPlanner.BuildValidScriptFormIds(masterRecords, ordered),
            RecordIndexByEmittedFormId = indexByFormId.ToImmutable(),
            Diagnostics = diagnostics
                .AddRange(scriptSanitation.Diagnostics)
                .AddRange(packageSanitation.Diagnostics)
                .AddRange(packageDiagnostics)
                .AddRange(cellSection?.Diagnostics ?? []),
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
                LandByCellSourceFormId = cellResult.LandByCellSourceToEmitted,
            };
        }

        if (cellSection is not null && masterRecordsByFormId is not null)
        {
            plan = LandTextureRewritePlanner.Apply(plan, masterRecordsByFormId);
        }

        // Phase F: per-ref emit/drop verdicts. Runs AFTER top-level allocation because a
        // ref's base may resolve through a top-level-planner-allocated record (e.g. a
        // recovered leveled-spawn actor pointing at a planner-emitted proto NPC_).
        if (cellVerdictInputs is { } verdictInputs
            && masterRecordsByFormId is not null
            && !plan.CellsByFormId.IsEmpty)
        {
            plan = plan with
            {
                CellsByFormId = CellChildVerdictPlanner.Apply(
                    plan.CellsByFormId, masterRecordsByFormId,
                    sourceToEmitted, emittedFormIds, verdictInputs),
            };
            plan = PostVerdictScriptClosurePlanner.Apply(plan, masterRecords);
        }

        if (masterRecordsByFormId is not null && !plan.CellsByFormId.IsEmpty)
        {
            plan = plan with
            {
                NavmDoorLinks = NavmDoorLinkPlanner.Build(plan, masterRecordsByFormId),
            };
        }

        return plan;
    }

    internal static ImmutableArray<PlanDiagnostic> BuildPackageReferenceDiagnostics(
        ImmutableArray<RecordPlan> records,
        IReadOnlySet<uint> validPackageFormIds)
    {
        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();
        foreach (var record in records)
        {
            if (record.Type != "NPC_" || record.Disposition is RecordDisposition.KeepMaster or RecordDisposition.Skip)
            {
                continue;
            }

            foreach (var reference in record.References)
            {
                if (!reference.FieldPath.StartsWith("PKID[", StringComparison.Ordinal))
                {
                    continue;
                }

                var finalFormId = reference.FinalFormId ?? reference.OriginalFormId ?? 0;
                if (reference.Action == ResolvedRefAction.Resolved
                    && validPackageFormIds.Contains(finalFormId))
                {
                    continue;
                }

                var wrongType = reference.Action == ResolvedRefAction.Resolved;

                diagnostics.Add(new PlanDiagnostic
                {
                    Kind = PlanDiagnosticKind.Decision,
                    Phase = "References",
                    Code = wrongType
                        ? "references.drop.pkid-wrong-type"
                        : "references.drop.pkid-dangle",
                    RecordType = record.Type,
                    FormId = record.FormId,
                    Message = wrongType
                        ? $"Dropped {reference.FieldPath} package reference 0x{reference.OriginalFormId ?? 0:X8}: "
                          + $"resolved target 0x{finalFormId:X8} is live but is not a PACK record."
                        : $"Dropped {reference.FieldPath} package reference "
                          + $"0x{reference.OriginalFormId ?? 0:X8}: {reference.Reason}",
                });
            }
        }

        return diagnostics.ToImmutable();
    }

    private static CellSectionPlanner.CellSectionResult? RunCellSection(
        RecordCollection dmpRecords,
        IReadOnlySet<uint> masterFormIds,
        IReadOnlyDictionary<uint, PcEsmCellContext>? masterCellContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord>? masterRecordsByFormId,
        FormIdAllocator? cellChildAllocator,
        bool emitMasterCellNavmAugmentation,
        IReadOnlySet<uint>? masterRefFormIds,
        bool replaceCellTemporariesOnOverride,
        IReadOnlyDictionary<uint, uint>? masterRefToCell)
    {
        if (masterCellContexts is null || masterRecordsByFormId is null || cellChildAllocator is null)
        {
            return null; // Cell-section planning requires the master cell index + an allocator.
        }

        return CellSectionPlanner.Plan(
            masterCellContexts,
            masterRecordsByFormId,
            dmpRecords.Cells,
            dmpRecords.NavMeshes,
            dmpRecords.Worldspaces,
            masterFormIds,
            cellChildAllocator,
            emitMasterCellNavmAugmentation,
            masterRefFormIds,
            replaceCellTemporariesOnOverride,
            masterRefToCell);
    }

    private static ImmutableHashSet<uint> BuildEmittedFormIds(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions,
        ImmutableDictionary<uint, uint> sourceToEmitted,
        IReadOnlySet<uint> masterFormIds,
        IEnumerable<uint>? containedAllocations = null)
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

        if (containedAllocations is not null)
        {
            foreach (var allocated in containedAllocations)
            {
                builder.Add(allocated);
            }
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
        ImmutableDictionary<int, ImmutableArray<ResolvedRef>> refsByIndex,
        ImmutableDictionary<uint, ImmutableHashSet<string>> retainMasterSubrecords)
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
            var retainedSignatures = entry.MasterFormId is { } masterFormId
                                     && retainMasterSubrecords.TryGetValue(masterFormId, out var configured)
                ? configured
                : ImmutableHashSet.Create<string>(StringComparer.Ordinal);

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
                RetainMasterSubrecordSignatures = retainedSignatures,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = decision.Provenance,
            });
        }

        return builder.ToImmutable();
    }

    private static void ValidateDiagnosticDirectives(
        IReadOnlyList<ParsedMainRecord> masterRecords,
        IReadOnlyList<CatalogEntry> catalog,
        IReadOnlySet<string> enabledTypes,
        ImmutableHashSet<uint> keepMasterFormIds,
        ImmutableDictionary<uint, ImmutableHashSet<string>> retainMasterSubrecords)
    {
        var overlap = keepMasterFormIds.FirstOrDefault(retainMasterSubrecords.ContainsKey);
        if (overlap != 0 || (keepMasterFormIds.Contains(0) && retainMasterSubrecords.ContainsKey(0)))
        {
            throw new InvalidOperationException(
                $"Diagnostic FormID 0x{overlap:X8} cannot be both master-pure and partially retained.");
        }

        var masterByFormId = new Dictionary<uint, ParsedMainRecord>();
        foreach (var master in masterRecords)
        {
            masterByFormId.TryAdd(master.Header.FormId, master);
        }

        foreach (var formId in keepMasterFormIds.Concat(retainMasterSubrecords.Keys).Distinct())
        {
            if (!masterByFormId.TryGetValue(formId, out var master))
            {
                throw new InvalidOperationException(
                    $"Diagnostic FormID 0x{formId:X8} does not exist in the master ESM.");
            }

            if (!enabledTypes.Contains(master.Header.Signature))
            {
                throw new InvalidOperationException(
                    $"Diagnostic FormID 0x{formId:X8} is {master.Header.Signature}, which is not planner-enabled.");
            }

            var entry = catalog.FirstOrDefault(candidate =>
                candidate.MasterFormId == formId
                && string.Equals(candidate.Type, master.Header.Signature, StringComparison.Ordinal));
            if (entry is null || entry.Source != SourceKind.DmpOverride)
            {
                throw new InvalidOperationException(
                    $"Diagnostic FormID 0x{formId:X8} is not an exact DMP override of its master record.");
            }

            if (!retainMasterSubrecords.TryGetValue(formId, out var signatures))
            {
                continue;
            }

            if (signatures.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Diagnostic FormID 0x{formId:X8} has no retained subrecord signatures.");
            }

            foreach (var signature in signatures)
            {
                if (signature.Length != 4)
                {
                    throw new InvalidOperationException(
                        $"Diagnostic subrecord signature '{signature}' for 0x{formId:X8} must be exactly four characters.");
                }

                if (!master.Subrecords.Any(subrecord =>
                        string.Equals(subrecord.Signature, signature, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Master {master.Header.Signature} 0x{formId:X8} has no {signature} subrecord to retain.");
                }
            }
        }
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

