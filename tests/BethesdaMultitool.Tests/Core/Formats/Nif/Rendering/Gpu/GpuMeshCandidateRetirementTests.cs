using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.Core.Lifetime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Exercises exact mesh residency through submission outcomes, retained draw pins and actual resource cleanup.</summary>
public sealed class GpuMeshCandidateRetirementTests
{
    /// <summary>Only accepted submission publishes; other outcomes retain the candidate until recording and batch users retire.</summary>
    /// <param name="outcome">The actual recorder classification, independently of completion.</param>
    [Theory]
    [InlineData(SubmissionOutcome.Submitted)]
    [InlineData(SubmissionOutcome.SubmissionUncertain)]
    [InlineData(SubmissionOutcome.DefinitelyAbandoned)]
    public void ExactOutcomeAndAllPinsControlCandidateRelease(SubmissionOutcome outcome)
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(160, 2);
        var deferred = new RetiredResourceDisposal();
        var retirement = new SubmissionResourceRetirement(4);
        retirement.BeginRecording();
        var geometry = new GpuSubmissionProbe12();
        var texture = new GpuSubmissionProbe12();
        var identity = TextureIdentity();
        var entry = cache.Reserve("mesh", 80);
        var resources = GpuMeshResources12.CreateForResidency(_ => geometry.Dispose(), acquired =>
        {
            Assert.Same(identity, acquired);
            texture.Dispose();
        });
        entry.Attach(resources);
        retirement.Enlist(entry);
        retirement.Retain(new GpuMeshCandidateRetirement12(entry, deferred), "candidate", stage: 1);
        resources.AcquireGeometry(() =>
        {
            Assert.Equal(1, retirement.PendingResourceCount);
            Assert.True(retirement.HasRecording);
            return default;
        });
        resources.AcquireTexture(() => identity);
        resources.AcquireTexture(() => identity);
        resources.Commit();
        entry.MarkPrepared();
        var batchPin = entry.AcquirePreparedPin();
        retirement.Retain(batchPin.Retain(), "recording pin", stage: 0);

        if (outcome == SubmissionOutcome.DefinitelyAbandoned)
        {
            retirement.AbandonRecording();
        }
        else
        {
            retirement.MarkSubmissionPossible();
            retirement.ReportSubmission(outcome);
            retirement.ReleaseCompleted(1_000); // Unfenced work is not retired by a numeric completion alone.
            Assert.Equal(2, retirement.PendingResourceCount);
            retirement.AssociateUnfenced(10);
            retirement.ReleaseCompleted(9);
        }
        var submitted = outcome == SubmissionOutcome.Submitted;
        Assert.Equal(submitted ? ResourceResidencyState.Resident : ResourceResidencyState.Retiring, entry.State);
        Assert.Equal(0, geometry.ReleaseAttempts);
        Assert.Equal(0, texture.ReleaseAttempts);
        Assert.Equal(80, cache.TotalBytes);
        Assert.Equal(80, cache.PinnedBytes);
        var completion = outcome == SubmissionOutcome.DefinitelyAbandoned ? 0UL : 10UL;
        retirement.ReleaseCompleted(completion);
        Assert.False(retirement.HasPending);
        if (submitted)
        {
            Assert.False(deferred.HasPending);
            Assert.False(resources.IsReleased);
            Assert.True(cache.Remove("mesh"));
            new GpuMeshCandidateRetirement12(entry, deferred).Dispose();
        }
        else
        {
            Assert.False(cache.TryAcquire("mesh", out _));
            Assert.Equal(80, cache.RetiringBytes);
        }
        Assert.True(deferred.HasPending);
        Assert.Throws<AggregateException>(deferred.Dispose);
        Assert.Same(resources, batchPin.Resource);
        Assert.Equal(0, geometry.ReleaseAttempts);
        batchPin.Dispose();
        deferred.Dispose();
        Assert.False(deferred.HasPending);
        Assert.False(retirement.HasPending);
        Assert.True(resources.IsReleased);
        Assert.Equal(1, geometry.ReleaseAttempts);
        Assert.Equal(2, texture.ReleaseAttempts);
        Assert.Equal(ResourceResidencyState.Released, entry.State);
        Assert.Equal(0, cache.TotalBytes);
        Assert.Equal(0, cache.PinnedBytes);
    }

    /// <summary>Reusing a key does not replace the resource handed from an old frozen batch to a later recording.</summary>
    [Fact]
    public void ReplacementCannotSubstituteTheRetainedOldMeshForALaterRecording()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(240, 3);
        var deferred = new RetiredResourceDisposal();
        var retirement = new SubmissionResourceRetirement(4);
        var oldRelease = new GpuSubmissionProbe12();
        var old = cache.Reserve("mesh", 80);
        var oldResources = GpuMeshResources12.CreateForResidency(_ => oldRelease.Dispose(), _ => { });
        old.Attach(oldResources);
        retirement.BeginRecording();
        retirement.Enlist(old);
        retirement.Retain(new GpuMeshCandidateRetirement12(old, deferred), "old candidate");
        oldResources.AcquireGeometry(() => default);
        oldResources.Commit();
        old.MarkPrepared();
        var batchPin = old.AcquirePreparedPin();
        Submit(retirement, 10);
        retirement.ReleaseCompleted(10);
        Assert.False(retirement.HasPending);
        Assert.False(deferred.HasPending);
        Assert.Equal(0, oldRelease.ReleaseAttempts);

        var newRelease = new GpuSubmissionProbe12();
        var replacement = cache.Reserve("mesh", 96);
        var newResources = GpuMeshResources12.CreateForResidency(_ => newRelease.Dispose(), _ => { });
        replacement.Attach(newResources);
        newResources.AcquireGeometry(() => default);
        newResources.Commit();
        replacement.MarkPrepared();
        replacement.PublishPrepared();
        Assert.Equal(ResourceResidencyState.Retiring, old.State);
        Assert.Equal(176, cache.TotalBytes);

        retirement.BeginRecording();
        var recordingPin = batchPin.Retain();
        Assert.Same(oldResources, recordingPin.Resource);
        retirement.Retain(recordingPin, "old recording pin", stage: 0);
        retirement.Retain(new GpuMeshCandidateRetirement12(old, deferred), "old retired mesh", stage: 1);
        Assert.True(cache.TryAcquire("mesh", out var currentPin));
        Assert.Same(newResources, currentPin!.Resource);
        currentPin.Dispose();
        Submit(retirement, 20);
        batchPin.Dispose();
        retirement.ReleaseCompleted(19);
        Assert.Equal(0, oldRelease.ReleaseAttempts);
        Assert.Equal(80, cache.PinnedBytes);
        Assert.Throws<InvalidOperationException>(old.ReleaseAfterRetirement);
        retirement.ReleaseCompleted(20);
        Assert.False(retirement.HasPending);
        Assert.Equal(0, oldRelease.ReleaseAttempts);
        Assert.Equal(176, cache.TotalBytes);
        Assert.True(deferred.HasPending);
        deferred.Dispose();
        Assert.Equal(1, oldRelease.ReleaseAttempts);
        Assert.Equal(0, newRelease.ReleaseAttempts);
        Assert.Equal(ResourceResidencyState.Released, old.State);
        Assert.Equal(ResourceResidencyState.Resident, replacement.State);
        Assert.Equal(96, cache.TotalBytes);
        Assert.Equal(0, cache.PinnedBytes);
    }

    /// <summary>Partial geometry and aliased-texture cleanup keep the full charge until all failed actions actually succeed.</summary>
    [Fact]
    public void FailedRetirementRetainsAccountingAndRetriesOnlyFailedAcquisitions()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(80, 1);
        var deferred = new RetiredResourceDisposal();
        var retirement = new SubmissionResourceRetirement(2);
        var geometry = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var texture = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var identity = TextureIdentity();
        var expectedRange = new GeometryAllocation12(new ByteArenaAllocator(256).Allocate(70), 1_024, 1_088, 64, 6);
        var entry = cache.Reserve("mesh", 80);
        var resources = GpuMeshResources12.CreateForResidency(range =>
        {
            Assert.Equal(expectedRange.Allocation, range.Allocation);
            geometry.Dispose();
        }, acquired =>
        {
            Assert.Same(identity, acquired);
            texture.Dispose();
        });
        entry.Attach(resources);
        retirement.BeginRecording();
        retirement.Enlist(entry);
        retirement.Retain(new GpuMeshCandidateRetirement12(entry, deferred), "failed materialization");
        resources.AcquireGeometry(() => expectedRange);
        resources.AcquireTexture(() => identity);
        resources.AcquireTexture(() => identity);
        Assert.Throws<InvalidOperationException>(() => resources.AcquireTexture(
            () => throw new InvalidOperationException("Later material preparation failed.")));
        retirement.AbandonRecording();
        retirement.ReleaseCompleted(0);
        Assert.False(retirement.HasPending);
        Assert.Equal(0, geometry.ReleaseAttempts);
        Assert.Throws<AggregateException>(deferred.Dispose);
        Assert.Equal(1, geometry.ReleaseAttempts);
        Assert.Equal(2, texture.ReleaseAttempts);
        Assert.False(resources.IsReleased);
        Assert.True(deferred.HasPending);
        Assert.Equal(80, cache.TotalBytes);
        Assert.Equal(80, cache.RetiringBytes);
        Assert.Throws<InvalidOperationException>(() => cache.Reserve("replacement", 1));

        deferred.Dispose();
        Assert.Equal(2, geometry.ReleaseAttempts);
        Assert.Equal(3, texture.ReleaseAttempts);
        Assert.True(resources.IsReleased);
        Assert.False(retirement.HasPending);
        Assert.False(deferred.HasPending);
        Assert.Equal(0, cache.TotalBytes);
        Assert.Equal(0, cache.EntryCount);
        deferred.Dispose();
        Assert.Equal(2, geometry.ReleaseAttempts);
        Assert.Equal(3, texture.ReleaseAttempts);
    }

    /// <summary>The initial holder cannot dispose reserved, prepared or resident cache ownership prematurely.</summary>
    [Fact]
    public void InitialHolderOnlyReleasesEntriesAlreadyMarkedRetiring()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(80, 1);
        var geometry = new GpuSubmissionProbe12();
        var deferred = new RetiredResourceDisposal();
        var entry = cache.Reserve("mesh", 80);
        var resources = GpuMeshResources12.CreateForResidency(_ => geometry.Dispose(), _ => { });
        entry.Attach(resources);
        var holder = new GpuMeshCandidateRetirement12(entry, deferred);
        holder.Dispose();
        Assert.Equal(ResourceResidencyState.Reserved, entry.State);
        Assert.False(deferred.HasPending);
        resources.AcquireGeometry(() => default);
        resources.Commit();
        entry.MarkPrepared();
        holder.Dispose();
        Assert.Equal(ResourceResidencyState.Prepared, entry.State);
        Assert.False(deferred.HasPending);
        entry.PublishPrepared();
        holder.Dispose();
        Assert.Equal(ResourceResidencyState.Resident, entry.State);
        Assert.False(deferred.HasPending);
        Assert.Equal(0, geometry.ReleaseAttempts);
        Assert.Equal(80, cache.TotalBytes);
        Assert.True(cache.Remove("mesh"));
        var eviction = new GpuMeshCandidateRetirement12(entry, deferred);
        eviction.Dispose();
        eviction.Dispose();
        Assert.True(deferred.HasPending);
        Assert.Equal(0, geometry.ReleaseAttempts);
        deferred.Dispose();
        Assert.Equal(ResourceResidencyState.Released, entry.State);
        Assert.Equal(1, geometry.ReleaseAttempts);
        Assert.Equal(0, cache.TotalBytes);
        eviction.Dispose();
        new GpuMeshCandidateRetirement12(entry, deferred).Dispose();
        Assert.False(deferred.HasPending);
    }

    /// <summary>Frame advancement hands off pinned eviction without failing; cleanup retains bytes through later native failure.</summary>
    [Fact]
    public void DeletionQueueTickTransfersPinnedEntryWithoutAttemptingNativeCleanup()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(80, 1);
        using var queue = new GpuDeletionQueue12(2);
        var deferred = new RetiredResourceDisposal();
        var geometry = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var entry = cache.Reserve("mesh", 80);
        var resources = GpuMeshResources12.CreateForResidency(_ => geometry.Dispose(), _ => { });
        entry.Attach(resources);
        resources.AcquireGeometry(() => default);
        resources.Commit();
        entry.MarkPrepared();
        var batchPin = entry.AcquirePreparedPin();
        entry.PublishPrepared();
        Assert.True(cache.Remove("mesh"));
        var holder = new GpuMeshCandidateRetirement12(entry, deferred);
        queue.EnqueueDispose(holder);
        queue.Tick();
        Assert.False(deferred.HasPending);
        Assert.Null(Record.Exception(queue.Tick));
        Assert.Equal(0, queue.GetStats().QueueDepth);
        Assert.True(deferred.HasPending);
        Assert.Equal(80, cache.RetiringBytes);
        Assert.Equal(80, cache.PinnedBytes);
        Assert.Throws<AggregateException>(deferred.Dispose);
        Assert.Equal(0, geometry.ReleaseAttempts);
        Assert.Equal(80, cache.TotalBytes);
        batchPin.Dispose();
        holder.Dispose(); // A duplicate transfer would incorrectly retry the failure in the same drain.
        Assert.Throws<AggregateException>(deferred.Dispose);
        Assert.Equal(1, geometry.ReleaseAttempts);
        Assert.Equal(80, cache.TotalBytes);
        Assert.True(deferred.HasPending);
        deferred.Dispose();
        Assert.Equal(2, geometry.ReleaseAttempts);
        Assert.Equal(0, cache.TotalBytes);
        holder.Dispose();
        new GpuMeshCandidateRetirement12(entry, deferred).Dispose();
        Assert.False(deferred.HasPending);
    }

    /// <summary>A rejected transfer remains retryable instead of marking the entry as already handed off.</summary>
    [Fact]
    public void DeferredOwnerAdmissionFailureDoesNotConsumeTransfer()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(80, 1);
        var deferred = new RetiredResourceDisposal();
        var geometry = new GpuSubmissionProbe12();
        var entry = cache.Reserve("mesh", 80);
        var resources = GpuMeshResources12.CreateForResidency(_ => geometry.Dispose(), _ => { });
        entry.Attach(resources);
        resources.AcquireGeometry(() => default);
        Assert.True(cache.Remove("mesh"));
        var holder = new GpuMeshCandidateRetirement12(entry, deferred);
        deferred.Add(holder, "late prerequisite", stage: 1);
        // During stage one, Shared rejects adding the holder's stage-zero entry. No native release occurs.
        Assert.Throws<AggregateException>(deferred.Dispose);
        Assert.Equal(0, geometry.ReleaseAttempts);
        Assert.Equal(80, cache.TotalBytes);
        holder.Dispose(); // Retry outside the active stage can now transfer the exact entry.
        deferred.Dispose();
        Assert.False(deferred.HasPending);
        Assert.Equal(1, geometry.ReleaseAttempts);
        Assert.Equal(0, cache.TotalBytes);
    }

    /// <summary>A queued holder that first runs after cache shutdown cannot repopulate cleanup with an already released entry.</summary>
    [Fact]
    public void FirstQueueCallbackAfterCacheShutdownDoesNotRepopulateDeferredOwner()
    {
        using var cache = new ResourceResidencyCache<string, GpuMeshResources12>(80, 1);
        using var queue = new GpuDeletionQueue12(1);
        var deferred = new RetiredResourceDisposal();
        var geometry = new GpuSubmissionProbe12();
        var entry = cache.Reserve("mesh", 80);
        var resources = GpuMeshResources12.CreateForResidency(_ => geometry.Dispose(), _ => { });
        entry.Attach(resources);
        resources.AcquireGeometry(() => default);
        entry.MarkPrepared();
        entry.PublishPrepared();
        queue.EnqueueDispose(new GpuMeshCandidateRetirement12(entry, deferred));
        cache.Dispose(); // The caller has retired all users; this case never acquired a pin.
        Assert.Equal(ResourceResidencyState.Released, entry.State);
        Assert.Equal(1, geometry.ReleaseAttempts);
        Assert.False(deferred.HasPending);
        Assert.Null(Record.Exception(queue.Tick));
        Assert.Equal(0, queue.GetStats().QueueDepth);
        Assert.False(deferred.HasPending);
        Assert.Equal(1, geometry.ReleaseAttempts);
    }

    /// <summary>Supplies accepted submission followed by a real caller-provided signal, without claiming it completed.</summary>
    /// <param name="retirement">Active recording owner.</param>
    /// <param name="fence">Positive strictly increasing queue signal.</param>
    private static void Submit(SubmissionResourceRetirement retirement, ulong fence)
    {
        retirement.MarkSubmissionPossible();
        retirement.ReportSubmission(SubmissionOutcome.Submitted);
        retirement.AssociateUnfenced(fence);
    }

    /// <summary>Creates a synthetic entry identity; tests never access a native texture resource.</summary>
    /// <returns>A repeatable cache-entry object that may be acquired more than once.</returns>
    private static GpuTextureCache12.Entry TextureIdentity() => new(
        null!, default, default, 7, default, default, false, "texture");
}
