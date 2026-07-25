using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class GpuRingTailReservationTests
{
    // ShadowMapRenderer12 is WINDOWS_GUI-only while this test assembly targets portable net10.0.
    // SunShadowMathTests separately pins the host reservation formula to that renderer's count.
    private const int ShadowCascadeCount = 4;

    // Mirrors WorldView3DControl's WaterPassRingReservationBytes (WINDOWS_GUI-only). Kept in sync here
    // so the water reservation's sizing contract is pinned the same way the shadow one is above.
    private const uint WaterPassMaxReservedBatches = 64;

    private const uint WaterPassRingReservationBytes =
        WaterPassMaxReservedBatches * 4u * GpuRingBuffer12.CbAlignment;

    [Fact]
    public void ShadowSizedTail_FitsBeforeSceneConsumesItsAllocationWindow()
    {
        const uint total = 4096;
        const uint shadowTail =
            (ShadowCascadeCount * 3u + 1u) * GpuRingBuffer12.CbAlignment;

        Assert.True(GpuRingBuffer12.TryPlanTailReservation(
            768,
            total,
            0,
            shadowTail,
            out var reserved));
        Assert.Equal(shadowTail, reserved);
    }

    [Fact]
    public void TailReservation_RejectsAWindowAlreadyConsumedBySceneDraws()
    {
        Assert.False(GpuRingBuffer12.TryPlanTailReservation(
            769,
            4096,
            0,
            3328,
            out var reserved));
        Assert.Equal(0u, reserved);
    }

    [Fact]
    public void TailReservations_AreAdditiveAndFailurePreservesThePriorTail()
    {
        Assert.True(GpuRingBuffer12.TryPlanTailReservation(
            512,
            4096,
            1024,
            2048,
            out var reserved));
        Assert.Equal(3072u, reserved);

        Assert.False(GpuRingBuffer12.TryPlanTailReservation(
            1025,
            4096,
            reserved,
            1,
            out var preserved));
        Assert.Equal(reserved, preserved);
    }

    [Fact]
    public void OrdinaryAllocations_StopAtReservedTail_ThenResumeAfterRelease()
    {
        Assert.False(GpuRingBuffer12.TryPlanAllocation(
            1024,
            4096,
            3072,
            1,
            GpuRingBuffer12.CbAlignment,
            out _,
            out var stoppedOffset));
        Assert.Equal(1024u, stoppedOffset);

        Assert.True(GpuRingBuffer12.TryPlanAllocation(
            1024,
            4096,
            0,
            96,
            GpuRingBuffer12.CbAlignment,
            out var allocationOffset,
            out var nextOffset));
        Assert.Equal(1024u, allocationOffset);
        Assert.Equal(1120u, nextOffset);
    }

    [Fact]
    public void PartialRelease_UnwindsAStackedReservationOnePassAtATime()
    {
        const uint shadowTail =
            (ShadowCascadeCount * 3u + 1u) * GpuRingBuffer12.CbAlignment;

        // Water is reserved on top of the shadow tail (additive), then released back to the pass while
        // the shadow reservation must survive for the frame-end replay.
        var stacked = shadowTail + WaterPassRingReservationBytes;
        Assert.Equal(shadowTail, GpuRingBuffer12.PlanTailRelease(stacked, WaterPassRingReservationBytes));

        // Over-releasing (or releasing the whole stack) clamps at zero rather than underflowing.
        Assert.Equal(0u, GpuRingBuffer12.PlanTailRelease(shadowTail, WaterPassRingReservationBytes));
        Assert.Equal(0u, GpuRingBuffer12.PlanTailRelease(stacked, stacked + 1));
    }

    [Fact]
    public void WaterConstantSequence_FitsAfterAnySceneEnd_WithShadowStillReserved()
    {
        const uint shadowTail =
            (ShadowCascadeCount * 3u + 1u) * GpuRingBuffer12.CbAlignment;
        // A one-per-frame uniforms CB, then a noise CB + uniforms CB for every visible WATR batch —
        // the water pass's worst case at the reserved batch budget. (Sizes: WaterFrameUniforms = 448,
        // FnvNoiseUniforms rounds within one 256-byte block.)
        var waterSequence = new List<uint> { 448 };
        for (var batch = 0; batch < WaterPassMaxReservedBatches; batch++)
        {
            waterSequence.Add(96); // noise-prepass CB
            waterSequence.Add(448); // per-batch uniforms CB
        }

        // Shadow + water reserved as one stacked tail at frame start (bump = 0).
        uint reserved = 0;
        Assert.True(GpuRingBuffer12.TryPlanTailReservation(0, uint.MaxValue, reserved, shadowTail, out reserved));
        Assert.True(GpuRingBuffer12.TryPlanTailReservation(
            0, uint.MaxValue, reserved, WaterPassRingReservationBytes, out reserved));
        Assert.Equal(shadowTail + WaterPassRingReservationBytes, reserved);

        // Size the slot so the scene draws can consume every byte the ordinary limit allows, leaving
        // the tightest possible headroom for water. Scene ends anywhere in [0, ordinaryLimit].
        const uint sceneWindow = 4096;
        var total = reserved + sceneWindow;
        var ordinaryLimit = total - reserved;

        // Water releases only its own budget; the shadow tail stays protected for the replay.
        var duringWater = GpuRingBuffer12.PlanTailRelease(reserved, WaterPassRingReservationBytes);
        Assert.Equal(shadowTail, duringWater);

        for (uint sceneEnd = 0; sceneEnd <= ordinaryLimit; sceneEnd++)
        {
            var offset = sceneEnd;
            foreach (var size in waterSequence)
            {
                Assert.True(
                    GpuRingBuffer12.TryPlanAllocation(
                        offset, total, duringWater, size, GpuRingBuffer12.CbAlignment,
                        out _, out var nextOffset),
                    $"sceneEnd={sceneEnd}, size={size}, offset={offset}");
                offset = nextOffset;
            }
        }
    }

    [Fact]
    public void ShadowConstantSequence_FitsAfterEveryPossibleSceneEndOffset()
    {
        const uint totalBytes = 10_003; // deliberately not CB-aligned
        const uint reservedBytes =
            (ShadowCascadeCount * 3u + 1u) * GpuRingBuffer12.CbAlignment;
        uint[] allocationSizes = [96, 64, 16]; // reference b0, terrain b0, terrain b2
        var ordinaryLimit = totalBytes - reservedBytes;

        for (uint sceneEnd = 0; sceneEnd <= ordinaryLimit; sceneEnd++)
        {
            Assert.True(GpuRingBuffer12.TryPlanTailReservation(
                sceneEnd,
                totalBytes,
                0,
                reservedBytes,
                out _));

            var shadowOffset = sceneEnd;
            for (var cascade = 0; cascade < ShadowCascadeCount; cascade++)
            {
                foreach (var size in allocationSizes)
                {
                    Assert.True(GpuRingBuffer12.TryPlanAllocation(
                            shadowOffset,
                            totalBytes,
                            0, // released immediately before shadow replay
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