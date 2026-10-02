using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Resources;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;
using Slfx77.Multitool.Core.Lifetime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Exercises physical-pressure selection against the production LRU, exact residency entries and mesh cleanup.</summary>
public sealed class ReferenceGeometryPressurePolicyTests
{
    /// <summary>Physical fragmentation requests one eligible resident even when attributed bytes are far below the limit.</summary>
    [Fact]
    public void FragmentedBackingEvictsOldestResidentAndProtectsDemandAndPreparation()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(4096, 16);
        using var lru = CreateLru(cache);
        var demanded = AddResident(cache, lru, "demanded");
        var prepared = AddResident(cache, lru, "prepared", publish: false);
        var oldest = AddResident(cache, lru, "oldest");
        var newest = AddResident(cache, lru, "newest");
        var policy = new ReferenceGeometryPressurePolicy12();

        Assert.True(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));

        Assert.True(lru.ContainsKey(demanded.Key));
        Assert.True(lru.ContainsKey(prepared.Key));
        Assert.False(lru.ContainsKey(oldest.Key));
        Assert.True(lru.ContainsKey(newest.Key));
        Assert.Equal(ResourceResidencyState.Retiring, oldest.State);
        Assert.Equal(256, cache.TotalBytes);
        Assert.False(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
    }

    /// <summary>Pending CPU pins and failed release keep the exact victim gate closed without evicting every other mesh.</summary>
    [Fact]
    public void PinsAndFailedCleanupDelayFurtherEvictionUntilActualRelease()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(4096, 16);
        using var lru = CreateLru(cache);
        var probe = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var first = AddResident(cache, lru, "first", probe);
        var second = AddResident(cache, lru, "second");
        var demanded = AddResident(cache, lru, "demanded");
        var policy = new ReferenceGeometryPressurePolicy12();
        Assert.True(cache.TryAcquire(first.Key, out var pin));
        using var retained = pin!;

        Assert.True(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
        Assert.Throws<InvalidOperationException>(first.ReleaseAfterRetirement);
        Assert.False(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
        retained.Dispose();
        Assert.Throws<AggregateException>(first.ReleaseAfterRetirement);
        Assert.Equal(1, probe.ReleaseAttempts);
        Assert.False(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
        Assert.True(lru.ContainsKey(second.Key));
        first.ReleaseAfterRetirement();

        Assert.Equal(2, probe.ReleaseAttempts);
        Assert.True(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
        Assert.Equal(ResourceResidencyState.Retiring, second.State);
    }

    /// <summary>Reusing and releasing the victim's key cannot complete retirement of the earlier exact entry.</summary>
    [Fact]
    public void SameKeyReplacementCannotUnlockAnEarlierVictim()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(4096, 16);
        using var lru = CreateLru(cache);
        var original = AddResident(cache, lru, "reused");
        var next = AddResident(cache, lru, "next");
        var demanded = AddResident(cache, lru, "demanded");
        var policy = new ReferenceGeometryPressurePolicy12();
        Assert.True(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
        var replacement = AddResident(cache, lru, "reused");
        lru.Evict(replacement.Key);
        replacement.ReleaseAfterRetirement();

        Assert.NotEqual(original.Generation, replacement.Generation);
        Assert.Equal(ResourceResidencyState.Retiring, original.State);
        Assert.False(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
        Assert.True(lru.ContainsKey(next.Key));
        original.ReleaseAfterRetirement();
        Assert.True(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
        Assert.Equal(ResourceResidencyState.Retiring, next.State);
    }

    /// <summary>A denied candidate already retiring does not prevent the first real resident from relieving physical pressure.</summary>
    [Fact]
    public void UnrelatedCandidateRetirementNeitherBlocksNorCompletesVictimRetirement()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(4096, 16);
        using var lru = CreateLru(cache);
        var victim = AddResident(cache, lru, "victim");
        var demanded = AddResident(cache, lru, "demanded");
        var failedCandidate = cache.Reserve("denied", 64);
        cache.Remove(failedCandidate.Key);
        var policy = new ReferenceGeometryPressurePolicy12();

        Assert.Equal(64, cache.RetiringBytes);
        Assert.True(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
        failedCandidate.ReleaseAfterRetirement();
        Assert.False(policy.TryRequestEviction(1024, 4096, lru, demanded, static entry => entry));
        Assert.Equal(ResourceResidencyState.Retiring, victim.State);
        Assert.True(lru.ContainsKey(demanded.Key));
    }

    /// <summary>A block larger than the entire physical budget never evicts meshes that cannot make it fit.</summary>
    [Fact]
    public void ImpossibleRequestLeavesResidentsAndFuturePressureRecoveryIntact()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(4096, 16);
        using var lru = CreateLru(cache);
        var victim = AddResident(cache, lru, "victim");
        var demanded = AddResident(cache, lru, "demanded");
        var policy = new ReferenceGeometryPressurePolicy12();

        Assert.False(policy.TryRequestEviction(4097, 4096, lru, demanded, static entry => entry));
        Assert.True(lru.ContainsKey(victim.Key));
        Assert.Equal(ResourceResidencyState.Resident, victim.State);
        Assert.True(policy.TryRequestEviction(4096, 4096, lru, demanded, static entry => entry));
        victim.ReleaseAfterRetirement();
        Assert.False(policy.TryRequestEviction(4096, 4096, lru, demanded, static entry => entry));
        Assert.True(lru.ContainsKey(demanded.Key));
    }

    /// <summary>Creates recency storage whose eviction retires entries without pretending that their resources have released.</summary>
    /// <param name="cache">Exact residency owner retained for later proven retirement.</param>
    /// <returns>An LRU charging only resident meshes, as the production cache does after publication.</returns>
    private static LruCache<string, ResourceResidencyEntry<string, GpuMeshResources12>> CreateLru(
        ResourceResidencyCache<string, GpuMeshResources12> cache) =>
        new("physical-pressure-test", ResourceCategory.GpuAttributed,
            sizeOf: static (_, entry) => entry.State == ResourceResidencyState.Resident ? entry.AllocationBytes : 0,
            onEvicted: (key, _) => cache.Remove(key));

    /// <summary>Creates production geometry ownership without native allocation and adds its exact entry to recency storage.</summary>
    /// <param name="cache">Residency owner for the test.</param>
    /// <param name="lru">Recency storage under test.</param>
    /// <param name="key">Source-local resource identity.</param>
    /// <param name="probe">Optional release failure and observation probe.</param>
    /// <param name="publish">Whether to publish; false leaves a prepared candidate that must be skipped.</param>
    /// <returns>The exact prepared or resident entry.</returns>
    private static ResourceResidencyEntry<string, GpuMeshResources12> AddResident(
        ResourceResidencyCache<string, GpuMeshResources12> cache,
        LruCache<string, ResourceResidencyEntry<string, GpuMeshResources12>> lru,
        string key, GpuSubmissionProbe12? probe = null, bool publish = true)
    {
        var resources = GpuMeshResources12.CreateForResidency(_ => probe?.Dispose(), _ => { });
        resources.AcquireGeometry(() => default);
        resources.Commit();
        var entry = cache.Reserve(key, 64);
        entry.Attach(resources);
        entry.MarkPrepared();
        if (publish) entry.PublishPrepared();
        lru.Set(key, entry);
        return entry;
    }
}
