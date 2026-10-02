using Slfx77.Multitool.Core.Lifetime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks exact geometry admission charges independently of device allocation and across native upload paths.</summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class GpuGeometryAllocationBytesTests
{
    /// <summary>Vertex padding and final range padding produce the allocator's exact charge, including beyond 32-bit sums.</summary>
    /// <param name="vertexBytes">Raw vertex stream size.</param>
    /// <param name="indexBytes">Raw index stream size.</param>
    /// <param name="expected">Independently specified final aligned byte charge.</param>
    [Theory]
    [InlineData(1L, 0L, 16L)]
    [InlineData(16L, 0L, 16L)]
    [InlineData(17L, 0L, 32L)]
    [InlineData(1L, 1L, 32L)]
    [InlineData(16L, 1L, 32L)]
    [InlineData(17L, 3L, 48L)]
    [InlineData(32L, 16L, 48L)]
    [InlineData(64L, 6L, 80L)]
    [InlineData(1L, int.MaxValue, 2_147_483_664L)]
    [InlineData(int.MaxValue, 0L, 2_147_483_648L)]
    [InlineData(int.MaxValue, int.MaxValue, 4_294_967_296L)]
    [InlineData(long.MaxValue - 15, 0L, long.MaxValue - 15)]
    [InlineData(16L, long.MaxValue - 31, long.MaxValue - 15)]
    public void ChargeMatchesTwoStreamLayoutAndActualAllocator(long vertexBytes, long indexBytes, long expected)
    {
        var charge = GpuGeometryArena12.CalculateAllocationBytes(vertexBytes, indexBytes);
        Assert.Equal(expected, charge);
        Assert.Equal(0L, charge % 16);
        var allocator = new ByteArenaAllocator(4096) { StrictValidation = true };
        var paddedVertexBytes = checked(vertexBytes + 15) & ~15L;
        var allocation = allocator.Allocate(checked(paddedVertexBytes + indexBytes));
        Assert.Equal(expected, allocation.AlignedSize);
        Assert.Equal(expected, allocator.AllocatedBytes);
        allocator.Free(allocation);
        Assert.Equal(0L, allocator.AllocatedBytes);
    }

    /// <summary>Invalid lengths cannot enter the allocator or be misreported as a small positive charge.</summary>
    /// <param name="vertexBytes">Vertex length, including absent or invalid input.</param>
    /// <param name="indexBytes">Index length, including invalid input.</param>
    [Theory]
    [InlineData(-1L, 0L)]
    [InlineData(0L, 0L)]
    [InlineData(0L, 16L)]
    [InlineData(1L, -1L)]
    [InlineData(long.MinValue, 0L)]
    [InlineData(16L, long.MinValue)]
    public void InvalidStreamLengthsAreRejected(long vertexBytes, long indexBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GpuGeometryArena12.CalculateAllocationBytes(vertexBytes, indexBytes));
    }

    /// <summary>Overflow in either alignment or stream addition is rejected instead of wrapping into an admitted charge.</summary>
    /// <param name="vertexBytes">Vertex length at the selected arithmetic boundary.</param>
    /// <param name="indexBytes">Index length at the selected arithmetic boundary.</param>
    [Theory]
    [InlineData(long.MaxValue, 0L)]
    [InlineData(long.MaxValue - 14, 0L)]
    [InlineData(16L, long.MaxValue)]
    [InlineData(16L, long.MaxValue - 30)]
    [InlineData(long.MaxValue - 15, 16L)]
    public void OverflowIsRejectedBeforeAllocation(long vertexBytes, long indexBytes)
    {
        Assert.Throws<OverflowException>(() =>
            GpuGeometryArena12.CalculateAllocationBytes(vertexBytes, indexBytes));
    }

    /// <summary>Mapped and recorded-copy uploads preserve raw view sizes, padded index offsets and exact admission charges.</summary>
    /// <param name="path">Zero selects direct UPLOAD, one the unified UPLOAD path, and two the DEFAULT copy path.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", GpuTestGuard.Category)]
    public void NativeUploadPathsReturnThePredictedRangeWithoutChangingViews(int path)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        var backing = path == 2 ? GpuGeometryArenaBackingMode.DefaultHeap : GpuGeometryArenaBackingMode.UploadHeap;
        using var arena = new GpuGeometryArena12(gpu, blockSize: 65_536, backingMode: backing);
        using var queue = new GpuDeletionQueue12(2);
        try
        {
            recorder.BeginFrame();
            var vertices = new byte[17];
            var indices = new byte[3];
            var allocation = path == 0
                ? arena.Upload(vertices, indices)
                : arena.Upload(recorder.CommandList, queue, vertices, indices);
            Assert.Equal(35L, allocation.Allocation.Size);
            Assert.Equal(48L, allocation.Allocation.AlignedSize);
            Assert.Equal(GpuGeometryArena12.CalculateAllocationBytes(vertices.Length, indices.Length),
                allocation.Allocation.AlignedSize);
            Assert.Equal(32UL, allocation.IndexBufferLocation - allocation.VertexBufferLocation);
            Assert.Equal(17U, allocation.VertexBytes);
            Assert.Equal(3U, allocation.IndexBytes);
            recorder.EndFrame();
            recorder.WaitForGpuIdle();
            queue.Dispose();
            arena.Free(allocation);
        }
        finally
        {
            // No resource teardown may race a submitted DEFAULT copy, including on assertion failure.
            recorder.Dispose();
            queue.Dispose();
        }
    }
}
