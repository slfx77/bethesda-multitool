using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Allocation;

/// <summary>
///     The single FormID allocation site in the planner pipeline.
/// </summary>
/// <remarks>
///     Phase C — collapses the 7 legacy allocation sites in <c>PluginBuilder</c>
///     (lines 1049 / 1116 / 1831 / 1990 / 2137 / 2440 / 5155, plus the 3726–7 synthetic
///     DOOR+REFR rescue) into one deterministic pass. Walks <see cref="RecordDisposition.New" />
///     entries in <see cref="DeterministicAllocationOrder" /> and assigns each a fresh
///     plugin-range FormID. The output map is consumed by the writer; nothing else allocates.
/// </remarks>
public sealed class FormIdPlanner
{
    private readonly FormIdAllocator _allocator;

    public FormIdPlanner(FormIdAllocator allocator)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    /// <summary>The allocator's NextObjectId after this phase completes. Goes into TES4 HEDR.</summary>
    public uint NextObjectId => _allocator.NextObjectId;

    /// <summary>
    ///     Allocate FormIDs for every <see cref="RecordDisposition.New" /> entry. Returns
    ///     the source→emitted map for the writer to consult during reference resolution.
    /// </summary>
    public ImmutableDictionary<uint, uint> AllocateAll(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions)
    {
        var sortable = new List<(CatalogEntry Entry, DispositionDecision Decision)>();
        foreach (var item in decisions)
        {
            if (item.Decision.Disposition == RecordDisposition.New
                && item.Entry.DmpFormId is not null)
            {
                sortable.Add(item);
            }
        }

        sortable.Sort((a, b) => DeterministicAllocationOrder.Instance.Compare(a.Entry, b.Entry));

        var builder = ImmutableDictionary.CreateBuilder<uint, uint>();
        foreach (var (entry, _) in sortable)
        {
            var source = entry.DmpFormId!.Value;
            if (builder.ContainsKey(source))
            {
                continue; // Idempotent: duplicates in input collapse to one allocation.
            }

            builder[source] = _allocator.Allocate();
        }

        return builder.ToImmutable();
    }
}
