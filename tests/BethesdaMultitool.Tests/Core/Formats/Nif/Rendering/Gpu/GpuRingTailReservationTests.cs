using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class GpuRingTailReservationTests
{
    // ShadowMapRenderer12 is WINDOWS_GUI-only while this test assembly targets portable net10.0.
    // SunShadowMathTests separately pins the host reservation formula to that renderer's count.
    private const int ShadowCascadeCount = 4;

    [Fact]
    public void ShadowSizedTail_FitsBeforeSceneConsumesItsAllocationWindow()
    {
        const uint total = 4096;
        const uint shadowTail =
            ((uint)ShadowCascadeCount * 3u + 1u) * GpuRingBuffer12.CbAlignment;

        Assert.True(GpuRingBuffer12.TryPlanTailReservation(
            currentOffset: 768,
            totalBytes: total,
            reservedTailBytes: 0,
            additionalBytes: shadowTail,
            out var reserved));
        Assert.Equal(shadowTail, reserved);
    }

    [Fact]
    public void TailReservation_RejectsAWindowAlreadyConsumedBySceneDraws()
    {
        Assert.False(GpuRingBuffer12.TryPlanTailReservation(
            currentOffset: 769,
            totalBytes: 4096,
            reservedTailBytes: 0,
            additionalBytes: 3328,
            out var reserved));
        Assert.Equal(0u, reserved);
    }

    [Fact]
    public void TailReservations_AreAdditiveAndFailurePreservesThePriorTail()
    {
        Assert.True(GpuRingBuffer12.TryPlanTailReservation(
            currentOffset: 512,
            totalBytes: 4096,
            reservedTailBytes: 1024,
            additionalBytes: 2048,
            out var reserved));
        Assert.Equal(3072u, reserved);

        Assert.False(GpuRingBuffer12.TryPlanTailReservation(
            currentOffset: 1025,
            totalBytes: 4096,
            reservedTailBytes: reserved,
            additionalBytes: 1,
            out var preserved));
        Assert.Equal(reserved, preserved);
    }

    [Fact]
    public void OrdinaryAllocations_StopAtReservedTail_ThenResumeAfterRelease()
    {
        Assert.False(GpuRingBuffer12.TryPlanAllocation(
            currentOffset: 1024,
            totalBytes: 4096,
            reservedTailBytes: 3072,
            size: 1,
            alignment: GpuRingBuffer12.CbAlignment,
            out _,
            out var stoppedOffset));
        Assert.Equal(1024u, stoppedOffset);

        Assert.True(GpuRingBuffer12.TryPlanAllocation(
            currentOffset: 1024,
            totalBytes: 4096,
            reservedTailBytes: 0,
            size: 96,
            alignment: GpuRingBuffer12.CbAlignment,
            out var allocationOffset,
            out var nextOffset));
        Assert.Equal(1024u, allocationOffset);
        Assert.Equal(1120u, nextOffset);
    }

    [Fact]
    public void ShadowConstantSequence_FitsAfterEveryPossibleSceneEndOffset()
    {
        const uint totalBytes = 10_003; // deliberately not CB-aligned
        const uint reservedBytes =
            ((uint)ShadowCascadeCount * 3u + 1u) * GpuRingBuffer12.CbAlignment;
        uint[] allocationSizes = [96, 64, 16]; // reference b0, terrain b0, terrain b2
        var ordinaryLimit = totalBytes - reservedBytes;

        for (uint sceneEnd = 0; sceneEnd <= ordinaryLimit; sceneEnd++)
        {
            Assert.True(GpuRingBuffer12.TryPlanTailReservation(
                sceneEnd,
                totalBytes,
                reservedTailBytes: 0,
                additionalBytes: reservedBytes,
                out _));

            var shadowOffset = sceneEnd;
            for (var cascade = 0; cascade < ShadowCascadeCount; cascade++)
            {
                foreach (var size in allocationSizes)
                {
                    Assert.True(GpuRingBuffer12.TryPlanAllocation(
                            shadowOffset,
                            totalBytes,
                            reservedTailBytes: 0, // released immediately before shadow replay
                            size,
                            GpuRingBuffer12.CbAlignment,
                            out _,
                            out var nextOffset),
                        $"sceneEnd={sceneEnd}, cascade={cascade}, size={size}, offset={shadowOffset}");
                    shadowOffset = nextOffset;
                }
            }
        }
    }
}
