using System.Globalization;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRendererConstants12;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

public sealed class CaptureShadowPrimingPolicyTests
{
    [Theory]
    [InlineData(0x0)]
    [InlineData(0x1)]
    [InlineData(0x2)]
    [InlineData(0x3)]
    [InlineData(0x4)]
    [InlineData(0x5)]
    [InlineData(0x6)]
    [InlineData(0x7)]
    [InlineData(0x8)]
    [InlineData(0x9)]
    [InlineData(0xA)]
    [InlineData(0xB)]
    [InlineData(0xC)]
    [InlineData(0xD)]
    [InlineData(0xE)]
    [InlineData(0xF)]
    public void Decide_RequiresEveryCoherentCascadeButPreservesLegacyCapture(int completedCascadeMask)
    {
        Assert.Equal(4, CaptureShadowPrimingPolicy.MaxPrimeAttempts);
        Assert.Equal(0xF, CaptureShadowPrimingPolicy.CompleteCascadeMask);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var expected = CaptureShadowPrimingDecision.Ready;
            if (completedCascadeMask != 0xF)
            {
                expected = attempt < 4
                    ? CaptureShadowPrimingDecision.Retry
                    : CaptureShadowPrimingDecision.Fail;
            }

            Assert.Equal(expected,
                CaptureShadowPrimingPolicy.Decide(true, completedCascadeMask, attempt));
            Assert.Equal(CaptureShadowPrimingDecision.Ready,
                CaptureShadowPrimingPolicy.Decide(false, completedCascadeMask, attempt));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x10)]
    [InlineData(0x1F)]
    public void Decide_RequiresTheExactCompleteMask(int completedCascadeMask)
    {
        Assert.Equal(CaptureShadowPrimingDecision.Retry,
            CaptureShadowPrimingPolicy.Decide(true, completedCascadeMask, 1));
        Assert.Equal(CaptureShadowPrimingDecision.Fail,
            CaptureShadowPrimingPolicy.Decide(true, completedCascadeMask, 4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_BoundedRetriesAcceptCompletionOnTheFinalAttempt(bool completesOnLastAttempt)
    {
        int[] masks = [0x0, 0x3, 0x7, completesOnLastAttempt ? 0xF : 0x7];

        for (var index = 0; index < masks.Length - 1; index++)
        {
            Assert.Equal(CaptureShadowPrimingDecision.Retry,
                CaptureShadowPrimingPolicy.Decide(true, masks[index], index + 1));
        }

        Assert.Equal(completesOnLastAttempt
                ? CaptureShadowPrimingDecision.Ready
                : CaptureShadowPrimingDecision.Fail,
            CaptureShadowPrimingPolicy.Decide(true, masks[^1], masks.Length));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void Decide_ValidatesAttemptCountBeforeAnyReadyShortcut(int primeAttempts)
    {
        foreach (var coherent in new[] { false, true })
        {
            foreach (var mask in new[] { 0, 0xF })
            {
                Assert.Throws<ArgumentOutOfRangeException>(nameof(primeAttempts), () =>
                    CaptureShadowPrimingPolicy.Decide(coherent, mask, primeAttempts));
            }
        }
    }

    [Fact]
    public void AuthoritativeEmptyReplaysCompleteWithoutPublishedDrawAvailability()
    {
        var reference = ReferenceShadowReplaySource();
        var emptyReference = SourceContract.Extract(
            reference, "if (_shadowDraws.Count == 0)", "var hasCascadeInstances = false;");
        SourceContract.AssertOrder(emptyReference, "LastShadowReplayCompleted = true;", "return false;");
        var filteredReference = SourceContract.Extract(
            reference, "if (!hasCascadeInstances)", "var cmd = _recorder.CommandList;");
        SourceContract.AssertOrder(filteredReference, "LastShadowReplayCompleted = true;", "return false;");

        var terrain = TerrainShadowReplaySource();
        var emptyTerrain = SourceContract.Extract(
            terrain, "if ((_spatialIndex is null", "var cmd = _recorder.CommandList;");
        SourceContract.AssertOrder(emptyTerrain, "LastShadowReplayCompleted = true;", "return 0;");

        // Zero submitted draws is compatible with all four completion bits. Using the draw
        // availability mask (zero) instead would wrongly retry this authoritative empty result.
        Assert.Equal(CaptureShadowPrimingDecision.Ready,
            CaptureShadowPrimingPolicy.Decide(true, completedCascadeMask: 0xF, primeAttempts: 1));
        Assert.Equal(CaptureShadowPrimingDecision.Retry,
            CaptureShadowPrimingPolicy.Decide(true, completedCascadeMask: 0, primeAttempts: 1));
    }

    [Fact]
    public void RingReservationCoversProductionAllocationsAfterEveryInitialAlignment()
    {
        var sizes = ReadProductionShadowAllocationSizes();
        var alignedCascadeBytes = sizes.Sum(size =>
            (long)((size + GpuRingBuffer12.CbAlignment - 1) / GpuRingBuffer12.CbAlignment)
            * GpuRingBuffer12.CbAlignment);
        var requiredBytes = 4 * alignedCascadeBytes + GpuRingBuffer12.CbAlignment;

        Assert.Equal(4352u, CaptureShadowPrimingPolicy.RingReservationBytes);
        Assert.True(CaptureShadowPrimingPolicy.RingReservationBytes >= requiredBytes,
            $"The four shadow cascades and initial alignment need {requiredBytes} bytes.");

        for (uint sceneEnd = 0; sceneEnd < GpuRingBuffer12.CbAlignment; sceneEnd++)
        {
            var total = sceneEnd + CaptureShadowPrimingPolicy.RingReservationBytes;
            Assert.True(GpuRingBuffer12.TryPlanTailReservation(
                sceneEnd, total, 0, CaptureShadowPrimingPolicy.RingReservationBytes, out _));

            var offset = sceneEnd;
            for (var cascade = 0; cascade < 4; cascade++)
            {
                foreach (var size in sizes)
                {
                    Assert.True(GpuRingBuffer12.TryPlanAllocation(
                            offset, total, 0, size, GpuRingBuffer12.CbAlignment, out _, out var nextOffset),
                        $"sceneEnd={sceneEnd}, cascade={cascade}, size={size}, offset={offset}");
                    offset = nextOffset;
                }
            }
        }
    }

    private static uint[] ReadProductionShadowAllocationSizes()
    {
        // Read the compiled constant and exact blittable layout, not a copy of its arithmetic
        // expression. The old 96-byte fixture missed the actual 352-byte shadow b0 allocation.
        Assert.Equal(InstancedShadowPerFrameConstants.ByteSize,
            (uint)Marshal.SizeOf<InstancedShadowPerFrameConstants>());

        var reference = ReferenceShadowReplaySource();
        Assert.Equal(1, SourceContract.CountOccurrences(reference, "_ringBuffer.TryAllocate("));
        Assert.Contains("_recorder.FrameIndex, InstancedShadowPerFrameConstants.ByteSize, out var perFrameAlloc,",
            reference, StringComparison.Ordinal);
        SourceContract.AssertOrder(reference,
            "_ringBuffer.TryAllocate(",
            "GpuRingBuffer12.CbAlignment",
            "*(InstancedShadowPerFrameConstants*)perFrameAlloc.CpuPtr = constants;");

        var terrainSource = RendererSource("TerrainRenderer12.cs");
        var terrain = TerrainShadowReplaySource();
        Assert.Equal(2, SourceContract.CountOccurrences(terrain, "_ringBuffer.TryAllocate("));
        Assert.Contains("PerFrameByteSize, out var perFrameAlloc, GpuRingBuffer12.CbAlignment)",
            terrain, StringComparison.Ordinal);
        Assert.Contains("PerModeByteSize, out var perModeAlloc, GpuRingBuffer12.CbAlignment)",
            terrain, StringComparison.Ordinal);
        SourceContract.AssertOrder(terrain,
            "PerFrameByteSize, out var perFrameAlloc",
            "PerModeByteSize, out var perModeAlloc",
            "foreach (var key in EnumerateCellKeysInCylinder(cylinder))");
        Assert.DoesNotContain("_ringBuffer.TryAllocate(",
            terrain[terrain.IndexOf("foreach (var key", StringComparison.Ordinal)..], StringComparison.Ordinal);

        return
        [
            InstancedShadowPerFrameConstants.ByteSize,
            ReadTerrainConstant(terrainSource, "PerFrameByteSize"),
            ReadTerrainConstant(terrainSource, "PerModeByteSize")
        ];
    }

    private static uint ReadTerrainConstant(string source, string name)
    {
        var declaration = SourceContract.Extract(source, $"private const uint {name} =", ";");
        return uint.Parse(declaration.AsSpan(declaration.IndexOf('=') + 1), CultureInfo.InvariantCulture);
    }

    private static string ReferenceShadowReplaySource() => SourceContract.Extract(
        RendererSource("ReferenceRenderer12.cs"),
        "public bool RenderShadowDepth(in SunShadowMath.LightFrustum frustum", "public bool RenderMirrorColor(");

    private static string TerrainShadowReplaySource() => SourceContract.Extract(
        RendererSource("TerrainRenderer12.cs"),
        "public int RenderShadowDepth(Matrix4x4 lightViewProj", "public int RenderMirror(");

    private static string RendererSource(string name) => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", name);
}
