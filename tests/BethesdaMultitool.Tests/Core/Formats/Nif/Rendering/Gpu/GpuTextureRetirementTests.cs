using System.Reflection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks texture/descriptor release ordering through the production deferred queue and Shared retry owner.</summary>
public sealed class GpuTextureRetirementTests
{
    /// <summary>A texture failure keeps its descriptor unavailable while unrelated retired textures still release.</summary>
    [Fact]
    public void DeferredTextureFailureBlocksItsSlotWithoutBlockingOtherBundles()
    {
        using var queue = new GpuDeletionQueue12(1);
        var texture = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var slot = new GpuSubmissionProbe12();
        var sibling = new GpuSubmissionProbe12();
        var retirement = new GpuTextureRetirement12(texture, slot.Dispose, queue, 65_536);
        retirement.Transfer();
        retirement.Transfer();
        queue.EnqueueDispose(sibling);
        Assert.True(retirement.IsTransferred);
        Assert.Equal(65_536, retirement.ResidentBytes);
        Assert.Throws<AggregateException>(queue.Tick);
        Assert.Equal(1, texture.ReleaseAttempts);
        Assert.Equal(0, slot.ReleaseAttempts);
        Assert.Equal(1, sibling.ReleaseAttempts);
        retirement.Transfer(); // The queue owns the failed child, not the cache token.
        Assert.Equal(1, queue.GetStats().QueueDepth);
        queue.Tick();
        Assert.Equal(2, texture.ReleaseAttempts);
        Assert.Equal(1, slot.ReleaseAttempts);
        Assert.Equal(0, queue.GetStats().QueueDepth);
    }

    /// <summary>A failed slot return retries only the descriptor after the texture was released successfully.</summary>
    [Fact]
    public void DescriptorFailureDoesNotRepeatSuccessfulTextureRelease()
    {
        using var queue = new GpuDeletionQueue12(1);
        var texture = new GpuSubmissionProbe12();
        var slot = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var retirement = new GpuTextureRetirement12(texture, slot.Dispose, queue, 65_536);
        retirement.Transfer();
        Assert.Throws<AggregateException>(queue.Tick);
        Assert.Equal(1, texture.ReleaseAttempts);
        Assert.Equal(1, slot.ReleaseAttempts);
        queue.Tick();
        Assert.Equal(1, texture.ReleaseAttempts);
        Assert.Equal(2, slot.ReleaseAttempts);
    }

    /// <summary>Queue rejection leaves both children in the cache's token for one later admission.</summary>
    [Fact]
    public void FailedQueueAdmissionRetainsTheCompleteBundle()
    {
        using var queue = new GpuDeletionQueue12(1);
        var texture = new GpuSubmissionProbe12();
        var slot = new GpuSubmissionProbe12();
        var retirement = new GpuTextureRetirement12(texture, slot.Dispose, queue, 65_536);
        SetFrameOrdinal(queue, ulong.MaxValue);
        Assert.Throws<OverflowException>(retirement.Transfer);
        Assert.False(retirement.IsTransferred);
        Assert.Equal(0, texture.ReleaseAttempts);
        Assert.Equal(0, slot.ReleaseAttempts);
        Assert.Equal(0, queue.GetStats().QueueDepth);
        SetFrameOrdinal(queue, 0);
        retirement.Transfer();
        retirement.Transfer();
        Assert.Equal(1, queue.GetStats().QueueDepth);
        queue.Tick();
        Assert.Equal(1, texture.ReleaseAttempts);
        Assert.Equal(1, slot.ReleaseAttempts);
    }

    /// <summary>Immediate cleanup retains partial progress both without a queue and after its caller-proven shutdown.</summary>
    /// <param name="stoppedQueue">Whether the synchronous path runs through an already stopped queue.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SynchronousReleaseFailureRetainsPartialProgress(bool stoppedQueue)
    {
        using var queue = new GpuDeletionQueue12(1);
        queue.Dispose();
        var texture = new GpuSubmissionProbe12();
        var slot = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var retirement = new GpuTextureRetirement12(texture, slot.Dispose, stoppedQueue ? queue : null, 65_536);
        Assert.Throws<AggregateException>(retirement.Transfer);
        Assert.False(retirement.IsTransferred);
        retirement.Transfer();
        retirement.Transfer();
        Assert.True(retirement.IsTransferred);
        Assert.Equal(1, texture.ReleaseAttempts);
        Assert.Equal(2, slot.ReleaseAttempts);
        Assert.Equal(0, queue.GetStats().QueueDepth);
    }

    /// <summary>Placeholders transfer only their slot and recursive callbacks cannot declare a partial bundle complete.</summary>
    [Fact]
    public void PlaceholderRetirementRejectsRecursiveTransferAndNeverOwnsItsFallback()
    {
        GpuTextureRetirement12? retirement = null;
        var releases = 0;
        retirement = new GpuTextureRetirement12(null, () =>
        {
            Assert.Throws<InvalidOperationException>(retirement!.Transfer);
            releases++;
        }, null, 0);
        retirement.Transfer();
        retirement.Transfer();
        Assert.Equal(0, retirement.ResidentBytes);
        Assert.Equal(1, releases);
        Assert.True(retirement.IsTransferred);
    }

    /// <summary>Positions an idle queue clock at a deterministic admission boundary without billions of ticks.</summary>
    /// <param name="queue">Production queue whose pending collection is empty.</param>
    /// <param name="ordinal">Monotonic frame position used by admission.</param>
    internal static void SetFrameOrdinal(GpuDeletionQueue12 queue, ulong ordinal)
    {
        var field = typeof(GpuDeletionQueue12).GetField("_frameOrdinal", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(queue, ordinal);
    }
}
