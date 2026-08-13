using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells.Policies;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Orchestrates the cell-section planning phases — catalog, disposition, child
///     allocation, and worldspace catalog — and produces the cell-related slices of
///     <see cref="EmitPlan" />. <see cref="EsmPlanner" /> calls this when <c>"CELL"</c>
///     is in <c>PlannerEnabledRecordTypes</c>.
/// </summary>
/// <remarks>
///     Tier 6.1 ships the orchestration with empty child arrays in each
///     <see cref="CellPlan" /> — the catalog runs, dispositions are decided, FormIDs
///     are allocated, and worldspace plans are built, but per-cell placed-ref / LAND /
///     NAVM record plans are populated in Tier 6.1b. <c>PlanCellSectionBuilder</c>
///     still emits cell GRUPs through legacy <see cref="CellGrupBuilder" /> framing,
///     just with the planner-side data shape replacing legacy bundles.
/// </remarks>
public sealed class CellSectionPlanner
{
    public static CellSectionResult Plan(
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyList<CellRecord> dmpCells,
        IReadOnlyList<NavMeshRecord> dmpNavmeshes,
        IReadOnlyList<WorldspaceRecord> dmpWorldspaces,
        IReadOnlySet<uint> masterFormIds,
        FormIdAllocator allocator,
        bool emitMasterCellNavmAugmentation = false,
        IReadOnlySet<uint>? masterRefFormIds = null,
        bool replaceCellTemporariesOnOverride = false,
        IReadOnlyDictionary<uint, uint>? masterRefToCell = null,
        IReadOnlyDictionary<uint, List<uint>>? masterLandsByCell = null)
    {
        ArgumentNullException.ThrowIfNull(masterContexts);
        ArgumentNullException.ThrowIfNull(masterRecordsByFormId);
        ArgumentNullException.ThrowIfNull(dmpCells);
        ArgumentNullException.ThrowIfNull(dmpNavmeshes);
        ArgumentNullException.ThrowIfNull(dmpWorldspaces);
        ArgumentNullException.ThrowIfNull(masterFormIds);
        ArgumentNullException.ThrowIfNull(allocator);

        var catalog = CellCatalog.Build(masterContexts, masterRecordsByFormId, dmpCells);
        if (catalog.Count == 0)
        {
            return Empty();
        }

        // Coord-based pairing. Catches proto cells at master coords carrying different FormIDs
        // (522 such collisions in WastelandNV alone). Without it both survive to emission and
        // the engine destroys one with a "Cell will be destroyed" warning. After reconciliation:
        // one DmpOverride per coord-match, keyed on master's FormID with proto placement data.
        catalog = CoordBasedCellPairingPass.Reconcile(
            catalog, masterRecordsByFormId, out var protoToMasterCellAlias);

        // Coord-paired proto cells re-keyed to master FormIDs — their NAVMs must follow the
        // alias or they orphan (never attach as children while NAVI rows still emit).
        uint EffectiveCellFormId(uint cellFormId)
            => protoToMasterCellAlias.TryGetValue(cellFormId, out var master) ? master : cellFormId;

        // Master-cell NAVM gate — rationale, engine RE, and why the Warning must be loud all
        // live on MasterCellNavmSuppression. Post-alias, so folded proto cells count as master.
        var navmSuppressionDiagnostics = ImmutableArray<PlanDiagnostic>.Empty;
        if (!emitMasterCellNavmAugmentation)
        {
            dmpNavmeshes = MasterCellNavmSuppression.Apply(
                dmpNavmeshes, masterContexts, EffectiveCellFormId, out navmSuppressionDiagnostics);
        }

        var dispositionEngine = new CellDispositionEngine([new DefaultCellDispositionPolicy()]);
        var decisions = dispositionEngine.Decide(catalog);
        var landPlanning = CellLandPlanner.DecideAll(catalog, masterRecordsByFormId, masterLandsByCell);
        var childAllocator = new CellChildAllocator(allocator);
        var allocations = childAllocator.AllocateAll(
            catalog, dmpNavmeshes, masterFormIds, landPlanning.DecisionsByCellSourceFormId);
        var worldspaceCatalog = WorldspaceCatalog.Build(catalog, dmpWorldspaces, masterFormIds);

        // Group NAVMs by (alias-resolved) parent cell once so per-cell walks are cheap.
        var navmsByCell = new Dictionary<uint, List<NavMeshRecord>>();
        foreach (var navm in dmpNavmeshes)
        {
            if (navm.CellFormId == 0)
            {
                continue;
            }

            var effectiveCellFormId = EffectiveCellFormId(navm.CellFormId);
            if (!navmsByCell.TryGetValue(effectiveCellFormId, out var list))
            {
                list = [];
                navmsByCell[effectiveCellFormId] = list;
            }

            list.Add(navm);
        }

        var (cells, duplicateChildDiagnostics) = BuildCellPlans(
            decisions, navmsByCell, landPlanning.DecisionsByCellSourceFormId,
            allocations, masterFormIds,
            masterRefFormIds, replaceCellTemporariesOnOverride);

        // Proto-authored duplicate placements of master actors fold onto master's sole ref
        // (move, never duplicate — 3× Arcade Gannon class). Before reparenting so converted
        // overrides ride the container routing.
        cells = DuplicateActorPlacementMerge.Apply(cells, masterRecordsByFormId);

        // Master doors the proto re-placed in NEW-worldspace cells clone as fresh NEW REFRs
        // (captured placement + teleport preserved; Overrides there die at the parent-cell
        // guard and the Strip loses every door). Runs BEFORE reparenting so persistent
        // door clones are re-homed to the container like any NEW persistent REFR.
        cells = OverrideDoorCloning.Apply(
            cells, masterContexts, masterRecordsByFormId, masterRefToCell, allocator,
            out var clonedDoorFormIds, out var doorDiagnostics);

        // Exterior persistents (NEW refs, persistent actors, map markers, rescued enable-parent
        // targets) move to the worldspace persistent-container cell — the loader never reads
        // Persistent-Children GRUPs under exterior grid cells (in-game proven; see
        // PersistentCellReparenting). The allocator lets the pass synthesize a NEW container
        // for proto worldspaces (e.g. TheStripWorld) without a master or captured one.
        cells = PersistentCellReparenting.Apply(
            cells, masterContexts, masterRecordsByFormId, out var reparentingDiagnostics,
            allocator, masterRefToCell);
        var (worldspaces, worldspaceSourceToEmitted) =
            BuildWorldspacePlans(worldspaceCatalog, allocator);

        var navmEntries = PlannedNavmEntryBuilder.Build(
            dmpCells, dmpNavmeshes, allocations.NavmSourceToEmitted,
            allocations.CellSourceToEmitted, worldspaceSourceToEmitted,
            masterContexts, emitMasterCellNavmAugmentation, protoToMasterCellAlias);

        return new CellSectionResult
        {
            CellsByFormId = cells,
            WorldspacesByFormId = worldspaces,
            NavmEntries = navmEntries,
            CellSourceToEmitted = allocations.CellSourceToEmitted,
            CellChildSourceToEmitted = allocations.PlacedRefSourceToEmitted,
            NavmSourceToEmitted = allocations.NavmSourceToEmitted,
            LandByCellSourceToEmitted = allocations.LandByCellSourceToEmitted,
            AdditionalEmittedFormIds = clonedDoorFormIds,
            WorldspaceSourceToEmitted = worldspaceSourceToEmitted,
            Diagnostics = landPlanning.Diagnostics
                .AddRange(doorDiagnostics)
                .AddRange(reparentingDiagnostics)
                .AddRange(navmSuppressionDiagnostics)
                .AddRange(duplicateChildDiagnostics),
        };
    }

    private static (ImmutableDictionary<uint, CellPlan> Cells, ImmutableArray<PlanDiagnostic> Diagnostics)
        BuildCellPlans(
        IReadOnlyList<(CellCatalogEntry Entry, Disposition.DispositionDecision Decision)> decisions,
        IReadOnlyDictionary<uint, List<NavMeshRecord>> navmsByCell,
        IReadOnlyDictionary<uint, CellLandDecision> landDecisions,
        CellChildAllocator.AllocationResult allocations,
        IReadOnlySet<uint> masterFormIds,
        IReadOnlySet<uint>? masterRefFormIds,
        bool replaceCellTemporariesOnOverride)
    {
        var cells = ImmutableDictionary.CreateBuilder<uint, CellPlan>();
        var duplicateDiagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();

        foreach (var (entry, decision) in decisions)
        {
            if (decision.Disposition == RecordDisposition.Skip)
            {
                continue;
            }

            var context = entry.MasterContext ?? CellContextSynthesizer.Synthesize(entry);
            var (mode, dropRenderCullingMarkers) = PlanMergeMode(
                entry, context, masterRefFormIds, replaceCellTemporariesOnOverride);
            var (persistent, vwd, temporary, duplicateChildDrops) =
                CellChildPlanBuilder.BuildChildPlans(
                    entry, navmsByCell, landDecisions, allocations, masterFormIds);
            if (duplicateChildDrops > 0)
            {
                duplicateDiagnostics.Add(new PlanDiagnostic
                {
                    Kind = PlanDiagnosticKind.Warning,
                    Phase = "Cells",
                    Code = "refr.intra-cell-duplicate-dropped",
                    RecordType = "CELL",
                    FormId = entry.CellFormId,
                    Message =
                        $"CELL 0x{entry.CellFormId:X8}: dropped {duplicateChildDrops:N0} duplicate " +
                        "placed-ref capture(s) that resolved to an already-planned FormID.",
                });
            }
            // DmpNew cells whose proto FormID isn't in master get a fresh plugin-range
            // FormID in Pass 0 of the allocator. Use that emitted ID as the cell's plan
            // key (== record FormID == GRUP label) so the engine sees the cell as new
            // content owned by our ESP rather than as an override of a master cell that
            // doesn't exist.
            var emittedCellFormId = allocations.CellSourceToEmitted.TryGetValue(
                entry.CellFormId, out var alloc)
                ? alloc
                : entry.CellFormId;

            cells.Add(emittedCellFormId, new CellPlan
            {
                CellFormId = emittedCellFormId,
                CellRecordPlan = new RecordPlan
                {
                    Type = "CELL",
                    Disposition = decision.Disposition,
                    FormId = emittedCellFormId,
                    SourceFormId = entry.DmpModel?.FormId,
                    Model = entry.DmpModel,
                    Master = entry.MasterRecord,
                    References = ImmutableArray<ResolvedRef>.Empty,
                    OverrideSubrecords = null,
                    ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                    Provenance = decision.Provenance,
                },
                Context = context,
                PersistentChildren = persistent,
                VwdChildren = vwd,
                TemporaryChildren = temporary,
                ParentWorldspaceFormId = context.WorldspaceFormId,
                Mode = mode,
                DropRenderCullingMarkers = dropRenderCullingMarkers,
            });
        }

        return (cells.ToImmutable(), duplicateDiagnostics.ToImmutable());
    }

    /// <summary>
    ///     Settle the cell's merge-mode + render-culling-marker policy at plan time. Master
    ///     cells classify via
    ///     <see cref="CellMerger.Classify" /> (any non-persistent master-resident capture ⇒
    ///     LoadedReplacement; persistent-only ⇒ PersistentOnly); brand-new cells emit all
    ///     their children unconditionally (LoadedReplacement semantics). Returns null Mode
    ///     when the master ref set wasn't supplied — the writer's transitional fallback
    ///     (<c>CellDecisionFallback</c>) then computes both.
    /// </summary>
    private static (CellMergeMode? Mode, bool DropRenderCullingMarkers) PlanMergeMode(
        CellCatalogEntry entry,
        PcEsmCellContext context,
        IReadOnlySet<uint>? masterRefFormIds,
        bool replaceCellTemporariesOnOverride)
    {
        if (masterRefFormIds is null)
        {
            return (null, false);
        }

        var isMasterAnchored = entry.MasterRecord is not null;
        var mode = (isMasterAnchored, entry.DmpModel) switch
        {
            (false, _) => CellMergeMode.LoadedReplacement,
            (true, null) => CellMergeMode.Skip,
            (true, { } dmpModel) => CellMerger.Classify(dmpModel, masterRefFormIds),
        };

        // Replaced interiors drop captured render-culling markers (their final-game bounds
        // mis-cull the proto layout) unless the diagnostic replace-temporaries mode is on.
        var dropRenderCullingMarkers = isMasterAnchored
            && context.IsInterior
            && mode == CellMergeMode.LoadedReplacement
            && !replaceCellTemporariesOnOverride;

        return (mode, dropRenderCullingMarkers);
    }

    private static (ImmutableDictionary<uint, WorldspacePlan> Plans,
        ImmutableDictionary<uint, uint> SourceToEmitted) BuildWorldspacePlans(
        IReadOnlyList<WorldspaceCatalog.WorldspaceCatalogEntry> worldspaceEntries,
        FormIdAllocator allocator)
    {
        var worldspaces = ImmutableDictionary.CreateBuilder<uint, WorldspacePlan>();
        var sourceToEmitted = ImmutableDictionary.CreateBuilder<uint, uint>();

        foreach (var entry in worldspaceEntries)
        {
            var disposition = entry.Source switch
            {
                WorldspaceCatalog.WorldspaceCatalogSource.MasterOnly => RecordDisposition.KeepMaster,
                WorldspaceCatalog.WorldspaceCatalogSource.DmpOverride => RecordDisposition.Override,
                WorldspaceCatalog.WorldspaceCatalogSource.DmpNew => RecordDisposition.New,
                _ => throw new InvalidOperationException($"Unknown WorldspaceCatalogSource: {entry.Source}"),
            };

            // DmpNew worldspaces get a plugin-range FormID up front so cell GRUPs can wrap
            // their child cells under the allocated anchor. KeepMaster / Override keep the
            // existing master FormID.
            var emitFormId = disposition == RecordDisposition.New
                ? allocator.Allocate()
                : entry.WorldspaceFormId;

            if (disposition == RecordDisposition.New)
            {
                sourceToEmitted[entry.WorldspaceFormId] = emitFormId;
            }

            worldspaces.Add(entry.WorldspaceFormId, new WorldspacePlan
            {
                WorldspaceFormId = emitFormId,
                WorldspaceRecordPlan = new RecordPlan
                {
                    Type = "WRLD",
                    Disposition = disposition,
                    FormId = emitFormId,
                    SourceFormId = entry.DmpModel?.FormId ?? entry.WorldspaceFormId,
                    Model = entry.DmpModel,
                    Master = null,
                    References = ImmutableArray<ResolvedRef>.Empty,
                    OverrideSubrecords = null,
                    ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                    Provenance = new PlanProvenance
                    {
                        PolicyId = "WorldspaceCatalog." + entry.Source,
                        Reason = $"Worldspace owns {entry.CellFormIds.Count} planned cell(s).",
                    },
                },
                CellFormIds = entry.CellFormIds.ToImmutableArray(),
            });
        }

        return (worldspaces.ToImmutable(), sourceToEmitted.ToImmutable());
    }

    private static CellSectionResult Empty() => new()
    {
        CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty,
        WorldspacesByFormId = ImmutableDictionary<uint, WorldspacePlan>.Empty,
        NavmEntries = ImmutableArray<PlannedNavmEntry>.Empty,
        CellChildSourceToEmitted = ImmutableDictionary<uint, uint>.Empty,
        NavmSourceToEmitted = ImmutableDictionary<uint, uint>.Empty,
        LandByCellSourceToEmitted = ImmutableDictionary<uint, uint>.Empty,
        AdditionalEmittedFormIds = ImmutableHashSet<uint>.Empty,
        WorldspaceSourceToEmitted = ImmutableDictionary<uint, uint>.Empty,
        Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
    };

    public sealed record CellSectionResult
    {
        public required ImmutableDictionary<uint, CellPlan> CellsByFormId { get; init; }
        public required ImmutableDictionary<uint, WorldspacePlan> WorldspacesByFormId { get; init; }
        public required ImmutableArray<PlannedNavmEntry> NavmEntries { get; init; }
        public required ImmutableDictionary<uint, uint> CellChildSourceToEmitted { get; init; }
        public required ImmutableDictionary<uint, uint> NavmSourceToEmitted { get; init; }
        public ImmutableDictionary<uint, uint> LandByCellSourceToEmitted { get; init; } =
            ImmutableDictionary<uint, uint>.Empty;
        /// <summary>Post-allocation child FormIDs that must enter the emitted-ID set.</summary>
        public ImmutableHashSet<uint> AdditionalEmittedFormIds { get; init; } =
            ImmutableHashSet<uint>.Empty;
        public ImmutableDictionary<uint, uint> WorldspaceSourceToEmitted { get; init; } =
            ImmutableDictionary<uint, uint>.Empty;
        public ImmutableDictionary<uint, uint> CellSourceToEmitted { get; init; } =
            ImmutableDictionary<uint, uint>.Empty;
        public ImmutableArray<PlanDiagnostic> Diagnostics { get; init; } =
            ImmutableArray<PlanDiagnostic>.Empty;
    }
}
