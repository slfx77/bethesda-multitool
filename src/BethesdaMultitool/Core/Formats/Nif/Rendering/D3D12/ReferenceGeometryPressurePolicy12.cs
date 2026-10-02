using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Resources;
using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Requests one resident eviction at a time until physical arena backing can be admitted.</summary>
/// <remarks>The retained entry identifies completion, not ownership. Its existing retirement owner still
/// supplies GPU proof and returns its bytes. A reused key or an unrelated failed candidate cannot complete it.</remarks>
internal sealed class ReferenceGeometryPressurePolicy12
{
    private ResourceResidencyEntry<string, GpuMeshResources12>? _pendingVictim;

    /// <summary>Evicts the oldest eligible resident only after the previous exact victim has actually released.</summary>
    /// <typeparam name="TNode">Existing cache node whose identity and recency remain application-owned.</typeparam>
    /// <param name="requestedBytes">Device-measured charge for the rejected complete arena block.</param>
    /// <param name="maximumBytes">Fixed physical ceiling; requests above it cannot benefit from eviction.</param>
    /// <param name="meshes">Existing LRU with positive attributed sizes for resident meshes.</param>
    /// <param name="demandedNode">Current demand node, which must remain available for retry.</param>
    /// <param name="getResidency">Returns the node's exact entry without changing cache state.</param>
    /// <returns>Whether one resident eviction was requested; removal does not imply physical bytes are free.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either byte count is not positive.</exception>
    /// <exception cref="ArgumentNullException">A cache, node or entry selector is null.</exception>
    internal bool TryRequestEviction<TNode>(long requestedBytes, long maximumBytes,
        LruCache<string, TNode> meshes, TNode demandedNode,
        Func<TNode, ResourceResidencyEntry<string, GpuMeshResources12>?> getResidency)
        where TNode : class
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(demandedNode);
        ArgumentNullException.ThrowIfNull(getResidency);
        if (requestedBytes > maximumBytes ||
            _pendingVictim is { State: not ResourceResidencyState.Released })
            return false;

        _pendingVictim = null;
        // Physical fragmentation can reject a block while attributed LRU bytes remain well below
        // the logical cap. Ask for one positive resident range, not a physical-byte trim target.
        meshes.TrimToBytes(Math.Max(0, meshes.EstimatedBytes - 1), (_, candidate) =>
        {
            if (ReferenceEquals(candidate, demandedNode) || _pendingVictim is not null ||
                getResidency(candidate) is not { State: ResourceResidencyState.Resident } entry)
                return true;
            _pendingVictim = entry;
            return false;
        });
        return _pendingVictim is not null;
    }
}
