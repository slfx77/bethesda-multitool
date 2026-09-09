using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

[Collection(OblivionWaterReplayGpuGroup.Name)]
[Trait("Category", TestCategories.Gpu)]
public sealed class OblivionWaterContinuousHeightGpuTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Continuous_signed_height_upload_and_HMAP_normal_survive_abort_repeat_and_time_changes(
        bool hardware, bool highResolution)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var fixture = new OblivionWaterReplayGpuFixture(
            highResolution ? "continuous-height-hires" : "continuous-height-lowres", hardware);
        fixture.InitializeContinuousHeight(highResolution);
        Assert.Throws<InvalidOperationException>(() => fixture.ContinuousHeight.Record(0.31f));
        Assert.Null(fixture.ContinuousHeight.LastCommittedElapsedSeconds);
        fixture.RecordContinuousHeight(0.31f, 1);
        Assert.Null(fixture.ContinuousHeight.LastCommittedElapsedSeconds);
        Assert.Throws<InvalidOperationException>(() => fixture.ContinuousHeight.Dispose());
        fixture.Recorder.AbortFrame();
        fixture.RejectAbortedContinuousHeight();
        Assert.Null(fixture.ContinuousHeight.LastCommittedElapsedSeconds);
        Assert.Null(fixture.Prepass.OrdinaryNormal);
        Assert.Equal(0ul, fixture.ContinuousHeight.LastCommittedFence);

        fixture.RecordContinuousHeight(0.31f, 1);
        var first = fixture.SubmitContinuousHeightAndCheck();
        var firstNormal = fixture.Bytes(6).ToArray();
        var pointer = fixture.Resources[new OblivionWaterSimulationResource(8)].Texture.NativePointer;
        var firstFence = fixture.ContinuousHeight.LastCommittedFence;
        fixture.RecordContinuousHeight(0.31f + 32f / 12f, 2);
        Assert.Equal(0.31f, fixture.ContinuousHeight.LastCommittedElapsedSeconds);
        var later = fixture.SubmitContinuousHeightAndCheck();
        Assert.NotEqual(first, later);
        Assert.NotEqual(firstNormal, fixture.Bytes(6));
        Assert.Equal(pointer, fixture.Resources[new OblivionWaterSimulationResource(8)].Texture.NativePointer);
        Assert.True(fixture.ContinuousHeight.LastCommittedFence > firstFence);

        var committed = fixture.ContinuousHeight.LastCommittedElapsedSeconds;
        var committedFence = fixture.ContinuousHeight.LastCommittedFence;
        fixture.RecordContinuousHeight(9.7f, 3);
        fixture.Recorder.AbortFrame();
        fixture.RejectAbortedContinuousHeight();
        Assert.Equal(committed, fixture.ContinuousHeight.LastCommittedElapsedSeconds);
        Assert.Equal(committedFence, fixture.ContinuousHeight.LastCommittedFence);
        fixture.RecordContinuousHeight(committed!.Value, 3);
        Assert.Equal(later, fixture.SubmitContinuousHeightAndCheck());
        Assert.Equal(committedFence, fixture.ContinuousHeight.LastCommittedFence); // Paused input reused.

        fixture.RecordContinuousHeight(0.31f, 4);
        Assert.Equal(first, fixture.SubmitContinuousHeightAndCheck());
        Assert.Equal(firstNormal, fixture.Bytes(6));
        Assert.Equal(pointer, fixture.Resources[new OblivionWaterSimulationResource(8)].Texture.NativePointer);
        Assert.Equal(0L, GpuShaderCompiler12.CompileCount);
    }
}