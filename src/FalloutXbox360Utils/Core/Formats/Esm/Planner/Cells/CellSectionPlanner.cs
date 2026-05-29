using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells.Policies;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

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
///     NAVM record plans are populated in Tier 6.1b. <see cref="PlanCellSectionBuilder" />
///     still emits cell GRUPs through legacy <see cref="CellGrupBuilder" /> framing,
///     just with the planner-side data shape replacing legacy bundles.
/// </remarks>
public sealed class CellSectionPlanner
{
    public CellSectionResult Plan(
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyList<CellRecord> dmpCells,
        IReadOnlyList<NavMeshRecord> dmpNavmeshes,
        IReadOnlyList<WorldspaceRecord> dmpWorldspaces,
        IReadOnlySet<uint> masterFormIds,
        FormIdAllocator allocator)
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

        var dispositionEngine = new CellDispositionEngine([new DefaultCellDispositionPolicy()]);
        var decisions = dispositionEngine.Decide(catalog);
        var childAllocator = new CellChildAllocator(allocator);
        var allocations = childAllocator.AllocateAll(catalog, dmpNavmeshes, masterFormIds);
        var worldspaceCatalog = WorldspaceCatalog.Build(catalog, dmpWorldspaces, masterFormIds);

        var cells = BuildCellPlans(decisions);
        var worldspaces = BuildWorldspacePlans(worldspaceCatalog);

        return new CellSectionResult
        {
            CellsByFormId = cells,
            WorldspacesByFormId = worldspaces,
            NavmEntries = ImmutableArray<PlannedNavmEntry>.Empty,
            CellChildSourceToEmitted = allocations.PlacedRefSourceToEmitted,
            NavmSourceToEmitted = allocations.NavmSourceToEmitted,
        };
    }

    private static ImmutableDictionary<uint, CellPlan> BuildCellPlans(
        IReadOnlyList<(CellCatalogEntry Entry, Disposition.DispositionDecision Decision)> decisions)
    {
        var cells = ImmutableDictionary.CreateBuilder<uint, CellPlan>();

        foreach (var (entry, decision) in decisions)
        {
            if (decision.Disposition == RecordDisposition.Skip)
            {
                continue;
            }

            var context = entry.MasterContext ?? SynthesizeContext(entry);
            cells.Add(entry.CellFormId, new CellPlan
            {
                CellFormId = entry.CellFormId,
                CellRecordPlan = new RecordPlan
                {
                    Type = "CELL",
                    Disposition = decision.Disposition,
                    FormId = entry.CellFormId,
                    SourceFormId = entry.DmpModel?.FormId,
                    Model = entry.DmpModel,
                    Master = entry.MasterRecord,
                    References = ImmutableArray<ResolvedRef>.Empty,
                    OverrideSubrecords = null,
                    ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                    Provenance = decision.Provenance,
                },
                Context = context,
                PersistentChildren = ImmutableArray<RecordPlan>.Empty,
                VwdChildren = ImmutableArray<RecordPlan>.Empty,
                TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
                ParentWorldspaceFormId = context.WorldspaceFormId,
            });
        }

        return cells.ToImmutable();
    }

    private static ImmutableDictionary<uint, WorldspacePlan> BuildWorldspacePlans(
        IReadOnlyList<WorldspaceCatalog.WorldspaceCatalogEntry> worldspaceEntries)
    {
        var worldspaces = ImmutableDictionary.CreateBuilder<uint, WorldspacePlan>();

        foreach (var entry in worldspaceEntries)
        {
            var disposition = entry.Source switch
            {
                WorldspaceCatalog.WorldspaceCatalogSource.MasterOnly => RecordDisposition.KeepMaster,
                WorldspaceCatalog.WorldspaceCatalogSource.DmpOverride => RecordDisposition.Override,
                WorldspaceCatalog.WorldspaceCatalogSource.DmpNew => RecordDisposition.New,
                _ => throw new InvalidOperationException($"Unknown WorldspaceCatalogSource: {entry.Source}"),
            };

            worldspaces.Add(entry.WorldspaceFormId, new WorldspacePlan
            {
                WorldspaceFormId = entry.WorldspaceFormId,
                WorldspaceRecordPlan = new RecordPlan
                {
                    Type = "WRLD",
                    Disposition = disposition,
                    FormId = entry.WorldspaceFormId,
                    SourceFormId = entry.DmpModel?.FormId,
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

        return worldspaces.ToImmutable();
    }

    /// <summary>
    ///     For DMP-new cells that don't have a master context, synthesize one from the
    ///     captured grid coords. The writer needs block / sub-block labels to nest the
    ///     cell under the right GRUP; without master context we compute them from grid.
    /// </summary>
    private static PcEsmCellContext SynthesizeContext(CellCatalogEntry entry)
    {
        var dmp = entry.DmpModel
            ?? throw new InvalidOperationException(
                $"CellCatalogEntry 0x{entry.CellFormId:X8} has neither master context nor DMP model.");

        var isInterior = !dmp.WorldspaceFormId.HasValue;
        return new PcEsmCellContext
        {
            CellFormId = entry.CellFormId,
            IsInterior = isInterior,
            WorldspaceFormId = dmp.WorldspaceFormId,
            BlockGroupType = isInterior ? 2 : 4,
            SubblockGroupType = isInterior ? 3 : 5,
            BlockLabel = null,
            SubblockLabel = null,
        };
    }

    private static CellSectionResult Empty() => new()
    {
        CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty,
        WorldspacesByFormId = ImmutableDictionary<uint, WorldspacePlan>.Empty,
        NavmEntries = ImmutableArray<PlannedNavmEntry>.Empty,
        CellChildSourceToEmitted = ImmutableDictionary<uint, uint>.Empty,
        NavmSourceToEmitted = ImmutableDictionary<uint, uint>.Empty,
    };

    public sealed record CellSectionResult
    {
        public required ImmutableDictionary<uint, CellPlan> CellsByFormId { get; init; }
        public required ImmutableDictionary<uint, WorldspacePlan> WorldspacesByFormId { get; init; }
        public required ImmutableArray<PlannedNavmEntry> NavmEntries { get; init; }
        public required ImmutableDictionary<uint, uint> CellChildSourceToEmitted { get; init; }
        public required ImmutableDictionary<uint, uint> NavmSourceToEmitted { get; init; }
    }
}
