using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Retains each partial geometry upload before recording can make its range or staging GPU-visible.</summary>
/// <remarks>The caller retains this owner before acquisition and establishes retirement before disposal.
/// Successful range publication transfers only the range; staging and copy bookkeeping remain here.</remarks>
internal sealed class GpuGeometryUploadRetirement12 : IDisposable
{
    private readonly RetiredResourceDisposal _cleanup = new();
    private readonly Action<ByteArenaAllocation> _freeRange;
    private readonly Action<int> _completeCopy;
    private ByteArenaAllocation _allocation;
    private IDisposable? _staging;
    private bool _ownsAllocation;
    private bool _copyPending;
    private bool _uploadActive = true;

    /// <summary>Registers every possible cleanup action before any acquisition.</summary>
    /// <param name="freeRange">Returns a failed unpublished range, with retry support.</param>
    /// <param name="completeCopy">Returns the destination block's copy hold after retirement.</param>
    internal GpuGeometryUploadRetirement12(Action<ByteArenaAllocation> freeRange, Action<int> completeCopy)
    {
        _freeRange = freeRange;
        _completeCopy = completeCopy;
        _cleanup.Add(ReleaseStaging, "geometry upload staging");
        _cleanup.Add(ReleaseCopy, "geometry upload copy hold");
        _cleanup.Add(ReleaseAllocation, "unpublished geometry range");
    }

    /// <summary>Captures an allocated range without further allocation or callbacks.</summary>
    /// <param name="allocation">Exact newly allocated range.</param>
    internal void OwnAllocation(ByteArenaAllocation allocation)
    {
        _allocation = allocation;
        _ownsAllocation = true;
    }

    /// <summary>Captures staging immediately after creation or ring reservation.</summary>
    /// <param name="staging">Independent transient resource or exact ring release handle.</param>
    internal void OwnStaging(IDisposable staging) => _staging = staging;

    /// <summary>Records the arena's already-applied copy hold before the first native copy command.</summary>
    internal void OwnCopyHold() => _copyPending = true;

    /// <summary>Transfers only the range to an external acquisition owner or successfully published return value.</summary>
    internal void TransferAllocation() => _ownsAllocation = false;

    /// <summary>Ends acquisition before an explicit or deferred cleanup attempt.</summary>
    internal void EndUpload() => _uploadActive = false;

    /// <summary>Releases retired partial work; failed child actions remain independently retryable.</summary>
    /// <exception cref="InvalidOperationException">The upload has not finished acquiring or recording.</exception>
    public void Dispose()
    {
#pragma warning disable S3877 // Reentrant queue draining cannot release an upload still recording native commands.
        if (_uploadActive) throw new InvalidOperationException("Geometry upload retirement requires completed acquisition.");
#pragma warning restore S3877
        _cleanup.Dispose();
    }

    /// <summary>Releases the exact staging identity once, retaining it when disposal fails.</summary>
    private void ReleaseStaging()
    {
        _staging?.Dispose();
        _staging = null;
    }

    /// <summary>Balances the block hold once even when another retired child cannot release.</summary>
    private void ReleaseCopy()
    {
        if (!_copyPending) return;
        _completeCopy(_allocation.BlockIndex);
        _copyPending = false;
    }

    /// <summary>Returns only an unpublished range still owned by this failed upload.</summary>
    private void ReleaseAllocation()
    {
        if (!_ownsAllocation) return;
        _freeRange(_allocation);
        _ownsAllocation = false;
    }
}
