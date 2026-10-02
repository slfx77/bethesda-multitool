using Slfx77.Multitool.Core.Lifetime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks the production materialization ledger without depending on a native device.</summary>
public sealed class GpuMeshResourcesTests
{
    /// <summary>An upload failure after range transfer retains cleanup, and its callback cannot acquire again later.</summary>
    [Fact]
    public void FailedProducerAfterRangeTransferKeepsTheExactAllocationUntilRetirement()
    {
        using var queue = new GpuDeletionQueue12(2);
        var expected = new GeometryAllocation12(new ByteArenaAllocator(256).Allocate(70), 0, 0, 64, 6);
        var releases = 0;
        var resources = GpuMeshResources12.Begin(queue, range =>
        {
            Assert.Equal(expected.Allocation, range.Allocation);
            releases++;
        }, _ => { });
        Action<GeometryAllocation12>? escaped = null;
        Assert.Throws<IOException>(() => resources.AcquireGeometry(retain =>
        {
            escaped = retain;
            retain(expected);
            throw new IOException("Producer failed after recording a copy.");
        }));
        Assert.Equal(0, releases);
        Assert.Throws<InvalidOperationException>(() => escaped!(expected));
        queue.Tick();
        Assert.Equal(0, releases);
        queue.Tick();
        Assert.Equal(1, releases);
        Assert.True(resources.IsReleased);
        resources.Dispose();
        Assert.Equal(1, releases);
    }

    /// <summary>Failed construction retains geometry and each aliased acquisition until retirement, then retries only failures.</summary>
    [Fact]
    public void FailedMaterializationRetainsAliasedReferencesAndIndependentReleaseProgress()
    {
        using var queue = new GpuDeletionQueue12(2);
        var texture = TextureIdentity();
        var geometry = new GpuSubmissionProbe12();
        var reference = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var resources = GpuMeshResources12.Begin(queue, _ => geometry.Dispose(), entry =>
        {
            Assert.Same(texture, entry);
            reference.Dispose();
        });
        resources.AcquireGeometry(() => default);
        resources.AcquireTexture(() => texture);
        resources.AcquireTexture(() => texture);
        Assert.Throws<InvalidOperationException>(() => resources.AcquireTexture(
            () => throw new InvalidOperationException("Texture preparation failed.")));
        queue.Tick();
        Assert.Equal(0, geometry.ReleaseAttempts);
        Assert.Equal(0, reference.ReleaseAttempts);
        Assert.Throws<AggregateException>(queue.Tick);
        Assert.False(resources.IsReleased);
        Assert.Equal(1, geometry.ReleaseAttempts);
        Assert.Equal(2, reference.ReleaseAttempts);
        queue.Tick();
        Assert.True(resources.IsReleased);
        Assert.Equal(1, geometry.ReleaseAttempts);
        Assert.Equal(3, reference.ReleaseAttempts);
        resources.Dispose();
        Assert.Equal(3, reference.ReleaseAttempts);
    }

    /// <summary>Committing disarms rollback; actual cleanup remains separately delayed and retryable.</summary>
    [Fact]
    public void CommittedMeshSurvivesRollbackTokenAndReleasesOnlyAfterItsOwnRetirement()
    {
        using var queue = new GpuDeletionQueue12(2);
        var geometry = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var reference = new GpuSubmissionProbe12();
        var resources = GpuMeshResources12.Begin(queue, _ => geometry.Dispose(), _ => reference.Dispose());
        resources.AcquireGeometry(() => default);
        resources.AcquireTexture(TextureIdentity);
        resources.Commit();
        Assert.Throws<InvalidOperationException>(resources.Commit);
        Assert.Throws<InvalidOperationException>(() => resources.AcquireTexture(TextureIdentity));
        queue.Tick();
        queue.Tick();
        Assert.Equal(0, geometry.ReleaseAttempts);
        Assert.Equal(0, reference.ReleaseAttempts);
        queue.EnqueueDispose(resources);
        queue.Tick();
        Assert.Equal(0, geometry.ReleaseAttempts);
        Assert.Throws<AggregateException>(queue.Tick);
        Assert.False(resources.IsReleased);
        Assert.Equal(1, reference.ReleaseAttempts);
        queue.Tick();
        Assert.True(resources.IsReleased);
        Assert.Equal(2, geometry.ReleaseAttempts);
        Assert.Equal(1, reference.ReleaseAttempts);
    }

    /// <summary>Rejected acquisition and queue admission never invent ownership or invoke native release.</summary>
    [Fact]
    public void FailedGeometryAndStoppedQueueDoNotReleaseUnacquiredResources()
    {
        using var queue = new GpuDeletionQueue12(1);
        var release = new GpuSubmissionProbe12();
        var resources = GpuMeshResources12.Begin(queue, _ => release.Dispose(), _ => release.Dispose());
        Assert.Throws<InvalidOperationException>(() => resources.AcquireGeometry(
            () => throw new InvalidOperationException("Upload rejected.")));
        Assert.Throws<InvalidOperationException>(resources.Commit);
        queue.Tick();
        Assert.True(resources.IsReleased);
        Assert.Equal(0, release.ReleaseAttempts);
        Assert.Throws<InvalidOperationException>(() => resources.AcquireGeometry(() => default));
        queue.Dispose();
        Assert.Throws<InvalidOperationException>(() => GpuMeshResources12.Begin(
            queue, _ => release.Dispose(), _ => release.Dispose()));
        Assert.Equal(0, release.ReleaseAttempts);
    }

    /// <summary>Foreign-thread and callback reentry cannot publish, retire or extend an in-progress acquisition.</summary>
    [Fact]
    public void ReentrantAndForeignThreadTransitionsPreserveTheOriginalOwner()
    {
        using var queue = new GpuDeletionQueue12(1);
        GpuMeshResources12? resources = null;
        var frees = 0;
        resources = GpuMeshResources12.Begin(queue, _ =>
        {
            Assert.Throws<InvalidOperationException>(resources!.Dispose);
            frees++;
        }, _ => { });
        resources.AcquireGeometry(() =>
        {
            Assert.Throws<InvalidOperationException>(resources.Commit);
            Assert.Throws<InvalidOperationException>(resources.Dispose);
            return default;
        });
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(resources.Commit)) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<InvalidOperationException>(error);
        queue.Tick();
        Assert.Equal(1, frees);
        Assert.True(resources.IsReleased);
    }

    /// <summary>Creates only a reference identity; native texture access is outside these managed lifetime cases.</summary>
    private static GpuTextureCache12.Entry TextureIdentity() => new(
        null!, new ShaderResourceViewDescription(), default, 7, default, default, false, "texture");
}
