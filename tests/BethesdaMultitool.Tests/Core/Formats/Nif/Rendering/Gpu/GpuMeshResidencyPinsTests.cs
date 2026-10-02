using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.Core.Lifetime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Exercises the actual recording pin collection with production mesh ownership and portable resources.</summary>
public sealed class GpuMeshResidencyPinsTests
{
    /// <summary>Recording ownership survives removal and closure without changing resource identity or multiplying placement pins.</summary>
    [Fact]
    public void RecordingClonesUniqueResourcesAfterRemovalAndRetainsThemAcrossCacheClosure()
    {
        using var queue = new GpuDeletionQueue12(2);
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(1024, 4);
        var probe = new GpuSubmissionProbe12();
        var entry = Prepare(cache, queue, "mesh", probe);
        entry.PublishPrepared();
        Assert.True(cache.TryAcquire("mesh", out var snapshot));
        using var pins = new GpuMeshResidencyPins12();
        cache.Remove("mesh");
        pins.Retain(snapshot!);
        pins.Retain(snapshot!);
        Assert.Equal(1, pins.Count);
        Assert.Throws<AggregateException>(cache.Dispose);
        using var secondRecording = new GpuMeshResidencyPins12();
        secondRecording.Retain(snapshot!);
        snapshot!.Dispose();
        pins.Dispose();
        Assert.Throws<InvalidOperationException>(entry.ReleaseAfterRetirement);
        Assert.Equal(0, probe.ReleaseAttempts);
        secondRecording.Dispose();
        entry.ReleaseAfterRetirement();
        Assert.Equal(1, probe.ReleaseAttempts);
        Assert.Equal(0, pins.Count);
        Assert.Equal(ResourceResidencyState.Released, entry.State);
    }

    /// <summary>A prepared resource can be retained before publication and is not freed by definite recording abandonment alone.</summary>
    [Fact]
    public void AbandonedPreparationRemainsOwnedUntilSnapshotAndRecordingReturnTheirPins()
    {
        using var queue = new GpuDeletionQueue12(2);
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(1024, 4);
        var probe = new GpuSubmissionProbe12();
        var entry = Prepare(cache, queue, "pending", probe);
        using var snapshot = entry.AcquirePreparedPin();
        using var pins = new GpuMeshResidencyPins12();
        pins.Retain(snapshot);
        entry.OnSubmissionOutcome(SubmissionOutcome.DefinitelyAbandoned);
        snapshot.Dispose();
        Assert.Throws<InvalidOperationException>(entry.ReleaseAfterRetirement);
        Assert.Equal(0, probe.ReleaseAttempts);
        pins.Dispose();
        entry.ReleaseAfterRetirement();
        Assert.Equal(1, probe.ReleaseAttempts);
        Assert.Throws<ObjectDisposedException>(() => pins.Retain(snapshot));
    }

    /// <summary>Wrong-thread retirement leaves the complete recording borrow reachable for its owner-thread retry.</summary>
    [Fact]
    public void CrossThreadDisposeDoesNotLoseRecordingOwnership()
    {
        using var queue = new GpuDeletionQueue12(2);
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(1024, 4);
        var probe = new GpuSubmissionProbe12();
        var entry = Prepare(cache, queue, "thread", probe);
        using var snapshot = entry.AcquirePreparedPin();
        using var pins = new GpuMeshResidencyPins12();
        pins.Retain(snapshot);
        Exception? failure = null;
        var worker = new Thread(() => { try { pins.Dispose(); } catch (Exception error) { failure = error; } });
        worker.Start();
        worker.Join();
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, pins.Count);
        snapshot.Dispose();
        Assert.Throws<InvalidOperationException>(entry.ReleaseAfterRetirement);
        pins.Dispose();
        entry.ReleaseAfterRetirement();
        Assert.Equal(1, probe.ReleaseAttempts);
    }

    /// <summary>Creates the actual mesh cleanup owner with one observable geometry release and no native device.</summary>
    private static ResourceResidencyEntry<string, GpuMeshResources12> Prepare(
        ResourceResidencyCache<string, GpuMeshResources12> cache, GpuDeletionQueue12 queue,
        string key, GpuSubmissionProbe12 probe)
    {
        var resources = GpuMeshResources12.Begin(queue, _ => probe.Dispose(), _ => { });
        resources.AcquireGeometry(() => default);
        resources.Commit();
        var entry = cache.Reserve(key, 64);
        entry.Attach(resources);
        entry.MarkPrepared();
        return entry;
    }
}
