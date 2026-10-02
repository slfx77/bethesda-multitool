using Slfx77.Multitool.Core.Lifetime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks the production upload ticket's retained partial ownership without native failure injection.</summary>
public sealed class GpuGeometryUploadRetirementTests
{
    /// <summary>Failed recording retains range, staging and copy hold; cleanup retries only failed children after retirement.</summary>
    [Fact]
    public void PartialUploadRetainsAllChildrenAndRejectsPrematureRetirement()
    {
        using var queue = new GpuDeletionQueue12(1);
        var allocator = new ByteArenaAllocator(64);
        for (var block = 0; block < 3; block++) allocator.Allocate(64);
        allocator.Allocate(16);
        var range = allocator.Allocate(35);
        var rangeRelease = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var staging = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var copyReleases = 0;
        var ticket = new GpuGeometryUploadRetirement12(allocation =>
        {
            Assert.Equal(range, allocation);
            rangeRelease.Dispose();
        }, block =>
        {
            Assert.Equal(3, block);
            copyReleases++;
        });
        queue.EnqueueDispose(ticket);
        ticket.OwnAllocation(range);
        ticket.OwnStaging(staging);
        ticket.OwnCopyHold();
        Assert.Throws<AggregateException>(queue.Tick);
        Assert.Equal(0, staging.ReleaseAttempts);
        Assert.Equal(0, rangeRelease.ReleaseAttempts);
        ticket.EndUpload();
        Assert.Throws<AggregateException>(queue.Tick);
        Assert.Equal(1, staging.ReleaseAttempts);
        Assert.Equal(1, rangeRelease.ReleaseAttempts);
        Assert.Equal(1, copyReleases);
        Assert.Equal(1, queue.GetStats().QueueDepth);
        queue.Tick();
        Assert.Equal(2, staging.ReleaseAttempts);
        Assert.Equal(2, rangeRelease.ReleaseAttempts);
        Assert.Equal(1, copyReleases);
        Assert.Equal(0, queue.GetStats().QueueDepth);
    }

    /// <summary>External capture or successful publication transfers the range while the queue still retains copy-side resources.</summary>
    [Fact]
    public void PublishedRangeDoesNotEscapeOrRepeatStagingRetirement()
    {
        using var queue = new GpuDeletionQueue12(2);
        var rangeRelease = new GpuSubmissionProbe12();
        var staging = new GpuSubmissionProbe12();
        var copyReleases = 0;
        var ticket = new GpuGeometryUploadRetirement12(_ => rangeRelease.Dispose(), _ => copyReleases++);
        queue.EnqueueDispose(ticket);
        ticket.OwnAllocation(new ByteArenaAllocator(256).Allocate(35));
        ticket.TransferAllocation(); // A caller capture stays responsible even if recording then fails.
        ticket.OwnStaging(staging);
        ticket.OwnCopyHold();
        ticket.EndUpload();
        queue.Tick();
        Assert.Equal(0, staging.ReleaseAttempts);
        queue.Tick();
        Assert.Equal(0, rangeRelease.ReleaseAttempts);
        Assert.Equal(1, staging.ReleaseAttempts);
        Assert.Equal(1, copyReleases);
        ticket.Dispose();
        Assert.Equal(1, staging.ReleaseAttempts);
        Assert.Equal(1, copyReleases);
    }

    /// <summary>Backing failure returns an unpublished range without inventing staging or a copy hold.</summary>
    [Fact]
    public void FailureBeforeCopyReleasesOnlyAcquiredOwnership()
    {
        using var queue = new GpuDeletionQueue12(1);
        var rangeRelease = new GpuSubmissionProbe12();
        var copies = 0;
        var ticket = new GpuGeometryUploadRetirement12(_ => rangeRelease.Dispose(), _ => copies++);
        queue.EnqueueDispose(ticket);
        ticket.OwnAllocation(new ByteArenaAllocator(256).Allocate(35));
        ticket.EndUpload();
        queue.Tick();
        Assert.Equal(1, rangeRelease.ReleaseAttempts);
        Assert.Equal(0, copies);
        Assert.Equal(0, queue.GetStats().QueueDepth);
    }
}
