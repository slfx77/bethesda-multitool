using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Phase C for cells. Single deterministic pass over every new placed reference
///     (REFR/ACHR/ACRE not in master) and every new NAVM, allocating plugin-range
///     FormIDs upfront. Subsumes legacy <c>PreAllocateNewPlacedRefFormIds</c>
///     (<c>PluginBuilder.cs:1017</c>) and Phase A NAVM allocation
///     (<c>PluginBuilder.cs:2050-2087</c>).
/// </summary>
/// <remarks>
///     The legacy versions ran across the pipeline in two separate pre-passes; this
///     class collapses them into one. Output is two source→emitted maps consumed by
///     <see cref="EmitPlan.SourceToEmittedFormId" /> and by the per-record reference
///     resolver (NAVM NVEX cross-references must already see allocated FormIDs).
/// </remarks>
public sealed class CellChildAllocator
{
    private readonly FormIdAllocator _allocator;

    public CellChildAllocator(FormIdAllocator allocator)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    /// <summary>
    ///     Allocate FormIDs for every new placed ref and new NAVM. Inputs are dispositioned
    ///     cell entries (only DMP-Override and DMP-New cells contribute children) plus the
    ///     navmesh list. Existing master FormIDs are not allocated; they're already valid.
    /// </summary>
    public AllocationResult AllocateAll(
        IReadOnlyList<CellCatalogEntry> cellEntries,
        IReadOnlyList<NavMeshRecord> dmpNavmeshes,
        IReadOnlySet<uint> masterFormIds,
        IReadOnlyDictionary<uint, CellLandDecision>? landDecisions = null)
    {
        ArgumentNullException.ThrowIfNull(cellEntries);
        ArgumentNullException.ThrowIfNull(dmpNavmeshes);
        ArgumentNullException.ThrowIfNull(masterFormIds);

        var cellMap = ImmutableDictionary.CreateBuilder<uint, uint>();
        var placedRefMap = ImmutableDictionary.CreateBuilder<uint, uint>();
        var navmMap = ImmutableDictionary.CreateBuilder<uint, uint>();
        var landByCellMap = ImmutableDictionary.CreateBuilder<uint, uint>();

        // Pass 0: cells themselves. DmpNew cells (no master FormID match) frequently carry
        // proto-era FormIDs that LOOK like master overrides (low load-order byte) but
        // aren't actually in the master ESM — emitting them verbatim makes the engine
        // treat them as overrides of nonexistent master cells, then virtual-cell autogen
        // collides at the same grid coord and a real master cell gets destroyed,
        // orphaning its NAVMs. Allocate plugin-range FormIDs for these so the engine
        // sees them as new content owned by our ESP.
        foreach (var entry in cellEntries)
        {
            if (entry.Source != Catalog.SourceKind.DmpNew || entry.DmpModel is null)
            {
                continue;
            }

            if (!IsAllocatableCell(entry.CellFormId, masterFormIds))
            {
                continue;
            }

            if (cellMap.ContainsKey(entry.CellFormId))
            {
                continue;
            }

            cellMap[entry.CellFormId] = _allocator.Allocate();
        }

        // Pass 1: walk cells in catalog order, allocate placed-ref FormIDs.
        foreach (var entry in cellEntries)
        {
            if (entry.DmpModel is null)
            {
                continue;
            }

            foreach (var placed in entry.DmpModel.PlacedObjects)
            {
                if (!IsAllocatablePlacedRef(placed, masterFormIds))
                {
                    continue;
                }

                if (placedRefMap.ContainsKey(placed.FormId))
                {
                    continue; // Dedup across multi-snapshot unions.
                }

                placedRefMap[placed.FormId] = _allocator.Allocate();
            }
        }

        // Pass 2: walk navmeshes in DMP order, allocate per-cell.
        foreach (var navm in dmpNavmeshes)
        {
            if (!IsAllocatableNavm(navm, masterFormIds))
            {
                continue;
            }

            if (navmMap.ContainsKey(navm.FormId))
            {
                continue;
            }

            navmMap[navm.FormId] = _allocator.Allocate();
        }

        // Pass 3: LAND has no trustworthy source FormID in a runtime cell capture, so
        // allocate by source CELL identity in catalog order. Keeping this separate from
        // CellSourceToEmitted prevents a cell→LAND mapping from colliding with the
        // cell's own source→emitted mapping.
        if (landDecisions is not null)
        {
            foreach (var entry in cellEntries)
            {
                if (landDecisions.ContainsKey(entry.CellFormId)
                    && !landByCellMap.ContainsKey(entry.CellFormId))
                {
                    landByCellMap[entry.CellFormId] = _allocator.Allocate();
                }
            }
        }

        return new AllocationResult
        {
            CellSourceToEmitted = cellMap.ToImmutable(),
            PlacedRefSourceToEmitted = placedRefMap.ToImmutable(),
            NavmSourceToEmitted = navmMap.ToImmutable(),
            LandByCellSourceToEmitted = landByCellMap.ToImmutable(),
        };
    }

    /// <summary>Combined per-pass output.</summary>
    public sealed record AllocationResult
    {
        public ImmutableDictionary<uint, uint> CellSourceToEmitted { get; init; } =
            ImmutableDictionary<uint, uint>.Empty;
        public required ImmutableDictionary<uint, uint> PlacedRefSourceToEmitted { get; init; }
        public required ImmutableDictionary<uint, uint> NavmSourceToEmitted { get; init; }
        public ImmutableDictionary<uint, uint> LandByCellSourceToEmitted { get; init; } =
            ImmutableDictionary<uint, uint>.Empty;
    }

    /// <summary>
    ///     A cell needs a fresh plugin-range FormID when it's NOT in the master record set.
    ///     Unlike placed refs there is no "engine-fixed cell FormID" range (no equivalent of
    ///     player ref 0x14) — every cell is authored content, so any not-in-master FormID is
    ///     fair game for allocation. Zero is the sentinel for unresolved cell pointers.
    /// </summary>
    private static bool IsAllocatableCell(uint cellFormId, IReadOnlySet<uint> masterFormIds)
    {
        if (cellFormId == 0)
        {
            return false;
        }

        if (masterFormIds.Contains(cellFormId))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Engine-fixed REFR FormIDs that the FNV runtime hard-codes (the player ref and
    ///     a small handful of "default" objects). Master ESM does NOT define these — they
    ///     live in the engine — so the master-set check passes them through to this guard.
    ///     Allocating a new FormID for any of these would break engine state restoration
    ///     and combat targeting.
    ///
    ///     The previous implementation used a blanket <c>(formId &amp; 0xFF000000) == 0</c>
    ///     check, which skipped ALL master-prefix FormIDs — including proto-allocated REFRs
    ///     in the <c>0x0010BXXX</c> range that aren't actually in master. Those then emitted
    ///     verbatim as phantom-master overrides, same crash class as the cell-FormID bug
    ///     fixed in v60. The narrow allowlist preserves engine-fixed identity without
    ///     trapping legitimately-new placed refs.
    /// </summary>
    private static readonly HashSet<uint> EngineFixedPlacedRefs = new()
    {
        0x00000014u, // Player REFR
        0x00000018u, // Default weapon (fists / unarmed)
    };

    private static bool IsAllocatablePlacedRef(PlacedReference placed, IReadOnlySet<uint> masterFormIds)
    {
        // Subtype whitelist. REFR/ACHR/ACRE are the active set today; PGRE/PMIS/PARW/PBEA/
        // PCON/PHZD/PBAR are placed-object subtypes whose planner routing is incomplete
        // (PlannedPgreEncoder exists but no production caller). Including them in the
        // whitelist defensively means when routing lands the allocator already handles
        // the same phantom-master shape correctly — no follow-up edit here required.
        // Omitting a subtype here doesn't drop the record; it would preserve the source
        // FormID verbatim. For DmpNew records that emit through the planner with a
        // master-prefix proto FormID, that's the phantom-master crash class.
        if (placed.RecordType is not ("REFR" or "ACHR" or "ACRE"
                                      or "PGRE" or "PMIS" or "PARW" or "PBEA"
                                      or "PCON" or "PHZD" or "PBAR"))
        {
            return false;
        }

        if (placed.FormId == 0)
        {
            return false;
        }

        if (masterFormIds.Contains(placed.FormId))
        {
            return false; // Master-resident; existing FormID stands.
        }

        if (EngineFixedPlacedRefs.Contains(placed.FormId))
        {
            return false; // Engine-reserved singleton; keep identity.
        }

        return true;
    }

    /// <summary>
    ///     NAVMs don't have engine-fixed FormIDs (no equivalent of the player ref), so the
    ///     allocator only needs the master-set check. The asymmetry with
    ///     <see cref="IsAllocatablePlacedRef" /> is intentional — don't add a numeric-range
    ///     guard here without a concrete engine-reserved NAVM FormID to point at.
    /// </summary>
    private static bool IsAllocatableNavm(NavMeshRecord navm, IReadOnlySet<uint> masterFormIds)
    {
        if (navm.CellFormId == 0)
        {
            return false;
        }

        if (navm.RawSubrecords.Count == 0)
        {
            return false;
        }

        if (masterFormIds.Contains(navm.FormId))
        {
            return false;
        }

        return true;
    }
}
