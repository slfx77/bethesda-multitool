using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Exercises actual WARP recorder submission and abandonment with observable resource ownership.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class GpuCommandRecorderSubmissionIntegrationTests
{
    /// <summary>Submission publishes once after a successful signal and delays release until observed retirement.</summary>
    [Fact]
    public void SubmittedResourcesWaitForRetirementAndDuplicateParticipantsCommitOnce()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        var resource = new GpuSubmissionProbe12();
        var participant = new GpuSubmissionProbe12 { ReadFence = () => recorder.LastSubmittedFenceValue };
        Assert.False(recorder.IsRecording);
        Assert.Equal(0ul, recorder.RecordingGeneration);
        recorder.BeginFrame();
        Assert.True(recorder.IsRecording);
        Assert.Equal(1ul, recorder.RecordingGeneration);
        Assert.Throws<InvalidOperationException>(recorder.BeginFrame);
        Assert.Equal(1ul, recorder.RecordingGeneration);
        recorder.EnqueueDisposeAfterCurrentFrame(resource);
        recorder.EnlistCurrentFrame(participant);
        recorder.EnlistCurrentFrame(participant);
        recorder.EndFrame();
        Assert.False(recorder.IsRecording);
        Assert.Equal(1, participant.SubmittedCount);
        Assert.Equal(0, participant.AbortedCount);
        Assert.Equal(recorder.LastSubmittedFenceValue, participant.ObservedFence);
        Assert.True(participant.ObservedFence > 0);
        Assert.Equal(0, resource.ReleaseAttempts);
        recorder.WaitForGpuIdle();
        Assert.Equal(1, resource.ReleaseAttempts);
        recorder.BeginFrame();
        Assert.Equal(2ul, recorder.RecordingGeneration);
        recorder.EndFrame();
        recorder.WaitForGpuIdle();
        Assert.Equal(1, resource.ReleaseAttempts);
    }

    /// <summary>Callback and release failures do not skip sibling notification or prevent the next valid frame.</summary>
    [Fact]
    public void AbandonmentRetainsFailedReleaseAndDetachesThrowingParticipant()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        var resource = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        var throwing = new GpuSubmissionProbe12 { ThrowOnNotification = true };
        var sibling = new GpuSubmissionProbe12();
        recorder.BeginFrame();
        recorder.EnqueueDisposeAfterCurrentFrame(resource);
        recorder.EnlistCurrentFrame(throwing);
        recorder.EnlistCurrentFrame(sibling);
        Assert.True(recorder.AbortFrame());
        Assert.Equal(1, recorder.FrameIndex);
        Assert.Equal(1, resource.ReleaseAttempts);
        Assert.Equal(1, throwing.AbortedCount);
        Assert.Equal(1, sibling.AbortedCount);
        recorder.BeginFrame();
        Assert.Equal(2, resource.ReleaseAttempts);
        recorder.EnlistCurrentFrame(sibling);
        recorder.EndFrame();
        recorder.WaitForGpuIdle();
        Assert.Equal(1, sibling.SubmittedCount);
        Assert.Equal(1, throwing.AbortedCount);
        Assert.Equal(2, resource.ReleaseAttempts);
    }

    /// <summary>The optional uncertain-submission lifetime remains caller-owned after successful native submission.</summary>
    [Fact]
    public void SuccessfulSubmissionDoesNotTakeOptionalCallerLifetime()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        var callerLifetime = new GpuSubmissionProbe12();
        using (var recorder = new GpuCommandRecorder12(gpu))
        {
            recorder.BeginFrame();
            var outcome = recorder.EndFrameWithOutcome(callerLifetime);
            Assert.True(outcome.Succeeded);
            Assert.True(outcome.CommandListMayHaveReachedQueue);
            recorder.WaitForGpuIdle();
            Assert.Equal(0, callerLifetime.ReleaseAttempts);
        }
        Assert.Equal(0, callerLifetime.ReleaseAttempts);
        callerLifetime.Dispose();
        Assert.Equal(1, callerLifetime.ReleaseAttempts);
    }

    /// <summary>A native close failure abandons the unsubmitted frame and preserves the optional caller lifetime.</summary>
    [Fact]
    public void PreExecuteCloseFailureAbandonsWithoutPublishingOrTakingCallerLifetime()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        var resource = new GpuSubmissionProbe12();
        var participant = new GpuSubmissionProbe12();
        var callerLifetime = new GpuSubmissionProbe12();
        recorder.BeginFrame();
        recorder.EnqueueDisposeAfterCurrentFrame(resource);
        recorder.EnlistCurrentFrame(participant);
        // Closing an empty list twice is a deterministic pre-execute API error; no invalid work
        // reaches the queue and no device or queue is deliberately removed or corrupted.
        recorder.CommandList.Close();
        var outcome = recorder.EndFrameWithOutcome(callerLifetime);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.CommandListMayHaveReachedQueue);
        Assert.Equal(0ul, outcome.FenceValue);
        Assert.NotNull(outcome.Error);
        Assert.Same(outcome.Error, Record.Exception(outcome.ThrowIfFailed));
        Assert.Equal(0ul, recorder.LastSubmittedFenceValue);
        Assert.Equal(1, recorder.FrameIndex);
        Assert.Equal(0, participant.SubmittedCount);
        Assert.Equal(1, participant.AbortedCount);
        Assert.Equal(1, resource.ReleaseAttempts);
        Assert.Equal(0, callerLifetime.ReleaseAttempts);
        Assert.False(recorder.AbortFrame());
        Assert.Throws<InvalidOperationException>(recorder.BeginFrame);
        Assert.Throws<InvalidOperationException>(recorder.WaitForFrameSlot);
        recorder.WaitForGpuIdle();
        Assert.Equal(1, resource.ReleaseAttempts);
        Assert.Equal(1, participant.AbortedCount);
        Assert.Equal(0, callerLifetime.ReleaseAttempts);
        callerLifetime.Dispose();
        Assert.Equal(1, callerLifetime.ReleaseAttempts);
    }

    /// <summary>An idle fence retires earlier submissions without releasing or resolving an open recording.</summary>
    /// <param name="submit">Whether the retained current frame is later submitted or explicitly abandoned.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleWaitPreservesOpenRecordingUntilItsOwnOutcome(bool submit)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        var previousResource = new GpuSubmissionProbe12();
        recorder.BeginFrame();
        recorder.EnqueueDisposeAfterCurrentFrame(previousResource);
        recorder.EndFrame();
        var previousFence = recorder.LastSubmittedFenceValue;
        var currentResource = new GpuSubmissionProbe12();
        var currentParticipant = new GpuSubmissionProbe12 { ReadFence = () => recorder.LastSubmittedFenceValue };
        recorder.BeginFrame();
        recorder.EnqueueDisposeAfterCurrentFrame(currentResource);
        recorder.EnlistCurrentFrame(currentParticipant);
        var frameIndex = recorder.FrameIndex;

        recorder.WaitForGpuIdle();
        Assert.Equal(1, previousResource.ReleaseAttempts);
        Assert.Equal(0, currentResource.ReleaseAttempts);
        Assert.Equal(0, currentParticipant.SubmittedCount);
        Assert.Equal(0, currentParticipant.AbortedCount);
        Assert.Equal(frameIndex, recorder.FrameIndex);
        Assert.Equal(previousFence, recorder.LastSubmittedFenceValue);
        var laterResource = new GpuSubmissionProbe12();
        recorder.EnqueueDisposeAfterCurrentFrame(laterResource);
        recorder.EnlistCurrentFrame(currentParticipant);

        if (submit)
        {
            recorder.EndFrame();
            Assert.True(recorder.LastSubmittedFenceValue > previousFence);
            Assert.Equal(recorder.LastSubmittedFenceValue, currentParticipant.ObservedFence);
            Assert.Equal(1, currentParticipant.SubmittedCount);
            Assert.Equal(0, currentParticipant.AbortedCount);
            Assert.Equal(0, currentResource.ReleaseAttempts);
            Assert.Equal(0, laterResource.ReleaseAttempts);
        }
        else
        {
            Assert.True(recorder.AbortFrame());
            Assert.Equal(previousFence, recorder.LastSubmittedFenceValue);
            Assert.Equal(0, currentParticipant.SubmittedCount);
            Assert.Equal(1, currentParticipant.AbortedCount);
            Assert.Equal(1, currentResource.ReleaseAttempts);
            Assert.Equal(1, laterResource.ReleaseAttempts);
        }
        recorder.WaitForGpuIdle();
        Assert.Equal(1, previousResource.ReleaseAttempts);
        Assert.Equal(1, currentResource.ReleaseAttempts);
        Assert.Equal(1, laterResource.ReleaseAttempts);
        Assert.Equal(1, currentParticipant.SubmittedCount + currentParticipant.AbortedCount);
        Assert.Equal((frameIndex + 1) % GpuCommandRecorder12.FramesInFlight, recorder.FrameIndex);
    }

    /// <summary>A rejected cross-thread mutation leaves the open recorder and caller ownership unchanged.</summary>
    [Fact]
    public void CrossThreadAdmissionDoesNotTransferOwnershipOrAdvanceFrame()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        var callerResource = new GpuSubmissionProbe12();
        Exception? rejection = null;
        recorder.BeginFrame();
        var thread = new Thread(() =>
        {
            try
            {
                recorder.EnqueueDisposeAfterCurrentFrame(callerResource);
            }
            catch (Exception error)
            {
                rejection = error;
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The rejected recorder mutation did not return.");
        Assert.IsType<InvalidOperationException>(rejection);
        Assert.Equal(0, recorder.FrameIndex);
        Assert.Equal(0, callerResource.ReleaseAttempts);
        recorder.EndFrame();
        recorder.WaitForGpuIdle();
        Assert.Equal(0, callerResource.ReleaseAttempts);
        callerResource.Dispose();
    }

    /// <summary>Synchronous no-frame cleanup rejects nested recording or ownership transfer, including after shutdown.</summary>
    /// <param name="stopRecorder">Whether the recorder has already completed its terminal cleanup.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoFrameReleaseRejectsReentryWithoutTakingNestedResource(bool stopRecorder)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        if (stopRecorder) { recorder.Dispose(); }
        var nestedResource = new GpuSubmissionProbe12();
        var releasedResource = new GpuSubmissionProbe12
        {
            OnRelease = () =>
            {
                Assert.Throws<InvalidOperationException>(() => recorder.EnqueueDisposeAfterCurrentFrame(nestedResource));
                Assert.Throws<InvalidOperationException>(recorder.BeginFrame);
                Assert.Throws<InvalidOperationException>(() => _ = recorder.CommandList);
            }
        };

        recorder.EnqueueDisposeAfterCurrentFrame(releasedResource);

        Assert.Equal(1, releasedResource.ReleaseAttempts);
        Assert.Equal(0, nestedResource.ReleaseAttempts);
        Assert.False(recorder.IsRecording);
        Assert.Equal(0, recorder.FrameIndex);
        nestedResource.Dispose();
    }

    /// <summary>Child release failure leaves a successful idle proof usable and remains owned until disposal retries it.</summary>
    [Fact]
    public void ProvenIdleCleanupFailureIsRetainedWithoutBecomingFenceFailure()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        var resource = new GpuSubmissionProbe12 { RemainingReleaseFailures = 1 };
        recorder.BeginFrame();
        recorder.EnqueueDisposeAfterCurrentFrame(resource);
        recorder.EndFrame();
        var submittedFence = recorder.LastSubmittedFenceValue;

        recorder.WaitForGpuIdle();
        Assert.Equal(1, resource.ReleaseAttempts);
        Assert.Equal(submittedFence, recorder.LastSubmittedFenceValue);
        recorder.Dispose();
        Assert.Equal(2, resource.ReleaseAttempts);
        recorder.Dispose();
        Assert.Equal(2, resource.ReleaseAttempts);
    }

    /// <summary>Initialization admission failure remains device-owned, blocks another attempt, and permits safe device cleanup.</summary>
    [Fact]
    public void FailedInitializationAdmissionRemainsOwnedUntilDeviceCleanup()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        // No commands have been submitted. Exhaust ordinary signals without removing or corrupting
        // the device; initialization must reject admission before creating native recorder objects.
        gpu.FrameFence.Signal(ulong.MaxValue - 1);
        var admissionFailure = Assert.Throws<InvalidOperationException>(() => new GpuCommandRecorder12(gpu));
        Assert.Contains("exhausted ordinary signal values", admissionFailure.Message, StringComparison.Ordinal);
        var retainedFailure = Assert.Throws<InvalidOperationException>(() => new GpuCommandRecorder12(gpu));
        Assert.Contains("incomplete frame recorder", retainedFailure.Message, StringComparison.Ordinal);

        gpu.Dispose();
        gpu.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new GpuCommandRecorder12(gpu));
    }
}
