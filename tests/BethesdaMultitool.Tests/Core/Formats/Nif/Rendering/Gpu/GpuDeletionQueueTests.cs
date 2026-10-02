using System.Reflection;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks the frame policy around Shared cleanup without a native device or elapsed-time assumptions.</summary>
public sealed class GpuDeletionQueueTests
{
    /// <summary>A failed release remains visible and retries without delaying or repeating successful siblings.</summary>
    [Fact]
    public void FenceSynchronizedDelayRetainsFailuresAndReleasesIndependentSiblings()
    {
        using var queue = new GpuDeletionQueue12(2);
        var failing = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var sibling = new GpuSubmissionProbe12();
        queue.EnqueueDispose(failing);
        queue.EnqueueDispose(sibling);
        queue.Tick();
        Assert.Equal(0, failing.ReleaseAttempts);
        Assert.Equal(0, sibling.ReleaseAttempts);
        Assert.Equal(2, queue.GetStats().QueueDepth);
        Assert.Throws<AggregateException>(queue.Tick);
        Assert.Equal(1, failing.ReleaseAttempts);
        Assert.Equal(1, sibling.ReleaseAttempts);
        Assert.Equal(1, queue.GetStats().QueueDepth);
        queue.Tick();
        Assert.Equal(2, failing.ReleaseAttempts);
        Assert.Equal(1, sibling.ReleaseAttempts);
        Assert.Equal(0, queue.GetStats().QueueDepth);
    }

    /// <summary>A callback may enqueue a child, but cannot advance the frame clock or start shutdown recursively.</summary>
    [Fact]
    public void ReleaseCallbacksCannotShortenTheFrameDelay()
    {
        using var queue = new GpuDeletionQueue12(2);
        var child = new GpuSubmissionProbe12();
        var parent = new GpuSubmissionProbe12
        {
            OnRelease = () =>
            {
                Assert.Throws<InvalidOperationException>(queue.Tick);
                Assert.Throws<InvalidOperationException>(queue.Dispose);
                queue.EnqueueDispose(child);
            }
        };
        queue.EnqueueDispose(parent);
        queue.Tick();
        queue.Tick();
        Assert.Equal(2U, queue.CurrentFrame);
        Assert.Equal(1, parent.ReleaseAttempts);
        Assert.Equal(0, child.ReleaseAttempts);
        Assert.Equal(1, queue.GetStats().QueueDepth);
        queue.Tick();
        Assert.Equal(0, child.ReleaseAttempts);
        queue.Tick();
        Assert.Equal(1, child.ReleaseAttempts);
        Assert.Equal(0, queue.GetStats().QueueDepth);
    }

    /// <summary>Shutdown after caller-proven retirement retries failed children and preserves late synchronous release.</summary>
    [Fact]
    public void ShutdownRetriesPendingResourcesWithoutReleasingSuccessfulChildrenTwice()
    {
        using var queue = new GpuDeletionQueue12(3);
        var failing = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var sibling = new GpuSubmissionProbe12();
        queue.EnqueueDispose(failing);
        queue.EnqueueDispose(sibling);
        Assert.Throws<AggregateException>(queue.Dispose);
        Assert.Equal(1, queue.GetStats().QueueDepth);
        Assert.Throws<ObjectDisposedException>(queue.Tick);
        queue.Dispose();
        queue.Dispose();
        Assert.Equal(2, failing.ReleaseAttempts);
        Assert.Equal(1, sibling.ReleaseAttempts);
        Assert.Equal(0, queue.GetStats().QueueDepth);
        var late = new GpuSubmissionProbe12();
        queue.EnqueueDispose(late);
        Assert.Equal(1, late.ReleaseAttempts);
    }

    /// <summary>A failed shutdown remains registered until the successful retry records zero pending resources.</summary>
    [Fact]
    public void ShutdownUnregistersOnlyAfterEveryOwnedReleaseSucceeds()
    {
        var registry = new ResourceRegistry();
        using var queue = new GpuDeletionQueue12(2).RegisterWith(registry, "retirement-test");
        var failing = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var sibling = new GpuSubmissionProbe12();
        queue.EnqueueDispose(failing);
        queue.EnqueueDispose(sibling);
        Assert.Throws<AggregateException>(queue.Dispose);
        Assert.Equal(1, Assert.Single(registry.GetSnapshot()).Stats.QueueDepth);
        Assert.Empty(registry.GetRetiredSnapshot());

        queue.Dispose();
        Assert.Empty(registry.GetSnapshot());
        var retired = Assert.Single(registry.GetRetiredSnapshot());
        Assert.Equal("GpuDeletionQueue12[retirement-test]", retired.DisplayName);
        Assert.Equal(0, retired.Stats.QueueDepth);
        Assert.Equal(2, failing.ReleaseAttempts);
        Assert.Equal(1, sibling.ReleaseAttempts);
        queue.Dispose();
        Assert.Single(registry.GetRetiredSnapshot());
    }

    /// <summary>Mutation on a foreign thread cannot transfer ownership, advance retirement or stop the real owner.</summary>
    [Fact]
    public void ForeignThreadRejectionLeavesCallerAndQueueOwnershipIntact()
    {
        using var queue = new GpuDeletionQueue12(1);
        var retained = new GpuSubmissionProbe12();
        var rejected = new GpuSubmissionProbe12();
        queue.EnqueueDispose(retained);
        Exception? enqueueError = null;
        Exception? tickError = null;
        Exception? disposeError = null;
        var worker = new Thread(() =>
        {
            enqueueError = Record.Exception(() => queue.EnqueueDispose(rejected));
            tickError = Record.Exception(queue.Tick);
            disposeError = Record.Exception(queue.Dispose);
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "Foreign-thread queue checks did not finish.");
        Assert.IsType<InvalidOperationException>(enqueueError);
        Assert.IsType<InvalidOperationException>(tickError);
        Assert.IsType<InvalidOperationException>(disposeError);
        Assert.Equal(0U, queue.CurrentFrame);
        Assert.Equal(1, queue.GetStats().QueueDepth);
        queue.Tick();
        Assert.Equal(1, retained.ReleaseAttempts);
        Assert.Equal(0, rejected.ReleaseAttempts);
        rejected.Dispose();
    }

    /// <summary>The public diagnostic counter may wrap without making an in-flight resource prematurely eligible.</summary>
    [Fact]
    public void DiagnosticCounterWrapDoesNotShortenRetirement()
    {
        using var queue = new GpuDeletionQueue12(2);
        // Fast-forward an otherwise empty clock instead of executing four billion synthetic frames.
        var clock = typeof(GpuDeletionQueue12).GetField("_frameOrdinal", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(clock);
        clock.SetValue(queue, (ulong)uint.MaxValue - 1);
        var resource = new GpuSubmissionProbe12();
        queue.EnqueueDispose(resource);
        queue.Tick();
        Assert.Equal(uint.MaxValue, queue.CurrentFrame);
        Assert.Equal(0, resource.ReleaseAttempts);
        queue.Tick();
        Assert.Equal(0U, queue.CurrentFrame);
        Assert.Equal(1, resource.ReleaseAttempts);
    }

    /// <summary>Invalid admission cannot create a queue with an unsafe delay or an ownerless entry.</summary>
    [Fact]
    public void InvalidArgumentsFailBeforeOwnershipTransfer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuDeletionQueue12(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuDeletionQueue12(-1));
        using var queue = new GpuDeletionQueue12(2);
        Assert.Throws<ArgumentNullException>(() => queue.EnqueueDispose(null!));
        Assert.Equal(0, queue.GetStats().QueueDepth);
    }
}
