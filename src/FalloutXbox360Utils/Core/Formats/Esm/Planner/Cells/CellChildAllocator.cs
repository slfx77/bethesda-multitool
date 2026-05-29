using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

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
        IReadOnlySet<uint> masterFormIds)
    {
        ArgumentNullException.ThrowIfNull(cellEntries);
        ArgumentNullException.ThrowIfNull(dmpNavmeshes);
        ArgumentNullException.ThrowIfNull(masterFormIds);

        var placedRefMap = ImmutableDictionary.CreateBuilder<uint, uint>();
        var navmMap = ImmutableDictionary.CreateBuilder<uint, uint>();

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

        return new AllocationResult
        {
            PlacedRefSourceToEmitted = placedRefMap.ToImmutable(),
            NavmSourceToEmitted = navmMap.ToImmutable(),
        };
    }

    /// <summary>Combined per-pass output.</summary>
    public sealed record AllocationResult
    {
        public required ImmutableDictionary<uint, uint> PlacedRefSourceToEmitted { get; init; }
        public required ImmutableDictionary<uint, uint> NavmSourceToEmitted { get; init; }
    }

    private static bool IsAllocatablePlacedRef(PlacedReference placed, IReadOnlySet<uint> masterFormIds)
    {
        if (placed.RecordType is not ("REFR" or "ACHR" or "ACRE"))
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

        // Runtime-state FormIDs (player ref 0x14, default weapon, etc.) keep their identity.
        if ((placed.FormId & 0xFF000000) == 0)
        {
            return false;
        }

        return true;
    }

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
