using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.Core.Lifetime;
using Xunit;
using CellEntry = Slfx77.Multitool.Core.Lifetime.ResourceResidencyEntry<(int gx, int gy), BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTerrainCellResources12>;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Exercises terrain cache policy and exact ownership through the real submission and deletion queues.</summary>
public sealed class GpuTerrainCellCacheTests
{
    /// <summary>Completion of an earlier upload cannot release a published range still used by a later drawing frame.</summary>
    [Fact]
    public void EarlierUploadRetirementDoesNotBypassLaterDrawEvictionDelay()
    {
        var arena = new ByteArenaAllocator(256);
        var recording = new SubmissionResourceRetirement(8);
        using var queue = new GpuDeletionQueue12(2);
        var releases = 0;
        using var cache = new GpuTerrainCellCache12<string>(1, 80, geometry =>
        {
            releases++;
            arena.Free(geometry.Allocation);
        }, Lifetime(recording, queue), () => { });
        recording.BeginRecording();
        var entry = Prepare(cache, arena, (0, 0), "uploaded cell");
        recording.MarkSubmissionPossible();
        recording.ReportSubmission(SubmissionOutcome.Submitted);
        recording.AssociateUnfenced(1);
        Assert.Equal(ResourceResidencyState.Resident, entry.State);
        Assert.True(recording.HasPending);

        // A later recording borrows and then evicts the cell before the upload-frame holder drains.
        recording.BeginRecording();
        Assert.True(cache.TryGet((0, 0), out var mesh));
        Assert.Equal("uploaded cell", mesh);
        Assert.Equal(80L, cache.Evict((0, 0)));
        recording.MarkSubmissionPossible();
        recording.ReportSubmission(SubmissionOutcome.Submitted);
        recording.AssociateUnfenced(2);
        Assert.Equal(ResourceResidencyState.Retiring, entry.State);
        Assert.Equal(1, queue.GetStats().QueueDepth);

        recording.ReleaseCompleted(1);
        Assert.False(recording.HasPending);
        Assert.Equal(0, releases);
        Assert.Equal(ResourceResidencyState.Retiring, entry.State);
        Assert.Equal(80L, arena.AllocatedBytes);
        Assert.Equal(80L, cache.EstimatedBytes);
        queue.Tick();
        Assert.Equal(0, releases);
        Assert.Equal(80L, arena.AllocatedBytes);

        // Only the independent eviction hold covers the later draw's retirement.
        recording.ReleaseCompleted(2);
        queue.Tick();
        Assert.Equal(1, releases);
        Assert.Equal(ResourceResidencyState.Released, entry.State);
        Assert.Equal(0L, arena.AllocatedBytes);
        Assert.Equal(0L, cache.EstimatedBytes);
        Assert.Equal(0, queue.GetStats().QueueDepth);
    }

    /// <summary>Failed submission removes draw eligibility without treating uncertainty as completed GPU execution.</summary>
    /// <param name="outcome">A rejected or uncertain recording, neither of which can publish drawable residency.</param>
    [Theory]
    [InlineData(SubmissionOutcome.DefinitelyAbandoned)]
    [InlineData(SubmissionOutcome.SubmissionUncertain)]
    public void FailedSubmissionIsNeverDrawableAndWaitsForItsActualRetirement(SubmissionOutcome outcome)
    {
        var arena = new ByteArenaAllocator(256);
        var recording = new SubmissionResourceRetirement(8);
        using var queue = new GpuDeletionQueue12(2);
        var releases = 0;
        using var cache = new GpuTerrainCellCache12<string>(2, 160, geometry =>
        {
            releases++;
            arena.Free(geometry.Allocation);
        }, Lifetime(recording, queue), () => { });
        recording.BeginRecording();
        var entry = Prepare(cache, arena, (1, 2), "candidate");
        Assert.True(cache.TryGet((1, 2), out var prepared));
        Assert.Equal("candidate", prepared);
        if (outcome == SubmissionOutcome.DefinitelyAbandoned) recording.AbandonRecording();
        else
        {
            recording.MarkSubmissionPossible();
            recording.ReportSubmission(outcome);
        }
        Assert.Equal(ResourceResidencyState.Retiring, entry.State);
        Assert.False(cache.TryGet((1, 2), out var unavailable));
        Assert.Null(unavailable);
        Assert.False(cache.TryPeek((1, 2), out _));
        Assert.Equal(80L, cache.EstimatedBytes);
        Assert.Equal(80L, arena.AllocatedBytes);
        Assert.Equal(0, releases);
        if (outcome == SubmissionOutcome.SubmissionUncertain)
        {
            recording.ReleaseCompleted(100);
            Assert.True(recording.HasPending);
            Assert.Equal(80L, cache.EstimatedBytes);
            recording.AssociateUnfenced(10);
            recording.ReleaseCompleted(9);
            Assert.Equal(0, releases);
            recording.ReleaseCompleted(10);
        }
        else recording.ReleaseCompleted(0);
        Assert.Equal(ResourceResidencyState.Released, entry.State);
        Assert.False(recording.HasPending);
        Assert.Equal(0L, cache.EstimatedBytes);
        Assert.Equal(0L, arena.AllocatedBytes);
        queue.Dispose();
        Assert.Equal(1, releases);
    }

    /// <summary>A removed LRU entry retains its charge through failed queue admission and failed arena release.</summary>
    [Fact]
    public void FailedQueueTransferAndReleaseKeepChargeUntilSuccessfulRetry()
    {
        var arena = new ByteArenaAllocator(256);
        var recording = new SubmissionResourceRetirement(8);
        using var queue = new GpuDeletionQueue12(2);
        var transfers = 0;
        var releases = 0;
        var transferFailure = new IOException("Deletion queue admission failed.");
        var releaseFailure = new IOException("Arena release failed.");
        var lifetime = new GpuTerrainCellCacheLifetime12(
            resource => recording.Retain(resource, "terrain candidate"), recording.Enlist,
            resource =>
            {
                if (++transfers == 1) throw transferFailure;
                queue.EnqueueDispose(resource);
            });
        using var cache = new GpuTerrainCellCache12<string>(1, 80, geometry =>
        {
            if (++releases == 1) throw releaseFailure;
            arena.Free(geometry.Allocation);
        }, lifetime, () => { });
        recording.BeginRecording();
        var old = Prepare(cache, arena, (0, 0), "old cell");
        SubmitAndComplete(recording, 1);
        var failedTransfer = Assert.Throws<AggregateException>(() => cache.Evict((0, 0)));
        Assert.Contains(transferFailure, failedTransfer.Flatten().InnerExceptions);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryPeek((0, 0), out _));
        Assert.Equal(80L, cache.EstimatedBytes);
        Assert.Equal(0, releases);
        Assert.Equal(0, queue.GetStats().QueueDepth);

        recording.BeginRecording();
        Assert.False(cache.TryReserve((1, 0), 80, out _));
        Assert.Equal(2, transfers);
        Assert.Equal(1, queue.GetStats().QueueDepth);
        queue.Tick();
        Assert.Equal(0, releases);
        var failedRelease = Assert.Throws<AggregateException>(queue.Tick);
        Assert.Contains(releaseFailure, failedRelease.Flatten().InnerExceptions);
        Assert.Equal(80L, cache.EstimatedBytes);
        Assert.Equal(80L, arena.AllocatedBytes);
        Assert.Equal(1, queue.GetStats().QueueDepth);
        Assert.False(cache.TryReserve((1, 0), 80, out _));
        queue.Tick();
        Assert.Equal(ResourceResidencyState.Released, old.State);
        Assert.Equal(0L, cache.EstimatedBytes);
        Assert.Equal(0L, arena.AllocatedBytes);
        Assert.Equal(0, queue.GetStats().QueueDepth);
        Assert.True(cache.TryReserve((1, 0), 80, out var replacement));
        cache.Abort(replacement!);
        recording.AbandonRecording();
        recording.ReleaseCompleted(1);
        Assert.Equal(0L, cache.EstimatedBytes);
        Assert.Equal(2, releases);
    }

    /// <summary>Old delayed returns cannot remove a replacement entry or an equal key in another world generation.</summary>
    [Fact]
    public void ReusedKeysAndIndependentWorldCachesRetainExactOwners()
    {
        var arena = new ByteArenaAllocator(512);
        var recording = new SubmissionResourceRetirement(16);
        using var queue = new GpuDeletionQueue12(2);
        var releasedTags = new List<string?>();
        Action<TerrainAllocation12> free = geometry =>
        {
            releasedTags.Add(geometry.DebugTag);
            arena.Free(geometry.Allocation);
        };
        using var firstWorld = new GpuTerrainCellCache12<string>(3, 240, free,
            Lifetime(recording, queue), () => { });
        using var secondWorld = new GpuTerrainCellCache12<string>(3, 240, free,
            Lifetime(recording, queue), () => { });
        recording.BeginRecording();
        var old = Prepare(firstWorld, arena, (4, 5), "old world cell");
        SubmitAndComplete(recording, 1);
        Assert.Equal(80L, firstWorld.Evict((4, 5)));
        recording.BeginRecording();
        var replacement = Prepare(firstWorld, arena, (4, 5), "same-key replacement");
        var otherWorld = Prepare(secondWorld, arena, (4, 5), "independent world");
        Assert.Throws<InvalidOperationException>(() => firstWorld.Abort(old));
        Assert.Throws<InvalidOperationException>(() => secondWorld.Abort(replacement));
        SubmitAndComplete(recording, 2);
        queue.Tick();
        Assert.Empty(releasedTags);
        queue.Tick();
        Assert.Equal("old world cell", Assert.Single(releasedTags));
        Assert.Equal(ResourceResidencyState.Released, old.State);
        Assert.Equal(ResourceResidencyState.Resident, replacement.State);
        Assert.Equal(ResourceResidencyState.Resident, otherWorld.State);
        Assert.True(firstWorld.TryGet((4, 5), out var first));
        Assert.Equal("same-key replacement", first);
        Assert.True(secondWorld.TryGet((4, 5), out var second));
        Assert.Equal("independent world", second);
        Assert.Equal(80L, firstWorld.EstimatedBytes);
        Assert.Equal(80L, secondWorld.EstimatedBytes);
        firstWorld.Dispose();
        Assert.Throws<ObjectDisposedException>(() => firstWorld.TryGet((4, 5), out _));
        Assert.True(secondWorld.TryPeek((4, 5), out _));
        queue.Tick();
        queue.Tick();
        Assert.Equal(0L, firstWorld.EstimatedBytes);
        Assert.Equal(80L, secondWorld.EstimatedBytes);
        Assert.Equal(80L, arena.AllocatedBytes);
        secondWorld.ReleaseAfterRetirement();
        queue.Dispose();
        Assert.Equal(0L, arena.AllocatedBytes);
        Assert.Equal(3, releasedTags.Count);
    }

    /// <summary>Peek leaves eviction priority unchanged; logical removal alone never creates room for a new upload.</summary>
    /// <param name="touch">Whether the first entry is genuinely drawn instead of only inspected by a secondary pass.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookupRecencyAndPendingRetirementControlBackpressure(bool touch)
    {
        var arena = new ByteArenaAllocator(512);
        var recording = new SubmissionResourceRetirement(16);
        using var queue = new GpuDeletionQueue12(2);
        using var cache = new GpuTerrainCellCache12<string>(2, 160,
            geometry => arena.Free(geometry.Allocation), Lifetime(recording, queue), () => { });
        recording.BeginRecording();
        Prepare(cache, arena, (0, 0), "first");
        Prepare(cache, arena, (1, 0), "second");
        SubmitAndComplete(recording, 1);
        Assert.True(touch ? cache.TryGet((0, 0), out _) : cache.TryPeek((0, 0), out _));
        recording.BeginRecording();
        Assert.False(cache.TryReserve((2, 0), 80, out _));
        Assert.Equal(touch, cache.ContainsKey((0, 0)));
        Assert.Equal(!touch, cache.ContainsKey((1, 0)));
        Assert.Equal(1, cache.Count);
        Assert.Equal(160L, cache.EstimatedBytes);
        Assert.Equal(160L, arena.AllocatedBytes);
        queue.Tick();
        Assert.Equal(160L, cache.EstimatedBytes);
        Assert.False(cache.TryReserve((2, 0), 80, out _));
        Assert.Equal(1, cache.Count);
        Assert.Equal(touch, cache.ContainsKey((0, 0)));
        Assert.Equal(!touch, cache.ContainsKey((1, 0)));
        queue.Tick();
        Assert.Equal(80L, cache.EstimatedBytes);
        Prepare(cache, arena, (2, 0), "new upload");
        SubmitAndComplete(recording, 2);
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet((2, 0), out var latest));
        Assert.Equal("new upload", latest);
    }

    /// <summary>Ordinary disposal only queues ownership; explicit retired shutdown retries failures without releasing successful siblings twice.</summary>
    [Fact]
    public void ShutdownKeepsFailedRangesAndDrainsSuccessfulSiblingsOnce()
    {
        var arena = new ByteArenaAllocator(256);
        var recording = new SubmissionResourceRetirement(8);
        using var queue = new GpuDeletionQueue12(2);
        var failedAttempts = 0;
        var releasedTags = new List<string?>();
        var failure = new IOException("First cell release failed.");
        using var cache = new GpuTerrainCellCache12<string>(2, 160, geometry =>
        {
            if (geometry.DebugTag == "retry" && ++failedAttempts == 1) throw failure;
            releasedTags.Add(geometry.DebugTag);
            arena.Free(geometry.Allocation);
        }, Lifetime(recording, queue), () => { });
        recording.BeginRecording();
        Prepare(cache, arena, (0, 0), "retry");
        Prepare(cache, arena, (1, 0), "sibling");
        SubmitAndComplete(recording, 1);
        cache.Dispose();
        cache.Dispose();
        Assert.Empty(releasedTags);
        Assert.Equal(0, cache.Count);
        Assert.Equal(160L, cache.EstimatedBytes);
        Assert.Equal(2, queue.GetStats().QueueDepth);
        Assert.Throws<ObjectDisposedException>(() => cache.TryReserve((2, 0), 80, out _));
        var cleanupFailure = Assert.Throws<AggregateException>(cache.ReleaseAfterRetirement);
        Assert.Contains(failure, cleanupFailure.Flatten().InnerExceptions);
        Assert.Equal("sibling", Assert.Single(releasedTags));
        Assert.Equal(80L, cache.EstimatedBytes);
        Assert.Equal(80L, arena.AllocatedBytes);
        Assert.Equal(1, cache.GetStats().EntryCount);
        cache.ReleaseAfterRetirement();
        Assert.Equal(0L, cache.EstimatedBytes);
        Assert.Equal(0L, arena.AllocatedBytes);
        Assert.Equal(0, cache.GetStats().EntryCount);
        Assert.Equal(2, failedAttempts);
        Assert.Equal(2, releasedTags.Count);
        queue.Dispose();
        Assert.Equal(2, releasedTags.Count);
    }

    /// <summary>Connects the cache to the actual portable submission and existing fence-delay retirement owners.</summary>
    /// <param name="recording">Recording owner driven explicitly by each test's execution outcome and completion proof.</param>
    /// <param name="queue">Existing deletion queue; test ticks represent caller-proven frame reuse.</param>
    /// <returns>Callbacks transferring exact resource and outcome ownership without another clock or mock cache.</returns>
    private static GpuTerrainCellCacheLifetime12 Lifetime(SubmissionResourceRetirement recording, GpuDeletionQueue12 queue) =>
        new(resource => recording.Retain(resource, "terrain candidate"), recording.Enlist, queue.EnqueueDispose);

    /// <summary>Admits, transfers and completes one real eighty-byte arena allocation under an active recording.</summary>
    /// <param name="cache">Exact world-generation cache.</param>
    /// <param name="arena">Allocator retaining the test's actual range identity.</param>
    /// <param name="key">Cell coordinates local to this cache.</param>
    /// <param name="label">Draw metadata and release identity.</param>
    /// <returns>The exact candidate for submission, replacement and retirement assertions.</returns>
    private static CellEntry Prepare(GpuTerrainCellCache12<string> cache, ByteArenaAllocator arena,
        (int gx, int gy) key, string label)
    {
        Assert.True(cache.TryReserve(key, 80, out var entry));
        Assert.NotNull(entry);
        entry.Resource.AcquireGeometry(retain =>
        {
            var geometry = new TerrainAllocation12(arena.Allocate(80), 0, 0, 64, 16, label);
            retain(geometry);
            return geometry;
        });
        cache.Complete(entry, label);
        return entry;
    }

    /// <summary>Reports accepted submission, assigns its successful signal, then supplies caller-proven completion.</summary>
    /// <param name="recording">Active recording containing the actual cache participants and retirement resources.</param>
    /// <param name="fence">Fresh monotonic completion value for this recording.</param>
    private static void SubmitAndComplete(SubmissionResourceRetirement recording, ulong fence)
    {
        recording.MarkSubmissionPossible();
        recording.ReportSubmission(SubmissionOutcome.Submitted);
        recording.AssociateUnfenced(fence);
        recording.ReleaseCompleted(fence);
    }
}
