namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Owns unpublished Shared families and route owners until factory construction commits.</summary>
/// <remarks>Rollback attempts every independent owner in reverse acquisition order and retains failed releases.
/// A throwing factory constructor still cannot publish this transaction for an external retry; it must report
/// cleanup failures together with the original error. Runtime retirement uses retained staged releases.</remarks>
internal sealed class ReferencePipelineConstructionTransaction12 : IDisposable
{
    private readonly List<IDisposable> _creationOrder = new(48);
    private readonly HashSet<IDisposable> _owned = new(48, ReferenceEqualityComparer.Instance);
    private bool _releasing;

    /// <summary>Reserves bookkeeping before the caller acquires native-dependent ownership.</summary>
    /// <param name="additionalCount">Maximum number of new independent owners to track without further growth.</param>
    /// <exception cref="ArgumentOutOfRangeException">The requested count is negative.</exception>
    /// <exception cref="OverflowException">The requested capacity exceeds a managed collection's index range.</exception>
    internal void Reserve(int additionalCount = 1)
    {
        VerifyNotReleasing();
        ArgumentOutOfRangeException.ThrowIfNegative(additionalCount);
        _creationOrder.EnsureCapacity(checked(_creationOrder.Count + additionalCount));
        _owned.EnsureCapacity(checked(_owned.Count + additionalCount));
    }

    /// <summary>Records one unpublished allocation, deduplicating aliases by reference identity.</summary>
    /// <param name="resource">A retained Shared family or managed route owner.</param>
    /// <exception cref="ArgumentNullException">The owner is null.</exception>
    internal void Track(IDisposable resource)
    {
        VerifyNotReleasing();
        ArgumentNullException.ThrowIfNull(resource);
        if (_owned.Contains(resource)) { return; }
        Reserve();
        _owned.Add(resource);
        _creationOrder.Add(resource);
    }

    /// <summary>Stops tracking an owner only after its caller has successfully released or transferred it.</summary>
    /// <param name="resource">Previously tracked owner whose release responsibility has ended.</param>
    internal void Forget(IDisposable resource)
    {
        VerifyNotReleasing();
        _owned.Remove(resource);
    }

    /// <summary>Transfers all completed resources to the factory's runtime fields without releasing them.</summary>
    internal void Commit()
    {
        VerifyNotReleasing();
        _owned.Clear();
        _creationOrder.Clear();
    }

    /// <summary>Attempts every independent rollback release and retains failures for a later retry.</summary>
    /// <exception cref="AggregateException">One or more retained owners could not be released.</exception>
    public void Dispose()
    {
        if (_releasing) { return; }
        _releasing = true;
        List<Exception>? failures = null;
        try
        {
            for (var index = _creationOrder.Count - 1; index >= 0; index--)
            {
                var resource = _creationOrder[index];
                try
                {
                    if (_owned.Contains(resource))
                    {
                        resource.Dispose();
                        _owned.Remove(resource);
                    }
                    _creationOrder.RemoveAt(index);
                }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
        }
        finally { _releasing = false; }
        if (failures is not null)
        {
#pragma warning disable S3877 // Failed rollback must report retained owners rather than claim successful cleanup.
            throw new AggregateException("Unpublished reference pipeline releases remain pending.", failures);
#pragma warning restore S3877
        }
    }

    /// <summary>Rejects callback mutations while reverse-order rollback is in progress.</summary>
    /// <exception cref="InvalidOperationException">Rollback is currently releasing an owner.</exception>
    private void VerifyNotReleasing()
    {
        if (_releasing)
        {
            throw new InvalidOperationException("Reference construction ownership cannot change during rollback.");
        }
    }
}
