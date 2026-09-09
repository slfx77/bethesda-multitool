using System.Collections.Immutable;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OblivionWaterReplayGpuGroup
{
    public const string Name = "TES4 displacement native replay";

    private OblivionWaterReplayGpuGroup()
    {
    }
}

[Collection(OblivionWaterReplayGpuGroup.Name)]
[Trait("Category", TestCategories.Gpu)]
public sealed class OblivionWaterDisplacementReplayGpuTests
{
    private static OblivionWaterDisplacementInvocation StampedRain()
    {
        return OblivionWaterSimulationTestData.Rain() with
        {
            RainRate = 10,
            // Controlled raw rand results, not an observed live weather event. They put the
            // stamp edges about .25 pixel from integer centers, away from raster ties.
            RainSamples = ImmutableArray.Create(new OblivionWaterRecordedRainSample(8224, 24543))
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rain_stamp_evolution_and_normal_match_fenced_readbacks_and_reject_naive_pixel_origin(bool hardware)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var fixture = new OblivionWaterReplayGpuFixture("rain-raster", hardware);
        var plan = fixture.Record(StampedRain());
        Assert.Collection(plan.Passes,
            pass => Assert.Equal(OblivionWaterSimulationStage.RainStamp, pass.Stage),
            pass => Assert.Equal(OblivionWaterSimulationStage.RainEvolution, pass.Stage),
            pass => Assert.Equal(OblivionWaterSimulationStage.Normal, pass.Stage));
        Assert.Equal(0, fixture.Prepass.CommittedState.LastInvocation);
        Assert.Null(fixture.Prepass.OrdinaryNormal);
        fixture.SubmitAndCheck();
        Assert.True(fixture.RejectedNaiveChannels >= 64,
            "A nonuniform stamp must distinguish the unadapted viewport, including sampling around its edges.");
        Assert.Equal(fixture.Resources[new OblivionWaterSimulationResource(6)].Srv.BindlessIndex,
            fixture.Prepass.OrdinaryNormal);
        Assert.Null(fixture.Prepass.WadingNormal);
        Assert.Equal(3, fixture.RecordedUploads);
        Assert.Equal(3, fixture.RecordedPasses);
        Assert.Equal(1, fixture.RecordedClears);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_wading_without_evolution_writes_scratch_but_publishes_unchanged_current(bool hardware)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var fixture = new OblivionWaterReplayGpuFixture("wading-no-step", hardware);
        fixture.Record(OblivionWaterSimulationTestData.Rain());
        fixture.SubmitAndCheck();
        var ordinary = fixture.Bytes(6).ToArray();
        var wadingCurrent = fixture.Prepass.CommittedState.WadingHeight;
        var plan = fixture.Record(OblivionWaterSimulationTestData.Wading(2) with
        {
            RecenterOffset = new Vector2(0.125f, -0.25f)
        });
        Assert.Collection(plan.Passes,
            pass => Assert.Equal(OblivionWaterSimulationStage.Recenter, pass.Stage),
            pass =>
            {
                Assert.Equal(OblivionWaterSimulationStage.Normal, pass.Stage);
                Assert.Equal(wadingCurrent, pass.Input0);
            });
        fixture.SubmitAndCheck();
        Assert.Equal(wadingCurrent, fixture.Prepass.CommittedState.FinalInput);
        Assert.Equal(ordinary, fixture.Bytes(6));
        Assert.NotEqual(fixture.Bytes(wadingCurrent.Value), fixture.Bytes(plan.Passes[0].Output.Value));
        Assert.NotNull(fixture.Prepass.WadingNormal);
        Assert.NotEqual(fixture.Prepass.OrdinaryNormal, fixture.Prepass.WadingNormal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Wading_impulse_evolves_with_clamp_sampling_and_keeps_ordinary_output_unpublished(
        bool hardware, bool crossesRightEdge)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var fixture = new OblivionWaterReplayGpuFixture(
            crossesRightEdge ? "wading-impulse-edge" : "wading-impulse", hardware);
        // Quarter-pixel boundaries distinguish the viewport translation without raster ties.
        // These source-ABI rows are test inputs, not a reconstructed actor transform.
        var stamp = new OblivionWaterRecordedWadingStamp(
            new Vector4(0.0625f, 0, crossesRightEdge ? 0.986328125f : -0.748046875f, 0),
            new Vector4(0, 0.09375f, 0.498046875f, 0));
        var plan = fixture.Record(OblivionWaterSimulationTestData.Wading() with { WadingStamp = stamp });
        Assert.Collection(plan.Passes,
            pass => Assert.Equal(OblivionWaterSimulationStage.Recenter, pass.Stage),
            pass => Assert.Equal(OblivionWaterSimulationStage.WadingStamp, pass.Stage),
            pass => Assert.Equal(OblivionWaterSimulationStage.WadingEvolution, pass.Stage),
            pass => Assert.Equal(OblivionWaterSimulationStage.Normal, pass.Stage));
        Assert.Null(fixture.Prepass.WadingNormal);
        fixture.SubmitAndCheck();
        var firstHeight = fixture.Bytes(plan.Next.WadingHeight.Value).ToArray();
        var firstNormal = fixture.Bytes(7).ToArray();
        Assert.True(fixture.RejectedNaiveChannels >= 64,
            "The nonuniform wading impulse must distinguish the unadapted viewport.");
        Assert.Null(fixture.Prepass.OrdinaryNormal);
        Assert.Equal(fixture.Resources[new OblivionWaterSimulationResource(7)].Srv.BindlessIndex,
            fixture.Prepass.WadingNormal);

        var continued = fixture.Record(OblivionWaterSimulationTestData.Wading(2) with
        {
            DeltaSeconds = OblivionWaterDisplacementSchedule.WadingInterval
        });
        Assert.DoesNotContain(continued.Passes, pass => pass.Stage == OblivionWaterSimulationStage.WadingStamp);
        fixture.SubmitAndCheck();
        Assert.NotEqual(firstHeight, fixture.Bytes(continued.Next.WadingHeight.Value));
        Assert.NotEqual(firstNormal, fixture.Bytes(7));
        Assert.Null(fixture.Prepass.OrdinaryNormal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failure_after_first_upload_transfer_aborts_once_and_allows_same_invocation_retry(bool hardware)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var fixture = new OblivionWaterReplayGpuFixture("transfer-failure", hardware);
        var before = GpuFixedFootprintTracker12.NonLocalInstance.GetStats();
        var injectedFailure = new InvalidOperationException("Controlled replay upload-transfer failure.");
        fixture.Inject = observation =>
        {
            if (observation.Kind == OblivionWaterDisplacementReplayObservationKind.UploadTransferred &&
                observation.PassIndex == 0) throw injectedFailure;
        };
        Assert.Same(injectedFailure,
            Assert.Throws<InvalidOperationException>(() => fixture.Record(StampedRain())));
        fixture.RejectAbortedRecording();
        Assert.Equal(1, fixture.RecordedUploads);
        Assert.Equal(0, fixture.RecordedPasses);
        Assert.Equal(0ul, fixture.Recorder.LastSubmittedFenceValue);
        Assert.Equal(OblivionWaterSimulationTestData.State, fixture.Prepass.CommittedState);
        Assert.Null(fixture.Prepass.LastSubmittedPlan);
        Assert.All(fixture.Resources.Values, texture => Assert.False(texture.Initialized));
        var after = GpuFixedFootprintTracker12.NonLocalInstance.GetStats();
        Assert.Equal(before.EntryCount, after.EntryCount);
        Assert.Equal(before.EstimatedBytes, after.EstimatedBytes);
        fixture.Inject = null;
        fixture.Record(StampedRain()); // BeginFrame succeeds only if the prepass ended the failed frame.
        Assert.True(GpuFixedFootprintTracker12.NonLocalInstance.GetStats().EntryCount > before.EntryCount);
        fixture.SubmitAndCheck();
        Assert.Equal(before.EntryCount, GpuFixedFootprintTracker12.NonLocalInstance.GetStats().EntryCount);
        Assert.Equal(before.EstimatedBytes, GpuFixedFootprintTracker12.NonLocalInstance.GetStats().EstimatedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_abort_discards_recorded_clears_and_publication_then_retry_reinitializes(bool hardware)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var fixture = new OblivionWaterReplayGpuFixture("explicit-abort", hardware);
        fixture.Record(StampedRain());
        Assert.Equal(3, fixture.RecordedPasses);
        Assert.Equal(1, fixture.RecordedClears);
        Assert.All(fixture.Resources.Values, texture => Assert.False(texture.Initialized));
        fixture.Recorder.AbortFrame();
        fixture.RejectAbortedRecording();
        Assert.Equal(0ul, fixture.Recorder.LastSubmittedFenceValue);
        Assert.Null(fixture.Prepass.LastSubmittedPlan);
        Assert.Null(fixture.Prepass.OrdinaryNormal);
        Assert.Equal(OblivionWaterSimulationTestData.State, fixture.Prepass.CommittedState);
        fixture.Record(StampedRain());
        fixture.SubmitAndCheck();
        Assert.Equal(2, fixture.RecordedClears);
        Assert.True(fixture.Resources[new OblivionWaterSimulationResource(1)].Initialized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_plan_commits_only_after_submission_and_retirement_obeys_recording_and_frame_leases(bool hardware)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var fixture = new OblivionWaterReplayGpuFixture("empty-retirement", hardware);
        using var deletion = new GpuDeletionQueue12(GpuCommandRecorder12.FramesInFlight);
        fixture.Record(OblivionWaterSimulationTestData.Rain());
        var normal = fixture.Resources[new OblivionWaterSimulationResource(6)];
        Assert.Throws<InvalidOperationException>(() => normal.Retire(deletion));
        Assert.False(normal.Unavailable);
        fixture.SubmitAndCheck();
        var passCount = fixture.RecordedPasses;
        var uploadCount = fixture.RecordedUploads;
        var clearCount = fixture.RecordedClears;
        var plan = fixture.Record(OblivionWaterSimulationTestData.Rain(2) with { NormalOutput = null });
        Assert.Empty(plan.Passes);
        Assert.Equal(1, fixture.Prepass.CommittedState.LastInvocation);
        fixture.SubmitAndCheck();
        Assert.Equal(2, fixture.Prepass.CommittedState.LastInvocation);
        Assert.Equal(passCount, fixture.RecordedPasses);
        Assert.Equal(uploadCount, fixture.RecordedUploads);
        Assert.Equal(clearCount, fixture.RecordedClears);
        var count = fixture.Heap.PersistentCount;
        var slot = normal.Srv.BindlessIndex;
        normal.Retire(deletion);
        Assert.True(normal.Unavailable);
        Assert.Null(fixture.Prepass.OrdinaryNormal);
        Assert.Equal(count, fixture.Heap.PersistentCount);
        for (var frame = 0; frame < GpuCommandRecorder12.FramesInFlight; frame++)
        {
            fixture.Recorder.BeginFrame(); // Establish the existing fence-safe deletion boundary.
            deletion.Tick();
            fixture.Recorder.AbortFrame();
        }

        Assert.Equal(count - 1, fixture.Heap.PersistentCount);
        var reused = fixture.Heap.AllocatePersistent();
        Assert.Equal(slot, reused.BindlessIndex);
        fixture.Heap.FreePersistent(reused.BindlessIndex);
        Assert.Equal(0, deletion.GetStats().QueueDepth);
    }
}