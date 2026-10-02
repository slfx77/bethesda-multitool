using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Retains unique mesh resources for one recording until its existing submission owner retires them.</summary>
/// <remarks>This collection establishes no GPU retirement proof. Register it with the command recorder
/// before adding borrows or recording their first use. It owns no frame timer or fence.</remarks>
internal sealed class GpuMeshResidencyPins12 : IDisposable
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly HashSet<GpuMeshResources12> _resources = new(ReferenceEqualityComparer.Instance);
    private readonly RetiredResourceDisposal _pins = new();
    private bool _disposed;

    public int Count => _resources.Count;

    /// <summary>Clones an exact live pin once per resource, including a source whose cache key was removed.</summary>
    /// <param name="source">Existing source borrow that remains caller-owned.</param>
    /// <exception cref="InvalidOperationException">Called outside the creating thread.</exception>
    /// <exception cref="ObjectDisposedException">This recording collection has started retirement.</exception>
    public void Retain(ResourceResidencyPin<GpuMeshResources12> source)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resource = source.Resource;
        if (_resources.Contains(resource)) { return; }
        _resources.EnsureCapacity(_resources.Count + 1);
        var pin = source.Retain();
        try { _pins.Add(pin, "recorded mesh residency"); }
        catch { pin.Dispose(); throw; }
        _resources.Add(resource);
    }

    /// <summary>Returns independent borrows after caller-proven retirement, retaining failed returns for retry.</summary>
    public void Dispose()
    {
        VerifyAccess();
        _disposed = true;
        _pins.Dispose();
        _resources.Clear();
    }

    /// <summary>Rejects cross-thread changes before any residency ownership can move.</summary>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Recorded mesh borrows belong to their creating thread.");
        }
    }
}
